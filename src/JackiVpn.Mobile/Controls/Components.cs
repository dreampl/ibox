using System.Collections;
using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Controls;

public static class ViewExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

internal static class Rtl
{
    public static bool Of(VisualElement e) => ((IVisualElementController)e).EffectiveFlowDirection.HasFlag(EffectiveFlowDirection.RightToLeft);

    public static Color Res(string key) => (Color)Microsoft.Maui.Controls.Application.Current!.Resources[key];
}

/// <summary>
/// Animated switch: the thumb travels on a spring and stretches mid-way (squash and stretch), the track
/// cross-fades its colour. Two-way <see cref="IsToggled"/>.
/// </summary>
public sealed class ToggleSwitch : ContentView
{
    public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(nameof(IsToggled), typeof(bool), typeof(ToggleSwitch), false,
        BindingMode.TwoWay, propertyChanged: (b, o, n) => ((ToggleSwitch)b).Animate((bool)n, animate: true));

    public static readonly BindableProperty OnColorProperty = BindableProperty.Create(nameof(OnColor), typeof(Color), typeof(ToggleSwitch), Color.FromArgb("#5B6CFF"),
        propertyChanged: (b, _, _) => ((ToggleSwitch)b).Animate(((ToggleSwitch)b).IsToggled, animate: false));

    private readonly Border _trackOn;
    private readonly Ellipse _thumb;

    public ToggleSwitch()
    {
        WidthRequest = 52;
        HeightRequest = 32;
        VerticalOptions = LayoutOptions.Center;
        var trackOff = new Border { StrokeShape = new RoundRectangle { CornerRadius = 16 }, StrokeThickness = 0, BackgroundColor = Color.FromArgb("#26314B") };
        _trackOn = new Border { StrokeShape = new RoundRectangle { CornerRadius = 16 }, StrokeThickness = 0, BackgroundColor = OnColor, Opacity = 0 };
        _thumb = new Ellipse
        {
            WidthRequest = 24,
            HeightRequest = 24,
            Fill = new SolidColorBrush(Colors.White),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(4, 0),
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.35f, Radius = 6, Offset = new Point(0, 2) },
        };
        Content = new Grid { FlowDirection = FlowDirection.LeftToRight, Children = { trackOff, _trackOn, _thumb } };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            IsToggled = !IsToggled;
            try
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
            }
            catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
            {
            }
        };
        GestureRecognizers.Add(tap);
        Loaded += (_, _) => Animate(IsToggled, animate: false);
    }

    public bool IsToggled
    {
        get => (bool)GetValue(IsToggledProperty);
        set => SetValue(IsToggledProperty, value);
    }

    public Color OnColor
    {
        get => (Color)GetValue(OnColorProperty);
        set => SetValue(OnColorProperty, value);
    }

    private void Animate(bool on, bool animate)
    {
        _trackOn.BackgroundColor = OnColor;
        // Right-to-left: "on" sits on the left, like the system switch.
        var x = Localization.L.IsPersian ? (on ? 0 : 20) : (on ? 20 : 0);
        if (!animate || Motion.Reduced)
        {
            _thumb.TranslationX = x;
            _trackOn.Opacity = on ? 1 : 0;
            return;
        }

        _ = _trackOn.FadeToAsync(on ? 1 : 0, 220, Easing.CubicOut);
        _ = _thumb.TranslateToAsync(x, 0, 520, Motion.Spring);
        _ = StretchThumbAsync();
    }

    private async Task StretchThumbAsync()
    {
        await _thumb.ScaleXToAsync(1.3, 110, Easing.CubicOut);
        await _thumb.ScaleXToAsync(1, 480, Motion.Spring);
    }
}

