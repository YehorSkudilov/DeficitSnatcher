using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;

namespace DeficitSnatcher.Phone;

/// <summary>
/// The website itself, in the app, already signed in (the app puts its sign-in into the page): every control the site has.
/// Links to stores open in the phone's browser (where you're signed in to them); Back goes back in the site.
/// </summary>
public sealed class SitePage : ContentPage
{
    private readonly Microsoft.Maui.Controls.WebView _web = new() { BackgroundColor = Theme.Background };
    private readonly Label _message = new() { TextColor = Theme.Muted, FontSize = 15, Margin = new Thickness(20) };
    private string? _loadedFor;
    private bool _signedIn;

    public SitePage()
    {
        Title = "Site";
        BackgroundColor = Theme.Background;
        StatusBarSpace.Keep(this);
        _web.Navigating += OnNavigating;
        _web.Navigated += async (_, e) =>
        {
            if (e.Result != WebNavigationResult.Success || _signedIn)
                return;
            // The site keeps its sign-in in localStorage (ds.token): put the app's there once, then reload into it.
            _signedIn = true;
            try
            {
                var token = await Server.TokenAsync();
                await _web.EvaluateJavaScriptAsync($"localStorage.setItem('ds.token', '{token}'); location.reload();");
            }
            catch (Exception ex)
            {
                _message.Text = "Couldn't sign the site in: " + ex.Message;
            }
        };
        Content = _message;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!Server.SignedIn)
        {
            _message.Text = "Sign in on the Alarm tab first.";
            Content = _message;
            _loadedFor = null;
            return;
        }
        // Signed in (again, or to another site): load it.
        if (_loadedFor != Server.Url)
        {
            _loadedFor = Server.Url;
            _signedIn = false;
            Content = _web;
            _web.Source = Server.Url + "/#dashboard";
        }
    }

    /// <summary>Store pages (any other address than the site) open in the phone's browser.</summary>
    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var target) || !Uri.TryCreate(Server.Url, UriKind.Absolute, out var site))
            return;
        if (target.Scheme is "http" or "https" && !string.Equals(target.Host, site.Host, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            await Launcher.Default.OpenAsync(target);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (Content == _web && _web.CanGoBack)
        {
            _web.GoBack();
            return true;
        }
        return base.OnBackButtonPressed();
    }
}

/// <summary>The two tabs, at the bottom: the alarm (status, test, updates) and the site.</summary>
public sealed class MainTabs : Microsoft.Maui.Controls.TabbedPage
{
    public MainTabs()
    {
        BarBackgroundColor = Theme.Surface;
        SelectedTabColor = Theme.Accent;
        UnselectedTabColor = Theme.Muted;
        On<Microsoft.Maui.Controls.PlatformConfiguration.Android>().SetToolbarPlacement(ToolbarPlacement.Bottom);
        Children.Add(new MainPage { Title = "Alarm" });
        Children.Add(new SitePage());
    }
}
