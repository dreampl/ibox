using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Subscriptions;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>
/// The connect orb of the mockup: a clay plate, a track ring, and a disc in the state colour (grey off, blue→violet
/// while connecting with a spinner and a thinking orb, green with soft depth when on; magenta gaming, sky streaming).
/// A click while connecting cancels.
/// </summary>
public sealed class ConnectOrb : Grid
{
    private readonly Ellipse _halo = new() { Width = 226, Height = 226, Opacity = 0 };
    private readonly Ellipse _disc = new() { Width = 176, Height = 176 };
    private readonly Ellipse _onRing = new() { Width = 200, Height = 200, StrokeThickness = 2.5, Opacity = 0 };
    private readonly Avalonia.Controls.Shapes.Path _spinner = new()
    {
        Width = 200,
        Height = 200,
        StrokeThickness = 3.5,
        StrokeLineCap = PenLineCap.Round,
        Data = Geometry.Parse("M 100,1.75 A 98.25,98.25 0 0 1 198.25,100"),
        IsVisible = false,
    };
    private readonly RotateTransform _spin = new();
    private readonly TextBlock _caption = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Ellipse _captionDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 9, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    // The IBOX eye, as on Windows: asleep, seeking (thinking orb inside the O) or awake (follows the pointer).
    private readonly EyeMarkView _eye = new() { Width = 60, Height = 61, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScaleTransform _scale = new(1, 1);
    private DispatcherTimer? _anim;
    private (ConnectionState, ActivityProfile)? _shown;

    public ConnectOrb()
    {
        Width = 236;
        Height = 236;
        Cursor = new Cursor(StandardCursorType.Hand);
        _spinner.RenderTransform = _spin;
        var plate = new Border
        {
            Width = 214,
            Height = 214,
            CornerRadius = new CornerRadius(107),
            Background = CardFill,
            BorderBrush = B(Ui.Border),
            BorderThickness = new Thickness(1),
            BoxShadow = ClayShadow(raised: true),
        };
        var track = new Ellipse { Width = 200, Height = 200, Stroke = B(Ui.Divider), StrokeThickness = 2 };
        var depth = new Border { Width = 176, Height = 176, CornerRadius = new CornerRadius(88) };
        var sheen = new Ellipse
        {
            Width = 176,
            Height = 176,
            StrokeThickness = 1.2,
            Stroke = B(Color.Parse("#38FFFFFF")),
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#2EFFFFFF"), 0), new GradientStop(Color.Parse("#00FFFFFF"), 0.5), new GradientStop(Color.Parse("#14000000"), 1) },
            },
        };
        var face = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { _eye, _caption, _captionDot } };
        var root = new Grid { RenderTransform = _scale, Children = { _halo, plate, track, _spinner, _onRing, depth, _disc, sheen, face } };
        Children.Add(root);
        SetState(ConnectionState.Disconnected, ActivityProfile.Normal);
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.PointerMoved += OnPointerMoved;
            }
        };
        DetachedFromVisualTree += (_, e) =>
        {
            if (e.RootVisual is TopLevel top)
            {
                top.PointerMoved -= OnPointerMoved;
            }
        };
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) => _eye.LookAt(e.GetPosition(_eye));

    public void SetState(ConnectionState state, ActivityProfile activity)
    {
        if (_shown == (state, activity))
        {
            return;
        }

        _shown = (state, activity);
        var p = Look.P;
        var connected = state == ConnectionState.Connected;
        var failed = state == ConnectionState.Faulted;
        var busy = !ConnectionStateRules.IsIdle(state) && !connected;
        Color main;
        if (connected)
        {
            var (a, b, c) = activity switch
            {
                ActivityProfile.Gaming => (Color.Parse("#F472B6"), Color.Parse("#DB2777"), Color.Parse("#9D174D")),
                ActivityProfile.Streaming => (Color.Parse("#7DD3FC"), Color.Parse("#0EA5E9"), Color.Parse("#0369A1")),
                _ => (p.OrbOn1, p.OrbOn2, p.OrbOn3),
            };
            main = activity == ActivityProfile.Normal ? p.Success : b;
            _disc.Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.4, 0.3, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.4, 0.3, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops = { new GradientStop(a, 0), new GradientStop(b, 0.6), new GradientStop(c, 1) },
            };
        }
        else if (busy)
        {
            main = p.Accent;
            _disc.Fill = Gradient(p.Accent, p.Accent2);
        }
        else if (failed)
        {
            main = p.Danger;
            _disc.Fill = Gradient(Color.Parse("#F87171"), Color.Parse("#B91C1C"));
        }
        else
        {
            main = p.TextMuted;
            _disc.Fill = Gradient(p.Idle, p.Idle2);
        }

        // Ink on the orb: white, except the pastel mint (light, connected) and the light idle disc.
        var ink = p.IsLight && connected && activity == ActivityProfile.Normal ? Color.Parse("#0E9F6E")
            : p.IsLight && !connected && !busy && !failed ? p.TextSecondary
            : Colors.White;
        _eye.AwakeFrame = ink;
        _eye.State = connected ? Domain.Visuals.EyeState.Awake : busy ? Domain.Visuals.EyeState.Seeking : Domain.Visuals.EyeState.Asleep;
        _halo.Fill = new RadialGradientBrush { GradientStops = { new GradientStop(WithAlpha(main, connected ? 0.45 : 0.25), 0.55), new GradientStop(Colors.Transparent, 1) } };
        _halo.Opacity = connected || busy || failed ? 1 : 0;
        _onRing.Stroke = B(main);
        _onRing.Opacity = connected ? 0.9 : 0;
        _spinner.Stroke = AccentGradient;
        _spinner.IsVisible = busy;
        _caption.Text = connected
            ? activity switch { ActivityProfile.Gaming => "GAMING", ActivityProfile.Streaming => "STREAMING", _ => "CONNECTED" }
            : state == ConnectionState.Disconnecting ? "STOPPING…"
            : busy ? "CANCEL"
            : failed ? "RETRY"
            : "CONNECT";
        _caption.Foreground = B(ink == Colors.White ? Color.Parse("#F2FFFFFF") : ink);
        _captionDot.Fill = _caption.Foreground;
        _anim?.Stop();
        _anim = null;
        _scale.ScaleX = _scale.ScaleY = 1;
        if (busy)
        {
            var start = DateTime.UtcNow;
            _anim = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                var t = (DateTime.UtcNow - start).TotalSeconds;
                // Only the spinner turns: scaling the whole clay orb each frame re-rendered its shadows (slow).
                _spin.Angle = (t * 300) % 360;
            });
            _anim.Start();
        }
        else
        {
            _ = Pop();
        }
    }

    public async Task Pop()
    {
        foreach (var v in new[] { 0.96, 0.98, 1.02, 1.01, 1.0 })
        {
            _scale.ScaleX = _scale.ScaleY = v;
            await Task.Delay(45);
        }
    }
}

