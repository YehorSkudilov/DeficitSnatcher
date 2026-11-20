using Android.Content;
using Android.OS;

namespace DeficitSnatcher.Phone;

/// <summary>Sign in (the site's address, username, password); then the watch's status, test alarm, updates, sign out.</summary>
public sealed class MainPage : ContentPage
{
    private readonly Label _status = Small("", Theme.Text);
    private readonly Label _note = Small("", Theme.Muted);
    private readonly VerticalStackLayout _recent = new() { Spacing = 6 };
    private readonly VerticalStackLayout _ready = new() { Spacing = 8 };
    private string _readyKey = "";
    private IDispatcherTimer? _timer;

    private static Context Ctx => global::Android.App.Application.Context;

    public MainPage()
    {
        Title = "Deficit Snatcher";
        BackgroundColor = Theme.Background;
        StatusBarSpace.Keep(this);
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
                // What it needs to wake you: asked for right away (each opens Android's own screen for it).
                if (Problems().FirstOrDefault() is { } first)
                    first.Fix();
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
            Small("When something is in stock in your range, it rings loudly (even on silent). Stop & Open opens a cart for Shopify sites with a known variant; otherwise it opens the product page.",
                Theme.Muted),
            Heading("Ready to wake you"), _ready,
            test, update, _note,
            Heading("Recent alarms"), _recent,
            signOut);
    }

    private void RefreshStatus()
    {
        if (!Server.SignedIn)
            return;
        _status.Text = WatchService.Running ? WatchService.Status : "Not running";
        _status.TextColor = WatchService.Running && WatchService.Status.StartsWith("Watching") ? Theme.Good : Theme.Bad;
        if (!WatchService.Running)
            WatchService.Start(Ctx);
        ShowReadiness();
    }

    // ---- What the alarm needs to wake you ----

    private sealed record Need(string Name, bool Ok, string Why, Action Fix);

    private static void OpenSettings(string action, bool withPackage = true)
    {
        var intent = withPackage ? new Intent(action, global::Android.Net.Uri.Parse("package:" + Ctx.PackageName)) : new Intent(action);
        // The notification settings screen wants the app this way instead.
        intent.PutExtra(global::Android.Provider.Settings.ExtraAppPackage, Ctx.PackageName);
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            Ctx.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            // This phone hasn't got that screen: the app's own settings instead.
            Ctx.StartActivity(new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.Parse("package:" + Ctx.PackageName)).AddFlags(ActivityFlags.NewTask));
        }
    }

    private static List<Need> Needs()
    {
        var notifications = (global::Android.App.NotificationManager)Ctx.GetSystemService(Context.NotificationService)!;
        var power = (PowerManager)Ctx.GetSystemService(Context.PowerService)!;
        var list = new List<Need>
        {
            new("Notifications allowed", notifications.AreNotificationsEnabled(), "Without them the alarm can't show.",
                () => OpenSettings(global::Android.Provider.Settings.ActionAppNotificationSettings, withPackage: false)),
            new("No battery restrictions", power.IsIgnoringBatteryOptimizations(Ctx.PackageName),
                "Otherwise Android puts it to sleep when the screen is off, and nothing comes through.",
                () => OpenSettings(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations)),
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            list.Add(new("Full-screen alarm allowed", notifications.CanUseFullScreenIntent(),
                "So the alarm turns the screen on over the lock screen, like an alarm clock.",
                () => OpenSettings(global::Android.Provider.Settings.ActionManageAppUseFullScreenIntent)));
        return list;
    }

    private static List<Need> Problems() => Needs().Where(n => !n.Ok).ToList();

    private void ShowReadiness()
    {
        var needs = Needs();
        var key = string.Join(",", needs.Select(n => n.Ok));
        if (key == _readyKey)
            return;
        _readyKey = key;
        _ready.Children.Clear();
        foreach (var n in needs)
        {
            if (n.Ok)
            {
                _ready.Children.Add(Small("✓ " + n.Name, Theme.Good));
                continue;
            }
            _ready.Children.Add(Small("✗ " + n.Name + ": " + n.Why, Theme.Bad));
            var fix = Button("Fix: " + n.Name, Theme.Accent);
            fix.Clicked += (_, _) => n.Fix();
            _ready.Children.Add(fix);
        }
        _ready.Children.Add(Small("It rings on silent too (the alarm volume, set to full). Do Not Disturb lets alarms through unless "
            + "you've turned that off. A phone that's switched off can't ring.", Theme.Muted));
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
