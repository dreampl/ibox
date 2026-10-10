using System.ComponentModel;
using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Views;

/// <summary>
/// Home: timer and exit IP above a power button that carries the state (ripples while connecting, a luminous core
/// once connected, magenta for gaming and sky blue for streaming), and one compact card with location and live
/// numbers — everything at a glance without scrolling.
/// </summary>
public partial class HomeView : ContentView
{
    private readonly HomeViewModel _vm;
    private bool _wasConnected;
    private bool _hadActivity;
    private bool _hadError;
    private (PowerVisual, string)? _look;

    public HomeView(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.ServerPickerRequested = ShowServerPickerAsync;
        Power.Tapped += OnPowerTapped;
#if ANDROID
        // Equal-width digits: the centred timer must not wobble sideways as the seconds change.
        BigText.HandlerChanged += (_, _) =>
        {
            if (BigText.Handler?.PlatformView is Android.Widget.TextView tv)
            {
                tv.FontFeatureSettings = "tnum";
            }
        };
#endif
        Loaded += (_, _) =>
        {
            vm.PropertyChanged += OnChanged;
            _wasConnected = vm.IsConnected;
            _hadActivity = vm.HasActivity;
            Apply(animate: false);
        };
        Unloaded += (_, _) => vm.PropertyChanged -= OnChanged;
    }

    private async void OnPowerTapped(object? sender, EventArgs e)
    {
        try
        {
            await _vm.ToggleCommand.ExecuteAsync(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.ReportUnexpected(ex);
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HomeViewModel.PowerVisual) or nameof(HomeViewModel.AccentGradient):
                Apply(animate: true);
                break;
            case nameof(HomeViewModel.HasActivity):
                if (_vm.HasActivity && !_hadActivity && !Motion.Reduced)
                {
                    ActivityTag.Scale = 0.5;
                    ActivityTag.Opacity = 0;
                    _ = ActivityTag.FadeToAsync(1, 200);
                    _ = ActivityTag.ScaleToAsync(1, 620, Motion.Spring);
                }

                _hadActivity = _vm.HasActivity;
                break;
            case nameof(HomeViewModel.HasError):
                if (_vm.HasError && !_hadError && !Motion.Reduced)
                {
                    ErrorCard.TranslationY = -12;
                    ErrorCard.Opacity = 0;
                    _ = ErrorCard.FadeToAsync(1, 220);
                    _ = ErrorCard.TranslateToAsync(0, 0, 520, Motion.Spring);
                }

                _hadError = _vm.HasError;
                break;
            case nameof(HomeViewModel.LocationFlag) when _vm.IsConnected && !Motion.Reduced:
                FlagLabel.Scale = 0.3;
                _ = FlagLabel.ScaleToAsync(1, 640, Motion.Spring);
                break;
        }
    }

    private void Apply(bool animate)
    {
        var look = (_vm.PowerVisual, _vm.AccentGradient);
        if (animate && look == _look)
        {
            return;
        }

        _look = look;
        var accent = _vm.AccentColor;
        Power.SetState(_vm.PowerVisual, _vm.AccentGradient, accent, animate);
        Pages.MainNav.Current?.SetPower(_vm.PowerVisual, _vm.AccentGradient, accent);
        var tint = _vm.IsConnected ? accent : Color.FromArgb("#5B6CFF");
        HorizonRim.Stroke = new SolidColorBrush(tint.WithAlpha(0.35f));
        Horizon.Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0.62f),
            new(tint.WithAlpha(_vm.IsConnected ? 0.26f : 0.18f), 0.71f),
            new(tint.WithAlpha(0.06f), 0.78f),
            new(Colors.Transparent, 0.9f),
        }, new Point(0.5, 1), 0.75);
        ActivityTag.Stroke = new SolidColorBrush(accent.WithAlpha(0.6f));
        ActivityIcon.Color = accent;
        ActivityLabel.TextColor = accent;
        StatusIcon.Color = accent;

        var connected = _vm.IsConnected;
        if (animate && !Motion.Reduced)
        {
            // Hero text rises into place on every state change.
            foreach (var label in new VisualElement[] { BigText, SubText })
            {
                label.Opacity = 0;
                label.TranslationY = 12;
                _ = label.FadeToAsync(1, 260, Easing.CubicOut);
                _ = label.TranslateToAsync(0, 0, 560, Motion.Spring);
            }

            if (connected && !_wasConnected)
            {
                _ = Motion.StretchAsync(InfoCard);
            }
        }

        _wasConnected = connected;
    }

    private async void OnStatsTapped(object? sender, TappedEventArgs e)
    {
        if (!_vm.IsConnected)
        {
            return;
        }

        _ = Motion.TapAsync(Stats);
        await Sheets.ShowAsync(new SpeedTestSheet(_vm), L.Get("Speed_Title"));
    }

    private async Task ShowServerPickerAsync()
    {
        if (IPlatformApplication.Current?.Services.GetService(typeof(ServersViewModel)) is not ServersViewModel servers)
        {
            return;
        }

        await servers.LoadAsync();
        await Sheets.ShowAsync(new ServerPicker(servers, _vm), L.Get("Servers_Select"));
    }
}

