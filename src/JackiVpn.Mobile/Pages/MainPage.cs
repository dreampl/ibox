using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.ViewModels;
using JackiVpn.Mobile.Views;

namespace JackiVpn.Mobile.Pages;

/// <summary>Access to the floating bar from the views (the power button mirrors the connection).</summary>
public static class MainNav
{
    public static FloatingNavBar? Current { get; set; }
}

/// <summary>
/// The app's single main screen: four tabs hosted in one page (so switching can use custom transitions), the
/// floating glass navigation bar with the power button, the progressive blur/fade above it, and the drawer host.
/// </summary>
public sealed class MainPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly Grid _host = new();
    private readonly FloatingNavBar _nav = new();
    private readonly BottomSheetHost _sheet = new();
    private readonly View?[] _views = new View?[4];
    private int _current = -1;
    private bool _switching;

    public MainPage(IServiceProvider services)
    {
        _services = services;
        Shell.SetNavBarIsVisible(this, false);
        Shell.SetTabBarIsVisible(this, false);
        Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["PageGradient"];
        FlowDirection = L.Flow;
        MainNav.Current = _nav;
        BottomSheetHost.Current = _sheet;
        BlurBackdrop.Source = _host;

        // Content gets blurrier and darker towards the bottom, under the floating bar.
        var floor = new Grid
        {
            VerticalOptions = LayoutOptions.End,
            HeightRequest = 170,
            InputTransparent = true,
            Children =
            {
                new BlurBackdrop { FadeTop = true, Radius = 14 },
                new BoxView { Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["BottomScrim"] },
            },
        };

        Content = new Grid { Children = { _host, floor, _nav, _sheet } };
        _nav.SelectedChanged += (_, i) => _ = ShowAsync(i);
        _nav.AddTapped += (_, _) => _ = AddAsync();
        if (_services.GetService(typeof(ServersViewModel)) is ServersViewModel servers)
        {
            servers.Navigated += (_, _) => MainThread.BeginInvokeOnMainThread(() => _nav.Select(0, raise: true));
        }

        L.LanguageChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Rebuild);
        Loaded += async (_, _) =>
        {
            if (_current < 0)
            {
                _ = ShowAsync(0);
            }

            var homeVm = Resolve<HomeViewModel>();
            _nav.SetPower(homeVm.PowerVisual, homeVm.AccentGradient, homeVm.AccentColor);

            await FirstRunChecksAsync();
            PrewarmTabs();
        };
    }

    private bool _checked;

    /// <summary>The introduction on first start; afterwards, a report from a crash last time (shared only if the user wants).</summary>
    private async Task FirstRunChecksAsync()
    {
        if (_checked)
        {
            return;
        }

        _checked = true;
        try
        {
            var app = Resolve<Services.MobileServices>();
            await app.InitializeAsync();
            if (!app.Settings.Current.Ui.OnboardingDone)
            {
                await Navigation.PushModalAsync(new OnboardingPage(app), animated: false);
                return;
            }

            var crash = MainApplication.CrashFile;
            if (File.Exists(crash))
            {
                var share = await DisplayAlertAsync(L.Get("Crash_Title"), L.Get("Crash_Text"), L.Get("Crash_Share"), L.Get("Crash_Dismiss"));
                var copy = Path.Combine(FileSystem.CacheDirectory, "jackivpn-crash.txt");
                File.Move(crash, copy, overwrite: true);
                if (share)
                {
                    await Share.Default.RequestAsync(new ShareFileRequest { Title = "IBOX crash report", File = new ShareFile(copy, "text/plain") });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Optional; never block the main screen.
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_current is 1 or 2)
        {
            _ = RefreshAsync(_current);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_sheet.IsOpen)
        {
            _ = _sheet.HideAsync();
            return true;
        }

        if (_current != 0)
        {
            _nav.Select(0, raise: true);
            return true;
        }

        return base.OnBackButtonPressed();
    }

    /// <summary>
    /// Builds the other tabs' views one by one shortly after start, while nothing else happens, so the first tap on
    /// a tab shows it at once instead of waiting for its layout to be inflated.
    /// </summary>
    private void PrewarmTabs()
    {
        for (var i = 1; i < _views.Length; i++)
        {
            var index = i;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(1200 + (index * 450)), () =>
            {
                if (_views[index] is null && !_switching)
                {
                    _views[index] = Create(index);
                }
            });
        }
    }

    private View Create(int index) => index switch
    {
        0 => new HomeView(Resolve<HomeViewModel>()),
        1 => new ServersView(Resolve<ServersViewModel>()),
        2 => new SubscriptionsView(Resolve<SubscriptionsViewModel>()),
        _ => new SettingsView(Resolve<SettingsViewModel>()),
    };

    private T Resolve<T>() where T : notnull => (T)_services.GetService(typeof(T))!;

    /// <summary>
    /// Tab transition: the old view drifts away and fades while the new one slides in from the side of the tab
    /// that was tapped and settles on a spring (direction follows the reading direction).
    /// </summary>
    private async Task ShowAsync(int index)
    {
        // Taps during a transition are not lost: the last tab asked for is shown right after.
        _wanted = index;
        if (_switching)
        {
            return;
        }

        while (_wanted != _current)
        {
            await SwitchAsync(_wanted);
        }
    }

    private int _wanted = -1;

    private async Task SwitchAsync(int index)
    {
        _switching = true;
        try
        {
            var previous = _current >= 0 ? _views[_current] : null;
            var view = _views[index] ??= Create(index);
            if (!_host.Children.Contains(view))
            {
                _host.Children.Add(view);
            }

            var direction = (_current < 0 ? 0 : Math.Sign(index - _current)) * (L.IsPersian ? -1 : 1);
            _current = index;
            view.IsVisible = true;
            _ = RefreshAsync(index);
            if (Motion.Reduced || previous is null)
            {
                view.Opacity = 1;
                view.TranslationX = 0;
                if (previous is not null)
                {
                    previous.IsVisible = false;
                }

                return;
            }

            view.Opacity = 0;
            view.TranslationX = 46 * direction;
            view.Scale = 0.985;
            var blur = new Animation(_ => BlurBackdrop.InvalidateAll());
            blur.Commit(this, "blur-follow", 16, 560, repeat: () => false);
            // The new page is usable as soon as it has faded in; its spring keeps settling on its own.
            _ = view.TranslateToAsync(0, 0, 520, Motion.Spring);
            _ = view.ScaleToAsync(1, 520, Motion.Spring);
            await Task.WhenAll(
                previous.FadeToAsync(0, 140, Easing.CubicIn),
                previous.TranslateToAsync(-28 * direction, 0, 160, Easing.CubicIn),
                previous.ScaleToAsync(0.97, 160, Easing.CubicIn),
                view.FadeToAsync(1, 200, Easing.CubicOut));
            previous.IsVisible = false;
            previous.TranslationX = 0;
            previous.Scale = 1;
            previous.Opacity = 1;
        }
        finally
        {
            _switching = false;
        }
    }

    private async Task RefreshAsync(int index)
    {
        switch (_views[index])
        {
            case ServersView s:
                await s.AppearAsync();
                break;
            case SubscriptionsView sub:
                await sub.AppearAsync();
                break;
        }
    }

    /// <summary>The "+" button: add servers from a QR code, the clipboard or a typed link.</summary>
    private async Task AddAsync()
    {
        var choice = await Sheets.ActionsAsync(L.Get("Add_Title"),
            ("scan", L.Get("Add_Scan"), "qr", false),
            ("paste", L.Get("Add_Paste"), "paste", false),
            ("link", L.Get("Add_Link"), "globe", false));
        string? text = null;
        switch (choice)
        {
            case "scan":
                if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
                {
                    await DisplayAlertAsync(L.Get("Add_Scan"), L.Get("Qr_Denied"), L.Get("Ok"));
                    return;
                }

                if (Platform.CurrentActivity is { } activity)
                {
                    text = await Platforms.Android.QrScanActivity.ScanAsync(activity);
                }

                break;
            case "paste":
                text = Clipboard.Default.HasText ? await Clipboard.Default.GetTextAsync() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    await DisplayAlertAsync(L.Get("Add_Paste"), L.Get("Add_Empty"), L.Get("Ok"));
                    return;
                }

                break;
            case "link":
                text = await DisplayPromptAsync(L.Get("Add_Link"), L.Get("Srv_ImportPrompt"), L.Get("Srv_Import"), L.Get("Cancel"), maxLength: 1_000_000);
                break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Show the result where the servers are listed.
        _nav.Select(1, raise: true);
        await Resolve<ServersViewModel>().ImportTextAsync(text);
    }

    /// <summary>Language switch: rebuild every view so texts and the reading direction change everywhere (the bar relabels itself).</summary>
    private void Rebuild()
    {
        FlowDirection = L.Flow;
        Resolve<HomeViewModel>().Relocalize();
        Resolve<ServersViewModel>().Relocalize();
        var keep = Math.Max(0, _current);
        _host.Children.Clear();
        Array.Clear(_views);
        _current = -1;
        _ = ShowAsync(keep);
        _nav.Select(keep, raise: false, animate: false);
    }
}
