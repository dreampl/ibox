using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using JackiVpn.Application.Media;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Music first: while something plays, the open island is a media player — cover, title and artist, a seekable
/// progress line, previous / play / next — and the connection shrinks to the small eye and the ping in the corner.
/// Collapsed, the cover and three small equaliser bars close the line. When the music stops the VPN details come
/// back. The clock behind the progress runs only while that progress is on screen.
/// </summary>
public sealed partial class NotchWindow
{
    private const double MediaCardHeight = 128;
    private const double ArtSize = 76;
    private const double MediaTextLeft = 22 + ArtSize + 16;
    private const double MusicMarkGap = 10;

    private readonly Canvas _mediaCard = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Border _art = new() { Width = ArtSize, Height = ArtSize, CornerRadius = new CornerRadius(14), Background = FrozenBrush(0x1F, 0x26, 0x36) };
    private readonly TextBlock _artNote = new() { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "", FontSize = 28, Foreground = FrozenBrush(0x5A, 0xAC, 0xFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _mediaTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _mediaArtist = new() { FontSize = 12, Foreground = FrozenBrush(0x9C, 0xA3, 0xAF), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _track = new() { Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Rectangle _trackBack = new() { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
    private readonly Rectangle _trackFill = new() { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _knob = new() { Width = 9, Height = 9, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
    private readonly TextBlock _elapsed = new() { FontSize = 10.5, FontFamily = new FontFamily("Consolas"), Foreground = FrozenBrush(0x9C, 0xA3, 0xAF) };
    private readonly TextBlock _remaining = new() { FontSize = 10.5, FontFamily = new FontFamily("Consolas"), Foreground = FrozenBrush(0x9C, 0xA3, 0xAF), TextAlignment = TextAlignment.Right };
    private readonly TextBlock _cardPlayGlyph = new() { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Foreground = Brushes.Black, Text = "" };
    private readonly StackPanel _cardControls = new() { Orientation = Orientation.Horizontal };

    /// <summary>The connection in the media card's corner: a coloured dot and the ping, beside the small eye.</summary>
    private readonly TextBlock _cornerVpn = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Opacity = 0, IsHitTestVisible = false };

    /// <summary>Collapsed: the cover and three equaliser bars at the end of the line.</summary>
    private readonly StackPanel _musicMark = new() { Orientation = Orientation.Horizontal, Height = 18, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _markArt = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = FrozenBrush(0x1F, 0x26, 0x36) };
    private readonly ScaleTransform[] _bars = [new(1, 0.4), new(1, 0.4), new(1, 0.4)];
    private bool _barsRunning;

    private readonly DispatcherTimer _mediaClock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Spring _mediaMix;
    private bool _mediaFirst;
    private bool _seeking;
    private double _seekFraction;
    private byte[]? _shownArt;

    /// <summary>Jump to a position in what is playing (the user dragged the progress line).</summary>
    public event EventHandler<TimeSpan>? SeekRequested;

    private void BuildMedia()
    {
        _art.Child = _artNote;
        _art.ClipToBounds = true;
        _mediaCard.Children.Add(_art);
        Canvas.SetLeft(_art, 22);
        Canvas.SetTop(_art, 24);

        var textWidth = ExpandedWidth - MediaTextLeft - 22;
        _mediaTitle.Width = textWidth - 70; // the corner keeps room for the ping and the eye
        _mediaArtist.Width = textWidth;
        _mediaCard.Children.Add(_mediaTitle);
        Canvas.SetLeft(_mediaTitle, MediaTextLeft);
        Canvas.SetTop(_mediaTitle, 24);
        _mediaCard.Children.Add(_mediaArtist);
        Canvas.SetLeft(_mediaArtist, MediaTextLeft);
        Canvas.SetTop(_mediaArtist, 44);

        // The progress line: 3 px, 4 px with a 9 px knob under the pointer; press or drag anywhere on it to seek.
        _track.Width = textWidth;
        _track.Children.Add(_trackBack);
        _track.Children.Add(_trackFill);
        _track.Children.Add(_knob);
        _track.MouseEnter += (_, _) => HoverTrack(true);
        _track.MouseLeave += (_, _) => HoverTrack(false);
        _track.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _seeking = true;
            _track.CaptureMouse();
            SeekPreview(e.GetPosition(_track).X);
        };
        _track.MouseMove += (_, e) =>
        {
            if (_seeking)
            {
                SeekPreview(e.GetPosition(_track).X);
            }
        };
        _track.MouseLeftButtonUp += (_, e) =>
        {
            if (!_seeking)
            {
                return;
            }

            e.Handled = true;
            _seeking = false;
            _track.ReleaseMouseCapture();
            if (_nowPlaying is { Length: { } length } np && np.HasTimeline)
            {
                var target = TimeSpan.FromSeconds(length.TotalSeconds * _seekFraction);
                SeekRequested?.Invoke(this, target);

                // Show the new place at once; the player's own report follows.
                _nowPlaying = np with { Position = target, PositionAt = DateTimeOffset.UtcNow };
            }

            UpdateProgress();
            HoverTrack(_track.IsMouseOver);
        };
        _mediaCard.Children.Add(_track);
        Canvas.SetLeft(_track, MediaTextLeft);
        Canvas.SetTop(_track, 64);
        _elapsed.Width = _remaining.Width = textWidth / 2;
        _mediaCard.Children.Add(_elapsed);
        Canvas.SetLeft(_elapsed, MediaTextLeft);
        Canvas.SetTop(_elapsed, 79);
        _mediaCard.Children.Add(_remaining);
        Canvas.SetLeft(_remaining, MediaTextLeft + (textWidth / 2));
        Canvas.SetTop(_remaining, 79);

        _cardControls.Children.Add(CardButton(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = Brushes.White, Text = "" }, -1, "Previous", primary: false));
        _cardControls.Children.Add(CardButton(_cardPlayGlyph, 0, "Play / pause", primary: true));
        _cardControls.Children.Add(CardButton(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = Brushes.White, Text = "" }, 1, "Next", primary: false));
        _cardControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _mediaCard.Children.Add(_cardControls);
        Canvas.SetLeft(_cardControls, MediaTextLeft + ((textWidth - _cardControls.DesiredSize.Width) / 2));
        Canvas.SetTop(_cardControls, 92);
        _main.Children.Add(_mediaCard);
        _main.Children.Add(_cornerVpn);

        _musicMark.Children.Add(_markArt);
        var bars = new Canvas { Width = 14, Height = 14, Margin = new Thickness(6, 2, 0, 0) };
        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = new Rectangle { Width = 2.6, Height = 14, RadiusX = 1.3, RadiusY = 1.3, Fill = FrozenBrush(0xC7, 0xD2, 0xFE), RenderTransform = _bars[i], RenderTransformOrigin = new Point(0.5, 1) };
            Canvas.SetLeft(bar, i * 4.6);
            bars.Children.Add(bar);
        }

        _musicMark.Children.Add(bars);
        _main.Children.Add(_musicMark);
        _mediaClock.Tick += (_, _) => UpdateProgress();
    }

    private Button CardButton(TextBlock glyph, int command, string tip, bool primary)
    {
        var size = primary ? 34 : 30;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2.0));
        border.SetValue(Border.BackgroundProperty, primary ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var scale = new ScaleTransform(1, 1);
        var b = new Button
        {
            Content = glyph,
            Width = size,
            Height = size,
            Margin = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        // Pressed: a small give (0.9) that springs back.
        void Press(double to)
        {
            var d = Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(to < 1 ? 90 : 260);
            var anim = new DoubleAnimation(to, d) { EasingFunction = to < 1 ? null : new ElasticEase { Oscillations = 1, Springiness = 6 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        b.PreviewMouseLeftButtonDown += (_, _) => Press(0.9);
        b.PreviewMouseLeftButtonUp += (_, _) => Press(1);
        b.MouseLeave += (_, _) => Press(1);
        b.Click += (_, _) => MediaCommand?.Invoke(this, command);
        return b;
    }

    /// <summary>
    /// Whether the open island is the media player. Playing turns it on; a pause keeps it while the island stays
    /// open (so the play button is still under the pointer), and it gives way once the island closes.
    /// </summary>
    private void UpdateMediaFirst()
    {
        var playing = ShowsMusic && _nowPlaying is { IsPlaying: true };
        var first = playing || (_mediaFirst && _expanded && ShowsMusic);
        if (first != _mediaFirst)
        {
            _mediaFirst = first;
            _mediaMix.To(first ? 1 : 0);
            Animate();
        }
    }

    /// <summary>Fills the card and the collapsed mark from what is playing.</summary>
    private void RenderMedia()
    {
        UpdateMediaFirst();
        var np = _nowPlaying;
        _musicMark.Visibility = ShowsMusic && np is { IsPlaying: true } ? Visibility.Visible : Visibility.Collapsed;
        if (np is null)
        {
            SyncMediaClocks();
            return;
        }

        _mediaTitle.Text = np.Title;
        _mediaArtist.Text = np.Artist ?? AppName(np.App);
        _cardPlayGlyph.Text = np.IsPlaying ? "" : "";
        if (!ReferenceEquals(np.Artwork, _shownArt))
        {
            _shownArt = np.Artwork;
            var image = Decode(np.Artwork, (int)ArtSize * 2);
            _art.Background = image is null ? FrozenBrush(0x1F, 0x26, 0x36) : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _artNote.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            _markArt.Background = image is null ? FrozenBrush(0x1F, 0x26, 0x36) : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }

        // The corner: connection colour and ping (or the state's colour alone).
        var connected = _status.State == ConnectionState.Connected;
        var color = connected ? ConnectedBlue : _status.State == ConnectionState.Faulted ? Color.FromRgb(0xF8, 0x71, 0x71) : Grey;
        _cornerVpn.Inlines.Clear();
        if (ConnectionVisible)
        {
            _cornerVpn.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = new SolidColorBrush(color), FontSize = 9 });
            _cornerVpn.Inlines.Add(new System.Windows.Documents.Run(connected && _status.LatencyMs is { } ms ? $"{ms:0} ms" : connected ? "VPN" : "Off") { Foreground = Brushes.White });
        }

        _cornerVpn.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _musicMark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        UpdateProgress();
        SyncMediaClocks();
    }

    private static string AppName(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        // "Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Chrome" → a readable name.
        var name = id.Contains('!', StringComparison.Ordinal) ? id[(id.LastIndexOf('!') + 1)..] : id;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static BitmapImage? Decode(byte[]? bytes, int width)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private void HoverTrack(bool over)
    {
        var on = over || _seeking;
        _trackBack.Height = _trackFill.Height = on ? 4 : 3;
        _knob.Opacity = on && _nowPlaying is { HasTimeline: true } ? 1 : 0;
    }

    private void SeekPreview(double x)
    {
        _seekFraction = Math.Clamp(x / Math.Max(1, _track.Width), 0, 1);
        if (_nowPlaying is { Length: { } length })
        {
            ShowProgress(_seekFraction, TimeSpan.FromSeconds(length.TotalSeconds * _seekFraction), length);
        }
    }

    private void UpdateProgress()
    {
        if (_seeking)
        {
            return;
        }

        if (_nowPlaying is { Length: { } length } np && np.PositionNow(DateTimeOffset.UtcNow) is { } at)
        {
            ShowProgress(at.TotalSeconds / length.TotalSeconds, at, length);
            _track.Visibility = _elapsed.Visibility = _remaining.Visibility = Visibility.Visible;
        }
        else
        {
            _track.Visibility = _elapsed.Visibility = _remaining.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowProgress(double fraction, TimeSpan at, TimeSpan length)
    {
        var f = Math.Clamp(fraction, 0, 1);
        _trackFill.Width = _track.Width * f;
        _knob.Margin = new Thickness(Math.Clamp((_track.Width * f) - 4.5, 0, _track.Width - 9), 0, 0, 0);
        _elapsed.Text = NowPlaying.Clock(at);
        _remaining.Text = "-" + NowPlaying.Clock(length - at);
    }

    /// <summary>
    /// The progress clock runs only while the open card shows a moving timeline; the equaliser only while the
    /// collapsed island is on screen, awake and something plays. Otherwise nothing ticks.
    /// </summary>
    private void SyncMediaClocks()
    {
        var visible = IsVisible && _presence == Domain.Visuals.NotchPresence.Normal;
        var clock = visible && _expanded && _mediaFirst && _nowPlaying is { IsPlaying: true, HasTimeline: true };
        if (clock != _mediaClock.IsEnabled)
        {
            _mediaClock.IsEnabled = clock;
        }

        var bars = visible && !_expanded && !_faded && _seeThrough == SeeThrough.None && ShowsMusic && _nowPlaying is { IsPlaying: true } && !Motion.Reduced;
        if (bars == _barsRunning)
        {
            return;
        }

        _barsRunning = bars;
        for (var i = 0; i < _bars.Length; i++)
        {
            if (bars)
            {
                var anim = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(380 + (i * 130)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 90),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Timeline.SetDesiredFrameRate(anim, 24);
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            else
            {
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _bars[i].ScaleY = 0.4;
            }
        }
    }

    private double MusicMarkWidth() => _musicMark.Visibility == Visibility.Visible ? MusicMarkGap + _musicMark.DesiredSize.Width : 0;

    /// <summary>
    /// Lays out the media card for the expansion <paramref name="k"/> and the mix <paramref name="m"/> (0 = VPN
    /// details, 1 = media player): its parts rise in one after another like the detail rows.
    /// </summary>
    private void PlaceMedia(double k, double m, double width)
    {
        var show = k * m;
        _mediaCard.Opacity = Math.Clamp((show - 0.25) / 0.6, 0, 1);
        _mediaCard.IsHitTestVisible = show > 0.6;
        _cornerVpn.Opacity = _mediaCard.Opacity;
        Canvas.SetLeft(_cornerVpn, width - 22 - 18 - 8 - _cornerVpn.DesiredSize.Width);
        Canvas.SetTop(_cornerVpn, 22);
    }
}
