namespace DeficitSnatcher.Api.Pc;

/// <summary>The newest PC helper on the download page (the pc-helper release's version.txt), looked up at most every 10 minutes.</summary>
public sealed class HelperVersions(IHttpClientFactory httpFactory, ILogger<HelperVersions> log)
{
    public const string ReleaseBase = "https://github.com/YehorSkudilov/DeficitSnatcher/releases/download/pc-helper";
    private (string? Latest, string? Error, DateTimeOffset At) _cached;
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<(string? Latest, string? Error)> LatestAsync()
    {
        if (DateTimeOffset.UtcNow - _cached.At < TimeSpan.FromMinutes(10))
            return (_cached.Latest, _cached.Error);
        await _one.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _cached.At < TimeSpan.FromMinutes(10))
                return (_cached.Latest, _cached.Error);
            try
            {
                var http = httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(20);
                var text = (await http.GetStringAsync($"{ReleaseBase}/version.txt")).Trim();
                _cached = Version.TryParse(text, out _) ? (text, null, DateTimeOffset.UtcNow) : (null, "No version on the download page yet.", DateTimeOffset.UtcNow);
            }
            catch (Exception e)
            {
                log.LogInformation("Couldn't look up the helper's latest version: {Error}", e.Message);
                _cached = (null, "Couldn't look up the latest version (no version on the download page yet, or no internet).", DateTimeOffset.UtcNow);
            }
            return (_cached.Latest, _cached.Error);
        }
        finally
        {
            _one.Release();
        }
    }
}
