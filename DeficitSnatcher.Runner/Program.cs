// Deficit Snatcher's PC helper: lends the site this computer's Chrome, for stores that block servers (Walmart).
//
//   DeficitSnatcher.Runner --site https://snatcher.example.com --key dsr_…     (first time; both are remembered)
//   DeficitSnatcher.Runner                                                     (after that)
//
// It opens Chrome with its own profile (sign in to the stores there once; it's kept), and connects out to the site. The
// site then checks and buys the pages set to "Run on my PC" in that Chrome. Nothing on this computer listens to the
// network: the connection goes out to the site. Close this window (or Ctrl+C) to stop.

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Playwright;

var options = Options.Load(args);
if (options.Site == null || options.Key == null)
{
    Console.WriteLine("""
        Deficit Snatcher PC helper

        Start it once with your site's address and the helper key (the site: Settings → PC helper → Make a key):
          DeficitSnatcher.Runner --site https://your-site --key dsr_...
        Both are remembered for next time.
        """);
    return 1;
}
Directory.CreateDirectory(options.ProfileDir);
Console.WriteLine($"Site:    {options.Site}");
Console.WriteLine($"Profile: {options.ProfileDir}");

// Chrome with a DevTools port on this computer only (the helper joins it to the site's connection).
var port = FreePort();
using var playwright = await Playwright.CreateAsync();
var launch = new BrowserTypeLaunchPersistentContextOptions
{
    Headless = options.Headless,
    Args = [$"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", "--disable-blink-features=AutomationControlled"],
    ViewportSize = ViewportSize.NoViewport,
    Locale = "en-CA",
};
IBrowserContext browser;
string browserName;
try
{
    // The real Chrome, when it's installed: what stores see every day.
    browser = await playwright.Chromium.LaunchPersistentContextAsync(options.ProfileDir, new(launch) { Channel = "chrome" });
    browserName = "Google Chrome";
}
catch (PlaywrightException)
{
    Console.WriteLine("Google Chrome isn't installed: using Chromium (downloading it the first time)…");
    Microsoft.Playwright.Program.Main(["install", "chromium"]);
    browser = await playwright.Chromium.LaunchPersistentContextAsync(options.ProfileDir, new(launch) { Channel = "chromium" });
    browserName = "Chromium";
}

var start = browser.Pages.Count > 0 ? browser.Pages[0] : await browser.NewPageAsync();
await start.SetContentAsync("""
    <html><head><title>Deficit Snatcher PC helper</title></head>
    <body style="font-family:system-ui;background:#0a0c10;color:#eee;padding:40px;max-width:640px">
    <h1 style="color:#ff4d5e">Deficit Snatcher PC helper</h1>
    <p>Keep this window open. The site checks and buys the pages set to <b>Run on my PC</b> here, in new tabs.</p>
    <p>Sign in to the stores (Walmart…) in a new tab once: this browser keeps its own profile, so it stays signed in.</p>
    <p>If a store asks you to prove you're human (<i>Press &amp; Hold</i>), you'll get an alert: do it in that tab, and the bot carries on.</p>
    </body></html>
    """);

using var http = new HttpClient();
var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version")).RootElement;
var cdpUrl = version.GetProperty("webSocketDebuggerUrl").GetString()!;
var browserVersion = $"{browserName} {version.GetProperty("Browser").GetString()?.Split('/').LastOrDefault()}";
Console.WriteLine($"Browser: {browserVersion}");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
browser.Close += (_, _) => stop.Cancel();

