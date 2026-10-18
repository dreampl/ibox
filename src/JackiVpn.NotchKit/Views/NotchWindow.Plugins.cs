using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JackiVpn.Domain.Bar;

namespace JackiVpn.Desktop.Views;

/// <summary>A plugin's dashboard as IBAR shows it in the open island.</summary>
public sealed record PluginBoard(string PluginId, string Name, ImageSource? Icon, IReadOnlyList<PluginWidget> Widgets, IReadOnlyList<(string Id, string Title)> Buttons);

/// <summary>
/// What IBAR adds on top of the shared island: whether the connection is shown at all (only while IBOX runs), an
/// extra details line (usage, traffic mode, Dual VPN, network), extra menu items, and plugins — status chips that
/// come out of the island's left side and small dashboards in the open island.
/// </summary>
public sealed partial class NotchWindow
{
    private const double PluginBoardHeight = 64;
    private readonly TextBlock _extra = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _boards = new() { Width = ExpandedWidth - 40 };
    private readonly List<Satellite> _chips = [];
    private IReadOnlyList<PluginBoard> _boardList = [];

    /// <summary>The name in the details title ("IBOX", or "IBAR").</summary>
    public string Brand { get; set; } = "IBOX";

    /// <summary>False while there is no VPN to show (IBAR without IBOX): the island shows the rest only.</summary>
    public bool ConnectionVisible
    {
        get => _connectionVisible;
        set
        {
            if (_connectionVisible != value)
            {
                _connectionVisible = value;
                _shownKey = null;
                if (IsLoaded)
                {
                    Render();
                }
            }
        }
    }

    private bool _connectionVisible = true;

    /// <summary>The second details line when the connection is hidden.</summary>
    public string IdleText { get; set; } = "IBOX is not running";

    /// <summary>One more details line (IBAR: today's usage, traffic mode, Dual VPN, network).</summary>
    public string? ExtraLine
    {
        get => _extraLine;
        set
        {
            if (_extraLine != value)
            {
                _extraLine = value;
                _shownKey = null;
            }
        }
    }

    private string? _extraLine;

    /// <summary>Menu items added to the island's right-click menu each time it opens.</summary>
    public Func<IEnumerable<MenuItem>>? MenuExtras { get; set; }

    /// <summary>A plugin button was pressed (plugin id, action id).</summary>
    public event EventHandler<(string Plugin, string Action)>? PluginAction;

    private double PluginRowHeight => _boardList.Count == 0 ? 0 : (_boardList.Count * PluginBoardHeight) + 4;

    /// <summary>Plugin status chips: coloured circles with a number, out of the island's left side.</summary>
    public void SetChips(IReadOnlyList<(Color Color, string Text)> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);
        foreach (var c in _chips)
        {
            _body.Satellites.Remove(c);
        }

        _chips.Clear();
        for (var i = 0; i < Math.Min(chips.Count, 4); i++)
        {
            var brush = new SolidColorBrush(chips[i].Color);
            brush.Freeze();
            var chip = new Satellite { Left = true, Out = 14 + (i * 21), Radius = 8, Color = brush, Text = chips[i].Text, Y = (CollapsedHeight / 2) - 0.5 };
            _chips.Add(chip);
            _body.Satellites.Add(chip);
        }

        _body.Refresh();
    }

    /// <summary>Plugin dashboards in the open island (below the shelf).</summary>
    public void SetBoards(IReadOnlyList<PluginBoard> boards)
    {
        ArgumentNullException.ThrowIfNull(boards);
        _boardList = boards.Take(2).ToList();
        _boards.Children.Clear();
        foreach (var board in _boardList)
        {
            _boards.Children.Add(Board(board));
        }

        if (!_main.Children.Contains(_boards))
        {
            Canvas.SetLeft(_boards, 20);
            _main.Children.Add(_boards);
        }

        Render();
    }

    private void PlaceBoards(double expansion)
    {
        Canvas.SetTop(_boards, DetailsArea + (ShowsShelf ? ShelfRowHeight : 0) - 4);
        var p = Math.Clamp((expansion - 0.6) / 0.4, 0, 1);
        _boards.Opacity = p * p * (3 - (2 * p));
        _boards.IsHitTestVisible = p > 0.5;
    }

    private FrameworkElement Board(PluginBoard board)
    {
        var row = new DockPanel { Height = PluginBoardHeight - 6, Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var head = new StackPanel { Width = 74, VerticalAlignment = VerticalAlignment.Center };
        if (board.Icon is { } icon)
        {
            head.Children.Add(new Image { Source = icon, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left });
        }

        head.Children.Add(new TextBlock { Text = board.Name, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
        DockPanel.SetDock(head, Dock.Left);
        row.Children.Add(head);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (id, title) in board.Buttons.Take(3))
        {
            var b = PanelButton(title, Color.FromRgb(0x2A, 0x2F, 0x3A), Colors.White, () => PluginAction?.Invoke(this, (board.PluginId, id)), title);
            b.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(b);
        }

        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var widgets = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var w in board.Widgets.Where(BarPlugins.IsValid).Take(3))
        {
            widgets.Children.Add(Widget(w));
        }

        row.Children.Add(widgets);
        return row;
    }

    /// <summary>The four widget kinds, drawn the same way for every plugin (no plugin decides how anything looks).</summary>
    private static FrameworkElement Widget(PluginWidget w)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 120 };
        if (w.Label is { } label)
        {
            box.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        switch (w.Kind)
        {
            case "number":
                box.Children.Add(new TextBlock { Text = w.Value, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                break;
            case "bar":
                var track = new Grid { Width = 80, Height = 6, Margin = new Thickness(0, 5, 0, 0) };
                track.Children.Add(new Rectangle { RadiusX = 3, RadiusY = 3, Fill = TileBrush });
                track.Children.Add(new Rectangle { RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(Color.FromRgb(0x5A, 0xAC, 0xFF)), Width = 80 * Math.Clamp(w.Fraction ?? 0, 0, 1), HorizontalAlignment = HorizontalAlignment.Left });
                box.Children.Add(track);
                break;
            case "list":
                foreach (var item in w.Items ?? [])
                {
                    box.Children.Add(new TextBlock { Text = item, FontSize = 10.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                }

                break;
            case "spark":
                var values = w.Values ?? [];
                double min = values.Min(), max = values.Max(), span = Math.Max(1e-9, max - min);
                var line = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x5A, 0xAC, 0xFF)), StrokeThickness = 1.5, Margin = new Thickness(0, 4, 0, 0) };
                for (var i = 0; i < values.Count; i++)
                {
                    line.Points.Add(new Point(i * 90.0 / (values.Count - 1), 22 - ((values[i] - min) / span * 22)));
                }

                box.Children.Add(new Canvas { Width = 90, Height = 24, Children = { line } });
                break;
        }

        return box;
    }

    internal static string Short(double v) => v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
}