/// <summary>
/// Segmented control: a pill slides to the chosen segment (stretching on the way, then springing into place).
/// </summary>
public sealed class SegmentedControl : ContentView
{
    public static readonly BindableProperty ItemsProperty = BindableProperty.Create(nameof(Items), typeof(IList), typeof(SegmentedControl), null,
        propertyChanged: (b, _, _) => ((SegmentedControl)b).Build());

    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(SegmentedControl), 0,
        BindingMode.TwoWay, propertyChanged: (b, o, n) => ((SegmentedControl)b).Move((int)o, (int)n, animate: true));

    private readonly Grid _grid = new() { HeightRequest = 40, FlowDirection = FlowDirection.LeftToRight };
    private readonly Border _pill = new()
    {
        StrokeShape = new RoundRectangle { CornerRadius = 14 },
        StrokeThickness = 0,
        HorizontalOptions = LayoutOptions.Start,
        Margin = new Thickness(3),
    };

    private readonly List<Label> _labels = [];

    public SegmentedControl()
    {
        _pill.Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["PrimaryGradient"];
        Content = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 17 },
            Stroke = Rtl.Res("Stroke"),
            StrokeThickness = 1,
            BackgroundColor = Rtl.Res("Surface2"),
            Content = _grid,
        };
        SizeChanged += (_, _) => Move(SelectedIndex, SelectedIndex, animate: false);
    }

    public IList? Items
    {
        get => (IList?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private void Build()
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _labels.Clear();
        var items = Items?.Cast<object>().Select(o => o?.ToString() ?? string.Empty).ToList() ?? [];
        foreach (var _ in items)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        if (items.Count > 0)
        {
            _grid.Add(_pill);
            Grid.SetColumnSpan(_pill, 1);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var label = new Label
            {
                Text = items[i],
                FontSize = 13,
                FontFamily = "OpenSansSemibold",
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                TextColor = i == SelectedIndex ? Colors.White : Rtl.Res("Text2"),
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => SelectedIndex = index;
            label.GestureRecognizers.Add(tap);
            _labels.Add(label);
            _grid.Add(label, Localization.L.IsPersian ? items.Count - 1 - i : i, 0);
        }

        Move(SelectedIndex, SelectedIndex, animate: false);
    }

    private void Move(int from, int to, bool animate)
    {
        if (_labels.Count == 0 || Width <= 0)
        {
            return;
        }

        to = Math.Clamp(to, 0, _labels.Count - 1);
        var segment = (Width - 2) / _labels.Count;
        _pill.WidthRequest = segment - 6;
        var column = Localization.L.IsPersian ? _labels.Count - 1 - to : to;
        var x = column * segment;
        for (var i = 0; i < _labels.Count; i++)
        {
            _labels[i].TextColor = i == to ? Colors.White : Rtl.Res("Text2");
        }

        if (!animate || Motion.Reduced)
        {
            _pill.TranslationX = x;
            return;
        }

        _ = _pill.TranslateToAsync(x, 0, 560, Motion.Spring);
        _ = StretchAsync(Math.Abs(to - from));
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
        }
    }

    private async Task StretchAsync(int distance)
    {
        await _pill.ScaleXToAsync(1 + 0.18 * Math.Max(1, distance), 120, Easing.CubicOut);
        await _pill.ScaleXToAsync(1, 520, Motion.Spring);
    }
}

