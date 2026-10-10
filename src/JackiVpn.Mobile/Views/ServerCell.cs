using System.ComponentModel;
using JackiVpn.Mobile.Controls;
using JackiVpn.Mobile.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace JackiVpn.Mobile.Views;

/// <summary>
/// One server row: flag, name, protocol and connect time, signal bars and a star. The selected server is a bright
/// card (as in the reference design); selection and favourites animate.
/// </summary>
public sealed class ServerCell : ContentView
{
    private readonly Border _card;
    private readonly Label _name = new() { FontFamily = "OpenSansSemibold", FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _sub = new() { FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _flag = new() { FontSize = 22, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    private readonly Border _flagBubble;
    private readonly SignalBars _bars = new();
    private readonly Icon _star = new() { Name = "star", Size = 20 };
    private readonly OrbView _testing = new() { State = Domain.Visuals.OrbState.Searching, WidthRequest = 22, HeightRequest = 22, IsVisible = false };
    private readonly Border _badge;
    private readonly Icon _more = new() { Name = "more", Size = 20, StrokeThickness = 3 };
    private readonly Grid _moreHit;
    private ServerItem? _item;

    public ServerCell(Func<ServerItem, Task> tapped, Func<ServerItem, Task> more, Func<ServerItem, Task> favorite)
    {
        _flagBubble = new Border { WidthRequest = 40, HeightRequest = 40, StrokeShape = new RoundRectangle { CornerRadius = 20 }, StrokeThickness = 0, Content = _flag };
        _badge = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 7 },
            StrokeThickness = 0,
            Padding = new Thickness(6, 1),
            Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["PrimaryGradient"],
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
            Content = new Label { Text = "✓", FontSize = 10, TextColor = Colors.White, FontFamily = "OpenSansSemibold" },
        };
        var starHit = new Grid { WidthRequest = 36, HeightRequest = 36, Children = { _star } };
        var starTap = new TapGestureRecognizer();
        starTap.Tapped += async (_, _) =>
        {
            if (_item is { } i)
            {
                _ = Motion.StretchAsync(_star);
                await favorite(i);
            }
        };
        starHit.GestureRecognizers.Add(starTap);

        var grid = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 10,
        };
        _moreHit = new Grid { WidthRequest = 30, HeightRequest = 36, Children = { _more } };
        grid.Add(_flagBubble, 0, 0);

        // Name and badge share the row in a grid (a horizontal stack gives the name unlimited width, so long
        // provider names pushed the bars, star and menu out of the card instead of being shortened).
        var nameRow = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 6 };
        _name.MaxLines = 1;
        _sub.MaxLines = 1;
        _badge.HorizontalOptions = LayoutOptions.Start;
        nameRow.Add(_name, 0, 0);
        nameRow.Add(_badge, 1, 0);
        grid.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 1,
            Children = { nameRow, _sub },
        }, 1, 0);
        grid.Add(new Grid { Children = { _bars, _testing } }, 2, 0);
        grid.Add(starHit, 3, 0);
        grid.Add(_moreHit, 4, 0);
        _card = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            StrokeThickness = 1,
            Padding = new Thickness(12, 10, 6, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Content = grid,
        };
        Content = _card;

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (_item is { } i)
            {
                _ = Motion.TapAsync(_card);
                await tapped(i);
            }
        };
        _card.GestureRecognizers.Add(tap);
        var moreTap = new TapGestureRecognizer();
        moreTap.Tapped += async (_, _) =>
        {
            if (_item is { } i)
            {
                _ = Motion.TapAsync(_more);
                await more(i);
            }
        };
        _moreHit.GestureRecognizers.Add(moreTap);
        Motion.SetAppear(this, true);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemChanged;
        }

        _item = BindingContext as ServerItem;
        if (_item is not null)
        {
            _item.PropertyChanged += OnItemChanged;
            Apply(animate: false);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => MainThread.BeginInvokeOnMainThread(() => Apply(animate: e.PropertyName is nameof(ServerItem.IsSelected) or nameof(ServerItem.IsFavorite) or ""));

    private void Apply(bool animate)
    {
        if (_item is not { } i)
        {
            return;
        }

        var selected = i.IsSelected;
        _name.Text = i.Name;
        _sub.Text = i.Subtitle;
        _flag.Text = i.Flag;
        _bars.Level = i.SignalLevel;
        _testing.IsVisible = i.IsTesting;
        _testing.Tint = selected ? Color.FromArgb("#2A3550") : Rtl.Res("Text");
        _bars.IsVisible = !i.IsTesting;
        _badge.IsVisible = i.IsOfficial;
        _card.BackgroundColor = selected ? Color.FromArgb("#F2F5FF") : Rtl.Res("Surface2");
        _card.Stroke = selected ? Colors.Transparent : Rtl.Res("Stroke");
        _name.TextColor = selected ? Color.FromArgb("#0B1220") : Rtl.Res("Text");
        _sub.TextColor = selected ? Color.FromArgb("#4B5675") : Rtl.Res("Text3");
        _flagBubble.BackgroundColor = selected ? Color.FromArgb("#E3E8F7") : Rtl.Res("Surface3");
        _star.Color = i.IsFavorite ? Rtl.Res("Teal") : selected ? Color.FromArgb("#4B5675") : Rtl.Res("Text3");
        _star.Fill = i.IsFavorite ? new SolidColorBrush(Rtl.Res("Teal")) : null;
        _more.Color = selected ? Color.FromArgb("#4B5675") : Rtl.Res("Text3");
        if (animate && selected && !Motion.Reduced)
        {
            _ = Motion.StretchAsync(_card);
        }
    }
}

