using Android.App;
using Android.Content;
using Android.Content.PM;

namespace DeficitSnatcher.Phone;

/// <summary>
/// Updates the app from the "android" release on GitHub: version.txt there is the newest build's number; when it's higher
/// than this one, the APK is downloaded and installed over this one (settings kept). The first update after installing from
/// the browser asks you to confirm (and to allow installing from this app); after that Android lets it update by itself.
/// </summary>
public static class Updater
{
    private const string Release = "https://github.com/YehorSkudilov/DeficitSnatcher/releases/download/android";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static int _busy;

    public static long CurrentVersion(Context context)
    {
        var info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
        return OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
    }

    /// <summary>Checks and installs a newer version; what happened, in words.</summary>
    /// <param name="interactive">From the button: may ask to allow installing from this app.</param>
    public static async Task<string> CheckAsync(Context context, bool interactive)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return "Already checking.";
        try
        {
            Http.DefaultRequestHeaders.UserAgent.TryParseAdd("DeficitSnatcher-Android");
            var latestText = (await Http.GetStringAsync($"{Release}/version.txt")).Trim();
            if (!long.TryParse(latestText, out var latest))
                return "Couldn't read the latest version.";
            var mine = CurrentVersion(context);
            if (latest <= mine)
                return $"Up to date (build {mine}).";
            if (!context.PackageManager!.CanRequestPackageInstalls())
            {
                if (interactive)
                {
                    var allow = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                        global::Android.Net.Uri.Parse("package:" + context.PackageName)).AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(allow);
                }
                return $"Build {latest} is out: allow \"Install unknown apps\" for Deficit Snatcher, then check again.";
            }

            var file = Path.Combine(context.CacheDir!.AbsolutePath, "update.apk");
            await using (var download = await Http.GetStreamAsync($"{Release}/DeficitSnatcher.apk"))
            await using (var target = File.Create(file))
                await download.CopyToAsync(target);

            var installer = context.PackageManager!.PackageInstaller;
            var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
            // Android 12+: no confirmation when this app installed the previous version itself.
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                parameters.SetRequireUserAction(2 /* USER_ACTION_NOT_REQUIRED */);
            var id = installer.CreateSession(parameters);
            using var session = installer.OpenSession(id);
            await using (var input = File.OpenRead(file))
            await using (var output = session.OpenWrite("app.apk", 0, input.Length))
            {
                await input.CopyToAsync(output);
                session.Fsync(output);
            }
            // Mutable: the installer adds its status to it (Android 12+ needs that said; before, it's the default).
            var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
            var done = PendingIntent.GetBroadcast(context, 0, new Intent(context, typeof(InstallReceiver)), flags);
            session.Commit(done!.IntentSender!);
            return $"Installing build {latest}… (the app restarts by itself)";
        }
        catch (Exception e)
        {
            return "Couldn't update: " + e.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}

/// <summary>What the installer says: when it needs you to confirm, a notification (tap to install).</summary>
[BroadcastReceiver(Exported = false, Enabled = true)]
public sealed class InstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null)
            return;
        var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, -1);
        if (status != (int)PackageInstallStatus.PendingUserAction)
            return;
        var confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? (Intent?)intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent)))
            : (Intent?)intent.GetParcelableExtra(Intent.ExtraIntent);
        if (confirm == null)
            return;
        confirm.AddFlags(ActivityFlags.NewTask);
        var channel = new NotificationChannel("update", "Updates", NotificationImportance.High);
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(channel);
        var tap = PendingIntent.GetActivity(context, 3, confirm, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        manager.Notify(3, new Notification.Builder(context, "update")
            .SetContentTitle("Deficit Snatcher update ready")!
            .SetContentText("Tap to install it.")!
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)!
            .SetContentIntent(tap)!
            .SetAutoCancel(true)!
            .Build()!);
        // In front (the Check for update button): straight to the confirmation.
        try
        {
            context.StartActivity(confirm);
        }
        catch (Exception)
        {
        }
    }
}
