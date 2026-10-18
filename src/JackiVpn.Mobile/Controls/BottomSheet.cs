using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Controls;

/// <summary>
/// Half-screen drawer: slides up on a spring over a dimmed backdrop, follows the finger when dragged and closes
/// when pulled down (or the backdrop / Back is tapped). One host lives in the main page; <see cref="Sheets"/>
/// builds pickers and action lists on top of it.
/// </summary>
public sealed class BottomSheetHost : Grid
{
    private readonly BoxView _backdrop = new() { Color = Colors.Black, Opacity = 0 };
    private readonly Border _sheet;
    private readonly Label _title = new() { FontFamily = "OpenSansSemibold", FontSize = 17, Margin = new Thickness(22, 4, 22, 6) };
    private readonly ContentView _body = new();
    private readonly VerticalStackLayout _header;
    private TaskCompletionSource<bool>? _closed;
    private double _dragStart;

    public BottomSheetHost()
    {
        IsVisible = false;
        InputTransparent = true;
        CascadeInputTransparent = false;
        var handle = new Border
        {
            WidthRequest = 42,
            HeightRequest = 5,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            StrokeThickness = 0,
            BackgroundColor = Colors.White.WithAlpha(0.22f),
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 10, 0, 8),
        };
        _sheet = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(30, 30, 0, 0) },
            Stroke = Rtl.Res("Stroke"),
            StrokeThickness = 1,
            BackgroundColor = Rtl.Res("Surface"),
            VerticalOptions = LayoutOptions.End,
            Padding = new Thickness(0, 0, 0, 24),
            Content = new VerticalStackLayout { Children = { (_header = new VerticalStackLayout { BackgroundColor = Colors.Transparent, Children = { handle, _title } }), _body } },
        };

        var tapOut = new TapGestureRecognizer();
        tapOut.Tapped += (_, _) => _ = HideAsync();
        _backdrop.GestureRecognizers.Add(tapOut);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPan;
        _header.GestureRecognizers.Add(pan);

        Children.Add(_backdrop);
        Children.Add(_sheet);
    }

    public static BottomSheetHost? Current { get; set; }

    public bool IsOpen => IsVisible && _closed is not null;

    /// <summary>Shows <paramref name="content"/>; completes when the sheet is closed.</summary>
    public async Task ShowAsync(View content, string? title)
    {
        if (_closed is not null)
        {
            await HideAsync();
        }

        _closed = new TaskCompletionSource<bool>();
        _generation++;
        _title.Text = title;
        _title.IsVisible = !string.IsNullOrEmpty(title);
        _body.Content = content;
        _sheet.MaximumHeightRequest = Math.Max(300, Height * 0.86);
        IsVisible = true;
        InputTransparent = false;
        _sheet.TranslationY = Math.Max(Height, 600);
        _sheet.Opacity = 1;
        _ = _backdrop.FadeToAsync(0.55, Motion.Ms(260), Easing.CubicOut);
        await _sheet.TranslateToAsync(0, 0, Motion.Ms(620), Motion.Spring);
        await _closed.Task;
    }

    public async Task HideAsync()
    {
        if (_closed is not { } closed)
        {
            return;
        }

        _closed = null;
        var generation = _generation;
        InputTransparent = true;
        closed.TrySetResult(true);
        _ = _backdrop.FadeToAsync(0, Motion.Ms(220), Easing.CubicIn);
        await _sheet.TranslateToAsync(0, Math.Max(_sheet.Height, 300) + 40, Motion.Ms(240), Easing.CubicIn);
        if (generation == _generation)
        {
            // Not if another sheet opened meanwhile (an action that leads to a second sheet).
            IsVisible = false;
            _body.Content = null;
        }
    }

    private int _generation;

    private void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dragStart = _sheet.TranslationY;
                break;
            case GestureStatus.Running:
                // Down follows the finger; up resists (rubber band).
                var y = _dragStart + e.TotalY;
                _sheet.TranslationY = y >= 0 ? y : y / 4;
                _backdrop.Opacity = 0.55 * Math.Clamp(1 - (_sheet.TranslationY / Math.Max(1, _sheet.Height)), 0, 1);
                break;
            case GestureStatus.Completed or GestureStatus.Canceled:
                if (_sheet.TranslationY > Math.Min(140, _sheet.Height * 0.3))
                {
                    _ = HideAsync();
                }
                else
                {
                    _ = _sheet.TranslateToAsync(0, 0, Motion.Ms(520), Motion.Spring);
                    _ = _backdrop.FadeToAsync(0.55, Motion.Ms(200));
                }

                break;
        }
    }
}

public sealed record SheetOption<T>(string Label, T Value, string? Subtitle = null, string? Icon = null);