public sealed class DashboardPage(Shell shell) : Page(shell)
{
    private const int HistoryLength = 60;
    private readonly Queue<double> _downHistory = new(Enumerable.Repeat(0.0, HistoryLength));
    private readonly Queue<double> _upHistory = new(Enumerable.Repeat(0.0, HistoryLength));
    private ConnectOrb _orb = new();
    private TextBlock _title = new();
    private TextBlock _detail = new();
    private WrapPanel _chips = new();
    private string? _chipKey;
    private TextBlock _ip = new();
    private TextBlock _reason = new();
    private TextBlock _error = new();
    private Border _modeTun = new();
    private Border _modeProxy = new();
    private ToggleSwitch? _smart;
    private FlagView _locationFlag = new();
    private TextBlock _locationTitle = new();
    private TextBlock _locationSub = new();
    private WorldMapView _map = new();
    private Rectangle _glow = new();
    private TextBlock _down = new();
    private TextBlock _up = new();
    private TextBlock _downTotal = new();
    private TextBlock _upTotal = new();
    private TrafficChart _chart = new();
    private SpeedGauge _gauge = new();
    private TextBlock _gaugeValue = new();
    private TextBlock _speedPhase = new();
    private TextBlock _speedDown = new();
    private TextBlock _speedUp = new();
    private TextBlock _speedPing = new();
    private TextBlock _speedNote = new();
    private Button? _speedButton;
    private CancellationTokenSource? _speedCts;
    private Ellipse _serverDot = new();
    private TextBlock _serverState = new();
    private FlagView _serverFlag = new();
    private TextBlock _serverTitle = new();
    private TextBlock _serverSub = new();
    private TextBlock _tileMode = new();
    private TextBlock _tileCoverage = new();
    private TextBlock _tileProtocol = new();
    private Panel _plan = new();
    private ConnectionStatus _status = new();
    private string? _selectedName;
    private string? _selectedCountry;
    private string? _selectedProtocol;
    private DispatcherTimer? _clock;
    private TextBlock _duration = new();

    public override string Title => "Dashboard";

    public override string Glyph => Icons.Home;

    protected override Control Build()
    {
        _orb = new ConnectOrb { VerticalAlignment = VerticalAlignment.Center };
        _orb.PointerPressed += async (_, _) => await ToggleAsync();
        var hero = BuildHero();
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("1.32*,20,*,20,*"), Margin = new Thickness(0, 20, 0, 0) };
        cards.Children.Add(BuildTraffic());
        var speed = BuildSpeed();
        Grid.SetColumn(speed, 2);
        cards.Children.Add(speed);
        var server = BuildServer();
        Grid.SetColumn(server, 4);
        cards.Children.Add(server);
        _plan = new Panel();