/// <summary>A settings row: tinted icon tile, title, optional subtitle, a trailing control or a chevron.</summary>
public sealed class SettingRow : ContentView
{
    public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(string), typeof(SettingRow), string.Empty,
        propertyChanged: (b, _, n) => ((SettingRow)b)._icon.Name = (string)n);

    public static readonly BindableProperty TintProperty = BindableProperty.Create(nameof(Tint), typeof(Color), typeof(SettingRow), Color.FromArgb("#5B6CFF"),
        propertyChanged: (b, _, n) => ((SettingRow)b).ApplyTint((Color)n));

    public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(SettingRow), string.Empty,
        propertyChanged: (b, _, n) => ((SettingRow)b)._title.Text = (string)n);

    public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(nameof(Subtitle), typeof(string), typeof(SettingRow), null,
        propertyChanged: (b, _, n) =>
        {
            var row = (SettingRow)b;
            row._subtitle.Text = (string?)n;
            row._subtitle.IsVisible = !string.IsNullOrEmpty((string?)n);
        });

    public static readonly BindableProperty ValueProperty = BindableProperty.Create(nameof(Value), typeof(string), typeof(SettingRow), null,
        propertyChanged: (b, _, n) =>
        {
            var row = (SettingRow)b;
            row._value.Text = (string?)n;
            row._value.IsVisible = !string.IsNullOrEmpty((string?)n);
        });

    public static readonly BindableProperty TrailingProperty = BindableProperty.Create(nameof(Trailing), typeof(View), typeof(SettingRow), null,
        propertyChanged: (b, _, n) => ((SettingRow)b).SetTrailing((View?)n));

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(SettingRow), null,
        propertyChanged: (b, _, n) => ((SettingRow)b)._chevron.IsVisible = n is not null && ((SettingRow)b).Trailing is null);

    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(SettingRow), null);

    private readonly Icon _icon = new() { Size = 19 };
    private readonly Border _tile;
    private readonly Label _title = new() { FontFamily = "OpenSansSemibold", FontSize = 15 };
    private readonly Label _subtitle = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap, IsVisible = false };
    private readonly Label _value = new() { FontSize = 13, VerticalOptions = LayoutOptions.Center, IsVisible = false };
    private readonly Icon _chevron = new() { Name = "chevron", Size = 16, IsVisible = false };
    private readonly ContentView _trailing = new() { VerticalOptions = LayoutOptions.Center };
    private readonly Grid _root;

    public SettingRow()
    {
        _subtitle.TextColor = Rtl.Res("Text3");
        _value.TextColor = Rtl.Res("Text2");
        _chevron.Color = Rtl.Res("Text3");
        _tile = new Border
        {
            WidthRequest = 36,
            HeightRequest = 36,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            StrokeThickness = 0,
            VerticalOptions = LayoutOptions.Center,
            Content = _icon,
        };
        ApplyTint(Tint);
        _root = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 12,
            Padding = new Thickness(14, 12),
        };
        _root.Add(_tile, 0, 0);
        _root.Add(new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { _title, _subtitle } }, 1, 0);
        _root.Add(_value, 2, 0);
        _root.Add(_trailing, 3, 0);
        _root.Add(_chevron, 4, 0);
        Content = _root;

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (Command is { } cmd && cmd.CanExecute(CommandParameter))
            {
                _ = FlashAsync();
                cmd.Execute(CommandParameter);
            }
            else if (Trailing is ToggleSwitch toggle)
            {
                toggle.IsToggled = !toggle.IsToggled;
            }

            await Task.CompletedTask;
        };
        GestureRecognizers.Add(tap);
    }

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public string? Value { get => (string?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public View? Trailing { get => (View?)GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    private void ApplyTint(Color c)
    {
        _icon.Color = c;
        _tile.BackgroundColor = c.WithAlpha(0.16f);
    }

    private void SetTrailing(View? view)
    {
        _trailing.Content = view;
        _chevron.IsVisible = view is null && Command is not null;
    }

    private async Task FlashAsync()
    {
        if (Motion.Reduced)
        {
            return;
        }

        _root.BackgroundColor = Colors.White.WithAlpha(0.06f);
        await _root.ScaleToAsync(0.985, 80, Easing.CubicOut);
        _ = _root.ScaleToAsync(1, 420, Motion.Spring);
        await Task.Delay(140);
        _root.BackgroundColor = Colors.Transparent;
    }
}

/// <summary>A group of rows on one rounded surface, with an optional overline title.</summary>
[ContentProperty(nameof(Rows))]
public sealed class SettingGroup : ContentView
{
    public static readonly BindableProperty HeaderProperty = BindableProperty.Create(nameof(Header), typeof(string), typeof(SettingGroup), null,
        propertyChanged: (b, _, n) =>
        {
            var g = (SettingGroup)b;
            g._header.Text = ((string?)n)?.ToUpperInvariant();
            g._header.IsVisible = !string.IsNullOrEmpty((string?)n);
        });

    private readonly Label _header = new() { Margin = new Thickness(6, 18, 6, 8), IsVisible = false };
    private readonly VerticalStackLayout _rows = [];

    public SettingGroup()
    {
        _header.Style = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Overline"];
        Content = new VerticalStackLayout
        {
            Children =
            {
                _header,
                new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 22 },
                    Stroke = Rtl.Res("Stroke"),
                    StrokeThickness = 1,
                    BackgroundColor = Rtl.Res("Surface"),
                    Content = _rows,
                },
            },
        };
        Motion.SetAppear(this, true);
    }

    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>XAML children land in the rounded surface.</summary>
    public IList<IView> Rows => _rows.Children;
}

