using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using JackiVpn.Desktop.ViewModels;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The notch: a small black island on the top edge of the screen with the IBOX eye, the connection state and the
/// live ping (other details on request). The eye follows the pointer and plays; hovering grows the island on a
/// spring and shows more; it can be dragged along the top edge, fades out of the way after a few seconds, and comes
/// back to full strength when the pointer rests on it for 1.5 s. Never takes focus, never in Alt+Tab, hidden while a
/// full-screen app (game, video, slides) is in front. Everything animates only while something changes.
/// </summary>
public sealed partial class NotchWindow : Window
{
    // Room around the island for its soft shadow (the window itself is transparent and click-through there).
    private const double WindowWidth = 540;
    private const double WindowHeight = 180;
    private const double CollapsedHeight = 34;
    private const double ExpandedWidth = 410;
    private const double ExpandedHeight = 112;
    private const double EyeBig = 56;
    private const double FadedOpacity = 0.32;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Border _pill;
    private readonly Border _shadow;
    private readonly Grid _island;
    private readonly OrbView _orb = new() { State = OrbState.Connecting, Tint = Color.FromRgb(0x9C, 0xCF, 0xFF), Width = 44, Height = 44, Detail = 64, MinInk = 0.4, Opacity = 0, IsRunning = false };
    private readonly ScaleTransform _orbScale = new(0.6, 0.6);
    private readonly Canvas _surface = new();
    private readonly EyeMarkView _eye = new() { Width = 36, Height = 36.5 };
    private readonly ScaleTransform _eyeScale = new(0.5, 0.5);
    private readonly TextBlock _line = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _ghost = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Opacity = 0, RenderTransform = new TranslateTransform() };
    private readonly Grid _lineHost = new();
    private readonly StackPanel _details = new();
    private Spring? _pingCount;
    private readonly TextBlock _statusText = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, RenderTransform = new TranslateTransform() };
    private readonly ParticleIconView _statusIcon = new(34) { Width = 18, Height = 18, Duration = 0.9 };
    private readonly Canvas _statusHost = new();
    private readonly ParticleIconView _badge = new(72) { Width = 36, Height = 36, Shimmer = true, Opacity = 0 };
    private readonly ScaleTransform _badgeScale = new(0.5, 0.5);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _badgeTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private Spring? _statusWidth;
    private bool _statusAsIcon;
    private bool _badgeShowing;
    private VisualMode _lastMode;
    private string _lineSignature = string.Empty;
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
    private readonly TextBlock _ping = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _server = new() { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _where = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _speed = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)) };
    private readonly TextBlock _musicText = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _musicPlayGlyph = new() { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Brushes.White, Text = "\uE768" };
    private readonly Grid _music = new() { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private Application.Media.NowPlaying? _nowPlaying;
    private readonly ScaleTransform _press = new(1, 1);
    private readonly Spring _width;
    private readonly Spring _height;
    private readonly Spring _expand;
    private readonly Spring _group;
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _fullScreenTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ConnectionStatus _status = new();
    private VisualMode _mode;
    private NotchSettings _settings = new();
    private bool _expanded;
    private bool _faded;
    private bool _hiddenForFullScreen;
    private bool _animating;
    private double _lastFrame;
    private Point _lastCursor;
    private bool _dragging;
    private bool _dragMoved;
    private double _dragStartCursor;
    private double _dragStartCenter;
    private double _center;

    public NotchWindow()
    {
        Title = "IBOX notch";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = WindowWidth;
        Height = WindowHeight;
        Top = 0;
        Focusable = false;

        _eye.RenderTransform = _eyeScale;
        _surface.Children.Add(_eye);
        _line.RenderTransform = new TranslateTransform();
        _lineHost.Children.Add(_ghost);
        _lineHost.Children.Add(_line);
        _surface.Children.Add(_lineHost);
        // The state: a word for two seconds, then an icon made of orb dots (blue link / grey broken link).
        _statusHost.Children.Add(_statusText);
        _statusHost.Children.Add(_statusIcon);
        Canvas.SetTop(_statusIcon, -0.5);
        _surface.Children.Add(_statusHost);
        // A traffic mode shows itself for ten seconds: the eye's dots become a gamepad, a leaf, a play sign or a monitor.
        _badge.RenderTransform = _badgeScale;
        _surface.Children.Add(_badge);
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        head.Children.Add(_title);
        Grid.SetColumn(_ping, 1);
        head.Children.Add(_ping);
        _details.Children.Add(head);
        _details.Children.Add(_server);
        _details.Children.Add(_where);
        _details.Children.Add(_speed);

        // Now playing: the item and three small buttons (previous, play/pause, next).
        _music.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _music.ColumnDefinitions.Add(new ColumnDefinition());
        _music.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\uEC4F", FontSize = 12, Foreground = new SolidColorBrush(ConnectedBlue), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _music.Children.Add(note);
        Grid.SetColumn(_musicText, 1);
        _music.Children.Add(_musicText);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        controls.Children.Add(MediaButton(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Brushes.White, Text = "\uE892" }, -1, "Previous"));
        controls.Children.Add(MediaButton(_musicPlayGlyph, 0, "Play / pause"));
        controls.Children.Add(MediaButton(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Brushes.White, Text = "\uE893" }, 1, "Next"));
        Grid.SetColumn(controls, 2);
        _music.Children.Add(controls);
        _details.Children.Add(_music);
        foreach (UIElement row in _details.Children)
        {
            row.RenderTransform = new TranslateTransform();
            row.Opacity = 0;
        }

        _details.Width = ExpandedWidth - DetailsLeft - 24;
        _surface.Children.Add(_details);
        Canvas.SetLeft(_details, DetailsLeft);

        // While connecting, a large high-detail thinking orb takes the eye's place.
        _orb.RenderTransform = _orbScale;
        _surface.Children.Add(_orb);

        _pill = new Border
        {
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = _surface,
            Cursor = Cursors.Hand,
        };
        // The shadow lives on its own layer behind the island, so the clip of the content never cuts it off.
        _shadow = new Border
        {
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 2, Direction = 270, Opacity = 0.85, Color = Colors.Black },
        };
        _island = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = _press,
            Children = { _shadow, _pill },
        };
        Content = new Grid { Children = { _island } };

        _width = new Spring(190, 230, 0.8, v =>
        {
            _pill.Width = _shadow.Width = Math.Max(40, v);
            ApplyExpansion(_expand!.Value);
        });
        _height = new Spring(CollapsedHeight, 225, 0.78, v =>
        {
            _pill.Height = _shadow.Height = Math.Max(20, v);
            var r = Math.Min(_pill.Height / 2, 20);
            _pill.CornerRadius = _shadow.CornerRadius = new CornerRadius(0, 0, r, r);
        });
        _expand = new Spring(0, 165, 0.9, ApplyExpansion);
        // Width of the eye+text group, on the same spring as the island: the group re-centres smoothly.
        _group = new Spring(120, 230, 0.8, _ => ApplyExpansion(_expand.Value));
        _statusWidth = new Spring(80, 230, 0.8, _ => ApplyExpansion(_expand.Value));
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ShowStatusIcon();
        };
        _badgeTimer.Tick += (_, _) => EndBadge();

        _eye.BeforeFrame = eye =>
        {
            // A faded notch rests: following every mouse move across the screen would redraw it all day.
            if (eye.State == EyeState.Awake && !_faded && ScreenCursor.TryGet(out var p) && p != _lastCursor)
            {
                _lastCursor = p;
                eye.LookAtScreen(p, 420, 260);
            }
        };

        _pill.MouseEnter += (_, _) => OnHover(true);
        _pill.MouseLeave += (_, _) => OnHover(false);
        _pill.MouseLeftButtonDown += OnPress;
        _pill.MouseMove += OnDrag;
        _pill.MouseLeftButtonUp += OnRelease;
        _fadeTimer.Tick += (_, _) =>
        {
            _fadeTimer.Stop();
            if (!_pill.IsMouseOver)
            {
                SetFaded(true);
            }
        };
        _holdTimer.Tick += (_, _) =>
        {
            // Pointer held on the faded notch for 1.5 s: back to full strength.
            _holdTimer.Stop();
            SetFaded(false);
            SetExpanded(true);
        };
        _fullScreenTimer.Tick += (_, _) => CheckFullScreen();
        SourceInitialized += (_, _) => MakeToolWindow();
        Loaded += (_, _) =>
        {
            PlaceAt(_center);
            Render();
            // First appearance: no glide from the springs' starting values.
            _width.Snap();
            _group.Snap();
            _statusWidth.Snap();
            _height.Snap();
            _fullScreenTimer.Start();
            RestartFade();
        };
        Closed += (_, _) =>
        {
            _fadeTimer.Stop();
            _holdTimer.Stop();
            _fullScreenTimer.Stop();
            _statusTimer.Stop();
            _badgeTimer.Stop();
            StopAnimating();
        };
    }

    /// <summary>Open the app (double click / menu).</summary>
    public event EventHandler? OpenRequested;

    public event EventHandler? ToggleRequested;

    public event EventHandler? CustomizeRequested;

    public event EventHandler? HideRequested;

    /// <summary>Previous (-1), play/pause (0) or next (+1) on the system's media session.</summary>
    public event EventHandler<int>? MediaCommand;

    /// <summary>The user dragged it: new position 0 (left) … 1 (right).</summary>
    public event EventHandler<double>? Moved;

    /// <summary>Test copies: the demo cycle plays the chime only once.</summary>
    internal bool SuppressSound { get; set; }

    /// <summary>What the system is playing (null = nothing); shown when the notch settings allow it.</summary>
    public void SetNowPlaying(Application.Media.NowPlaying? item)
    {
        if (item == _nowPlaying)
        {
            return;
        }

        _nowPlaying = item;
        Render();
        if (_expanded)
        {
            _height.To(ExpandedHeightNow);
            Animate();
        }
    }

    private bool ShowsMusic => _settings.ShowNowPlaying && _nowPlaying is not null;

    /// <summary>Expanded height: one row more while something is playing.</summary>
    private double ExpandedHeightNow => ExpandedHeight + (ShowsMusic ? 28 : 0);

    private Button MediaButton(TextBlock glyph, int command, string tip)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 26,
            Height = 22,
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false,
            Template = MediaButtonTemplate(),
        };
        b.Click += (_, _) => MediaCommand?.Invoke(this, command);
        return b;
    }

    private static ControlTemplate MediaButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    public void Apply(NotchSettings settings)
    {
        _settings = settings;
        _fadeTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, settings.FadeAfterSeconds));
        if (!_dragging)
        {
            _center = settings.Position;
            if (IsLoaded)
            {
                PlaceAt(_center);
            }
        }

        if (settings.FadeAfterSeconds <= 0)
        {
            _fadeTimer.Stop();
            SetFaded(false);
        }

        Render();
    }

    private bool _first = true;

    private string? _shownKey;

    public void Update(ConnectionStatus status, VisualMode mode)
    {
        // The status arrives every second (traffic counters): redraw only when something the notch shows changed.
        var key = string.Join('|',
            status.State, status.LatencyMs is { } l ? Math.Round(l).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-", status.ProfileName,
            status.ExitCountryName ?? status.ExitCountryCode, status.LastError, status.Mode, mode,
            status.ConnectedSinceUtc is { } since ? ((int)(DateTimeOffset.UtcNow - since).TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-");
        if (!_first && key == _shownKey)
        {
            return;
        }

        _shownKey = key;

        // The very first state counts as a change too (it is said, then becomes its icon).
        var firstUpdate = _first;
        var changed = status.State != _status.State || _first;
        _first = false;
        _status = status;
        _mode = mode;
        _eye.State = status.State == ConnectionState.Connected ? EyeState.Awake : ConnectionStateRules.IsIdle(status.State) ? EyeState.Asleep : EyeState.Seeking;
        var seeking = _eye.State == EyeState.Seeking;
        if (seeking)
        {
            _orb.State = ThinkingOrbs.ForConnection(status.State);
        }

        if (seeking != _orb.IsRunning)
        {
            // Connecting: the eye steps aside for the orb (and comes back when it is through).
            _orb.IsRunning = seeking;
            var d = Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(320);
            _orb.BeginAnimation(OpacityProperty, new DoubleAnimation(seeking ? 1 : 0, d));
            _eye.BeginAnimation(OpacityProperty, new DoubleAnimation(seeking || _badgeShowing ? 0 : 1, d));
        }

        if (changed)
        {
            // A new state is first said in words; after two seconds it becomes an icon (connected / not connected).
            _statusTimer.Stop();
            if (_statusAsIcon)
            {
                _statusAsIcon = false;
                MorphStatus(toIcon: false);
            }

            if (status.State is ConnectionState.Connected or ConnectionState.Disconnected)
            {
                _statusTimer.Start();
            }
        }

        var modeChanged = mode != _lastMode;
        _lastMode = mode;
        if (status.State == ConnectionState.Connected && (modeChanged || changed) && MorphIcons.ForMode(mode) is { } icon)
        {
            StartBadge(mode, icon);
        }
        else if (_badgeShowing && (mode == VisualMode.Normal || status.State != ConnectionState.Connected))
        {
            EndBadge();
        }

        Render();
        if (changed && !firstUpdate && status.State == ConnectionState.Connected && _settings.Sound && !SuppressSound)
        {
            Services.Chime.PlayConnected();
        }

        if (changed && IsLoaded)
        {
            // A state change is worth a look: come back for a moment.
            SetFaded(false);
            RestartFade();
        }
    }

    /// <summary>The word gives way to an icon of orb dots: a blue link when connected, a grey broken link when not.</summary>
    private void ShowStatusIcon()
    {
        var connected = _status.State == ConnectionState.Connected;
        if (!connected && _status.State != ConnectionState.Disconnected)
        {
            return;
        }

        _statusIcon.Icon = connected ? MorphIcon.Link : MorphIcon.Unlink;
        _statusIcon.Tint = connected ? ConnectedBlue : Grey;
        _statusAsIcon = true;
        MorphStatus(toIcon: true);
        Render();
    }

    private void MorphStatus(bool toIcon)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var reduced = Motion.Reduced;
        TimeSpan Ms(int ms) => reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
        var slide = (TranslateTransform)_statusText.RenderTransform;
        if (toIcon)
        {
            _statusText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(260)) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, Ms(300)) { EasingFunction = ease });
            _statusIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(200)));
            _statusIcon.Snap(0);
            _statusIcon.Assemble();
        }
        else
        {
            // Out of the way at once (the word comes back in the same place).
            _statusIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(160)));
            _statusIcon.Disperse();
            _statusText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(320)) { BeginTime = Ms(120), EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Ms(380)) { BeginTime = Ms(120), EasingFunction = ease });
        }
    }

    /// <summary>
    /// A traffic mode announces itself: the eye's dots fly into its icon (gamepad, leaf, play sign, monitor) for ten
    /// seconds, then dissolve back into the eye.
    /// </summary>
    private void StartBadge(VisualMode mode, MorphIcon icon)
    {
        var c = MorphIcons.ColorOf(mode);
        _badge.Icon = icon;
        _badge.Tint = Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        _badge.Snap(0);
        var d = Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(260);
        _badge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        _eye.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        _badge.Assemble();
        _badgeShowing = true;
        _badgeTimer.Stop();
        _badgeTimer.Start();
        if (IsLoaded)
        {
            // Worth seeing: come back from faded for the show.
            SetFaded(false);
            RestartFade();
        }
    }

    private void EndBadge()
    {
        _badgeTimer.Stop();
        if (!_badgeShowing)
        {
            return;
        }

        _badgeShowing = false;
        var reduced = Motion.Reduced;
        _badge.Disperse(() => _badge.BeginAnimation(OpacityProperty, new DoubleAnimation(0, reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(220))));
        if (_eye.State != EyeState.Seeking)
        {
            _eye.BeginAnimation(OpacityProperty, new DoubleAnimation(1, reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(420)) { BeginTime = reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(650) });
        }
    }

    // ------------------------------------------------------------------ content

    private static readonly Color ConnectedBlue = Color.FromRgb(0x5A, 0xAC, 0xFF);
    private static readonly Color Grey = Color.FromRgb(0x9C, 0xA3, 0xAF);

    private void Render()
    {
        var st = _status;
        var connected = st.State == ConnectionState.Connected;
        var stateColor = connected ? ConnectedBlue : st.State == ConnectionState.Faulted ? Color.FromRgb(0xF8, 0x71, 0x71) : ConnectionStateRules.IsIdle(st.State) ? Grey : Color.FromRgb(0x7D, 0xB8, 0xFF);
        var stateText = st.State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Disconnected => "Not connected",
            ConnectionState.Faulted => "Failed",
            ConnectionState.Reconnecting => "Reconnecting…",
            ConnectionState.Disconnecting => "Stopping…",
            _ => "Connecting…",
        };
        var ping = connected && st.LatencyMs is { } ms ? $"{ms:0} ms" : null;
        var pingColor = st.LatencyMs switch { < 150 => Color.FromRgb(0x4A, 0xDE, 0x80), < 300 => Color.FromRgb(0xFB, 0xBF, 0x24), _ => Color.FromRgb(0xF8, 0x71, 0x71) };

        // The rest of the line (ping, tunnel, …); the state itself has its own slot (word, then icon).
        var s = _settings;
        var showStatus = s.ShowStatus || !connected;
        var before = _line.Inlines.OfType<Run>().Select(r => (r.Text, r.Foreground)).ToList();
        _line.Inlines.Clear();
        void Part(string text, Color? color = null)
        {
            if (_line.Inlines.Count > 0 || showStatus)
            {
                _line.Inlines.Add(new Run("  ·  ") { Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)) });
            }

            _line.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(color ?? Colors.White) });
        }

        if (connected)
        {
            if (s.ShowPing && ping is not null)
            {
                Part(ping, pingColor);
            }

            if (s.ShowTunnel)
            {
                Part(st.Mode == ConnectionMode.Tun ? "TUN" : "Proxy");
            }

            if (s.ShowServer && st.ProfileName is { } name)
            {
                Part(name.Length > 26 ? name[..25] + "…" : name);
            }

            if (s.ShowCountry && (st.ExitCountryName ?? st.ExitCountryCode) is { } country)
            {
                Part(country);
            }

            if (s.ShowDuration && st.ConnectedSinceUtc is { } since)
            {
                Part(Duration(DateTimeOffset.UtcNow - since));
            }
        }

        if (ShowsMusic && _nowPlaying is { IsPlaying: true } playing)
        {
            Part("♪ " + playing.Line(26), Color.FromRgb(0xC7, 0xD2, 0xFE));
        }

        if (!showStatus && _line.Inlines.Count == 0)
        {
            // Nothing else chosen: the state is shown after all.
            showStatus = true;
        }

        var signature = string.Concat(_line.Inlines.OfType<Run>().Select(r => new string(r.Text.Where(c => !char.IsAsciiDigit(c)).ToArray()) + "|"));
        if (signature != _lineSignature && before.Count > 0 && IsLoaded)
        {
            Morph(before);
        }

        _lineSignature = signature;
        _statusHost.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        _statusText.Text = stateText;
        _statusText.Foreground = new SolidColorBrush(stateColor);

        _title.Inlines.Clear();
        _title.Inlines.Add(new Run("IBOX  ") { Foreground = new SolidColorBrush(Grey), FontSize = 11 });
        _title.Inlines.Add(new Run(stateText) { Foreground = new SolidColorBrush(stateColor) });
        if (connected && st.LatencyMs is { } pingMs)
        {
            // The big number counts to the new value instead of jumping.
            if (_pingCount is null)
            {
                _pingCount = new Spring(pingMs, 70, 1.0, v => _ping.Text = $"{v:0} ms");
                _pingCount.Snap();
            }
            else
            {
                _pingCount.To(pingMs);
            }
        }
        else
        {
            _pingCount = null;
            _ping.Text = "—";
        }

        _ping.Foreground = new SolidColorBrush(ping is null ? Color.FromRgb(0x6B, 0x72, 0x80) : pingColor);
        _server.Text = connected ? st.ProfileName ?? "—" : st.State == ConnectionState.Faulted ? st.LastError ?? "Connection failed" : "Click the eye to open IBOX";
        _where.Text = connected
            ? string.Join("  ·  ", new[]
            {
                st.ExitCountryName ?? st.ExitCountryCode,
                st.ConnectedSinceUtc is { } t ? Duration(DateTimeOffset.UtcNow - t) : null,
                st.Mode == ConnectionMode.Tun ? "TUN · all apps" : "System proxy",
                _mode is VisualMode.Normal ? null : ModePalette.Label(_mode).ToLowerInvariant(),
            }.Where(x => !string.IsNullOrEmpty(x)))
            : string.Empty;
        _music.Visibility = ShowsMusic ? Visibility.Visible : Visibility.Collapsed;
        if (_nowPlaying is { } np)
        {
            _musicText.Text = np.Line(48);
            _musicPlayGlyph.Text = np.IsPlaying ? "\uE769" : "\uE768";
        }

        _speed.Text = connected && st.Traffic is { } tr ? $"↓ {Format.Speed(tr.DownloadBytesPerSecond)}    ↑ {Format.Speed(tr.UploadBytesPerSecond)}" : string.Empty;

        _line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _statusText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _details.Measure(new Size(_details.Width, double.PositiveInfinity));
        _statusWidth?.To(StatusWidth());
        _group.To(GroupWidth());
        if (!_expanded)
        {
            _width.To(CollapsedWidth());
        }

        Animate();
        ApplyExpansion(_expand.Value);
    }

    /// <summary>Width the state slot is heading to: none, the word, or the 18 px icon.</summary>
    private double StatusWidth() => _statusHost.Visibility != Visibility.Visible ? 0 : _statusAsIcon ? 18 : _statusText.DesiredSize.Width;

    private double GroupWidth() => EyeSmall + EyeGap + StatusWidth() + (_line.Inlines.Count > 0 ? _line.DesiredSize.Width : 0);
    private const double EyeSmall = 20;
    private const double EyeGap = 14;
    private const double SidePadding = 30;

    private const double DetailsLeft = 26 + EyeBig + 24;

    /// <summary>
    /// Lays the content out for the island's current (animated) size. Collapsed, the eye and the line are one group
    /// centred in the island — when the ping appears the island widens on its spring and the group glides back to
    /// the centre with it. Expanded (t → 1), the eye grows and settles at the left, exactly centred in the island's
    /// height, and the detail rows rise in one after another.
    /// </summary>
    private void ApplyExpansion(double t)
    {
        var e = Math.Clamp(t, 0, 1.15);
        var k = Math.Clamp(e, 0, 1);
        var width = double.IsNaN(_pill.Width) ? CollapsedWidth() : _pill.Width;
        var height = double.IsNaN(_pill.Height) ? CollapsedHeight : _pill.Height;
        var group = _group?.Value ?? GroupWidth();
        var groupLeft = Math.Max(SidePadding * 0.6, (width - group) / 2);

        // The eye: centred in the island's current height at every moment of the morph.
        var eyeSize = EyeSmall + ((EyeBig - EyeSmall) * e);
        _eyeScale.ScaleX = _eyeScale.ScaleY = eyeSize / 36;
        var eyeLeft = groupLeft + ((26 - groupLeft) * k);
        var eyeTop = (height - eyeSize) / 2;
        Canvas.SetLeft(_eye, eyeLeft);
        Canvas.SetTop(_eye, eyeTop);
        _badgeScale.ScaleX = _badgeScale.ScaleY = eyeSize / 36;
        Canvas.SetLeft(_badge, eyeLeft);
        Canvas.SetTop(_badge, eyeTop);

        // The orb takes the eye's place, a little larger (31 px collapsed, 62 px expanded).
        _orbScale.ScaleX = _orbScale.ScaleY = 0.7 + (0.7 * Math.Clamp(e, 0, 1.1));
        var orbSize = 44 * _orbScale.ScaleX;
        Canvas.SetLeft(_orb, eyeLeft + (eyeSize / 2) - (orbSize / 2));
        Canvas.SetTop(_orb, eyeTop + (eyeSize / 2) - (orbSize / 2));

        var lineOpacity = Math.Clamp(1 - (e * 2.2), 0, 1);
        var statusLeft = groupLeft + EyeSmall + EyeGap;
        var statusWidth = _statusWidth?.Value ?? StatusWidth();
        var lineTop = ((height - 17) / 2) - (4 * k);
        _statusHost.Opacity = lineOpacity;
        Canvas.SetLeft(_statusHost, statusLeft);
        Canvas.SetTop(_statusHost, lineTop);
        _lineHost.Opacity = lineOpacity;
        Canvas.SetLeft(_lineHost, statusLeft + statusWidth);
        Canvas.SetTop(_lineHost, lineTop);

        // Details: vertically centred, rows rising in one after another (staggered).
        Canvas.SetTop(_details, Math.Max(10, (ExpandedHeightNow - _details.DesiredSize.Height) / 2));
        var i = 0;
        foreach (UIElement row in _details.Children)
        {
            var start = 0.28 + (0.11 * i++);
            var p = Math.Clamp((e - start) / 0.5, 0, 1);
            var eased = p * p * (3 - (2 * p));
            row.Opacity = eased;
            ((TranslateTransform)row.RenderTransform).Y = 9 * (1 - eased);
        }
    }

    /// <summary>
    /// The line changed shape (state, ping appearing…): the old words drift up and fade while the new ones rise
    /// into place — a soft morph instead of a jump.
    /// </summary>
    private void Morph(List<(string Text, Brush Foreground)> before)
    {
        if (Motion.Reduced)
        {
            return;
        }

        _ghost.Inlines.Clear();
        foreach (var (text, brush) in before)
        {
            _ghost.Inlines.Add(new Run(text) { Foreground = brush });
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        ((TranslateTransform)_ghost.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -7, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        _line.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)) { BeginTime = TimeSpan.FromMilliseconds(70), EasingFunction = ease });
        ((TranslateTransform)_line.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(70), EasingFunction = ease });
    }

    /// <summary>The centred group plus generous, equal room on both sides.</summary>
    private double CollapsedWidth() => Math.Clamp(GroupWidth() + (2 * SidePadding), 150, 420);

    private static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m" : $"{(int)d.TotalMinutes} min";

    private static Color Argb(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    // ------------------------------------------------------------------ hover, fade, expand

    private void OnHover(bool inside)
    {
        if (_dragging)
        {
            return;
        }

        if (inside)
        {
            _fadeTimer.Stop();
            if (_faded)
            {
                // Faded: only a deliberate rest of the pointer (1.5 s) brings it back; meanwhile a hint of it.
                _holdTimer.Start();
                FadeTo(0.55, 1400);
                return;
            }

            _eye.Eye.Poke(); _eye.Wake();
            SetExpanded(true);
        }
        else
        {
            _holdTimer.Stop();
            SetExpanded(false);
            if (_faded)
            {
                FadeTo(FadedOpacity, 300);
            }

            RestartFade();
        }
    }

    /// <summary>Test copies only: show the expanded state without a pointer.</summary>
    internal void DemoExpand(bool expanded) => SetExpanded(expanded);

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        _expanded = expanded;
        // A gentle "pop": the island grows past its size a little and settles.
        _width.To(expanded ? ExpandedWidth : CollapsedWidth(), expanded ? 190 : 0);
        _height.To(expanded ? ExpandedHeightNow : CollapsedHeight, expanded ? 130 : 0);
        _expand.To(expanded ? 1 : 0);
        Animate();
    }

    private void RestartFade()
    {
        _fadeTimer.Stop();
        if (_settings.FadeAfterSeconds > 0)
        {
            _fadeTimer.Start();
        }
    }

    private void SetFaded(bool faded)
    {
        _faded = faded;
        FadeTo(faded ? FadedOpacity : 1, faded ? 700 : 220);

        // Faded, the eye rests (no glancing, rare blinks): a transparent window is costly to redraw all day.
        _eye.Eye.Resting = faded;
        if (!faded)
        {
            _eye.Wake();
        }
    }

    private void FadeTo(double opacity, int ms)
    {
        var duration = Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
        _island.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    // ------------------------------------------------------------------ drag along the top edge, click

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (ScreenCursor.TryGet(out var p))
        {
            _dragging = true;
            _dragMoved = false;
            _dragStartCursor = p.X;
            _dragStartCenter = _center;
            _pill.CaptureMouse();
            Squeeze(0.95);
        }
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (!_dragging || !ScreenCursor.TryGet(out var p))
        {
            return;
        }

        var screen = ScreenWidthPx();
        var dx = p.X - _dragStartCursor;
        if (Math.Abs(dx) > 4)
        {
            _dragMoved = true;
            SetExpanded(false);
        }

        if (_dragMoved && screen > 0)
        {
            _center = Math.Clamp(_dragStartCenter + (dx / screen), 0.04, 0.96);
            PlaceAt(_center);
        }
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _pill.ReleaseMouseCapture();
        Squeeze(1);
        if (_dragMoved)
        {
            Moved?.Invoke(this, _center);
        }
        else
        {
            // A tap: the eye reacts (double blink and a hop).
            _eye.Eye.Poke(); _eye.Wake();
        }
    }

    private void Squeeze(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to < 1 ? 90 : 420)) { EasingFunction = to < 1 ? new CubicEase { EasingMode = EasingMode.EaseOut } : new ElasticEase { Oscillations = 1, Springiness = 5 } };
        _press.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void PlaceAt(double center)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (center * screenWidth) - (WindowWidth / 2);
        Top = 0;
    }

    private double ScreenWidthPx()
    {
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        return SystemParameters.PrimaryScreenWidth * scale;
    }

    // ------------------------------------------------------------------ springs (frame loop only while moving)

    private void Animate()
    {
        if (Motion.Reduced)
        {
            _width.Snap();
            _height.Snap();
            _expand.Snap();
            _group.Snap();
            _statusWidth?.Snap();
            return;
        }

        if (!_animating)
        {
            _animating = true;
            _lastFrame = Clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void StopAnimating()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrame, 0, 0.034);
        _lastFrame = now;
        var moving = _width.Step(dt) | _height.Step(dt) | _expand.Step(dt) | _group.Step(dt) | (_statusWidth?.Step(dt) ?? false) | (_pingCount?.Step(dt) ?? false);
        if (!moving)
        {
            StopAnimating();
        }
    }

    /// <summary>A damped spring (semi-implicit Euler) that writes its value through <c>apply</c>.</summary>
    private sealed class Spring(double value, double stiffness, double dampingRatio, Action<double> apply)
    {
        private readonly double _damping = 2 * Math.Sqrt(stiffness) * dampingRatio;
        private double _velocity;
        private double _target = value;

        public double Value { get; private set; } = value;

        public void To(double target, double kick = 0)
        {
            _target = target;
            _velocity += kick;
        }

        public void Snap()
        {
            Value = _target;
            _velocity = 0;
            apply(Value);
        }

        public bool Step(double dt)
        {
            var a = (stiffness * (_target - Value)) - (_damping * _velocity);
            _velocity += a * dt;
            Value += _velocity * dt;
            var moving = Math.Abs(_velocity) > 0.05 || Math.Abs(_target - Value) > 0.05;
            if (!moving)
            {
                Value = _target;
                _velocity = 0;
            }

            apply(Value);
            return moving;
        }
    }

    // ------------------------------------------------------------------ Windows plumbing

    private void MakeToolWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)(style | WsExToolWindow | WsExNoActivate));
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Open IBOX", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        var toggle = Item("Connect", () => ToggleRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(toggle);
        menu.Opened += (_, _) =>
        {
            toggle.Header = _status.State switch
            {
                ConnectionState.Disconnected or ConnectionState.Faulted => "Connect",
                ConnectionState.Connected => "Disconnect",
                ConnectionState.Disconnecting => "Stopping…",
                _ => "Cancel connecting",
            };
            toggle.IsEnabled = _status.State != ConnectionState.Disconnecting;
        };
        menu.Items.Add(Item("Customize…", () => CustomizeRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Hide notch", () => HideRequested?.Invoke(this, EventArgs.Empty)));
        _pill.ContextMenu = menu;
    }

    /// <summary>Hidden while a full-screen game, video or presentation is in front (Windows' own "busy" states).</summary>
    private void CheckFullScreen()
    {
        var hide = false;
        if (_settings.HideInFullScreen && SHQueryUserNotificationState(out var state) == 0)
        {
            hide = state is 2 or 3 or 4; // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
        }

        if (hide != _hiddenForFullScreen)
        {
            _hiddenForFullScreen = hide;
            Visibility = hide ? Visibility.Hidden : Visibility.Visible;
            if (!hide)
            {
                Topmost = false;
                Topmost = true;
            }
        }
    }

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
