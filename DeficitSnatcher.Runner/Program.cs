// Deficit Snatcher's PC helper: lends the site this computer's Chrome, for stores that block servers (Walmart, Costco…).
//
// Double-click it, paste the setup code from the site (Settings → PC helper), done: it installs itself, runs in the
// background and starts again at every login. Its Chrome stays minimised; it comes up when a store wants you to prove
// you're human. Change things from a terminal:  deficitsnatcher help

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

return await Helper.MainAsync(args);

static partial class Helper
{
    // ---- Where things live ----

    static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeficitSnatcher");
    static readonly string AppDir = Path.Combine(Home, "app");
    static readonly string ConfigFile = Path.Combine(Home, "helper.json");
    static readonly string StateFile = Path.Combine(Home, "state.json");
    static readonly string LogFile = Path.Combine(Home, "helper.log");
    static readonly string LockFile = Path.Combine(Home, "helper.lock");
    static readonly string CommandFile = Path.Combine(Home, "command.txt");
    static readonly string ProfileDir = Path.Combine(Home, "chrome-profile");
    static string ExeName => OperatingSystem.IsWindows() ? "DeficitSnatcher.Runner.exe" : "DeficitSnatcher.Runner";
    static string InstalledExe => Path.Combine(AppDir, ExeName);

    sealed class Config
    {
        public string? Site { get; set; }
        public string? Key { get; set; }
        public bool Headless { get; set; }
        public bool Autostart { get; set; } = true;
    }

    sealed record State(bool Running, bool Connected, string? Site, string? Browser, DateTimeOffset? Since, string? LastError, int Pid);

