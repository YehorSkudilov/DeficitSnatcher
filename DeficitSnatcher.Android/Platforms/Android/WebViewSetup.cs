namespace DeficitSnatcher.Phone;

/// <summary>The Site tab's web view keeps the site's sign-in (localStorage) and runs its script.</summary>
public static class WebViewSetup
{
    public static void Apply() =>
        Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("DeficitSnatcherSite", (handler, _) =>
        {
            var settings = handler.PlatformView.Settings;
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;
        });
}
