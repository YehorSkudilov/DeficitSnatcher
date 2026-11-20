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
        public bool Autostart { get; set; } = true;
        /// <summary>Installs new versions by itself (checked every few hours).</summary>
        public bool AutoUpdate { get; set; } = true;
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
        // A new version, started by the old one (Windows): it takes over once that one has stopped.
        if (command == "finish-update")
            return FinishUpdate(args.Length > 1 && int.TryParse(args[1], out var oldPid) ? oldPid : 0);

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
                "update" => SendCommand("update", "Checking for a new version: deficitsnatcher logs to see how it went."),
                "autoupdate" => AutoUpdate(args.Length > 1 ? args[1] : ""),
                "logs" => Logs(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 40),
                "uninstall" => Uninstall(),
                "version" => Print($"Deficit Snatcher PC helper {MyVersion}"),
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
        Console.WriteLine($"Version:    {MyVersion} (updates by itself: {(c.AutoUpdate ? "on" : "off")})");
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

    static int AutoUpdate(string what)
    {
        if (what is not ("on" or "off"))
            return Fail("deficitsnatcher autoupdate on | off");
        var c = LoadConfig();
        c.AutoUpdate = what == "on";
        SaveConfig(c);
        return Print(c.AutoUpdate ? "It installs new versions by itself." : "It no longer updates by itself (deficitsnatcher update, or Update on the site).");
    }

    /// <summary>Passes a command to the running helper.</summary>
    static int SendCommand(string cmd, string done)
    {
        if (!IsRunning())
            return Fail("It isn't running: deficitsnatcher start");
        File.WriteAllText(CommandFile, cmd);
        return Print(done);
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
              deficitsnatcher update          install a new version now, if there is one
              deficitsnatcher autoupdate off  don't update by itself (on: it does, the default)
              deficitsnatcher version         which version this is
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

        // Updates: a new version, when there is one, replaces this one and it stops (see Updater).
        var exitCode = 0;
        var updating = new SemaphoreSlim(1, 1);
        Func<Task> tryUpdate = async () =>
        {
            if (!await updating.WaitAsync(0))
                return;
            try
            {
                if (await Updater.InstallIfNewerAsync() is { } code)
                {
                    exitCode = code;
                    stop.Cancel();
                }
            }
            finally
            {
                updating.Release();
            }
        };
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            while (!stop.IsCancellationRequested)
            {
                if (LoadConfig().AutoUpdate)
                    await tryUpdate();
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });

        // Commands from the terminal (stop, window show/hide), through a file.
        IBrowserContext? browser = null;
        // The site needs you (sign in, prove you're human): the page opens in a window of its own, in front. Asked before
        // Chrome is up, it opens once Chrome is.
        var pendingUrls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Func<string, Task> show = async url =>
        {
            if (browser == null)
            {
                pendingUrls.Enqueue(url);
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
                else if (cmd == "update")
                    await tryUpdate();
                else if (browser != null && cmd is "show" or "hide")
                    await Chrome.SetWindowAsync(browser, cmd == "show" ? "normal" : "minimized");
            }
        });
        // The site leaves this helper alone while you're signing in (nothing opens behind your window).
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(5000);
                var busy = Chrome.OpenSignIns > 0;
                if (busy != BusyNow)
                {
                    BusyNow = busy;
                    await TellSiteAsync(new { signin = busy });
                }
            }
        });

        // Chrome, started again if it's closed (until told to stop).
        while (!stop.IsCancellationRequested)
        {
            try
            {
                c = LoadConfig();
                var (context, cdpUrl, name) = await Chrome.StartAsync(Log);
                browser = context;
                state = state with { Browser = name };
                WriteState(state);
                using var chromeClosed = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                context.Close += (_, _) => chromeClosed.Cancel();
                while (pendingUrls.TryDequeue(out var url))
                    await Chrome.OpenWindowAsync(context, url);
                await ConnectLoopAsync(c, cdpUrl, name, change => WriteState(state = change(state)), show, tryUpdate, chromeClosed.Token);
                browser = null;
                if (!stop.IsCancellationRequested)
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
                await Task.Delay(5000, stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        Log(exitCode == 0 ? "Stopped." : "Stopped, to start the new version.");
        WriteState(state with { Running = false, Connected = false });
        lockStream.Dispose();
        return exitCode;
    }

    /// <summary>Stays connected to the site (reconnecting when it drops), lending Chrome when the site asks.</summary>
    static async Task ConnectLoopAsync(Config c, string cdpUrl, string browserName, Action<Func<State, State>> update, Func<string, Task> show,
        Func<Task> tryUpdate, CancellationToken ct)
    {
        var wsBase = new Uri(c.Site!.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/'));
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var control = Socket(c.Key!);
                var query = $"?machine={Uri.EscapeDataString(Environment.MachineName)}&browser={Uri.EscapeDataString(browserName)}&can=show,update"
                            + $"&version={MyVersion}&signin={(BusyNow || Chrome.OpenSignIns > 0 ? 1 : 0)}";
                await control.ConnectAsync(new Uri(wsBase, "/api/runner/connect" + query), ct);
                Control = control;
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
                    // Update now (the site's button).
                    else if (msg.RootElement.TryGetProperty("update", out _))
                    {
                        Log("The site asked to update.");
                        _ = Task.Run(tryUpdate);
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
        public static async Task<(IBrowserContext Context, string CdpUrl, string Name)> StartAsync(Action<string> log)
        {
            Directory.CreateDirectory(ProfileDir);
            _playwright ??= await Playwright.CreateAsync();
            var port = FreePort();
            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                // Always with a window: a headless Chrome is the easiest thing for a store's bot check to spot.
                Headless = false,
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

            // Hides the one property that says "a program is driving this browser".
            await context.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");

            var start = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
            // Tabs Chrome brought back from last time: the site opens what it needs.
            foreach (var restored in context.Pages.Skip(1).ToList())
                try
                {
                    await restored.CloseAsync();
                }
                catch (PlaywrightException)
                {
                }
            await start.SetContentAsync("""
                <html><head><title>Deficit Snatcher PC helper</title></head>
                <body style="font-family:system-ui;background:#0a0c10;color:#eee;padding:40px;max-width:640px">
                <h1 style="color:#ff4d5e">Deficit Snatcher PC helper</h1>
                <p>This Chrome belongs to the helper; this window stays minimised and the bot's tabs open here.</p>
                <p>To sign in to a store, press <b>Log in</b> next to it on the site's Helpers tab: it opens in a window of its own.</p>
                <p>When a store asks you to prove you're human (<i>Press &amp; Hold</i>), that page opens in a window of its own too, and you
                get an alert.</p>
                <p style="color:#999">In a terminal: <code>deficitsnatcher window hide</code> to minimise it, <code>deficitsnatcher help</code>.</p>
                </body></html>
                """);
            // How the site finds this window, to open the bot's tabs in it (not in a sign-in window).
            await start.EvaluateAsync("() => { window.name = 'ds-home'; }");

            using var http = new HttpClient();
            var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version")).RootElement;
            var cdpUrl = version.GetProperty("webSocketDebuggerUrl").GetString()!;
            var full = $"{name} {version.GetProperty("Browser").GetString()?.Split('/').LastOrDefault()}";
            log($"Browser: {full}");

            // Shown the first time (to sign in to the stores), minimised after that.
            var marker = Path.Combine(Home, "started-before");
            if (File.Exists(marker))
                await SetWindowAsync(context, "minimized");
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
            return (context, cdpUrl, full);
        }

        /// <summary>The windows opened for the owner (sign in, prove you're human), one per store. While any is open the site
        /// gives this helper no work, so nothing opens behind it.</summary>
        static readonly List<(IPage Page, string Store)> SignIns = [];

        public static int OpenSignIns
        {
            get
            {
                lock (SignIns)
                {
                    SignIns.RemoveAll(p => p.Page.IsClosed);
                    return SignIns.Count;
                }
            }
        }

        static string Store(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host.StartsWith("www.") ? u.Host[4..] : u.Host) : "";

        /// <summary>
        /// The page in a window of its own, in front: one per store (asked again, that window comes back to the front). The
        /// bot's tabs stay in the helper's own minimised window.
        /// </summary>
        public static async Task OpenWindowAsync(IBrowserContext context, string url)
        {
            try
            {
                IPage? page;
                lock (SignIns)
                    page = SignIns.FirstOrDefault(p => !p.Page.IsClosed && p.Store == Store(url)).Page;
                if (page == null)
                {
                    var before = context.Pages.ToHashSet();
                    var first = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
                    var cdp = await context.NewCDPSessionAsync(first);
                    await cdp.SendAsync("Target.createTarget", new Dictionary<string, object> { ["url"] = url, ["newWindow"] = true });
                    await cdp.DetachAsync();
                    for (var i = 0; i < 50 && page == null; i++)
                    {
                        await Task.Delay(100);
                        page = context.Pages.FirstOrDefault(p => !before.Contains(p) && p.Url.StartsWith(url[..Math.Min(url.Length, 20)], StringComparison.OrdinalIgnoreCase))
                               ?? (i > 20 ? context.Pages.FirstOrDefault(p => !before.Contains(p)) : null);
                    }
                    if (page == null)
                        throw new InvalidOperationException("Chrome didn't open the window.");
                    lock (SignIns)
                        SignIns.Add((page, Store(url)));
                    Log($"Opened {Store(url)} in a window for you.");
                    await TellSiteAsync(new { signin = true });
                }
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
                // Chrome is closing.
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

static partial class Helper
{
    /// <summary>Where new versions are (DEFICITSNATCHER_RELEASES overrides it, for testing).</summary>
    static string ReleaseBase => Environment.GetEnvironmentVariable("DEFICITSNATCHER_RELEASES")
        ?? "https://github.com/YehorSkudilov/DeficitSnatcher/releases/download/pc-helper";

    /// <summary>This build's version (CI sets it: 1.0.{build number}).</summary>
    static string MyVersion => (typeof(Helper).Assembly.GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    static string Rid => OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
        : "linux-x64";

    /// <summary>
    /// Finds and installs a newer helper from the site's download (the pc-helper release). Windows can't replace a running
    /// program, so the new copy is started to take over once this one has stopped; elsewhere the files are swapped in place
    /// and this one stops with an error code, which makes the service manager start the new version (or it's started
    /// directly when there's no service).
    /// </summary>
    static class Updater
    {
        /// <summary>The exit code to stop with, when a new version is in place; null: nothing to do.</summary>
        public static async Task<int?> InstallIfNewerAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DeficitSnatcher-PC-helper");
                var latestText = (await http.GetStringAsync($"{ReleaseBase}/version.txt")).Trim();
                if (!Version.TryParse(latestText, out var latest) || !Version.TryParse(MyVersion, out var mine))
                {
                    Log($"Update: couldn't read the latest version ({latestText}).");
                    return null;
                }
                if (latest <= mine)
                {
                    Log($"Update: {MyVersion} is the latest.");
                    return null;
                }
                Log($"Update: downloading {latest.ToString(3)} (this is {MyVersion}).");
                var root = Path.Combine(Home, "update");
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                var dir = Path.Combine(root, latest.ToString(3));
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(root, "helper.zip");
                await using (var file = File.Create(zip))
                await using (var body = await http.GetStreamAsync($"{ReleaseBase}/DeficitSnatcher-PC-helper-{Rid}.zip"))
                    await body.CopyToAsync(file);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
                File.Delete(zip);
                var newExe = Path.Combine(dir, ExeName);
                if (!File.Exists(newExe))
                {
                    Log("Update: the download doesn't have the helper in it.");
                    return null;
                }

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(newExe, $"finish-update {Environment.ProcessId}")
                        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Home });
                    Log($"Update: {latest.ToString(3)} takes over once this stops.");
                    return 0;
                }

                // Unix: a running program's files can be replaced (removed, then written anew).
                MakeExecutable(dir);
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(AppDir, Path.GetRelativePath(dir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target))
                        File.Delete(target);
                    File.Copy(f, target);
                    File.SetUnixFileMode(target, File.GetUnixFileMode(f));
                }
                Directory.Delete(root, recursive: true);
                Log($"Update: {latest.ToString(3)} is installed; restarting.");
                if (Service.StartsItself && Service.IsEnabled())
                    return 75; // The service manager starts it again (it restarts on an error exit).
                Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"sleep 3; exec \"{InstalledExe}\" run"])
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Home });
                return 0;
            }
            catch (Exception e)
            {
                Log("Update failed: " + e.Message.Split('\n')[0]);
                return null;
            }
        }

        static void MakeExecutable(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (name == ExeName || name == "node" || name.EndsWith(".sh", StringComparison.Ordinal))
                    File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
    }

    /// <summary>Windows: the new version, once the old one has stopped, copies itself into place and starts.</summary>
    static int FinishUpdate(int oldPid)
    {
        try
        {
            if (oldPid > 0)
                try
                {
                    using var old = Process.GetProcessById(oldPid);
                    if (!old.WaitForExit(TimeSpan.FromMinutes(2)))
                        old.Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            // Its browser driver may still be closing: try for a minute.
            for (var attempt = 1; ; attempt++)
                try
                {
                    CopyDirectory(here, AppDir);
                    break;
                }
                catch (IOException) when (attempt < 30)
                {
                    Thread.Sleep(2000);
                }
            Log($"Update: {MyVersion} is installed.");
            var c = LoadConfig();
            if (c.Autostart)
                Service.Enable();
            StartBackground();
            return 0;
        }
        catch (Exception e)
        {
            Log("Update couldn't finish: " + e.Message);
            // Don't leave it stopped: start what's installed.
            try
            {
                StartBackground();
            }
            catch (Exception)
            {
            }
            return 1;
        }
    }
}

static partial class Helper
{
    /// <summary>The connection to the site (for telling it things: "signing in").</summary>
    static ClientWebSocket? Control;
    /// <summary>What the site was last told: leave this helper alone (signing in, or Chrome has a window for it).</summary>
    static volatile bool BusyNow;
    static readonly SemaphoreSlim ControlSend = new(1, 1);

    static async Task TellSiteAsync(object message)
    {
        if (Control is not { State: WebSocketState.Open } ws)
            return;
        await ControlSend.WaitAsync();
        try
        {
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception)
        {
        }
        finally
        {
            ControlSend.Release();
        }
    }
}
