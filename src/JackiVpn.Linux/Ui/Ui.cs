using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace JackiVpn.Linux.Ui;

/// <summary>The Windows app's token set (see <see cref="Look"/>) and the building blocks of every page.</summary>
public static class Ui
{
    public static Color Window => Look.P.Window;
    public static Color Sidebar => Look.P.Sidebar;
    public static Color CardBg => Look.P.Card;
    public static Color CardHover => Look.P.CardHover;
    public static Color InputBg => Look.P.Input;
    public static Color TileBg => Look.P.Tile;
    public static Color Border => Look.P.Border;
    public static Color Text => Look.P.Text;
    public static Color Text2 => Look.P.TextSecondary;
    public static Color Muted => Look.P.TextMuted;
    public static Color Accent => Look.P.Accent;
    public static Color Accent2 => Look.P.Accent2;
    public static Color Success => Look.P.Success;
    public static Color Success2 => Look.P.Success2;
    public static Color Warning => Look.P.Warning;
    public static Color Danger => Look.P.Danger;
    public static Color Idle => Look.P.Idle;
    public static Color Idle2 => Look.P.Idle2;
    public static Color Chart1 => Look.P.Chart1;
    public static Color Chart2 => Look.P.Chart2;
    public static readonly Color Gaming = Color.Parse("#E040FB");
    public static readonly Color Streaming = Color.Parse("#38BDF8");
    public static readonly Color Violet = Color.Parse("#8B5CF6");

    public static IBrush B(Color c) => new SolidColorBrush(c);

