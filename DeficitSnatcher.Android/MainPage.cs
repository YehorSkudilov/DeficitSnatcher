using Android.Content;
using Android.OS;

namespace DeficitSnatcher.Phone;

/// <summary>Sign in (the site's address, username, password); then the watch's status, test alarm, updates, sign out.</summary>
public sealed class MainPage : ContentPage
{
    private readonly Label _status = Small("", Theme.Text);
    private readonly Label _note = Small("", Theme.Muted);
    private readonly VerticalStackLayout _recent = new() { Spacing = 6 };
    private IDispatcherTimer? _timer;

    private static Context Ctx => global::Android.App.Application.Context;

    public MainPage()
    {
        Title = "Deficit Snatcher";
        BackgroundColor = Theme.Background;
        Show();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // The alarm and the watch's notification need this (Android 13+).
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        if (Server.SignedIn)
        {
            WatchService.Start(Ctx);
            await LoadRecentAsync();
        }
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        base.OnDisappearing();
    }

    private void Show() => Content = new ScrollView { Content = Server.SignedIn ? SignedInView() : SignInView() };

    private View SignInView()
    {
        var url = Entry("Site address (e.g. deficitsnatcher.example.com)", Server.Url, Keyboard.Url);
        var user = Entry("Username", Server.Username, Keyboard.Text);
        var password = Entry("Password", "", Keyboard.Text);
        password.IsPassword = true;
        var error = Small("", Theme.Bad);
        var button = Button("Sign in", Theme.Accent);
        button.Clicked += async (_, _) =>
        {
            button.IsEnabled = false;
            error.Text = "";
            try
            {
                await Server.SignInAsync(url.Text ?? "", user.Text ?? "", password.Text ?? "");
                WatchService.Start(Ctx);
                Show();
                await LoadRecentAsync();
            }
            catch (Exception e)
            {
                error.Text = e is HttpRequestException ? $"Couldn't reach the site: {e.Message}" : e.Message;
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return Page(
            Heading("Sign in"),
            Small("The same address, username and password as the website. It stays signed in.", Theme.Muted),
            url, user, password, button, error);
    }

    private View SignedInView()
    {
        var test = Button("Test alarm", Theme.Accent);
        test.Clicked += async (_, _) => await Try(async () =>
        {
            await Server.TestAlarmAsync();
            _note.Text = "Rung: it should go off within a few seconds.";
        });

        var battery = Button("Allow running in the background", Theme.Surface);
        battery.Clicked += (_, _) =>
        {
            var power = (PowerManager)Ctx.GetSystemService(Context.PowerService)!;
            if (power.IsIgnoringBatteryOptimizations(Ctx.PackageName))
            {
                _note.Text = "Already allowed: Android won't put it to sleep.";
                return;
            }
            var ask = new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                global::Android.Net.Uri.Parse("package:" + Ctx.PackageName)).AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(ask);
        };

        var update = Button("Check for update", Theme.Surface);
        update.Clicked += async (_, _) =>
        {
            update.IsEnabled = false;
            _note.Text = "Checking…";
            _note.Text = await Updater.CheckAsync(Ctx, interactive: true);
            update.IsEnabled = true;
        };

        var signOut = Button("Sign out", Theme.Surface);
        signOut.Clicked += (_, _) =>
        {
            Server.SignOut();
            WatchService.StopIt(Ctx);
            Show();
        };

        RefreshStatus();
        return Page(
            Heading("Watching for stock"),
            _status,
            Small($"{Server.Url} · {Server.Username} · build {Updater.CurrentVersion(Ctx)}", Theme.Muted),
            Small("When something is in stock in your range, it rings loudly (even on silent); turn it off and the store's page opens.",
                Theme.Muted),
            test, battery, update, _note,
            Heading("Recent alarms"), _recent,
            signOut);
    }

    private void RefreshStatus()
    {
        if (!Server.SignedIn)
            return;
        var power = (PowerManager)Ctx.GetSystemService(Context.PowerService)!;
        var asleep = power.IsIgnoringBatteryOptimizations(Ctx.PackageName) ? "" : "\nAndroid may put it to sleep: tap \"Allow running in the background\".";
        _status.Text = (WatchService.Running ? WatchService.Status : "Not running") + asleep;
        _status.TextColor = WatchService.Running && WatchService.Status.StartsWith("Watching") ? Theme.Good : Theme.Bad;
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            var list = await Server.RecentAsync();
            _recent.Children.Clear();
            if (list.Count == 0)
                _recent.Children.Add(Small("None yet.", Theme.Muted));
            foreach (var a in list.Take(10))
            {
                var row = Small($"{a.At.ToLocalTime():MMM d, HH:mm} · {a.Product} at {a.Store}{(a.Price is { } p ? $" · {p:0.00}" : "")}{(a.Test ? " (test)" : "")}",
                    Theme.Text);
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await Launcher.Default.OpenAsync(a.Url);
                row.GestureRecognizers.Add(tap);
                _recent.Children.Add(row);
            }
        }
        catch (Exception e)
        {
            _note.Text = "Couldn't load the recent alarms: " + e.Message;
        }
    }

    private async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            _note.Text = e.Message;
        }
    }

    // ---- Small view helpers ----

    private static VerticalStackLayout Page(params View[] views)
    {
        var page = new VerticalStackLayout { Padding = new Thickness(20, 24), Spacing = 14 };
        foreach (var v in views)
            page.Children.Add(v);
        return page;
    }

    private static Label Heading(string text) => new() { Text = text, FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };

    private static Label Small(string text, Color color) => new() { Text = text, FontSize = 15, TextColor = color };

    private static Entry Entry(string placeholder, string text, Keyboard keyboard) => new()
    {
        Placeholder = placeholder, Text = text, Keyboard = keyboard, TextColor = Theme.Text, PlaceholderColor = Theme.Muted,
        BackgroundColor = Theme.Surface, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
    };

    private static Button Button(string text, Color color) => new()
    {
        Text = text, BackgroundColor = color, TextColor = Theme.Text, CornerRadius = 10, FontSize = 16, Padding = new Thickness(12, 14),
    };
}
