using System.Collections.Concurrent;
using System.Net;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// The proxy the stores are reached through (Settings → Checking → Proxy), e.g. http://user:pass@proxy.example.com:8000 or
/// socks5://host:1080. A residential proxy gets past stores that block servers' addresses. Not used for ChatGPT.
/// </summary>
public static class StoreProxy
{
    public sealed record Parsed(string Server, string? Username, string? Password, Uri Uri);

    public static Parsed? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "socks5" or "socks4"))
            return null;
        string? user = null, pass = null;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return new Parsed($"{uri.Scheme}://{uri.Host}:{uri.Port}", user, pass, uri);
    }

    /// <summary>What's wrong with a proxy address, or null (empty is fine: no proxy).</summary>
    public static string? Validate(string? url) =>
        string.IsNullOrWhiteSpace(url) || Parse(url) != null
            ? null
            : "The proxy must look like http://user:password@host:port (or https://, socks5://).";

    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    /// <summary>An HTTP client through the proxy, kept per proxy address.</summary>
    public static HttpClient Client(Parsed proxy) => Clients.GetOrAdd(proxy.Uri.ToString(), _ =>
    {
        var handler = Tracking.PageFetcher.CreateHandler();
        handler.UseProxy = true;
        handler.Proxy = new WebProxy(new Uri(proxy.Server))
        {
            Credentials = proxy.Username == null ? null : new NetworkCredential(proxy.Username, proxy.Password),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    });
}
