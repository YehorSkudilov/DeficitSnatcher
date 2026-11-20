using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeficitSnatcher.Api.Buying;

/// <summary>
/// One step of a site's checkout script, run in Chromium. Values may use placeholders: {{url}}, {{origin}},
/// {{variantId}}, {{quantity}}, {{price}} and the checkout profile's ({{email}}, {{firstName}}, {{card.number}}, …; see
/// CheckoutProfile.Placeholders).
/// </summary>
public sealed record CheckoutStep
{
    /// <summary>goto, click, fill, type, select, check, press, wait, waitFor, waitForUrl, expectText.</summary>
    public string Action { get; init; } = "";
    /// <summary>A Playwright selector (CSS, text=…, role=…).</summary>
    public string? Selector { get; init; }
    /// <summary>The URL, text, key, option, milliseconds or URL part, depending on the action.</summary>
    public string? Value { get; init; }
    /// <summary>The iframe the selector is inside (card fields usually are), as a selector for the iframe.</summary>
    public string? Frame { get; init; }
    /// <summary>A step that places the order: dry runs stop before the first one.</summary>
    public bool Final { get; init; }
    /// <summary>Carry on when it fails (a cookie banner that's only sometimes there).</summary>
    public bool Optional { get; init; }
    public int? TimeoutMs { get; init; }
    public string? Note { get; init; }

    public static readonly string[] Actions =
        ["goto", "click", "fill", "type", "select", "check", "press", "wait", "waitFor", "waitForUrl", "expectText"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static List<CheckoutStep> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<CheckoutStep>>(json, Json) ?? [];

    public static string Serialize(IEnumerable<CheckoutStep> steps) => JsonSerializer.Serialize(steps, Json);

    /// <summary>What's wrong with a script, or null.</summary>
    public static string? Validate(IReadOnlyList<CheckoutStep> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var n = $"Step {i + 1}";
            if (!Actions.Contains(s.Action))
                return $"{n}: unknown action \"{s.Action}\".";
            var needsSelector = s.Action is "click" or "fill" or "type" or "select" or "check" or "press" or "waitFor";
            if (needsSelector && string.IsNullOrWhiteSpace(s.Selector))
                return $"{n} ({s.Action}) needs a selector.";
            var needsValue = s.Action is "goto" or "fill" or "type" or "select" or "press" or "wait" or "waitForUrl" or "expectText";
            if (needsValue && string.IsNullOrEmpty(s.Value))
                return $"{n} ({s.Action}) needs a value.";
            if (s.Action == "wait" && !int.TryParse(s.Value, out _))
                return $"{n}: wait takes milliseconds.";
        }
        return null;
    }
}
