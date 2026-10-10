using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.Localization;
using JackiVpn.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Pages;

/// <summary>
/// First run: three short slides over a slowly turning glossy sphere (colour changes per slide), stretchy page
/// dots, and the language choice on the last slide.
/// </summary>
public sealed class OnboardingPage : ContentPage
{
    private static readonly (string Title, string Text, string Light, string Dark)[] Slides =
    [
        ("Onb_Title1", "Onb_Text1", "#A5B4FF", "#3341C9"),
        ("Onb_Title2", "Onb_Text2", "#99F6E4", "#0F8F80"),
        ("Onb_Title3", "Onb_Text3", "#F5D0FE", "#9D2BC2"),
    ];

    private readonly MobileServices _s;
    private readonly Ellipse _sphere = new() { WidthRequest = 230, HeightRequest = 230 };
    private readonly Ellipse _shine = new() { WidthRequest = 230, HeightRequest = 230, InputTransparent = true };
    private readonly Ellipse[] _rings = new Ellipse[3];
    private readonly Label _title = new() { FontSize = 30, FontFamily = "OpenSansSemibold", HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _text = new() { FontSize = 14, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(30, 0) };
    private readonly Label _over = new() { FontSize = 13, HorizontalTextAlignment = TextAlignment.Center };
    private readonly BoxView[] _dots = new BoxView[3];
    private readonly Grid _languages;
    private readonly Label _buttonText = new() { FontFamily = "OpenSansSemibold", FontSize = 16, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center };
    private readonly Border _button;
    private int _index;

    public OnboardingPage(MobileServices services)
    {
        _s = services;
        Shell.SetNavBarIsVisible(this, false);
        Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["PageGradient"];
        FlowDirection = L.Flow;
        _text.TextColor = Rtl.Res("Text2");
        _over.TextColor = Rtl.Res("Text3");

        var stage = new Grid { HeightRequest = 330, HorizontalOptions = LayoutOptions.Center, WidthRequest = 330 };
        for (var i = 0; i < 3; i++)
        {
            _rings[i] = new Ellipse
            {
                WidthRequest = 270 + i * 28,
                HeightRequest = 86 + i * 18,
                Stroke = new SolidColorBrush(Colors.White.WithAlpha(0.14f - i * 0.03f)),
                StrokeThickness = 1.2,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Rotation = -18 + i * 14,
            };
            stage.Add(_rings[i]);
        }

        stage.Add(_sphere);
        stage.Add(_shine);
        _sphere.HorizontalOptions = _shine.HorizontalOptions = LayoutOptions.Center;
        _sphere.VerticalOptions = _shine.VerticalOptions = LayoutOptions.Center;
        _shine.Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new(Colors.White.WithAlpha(0.55f), 0f),
            new(Colors.White.WithAlpha(0.0f), 0.45f),
        }, new Point(0.32, 0.28), 0.5);

