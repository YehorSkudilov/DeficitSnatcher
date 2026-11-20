using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace DeficitSnatcher.Phone;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Light icons on the dark bars, and the strips behind the status bar and the gesture line the same color as the app's
        // bars (the site's menu, the tabs), so there's no odd band above or below them.
        if (Window != null)
        {
            Window.DecorView.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#151922"));
            var bars = WindowCompat.GetInsetsController(Window, Window.DecorView);
            bars.AppearanceLightStatusBars = false;
            bars.AppearanceLightNavigationBars = false;
        }
    }
}
