using System.Globalization;
using JackiVpn.Domain.Settings;
using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Platforms.Android;
using JackiVpn.Mobile.Services;

namespace JackiVpn.Mobile.Pages;

/// <summary>
/// Dynamic island settings: on/off (asks for "Display over other apps" the first time), what it shows, where it sits
/// (across and down the screen, or simply drag it), when it fades and whether it hides while IBOX is open.
/// </summary>
public sealed class IslandPage : AnimatedPage
{
    private readonly MobileServices _s;
    private readonly VerticalStackLayout _body = new() { Padding = new Thickness(20, 12, 20, 40), Spacing = 4 };
    private bool _askedPermission;

    public IslandPage(MobileServices services)
    {
        _s = services;
        Title = L.Get("Island_Title");
        Shell.SetNavBarIsVisible(this, true);
        Content = new ScrollView { Content = _body };
    }

    private IslandSettings Cur => _s.Settings.Current.Ui.Island;

    private void Set(Func<IslandSettings, IslandSettings> change) => _s.Settings.Update(x => x with { Ui = x.Ui with { Island = change(x.Ui.Island) } });

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Back from the system screen: switch on if the permission was just granted.
        if (_askedPermission && IslandOverlay.CanShow(_s.Context) && !Cur.Enabled)
        {
            Set(i => i with { Enabled = true });
        }

        _askedPermission = false;
        _s.IslandPreview = true;
        _s.SyncIsland();
        Build();
    }

    protected override void OnDisappearing()
    {
        _s.IslandPreview = false;
        _s.SyncIsland();
        base.OnDisappearing();
    }

    private void Build()
    {
        _body.Children.Clear();
        var i = Cur;
        _body.Children.Add(new Label { Text = L.Get("Island_Hint"), Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Hint"], Margin = new Thickness(4, 4, 4, 8) });

        var main = new SettingGroup();
        main.Rows.Add(Toggle("sparkle", L.Get("Island_Enable"), IslandOverlay.CanShow(_s.Context) ? null : L.Get("Island_Permission"), i.Enabled, on =>
        {
            if (on && !IslandOverlay.CanShow(_s.Context))
            {
                _askedPermission = true;
                IslandOverlay.RequestPermission(_s.Context);
                return;
            }

            Set(x => x with { Enabled = on });
        }));
        main.Rows.Add(Toggle("phone", L.Get("Island_HideInApp"), null, i.HideInApp, on => Set(x => x with { HideInApp = on })));
        _body.Children.Add(main);

        var show = new SettingGroup { Header = L.Get("Island_Show") };
        show.Rows.Add(Toggle("check", L.Get("Island_Status"), null, i.ShowStatus, on => Set(x => x with { ShowStatus = on })));
        show.Rows.Add(Toggle("clock", L.Get("Island_Ping"), null, i.ShowPing, on => Set(x => x with { ShowPing = on })));
        show.Rows.Add(Toggle("servers", L.Get("Island_Server"), null, i.ShowServer, on => Set(x => x with { ShowServer = on })));
        show.Rows.Add(Toggle("globe", L.Get("Island_Country"), null, i.ShowCountry, on => Set(x => x with { ShowCountry = on })));
        show.Rows.Add(Toggle("sync", L.Get("Island_Duration"), null, i.ShowDuration, on => Set(x => x with { ShowDuration = on })));
        _body.Children.Add(show);

        var place = new SettingGroup { Header = L.Get("Island_Position") };
        place.Rows.Add(Slider(L.Get("Island_PosX"), i.X, v => Set(x => x with { X = v })));
        place.Rows.Add(Slider(L.Get("Island_PosY"), i.Y, v => Set(x => x with { Y = v })));
        place.Rows.Add(new Label { Text = L.Get("Island_PosHint"), Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Hint"], Margin = new Thickness(16, 4, 16, 8) });
        place.Rows.Add(new SettingRow
        {
            Icon = "refresh",
            Tint = Rtl.Res("Primary2"),
            Title = L.Get("Island_Reset"),
            Command = new Command(() =>
            {
                Set(x => x with { X = 0.5, Y = 0.012 });
                Build();
            }),
        });
        _body.Children.Add(place);

        var fade = new SettingGroup();
        fade.Rows.Add(new SettingRow
        {
            Icon = "clock",
            Tint = Rtl.Res("Primary2"),
            Title = L.Get("Island_Fade"),
            Value = i.FadeAfterSeconds <= 0 ? "—" : i.FadeAfterSeconds.ToString(CultureInfo.InvariantCulture),
            Command = new Command(async () =>
            {
                var v = await Sheets.PickAsync(L.Get("Island_Fade"), new[] { 0, 3, 6, 10, 20, 30 }.Select(s => new SheetOption<int>(s == 0 ? "—" : s.ToString(CultureInfo.InvariantCulture), s)).ToList(), i.FadeAfterSeconds);
                Set(x => x with { FadeAfterSeconds = v });
                Build();
            }),
        });
        _body.Children.Add(fade);
    }

    private static SettingRow Toggle(string icon, string title, string? subtitle, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsToggled = value, VerticalOptions = LayoutOptions.Center };
        toggle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToggleSwitch.IsToggled))
            {
                changed(toggle.IsToggled);
            }
        };
        return new SettingRow { Icon = icon, Tint = Rtl.Res("Primary2"), Title = title, Subtitle = subtitle, Trailing = toggle };
    }

    private static View Slider(string title, double value, Action<double> changed)
    {
        var label = new Label { Text = title, Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Value"], FontSize = 14 };
        var percent = new Label { Text = $"{value * 100:0}%", Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Hint"], HorizontalOptions = LayoutOptions.End };
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, MinimumTrackColor = Rtl.Res("Primary2"), ThumbColor = Colors.White };
        // The island moves while the finger moves; the value is saved when it stops.
        slider.ValueChanged += (_, e) =>
        {
            percent.Text = $"{e.NewValue * 100:0}%";
            changed(Math.Round(e.NewValue, 3));
        };
        var head = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        head.Add(label, 0, 0);
        head.Add(percent, 1, 0);
        return new VerticalStackLayout { Padding = new Thickness(16, 10), Spacing = 4, Children = { head, slider } };
    }
}