// Stay connected to the site, reconnecting when the connection drops.
var wsBase = new Uri(options.Site.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/'));
var delay = TimeSpan.FromSeconds(2);
while (!stop.IsCancellationRequested)
{
    try
    {
        using var control = Socket(options.Key);
        var query = $"?machine={Uri.EscapeDataString(Environment.MachineName)}&browser={Uri.EscapeDataString(browserVersion)}";
        await control.ConnectAsync(new Uri(wsBase, "/api/runner/connect" + query), stop.Token);
        Console.WriteLine($"{DateTime.Now:T} Connected to the site.");
        delay = TimeSpan.FromSeconds(2);
        var buffer = new byte[4096];
        while (control.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            var r = await control.ReceiveAsync(buffer, stop.Token);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine($"{DateTime.Now:T} The site closed the connection: {control.CloseStatusDescription}");
                break;
            }
            if (Environment.GetEnvironmentVariable("DS_DEBUG") == "1")
                Console.WriteLine($"{DateTime.Now:T} from site: {System.Text.Encoding.UTF8.GetString(buffer, 0, r.Count)}");
            using var msg = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
            // Read the id now: the message is gone by the time the task runs.
            if (msg.RootElement.TryGetProperty("open", out var open) && open.GetString() is { } pipeId)
                _ = Task.Run(() => PipeAsync(pipeId, stop.Token));
        }
    }
    catch (Exception e) when (e is WebSocketException or HttpRequestException)
    {
        Console.WriteLine($"{DateTime.Now:T} Can't reach the site ({e.Message}); trying again in {delay.TotalSeconds:0} s.");
        if (e.Message.Contains("401"))
            Console.WriteLine("  The key was refused: make a new one on the site (Settings → PC helper) and start with --key.");
    }
    catch (OperationCanceledException)
    {
        break;
    }
    try
    {
        await Task.Delay(delay, stop.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
}

Console.WriteLine("Stopping…");
await browser.CloseAsync();
return 0;

// The site asked for the browser: join a new socket to the site with one to Chrome's DevTools.
async Task PipeAsync(string id, CancellationToken ct)
{
    Debug($"pipe {id}: start");
    using var site = Socket(options.Key!);
    using var chrome = new ClientWebSocket();
    chrome.Options.KeepAliveInterval = TimeSpan.Zero;
    try
    {
        Debug($"pipe {id}: connecting to the site");
        await site.ConnectAsync(new Uri(wsBase, $"/api/runner/pipe/{id}"), ct);
        Debug($"pipe {id}: connecting to Chrome {cdpUrl}");
        await chrome.ConnectAsync(new Uri(cdpUrl), ct);
        Console.WriteLine($"{DateTime.Now:T} The site is using the browser.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(Copy(site, chrome, cts.Token), Copy(chrome, site, cts.Token));
        cts.Cancel();
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception e)
    {
        Console.WriteLine($"{DateTime.Now:T} Couldn't lend the browser: {e.GetType().Name}: {e.Message}");
    }
    Console.WriteLine($"{DateTime.Now:T} The site is done with the browser.");
}

static void Debug(string s)
{
    if (Environment.GetEnvironmentVariable("DS_DEBUG") == "1")
        Console.WriteLine($"{DateTime.Now:T} {s}");
}

static async Task Copy(WebSocket from, WebSocket to, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var r = await from.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                return;
            await to.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct);
        }
    }
    catch (Exception e) when (e is WebSocketException or OperationCanceledException)
    {
    }
}

static ClientWebSocket Socket(string key)
{
    var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("Authorization", "Runner " + key);
    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
    return ws;
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

/// <summary>--site, --key, --profile, --headless; site and key are remembered in a file next to the profile.</summary>
sealed class Options
{
    public string? Site { get; set; }
    public string? Key { get; set; }
    public string ProfileDir { get; set; } = "";
    public bool Headless { get; set; }

    private static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeficitSnatcher");
    private static string File => Path.Combine(Home, "helper.json");

    public static Options Load(string[] args)
    {
        var saved = System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Options>(System.IO.File.ReadAllText(File)) ?? new Options() : new Options();
        var o = new Options { Site = saved.Site, Key = saved.Key, ProfileDir = Path.Combine(Home, "chrome-profile") };
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--site" when i + 1 < args.Length:
                    o.Site = args[++i].TrimEnd('/');
                    break;
                case "--key" when i + 1 < args.Length:
                    o.Key = args[++i];
                    break;
                case "--profile" when i + 1 < args.Length:
                    o.ProfileDir = args[++i];
                    break;
                case "--headless":
                    o.Headless = true;
                    break;
            }
        if (o.Site != null && o.Key != null && (o.Site != saved.Site || o.Key != saved.Key))
        {
            Directory.CreateDirectory(Home);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new { o.Site, o.Key }));
        }
        return o;
    }
}