        var dots = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < 3; i++)
        {
            _dots[i] = new BoxView { HeightRequest = 4, WidthRequest = 10, CornerRadius = 2, Color = Rtl.Res("Text3") };
            dots.Add(_dots[i]);
        }

        _languages = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 12, Margin = new Thickness(24, 6), IsVisible = false };
        _languages.Add(LanguageCard("English", "en"), 0, 0);
        _languages.Add(LanguageCard("فارسی", "fa"), 1, 0);

        _button = new Border
        {
            HeightRequest = 58,
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            StrokeThickness = 0,
            Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["PrimaryGradient"],
            Margin = new Thickness(24, 10, 24, 30),
            Padding = new Thickness(22, 0),
            Shadow = new Shadow { Brush = new SolidColorBrush(Color.FromArgb("#5B6CFF")), Opacity = 0.5f, Radius = 26, Offset = new Point(0, 10) },
            Content = new Grid
            {
                ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
                Children = { _buttonText, new Icon { Name = "arrow", Size = 22 }.Also(i => Grid.SetColumn(i, 1)) },
            },
        };
        var next = new TapGestureRecognizer();
        next.Tapped += async (_, _) =>
        {
            _ = Motion.StretchAsync(_button);
            await NextAsync();
        };
        _button.GestureRecognizers.Add(next);

        var skip = new Label { FontSize = 14, TextColor = Rtl.Res("Text2"), HorizontalOptions = LayoutOptions.End, Margin = new Thickness(24, 12) };
        skip.SetBinding(Label.TextProperty, new Binding("[Onb_Skip]", source: L.Instance));
        var skipTap = new TapGestureRecognizer();
        skipTap.Tapped += async (_, _) => await FinishAsync();
        skip.GestureRecognizers.Add(skipTap);

        Content = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children =
            {
                skip,
                new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 10, Children = { stage, dots, _over, _title, _text } }.Also(v => Grid.SetRow(v, 1)),
                _languages.Also(v => Grid.SetRow(v, 2)),
                _button.Also(v => Grid.SetRow(v, 3)),
            },
        };

        var swipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Left | SwipeDirection.Right };
        swipe.Swiped += async (_, e) =>
        {
            var forward = (e.Direction == SwipeDirection.Left) ^ L.IsPersian;
            if (forward)
            {
                await NextAsync();
            }
            else if (_index > 0)
            {
                await ShowAsync(_index - 1);
            }
        };
        Content.GestureRecognizers.Add(swipe);
        Loaded += async (_, _) =>
        {
            Spin();
            await ShowAsync(0, animate: false);
        };
        Unloaded += (_, _) => this.AbortAnimation("orb");
    }

    private Border LanguageCard(string label, string code)
    {
        var card = new Border
        {
            HeightRequest = 56,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Stroke = Rtl.Res("Stroke"),
            BackgroundColor = Rtl.Res("Surface2"),
            Content = new Label { Text = label, FontSize = 17, FontFamily = "OpenSansSemibold", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            await Motion.TapAsync(card);
            _s.Settings.Update(x => x with { Ui = x.Ui with { Language = code } });
            L.Instance.Apply(code);
            FlowDirection = L.Flow;
            await ShowAsync(_index, animate: false);
            HighlightLanguage();
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private void HighlightLanguage()
    {
        for (var i = 0; i < 2; i++)
        {
            var card = (Border)_languages.Children[i];
            var active = (i == 1) == L.IsPersian;
            card.Stroke = active ? (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["GlassRimAccent"] : new SolidColorBrush(Rtl.Res("Stroke"));
            card.StrokeThickness = active ? 2 : 1;
        }
    }

    private async Task NextAsync()
    {
        if (_index < Slides.Length - 1)
        {
            await ShowAsync(_index + 1);
        }
        else
        {
            await FinishAsync();
        }
    }

    private async Task FinishAsync()
    {
        _s.Settings.Update(x => x with { Ui = x.Ui with { OnboardingDone = true } });
        await Navigation.PopModalAsync(animated: true);
    }

    private async Task ShowAsync(int index, bool animate = true)
    {
        var forward = index >= _index;
        _index = index;
        var (title, text, light, dark) = Slides[index];
        _sphere.Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(light), 0f),
            new(Color.FromArgb(dark), 0.75f),
            new(Color.FromArgb(dark).WithLuminosity(0.12f), 1f),
        }, new Point(0.35, 0.3), 0.75);
        _sphere.Shadow = new Shadow { Brush = new SolidColorBrush(Color.FromArgb(dark)), Opacity = 0.7f, Radius = 60, Offset = new Point(0, 20) };
        _over.Text = "IBOX";
        _buttonText.Text = L.Get(index == Slides.Length - 1 ? "Onb_Start" : "Onb_Next");
        _languages.IsVisible = index == Slides.Length - 1;
        HighlightLanguage();
        for (var i = 0; i < _dots.Length; i++)
        {
            var active = i == index;
            var dot = _dots[i];
            dot.Color = active ? Rtl.Res("Text") : Rtl.Res("Text3");
            if (animate && !Motion.Reduced)
            {
                new Animation(v => dot.WidthRequest = v, dot.WidthRequest, active ? 34 : 10, Motion.Spring).Commit(dot, "dot", 16, 600);
            }
            else
            {
                dot.WidthRequest = active ? 34 : 10;
            }
        }

        if (!animate || Motion.Reduced)
        {
            _title.Text = L.Get(title);
            _text.Text = L.Get(text);
            return;
        }

        var dir = (forward ? 1 : -1) * (L.IsPersian ? -1 : 1);
        await Task.WhenAll(_title.FadeToAsync(0, 120), _text.FadeToAsync(0, 120), _title.TranslateToAsync(-30 * dir, 0, 120), _sphere.ScaleToAsync(0.86, 160, Easing.CubicIn));
        _title.Text = L.Get(title);
        _text.Text = L.Get(text);
        _title.TranslationX = 40 * dir;
        _text.TranslationX = 40 * dir;
        await Task.WhenAll(
            _title.FadeToAsync(1, 260),
            _text.FadeToAsync(1, 320),
            _title.TranslateToAsync(0, 0, 560, Motion.Spring),
            _text.TranslateToAsync(0, 0, 640, Motion.Spring),
            _sphere.ScaleToAsync(1, 720, Motion.Spring));
    }

    /// <summary>Rings orbit and the sphere floats (30 fps; a calm, continuous motion).</summary>
    private void Spin()
    {
        if (Motion.Reduced)
        {
            return;
        }

        new Animation(v =>
        {
            var a = v * Math.PI * 2;
            for (var i = 0; i < _rings.Length; i++)
            {
                _rings[i].Rotation = (-18 + i * 14) + v * 360 * (i % 2 == 0 ? 1 : -1) * 0.25;
                _rings[i].Scale = 1 + 0.03 * Math.Sin(a + i);
            }

            _sphere.TranslationY = 6 * Math.Sin(a * 2);
            _shine.TranslationY = _sphere.TranslationY;
        }, 0, 1).Commit(this, "orb", 33, 12000, Easing.Linear, repeat: () => true);
    }
}
