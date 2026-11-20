namespace DeficitSnatcher.Phone;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        WebViewSetup.Apply();
        return MauiApp.CreateBuilder().UseMauiApp<App>().Build();
    }
}
