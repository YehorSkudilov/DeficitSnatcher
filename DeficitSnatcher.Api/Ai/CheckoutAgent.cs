using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeficitSnatcher.Api.Buying;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Ai;

/// <summary>
/// ChatGPT going through a store's checkout in the browser: each turn it sees the page (text, the elements it can use,
/// and a screenshot until card details are typed) and answers with one action, which is run as a checkout step. What
/// it did is a list of ordinary checkout steps, so it can be saved and replayed without the AI.
///
/// It never sees the checkout profile's values: it writes {{email}}, {{card.number}} and the like, and they're filled
/// in here. Before a real order it must read the order total off the page, and that must be within the limit.
/// </summary>
public sealed partial class CheckoutAgent(OpenAiClient ai)
{
    public sealed record Goal(
        string ProductName,
        string ProductUrl,
        int Quantity,
        decimal MaxTotal,
        string Currency,
        bool DryRun,
        IReadOnlyCollection<string> Placeholders,
        /// <summary>Why the AI is here: no steps yet, or a step broke (with what was planned after it).</summary>
        string Situation);

    public enum End
    {
        /// <summary>The order went through (the AI saw the confirmation).</summary>
        Done,
        /// <summary>Dry run: everything up to placing the order worked.</summary>
        DryRunStop,
        /// <summary>The AI couldn't go on.</summary>
        Stuck,
        /// <summary>The order total couldn't be read or was over the limit: the order wasn't placed.</summary>
        PriceCheckFailed,
        /// <summary>The order was submitted but the AI couldn't see a confirmation.</summary>
        Uncertain,
    }

    public sealed record Result(End End, string Message, List<CheckoutStep> Steps, bool FinalExecuted);

    /// <summary>Runs one step (placeholders filled in, recorded in the checkout's log with a screenshot).</summary>
    public delegate Task<(bool Ok, string? Error)> Execute(CheckoutStep step, string note);

    /// <summary>Records the final step that a dry run doesn't run.</summary>
    public delegate Task RecordNotRun(CheckoutStep step, string note);

    private const string System = """
        You operate a web browser to buy a product in an online store, one action at a time. Each turn you get the page:
        its URL, visible text, the elements you can act on (each with an id), and sometimes a screenshot.

        Answer with JSON only:
        {
          "status": "act" | "done" | "stuck",
          "note": "one short sentence: what you see and why this action",
          "action": { "type": "click|fill|type|select|check|press|goto|wait", "element": "e12", "value": "...", "final": false },
          "orderTotal": null,
          "orderQuantity": null,
          "otherItems": null
        }

        Rules:
        - "element" must be an id from the list. For goto, put the URL in value and no element. For wait, value is milliseconds.
        - fill types text into an input; type presses keys one by one (use it for card fields that ignore fill); select picks
          an option by its visible text; check ticks a checkbox; press sends a key (value like "Enter") to the element.
        - Never type personal or payment details yourself. Use these placeholders as the value, exactly: {placeholders}.
          Combine them when a field needs it (e.g. "{{card.expiryMonth}}/{{card.expiryYear2}}").
        - Put exactly {quantity} of the item in the cart, never more. If the cart already holds more of it (from an earlier
          attempt) or other items, remove them or lower the quantity first. Then go to checkout, check out as a guest unless already signed in, fill the
          contact, shipping and payment details, choose the cheapest standard shipping, accept required terms, and close
          cookie banners or pop-ups that block the page.
        - Make sure the item is the right product: {product}. If the page is for something else, or it's sold out, answer
          "stuck" and say so.
        - "final": true only on the one action that places or pays for the order (Place order, Pay now, Complete purchase,
          Submit order). On that action, also give "orderTotal": the order's grand total as shown on the page (a number),
          "orderQuantity": how many of the product the order contains, and "otherItems": true if anything else is in it.
        - After the order is placed, answer "done" once you see the confirmation (order number, thank you).
        - If something fails, look at the error and try another way (another element, close a pop-up, scroll by waiting).
          Answer "stuck" with the reason if you can't go on (captcha, sign-in required, the item can't be added).
        """;