/// <summary>Group header: subscription name, server count and an open/close chevron that rotates.</summary>
public sealed class ServerGroupHeader : ContentView
{
    private readonly Icon _chevron = new() { Name = "chevron-down", Size = 18 };
    private readonly Label _name = new() { FontFamily = "OpenSansSemibold", FontSize = 13, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _count = new() { FontSize = 12 };
    private readonly Border _official;

    public ServerGroupHeader()
    {
        _chevron.Color = Rtl.Res("Text3");
        _name.TextColor = Rtl.Res("Text2");
        _count.TextColor = Rtl.Res("Text3");
        _official = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 7 },
            StrokeThickness = 0,
            Padding = new Thickness(6, 1),
            BackgroundColor = Rtl.Res("PrimarySoft"),
            VerticalOptions = LayoutOptions.Center,
            Content = new Label { FontSize = 10, TextColor = Rtl.Res("Primary2"), FontFamily = "OpenSansSemibold" }
                .Also(l => l.SetBinding(Label.TextProperty, new Binding("[Badge_Official]", source: Localization.L.Instance))),
        };
        // The name takes the remaining width and is shortened; badge, count and chevron always stay visible.
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 8, Padding = new Thickness(4, 14, 4, 8) };
        _name.MaxLines = 1;
        _name.VerticalOptions = LayoutOptions.Center;
        grid.Add(_name, 0, 0);
        grid.Add(_official, 1, 0);
        grid.Add(_count, 2, 0);
        grid.Add(_chevron, 3, 0);
        _count.HorizontalOptions = LayoutOptions.End;
        _count.VerticalOptions = LayoutOptions.Center;
        Content = grid;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (BindingContext is ServerGroup g)
            {
                g.Toggle();
                _ = _chevron.RotateToAsync(g.IsExpanded ? 0 : (Localization.L.IsPersian ? 90 : -90), Motion.Ms(380), Motion.Spring);
            }
        };
        GestureRecognizers.Add(tap);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (BindingContext is ServerGroup g)
        {
            _name.Text = g.Name;
            _count.Text = g.CountText;
            _official.IsVisible = g.IsOfficial;
            _chevron.Rotation = g.IsExpanded ? 0 : (Localization.L.IsPersian ? 90 : -90);
        }
    }
}
