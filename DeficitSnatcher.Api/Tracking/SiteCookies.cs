using System.Text.Json;

namespace DeficitSnatcher.Api.Tracking;

public sealed record SiteCookie(string Name, string Value, string Domain, string Path);

/// <summary>
/// A site's cookies, as pasted on its page: the JSON a browser cookie extension exports (Cookie-Editor, EditThisCookie:
/// [{"name","value","domain","path"}, …]), or a plain "name=value; name2=value2" header copied from the browser's
/// network tab. A signed-in session lets checks see member prices and the checkout skip signing in.
/// </summary>
public static class SiteCookies
{
    public static List<SiteCookie> Parse(string? text, string siteUrl)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var host = new Uri(siteUrl).Host;
        text = text.Trim();
        if (text.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(text);
            var list = new List<SiteCookie>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = c.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.IsNullOrEmpty(name) || value == null)
                    continue;
                var domain = c.TryGetProperty("domain", out var d) && !string.IsNullOrEmpty(d.GetString()) ? d.GetString()! : host;
                var path = c.TryGetProperty("path", out var p) && !string.IsNullOrEmpty(p.GetString()) ? p.GetString()! : "/";
                list.Add(new SiteCookie(name, value, domain, path));
            }
            return list;
        }
        return text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Length > 0)
            .Select(kv => new SiteCookie(kv[0].Trim(), kv[1].Trim(), host, "/"))
            .ToList();
    }

    /// <summary>Checks the text can be read, so a bad paste is refused when it's saved rather than at the next check.</summary>
    public static string? Validate(string? text, string siteUrl)
    {
        try
        {
            Parse(text, siteUrl);
            return null;
        }
        catch (Exception e)
        {
            return "The cookies can't be read: " + e.Message;
        }
    }

    public static string Header(IEnumerable<SiteCookie> cookies, Uri url) => string.Join("; ", cookies
        .Where(c => url.Host.EndsWith(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
        .Select(c => $"{c.Name}={c.Value}"));
}
