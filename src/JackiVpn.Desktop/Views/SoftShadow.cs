using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JackiVpn.Desktop.Services;

namespace JackiVpn.Desktop.Views;

/// <summary>Which of the theme's shadows (ThemeManager's E.* effects) a <see cref="SoftShadow"/> draws.</summary>
public enum ShadowKind
{
    /// <summary>E.ClayShadow: the cards.</summary>
    Clay = 0,

    /// <summary>E.ClayShadowRaised: the dashboard hero.</summary>
    Raised,

    /// <summary>E.SoftShadow: tiles, the orb's depth.</summary>
    Soft,

    /// <summary>E.ClayLight: the light theme's glow up-left (nothing on the dark theme).</summary>
    Light,

    /// <summary>E.AccentGlow: the selected rail item.</summary>
    Accent,
}

/// <summary>
/// A soft drop shadow drawn from a picture instead of a live <see cref="DropShadowEffect"/>. A live effect re-runs its
/// blur on the graphics card whenever anything near it redraws — the eye, the spinner, the breathing halo, a chart —
/// which made the window lag. Here the blur is rendered once per size and theme (at reduced resolution: a shadow has
/// no detail) and shared between equal shapes; while a window is being resized the last picture stretches until the
/// size settles. Place it behind the shape it belongs to, the same size; it draws beyond its bounds.
/// </summary>
public sealed class SoftShadow : FrameworkElement
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(SoftShadow), new FrameworkPropertyMetadata(new CornerRadius(20), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(ShadowKind), typeof(SoftShadow), new FrameworkPropertyMetadata(ShadowKind.Clay, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A shadow of its own (frozen) instead of the theme's <see cref="Kind"/>.</summary>
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(
        nameof(Shadow), typeof(DropShadowEffect), typeof(SoftShadow), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int MaxCached = 64;
    private static readonly Dictionary<string, Picture> Cache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _settle;
    private Picture? _shown;
    private string? _shownKey;
    private string? _waitingKey;
    private string? _settledKey;

    private sealed record Picture(BitmapSource? Bitmap, Thickness Pad);

    public SoftShadow()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _settledKey = _waitingKey;
            InvalidateVisual();
        };
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _shown is null)
            {
                InvalidateVisual(); // skipped while hidden (a page built ahead of its first visit)
            }
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            _settle.Stop();
        };
    }

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public ShadowKind Kind { get => (ShadowKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public DropShadowEffect? Shadow { get => (DropShadowEffect?)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Cache.Clear();
        _shown = null;
        _shownKey = null;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    private string EffectKey => Kind switch
    {
        ShadowKind.Raised => "E.ClayShadowRaised",
        ShadowKind.Soft => "E.SoftShadow",
        ShadowKind.Light => "E.ClayLight",
        ShadowKind.Accent => "E.AccentGlow",
        _ => "E.ClayShadow",
    };

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var w = Math.Round(ActualWidth);
        var h = Math.Round(ActualHeight);
        if (w < 1 || h < 1 || !IsVisible || (Shadow ?? TryFindResource(EffectKey)) is not DropShadowEffect effect)
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var r = CornerRadius;
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{w}x{h}|{r.TopLeft},{r.TopRight},{r.BottomRight},{r.BottomLeft}|{EffectKey}|{effect.Color}|{effect.BlurRadius}|{effect.ShadowDepth}|{effect.Direction}|{effect.Opacity}|{scale}");
        if (key != _shownKey)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                (_shown, _shownKey) = (cached, key);
            }
            else if (_shown is null || _settledKey == key)
            {
                // First picture now (a card never shows up without its shadow), or the size has settled.
                (_shown, _shownKey) = (Render(w, h, r, effect, scale), key);
                Remember(key, _shown);
            }
            else
            {
                // The size is still moving (a window resize): stretch the last picture, render once it settles.
                _waitingKey = key;
                _settle.Stop();
                _settle.Start();
            }
        }
        if (_shown?.Bitmap is { } bitmap)
        {
            var p = _shown.Pad;
            drawingContext.DrawImage(bitmap, new Rect(-p.Left, -p.Top, ActualWidth + p.Left + p.Right, ActualHeight + p.Top + p.Bottom));
        }
    }

    private static void Remember(string key, Picture picture)
    {
        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        Cache[key] = picture;
    }

    /// <summary>The shape with its effect, rendered once to a small bitmap (a third to a half of the screen's resolution).</summary>
#if DEBUG
    /// <summary>Time spent rendering shadow pictures (for the performance probe).</summary>
    internal static double RenderMs { get; set; }

    internal static int Renders { get; set; }
#endif

    private static Picture Render(double w, double h, CornerRadius radius, DropShadowEffect effect, double scale)
    {
#if DEBUG
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return RenderCore(w, h, radius, effect, scale);
        }
        finally
        {
            RenderMs += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Renders++;
        }
#else
        return RenderCore(w, h, radius, effect, scale);
#endif
    }

    private static Picture RenderCore(double w, double h, CornerRadius radius, DropShadowEffect effect, double scale)
    {
        var reach = Math.Ceiling(effect.BlurRadius + effect.ShadowDepth + 2);
        var pad = new Thickness(reach);
        var resolution = effect.BlurRadius >= 24 ? 0.34 : 0.5;
        var dpi = 96 * scale * resolution;
        var shape = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = radius,
            // The shape's own colour is the shadow's: its soft low-resolution edge then reads as part of the shadow.
            Background = new SolidColorBrush(effect.Color),
            Effect = effect,
        };
        var host = new Grid { Width = w + (2 * reach), Height = h + (2 * reach), Children = { shape } };
        shape.Margin = pad;
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
        var pixelsW = Math.Max(1, (int)Math.Ceiling(host.Width * dpi / 96));
        var pixelsH = Math.Max(1, (int)Math.Ceiling(host.Height * dpi / 96));
        var bitmap = new RenderTargetBitmap(pixelsW, pixelsH, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        return new Picture(bitmap, pad);
    }
}