/// <summary>Pickers and action lists shown in the bottom sheet.</summary>
public static class Sheets
{
    /// <summary>Single choice; returns the chosen value, or <paramref name="current"/> when dismissed.</summary>
    public static async Task<T> PickAsync<T>(string title, IReadOnlyList<SheetOption<T>> options, T current)
    {
        if (BottomSheetHost.Current is not { } host)
        {
            return current;
        }

        // The choice is returned the moment it is tapped; the sheet closes while the caller already acts on it.
        var chosen = new TaskCompletionSource<T>();
        var list = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(12, 4) };
        var checks = new List<Icon>();
        foreach (var option in options)
        {
            var selected = EqualityComparer<T>.Default.Equals(option.Value, current);
            var check = new Icon { Name = "check", Size = 20, Color = Rtl.Res("Primary2"), Opacity = selected ? 1 : 0, Scale = selected ? 1 : 0.4 };
            checks.Add(check);
            var texts = new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center,
                Children = { new Label { Text = option.Label, FontFamily = "OpenSansSemibold", FontSize = 15 } },
            };
            if (option.Subtitle is { Length: > 0 } sub)
            {
                texts.Children.Add(new Label { Text = sub, FontSize = 12, TextColor = Rtl.Res("Text3"), LineBreakMode = LineBreakMode.WordWrap });
            }

            var row = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                ColumnSpacing = 12,
                Padding = new Thickness(12, 12),
            };
            if (option.Icon is { } icon)
            {
                row.Add(new Icon { Name = icon, Size = 20, Color = Rtl.Res("Text2") }, 0, 0);
            }

            row.Add(texts, 1, 0);
            row.Add(check, 2, 0);
            var rowBorder = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 16 },
                StrokeThickness = 0,
                BackgroundColor = selected ? Colors.White.WithAlpha(0.05f) : Colors.Transparent,
                Content = row,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                foreach (var c in checks)
                {
                    _ = c.FadeToAsync(c == check ? 1 : 0, 120);
                }

                _ = check.ScaleToAsync(1, Motion.Ms(320), Motion.Spring);
                _ = Motion.TapAsync(rowBorder);
                chosen.TrySetResult(option.Value);
                // A short glance at the tick, then the sheet slides away (the result is already delivered).
                host.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(Motion.Reduced ? 0 : 120), () => _ = host.HideAsync());
            };
            rowBorder.GestureRecognizers.Add(tap);
            list.Children.Add(rowBorder);
            Motion.SetAppear(rowBorder, true);
        }

        var shown = host.ShowAsync(new ScrollView { Content = list }, title);
        _ = shown.ContinueWith(_ => chosen.TrySetResult(current), TaskScheduler.Default);
        return await chosen.Task;
    }

    /// <summary>Action list; returns the chosen key, or null.</summary>
    public static async Task<string?> ActionsAsync(string title, params (string Key, string Label, string Icon, bool Danger)[] actions)
    {
        if (BottomSheetHost.Current is not { } host)
        {
            return null;
        }

        var chosen = new TaskCompletionSource<string?>();
        var list = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(12, 4) };
        foreach (var a in actions)
        {
            var color = a.Danger ? Rtl.Res("Danger") : Rtl.Res("Text");
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 14, Padding = new Thickness(14, 14) };
            row.Add(new Icon { Name = a.Icon, Size = 21, Color = a.Danger ? color : Rtl.Res("Text2") }, 0, 0);
            row.Add(new Label { Text = a.Label, FontSize = 15, FontFamily = "OpenSansSemibold", TextColor = color, VerticalOptions = LayoutOptions.Center }, 1, 0);
            var border = new Border { StrokeShape = new RoundRectangle { CornerRadius = 16 }, StrokeThickness = 0, Content = row };
            var tap = new TapGestureRecognizer();
            var key = a.Key;
            tap.Tapped += (_, _) =>
            {
                // Act at once: the press feedback and the closing slide run while the caller already works.
                _ = Motion.TapAsync(border);
                chosen.TrySetResult(key);
                _ = host.HideAsync();
            };
            border.GestureRecognizers.Add(tap);
            Motion.SetAppear(border, true);
            list.Children.Add(border);
        }

        var shown = host.ShowAsync(list, title);
        _ = shown.ContinueWith(_ => chosen.TrySetResult(null), TaskScheduler.Default);
        return await chosen.Task;
    }

    /// <summary>Any custom content (e.g. the server picker).</summary>
    public static Task ShowAsync(View content, string? title) => BottomSheetHost.Current?.ShowAsync(content, title) ?? Task.CompletedTask;

    public static Task HideAsync() => BottomSheetHost.Current?.HideAsync() ?? Task.CompletedTask;
}
