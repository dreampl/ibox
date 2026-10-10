using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace JackiVpn.Linux.Ui;

/// <summary>The Windows app's dark theme (graphite surfaces, blue→violet accent) and the building blocks of every page.</summary>
public static class Ui
{
    public static readonly Color Window = Color.Parse("#15171C");
    public static readonly Color Sidebar = Color.Parse("#101216");
    public static readonly Color CardBg = Color.Parse("#1C1F26");
    public static readonly Color CardHover = Color.Parse("#242833");
    public static readonly Color InputBg = Color.Parse("#22262F");
    public static readonly Color Border = Color.Parse("#2C313C");
    public static readonly Color Text = Color.Parse("#E8EAF0");
    public static readonly Color Text2 = Color.Parse("#A6ADBB");
    public static readonly Color Muted = Color.Parse("#6B7280");
    public static readonly Color Accent = Color.Parse("#3B82F6");
    public static readonly Color Accent2 = Color.Parse("#8B5CF6");
    public static readonly Color Success = Color.Parse("#22C55E");
    public static readonly Color Success2 = Color.Parse("#14B8A6");
    public static readonly Color Warning = Color.Parse("#F59E0B");
    public static readonly Color Danger = Color.Parse("#EF4444");
    public static readonly Color Idle = Color.Parse("#3A404D");
    public static readonly Color Idle2 = Color.Parse("#2A2F3A");
    public static readonly Color Gaming = Color.Parse("#E040FB");
    public static readonly Color Streaming = Color.Parse("#38BDF8");

    public static IBrush B(Color c) => new SolidColorBrush(c);

    public static IBrush B(Color c, double alpha) => new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));

    public static LinearGradientBrush Gradient(Color a, Color b) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    public static LinearGradientBrush AccentGradient => Gradient(Accent, Accent2);

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

        top.Children.Add(T(title, 26, FontWeight.SemiBold));
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 18), Children = { top } };
        if (description is not null)
        {
            panel.Children.Add(Caption(description));
        }

        return panel;
    }

    public static Border Card(Control child, Thickness? padding = null) => new()
    {
        Background = B(CardBg),
        BorderBrush = B(Border),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = padding ?? new Thickness(18, 16),
        Child = child,
    };

    /// <summary>A titled card holding settings rows.</summary>
    public static Border Section(string title, string? hint, params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 14 };
        var head = new StackPanel { Spacing = 2, Children = { T(title, 15, FontWeight.SemiBold) } };
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
            Padding = new Thickness(16, 7),
            CornerRadius = new CornerRadius(8),
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
            Background = B(InputBg),
            BorderBrush = B(Border),
            BorderThickness = new Thickness(1),
            Foreground = B(Text),
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(8),
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
        var t = new TextBox { Text = text ?? string.Empty, Width = width, PlaceholderText = placeholder };
        t.LostFocus += (_, _) => changed(t.Text ?? string.Empty);
        return t;
    }

    public static Border Badge(string text, Color color) => new()
    {
        Background = B(color, 0.16),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 1),
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
