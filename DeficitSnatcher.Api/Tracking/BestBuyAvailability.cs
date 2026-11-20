using System.Text.Json;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Best Buy Canada's own stock lookup, the one its product page uses: by SKU and postal code, it says separately whether the
/// product can be bought online (shipping) and picked up at stores near that postal code. Asked from inside the product page's
/// tab (same cookies as the page), so it goes through the same bot check the page already passed. A page counts as in stock
/// when either one is buyable.
/// </summary>
public static class BestBuyAvailability
{
    public static bool Applies(Uri url) => url.Host.EndsWith("bestbuy.ca", StringComparison.OrdinalIgnoreCase);

    /// <summary>The SKU: the product URL's last number (…/playstation-5-pro-console/19492009).</summary>
    public static string? Sku(Uri url) =>
        url.Segments.Select(s => s.Trim('/')).LastOrDefault(s => s.Length >= 6 && s.All(char.IsDigit));

    /// <summary>The lookup's JSON, asked from the page; null when it couldn't be asked.</summary>
    public static async Task<string?> FetchAsync(IPage page, string sku, string postalCode, ILogger log)
    {
        var postal = new string(postalCode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var query = "accept=application%2Fvnd.bestbuy.standardproduct.v1%2Bjson&accept-language=en-CA&skus=" + Uri.EscapeDataString(sku)
            + (postal.Length >= 3 ? "&postalCode=" + Uri.EscapeDataString(postal) : "");
        const string script = """
            async (q) => {
              const r = await fetch('/ecomm-api/availability/products?' + q, { credentials: 'include', headers: { accept: 'application/json' } });
              return r.status + '\n' + await r.text();
            }
            """;
        try
        {
            var answer = await page.EvaluateAsync<string>(script, query).WaitAsync(TimeSpan.FromSeconds(15));
            var split = answer.IndexOf('\n');
            var status = split > 0 ? answer[..split] : "?";
            var body = split > 0 ? answer[(split + 1)..] : answer;
            if (status != "200")
            {
                log.LogInformation("Best Buy availability lookup answered {Status}", status);
                return null;
            }
            return body.TrimStart('﻿');
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException)
        {
            log.LogInformation("Best Buy availability lookup failed: {Error}", e.Message.Split('\n')[0]);
            return null;
        }
    }

    /// <summary>The page's result with stock from the lookup (online or pickup), and what each said in its notes.</summary>
    public static PageResult Apply(PageResult result, string json)
    {
        JsonElement item;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("availabilities", out var a) ? a : root;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                return result with { Notes = [.. result.Notes, "best buy lookup: no availability in its answer"] };
            item = list[0].Clone();
        }
        catch (JsonException)
        {
            return result with { Notes = [.. result.Notes, "best buy lookup: unreadable answer"] };
        }

        var (online, onlineStatus) = Channel(item, "shipping");
        var (pickup, pickupStatus) = Channel(item, "pickup");
        if (online == null && pickup == null)
            return result with { Notes = [.. result.Notes, "best buy lookup: no shipping or pickup in its answer"] };

        var stores = new List<string>();
        if (item.TryGetProperty("pickup", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array)
            foreach (var l in locs.EnumerateArray())
            {
                var qty = l.TryGetProperty("quantityOnHand", out var q) && q.TryGetInt32(out var n) ? n : 0;
                var has = qty > 0 || l.TryGetProperty("hasInventory", out var h) && h.ValueKind == JsonValueKind.True;
                if (has && l.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } s)
                    stores.Add(qty > 0 ? $"{s} ({qty})" : s);
            }

        var inStock = online == true || pickup == true;
        var note = $"stock: best buy lookup: online {Word(online, onlineStatus)}, pickup {Word(pickup, pickupStatus)}"
            + (stores.Count > 0 ? $" at {string.Join(", ", stores.Take(5))}" : "");
        var detail = $"online: {Short(online)} · pickup: {Short(pickup)}" + (stores.Count > 0 ? $" at {string.Join(", ", stores.Take(3))}" : "");
        return result with { InStock = inStock, StockDetail = detail, Notes = [.. result.Notes.Where(n => !n.StartsWith("stock:")), note] };
    }

    private static string Short(bool? buyable) => buyable switch { true => "yes", false => "no", null => "?" };


    /// <summary>Whether a channel (shipping or pickup) can be bought, and its status word.</summary>
    private static (bool? Buyable, string? Status) Channel(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var c) || c.ValueKind != JsonValueKind.Object)
            return (null, null);
        var status = c.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (c.TryGetProperty("purchasable", out var pu) && pu.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return (pu.ValueKind == JsonValueKind.True, status);
        if (status == null)
            return (null, null);
        var v = status.ToLowerInvariant();
        return (v.Contains("instock") || v == "available" || v.Contains("limited"), status);
    }

    private static string Word(bool? buyable, string? status) =>
        (buyable switch { true => "yes", false => "no", null => "unknown" }) + (status is { Length: > 0 } ? $" ({status})" : "");
}
