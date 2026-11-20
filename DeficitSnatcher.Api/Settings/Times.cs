using System.Globalization;
using System.Text.RegularExpressions;

namespace DeficitSnatcher.Api.Settings;

/// <summary>
/// The server keeps and writes every time in UTC. A time inside a message is written as a marker,
/// <c>{{t:2026-11-20T15:00:00Z|style}}</c>, that the site turns into the viewer's own local time (lib/api.ts). Styles:
/// none (date and time), <c>time</c>, <c>when</c> ("Today, around 7:00 AM"), <c>weekly</c> ("Fridays, around 7:00 AM").
/// Text that leaves the site (email) gets plain UTC instead (<see cref="Plain"/>).
/// </summary>
public static partial class Times
{
    public static string At(DateTimeOffset at, string style = "") =>
        "{{t:" + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + (style.Length > 0 ? "|" + style : "") + "}}";

    public static string At(DateTimeOffset? at, string style = "", string none = "never") => at is { } t ? At(t, style) : none;

    [GeneratedRegex(@"\{\{t:([0-9T:\-\.]+Z)(?:\|(\w+))?\}\}")]
    private static partial Regex Marker();

    /// <summary>The text with its time markers as plain UTC, for where the site doesn't render it (email, logs).</summary>
    public static string Plain(string text) => Marker().Replace(text, m =>
    {
        if (!DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return m.Value;
        var u = at.UtcDateTime;
        return m.Groups[2].Value switch
        {
            "time" => u.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC",
            "weekly" => u.ToString("dddd", CultureInfo.InvariantCulture) + "s, around " + u.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC",
            "when" => u.ToString("ddd MMM d", CultureInfo.InvariantCulture) + ", around " + u.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC",
            _ => u.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
        };
    });
}
