using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeficitSnatcher.Phone;

public sealed record AlarmDto(long Id, DateTimeOffset At, int? SiteId, string Product, string Store, decimal? Price, string Url, string Message, bool Test);

public sealed record AlarmsReply(long Latest, List<AlarmDto> Alarms);

/// <summary>
/// The site: signing in (the username and password are kept in the phone's secure storage, so it signs in again by itself
/// when its token runs out) and the alarms' long poll.
/// </summary>
public static class Server
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(75) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Url
    {
        get => Preferences.Get("server", "");
        private set => Preferences.Set("server", value);
    }

    public static string Username
    {
        get => Preferences.Get("username", "");
        private set => Preferences.Set("username", value);
    }

    public static bool SignedIn => Url.Length > 0 && Preferences.Get("signedIn", false);

    /// <summary>The last alarm seen (0: none yet, start from the newest).</summary>
    public static long LastAlarmId
    {
        get => Preferences.Get("lastAlarm", 0L);
        set => Preferences.Set("lastAlarm", value);
    }

    private static string? _token;

    /// <summary>Signs in and keeps the details; throws with a readable message when it can't.</summary>
    public static async Task SignInAsync(string url, string username, string password)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        _token = await LoginAsync(url, username.Trim(), password);
        Url = url;
        Username = username.Trim();
        await SecureStorage.Default.SetAsync("password", password);
        LastAlarmId = 0;
        Preferences.Set("signedIn", true);
    }

    public static void SignOut()
    {
        Preferences.Set("signedIn", false);
        SecureStorage.Default.Remove("password");
        _token = null;
    }

    private static async Task<string> LoginAsync(string url, string username, string password)
    {
        using var response = await Http.PostAsJsonAsync($"{url}/api/auth/login", new { username, password });
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Wrong username or password.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Too many tries: wait a minute.");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("token").GetString() ?? throw new InvalidOperationException("The site didn't send a token.");
    }

    /// <summary>A signed-in request; signs in again (once) when the token has run out.</summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (_token == null)
            {
                var password = await SecureStorage.Default.GetAsync("password") ?? throw new InvalidOperationException("Not signed in.");
                _token = await LoginAsync(Url, Username, password);
            }
            var request = make();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            var response = await Http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                return response;
            response.Dispose();
            _token = null;
        }
    }

    /// <summary>Alarms after the last one seen, waiting up to <paramref name="waitSeconds"/> for one.</summary>
    public static async Task<AlarmsReply> WaitForAlarmsAsync(long after, int waitSeconds, CancellationToken ct)
    {
        var query = after > 0 ? $"after={after}&wait={waitSeconds}" : "";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Url}/api/alarms?{query}"), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AlarmsReply>(Json, ct) ?? new AlarmsReply(after, []);
    }

    public static async Task<List<AlarmDto>> RecentAsync()
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Url}/api/alarms/recent"), CancellationToken.None);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<AlarmDto>>(Json) ?? [];
    }

    public static async Task TestAlarmAsync()
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Url}/api/alarms/test"), CancellationToken.None);
        response.EnsureSuccessStatusCode();
    }
}
