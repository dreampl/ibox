using Android.Graphics;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;

namespace JackiVpn.Mobile.Controls;

/// <summary>
/// Live frosted-glass backdrop: draws the blurred page content that lies underneath this view (Android 12+, on
/// the GPU via RenderNode + RenderEffect; nothing is copied to the CPU). <see cref="FadeTop"/> fades the blur in
/// from the top, so content gets progressively blurrier towards the bottom of the screen. Older Android versions
/// show nothing here and the translucent tint above it carries the look.
/// </summary>
public sealed class BlurBackdrop : View
{
    public static readonly BindableProperty FadeTopProperty = BindableProperty.Create(nameof(FadeTop), typeof(bool), typeof(BlurBackdrop), false);

    public static readonly BindableProperty RadiusProperty = BindableProperty.Create(nameof(Radius), typeof(double), typeof(BlurBackdrop), 26d);

    public BlurBackdrop()
    {
        InputTransparent = true;
    }

    /// <summary>The view whose content is blurred (the page host); set once by the main page.</summary>
    public static VisualElement? Source { get; set; }

    public bool FadeTop { get => (bool)GetValue(FadeTopProperty); set => SetValue(FadeTopProperty, value); }

    /// <summary>Blur radius in dp.</summary>
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    /// <summary>Redraws every live backdrop (during page transitions, when content moves without scrolling).</summary>
    public static void InvalidateAll()
    {
        foreach (var v in BlurBackdropView.Live.ToArray())
        {
            v.Invalidate();
        }
    }
}

public sealed class BlurBackdropHandler() : ViewHandler<BlurBackdrop, BlurBackdropView>(Mapper)
{
    public static readonly IPropertyMapper<BlurBackdrop, BlurBackdropHandler> Mapper = new PropertyMapper<BlurBackdrop, BlurBackdropHandler>(ViewMapper)
    {
        [nameof(BlurBackdrop.FadeTop)] = (h, v) => h.PlatformView.FadeTop = v.FadeTop,
        [nameof(BlurBackdrop.Radius)] = (h, v) => h.PlatformView.RadiusDp = (float)v.Radius,
    };

    protected override BlurBackdropView CreatePlatformView() => new(Context);
}

public sealed class BlurBackdropView : AView
{
    internal static readonly HashSet<BlurBackdropView> Live = [];
    private readonly int[] _self = new int[2];
    private readonly int[] _src = new int[2];
    private readonly Android.Graphics.Paint _maskPaint = new() { AntiAlias = false };
    private RenderNode? _node;
    private bool _drawing;
    private bool _reported;
    private Listener? _listener;

    public BlurBackdropView(Android.Content.Context context)
        : base(context)
    {
        _maskPaint.SetXfermode(new PorterDuffXfermode(PorterDuff.Mode.DstIn!));
        SetWillNotDraw(false);
    }

    public bool FadeTop { get; set; }

    public float RadiusDp { get; set; } = 26;

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        Live.Add(this);
        _listener = new Listener(this);
        ViewTreeObserver?.AddOnScrollChangedListener(_listener);
        ViewTreeObserver?.AddOnGlobalLayoutListener(_listener);
    }

    protected override void OnDetachedFromWindow()
    {
        Live.Remove(this);
        if (_listener is not null && ViewTreeObserver is { IsAlive: true } vto)
        {
            vto.RemoveOnScrollChangedListener(_listener);
            vto.RemoveOnGlobalLayoutListener(_listener);
        }

        _listener = null;
        _node?.DiscardDisplayList();
        base.OnDetachedFromWindow();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_drawing || !OperatingSystem.IsAndroidVersionAtLeast(31) || !canvas.IsHardwareAccelerated || Width <= 0 || Height <= 0
            || BlurBackdrop.Source?.Handler?.PlatformView is not AView source || !source.IsAttachedToWindow)
        {
            if (!_reported)
            {
                _reported = true;
                Android.Util.Log.Info("JackiVpn", $"Glass blur skipped: api31={OperatingSystem.IsAndroidVersionAtLeast(31)} hw={canvas.IsHardwareAccelerated} size={Width}x{Height} source={BlurBackdrop.Source?.Handler?.PlatformView?.GetType().Name}");
            }

            return;
        }

        try
        {
            _drawing = true;
            GetLocationInWindow(_self);
            source.GetLocationInWindow(_src);
            _node ??= new RenderNode("jacki-blur");
            _node.SetPosition(0, 0, Width, Height);
            var rc = _node.BeginRecording(Width, Height);
            rc.Translate(_src[0] - _self[0], _src[1] - _self[1]);
            source.Draw(rc);
            _node.EndRecording();
            var r = RadiusDp * Resources!.DisplayMetrics!.Density;
            _node.SetRenderEffect(RenderEffect.CreateBlurEffect(r, r, Shader.TileMode.Clamp!));

            if (FadeTop)
            {
                // Progressive: transparent at the top, fully blurred at the bottom.
                var layer = canvas.SaveLayer(0, 0, Width, Height, null);
                canvas.DrawRenderNode(_node);
                _maskPaint.SetShader(new LinearGradient(0, 0, 0, Height, Android.Graphics.Color.Transparent, Android.Graphics.Color.Black, Shader.TileMode.Clamp!));
                canvas.DrawRect(0, 0, Width, Height, _maskPaint);
                canvas.RestoreToCount(layer);
            }
            else
            {
                canvas.DrawRenderNode(_node);
            }
        }
        catch (Java.Lang.Exception ex)
        {
            // A device-specific rendering problem must never break the app; the tint alone remains.
            if (!_reported)
            {
                _reported = true;
                Android.Util.Log.Warn("JackiVpn", "Glass blur unavailable: " + ex.Message);
            }
        }
        finally
        {
            _drawing = false;
        }
    }

    private sealed class Listener(BlurBackdropView owner) : Java.Lang.Object, Android.Views.ViewTreeObserver.IOnScrollChangedListener, Android.Views.ViewTreeObserver.IOnGlobalLayoutListener
    {
        public void OnScrollChanged() => owner.Invalidate();

        public void OnGlobalLayout() => owner.Invalidate();
    }
}