    static Config LoadConfig() =>
        File.Exists(ConfigFile) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigFile)) ?? new Config() : new Config();

    static void SaveConfig(Config c)
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task<int> MainAsync(string[] args)
    {
        Directory.CreateDirectory(Home);
        var command = args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : "";

        // The background helper itself (what autostart runs): no window.
        if (command == "run")
            return await RunAsync();

        // Started without a terminal (double-clicked on Windows): a window to talk in.
        Terminal.Open();

        try
        {
            // Older way: --site … --key …
            if (args.Contains("--site") || args.Contains("--key"))
            {
                var c = LoadConfig();
                for (var i = 0; i < args.Length - 1; i++)
                    if (args[i] == "--site")
                        c.Site = args[i + 1].TrimEnd('/');
                    else if (args[i] == "--key")
                        c.Key = args[i + 1];
                SaveConfig(c);
                return Install(c);
            }
            return command switch
            {
                "" => Default(),
                "setup" => Setup(args.Length > 1 ? args[1] : null),
                "status" => Status(),
                "start" => Start(),
                "stop" => Stop(),
                "restart" => Restart(),
                "window" => Window(args.Length > 1 ? args[1] : ""),
                "autostart" => Autostart(args.Length > 1 ? args[1] : ""),
                "headless" => Headless(args.Length > 1 ? args[1] : ""),
                "logs" => Logs(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 40),
                "uninstall" => Uninstall(),
                "version" => Print($"Deficit Snatcher PC helper {typeof(Helper).Assembly.GetName().Version}"),
                _ => Help(),
            };
        }
        finally
        {
            Terminal.Done();
        }
    }

    // ---- Commands ----

    static int Default()
    {
        var c = LoadConfig();
        if (c.Site == null || c.Key == null)
            return Setup(null);
        // A newer download, opened: it replaces the installed copy (same setup).
        if (!string.Equals(Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)), Path.GetFullPath(AppDir),
                StringComparison.OrdinalIgnoreCase))
            return Install(c);
        // Set up already: make sure it's running, and say how it is.
        if (!IsRunning())
            StartBackground();
        return Status();
    }

    static int Setup(string? code)
    {
        Console.WriteLine("Deficit Snatcher PC helper");
        Console.WriteLine();
        if (string.IsNullOrWhiteSpace(code))
        {
            Console.WriteLine("Paste the setup code from your site (Helpers tab → Make a setup code), then press Enter:");
            Console.Write("> ");
            code = Console.ReadLine();
        }
        if (!SetupCode.TryRead(code, out var site, out var key))
            return Fail("That isn't a setup code. Copy the whole code from the site (it starts with DSH1.).");
        var c = LoadConfig();
        c.Site = site;
        c.Key = key;
        SaveConfig(c);
        Console.WriteLine($"Site: {site}");
        return Install(c);
    }

    /// <summary>Copies the helper to its own folder (the download can go), sets up autostart, and starts it.</summary>
    static int Install(Config c)
    {
        try
        {
            var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetFullPath(here), Path.GetFullPath(AppDir), StringComparison.OrdinalIgnoreCase))
            {
                if (IsRunning())
                    StopBackground();
                Console.WriteLine("Installing…");
                CopyDirectory(here, AppDir);
            }
            AddToPath();
            if (c.Autostart)
                Service.Enable();
            if (IsRunning())
                StopBackground();
            StartBackground();
            Console.WriteLine();
            Console.WriteLine(IsRunning() ? "Done. The helper runs in the background and starts again when you log in." : "Installed, but it didn't start: deficitsnatcher logs");
            Console.WriteLine("Its Chrome window opens: sign in to Walmart, Amazon and Costco there once. It's minimised after that.");
            Console.WriteLine("On the site, tick \"Run on my PC\" on those stores' pages.");
            Console.WriteLine();
            Console.WriteLine("Later, in a new terminal:  deficitsnatcher status | stop | start | window show | logs | help");
            return 0;
        }
        catch (Exception e)
        {
            return Fail("Couldn't install: " + e.Message);
        }
    }

    static int Status()
    {
        var c = LoadConfig();
        var running = IsRunning();
        var s = ReadState();
        Console.WriteLine($"Site:       {c.Site ?? "(not set up: deficitsnatcher setup)"}");
        Console.WriteLine($"Running:    {(running ? "yes" : "no (deficitsnatcher start)")}");
        if (running && s != null)
        {
            Console.WriteLine($"Connected:  {(s.Connected ? $"yes, since {s.Since?.LocalDateTime:g}" : "no, trying…")}");
            Console.WriteLine($"Browser:    {s.Browser}");
            if (!s.Connected && s.LastError != null)
                Console.WriteLine($"Last error: {s.LastError}");
        }
        Console.WriteLine($"Autostart:  {(c.Autostart ? "on" : "off")}");
        Console.WriteLine($"Window:     {(c.Headless ? "none (headless)" : "minimised Chrome")}");
        Console.WriteLine($"Files:      {Home}");
        return 0;
    }

    static int Start()
    {
        if (LoadConfig() is not { Site: not null, Key: not null })
            return Fail("Not set up yet: deficitsnatcher setup");
        if (IsRunning())
            return Print("It's already running.");
        StartBackground();
        return Print(IsRunning() ? "Started." : "It didn't start: deficitsnatcher logs");
    }

    static int Stop()
    {
        if (!IsRunning())
            return Print("It isn't running.");
        StopBackground();
        return Print("Stopped (it starts again at your next login, unless: deficitsnatcher autostart off).");
    }

    static int Restart()
    {
        if (IsRunning())
            StopBackground();
        StartBackground();
        return Print(IsRunning() ? "Restarted." : "It didn't start: deficitsnatcher logs");
    }

    static int Window(string what)
    {
        if (what is not ("show" or "hide"))
            return Fail("deficitsnatcher window show | hide");
        if (!IsRunning())
            return Fail("It isn't running: deficitsnatcher start");
        File.WriteAllText(CommandFile, what);
        return Print(what == "show" ? "Showing the helper's Chrome." : "Minimised the helper's Chrome.");
    }

    static int Autostart(string what)
    {
        if (what is not ("on" or "off"))
            return Fail("deficitsnatcher autostart on | off");
        var c = LoadConfig();
        c.Autostart = what == "on";
        SaveConfig(c);
        if (c.Autostart)
            Service.Enable();
        else
            Service.Disable();
        return Print(c.Autostart ? "It starts at every login." : "It no longer starts at login (deficitsnatcher start to run it now).");
    }

    static int Headless(string what)
    {
        if (what is not ("on" or "off"))
            return Fail("deficitsnatcher headless on | off   (on: no Chrome window at all; stores can't ask you to prove you're human then)");
        var c = LoadConfig();
        c.Headless = what == "on";
        SaveConfig(c);
        if (IsRunning())
        {
            StopBackground();
            StartBackground();
        }
        return Print(c.Headless ? "Chrome now runs without a window." : "Chrome runs in a minimised window.");
    }

    static int Logs(int lines)
    {
        if (!File.Exists(LogFile))
            return Print("No log yet.");
        using var f = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(f);
        foreach (var l in r.ReadToEnd().Split('\n').TakeLast(lines))
            Console.WriteLine(l.TrimEnd());
        return 0;
    }

    static int Uninstall()
    {
        if (IsRunning())
            StopBackground();
        Service.Disable();
        RemoveFromPath();
        Console.WriteLine("Removed: it won't start again. Your settings and its Chrome profile (sign-ins) are kept in:");
        Console.WriteLine("  " + Home);
        Console.WriteLine("Delete that folder too to remove everything.");
        return 0;
    }

    static int Help()
    {
        Console.WriteLine("""
            Deficit Snatcher PC helper

              deficitsnatcher                 set up (first time), or start it and show its status
              deficitsnatcher setup [code]    connect to your site with a setup code from Settings → PC helper
              deficitsnatcher status          running? connected?
              deficitsnatcher start | stop | restart
              deficitsnatcher window show     show its Chrome (to sign in to stores); window hide to minimise it
              deficitsnatcher autostart on    start at every login (off: only when you start it)
              deficitsnatcher headless on     no Chrome window at all (off: minimised window, the default)
              deficitsnatcher logs [lines]    what it's been doing
              deficitsnatcher uninstall       stop it and don't start it again
            """);
        return 0;
    }

    static int Print(string s)
    {
        Console.WriteLine(s);
        return 0;
    }

    static int Fail(string s)
    {
        Console.WriteLine(s);
        return 1;
    }

    // ---- Running in the background ----

    static bool IsRunning()
    {
        try
        {
            using var f = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false; // Nobody holds it.
        }
        catch (IOException)
        {
            return true;
        }
    }

    static void StartBackground()
    {
        var exe = File.Exists(InstalledExe) ? InstalledExe : Path.Combine(AppContext.BaseDirectory, ExeName);
        if (Service.StartsItself && Service.IsEnabled())
        {
            Service.StartNow();
            for (var i = 0; i < 20 && !IsRunning(); i++)
                Thread.Sleep(250);
        }
        // Windows, or the service manager didn't start it (no user session…): start it directly.
        if (!IsRunning())
            Process.Start(new ProcessStartInfo(exe, "run") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Home });
        for (var i = 0; i < 40 && !IsRunning(); i++)
            Thread.Sleep(250);
    }

    static void StopBackground()
    {
        if (Service.StartsItself && Service.IsEnabled())
            Service.StopNow();
        File.WriteAllText(CommandFile, "stop");
        for (var i = 0; i < 60 && IsRunning(); i++)
            Thread.Sleep(250);
        if (IsRunning() && ReadState() is { Pid: > 0 } s)
            try
            {
                Process.GetProcessById(s.Pid).Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
    }

    static State? ReadState()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void WriteState(State s)
    {
        try
        {
            File.WriteAllText(StateFile, JsonSerializer.Serialize(s));
        }
        catch (IOException)
        {
        }
    }

    static readonly object LogLock = new();

    static void Log(string s)
    {
        lock (LogLock)
            try
            {
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 2_000_000)
                    File.Move(LogFile, LogFile + ".old", overwrite: true);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
    }

    /// <summary>The helper itself: Chrome, and the connection to the site, until it's told to stop.</summary>
    static async Task<int> RunAsync()
    {
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return 0; // Already running.
        }
        using var held = lockStream;
        File.Delete(CommandFile);
        var c = LoadConfig();
        if (c.Site == null || c.Key == null)
        {
            Log("Not set up: run deficitsnatcher setup.");
            return 1;
        }
        Log($"Starting for {c.Site}");

        using var stop = new CancellationTokenSource();
        var state = new State(true, false, c.Site, null, null, null, Environment.ProcessId);
        WriteState(state);

        // Commands from the terminal (stop, window show/hide), through a file.
        IBrowserContext? browser = null;
        // Headless, but the site needs you (sign in, prove you're human): Chrome runs with a window until this time, then
        // goes back to headless. The page to open once it's up.
        var visibleUntil = DateTime.MinValue;
        string? pendingUrl = null;
        var runningHeadless = false;
        CancellationTokenSource? restartChrome = null;
        var restartNow = false;
        Func<string, Task> show = async url =>
        {
            visibleUntil = DateTime.UtcNow.AddMinutes(20);
            if (runningHeadless || browser == null)
            {
                Log("The site needs you: starting Chrome with a window.");
                pendingUrl = url;
                restartNow = true;
                restartChrome?.Cancel();
                return;
            }
            await Chrome.OpenWindowAsync(browser, url);
        };
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000);
                if (!File.Exists(CommandFile))
                    continue;
                string cmd;
                try
                {
                    cmd = File.ReadAllText(CommandFile).Trim();
                    File.Delete(CommandFile);
                }
                catch (IOException)
                {
                    continue;
                }
                Log($"Command: {cmd}");
                if (cmd == "stop")
                    stop.Cancel();
                else if (browser != null && cmd is "show" or "hide")
                    await Chrome.SetWindowAsync(browser, cmd == "show" ? "normal" : "minimized");
            }
        });
        // Back to headless once the window's time is up.
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(30000);
                if (c.Headless && !runningHeadless && browser != null && DateTime.UtcNow > visibleUntil)
                {
                    Log("Back to running Chrome without a window.");
                    restartNow = true;
                    restartChrome?.Cancel();
                }
            }
        });

        // Chrome, started again if it's closed (until told to stop).
        while (!stop.IsCancellationRequested)
        {
            try
            {
                c = LoadConfig();
                restartNow = false;
                var headless = c.Headless && DateTime.UtcNow > visibleUntil;
                var (context, cdpUrl, name) = await Chrome.StartAsync(headless, Log);
                browser = context;
                runningHeadless = headless;
                state = state with { Browser = name };
                WriteState(state);
                using var chromeClosed = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                restartChrome = chromeClosed;
                context.Close += (_, _) => chromeClosed.Cancel();
                if (pendingUrl is { } url)
                {
                    pendingUrl = null;
                    await Chrome.OpenWindowAsync(context, url);
                }
                await ConnectLoopAsync(c, cdpUrl, name, change => WriteState(state = change(state)), show, chromeClosed.Token);
                if (!stop.IsCancellationRequested && !restartNow)
                    Log("Chrome was closed: starting it again in 5 s (deficitsnatcher stop to stop the helper).");
                try
                {
                    await context.CloseAsync();
                }
                catch (Exception)
                {
                }
            }
            catch (Exception e)
            {
                Log("Chrome couldn't start: " + e.Message);
                state = state with { LastError = e.Message };
                WriteState(state);
            }
            try
            {
                // Restarting to show or hide the window: straight away.
                if (!restartNow)
                    await Task.Delay(5000, stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        Log("Stopped.");
        WriteState(state with { Running = false, Connected = false });
        return 0;
    }

    /// <summary>Stays connected to the site (reconnecting when it drops), lending Chrome when the site asks.</summary>
    static async Task ConnectLoopAsync(Config c, string cdpUrl, string browserName, Action<Func<State, State>> update, Func<string, Task> show,
        CancellationToken ct)
    {
        var wsBase = new Uri(c.Site!.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/'));
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var control = Socket(c.Key!);
                var query = $"?machine={Uri.EscapeDataString(Environment.MachineName)}&browser={Uri.EscapeDataString(browserName)}&can=show";
                await control.ConnectAsync(new Uri(wsBase, "/api/runner/connect" + query), ct);
                Log("Connected to the site.");
                update(s => s with { Connected = true, Since = DateTimeOffset.Now, LastError = null });
                delay = TimeSpan.FromSeconds(2);
                var buffer = new byte[4096];
                while (control.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await control.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Log($"The site closed the connection: {control.CloseStatusDescription}");
                        break;
                    }
                    using var msg = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
                    // Read the id now: the message is gone by the time the task runs.
                    if (msg.RootElement.TryGetProperty("open", out var open) && open.GetString() is { } pipeId)
                        _ = Task.Run(() => PipeAsync(wsBase, c.Key!, cdpUrl, pipeId, ct));
                    // The site needs you here (sign in, prove you're human): a window in front, on that page.
                    else if (msg.RootElement.TryGetProperty("show", out var shown) && shown.GetString() is { } showUrl
                             && Uri.TryCreate(showUrl, UriKind.Absolute, out var su) && su.Scheme is "https" or "http")
                    {
                        Log($"The site needs you on {su.Host}: bringing Chrome up.");
                        _ = Task.Run(() => show(showUrl));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                var why = e.Message.Contains("401") ? "the key was refused: get a new setup code (deficitsnatcher setup)" : e.Message;
                Log($"Can't reach the site ({why}); trying again in {delay.TotalSeconds:0} s.");
                update(s => s with { LastError = why });
            }
            update(s => s with { Connected = false });
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    /// <summary>The site asked for the browser: joins a new socket to the site with one to Chrome's DevTools.</summary>
    static async Task PipeAsync(Uri wsBase, string key, string cdpUrl, string id, CancellationToken ct)
    {
        using var site = Socket(key);
        using var chrome = new ClientWebSocket();
        chrome.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            await site.ConnectAsync(new Uri(wsBase, $"/api/runner/pipe/{id}"), ct);
            await chrome.ConnectAsync(new Uri(cdpUrl), ct);
            Log("The site is using the browser.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(Copy(site, chrome, cts.Token), Copy(chrome, site, cts.Token));
            cts.Cancel();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log($"Couldn't lend the browser: {e.Message}");
        }
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

    // ---- Installing ----

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }

    /// <summary>"deficitsnatcher" works in a new terminal: on Windows the app folder joins the user's PATH (with a
    /// deficitsnatcher.cmd); elsewhere a link in ~/.local/bin.</summary>
    static void AddToPath()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(AppDir, "deficitsnatcher.cmd"), $"@\"%~dp0{ExeName}\" %*\r\n");
                var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                if (!path.Split(';').Contains(AppDir, StringComparer.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable("PATH", path.TrimEnd(';') + ";" + AppDir, EnvironmentVariableTarget.User);
            }
            else
            {
                var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
                Directory.CreateDirectory(bin);
                var link = Path.Combine(bin, "deficitsnatcher");
                if (File.Exists(link))
                    File.Delete(link);
                File.CreateSymbolicLink(link, InstalledExe);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"(Couldn't add the deficitsnatcher command: {e.Message}. Use the program in {AppDir} instead.)");
        }
    }

    static void RemoveFromPath()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                Environment.SetEnvironmentVariable("PATH",
                    string.Join(';', path.Split(';').Where(p => !string.Equals(p, AppDir, StringComparison.OrdinalIgnoreCase))),
                    EnvironmentVariableTarget.User);
            }
            else
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "deficitsnatcher"));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Starting at login: Windows' Run key, a macOS LaunchAgent, a systemd user service on Linux.</summary>
    static class Service
    {
        const string Name = "DeficitSnatcher";
        static string MacPlist => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "ca.deficitsnatcher.helper.plist");
        static string LinuxUnit => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", "deficitsnatcher.service");

        /// <summary>The service manager starts and restarts it (macOS, Linux); on Windows the helper starts it itself.</summary>
        public static bool StartsItself => !OperatingSystem.IsWindows();

        public static bool IsEnabled() =>
            OperatingSystem.IsWindows() ? WindowsRunValue() != null : OperatingSystem.IsMacOS() ? File.Exists(MacPlist) : File.Exists(LinuxUnit);

        public static void Enable()
        {
            if (OperatingSystem.IsWindows())
                Shell("reg", $"add HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {Name} /t REG_SZ /d \"\\\"{InstalledExe}\\\" run\" /f");
            else if (OperatingSystem.IsMacOS())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MacPlist)!);
                File.WriteAllText(MacPlist, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0"><dict>
                      <key>Label</key><string>ca.deficitsnatcher.helper</string>
                      <key>ProgramArguments</key><array><string>{InstalledExe}</string><string>run</string></array>
                      <key>RunAtLoad</key><true/>
                      <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
                      <key>WorkingDirectory</key><string>{Home}</string>
                    </dict></plist>
                    """);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LinuxUnit)!);
                File.WriteAllText(LinuxUnit, $"""
                    [Unit]
                    Description=Deficit Snatcher PC helper
                    [Service]
                    ExecStart="{InstalledExe}" run
                    Restart=on-failure
                    RestartSec=10
                    [Install]
                    WantedBy=default.target
                    """);
                Shell("systemctl", "--user daemon-reload");
                Shell("systemctl", "--user enable deficitsnatcher");
            }
        }

        public static void Disable()
        {
            if (OperatingSystem.IsWindows())
                Shell("reg", $"delete HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {Name} /f");
            else if (OperatingSystem.IsMacOS())
            {
                Shell("launchctl", $"unload -w \"{MacPlist}\"");
                File.Delete(MacPlist);
            }
            else
            {
                Shell("systemctl", "--user disable --now deficitsnatcher");
                File.Delete(LinuxUnit);
            }
        }

        public static void StartNow()
        {
            if (OperatingSystem.IsMacOS())
                Shell("launchctl", $"load -w \"{MacPlist}\"");
            else
                Shell("systemctl", "--user start deficitsnatcher");
        }

        public static void StopNow()
        {
            if (OperatingSystem.IsMacOS())
                Shell("launchctl", $"unload \"{MacPlist}\"");
            else
                Shell("systemctl", "--user stop deficitsnatcher");
        }

        static string? WindowsRunValue()
        {
            var (code, output) = Shell("reg", $"query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {Name}");
            return code == 0 && output.Contains(Name) ? output : null;
        }

        static (int, string) Shell(string file, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                return (p.ExitCode, output);
            }
            catch (Exception e)
            {
                return (-1, e.Message);
            }
        }
    }

    // ---- Chrome ----

    static class Chrome
    {
        static IPlaywright? _playwright;

        /// <summary>Chrome (the installed Google Chrome when there is one) with its own profile and a DevTools port on this
        /// computer only; minimised once the stores have been signed in to.</summary>
        public static async Task<(IBrowserContext Context, string CdpUrl, string Name)> StartAsync(bool headless, Action<string> log)
        {
            Directory.CreateDirectory(ProfileDir);
            _playwright ??= await Playwright.CreateAsync();
            var port = FreePort();
            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = headless,
                Args = [$"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", "--disable-blink-features=AutomationControlled"],
                ViewportSize = ViewportSize.NoViewport,
                Locale = "en-CA",
            };
            IBrowserContext context;
            string name;
            try
            {
                context = await _playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, new(options) { Channel = "chrome" });
                name = "Google Chrome";
            }
            catch (PlaywrightException)
            {
                log("Google Chrome isn't installed: using Chromium (downloading it the first time).");
                Microsoft.Playwright.Program.Main(["install", "chromium"]);
                context = await _playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, new(options) { Channel = "chromium" });
                name = "Chromium";
            }

            var start = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
            await start.SetContentAsync("""
                <html><head><title>Deficit Snatcher PC helper</title></head>
                <body style="font-family:system-ui;background:#0a0c10;color:#eee;padding:40px;max-width:640px">
                <h1 style="color:#ff4d5e">Deficit Snatcher PC helper</h1>
                <p>This Chrome belongs to the helper; it stays minimised. The site checks and buys the pages set to <b>Run on my PC</b>
                here, in new tabs.</p>
                <p>Sign in to the stores (Walmart, Amazon, Costco…) in a new tab once: this Chrome keeps its own profile.</p>
                <p>When a store asks you to prove you're human (<i>Press &amp; Hold</i>), this window comes up and you get an alert.</p>
                <p style="color:#999">In a terminal: <code>deficitsnatcher window hide</code> to minimise it, <code>deficitsnatcher help</code>.</p>
                </body></html>
                """);

            using var http = new HttpClient();
            var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version")).RootElement;
            var cdpUrl = version.GetProperty("webSocketDebuggerUrl").GetString()!;
            var full = $"{name} {version.GetProperty("Browser").GetString()?.Split('/').LastOrDefault()}";
            log($"Browser: {full}");

            // Shown the first time (to sign in to the stores), minimised after that.
            var marker = Path.Combine(Home, "started-before");
            if (!headless && File.Exists(marker))
                await SetWindowAsync(context, "minimized");
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
            return (context, cdpUrl, full);
        }

        /// <summary>The page in a window of its own, in front (the bot's tabs open in the background elsewhere).</summary>
        public static async Task OpenWindowAsync(IBrowserContext context, string url)
        {
            try
            {
                var first = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
                var cdp = await context.NewCDPSessionAsync(first);
                await cdp.SendAsync("Target.createTarget", new Dictionary<string, object> { ["url"] = url, ["newWindow"] = true });
                await cdp.DetachAsync();
                await Task.Delay(1500);
                var page = context.Pages.LastOrDefault(p => p.Url.StartsWith(url[..Math.Min(url.Length, 30)], StringComparison.OrdinalIgnoreCase))
                           ?? context.Pages.Last();
                var c2 = await context.NewCDPSessionAsync(page);
                var window = await c2.SendAsync("Browser.getWindowForTarget");
                await c2.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
                {
                    ["windowId"] = window!.Value.GetProperty("windowId").GetInt32(),
                    ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
                });
                await c2.DetachAsync();
                await page.BringToFrontAsync();
            }
            catch (Exception e)
            {
                Log("Couldn't open the window: " + e.Message.Split('\n')[0]);
            }
        }

        public static async Task SetWindowAsync(IBrowserContext context, string state)
        {
            try
            {
                var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
                var cdp = await context.NewCDPSessionAsync(page);
                var window = await cdp.SendAsync("Browser.getWindowForTarget");
                var id = window!.Value.GetProperty("windowId").GetInt32();
                await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
                {
                    ["windowId"] = id,
                    ["bounds"] = new Dictionary<string, object> { ["windowState"] = state },
                });
                if (state == "normal")
                    await page.BringToFrontAsync();
                await cdp.DetachAsync();
            }
            catch (Exception)
            {
                // Headless: no window.
            }
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }
    }

    // ---- The setup code: the site's address and the key in one ----

    static class SetupCode
    {
        public static bool TryRead(string? code, out string site, out string key)
        {
            site = key = "";
            code = code?.Trim();
            if (string.IsNullOrEmpty(code) || !code.StartsWith("DSH1.", StringComparison.Ordinal))
                return false;
            try
            {
                var b64 = code[5..].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
                site = doc.RootElement.GetProperty("site").GetString()!.TrimEnd('/');
                key = doc.RootElement.GetProperty("key").GetString()!;
                return Uri.TryCreate(site, UriKind.Absolute, out _) && key.StartsWith("dsr_");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // ---- A console when started without one (Windows, double-clicked) ----

    static partial class Terminal
    {
        static bool _ownWindow;

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AttachConsole(int processId);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AllocConsole();

        /// <summary>On Windows the program has no console of its own (so the background helper has no window): borrow the
        /// terminal it was typed in, or open a window when it was double-clicked.</summary>
        public static void Open()
        {
            if (!OperatingSystem.IsWindows())
                return;
            if (!AttachConsole(-1))
            {
                AllocConsole();
                _ownWindow = true;
            }
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
            if (!_ownWindow)
                Console.WriteLine();
        }

        /// <summary>A window opened for this: keep it until Enter, so what happened can be read.</summary>
        public static void Done()
        {
            if (!_ownWindow)
                return;
            Console.WriteLine();
            Console.WriteLine("Press Enter to close this window.");
            Console.ReadLine();
        }
    }
}