    public static IBrush B(Color c, double alpha) => new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));

    public static LinearGradientBrush Gradient(Color a, Color b) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    public static LinearGradientBrush AccentGradient => new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Accent, 0), new GradientStop(Look.P.IsLight ? Accent : Accent2, 1) },
    };

    /// <summary>Card fill: the surface with a hair of depth (lighter at the top).</summary>
    public static LinearGradientBrush CardFill => new()
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Look.P.Card, 0), new GradientStop(Look.P.Card2, 1) },
    };

    /// <summary>
    /// Clay shadows: a soft dark shadow below, a faint highlight above (light theme: a white glow up-left, as in the
    /// mockup), and a one-pixel inner top highlight. Never neon.
    /// </summary>
    public static BoxShadows ClayShadow(bool raised = false)
    {
        var p = Look.P;
        var dark = new BoxShadow { OffsetY = raised ? 14 : 10, Blur = raised ? 36 : 28, Spread = -4, Color = WithAlpha(p.Shadow, p.ShadowOpacity * (raised ? 0.9 : 0.7)) };
        var light = p.IsLight
            ? new BoxShadow { OffsetX = -6, OffsetY = -6, Blur = 18, Color = WithAlpha(p.ShadowLight, p.ShadowLightOpacity) }
            : new BoxShadow { OffsetY = -1, Blur = 2, Color = WithAlpha(p.ShadowLight, p.ShadowLightOpacity) };
        var rim = new BoxShadow { IsInset = true, OffsetY = 1, Blur = 0, Color = p.Highlight };
        return new BoxShadows(dark, [light, rim]);
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>A clay card (matte surface, soft shadows, rounded 24).</summary>
    public static Border Clay(Control child, Thickness? padding = null, double radius = 24, bool raised = false) => new()
    {
        Background = CardFill,
        BorderBrush = B(Border, Look.P.IsLight ? 0.9 : 0.55),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding ?? new Thickness(22, 20),
        BoxShadow = ClayShadow(raised),
        Child = child,
    };

    /// <summary>The square rounded icon tile of the card titles and mode tiles.</summary>
    public static Border IconTile(string glyph, Color? color = null, double size = 42, double icon = 19) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size * 0.34),
        Background = B(TileBg),
        BorderBrush = B(Border, 0.7),
        BorderThickness = new Thickness(1),
        BoxShadow = new BoxShadows(new BoxShadow { IsInset = true, OffsetY = 1, Color = Look.P.Highlight }),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Icon(glyph, icon, color ?? Accent, 2),
    };

    /// <summary>Card title: icon tile + title, optional element on the right.</summary>
    public static DockPanel CardTitle(string glyph, string title, Control? right = null, Color? color = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            right.VerticalAlignment = VerticalAlignment.Center;
            d.Children.Add(right);
        }

        var tile = IconTile(glyph, color);
        DockPanel.SetDock(tile, Dock.Left);
        d.Children.Add(tile);
        d.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = B(Text), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
        return d;
    }

    /// <summary>A pill (status, quota): rounded, raised a little.</summary>
    public static Border Pill(Control child) => new()
    {
        Background = CardFill,
        BorderBrush = B(Border, 0.7),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(22),
        Padding = new Thickness(16, 9),
        BoxShadow = ClayShadow(),
        Child = child,
    };

    /// <summary>Round clay icon button (top bar).</summary>
    public static Button RoundButton(string glyph, Action onClick, string tip)
    {
        var b = new Button
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Background = CardFill,
            BorderBrush = B(Border, 0.7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = Icon(glyph, 19, Text2, 1.8),
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Outline button (secondary full-width actions such as Switch Server).</summary>
    public static Button Outline(string text, Action onClick, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        if (glyph is not null)
        {
            content.Children.Add(Icon(glyph, 15, Text, 1.8));
        }

        content.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = B(Text), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Background = B(TileBg),
            BorderBrush = B(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(16, 11),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBlock T(string text, double size = 13, FontWeight weight = FontWeight.Normal, Color? color = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = B(color ?? Text),
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Caption(string text) => T(text, 12, color: Text2);

    public static TextBlock Hint(string text) => T(text, 11.5, color: Muted);

    /// <summary>Page title and its one-line description, as on every Windows page.</summary>
    public static StackPanel Header(string title, string? description = null, Control? actions = null)
    {
        var top = new DockPanel { LastChildFill = true };
        if (actions is not null)
        {
            DockPanel.SetDock(actions, Dock.Right);
            top.Children.Add(actions);
        }

        // The window's top bar shows the page title: the page itself starts with its description and actions.
        top.Children.Add(description is null ? new Control() : T(description, 13.5, color: Text2));
        return new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 18), Children = { top } };
    }

    public static Border Card(Control child, Thickness? padding = null) => Clay(child, padding ?? new Thickness(22, 18), 22);

    /// <summary>A titled card holding settings rows.</summary>
    public static Border Section(string title, string? hint, params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 14 };
        var head = new StackPanel { Spacing = 2, Children = { T(title, 16, FontWeight.SemiBold) } };
        if (hint is not null)
        {
            head.Children.Add(Hint(hint));
        }

        panel.Children.Add(head);
        foreach (var r in rows)
        {
            panel.Children.Add(r);
        }

        return Card(panel);
    }

    /// <summary>Label (and hint) on the left, control on the right.</summary>
    public static Grid Row(string label, string? hint, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { T(label) } };
        if (hint is not null)
        {
            text.Children.Add(Hint(hint));
        }

        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    public static Button Primary(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Background = AccentGradient,
            Foreground = Brushes.White,
            Padding = new Thickness(18, 8),
            CornerRadius = new CornerRadius(20),
            FontWeight = FontWeight.SemiBold,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Ghost(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Background = B(TileBg),
            BorderBrush = B(Border),
            BorderThickness = new Thickness(1),
            Foreground = B(Text),
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(18),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static ToggleSwitch Toggle(bool value, Action<bool> changed)
    {
        var t = new ToggleSwitch { IsChecked = value, OnContent = null, OffContent = null, MinWidth = 0 };
        t.IsCheckedChanged += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    public static ComboBox Combo<T>(IReadOnlyList<(T Value, string Label)> items, T selected, Action<T> changed)
    {
        var c = new ComboBox { ItemsSource = items.Select(i => i.Label).ToList(), MinWidth = 190 };
        c.SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedIndex >= 0)
            {
                changed(items[c.SelectedIndex].Value);
            }
        };
        return c;
    }

    public static ComboBox EnumCombo<TEnum>(TEnum selected, Action<TEnum> changed)
        where TEnum : struct, Enum =>
        Combo(Enum.GetValues<TEnum>().Select(v => (v, Spaced(v.ToString()))).ToList(), selected, changed);

    public static NumericUpDown Number(double value, double min, double max, Action<double> changed, double step = 1)
    {
        var n = new NumericUpDown
        {
            Value = (decimal)value,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)step,
            MinWidth = 150,
            FormatString = step < 1 ? "0.##" : "0",
        };
        n.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                changed((double)v);
            }
        };
        return n;
    }

    public static TextBox Input(string? text, Action<string> changed, string? placeholder = null, double width = 260)
    {
        var t = new TextBox { Text = text ?? string.Empty, Width = width, PlaceholderText = placeholder, CornerRadius = new CornerRadius(12) };
        t.LostFocus += (_, _) => changed(t.Text ?? string.Empty);
        return t;
    }

    public static Border Badge(string text, Color color) => new()
    {
        Background = B(color, 0.16),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(9, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = B(color) },
    };

    /// <summary>A 24×24 stroked icon (paths in the style of the Windows app's line icons).</summary>
    public static Viewbox Icon(string data, double size = 18, Color? color = null, double stroke = 2) => new()
    {
        Width = size,
        Height = size,
        IsHitTestVisible = false,
        Child = new Canvas
        {
            Width = 24,
            Height = 24,
            Children =
            {
                new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(data),
                    Stroke = B(color ?? Text),
                    StrokeThickness = stroke,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                },
            },
        },
    };

    public static class Icons
    {
        public const string Home = "M3 11 12 4l9 7 M5 10v10h5v-6h4v6h5V10";
        public const string Servers = "M4 5h16v5H4z M4 14h16v5H4z M8 7.5h.01 M8 16.5h.01";
        public const string Subscriptions = "M12 3 3 8l9 5 9-5-9-5z M3 13l9 5 9-5 M3 17.5l9 5 9-5";
        public const string Routing = "M6 3v12 M6 15a3 3 0 1 0 0 6 3 3 0 0 0 0-6z M18 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6z M18 9c0 6-12 3-12 9";
        public const string Dns = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M3 12h18 M12 3c3 3 3 15 0 18 M12 3c-3 3-3 15 0 18";
        public const string Network = "M2 9a15 15 0 0 1 20 0 M5 13a10 10 0 0 1 14 0 M8.5 16.5a5 5 0 0 1 7 0 M12 20h.01";
        public const string Advanced = "M4 6h10 M18 6h2 M4 12h4 M12 12h8 M4 18h12 M20 18h0 M16 4v4 M10 10v4 M18 16v4";
        public const string Settings = "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M12 2v3 M12 19v3 M4.2 4.2l2.1 2.1 M17.7 17.7l2.1 2.1 M2 12h3 M19 12h3 M4.2 19.8l2.1-2.1 M17.7 6.3l2.1-2.1";
        public const string Logs = "M5 3h10l4 4v14H5z M14 3v5h5 M8 12h8 M8 16h8";
        public const string About = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 11v6 M12 7.5h.01";
        public const string Power = "M12 3v9 M18.4 6.6a9 9 0 1 1-12.8 0";
        public const string Star = "M12 3l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.3 6.4 20.2l1.1-6.2L3 9.6l6.2-.9z";
        public const string More = "M5 12h.01 M12 12h.01 M19 12h.01";
        public const string Down = "M12 4v14 M6 12l6 6 6-6";
        public const string Up = "M12 20V6 M6 12l6-6 6 6";
        public const string Clock = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z M12 7v5l3 2";
        public const string Chevron = "M6 9l6 6 6-6";
        public const string ChevronRight = "M9 6l6 6-6 6";
        public const string Bell = "M6 16V11a6 6 0 0 1 12 0v5l2 2H4z M10 20a2 2 0 0 0 4 0";
        public const string Globe = Dns;
        public const string Shield = "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z";
        public const string ShieldCheck = "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z M8.5 12l2.5 2.5 4.5-5";
        public const string Bolt = "M13 2 4 14h7l-1 8 9-12h-7z";
        public const string Pulse = "M3 12h4l2-5 4 10 2-5h6";
        public const string Gauge = "M12 3a9 9 0 0 0-9 9 9 9 0 0 0 2.6 6.4 M12 3a9 9 0 0 1 9 9 9 9 0 0 1-2.6 6.4 M12 12l4-4 M7 12h.01 M17 12h.01 M12 7h.01";
        public const string Apps = "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z";
        public const string Swap = "M4 8h14l-3-3 M20 16H6l3 3";
        public const string Infinity = "M7 9a3 3 0 1 0 0 6c3 0 7-6 10-6a3 3 0 1 1 0 6c-3 0-7-6-10-6z";
        public const string Sun = "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4";
        public const string Moon = "M20 14.5A8 8 0 0 1 9.5 4 8 8 0 1 0 20 14.5z";
        public const string User = "M12 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8z M4 21a8 8 0 0 1 16 0";
        public const string Play = "M7 5v14l11-7z";
        public const string Stats = "M5 20V10 M10 20V4 M15 20v-7 M20 20v-12";
        public const string Usage = "M4 20h16 M7 16v-4 M12 16V8 M17 16v-7";
        public const string Copy = "M9 9h11v11H9z M5 15H4V4h11v1";
        public const string Refresh = "M20 11a8 8 0 0 0-14.9-3 M4 4v4h4 M4 13a8 8 0 0 0 14.9 3 M20 20v-4h-4";
    }

    /// <summary>"IranFriendly" → "Iran friendly".</summary>
    public static string Spaced(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ').Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }

        return sb.ToString();
    }

    public static string Bytes(long? bytes) => bytes switch
    {
        null => "—",
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => $"{bytesPerSecond:0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:0.0} KB/s",
        _ => $"{bytesPerSecond / 1024 / 1024:0.0} MB/s",
    };

    public static string When(DateTimeOffset? at)
    {
        if (at is not { } t)
        {
            return "never";
        }

        var d = DateTimeOffset.UtcNow - t;
        return d.TotalMinutes < 1 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Flag(string? countryCode) =>
        countryCode is { Length: 2 } c && char.IsAsciiLetter(c[0]) && char.IsAsciiLetter(c[1])
            ? char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(c[0]) - 'A') + char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(c[1]) - 'A')
            : "🌐";
}
