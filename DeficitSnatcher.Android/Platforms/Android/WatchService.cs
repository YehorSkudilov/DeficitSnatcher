using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace DeficitSnatcher.Phone;

/// <summary>
/// Stays connected to the site (a foreground service with its own small notification, declared in AndroidManifest.xml) and
/// rings the alarm the moment a match comes in. The site holds each request open until there's an alarm (or about 40
/// seconds), so an alarm arrives within a second or two. Started when the app opens, at boot and after an update.
/// </summary>
[Register("ca.deficitsnatcher.app.WatchService")]
public sealed class WatchService : Service
{
    private const int NotificationId = 1;
    private const string Channel = "watch";

    private CancellationTokenSource? _stop;
    private PowerManager.WakeLock? _wakeLock;

    /// <summary>What the service is doing, for the app's screen.</summary>
    public static string Status { get; private set; } = "Not running";
    public static DateTimeOffset? LastContact { get; private set; }
    public static bool Running { get; private set; }

    public static void Start(Context context)
    {
        if (!Server.SignedIn)
            return;
        var intent = new Intent(context, typeof(WatchService));
        context.StartForegroundService(intent);
    }

    public static void StopIt(Context context) => context.StopService(new Intent(context, typeof(WatchService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel(this);
        var notification = Notice(this, "Starting…");
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
        if (_stop == null)
        {
            // The CPU stays awake for the connection (the screen doesn't): an alarm mustn't wait for the phone to wake up.
            _wakeLock ??= ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "deficitsnatcher:watch");
            _wakeLock!.Acquire();
            _stop = new CancellationTokenSource();
            Running = true;
            _ = Task.Run(() => LoopAsync(_stop.Token));
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _stop?.Cancel();
        _stop = null;
        Running = false;
        Status = "Not running";
        if (_wakeLock is { IsHeld: true })
            _wakeLock.Release();
        base.OnDestroy();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var failures = 0;
        var lastOk = DateTimeOffset.UtcNow;
        var nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(1);
        while (!ct.IsCancellationRequested)
        {
            if (!Server.SignedIn)
            {
                StopSelf();
                return;
            }
            try
            {
                var reply = await Server.WaitForAlarmsAsync(Server.LastAlarmId, 40, ct);
                Server.LastAlarmId = reply.Latest;
                LastContact = DateTimeOffset.Now;
                lastOk = DateTimeOffset.UtcNow;
                failures = 0;
                // The watchdog: the site is up but has stopped checking its pages.
                Watchdog.Report(this, reply.Watched > 0 && reply.LastCheck is { } checkedAt && DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromSeconds(reply.StaleAfter)
                    ? $"The site hasn't checked any page since {checkedAt.ToLocalTime():HH:mm}: it isn't watching for stock. Check it (Deploy, or the server)."
                    : null);
                // The newest match rings; an old one (the phone was offline for a while) doesn't: it's too late for it.
                if (reply.Alarms.LastOrDefault(a => DateTimeOffset.UtcNow - a.At < TimeSpan.FromMinutes(15)) is { } alarm)
                    Alarm.Ring(this, alarm);
                Update($"Watching · connected {LastContact:HH:mm}");
            }
            catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                failures++;
                Update($"Can't reach the site: {Short(e.Message)} (trying again)");
                // The watchdog: no alarm can come through while the site can't be reached.
                if (DateTimeOffset.UtcNow - lastOk > TimeSpan.FromMinutes(10))
                    Watchdog.Report(this, $"Can't reach your site since {lastOk.ToLocalTime():HH:mm}: no alarm can come through. ({Short(e.Message)})");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 3 * failures)), ct);
                }
                catch (System.OperationCanceledException)
                {
                    return;
                }
            }
            if (DateTimeOffset.UtcNow >= nextUpdateCheck)
            {
                nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(6);
                _ = Task.Run(() => Updater.CheckAsync(this, interactive: false));
            }
        }
    }

    private void Update(string status)
    {
        Status = status;
        ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, Notice(this, status));
    }

    private static string Short(string s) => s.Length > 80 ? s[..80] + "…" : s;

    private static void CreateChannel(Context context)
    {
        var channel = new NotificationChannel(Channel, "Watching for stock", NotificationImportance.Low)
        {
            Description = "The connection to your site (keeps the alarm ready).",
        };
        ((NotificationManager)context.GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
    }

    private static Notification Notice(Context context, string text)
    {
        var open = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)), PendingIntentFlags.Immutable);
        return new Notification.Builder(context, Channel)
            .SetContentTitle("Deficit Snatcher")!
            .SetContentText(text)!
            .SetSmallIcon(global::Android.Resource.Drawable.IcPopupSync)!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .Build()!;
    }
}

/// <summary>Starts watching after the phone restarts and after the app updates itself.</summary>
[BroadcastReceiver(Exported = true, Enabled = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class StartReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context != null)
            WatchService.Start(context);
    }
}

/// <summary>
/// Tells you, with an ordinary (loud) notification, when the alarm can't work: the site can't be reached, or it has stopped
/// checking pages. Cleared when it's fine again.
/// </summary>
public static class Watchdog
{
    private const int NotificationId = 4;
    private static string? _shown;

    public static void Report(Context context, string? problem)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (problem == null)
        {
            if (_shown != null)
                manager.Cancel(NotificationId);
            _shown = null;
            return;
        }
        // Once per problem (not every 40 seconds).
        if (_shown != null && _shown[..Math.Min(30, _shown.Length)] == problem[..Math.Min(30, problem.Length)])
            return;
        _shown = problem;
        manager.CreateNotificationChannel(new NotificationChannel("problems", "Problems", NotificationImportance.High)
        {
            Description = "When the alarm can't work: the site can't be reached, or it stopped checking.",
        });
        var open = PendingIntent.GetActivity(context, 4, new Intent(context, typeof(MainActivity)), PendingIntentFlags.Immutable);
        manager.Notify(NotificationId, new Notification.Builder(context, "problems")
            .SetContentTitle("Deficit Snatcher isn't watching")!
            .SetContentText(problem)!
            .SetStyle(new Notification.BigTextStyle().BigText(problem))!
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysWarning)!
            .SetContentIntent(open)!
            .Build()!);
    }
}