    public async Task<Result> RunAsync(IPage page, Goal goal, Execute execute, RecordNotRun recordNotRun, Func<bool> cardTyped,
        bool sendScreenshots, int maxActions, int siteId, int purchaseId, string purpose, CancellationToken ct)
    {
        var steps = new List<CheckoutStep>();
        var history = new List<string>();
        var finalExecuted = false;
        var errorsInARow = 0;
        var system = System
            .Replace("{placeholders}", string.Join(", ", goal.Placeholders.Select(p => "{{" + p + "}}")))
            .Replace("{quantity}", goal.Quantity.ToString())
            .Replace("{product}", goal.ProductName);

        for (var turn = 1; turn <= maxActions; turn++)
        {
            var state = await PageReader.ReadAsync(page, sendScreenshots && !cardTyped(), ct);
            var question = new StringBuilder()
                .AppendLine($"Goal: buy {goal.Quantity} × {goal.ProductName} ({goal.ProductUrl}). "
                    + (goal.DryRun
                        ? "This is a DRY RUN: go all the way, and when the next action would place the order, answer it with final=true (it won't be run)."
                        : $"This is a REAL purchase. The order total must be at most {goal.MaxTotal:0.00} {goal.Currency}."))
                .AppendLine(goal.Situation)
                .AppendLine()
                .AppendLine(history.Count == 0 ? "No actions yet." : "Your actions so far:\n" + string.Join("\n", history.TakeLast(15)))
                .AppendLine()
                .AppendLine($"Action {turn} of at most {maxActions}. The page now:")
                .AppendLine(state.Describe());
            var parts = new List<object> { OpenAiClient.Text(question.ToString()) };
            if (state.Screenshot != null)
                parts.Add(OpenAiClient.Image(state.Screenshot, detail: "low"));

            JsonElement answer;
            try
            {
                answer = await ai.AskJsonAsync(purpose, siteId, purchaseId, system, parts, ct);
            }
            catch (OpenAiClient.AiException e)
            {
                return new(finalExecuted ? End.Uncertain : End.Stuck, "AI: " + e.Message, steps, finalExecuted);
            }

            var status = Str(answer, "status") ?? "act";
            var note = Str(answer, "note") ?? "";
            if (status == "done")
                return finalExecuted
                    ? new(End.Done, "The AI saw the order confirmation: " + note, steps, true)
                    : new(End.Stuck, "The AI said it was done before placing the order: " + note, steps, false);
            if (status == "stuck")
                return new(finalExecuted ? End.Uncertain : End.Stuck, "The AI got stuck: " + note, steps, finalExecuted);
            if (!answer.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.Object)
            {
                history.Add($"{turn}. (no action given)");
                continue;
            }

            var type = Str(a, "type") ?? "";
            var value = Str(a, "value");
            var final = a.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.True;
            var elementId = Str(a, "element");
            var element = elementId == null ? null : state.Elements.FirstOrDefault(e => e.Id == elementId);

            string? problem = null;
            if (!CheckoutStep.Actions.Contains(type) || type is "waitFor" or "waitForUrl" or "expectText")
                problem = $"unknown action type \"{type}\"";
            else if (type is not ("goto" or "wait") && element == null)
                problem = $"no element {elementId} on the page";
            else if (value != null && LongNumber().IsMatch(value))
                problem = "don't type numbers like card numbers: use the {{card.…}} placeholders";
            if (problem != null)
            {
                history.Add($"{turn}. REFUSED: {problem}");
                if (++errorsInARow >= 4)
                    return new(finalExecuted ? End.Uncertain : End.Stuck, "The AI kept answering with actions that can't be run: " + problem, steps, finalExecuted);
                continue;
            }

            var step = new CheckoutStep
            {
                Action = type,
                Selector = element?.Selector,
                Frame = element?.Frame,
                Value = type is "click" or "check" ? null : value,
                Final = final,
                TimeoutMs = 10000,
                Note = $"AI: {note}",
            };

            if (final)
            {
                if (goal.DryRun)
                {
                    steps.Add(step);
                    await recordNotRun(step, note);
                    return new(End.DryRunStop, "The AI went through the checkout up to placing the order (dry run): " + note, steps, false);
                }
                var total = answer.TryGetProperty("orderTotal", out var t) ? Number(t) : null;
                var quantity = answer.TryGetProperty("orderQuantity", out var oq) ? Number(oq) : null;
                var others = answer.TryGetProperty("otherItems", out var oi) && oi.ValueKind == JsonValueKind.True;
                if (total == null)
                    return new(End.PriceCheckFailed, "Not placing the order: the AI couldn't read the order total on the page.", steps, false);
                if (quantity == null || quantity > goal.Quantity || quantity < 1)
                    return new(End.PriceCheckFailed,
                        quantity == null
                            ? "Not placing the order: the AI couldn't tell how many are in it."
                            : $"Not placing the order: it has {quantity} of the product, but only {goal.Quantity} may be bought.",
                        steps, false);
                if (others)
                    return new(End.PriceCheckFailed, "Not placing the order: it has other items in it too.", steps, false);
                if (total > goal.MaxTotal)
                    return new(End.PriceCheckFailed,
                        $"Not placing the order: the total is {total:0.00} {goal.Currency}, over the limit of {goal.MaxTotal:0.00} (maximum price plus tax).",
                        steps, false);
                history.Add($"{turn}. order total read as {total:0.00}, within {goal.MaxTotal:0.00}");
            }

            var (ok, error) = await execute(step, note);
            history.Add($"{turn}. {type} {element?.Label ?? value} ({step.Selector ?? value}){(final ? " [FINAL]" : "")}: {(ok ? "ok" : "FAILED: " + error?.Split('\n')[0])}");
            if (ok)
            {
                errorsInARow = 0;
                steps.Add(step);
                if (final)
                    finalExecuted = true;
                if (type is "click" or "press" or "goto" || final)
                {
                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = final ? 15000 : 4000 });
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
            else if (++errorsInARow >= 4)
                return new(finalExecuted ? End.Uncertain : End.Stuck, $"Four actions in a row failed; last: {error?.Split('\n')[0]}", steps, finalExecuted);
        }
        return new(finalExecuted ? End.Uncertain : End.Stuck, $"The AI used all {maxActions} actions without finishing.", steps, finalExecuted);
    }