/// <summary>Latency as 1–4 bars (green → amber → red), like a signal meter.</summary>
public sealed class SignalBars : HorizontalStackLayout
{
    public static readonly BindableProperty LevelProperty = BindableProperty.Create(nameof(Level), typeof(int), typeof(SignalBars), 0,
        propertyChanged: (b, _, _) => ((SignalBars)b).Apply());

    private readonly BoxView[] _bars = new BoxView[4];

    public SignalBars()
    {
        Spacing = 2.5;
        VerticalOptions = LayoutOptions.Center;
        for (var i = 0; i < 4; i++)
        {
            _bars[i] = new BoxView { WidthRequest = 3.5, HeightRequest = 5 + i * 3.5, CornerRadius = 2, VerticalOptions = LayoutOptions.End };
            Children.Add(_bars[i]);
        }

        Apply();
    }

    /// <summary>0 = unknown/failed, 1..4.</summary>
    public int Level { get => (int)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private void Apply()
    {
        var color = Level switch
        {
            >= 3 => Rtl.Res("Teal"),
            2 => Rtl.Res("Warn"),
            1 => Rtl.Res("Danger"),
            _ => Rtl.Res("Text3"),
        };
        for (var i = 0; i < 4; i++)
        {
            _bars[i].Color = i < Level ? color : Rtl.Res("Surface3");
        }
    }
}

/// <summary>Quota bar: the fill grows to its value on a spring when it appears or changes.</summary>
public sealed class UsageBar : ContentView
{
    public static readonly BindableProperty FractionProperty = BindableProperty.Create(nameof(Fraction), typeof(double), typeof(UsageBar), 0d,
        propertyChanged: (b, _, _) => ((UsageBar)b).Apply(animate: true));

    public static readonly BindableProperty BarColorProperty = BindableProperty.Create(nameof(BarColor), typeof(Color), typeof(UsageBar), Color.FromArgb("#5B6CFF"),
        propertyChanged: (b, _, n) => ((UsageBar)b)._fill.BackgroundColor = (Color)n);

    private readonly Border _fill = new()
    {
        StrokeShape = new RoundRectangle { CornerRadius = 4 },
        StrokeThickness = 0,
        HorizontalOptions = LayoutOptions.Start,
        BackgroundColor = Color.FromArgb("#5B6CFF"),
    };

    public UsageBar()
    {
        HeightRequest = 8;
        Content = new Grid
        {
            Children =
            {
                new Border { StrokeShape = new RoundRectangle { CornerRadius = 4 }, StrokeThickness = 0, BackgroundColor = Rtl.Res("Surface3") },
                _fill,
            },
        };
        SizeChanged += (_, _) => Apply(animate: true);
    }

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    public Color BarColor { get => (Color)GetValue(BarColorProperty); set => SetValue(BarColorProperty, value); }

    private void Apply(bool animate)
    {
        if (Width <= 0)
        {
            return;
        }

        var target = Math.Clamp(Fraction, 0, 1) * Width;
        if (!animate || Motion.Reduced)
        {
            _fill.WidthRequest = target;
            return;
        }

        var from = _fill.WidthRequest > 0 ? _fill.WidthRequest : 0;
        new Animation(v => _fill.WidthRequest = v, from, target, Motion.Spring).Commit(this, "usage", 16, 900);
    }
}