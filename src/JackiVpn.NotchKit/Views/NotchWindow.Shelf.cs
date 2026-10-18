using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JackiVpn.Desktop.Services;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// The shelf on the notch. Drag anything over the island and it leans towards the pointer and opens into a
/// placeholder of what is coming (a folder, a picture, an app, text, a link…); drop it, the island takes the hit and
/// ripples, swallows it and settles back with a small mark saying something waits on the shelf. Hover to see the
/// shelf; pull an item out and the island holds on to it (it stretches after it, from the bottom only — its top never
/// leaves the edge of the screen) until it lets go and swings back; the item is copied where it is dropped.
/// </summary>
public sealed partial class NotchWindow
{
    private const double ShelfRowHeight = 78;
    private const double ShelfMarkGap = 10;
    private const double DropWidth = 300;
    private const double DropHeight = 96;
    private const double TileSize = 46;
    private const double TearDistance = 72;
    private static readonly FontFamily Mdl2 = new("Segoe MDL2 Assets");
    private static readonly Brush TileBrush = FrozenBrush(0x1C, 0x23, 0x33);
    private static readonly Brush TileHover = FrozenBrush(0x2A, 0x34, 0x4A);
    private static readonly Brush Muted = FrozenBrush(0x9C, 0xA3, 0xAF);

