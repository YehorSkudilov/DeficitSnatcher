using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace DeficitSnatcher.Phone;

/// <summary>
/// The alarm: the phone's alarm sound at full alarm volume, looping, with vibration, and a full-screen page (over the lock
/// screen) until it's turned off. Turning it off opens the store's page. It stops by itself after 30 minutes.
/// </summary>
public static class Alarm
{
    private const string Channel = "alarm";
    private const int NotificationId = 2;
    private static MediaPlayer? _player;
    private static Vibrator? _vibrator;
    private static CancellationTokenSource? _timeout;

    /// <summary>What's ringing now (for the alarm page).</summary>
    public static AlarmDto? Current { get; private set; }

    public static void Ring(Context context, AlarmDto alarm)
    {
        Stop(context);
        Current = alarm;
        try
        {
            var audio = (AudioManager)context.GetSystemService(Context.AudioService)!;
            audio.SetStreamVolume(global::Android.Media.Stream.Alarm, audio.GetStreamMaxVolume(global::Android.Media.Stream.Alarm), 0);
            var sound = RingtoneManager.GetDefaultUri(RingtoneType.Alarm) ?? RingtoneManager.GetDefaultUri(RingtoneType.Ringtone);
            var player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Alarm)!.SetContentType(AudioContentType.Sonification)!.Build()!);
            player.SetDataSource(context, sound!);
            player.Looping = true;
            player.Prepare();
            player.Start();
            _player = player;
        }
        catch (Exception)
        {
            // No alarm sound on this phone: the vibration and the page still go.
        }
        try
        {
            _vibrator = (Vibrator?)context.GetSystemService(Context.VibratorService);
            _vibrator?.Vibrate(VibrationEffect.CreateWaveform([0, 800, 400], 0));
        }
        catch (Exception)
        {
        }

        var channel = new NotificationChannel(Channel, "In stock alarms", NotificationImportance.High)
        {
            Description = "Rings when something is in stock in your range.",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        channel.SetSound(null, null);
        channel.SetBypassDnd(true);
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(channel);
        var page = new Intent(context, typeof(AlarmActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.NoUserAction);
        var show = PendingIntent.GetActivity(context, 1, page, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(context, Channel)
            .SetContentTitle($"IN STOCK: {alarm.Product}")!
            .SetContentText($"{alarm.Store}{(alarm.Price is { } p ? $" · {p:0.00}" : "")}: tap to buy")!
            .SetSmallIcon(Resource.Drawable.tab_alarm)!
            .SetCategory(Notification.CategoryAlarm)!
            .SetFullScreenIntent(show, true)!
            .SetContentIntent(show)!
            .SetOngoing(true)!
            .Build()!;
        manager.Notify(NotificationId, notification);

        _timeout = new CancellationTokenSource();
        var token = _timeout.Token;
        _ = Task.Delay(TimeSpan.FromMinutes(30), token).ContinueWith(_ => Stop(context), token, TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    public static void Stop(Context context)
    {
        _timeout?.Cancel();
        _timeout = null;
        try
        {
            _player?.Stop();
            _player?.Release();
        }
        catch (Exception)
        {
        }
        _player = null;
        _vibrator?.Cancel();
        _vibrator = null;
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Cancel(NotificationId);
    }
}

/// <summary>The alarm's page: over the lock screen, the screen on. "Stop and open" silences it and opens the store's page.</summary>
[Activity(Theme = "@android:style/Theme.DeviceDefault.NoActionBar", ExcludeFromRecents = true, LaunchMode = LaunchMode.SingleInstance,
    ShowWhenLocked = true, TurnScreenOn = true)]
public sealed class AlarmActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        var alarm = Alarm.Current;

        var layout = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        layout.SetGravity(GravityFlags.Center);
        layout.SetPadding(48, 48, 48, 48);
        layout.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#0B0D12"));

        TextView Text(string s, float size, string color, bool bold = false)
        {
            var t = new TextView(this) { Text = s, Gravity = GravityFlags.Center };
            t.SetTextSize(global::Android.Util.ComplexUnitType.Sp, size);
            t.SetTextColor(global::Android.Graphics.Color.ParseColor(color));
            if (bold)
                t.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
            t.SetPadding(0, 12, 0, 12);
            layout.AddView(t);
            return t;
        }

        Text(alarm?.Test == true ? "TEST ALARM" : "IN STOCK", 40, "#2ED47A", bold: true);
        Text(alarm?.Product ?? "", 26, "#F2F4F8", bold: true);
        Text($"{alarm?.Store}{(alarm?.Price is { } p ? $" · {p:0.00}" : "")}", 22, "#9AA3B5");

        var open = new global::Android.Widget.Button(this) { Text = "STOP & OPEN CART / STORE" };
        open.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 22);
        open.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#2ED47A"));
        open.SetTextColor(global::Android.Graphics.Color.ParseColor("#0B0D12"));
        open.SetPadding(24, 48, 24, 48);
        open.Click += async (_, _) => await StopAndOpenAsync(alarm);
        var openParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = 64 };
        layout.AddView(open, openParams);

        var stop = new global::Android.Widget.Button(this) { Text = "Stop" };
        stop.Click += (_, _) => StopAndOpen(null);
        var stopParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = 24 };
        layout.AddView(stop, stopParams);

        SetContentView(layout);
    }

    private async Task StopAndOpenAsync(AlarmDto? alarm)
    {
        Alarm.Stop(this);
        var url = alarm?.Url;
        if (alarm is { Test: false, SiteId: { } siteId })
        {
            try
            {
                url = await Server.CartUrlAsync(siteId, CancellationToken.None) ?? url;
            }
            catch
            {
                // Keep the product-page fallback available if the app temporarily can't reach its server.
            }
        }
        if (!string.IsNullOrEmpty(url))
        {
            var view = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)).AddFlags(ActivityFlags.NewTask);
            // Locked: unlock first (the phone asks for your PIN/fingerprint), then the store opens.
            var keyguard = (KeyguardManager?)GetSystemService(KeyguardService);
            if (keyguard is { IsKeyguardLocked: true })
            {
                // This page stays until it's unlocked (or that's cancelled), so the store can open from it.
                keyguard.RequestDismissKeyguard(this, new OpenAfterUnlock(this, view));
                return;
            }
            StartActivity(view);
        }
        Finish();
    }

    private sealed class OpenAfterUnlock(Activity activity, Intent view) : KeyguardManager.KeyguardDismissCallback
    {
        public override void OnDismissSucceeded()
        {
            activity.StartActivity(view);
            activity.Finish();
        }

        public override void OnDismissCancelled() => activity.Finish();

        public override void OnDismissError() => activity.Finish();
    }
}