    public sealed record OrderCheck(bool Ok, string Message);

    private const string CheckSystem = """
        You check an online store's order review page just before the order is placed. Read the order as shown and answer
        with JSON only:
        { "orderTotal": 0.0, "orderQuantity": 0, "otherItems": false, "note": "what the order contains, briefly" }
        orderTotal: the grand total (with tax and shipping) as a number, or null if the page doesn't show one. orderQuantity: how
        many of the wanted product the order contains, or null if you can't tell. otherItems: true if anything else is in it.
        """;

    /// <summary>
    /// Before a saved checkout's final step places a real order: the AI reads the order off the page, and it must hold only
    /// the wanted product, at most the allowed number of it, for at most the allowed total.
    /// </summary>
    public async Task<OrderCheck> CheckOrderAsync(IPage page, Goal goal, bool sendScreenshot, int siteId, int purchaseId, CancellationToken ct)
    {
        var state = await PageReader.ReadAsync(page, sendScreenshot, ct);
        var parts = new List<object>
        {
            OpenAiClient.Text($"Wanted: {goal.Quantity} × {goal.ProductName}. Allowed total: at most {goal.MaxTotal:0.00} {goal.Currency}.\n\n{state.Describe(6000)}"),
        };
        if (state.Screenshot != null)
            parts.Add(OpenAiClient.Image(state.Screenshot, detail: "low"));
        JsonElement a;
        try
        {
            a = await ai.AskJsonAsync("check-order", siteId, purchaseId, CheckSystem, parts, ct);
        }
        catch (OpenAiClient.AiException e)
        {
            return new(false, "The AI couldn't check the order: " + e.Message);
        }
        var total = a.TryGetProperty("orderTotal", out var t) ? Number(t) : null;
        var quantity = a.TryGetProperty("orderQuantity", out var q) ? Number(q) : null;
        var others = a.TryGetProperty("otherItems", out var o) && o.ValueKind == JsonValueKind.True;
        var note = Str(a, "note") ?? "";
        if (quantity == null)
            return new(false, $"Couldn't tell how many are in the order. {note}");
        if (quantity > goal.Quantity || quantity < 1)
            return new(false, $"The order has {quantity} of the product; only {goal.Quantity} may be bought. {note}");
        if (others)
            return new(false, $"The order has other items in it too. {note}");
        if (total == null)
            return new(false, $"Couldn't read the order total. {note}");
        if (total > goal.MaxTotal)
            return new(false, $"The total is {total:0.00} {goal.Currency}, over the limit of {goal.MaxTotal:0.00}. {note}");
        return new(true, $"{quantity} × {goal.ProductName}, total {total:0.00} {goal.Currency}: within limits. {note}");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Number(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.String => Tracking.PageParser.ParsePrice(e.GetString()),
        _ => null,
    };

    [GeneratedRegex(@"\d[\d \-]{11,}\d")]
    private static partial Regex LongNumber();
}