    private readonly TextBlock _shelfMarkCount = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ScaleTransform _shelfMarkPop = new(1, 1);
    private readonly StackPanel _shelfMark = new() { Orientation = Orientation.Horizontal, Height = 18, Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly StackPanel _shelfTiles = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform _shelfRowShift = new();
    private readonly ScrollViewer _shelfRow = new() { Width = ExpandedWidth - 40, Height = ShelfRowHeight - 6, Opacity = 0, Focusable = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, ImageSource> _pictures = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _dropLeave = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private NotchShelf? _shelf;
    private bool _shelfDirty;
    private (ShelfKind Kind, int Count, string Label)? _dropPeek;

    // The drop placeholder.
    private readonly Grid _dropPanel = new() { Width = DropWidth, Height = DropHeight, Opacity = 0, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
    private readonly Border _dropCircle = new() { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly TextBlock _dropGlyph = new() { FontFamily = Mdl2, FontSize = 21, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _dropLabel = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
    private readonly ScaleTransform _dropIconScale = new(1, 1);
    private readonly Rectangle _dropFrame = new() { RadiusX = 16, RadiusY = 16, StrokeThickness = 1.4, StrokeDashArray = [4, 3], Margin = new Thickness(12, 8, 12, 12) };

    // Pulling an item out.
    private ShelfItem? _pullItem;
    private Border? _pullFace;
    private Point _pullStart;
    private bool _pulling;
    private bool _tearing;
    private Border? _floating;
    private Point _floatHome;
    private double _pullU = 0.5;
    private ShelfGhost? _dragGhost;

    /// <summary>The shelf this notch shows and fills.</summary>
    public void AttachShelf(NotchShelf shelf)
    {
        ArgumentNullException.ThrowIfNull(shelf);
        _shelf = shelf;
        void OnChanged(object? sender, EventArgs e)
        {
            RenderShelf();
            Render();
        }

        shelf.Changed += OnChanged;
        Closed += (_, _) => shelf.Changed -= OnChanged;
        RenderShelf();
        Render();
    }

    private bool ShowsShelf => _settings.Shelf && _shelf is { Count: > 0 };

    private double ShelfMarkWidth() => ShowsShelf ? ShelfMarkGap + _shelfMark.DesiredSize.Width : 0;

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Color KindColor(ShelfKind kind) => kind switch
    {
        ShelfKind.Folder => Color.FromRgb(0xFB, 0xBF, 0x24),
        ShelfKind.Image => Color.FromRgb(0x34, 0xD3, 0x99),
        ShelfKind.Video => Color.FromRgb(0xF4, 0x72, 0xB6),
        ShelfKind.Audio => Color.FromRgb(0xA7, 0x8B, 0xFA),
        ShelfKind.App => Color.FromRgb(0x60, 0xA5, 0xFA),
        ShelfKind.Archive => Color.FromRgb(0xFC, 0xA5, 0xA5),
        ShelfKind.Document => Color.FromRgb(0x93, 0xC5, 0xFD),
        ShelfKind.Link => Color.FromRgb(0x38, 0xBD, 0xF8),
        ShelfKind.Text => Color.FromRgb(0xE5, 0xE7, 0xEB),
        _ => Color.FromRgb(0x5A, 0xAC, 0xFF),
    };

    private void BuildShelf()
    {
        _shelfMark.RenderTransform = _shelfMarkPop;
        _shelfMark.Children.Add(new TextBlock { FontFamily = Mdl2, Text = "", FontSize = 12.5, Foreground = Brushes.White, Opacity = 0.9, VerticalAlignment = VerticalAlignment.Center });
        _shelfMark.Children.Add(_shelfMarkCount);
        _shelfMark.ToolTip = "On the shelf — hover to see";
        _main.Children.Add(_shelfMark);

        _shelfRow.Content = _shelfTiles;
        _shelfRow.RenderTransform = _shelfRowShift;
        _shelfRow.PreviewMouseWheel += (_, e) =>
        {
            _shelfRow.ScrollToHorizontalOffset(_shelfRow.HorizontalOffset - (e.Delta / 2.0));
            e.Handled = true;
        };
        Canvas.SetLeft(_shelfRow, 20);
        _main.Children.Add(_shelfRow);

        _dropLeave.Tick += (_, _) =>
        {
            _dropLeave.Stop();
            LeaveDrop();
        };
    }

    /// <summary>The placeholder a drag turns the island into: the kind of thing coming, its name, a dashed frame.</summary>
    private Grid BuildDropPanel()
    {
        _dropCircle.RenderTransform = _dropIconScale;
        _dropCircle.Child = _dropGlyph;
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        words.Children.Add(new TextBlock { Text = "Drop to keep it on the notch", FontSize = 11, Foreground = Muted });
        words.Children.Add(_dropLabel);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 2) };
        row.Children.Add(_dropCircle);
        row.Children.Add(words);
        _dropPanel.Children.Add(_dropFrame);
        _dropPanel.Children.Add(row);
        return _dropPanel;
    }

    private void RenderShelf()
    {
        if (_pullItem is not null)
        {
            // Not while an item is being pulled out (its tile holds the pointer).
            _shelfDirty = true;
            return;
        }

        _shelfDirty = false;
        var show = ShowsShelf;
        _shelfMark.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _shelfMarkCount.Text = (_shelf?.Count ?? 0).ToString(CultureInfo.InvariantCulture);
        _shelfMark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _shelfRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _shelfTiles.Children.Clear();
        if (!show || _shelf is null)
        {
            return;
        }

        foreach (var item in _shelf.Items)
        {
            _shelfTiles.Children.Add(Tile(item));
        }

        _shelfTiles.Children.Add(ClearTile());
    }

    // ------------------------------------------------------------------ tiles

    private FrameworkElement Tile(ShelfItem item)
    {
        var glyph = new TextBlock
        {
            FontFamily = Mdl2,
            Text = NotchShelf.Glyph(item.Kind),
            FontSize = 19,
            Foreground = new SolidColorBrush(KindColor(item.Kind)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image { Width = 34, Height = 34, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        var face = new Border
        {
            Width = TileSize,
            Height = TileSize,
            CornerRadius = new CornerRadius(12),
            Background = TileBrush,
            Cursor = Cursors.Hand,
            Child = new Grid { Children = { glyph, image } },
            ToolTip = item.Owned ? item.Name : item.Name + Environment.NewLine + item.Path,
        };
        if (_pictures.TryGetValue(item.Id, out var known))
        {
            Show(known);
        }
        else if (item.Kind is not (ShelfKind.Text or ShelfKind.Link))
        {
            _ = LoadAsync();
        }

        var remove = new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(8.5),
            Background = FrozenBrush(0x37, 0x41, 0x51),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, -5, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Hidden,
            ToolTip = "Remove from the shelf",
            Child = new TextBlock { FontFamily = Mdl2, Text = "", FontSize = 7, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        remove.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _pictures.Remove(item.Id);
            _shelf?.Remove(item.Id);
        };

        var top = new Grid { Width = TileSize, Height = TileSize, Children = { face, remove } };
        var name = new TextBlock { Text = item.Name, FontSize = 10, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Width = 54, Margin = new Thickness(0, 4, 0, 0) };
        var host = new StackPanel { Width = 54, Margin = new Thickness(0, 0, 8, 0), Opacity = item.Exists ? 1 : 0.4 };
        host.Children.Add(top);
        host.Children.Add(name);
        top.MouseEnter += (_, _) =>
        {
            face.Background = TileHover;
            remove.Visibility = Visibility.Visible;
        };
        top.MouseLeave += (_, _) =>
        {
            face.Background = TileBrush;
            remove.Visibility = Visibility.Hidden;
        };
        face.MouseLeftButtonDown += (_, e) => StartPull(item, face, e);
        face.MouseMove += (_, e) => MovePull(e);
        face.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            EndPull();
        };
        face.LostMouseCapture += (_, _) =>
        {
            if (!_tearing)
            {
                EndPull();
            }
        };
        face.GiveFeedback += (_, e) =>
        {
            _dragGhost?.Follow();
            e.UseDefaultCursors = true;
            e.Handled = true;
        };
        return host;

        void Show(ImageSource picture)
        {
            image.Source = picture;
            image.Visibility = Visibility.Visible;
            glyph.Visibility = Visibility.Collapsed;
        }

        async System.Threading.Tasks.Task LoadAsync()
        {
            if (await ShellImages.GetAsync(item.Path, 64) is { } picture)
            {
                _pictures[item.Id] = picture;
                Show(picture);
            }
        }
    }

    private FrameworkElement ClearTile()
    {
        var face = new Border
        {
            Width = TileSize,
            Height = TileSize,
            CornerRadius = new CornerRadius(12),
            BorderBrush = FrozenBrush(0x37, 0x41, 0x51),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Empty the shelf (your files stay where they are)",
            Child = new TextBlock { FontFamily = Mdl2, Text = "", FontSize = 15, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        face.MouseEnter += (_, _) => face.Background = TileBrush;
        face.MouseLeave += (_, _) => face.Background = Brushes.Transparent;
        face.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _pictures.Clear();
            _shelf?.Clear();
        };
        var host = new StackPanel { Width = 54 };
        host.Children.Add(face);
        host.Children.Add(new TextBlock { Text = "Clear", FontSize = 10, Foreground = Muted, TextAlignment = TextAlignment.Center, Width = 54, Margin = new Thickness(0, 4, 0, 0) });
        return host;
    }

    // ------------------------------------------------------------------ dropping onto the notch

    /// <summary>Copy (or link) when the source allows it — never move: the original always stays where it is.</summary>
    private static DragDropEffects Accepted(DragDropEffects allowed) =>
        allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : allowed.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.None;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_settings.Shelf || _shelf is null || _panel == NotchPanel.Ask || _pullItem is not null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (_panel != NotchPanel.Drop)
        {
            _dropPeek = NotchShelf.Peek(e.Data);
            if (_dropPeek is null || Accepted(e.AllowedEffects) == DragDropEffects.None)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            EnterDrop(_dropPeek.Value);
        }

        _dropLeave.Stop();
        e.Effects = Accepted(e.AllowedEffects);

        // The island leans towards the pointer: a gentle bulge right under it.
        _body.Membrane.Pull(e.GetPosition(_pill).X / Math.Max(1, _pill.ActualWidth), 9, 0.16, 260);
        Animate();
    }

    private void EnterDrop((ShelfKind Kind, int Count, string Label) peek)
    {
        var color = KindColor(peek.Kind);
        _dropGlyph.Text = NotchShelf.Glyph(peek.Kind);
        _dropGlyph.Foreground = new SolidColorBrush(color);
        _dropCircle.Background = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B));
        _dropFrame.Stroke = new SolidColorBrush(Color.FromArgb(0x8C, color.R, color.G, color.B));
        _dropLabel.Text = peek.Label;
        _dropIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _dropIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _dropCircle.BeginAnimation(OpacityProperty, null);
        _dropCircle.Opacity = 1;
        if (!Motion.Reduced)
        {
            // It breathes while waiting for the drop.
            var breathe = new DoubleAnimation(1, 1.08, TimeSpan.FromMilliseconds(820)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            _dropIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            _dropIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        }

        // A gentle grow, not the hover's pop.
        _width.Tune(120, 0.74);
        _height.Tune(110, 0.72);
        _holdTimer.Stop();
        ShowPanel(NotchPanel.Drop);
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Moving between parts of the island also "leaves" one of them: only a real exit closes the placeholder.
        if (_panel == NotchPanel.Drop)
        {
            _dropLeave.Stop();
            _dropLeave.Start();
        }
    }

    private void LeaveDrop()
    {
        if (_panel != NotchPanel.Drop)
        {
            return;
        }

        _dropPeek = null;
        _body.Membrane.Release();
        _width.Untune();
        _height.Untune();
        _dropIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _dropIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ShowPanel(NotchPanel.None);
    }

    private void OnDropped(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropLeave.Stop();
        if (_panel != NotchPanel.Drop || _shelf is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        IReadOnlyList<ShelfItem> added;
        try
        {
            added = _shelf.Add(e.Data);
        }
        catch (Exception ex) when (ex is COMException or System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            added = [];
        }

        e.Effects = added.Count > 0 ? Accepted(e.AllowedEffects) : DragDropEffects.None;
        var x = e.GetPosition(_pill).X / Math.Max(1, _pill.ActualWidth);
        _body.Membrane.Release();
        if (added.Count == 0)
        {
            LeaveDrop();
            return;
        }

        // The hit: the edge under the drop jumps and ripples outwards, the island gives a little and bounces back.
        _body.Membrane.Impulse(x, 520, 0.09);
        _height.To(_height.Target, 260);
        Animate();

        // It swallows the item: the icon swells, then sinks away.
        var reduced = Motion.Reduced;
        TimeSpan Ms(int ms) => reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
        var gulp = new DoubleAnimationUsingKeyFrames();
        gulp.KeyFrames.Add(new EasingDoubleKeyFrame(1.22, KeyTime.FromTimeSpan(Ms(110)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        gulp.KeyFrames.Add(new EasingDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(Ms(380)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        _dropIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, gulp);
        _dropIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, gulp);
        _dropCircle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(200)) { BeginTime = Ms(200) });
        After(reduced ? 0 : 400, () =>
        {
            LeaveDrop();
            PopShelfMark();
        });
    }

    /// <summary>The mark appears with a little bounce (or counts up).</summary>
    private void PopShelfMark()
    {
        if (Motion.Reduced)
        {
            return;
        }

        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new ElasticEase { Oscillations = 2, Springiness = 5 } };
        _shelfMarkPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _shelfMarkPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void After(int ms, Action action)
    {
        if (ms <= 0)
        {
            action();
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ pulling an item out

    private static double Rubber(double v, double limit) => Math.Sign(v) * limit * (1 - Math.Exp(-Math.Abs(v) / limit));

    private void StartPull(ShelfItem item, Border face, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            Open(item);
            return;
        }

        _pullItem = item;
        _pullFace = face;
        _pullStart = e.GetPosition(this);
        _pulling = false;
        _tearing = false;
        face.CaptureMouse();
    }

    private void MovePull(MouseEventArgs e)
    {
        if (_pullItem is null || _pullFace is null || _tearing || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var d = e.GetPosition(this) - _pullStart;
        if (!_pulling)
        {
            if (d.Length < 3)
            {
                return;
            }

            BeginPull();
        }

        // Sticky: the item follows reluctantly and the island stretches after it — down, and a little sideways.
        var rx = Rubber(d.X, 30);
        var ry = Rubber(Math.Max(0, d.Y), 58) + Rubber(Math.Min(0, d.Y), 6);
        if (_floating is not null)
        {
            Canvas.SetLeft(_floating, _floatHome.X + rx);
            Canvas.SetTop(_floating, _floatHome.Y + ry);
        }

        _body.LateralCenter = _pullU;
        _body.Membrane.Pull(_pullU, Math.Max(0, ry * 0.92), 0.075, 1600);
        _lateral.To(rx * 0.8);
        Animate();
        if (d.Length > TearDistance)
        {
            Tear();
        }
    }

    private void BeginPull()
    {
        if (_pullItem is not { } item || _pullFace is not { } face)
        {
            return;
        }

        _pulling = true;
        _fadeTimer.Stop();
        _floatHome = face.TranslatePoint(new Point(0, 0), _pill);
        _pullU = Math.Clamp((_floatHome.X + (TileSize / 2)) / Math.Max(1, _pill.ActualWidth), 0, 1);
        _floating = new Border
        {
            Width = TileSize,
            Height = TileSize,
            CornerRadius = new CornerRadius(12),
            Background = TileHover,
            Child = Picture(item, 34),
        };
        Canvas.SetLeft(_floating, _floatHome.X);
        Canvas.SetTop(_floating, _floatHome.Y);
        _overlay.Children.Add(_floating);
        face.Opacity = 0.25;
    }

    private UIElement Picture(ShelfItem item, double size) =>
        _pictures.TryGetValue(item.Id, out var picture)
            ? new Image { Source = picture, Width = size, Height = size, Stretch = Stretch.Uniform }
            : new TextBlock { FontFamily = Mdl2, Text = NotchShelf.Glyph(item.Kind), FontSize = size * 0.56, Foreground = new SolidColorBrush(KindColor(item.Kind)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// It comes loose: the island lets go and swings back up, and the item flies on with the pointer as a normal
    /// drag — dropped anywhere (Explorer, the desktop, a chat, an editor) it is copied there.
    /// </summary>
    private void Tear()
    {
        if (_pullItem is not { } item || _pullFace is not { } face)
        {
            return;
        }

        _tearing = true;
        _body.Membrane.Release();
        _body.Membrane.Impulse(_pullU, -160, 0.1);
        _lateral.To(0);
        Animate();
        if (_floating is not null)
        {
            _overlay.Children.Remove(_floating);
            _floating = null;
        }

        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        _dragGhost = new ShelfGhost(Picture(item, 36), fromDevice);
        _dragGhost.Follow();
        _dragGhost.Show();
        face.ReleaseMouseCapture();
        var result = DragDropEffects.None;
        try
        {
            result = DragDrop.DoDragDrop(face, NotchShelf.DataFor(item), DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or System.IO.IOException)
        {
            result = DragDropEffects.None;
        }

        // Dropped: it fades where it landed. Not dropped: it flies back home.
        var home = face.IsVisible ? face.PointToScreen(new Point(TileSize / 2, TileSize / 2)) : (Point?)null;
        _dragGhost.Finish(result != DragDropEffects.None, home is { } h ? fromDevice.Transform(h) : null);
        _dragGhost = null;
        face.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(360)) { BeginTime = TimeSpan.FromMilliseconds(result == DragDropEffects.None ? 300 : 0) });
        FinishPull();
    }

    private void EndPull()
    {
        if (_pullItem is null || _tearing)
        {
            return;
        }

        var face = _pullFace;
        var floating = _floating;
        _pullItem = null;
        face?.ReleaseMouseCapture();
        if (_pulling)
        {
            // Let go too early: the item snaps back into its place and the island swings back.
            _body.Membrane.Release();
            _lateral.To(0);
            Animate();
            if (floating is not null)
            {
                var reduced = Motion.Reduced;
                var back = new DoubleAnimation(_floatHome.X, reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(420)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 6 } };
                var up = new DoubleAnimation(_floatHome.Y, reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(420)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 6 } };
                up.Completed += (_, _) =>
                {
                    _overlay.Children.Remove(floating);
                    if (face is not null)
                    {
                        face.Opacity = 1;
                    }
                };
                floating.BeginAnimation(Canvas.LeftProperty, back);
                floating.BeginAnimation(Canvas.TopProperty, up);
            }
        }

        _floating = null;
        FinishPull();
    }

    private void FinishPull()
    {
        _pullItem = null;
        _pullFace = null;
        _pulling = false;
        _tearing = false;
        if (_shelfDirty)
        {
            RenderShelf();
            Render();
        }

        if (!_pill.IsMouseOver)
        {
            OnHover(false);
        }
    }

    private static void Open(ShelfItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            // gone or no app for it
        }
    }
}
