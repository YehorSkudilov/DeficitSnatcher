using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// A page's layout in a few characters: its element ids and class names, not its text, prices or counters, and not names
/// with numbers in them (a build's hashed class names, per-visit ids). The same fingerprint means the AI has already looked
/// at this page as it is, so it isn't asked again until the page changes.
/// </summary>
public static partial class PageFingerprint
{
    [GeneratedRegex(@"\b(?:id|class)\s*=\s*[""']([^""']{1,300})[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Attributes();

    [GeneratedRegex(@"^[A-Za-z_-]{2,40}$")]
    private static partial Regex Plain();

    public static string? Of(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return null;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Attributes().Matches(html))
            foreach (var name in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (Plain().IsMatch(name))
                    names.Add(name);
        if (names.Count < 5)
            return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(' ', names))))[..16];
    }
}
