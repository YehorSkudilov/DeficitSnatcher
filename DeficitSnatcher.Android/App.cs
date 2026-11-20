namespace DeficitSnatcher.Phone;

public sealed class App : Application
{
    public App() => UserAppTheme = AppTheme.Dark;

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(new MainPage()) { BarBackgroundColor = Theme.Background, BarTextColor = Theme.Text }) { Title = "Deficit Snatcher" };
}

/// <summary>The site's colors.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb("#0B0D12");
    public static readonly Color Surface = Color.FromArgb("#151922");
    public static readonly Color Text = Color.FromArgb("#F2F4F8");
    public static readonly Color Muted = Color.FromArgb("#9AA3B5");
    public static readonly Color Accent = Color.FromArgb("#3F7DFF");
    public static readonly Color Good = Color.FromArgb("#2ED47A");
    public static readonly Color Bad = Color.FromArgb("#FF4D5E");
}