/// <summary>Speed test drawer: three big numbers that count while the test runs.</summary>
public sealed class SpeedTestSheet : ContentView
{
    public SpeedTestSheet(HomeViewModel vm)
    {
        BindingContext = vm;
        Label Big(string path, Color color)
        {
            var l = new Label { FontSize = 30, FontFamily = "OpenSansSemibold", HorizontalOptions = LayoutOptions.Center, TextColor = color };
            l.SetBinding(Label.TextProperty, path);
            return l;
        }

        Label Small(string key) => new Label { FontSize = 12, TextColor = Rtl.Res("Text3"), HorizontalOptions = LayoutOptions.Center }.Also(l => l.SetBinding(Label.TextProperty, new Binding($"[{key}]", source: L.Instance)));
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)], Margin = new Thickness(0, 8) };
        grid.Add(new VerticalStackLayout { Children = { Big(nameof(HomeViewModel.SpeedDown), Rtl.Res("Teal")), Small("Speed_Down") } }, 0, 0);
        grid.Add(new VerticalStackLayout { Children = { Big(nameof(HomeViewModel.SpeedUp), Rtl.Res("Primary2")), Small("Speed_Up") } }, 1, 0);
        grid.Add(new VerticalStackLayout { Children = { Big(nameof(HomeViewModel.SpeedPing), Rtl.Res("Text")), Small("Speed_Ping") } }, 2, 0);
        var bar = new ProgressBar { Margin = new Thickness(0, 8) };
        bar.SetBinding(ProgressBar.ProgressProperty, nameof(HomeViewModel.SpeedFraction));
        bar.SetBinding(IsVisibleProperty, nameof(HomeViewModel.IsSpeedTesting));
        var phase = new Label { FontSize = 12, TextColor = Rtl.Res("Text2"), HorizontalTextAlignment = TextAlignment.Center };
        phase.SetBinding(Label.TextProperty, nameof(HomeViewModel.SpeedPhase));
        var orb = new OrbView { State = Domain.Visuals.OrbState.Listening, Tint = Rtl.Res("Teal"), WidthRequest = 64, HeightRequest = 64, HorizontalOptions = LayoutOptions.Center };
        orb.SetBinding(IsVisibleProperty, nameof(HomeViewModel.IsSpeedTesting));
        var button = new Button { Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Primary"], Margin = new Thickness(0, 12, 0, 0) };
        button.SetBinding(Button.TextProperty, nameof(HomeViewModel.SpeedButtonText));
        button.SetBinding(Button.CommandProperty, nameof(HomeViewModel.SpeedTestCommand));
        Content = new VerticalStackLayout { Padding = new Thickness(20, 4, 20, 8), Children = { orb, grid, bar, phase, button } };
        Loaded += (_, _) =>
        {
            if (!vm.IsSpeedTesting)
            {
                vm.SpeedTestCommand.Execute(null);
            }
        };
    }
}
