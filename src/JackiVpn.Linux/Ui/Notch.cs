using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using JackiVpn.Application.Media;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Visuals;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// The Linux notch, like the Windows one: a small black island at the top of the screen with the IBOX eye, the
/// connection state, the live ping and what is playing; hovering grows it smoothly into a card with the server, the
/// exit, speeds and media buttons. The window itself never moves or resizes on hover (that made it jump): it is as
/// big as the open card and only the black island inside animates; outside the island clicks pass through to the
/// windows below. Drag it along the top of any monitor; it fades out of the way after a few seconds.
/// </summary>
public sealed class Notch : Window
{
    private const double CollapsedHeight = 34;
    private const double ExpandedWidth = 410;
    private const double FadedOpacity = 0.32;
    private static readonly TimeSpan Anim = TimeSpan.FromMilliseconds(240);
    private readonly Shell _shell;
    private readonly Border _pill = new()
    {
        Background = Brushes.Black,
        CornerRadius = new CornerRadius(0, 0, 18, 18),
        ClipToBounds = true,
        Cursor = new Cursor(StandardCursorType.Hand),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly EyeMarkView _eye = new() { Width = 19, Height = 19, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _line = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _ping = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _server = new() { FontSize = 12.5, Foreground = new SolidColorBrush(Color.Parse("#E5E7EB")), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _where = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _speed = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")) };
    private readonly TextBlock _music = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#E5E7EB")), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _playPause;
    private readonly Grid _musicRow;
    private readonly StackPanel _collapsed;
    private readonly StackPanel _expandedView;
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private NotchSettings _settings = new();
    private ConnectionStatus _status = new();
    private NowPlaying? _nowPlaying;
    private bool _expanded;
    private bool _dragging;
    private bool _moved;
    private PixelPoint _grab;
    private double _center = 0.5;
    private string? _display;
    private Size _pillSize;

    public Notch(Shell shell)
    {
        _shell = shell;
        Title = "IBOX notch";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SizeToContent = SizeToContent.Manual;
        Width = ExpandedWidth;
        Height = CollapsedHeight;

        _collapsed = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Height = CollapsedHeight,
            Margin = new Thickness(16, 0, 18, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _eye, _line },
        };
        _playPause = MediaButton("M8 5v14l11-7z", () => _shell.Services.NowPlaying.PlayPauseAsync());
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                MediaButton("M18 6v12 M16 6 6 12l10 6z", () => _shell.Services.NowPlaying.PreviousAsync()),
                _playPause,
                MediaButton("M6 6v12 M8 6l10 6-10 6z", () => _shell.Services.NowPlaying.NextAsync()),
            },
        };
        _musicRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
        var note = Icon("M9 18V5l11-2v13 M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0z M20 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z", 14, Color.Parse("#7DB8FF"), 1.8);
        note.Margin = new Thickness(0, 0, 8, 0);
        _musicRow.Children.Add(note);
        Grid.SetColumn(_music, 1);
        _musicRow.Children.Add(_music);
        Grid.SetColumn(controls, 2);
        _musicRow.Children.Add(controls);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _title } };
        Grid.SetColumn(_ping, 1);
        head.Children.Add(_ping);
        _expandedView = new StackPanel
        {
            Width = ExpandedWidth - 44,
            Margin = new Thickness(22, 14, 22, 16),
            Spacing = 2,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { head, _server, _where, _speed, _musicRow },
        };

        // Size, corners and the cross-fade of the two faces animate on the compositor's clock (smooth, no timers).
        var easing = new CubicEaseOut();
        _pill.Transitions =
        [
            new DoubleTransition { Property = WidthProperty, Duration = Anim, Easing = easing },
            new DoubleTransition { Property = HeightProperty, Duration = Anim, Easing = easing },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(450) },
            new CornerRadiusTransition { Property = Avalonia.Controls.Border.CornerRadiusProperty, Duration = Anim, Easing = easing },
        ];
        _collapsed.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }];
        _expandedView.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200), Delay = TimeSpan.FromMilliseconds(70) }];
        _pill.Child = new Panel { Children = { _collapsed, _expandedView } };
        Content = new Panel { Background = Brushes.Transparent, Children = { _pill } };

        _pill.PointerEntered += (_, _) => Hover(true);
        _pill.PointerExited += (_, _) => Hover(false);
        _pill.PointerPressed += OnPressed;
        _pill.PointerMoved += OnMoved;
        _pill.PointerReleased += OnReleased;
        _leave.Tick += (_, _) =>
        {
            _leave.Stop();
            if (!_pill.IsPointerOver && !_dragging)
            {
                SetExpanded(false);
                RestartFade();
            }
        };
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            if (!_pill.IsPointerOver)
            {
                _pill.Opacity = FadedOpacity;
                _eye.Resting = true;
            }
        };
        var menu = new ContextMenu();
        var toggle = new MenuItem { Header = "Connect / disconnect" };
        toggle.Click += (_, _) => _ = _shell.Dashboard.ToggleAsync();
        var open = new MenuItem { Header = "Open IBOX" };
        open.Click += (_, _) => ShowMain();
        var hide = new MenuItem { Header = "Hide the notch" };
        hide.Click += (_, _) => _shell.Services.Settings.Update(s => s with { Ui = s.Ui with { Notch = s.Ui.Notch with { Enabled = false } } });
        menu.ItemsSource = new[] { toggle, open, hide };
        _pill.ContextMenu = menu;
        Opened += (_, _) =>
        {
            Resize(animate: false);
            Place();
            RestartFade();
        };
    }

    public void Apply(NotchSettings settings)
    {
        _settings = settings;
        _fade.Interval = TimeSpan.FromSeconds(Math.Max(1, settings.FadeAfterSeconds));
        if (!_dragging && (_center != settings.Position || _display != settings.Display))
        {
            _center = settings.Position;
            _display = settings.Display;
            Place();
        }

        Render();
    }

    public void Update(ConnectionStatus status)
    {
        _status = status;
        Render();
    }

    public void SetNowPlaying(NowPlaying? item)
    {
        _nowPlaying = item;
        Render();
    }

    private bool ShowsMusic => _settings.ShowNowPlaying && _nowPlaying is not null;

    private void Render()
    {
        var st = _status;
        var connected = st.State == ConnectionState.Connected;
        var busy = !ConnectionStateRules.IsIdle(st.State) && !connected;
        var stateColor = connected ? Color.Parse("#7DB8FF") : st.State == ConnectionState.Faulted ? Color.Parse("#F87171") : busy ? Color.Parse("#7DB8FF") : Color.Parse("#9CA3AF");
        var stateText = st.State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Disconnected => "Not connected",
            ConnectionState.Faulted => "Failed",
            ConnectionState.Reconnecting => "Reconnecting…",
            ConnectionState.Disconnecting => "Stopping…",
            _ => "Connecting…",
        };
        _eye.State = connected ? EyeState.Awake : busy ? EyeState.Seeking : EyeState.Asleep;
        var pingColor = st.LatencyMs switch { < 150 => Color.Parse("#4ADE80"), < 300 => Color.Parse("#FBBF24"), _ => Color.Parse("#F87171") };
        var s = _settings;
        _line.Inlines?.Clear();
        _line.Inlines ??= [];
        void Part(string text, Color color)
        {
            if (_line.Inlines!.Count > 0)
            {
                _line.Inlines.Add(new Avalonia.Controls.Documents.Run("  ·  ") { Foreground = new SolidColorBrush(Color.Parse("#6B7280")) });
            }

            _line.Inlines.Add(new Avalonia.Controls.Documents.Run(text) { Foreground = new SolidColorBrush(color) });
        }

        if (s.ShowStatus || !connected)
        {
            Part(stateText, stateColor);
        }

        if (connected)
        {
            if (s.ShowPing && st.LatencyMs is { } ms)
            {
                Part($"{ms:0} ms", pingColor);
            }

            if (s.ShowTunnel)
            {
                Part(st.Mode == ConnectionMode.Tun ? "TUN" : "Proxy", Colors.White);
            }

            if (s.ShowServer && st.ProfileName is { } name)
            {
                Part(name.Length > 26 ? name[..25] + "…" : name, Colors.White);
            }

            if (s.ShowCountry && (st.ExitCountryName ?? st.ExitCountryCode) is { } country)
            {
                Part(country, Colors.White);
            }

            if (s.ShowDuration && st.ConnectedSinceUtc is { } since)
            {
                Part(Duration(DateTimeOffset.UtcNow - since), Colors.White);
            }
        }

        if (ShowsMusic && _nowPlaying is { IsPlaying: true } playing)
        {
            Part("♪ " + playing.Line(26), Color.Parse("#C7D2FE"));
        }

        if (_line.Inlines!.Count == 0)
        {
            Part(stateText, stateColor);
        }

        _title.Inlines?.Clear();
        _title.Inlines ??= [];
        _title.Inlines.Add(new Avalonia.Controls.Documents.Run("IBOX  ") { Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")), FontSize = 11 });
        _title.Inlines.Add(new Avalonia.Controls.Documents.Run(stateText) { Foreground = new SolidColorBrush(stateColor) });
        _ping.Text = connected && st.LatencyMs is { } p ? $"{p:0} ms" : "—";
        _ping.Foreground = new SolidColorBrush(connected && st.LatencyMs is not null ? pingColor : Color.Parse("#6B7280"));
        _server.Text = connected ? st.ProfileName ?? "—" : st.State == ConnectionState.Faulted ? st.LastError ?? "Connection failed" : "Click to open IBOX";
        _where.Text = connected
            ? string.Join("  ·  ", new[]
            {
                st.ExitCountryName ?? st.ExitCountryCode,
                st.ConnectedSinceUtc is { } t ? Duration(DateTimeOffset.UtcNow - t) : null,
                st.Mode == ConnectionMode.Tun ? "TUN · all apps" : "System proxy",
            }.Where(x => !string.IsNullOrEmpty(x)))
            : string.Empty;
        _speed.Text = connected && st.Traffic is { } tr ? $"↓ {Speed(tr.DownloadBytesPerSecond)}    ↑ {Speed(tr.UploadBytesPerSecond)}" : string.Empty;
        _where.IsVisible = !string.IsNullOrEmpty(_where.Text);
        _speed.IsVisible = !string.IsNullOrEmpty(_speed.Text);
        _musicRow.IsVisible = ShowsMusic;
        if (_nowPlaying is { } np)
        {
            _music.Text = np.Line(48);
            _playPause.Content = Icon(np.IsPlaying ? "M8 5v14 M16 5v14" : "M8 5v14l11-7z", 11, Colors.White, 2.2);
        }

        Dispatcher.UIThread.Post(() => Resize(animate: true), DispatcherPriority.Background);
    }

    private static string Duration(TimeSpan d) => d.TotalHours >= 1 ? d.ToString(@"h\:mm", CultureInfo.InvariantCulture) + " h" : $"{(int)d.TotalMinutes} min";

    private Button MediaButton(string glyph, Func<Task> action)
    {
        var b = new Button
        {
            Width = 26,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = Icon(glyph, 11, Colors.White, 2.2),
        };
        b.Click += (_, e) =>
        {
            e.Handled = true;
            _ = action();
        };
        return b;
    }

    /// <summary>
    /// The island's size for the current face. The window keeps the open card's size (only its height follows the
    /// content, growing downwards), so nothing moves on screen while the island animates inside it.
    /// </summary>
    private void Resize(bool animate)
    {
        _collapsed.Measure(Size.Infinity);
        _expandedView.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        var open = new Size(ExpandedWidth, Math.Ceiling(_expandedView.DesiredSize.Height));
        var shut = new Size(Math.Min(ExpandedWidth, Math.Ceiling(_collapsed.DesiredSize.Width)), CollapsedHeight);
        var target = _expanded ? open : shut;
        if (Math.Abs(Height - open.Height) > 0.5)
        {
            Height = open.Height; // grows downwards from the top edge: nothing visible moves
        }

        if (target != _pillSize)
        {
            var transitions = _pill.Transitions;
            if (!animate)
            {
                _pill.Transitions = null;
            }

            _pill.Width = target.Width;
            _pill.Height = target.Height;
            _pill.Transitions = transitions;
            _pillSize = target;
            if (animate)
            {
                // A shrinking island gives the pointer back to the windows below once it has settled.
                DispatcherTimer.RunOnce(UpdateInputShape, Anim + TimeSpan.FromMilliseconds(60));
            }
        }

        UpdateInputShape();
    }

    /// <summary>The monitor it sits on: the saved one if it is still there, else the main one.</summary>
    private Screen? Monitor()
    {
        if (_display is { } key && Screens.All.FirstOrDefault(s => Key(s) == key) is { } saved)
        {
            return saved;
        }

        return Screens.Primary ?? Screens.All.FirstOrDefault();
    }

    private static string Key(Screen s) => string.Create(CultureInfo.InvariantCulture, $"{s.Bounds.X},{s.Bounds.Y}");

    /// <summary>
    /// At the top of the monitor's work area (below a desktop's top bar: GNOME pushes windows out of it, which made
    /// the notch jump), at the saved horizontal position.
    /// </summary>
    private void Place()
    {
        if (Monitor() is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = ExpandedWidth * screen.Scaling;
        var x = area.X + ((area.Width - width) * Math.Clamp(_center, 0, 1));
        var target = new PixelPoint((int)Math.Round(x), area.Y);
        if (Position != target)
        {
            Position = target;
        }
    }

    private void Hover(bool inside)
    {
        if (_dragging)
        {
            return;
        }

        if (inside)
        {
            _leave.Stop();
            _fade.Stop();
            _pill.Opacity = 1;
            _eye.Resting = false;
            _eye.Wake();
            SetExpanded(true);
        }
        else
        {
            // A short grace period: the edge of a growing island must not flicker it shut.
            _leave.Stop();
            _leave.Start();
        }
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        _expanded = expanded;
        _collapsed.Opacity = expanded ? 0 : 1;
        _expandedView.Opacity = expanded ? 1 : 0;
        _expandedView.IsHitTestVisible = expanded;
        _pill.CornerRadius = new CornerRadius(0, 0, expanded ? 22 : 18, expanded ? 22 : 18);
        Resize(animate: true);
    }

    private void RestartFade()
    {
        _fade.Stop();
        if (_settings.FadeAfterSeconds > 0)
        {
            _fade.Start();
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_pill).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _moved = false;
        var at = this.PointToScreen(e.GetPosition(this));
        _grab = new PixelPoint(at.X - Position.X, at.Y - Position.Y);
        e.Pointer.Capture(_pill);
    }

    /// <summary>Dragging: along the top of whichever monitor the pointer is on.</summary>
    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var at = this.PointToScreen(e.GetPosition(this));
        if (!_moved && Math.Abs(at.X - _grab.X - Position.X) < 5 && Math.Abs(at.Y - _grab.Y - Position.Y) < 5)
        {
            return;
        }

        _moved = true;
        if ((Screens.ScreenFromPoint(at) ?? Monitor()) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = ExpandedWidth * screen.Scaling;
        var span = Math.Max(1, area.Width - width);
        _center = Math.Clamp((at.X - _grab.X - area.X) / span, 0, 1);
        _display = Key(screen);
        Place();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        var wasDrag = _moved;
        _dragging = false;
        _moved = false;
        if (wasDrag)
        {
            var (at, display) = (_center, _display);
            _shell.Services.Settings.Update(s => s with { Ui = s.Ui with { Notch = s.Ui.Notch with { Position = at, Display = display } } });
        }
        else if (e.InitialPressMouseButton == MouseButton.Left)
        {
            ShowMain();
        }

        if (!_pill.IsPointerOver)
        {
            Hover(false);
        }
    }

    private void ShowMain()
    {
        _shell.Show();
        _shell.WindowState = WindowState.Normal;
        _shell.Activate();
    }

    /// <summary>
    /// X11 (and XWayland): only the island takes the mouse; the transparent rest of the window lets clicks through
    /// to whatever is below. Without the X shape extension the whole window simply stays clickable.
    /// </summary>
    private void UpdateInputShape()
    {
        if (!OperatingSystem.IsLinux() || TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle)
        {
            return;
        }

        var scale = RenderScaling;
        var w = (int)Math.Ceiling(Math.Max(_pillSize.Width, _pill.Bounds.Width) * scale);
        var h = (int)Math.Ceiling(Math.Max(_pillSize.Height, _pill.Bounds.Height) * scale);
        var x = (int)Math.Floor(((ExpandedWidth * scale) - w) / 2);
        X11Shape.SetInput(handle.Handle, x, 0, w, h);
    }

    private static class X11Shape
    {
        private static IntPtr _display;
        private static bool _broken;

        [StructLayout(LayoutKind.Sequential)]
        private struct XRectangle
        {
            public short X;
            public short Y;
            public ushort Width;
            public ushort Height;
        }

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr name);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(IntPtr display);

        [DllImport("libXext.so.6")]
        private static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOff, int yOff, XRectangle[] rectangles, int count, int op, int ordering);

        public static void SetInput(IntPtr window, int x, int y, int width, int height)
        {
            if (_broken)
            {
                return;
            }

            try
            {
                if (_display == IntPtr.Zero)
                {
                    _display = XOpenDisplay(IntPtr.Zero);
                    if (_display == IntPtr.Zero)
                    {
                        _broken = true;
                        return;
                    }
                }

                // ShapeInput = 2, ShapeSet = 0, Unsorted = 0.
                var rect = new XRectangle { X = (short)x, Y = (short)y, Width = (ushort)Math.Max(1, width), Height = (ushort)Math.Max(1, height) };
                XShapeCombineRectangles(_display, window, 2, 0, 0, [rect], 1, 0, 0);
                XFlush(_display);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _broken = true;
            }
        }
    }
}
