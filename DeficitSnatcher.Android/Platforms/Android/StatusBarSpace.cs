using AndroidX.Core.View;

namespace DeficitSnatcher.Phone;

/// <summary>
/// Keeps a page's content out from under the status bar (Android 15+ draws apps edge to edge, and the pages here sit in tabs
/// whose own inset handling doesn't reach them): the page measures where it really is on the screen and pads its top by
/// however much the status bar (or a camera cutout) covers it. Nothing covered: no padding, so it can't double up.
/// </summary>
public static class StatusBarSpace
{
    public static void Keep(ContentPage page)
    {
        // MAUI doesn't pad these pages itself: this is the only thing that does.
        page.SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.None);
        void Fix()
        {
            if (page.Handler?.PlatformView is not global::Android.Views.View view || !view.IsAttachedToWindow)
                return;
            var insets = ViewCompat.GetRootWindowInsets(view);
            if (insets == null)
                return;
            var top = insets.GetInsets(WindowInsetsCompat.Type.StatusBars() | WindowInsetsCompat.Type.DisplayCutout()).Top;
            var where = new int[2];
            view.GetLocationOnScreen(where);
            var density = view.Resources?.DisplayMetrics?.Density ?? 1;
            var needed = Math.Max(0, top - where[1]) / density;
            if (Math.Abs(page.Padding.Top - needed) > 0.5)
                page.Padding = new Thickness(page.Padding.Left, needed, page.Padding.Right, page.Padding.Bottom);
        }

        page.SizeChanged += (_, _) => page.Dispatcher.Dispatch(Fix);
        page.Appearing += (_, _) => page.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), Fix);
        page.HandlerChanged += (_, _) => page.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), Fix);
    }
}
