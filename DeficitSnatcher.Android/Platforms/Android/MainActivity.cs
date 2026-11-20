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
        // Android 15+ draws every app edge to edge (under the status bar and the gesture bar): the whole app is padded by
        // exactly those bars (and a camera cutout) here, so nothing sits under the clock, whatever the page does.
        var content = FindViewById(global::Android.Resource.Id.Content);
        if (content != null)
        {
            content.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#0B0D12"));
            ViewCompat.SetOnApplyWindowInsetsListener(content, new BarsPadding());
        }
        // Light icons on the dark bars.
        if (Window != null)
        {
            var bars = WindowCompat.GetInsetsController(Window, Window.DecorView);
            bars.AppearanceLightStatusBars = false;
            bars.AppearanceLightNavigationBars = false;
        }
    }

    private sealed class BarsPadding : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(global::Android.Views.View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            // The keyboard, when it's open, so it never covers what you're typing in.
            var keyboard = insets.GetInsets(WindowInsetsCompat.Type.Ime());
            v.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, keyboard.Bottom));
            // Used up here: the pages inside don't pad for them again.
            return WindowInsetsCompat.Consumed;
        }
    }
}