        EventHandler<ConnectionStatus> status = (_, st) => Dispatcher.UIThread.Post(() => Apply(st));
        EventHandler dualChanged = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _chipKey = null;
            Apply(_status);
        });
        S.DualVpn.Changed += dualChanged;
        Track(() => S.DualVpn.Changed -= dualChanged);
        S.Orchestrator.StatusChanged += status;
        Track(() => S.Orchestrator.StatusChanged -= status);
        EventHandler<Domain.Settings.AppSettings> settings = (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshSelectionAsync());
        S.Settings.Changed += settings;
        Track(() => S.Settings.Changed -= settings);
        var clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowDuration());
        _clock = clock;
        Track(clock.Stop);
        Apply(S.Orchestrator.Status);
        return new StackPanel { Children = { hero, cards, _plan } };
    }

    public override void OnShown()
    {
        if (_smart is not null)
        {
            _smart.IsChecked = S.Settings.Current.Connection.SmartMode;
        }

        ShowMode();
        _ = RefreshSelectionAsync();
        _ = RefreshPlanAsync();
    }

    // ------------------------------------------------------------------ hero

    private Border BuildHero()
    {
        _glow = new Rectangle { RadiusX = 30, RadiusY = 30, IsHitTestVisible = false };
        _map = new WorldMapView { Width = 330, Height = 138, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 26, 22) };

        _locationFlag = new FlagView { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0) };
        _locationTitle = new TextBlock { FontSize = 15.5, FontWeight = FontWeight.SemiBold, Foreground = B(Text), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        _locationSub = new TextBlock { FontSize = 12, Foreground = B(Text2), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170, Margin = new Thickness(0, 2, 0, 0) };
        var chevron = Icon(Icons.ChevronRight, 14, Text2, 2);
        DockPanel.SetDock(chevron, Dock.Right);
        chevron.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(_locationFlag, Dock.Left);
        var location = Clay(new DockPanel { Children = { chevron, _locationFlag, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _locationTitle, _locationSub } } } }, new Thickness(16, 12, 14, 12), 22);
        location.MinWidth = 250;
        location.Cursor = new Cursor(StandardCursorType.Hand);
        location.HorizontalAlignment = HorizontalAlignment.Right;
        location.VerticalAlignment = VerticalAlignment.Top;
        location.PointerPressed += (_, _) => Shell.Navigate(Shell.Servers);

        _modeTun = ChoiceTile(Icons.Globe, Accent, "TUN", "All apps", () => SetMode(ConnectionMode.Tun));
        _modeProxy = ChoiceTile(Icons.Shield, Success, "System Proxy", "Proxy apps only", () => SetMode(ConnectionMode.SystemProxy));
        _smart = Toggle(S.Settings.Current.Connection.SmartMode, v => S.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = v } }));
        var smartTile = Tile(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { IconTile(Icons.Bolt, Violet, 40, 18), new TextBlock { Text = "Smart Connect", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = B(Text), VerticalAlignment = VerticalAlignment.Center }, _smart },
        });
        ToolTip.SetTip(smartTile, "Test servers and pick the fastest working one; switch automatically when it fails");
        var tiles = new WrapPanel { Margin = new Thickness(0, 4, 270, 0), VerticalAlignment = VerticalAlignment.Top, Children = { _modeTun, _modeProxy, smartTile } };

        _title = new TextBlock { FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = B(Text) };
        _detail = new TextBlock { FontSize = 13, Foreground = B(Muted), Margin = new Thickness(0, 2, 0, 12), TextWrapping = TextWrapping.Wrap };
        _chips = new WrapPanel();
        _chipKey = null;
        _ip = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = 12.5, Foreground = B(Text2), Margin = new Thickness(0, 2, 0, 0) };
        _reason = new TextBlock { FontSize = 12, Foreground = B(Muted), Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _error = new TextBlock { FontSize = 12, Foreground = B(Danger), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        _duration = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = 12, Foreground = B(Text), VerticalAlignment = VerticalAlignment.Center };
        var statusBlock = new StackPanel { Margin = new Thickness(2, 10, 360, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail, _chips, _ip, _reason, _error } };
        Grid.SetRow(statusBlock, 1);

        var middle = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(24, 6, 0, 0), Children = { location, tiles, statusBlock } };
        Grid.SetColumn(middle, 1);
        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(22, 22, 26, 24), Children = { _orb, middle } };
        return Clay(new Grid { MinHeight = 300, Children = { _glow, _map, inner } }, new Thickness(0), 30, raised: true);
    }

    /// <summary>A clay tile holding a choice (TUN / proxy); the chosen one gets an accent rim.</summary>
    private static Border ChoiceTile(string glyph, Color color, string title, string subtitle, Action pick)
    {
        var tile = Tile(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                IconTile(glyph, color, 40, 18),
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = B(Text) },
                        new TextBlock { Text = subtitle, FontSize = 12, Foreground = B(Text2), Margin = new Thickness(0, 2, 0, 0) },
                    },
                },
            },
        });
        tile.Cursor = new Cursor(StandardCursorType.Hand);
        tile.PointerPressed += (_, _) => pick();
        return tile;
    }

    private static Border Tile(Control content)
    {
        var b = Clay(content, new Thickness(12, 10, 20, 10), 20);
        b.Margin = new Thickness(0, 0, 12, 12);
        return b;
    }

    private void SetMode(ConnectionMode mode)
    {
        S.Settings.Update(x => x with { Connection = x.Connection with { Mode = mode } });
        ShowMode();
        if (S.Orchestrator.State == ConnectionState.Connected)
        {
            Shell.Toast("Mode", "Applies the next time you connect.");
        }
    }

    private void ShowMode()
    {
        var tun = S.Settings.Current.Connection.Mode == ConnectionMode.Tun;
        foreach (var (tile, on) in new[] { (_modeTun, tun), (_modeProxy, !tun) })
        {
            tile.BorderBrush = Brushes.Transparent;
            tile.BorderThickness = new Thickness(0);
            tile.Background = on ? B(Accent, Look.P.IsLight ? 0.08 : 0.12) : CardFill;
        }

        _tileMode.Text = tun ? "TUN" : "Proxy";
        _tileCoverage.Text = tun ? "All apps" : "Proxy apps";
    }

    // ------------------------------------------------------------------ cards

    private Border BuildTraffic()
    {
        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Fill = B(Chart1), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Download", FontSize = 12, Foreground = B(Text2), Margin = new Thickness(0, 0, 8, 0) },
                new Ellipse { Width = 8, Height = 8, Fill = B(Chart2), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Upload", FontSize = 12, Foreground = B(Text2) },
            },
        };
        _down = new TextBlock { FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = B(Text), Text = "0 B/s" };
        _up = new TextBlock { FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = B(Text), Text = "0 B/s" };
        _downTotal = new TextBlock { FontSize = 12.5, Foreground = B(Text2), Text = "Download" };
        _upTotal = new TextBlock { FontSize = 12.5, Foreground = B(Text2), Text = "Upload" };
        var figures = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        figures.Children.Add(Figure(Icons.Down, Chart1, _down, _downTotal, 0));
        figures.Children.Add(Figure(Icons.Up, Chart2, _up, _upTotal, 1));
        _chart = new TrafficChart { Height = 150, Margin = new Thickness(0, 18, 0, 0) };
        return Clay(new StackPanel { Children = { CardTitle(Icons.Pulse, "Live Traffic", legend), figures, _chart } }, new Thickness(22, 20), 26);
    }

    private static Grid Figure(string glyph, Color color, TextBlock value, TextBlock caption, int column)
    {
        var icon = Icon(glyph, 22, color, 2.2);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 4, 12, 0);
        var text = new StackPanel { Children = { value, caption } };
        Grid.SetColumn(text, 1);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { icon, text } };
        Grid.SetColumn(g, column);
        return g;
    }

    private Border BuildSpeed()
    {
        _gauge = new SpeedGauge();
        _gaugeValue = new TextBlock { Text = "0.0", FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = B(Text), HorizontalAlignment = HorizontalAlignment.Center };
        _speedPhase = new TextBlock { Text = "Ready", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = B(Accent), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _gaugeValue, new TextBlock { Text = "Mbps", FontSize = 13, Foreground = B(Text2), HorizontalAlignment = HorizontalAlignment.Center }, _speedPhase },
        };
        var dial = new Grid { Height = 168, Margin = new Thickness(0, 4, 0, 0), Children = { _gauge, center } };
        _speedDown = SpeedValue();
        _speedUp = SpeedValue();
        _speedPing = SpeedValue();
        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(0, 8, 0, 0) };
        stats.Children.Add(SpeedStat(Icons.Down, Chart1, "Download", _speedDown, 0));
        stats.Children.Add(SpeedStat(Icons.Up, Chart2, "Upload", _speedUp, 1));
        stats.Children.Add(SpeedStat(Icons.Clock, Accent2, "Ping", _speedPing, 2));
        _speedButton = new Button
        {
            Background = AccentGradient,
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0),
            Content = RunLabel("Run Test"),
        };
        _speedButton.Click += (_, _) => _ = RunSpeedTestAsync();
        _speedNote = new TextBlock { FontSize = 11.5, Foreground = B(Muted), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Text = "Through the VPN (Cloudflare)." };
        return Clay(new StackPanel { Children = { CardTitle(Icons.Gauge, "Speed Test"), dial, stats, _speedButton, _speedNote } }, new Thickness(22, 20), 26);
    }

    private static TextBlock SpeedValue() => new() { Text = "–", FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = B(Text), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };

    private static StackPanel RunLabel(string text) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children = { Icon(Icons.Play, 14, Colors.White, 2), new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White } },
    };

    private static StackPanel SpeedStat(string glyph, Color color, string label, TextBlock value, int column)
    {
        var s = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Icon(glyph, 13, color, 2), new TextBlock { Text = label, FontSize = 12, Foreground = B(Text2) } } },
                value,
            },
        };
        Grid.SetColumn(s, column);
        return s;
    }

    private Border BuildServer()
    {
        _serverDot = new Ellipse { Width = 8, Height = 8, Fill = B(Muted), VerticalAlignment = VerticalAlignment.Center };
        _serverState = new TextBlock { Text = "Not connected", FontSize = 12.5, Foreground = B(Text2) };
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _serverDot, _serverState } };
        _serverFlag = new FlagView { Width = 46, Height = 46, Margin = new Thickness(0, 0, 14, 0) };
        _serverTitle = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = B(Text), TextTrimming = TextTrimming.CharacterEllipsis };
        _serverSub = new TextBlock { FontSize = 12, Foreground = B(Text2), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var chevron = Icon(Icons.ChevronRight, 14, Text2, 2);
        DockPanel.SetDock(chevron, Dock.Right);
        DockPanel.SetDock(_serverFlag, Dock.Left);
        var current = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = B(TileBg),
            BorderBrush = B(Ui.Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel { Children = { chevron, _serverFlag, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _serverTitle, _serverSub } } } },
        };
        current.PointerPressed += (_, _) => Shell.Navigate(Shell.Servers);
        _tileMode = new TextBlock();
        _tileCoverage = new TextBlock();
        _tileProtocol = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(-5, 16, -5, 0) };
        tiles.Children.Add(InfoTile(Icons.Gauge, Accent, _tileMode, 0));
        tiles.Children.Add(InfoTile(Icons.Apps, Text2, _tileCoverage, 1));
        tiles.Children.Add(InfoTile(Icons.Shield, Text2, _tileProtocol, 2));
        var switchButton = Outline("Switch Server", () => Shell.Navigate(Shell.Servers), Icons.Swap);
        switchButton.Margin = new Thickness(0, 16, 0, 0);
        return Clay(new StackPanel { Children = { CardTitle(Icons.Servers, "Server", state), current, tiles, switchButton } }, new Thickness(22, 20), 26);
    }

    private static Border InfoTile(string glyph, Color color, TextBlock text, int column)
    {
        text.FontSize = 12.5;
        text.Foreground = B(Text);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.Margin = new Thickness(0, 8, 0, 0);
        var b = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = B(TileBg),
            BorderBrush = B(Ui.Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 12),
            Margin = new Thickness(5, 0),
            Child = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { Icon(glyph, 19, color, 1.9), text } },
        };
        Grid.SetColumn(b, column);
        return b;
    }

    // ------------------------------------------------------------------ actions

    public async Task ToggleAsync()
    {
        _ = _orb.Pop();
        await Guard(async () =>
        {
            var state = S.Orchestrator.State;
            if (state == ConnectionState.Disconnecting)
            {
                return;
            }

            // The Dual VPN alone is not the main VPN: the button turns the main VPN on (the traffic is then split).
            if (state == ConnectionState.Connected && S.Orchestrator.Status.DualOnly)
            {
                await S.PrepareTunAsync(Shell.Lifetime.Token);
                await S.Orchestrator.ConnectAsync(new ConnectRequest(), Shell.Lifetime.Token);
                return;
            }

            // While connecting this cancels.
            if (!ConnectionStateRules.IsIdle(state))
            {
                await S.Orchestrator.DisconnectAsync();
                return;
            }

            var c = S.Settings.Current.Connection;
            if (!c.SmartMode && c.SelectedProfileId is null)
            {
                Shell.Toast("Choose a server", "Pick a server on the Servers page, or turn on smart connect.", warning: true);
                Shell.Navigate(Shell.Servers);
                return;
            }

            await S.PrepareTunAsync(Shell.Lifetime.Token);
            await S.Orchestrator.ConnectAsync(new ConnectRequest(), Shell.Lifetime.Token);
        });
    }

    private void Apply(ConnectionStatus st)
    {
        _status = st;
        var p = Look.P;
        var connected = st.State == ConnectionState.Connected;
        var busy = !ConnectionStateRules.IsIdle(st.State) && !connected;
        // The big orb is the main VPN: while the Dual VPN runs alone it reads "off".
        var alone = connected && st.DualOnly;
        _orb.SetState(alone ? ConnectionState.Disconnected : st.State, connected && !alone ? st.Activity : ActivityProfile.Normal);
        var tint = connected ? p.Success : busy ? p.Accent : st.State == ConnectionState.Faulted ? p.Danger : p.TextMuted;
        _glow.Fill = new RadialGradientBrush
        {
            Center = new RelativePoint(0.12, 0.45, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.12, 0.45, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(1.2, RelativeUnit.Relative),
            GradientStops = { new GradientStop(WithAlpha(tint, p.IsLight ? 0.08 : 0.14), 0), new GradientStop(Colors.Transparent, 1) },
        };
        _title.Text = st.State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Disconnected => "Not connected",
            ConnectionState.Faulted => "Connection failed",
            _ => Spaced(st.State.ToString()) + "…",
        };
        _detail.Text = connected
            ? st.ProfileName ?? string.Empty
            : st.State == ConnectionState.Disconnected ? "Choose a server or let smart connect pick the fastest." : st.ProfileName ?? "Click the orb to cancel.";
        if (alone)
        {
            _title.Text = "Dual VPN only";
            _detail.Text = "All traffic goes through " + S.DualVpn.Current.Name + ". Click the orb to turn IBOX on too: then only the company's part uses it.";
        }
        // The chips change rarely; the status arrives every second (traffic): rebuild them only when they differ.
        var dual = S.DualVpn.Current;
        var dualShown = dual is { Enabled: true, IsConfigured: true };
        var dualActive = dualShown && S.DualVpn.IsActive(st.State);
        var chipKey = string.Join('|', connected, st.LatencyMs is { } l ? Math.Round(l / 5) : -1, st.Mode, st.LocalProxyPort, st.Protocol, st.Core, st.Activity, st.JackiSmartConnectActive, dualShown, dualActive, dual.Name, Look.P.IsLight);
        if (chipKey != _chipKey)
        {
            _chipKey = chipKey;
            _chips.Children.Clear();
            if (dualShown)
            {
                var dualChip = Chip(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        new Ellipse { Width = 8, Height = 8, Fill = B(dualActive ? p.Success : p.TextMuted), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "Dual VPN · " + dual.Name + (dualActive ? " · active" : string.Empty), FontSize = 12, Foreground = B(Text) },
                    },
                });
                dualChip.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                ToolTip.SetTip(dualChip, dual.KindLabel + ": " + S.DualVpn.Describe(st.State) + ". Click to manage it.");
                dualChip.PointerPressed += (_, _) => Shell.Navigate(Shell.Servers);
                _chips.Children.Add(dualChip);
            }

            if (connected)
            {
                var latency = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        new Ellipse { Width = 8, Height = 8, Fill = B(st.LatencyMs switch { null => p.TextMuted, < 150 => p.Success, < 300 => p.Warning, _ => p.Danger }), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = st.LatencyMs is { } ms ? $"{ms:0} ms" : "— ms", FontFamily = new FontFamily("monospace"), FontSize = 12, Foreground = B(Text) },
                    },
                };
                _chips.Children.Add(Chip(latency));
                if (_duration.Parent is Panel oldParent)
                {
                    oldParent.Children.Remove(_duration);
                }

                _chips.Children.Add(Chip(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(Icons.Clock, 12, Text2, 2), _duration } }));
                var mode = st.Mode == ConnectionMode.Tun ? "TUN" : $"Proxy 127.0.0.1:{st.LocalProxyPort}";
                _chips.Children.Add(Chip(new TextBlock { Text = string.Join(" · ", new[] { mode, st.Protocol, st.Core?.ToString() }.Where(x => !string.IsNullOrEmpty(x))), FontSize = 12, Foreground = B(Text) }));
                if (st.Activity != ActivityProfile.Normal)
                {
                    var color = st.Activity == ActivityProfile.Gaming ? Color.Parse("#F43F7A") : Streaming;
                    var chip = Chip(new TextBlock { Text = st.Activity == ActivityProfile.Gaming ? "Gaming" : "Streaming", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = B(Text) });
                    chip.Background = B(color, 0.15);
                    chip.BorderBrush = B(color);
                    _chips.Children.Add(chip);
                }

                if (st.JackiSmartConnectActive)
                {
                    var chip = Chip(new TextBlock { Text = "Jacki Smart Connect", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = B(Accent) });
                    chip.Background = B(Accent, 0.12);
                    chip.BorderBrush = B(Accent);
                    _chips.Children.Add(chip);
                }
            }

        }

        _ip.Text = connected && st.PublicIp is { } ip ? ip : string.Empty;
        _ip.IsVisible = _ip.Text.Length > 0;
        _reason.Text = st.SelectionReason ?? string.Empty;
        _reason.IsVisible = _reason.Text.Length > 0;
        _error.Text = st.LastError ?? string.Empty;
        _error.IsVisible = !connected && !string.IsNullOrEmpty(st.LastError);

        _map.IsActive = connected;
        _map.Latitude = connected ? st.ExitLatitude : null;
        _map.Longitude = connected ? st.ExitLongitude : null;
        _map.CountryCode = connected ? st.ExitCountryCode : null;

        if (connected && st.Traffic is { } t)
        {
            Push(_downHistory, t.DownloadBytesPerSecond);
            Push(_upHistory, t.UploadBytesPerSecond);
            _down.Text = Speed(t.DownloadBytesPerSecond);
            _up.Text = Speed(t.UploadBytesPerSecond);
            _downTotal.Text = "Download · " + Bytes(t.DownloadBytesTotal);
            _upTotal.Text = "Upload · " + Bytes(t.UploadBytesTotal);
        }
        else if (!connected)
        {
            _down.Text = _up.Text = "0 B/s";
            _downTotal.Text = "Download";
            _upTotal.Text = "Upload";
        }

        _chart.SetSeries(_downHistory.ToArray(), _upHistory.ToArray());
        _serverDot.Fill = B(connected ? p.Success : busy ? p.Warning : p.TextMuted);
        _serverState.Text = connected ? "Connected" : busy ? "Connecting" : "Not connected";
        if (connected)
        {
            _clock?.Start();
        }
        else
        {
            _clock?.Stop();
        }

        ShowDuration();
        if (_speedButton is not null)
        {
            _speedButton.IsEnabled = connected || _speedCts is not null;
        }

        RefreshServerCard();
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength)
        {
            q.Dequeue();
        }
    }

    private static Border Chip(Control content) => new()
    {
        Background = B(Look.P.IsLight ? Color.Parse("#0A000000") : Color.Parse("#0FFFFFFF")),
        BorderBrush = B(Ui.Border),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(13),
        Padding = new Thickness(10, 4),
        Margin = new Thickness(0, 0, 8, 8),
        Child = content,
    };

    private void ShowDuration()
    {
        if (_status.State == ConnectionState.Connected && _status.ConnectedSinceUtc is { } since)
        {
            var d = DateTimeOffset.UtcNow - since;
            _duration.Text = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : d.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
    }

    private void RefreshServerCard()
    {
        var st = _status;
        if (st.State == ConnectionState.Connected)
        {
            var country = st.ExitCountryName ?? st.ProfileName ?? "Connected";
            _serverFlag.CountryCode = _locationFlag.CountryCode = st.ExitCountryCode;
            _serverTitle.Text = _locationTitle.Text = country;
            _serverSub.Text = string.Join(" · ", new[] { st.ExitCity, st.ProfileName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            _locationSub.Text = st.ExitCity ?? st.ExitIsp ?? st.PublicIp ?? "Locating…";
            _tileProtocol.Text = st.Protocol ?? "—";
        }
        else
        {
            _serverFlag.CountryCode = _locationFlag.CountryCode = _selectedCountry;
            _serverTitle.Text = _selectedName ?? "Smart connect";
            _serverSub.Text = _selectedName is null ? "Best server picked when you connect" : "Selected server";
            _locationTitle.Text = _selectedCountry is { } cc ? HttpLiveProbe.CountryName(cc) ?? cc : "Choose location";
            _locationSub.Text = _selectedName ?? "Smart: fastest server";
            _tileProtocol.Text = _selectedProtocol ?? "Auto";
        }
    }

    private async Task RefreshSelectionAsync()
    {
        var c = S.Settings.Current.Connection;
        if (c.SmartMode)
        {
            _selectedName = _selectedCountry = _selectedProtocol = null;
        }
        else
        {
            var profile = c.SelectedProfileId is { } id ? await S.Profiles.GetAsync(id, Shell.Lifetime.Token) : null;
            _selectedName = profile?.DisplayName;
            _selectedCountry = profile is null ? null : S.Insights.Get(profile.Fingerprint)?.Exit?.CountryCode ?? Domain.Visuals.CountryGuess.FromName(profile.DisplayName);
            _selectedProtocol = profile?.Protocol.ToString().ToUpperInvariant();
        }

        ShowMode();
        RefreshServerCard();
    }

    private async Task RefreshPlanAsync()
    {
        var subs = await S.Subscriptions.GetAllAsync(Shell.Lifetime.Token);
        _plan.Children.Clear();
        var sub = subs.FirstOrDefault(x => x.Enabled);
        if (sub is null)
        {
            return;
        }

        var m = sub.Metadata;
        var status = m.ComputeStatus(DateTimeOffset.UtcNow);
        var ok = status is not (SubscriptionStatus.Expired or SubscriptionStatus.Limited);
        var quota = m.IsUnlimitedTraffic == true ? "Unlimited"
            : m.TotalBytes is { } total && m.UsedBytes is { } used ? $"{Bytes(Math.Max(0, total - used))} left"
            : "Quota unknown";
        var expiry = m.IsUnlimitedExpiry == true ? "Never expires" : m.ExpireUtc is { } e ? "Expires " + When(e) : "Expiry unknown";
        var pill = Pill(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Icon(Icons.Infinity, 17, Accent, 2), new TextBlock { Text = quota, FontSize = 13.5, Foreground = B(Text), VerticalAlignment = VerticalAlignment.Center } },
        });
        pill.Padding = new Thickness(14, 7);
        pill.VerticalAlignment = VerticalAlignment.Center;
        var chevron = Icon(Icons.ChevronRight, 14, Text2, 2);
        chevron.Margin = new Thickness(14, 0, 4, 0);
        DockPanel.SetDock(chevron, Dock.Right);
        DockPanel.SetDock(pill, Dock.Right);
        var tile = IconTile(Icons.Servers, Text2, 48, 20);
        tile.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(tile, Dock.Left);
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = sub.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = B(Text), TextTrimming = TextTrimming.CharacterEllipsis },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Margin = new Thickness(0, 3, 0, 0),
                    Children =
                    {
                        new Ellipse { Width = 8, Height = 8, Fill = B(ok ? Success : Danger), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = ok ? "Active" : status.ToString(), FontSize = 12.5, Foreground = B(Text2) },
                        new TextBlock { Text = "·  " + expiry, FontSize = 12.5, Foreground = B(Muted) },
                    },
                },
            },
        };
        var strip = Clay(new DockPanel { Children = { chevron, pill, tile, info } }, new Thickness(18, 14), 24);
        strip.Margin = new Thickness(0, 20, 0, 0);
        strip.Cursor = new Cursor(StandardCursorType.Hand);
        strip.PointerPressed += (_, _) => Shell.Navigate(Shell.Subscriptions);
        _plan.Children.Add(strip);
    }

    private async Task RunSpeedTestAsync()
    {
        if (_speedCts is not null)
        {
            await _speedCts.CancelAsync();
            return;
        }

        var st = S.Orchestrator.Status;
        if (st.State != ConnectionState.Connected || st.Mode is not { } mode)
        {
            return;
        }

        _speedCts = CancellationTokenSource.CreateLinkedTokenSource(Shell.Lifetime.Token);
        _speedButton!.Content = RunLabel("Cancel");
        _speedDown.Text = _speedUp.Text = _speedPing.Text = "–";
        var progress = new Progress<SpeedTestProgress>(p =>
        {
            _gauge.Value = p.CurrentMbps;
            _gaugeValue.Text = p.CurrentMbps.ToString("0.0", CultureInfo.InvariantCulture);
            _speedPhase.Text = p.Phase switch
            {
                SpeedTestPhase.Latency => "Latency",
                SpeedTestPhase.Download => "Download",
                SpeedTestPhase.Upload => "Upload",
                _ => "Done",
            };
        });
        try
        {
            var r = await Task.Run(() => S.SpeedTest.RunAsync(mode, st.LocalProxyPort, progress, _speedCts.Token));
            _speedDown.Text = r.DownloadMbps is { } d ? $"{d:0.0}" : "–";
            _speedUp.Text = r.UploadMbps is { } u ? $"{u:0.0}" : "–";
            _speedPing.Text = r.IdleLatencyMs is { } l ? $"{l:0} ms" : "–";
            _gauge.Value = r.DownloadMbps ?? 0;
            _gaugeValue.Text = (r.DownloadMbps ?? 0).ToString("0.0", CultureInfo.InvariantCulture);
            _speedPhase.Text = r.Error is null ? "Done" : "Incomplete";
            _speedNote.Text = r.Error is { } e ? "Test incomplete: " + e : $"Cloudflare {r.Server}" + (r.LoadedLatencyMs is { } ll ? $" · {ll:0} ms under load" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            _speedPhase.Text = "Cancelled";
        }
        finally
        {
            _speedCts.Dispose();
            _speedCts = null;
            _speedButton.Content = RunLabel("Run Test");
        }
    }
}
