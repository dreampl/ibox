using JackiVpn.Application.Account;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Pages;

/// <summary>
/// The server's own mini apps (My subscription, Music) inside IBOX, signed in with the IBOX account: the same pages
/// Telegram shows. Signed out, or when music cannot open with the account, IBOX's own account page is used instead.
/// </summary>
public sealed class MiniAppPage : ContentPage
{
    private static readonly Color Ink = Color.FromArgb("#050505");
    private readonly MobileServices _s;
    private readonly WebView _web = new() { BackgroundColor = Ink };
    private readonly Button[] _tabs = new Button[2];
    private Uri? _page;
    private bool _music;

    public MiniAppPage(MobileServices services, bool music)
    {
        _s = services;
        _music = music;
        Title = "IBOX";
        BackgroundColor = Ink;
        Shell.SetNavBarIsVisible(this, false);
        var bar = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Padding = new Thickness(0, 10, 0, 8) };
        for (var i = 0; i < 2; i++)
        {
            var k = i;
            _tabs[i] = new Button
            {
                Text = i == 0 ? Tx("My subscription", "اشتراک من") : Tx("Music", "موزیک"),
                CornerRadius = 16,
                Padding = new Thickness(18, 6),
                FontSize = 13,
                HeightRequest = 34,
            };
            _tabs[i].Clicked += (_, _) => _ = LoadAsync(k == 1);
            bar.Add(_tabs[i]);
        }

        _web.Navigating += OnNavigating;
        _web.HandlerChanged += (_, _) =>
        {
            if (_web.Handler?.PlatformView is global::Android.Webkit.WebView w)
            {
                w.Settings.JavaScriptEnabled = true;
                w.Settings.DomStorageEnabled = true;   // the bridge and the music app keep their state there
                w.SetBackgroundColor(global::Android.Graphics.Color.Rgb(5, 5, 5));
            }
        };
        var grid = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)] };
        grid.Add(bar, 0, 0);
        grid.Add(_web, 0, 1);
        Content = grid;
    }

    /// <summary>Opens the mini app, or IBOX's own account page when signed out.</summary>
    public static Task OpenAsync(MobileServices services, bool music) =>
        Shell.Current.Navigation.PushAsync(services.Ibox.SignedIn ? new MiniAppPage(services, music) : new IboxAccountPage(services, music));

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _s.Ibox.SignInChanged += OnSignInChanged;
        if (_page is null)
        {
            _ = LoadAsync(_music);
        }
    }

    protected override void OnDisappearing()
    {
        _s.Ibox.SignInChanged -= OnSignInChanged;
        base.OnDisappearing();
    }

    private static string Tx(string en, string fa) => Localization.L.IsPersian ? fa : en;

    private void OnSignInChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        if (!_s.Ibox.SignedIn && Navigation.NavigationStack.LastOrDefault() == this)
        {
            await Navigation.PopAsync();
        }
    });

    private async Task LoadAsync(bool music)
    {
        _music = music;
        for (var i = 0; i < 2; i++)
        {
            var on = (i == 1) == music;
            _tabs[i].BackgroundColor = on ? Colors.White : Color.FromArgb("#1c1c1c");
            _tabs[i].TextColor = on ? Colors.Black : Color.FromArgb("#c8c8c8");
        }

        var lang = Localization.L.IsPersian ? "fa" : "en";
        var page = music ? await MiniAppPages.MusicAsync(_s.Ibox, lang) : MiniAppPages.Subscription(_s.Ibox, lang);
        if (page is null)
        {
            // Music could not open with the account: IBOX's own music page has the other ways in.
            await Navigation.PushAsync(new IboxAccountPage(_s, music));
            Navigation.RemovePage(this);
            return;
        }

        _page = page;
        _web.Source = new UrlWebViewSource { Url = page.AbsoluteUri };
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (_page is null)
        {
            return;
        }

        var (action, target) = MiniAppPages.Classify(_page, e.Url);
        if (action == MiniAppAction.None)
        {
            return;
        }

        e.Cancel = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (action)
            {
                case MiniAppAction.OpenOutside when target is not null:
                    try
                    {
                        await Launcher.Default.OpenAsync(target);
                    }
                    catch (Exception x) when (x is InvalidOperationException or NotSupportedException or global::Android.Content.ActivityNotFoundException)
                    {
                        // nothing on the phone opens that link
                    }

                    break;
                case MiniAppAction.Close:
                    await Navigation.PopAsync();
                    break;
                case MiniAppAction.SignedOut when _music:
                    _s.Ibox.Tokens.Remove(MusicClient.TokenName);   // the music session ended: a new one with the account
                    await LoadAsync(true);
                    break;
                case MiniAppAction.SignedOut:
                    await Navigation.PopAsync();
                    try
                    {
                        await _s.Ibox.Watcher.RefreshAsync();   // signs out here when the server says so
                    }
                    catch (Exception x) when (x is ServiceUnreachableException or InvalidOperationException)
                    {
                    }

                    break;
            }
        });
    }
}
