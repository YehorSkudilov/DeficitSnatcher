using System.Net;
using System.Net.Http.Json;
using DeficitSnatcher.Api.Settings;

namespace DeficitSnatcher.Api.Tracking;

/// <summary>
/// Unlocker-kind pages: Bright Data's Web Unlocker API fetches the page (it gets past the store's bot check, from new
/// addresses, solving CAPTCHAs) and sends back its HTML, which is read like an Html page. No script of the page runs.
/// </summary>
public sealed class WebUnlocker(IHttpClientFactory clients)
{
    public const string ClientName = "web-unlocker";
    private const string Endpoint = "https://api.brightdata.com/request";

    /// <summary>The page's HTML through the Web Unlocker API, and the status the store answered with.</summary>
    public async Task<(HttpStatusCode Status, string Html)> FetchAsync(Uri url, BotSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.BrightDataApiToken) || string.IsNullOrWhiteSpace(settings.BrightDataUnlockerZone))
            throw new HttpRequestException("Web Unlocker isn't set up: add the Bright Data API token and Web Unlocker zone in Settings → Checking.");
        var body = new Dictionary<string, string> { ["zone"] = settings.BrightDataUnlockerZone, ["url"] = url.ToString(), ["format"] = "raw" };
        if (settings.BrightDataCountry.Length > 0)
            body["country"] = settings.BrightDataCountry;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // It retries the store itself (new addresses, solving the check), which can take a while.
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(settings.RequestTimeoutSeconds, 90)));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", settings.BrightDataApiToken);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Web Unlocker didn't answer in time.");
        }
        using (response)
        {
            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            // Its own errors (bad token, unknown zone, couldn't get past the store) come with this header.
            var error = Header(response, "x-brd-error") ?? Header(response, "x-luminati-error");
            if (error != null || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired)
            {
                var what = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when error == null || error.Contains("auth", StringComparison.OrdinalIgnoreCase)
                        => "Bright Data refused the API token",
                    _ => "Web Unlocker couldn't get the page",
                };
                throw new HttpRequestException($"{what} ({(int)response.StatusCode}): {error ?? Short(html)}");
            }
            return (response.StatusCode, html);
        }
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join("; ", v) : null;

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
