#pragma warning disable CS0618, CA1422 // android.hardware.Camera: available on every supported Android version, no extra libraries.
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Views;
using Android.Widget;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using Camera = Android.Hardware.Camera;
using Color = Android.Graphics.Color;
using ImageFormat = Android.Graphics.ImageFormat;
using Result = Android.App.Result;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using View = Android.Views.View;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// Full-screen QR scanner: camera preview with a scanning window, torch, and "from gallery". Frames are decoded
/// off the UI thread, one at a time, only inside the window (cheap on battery); the first QR code found is
/// returned and the camera is released at once.
/// </summary>
[Activity(Theme = "@style/Maui.MainTheme.NoActionBar", ScreenOrientation = ScreenOrientation.Portrait, Exported = false, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class QrScanActivity : Activity, TextureView.ISurfaceTextureListener, Camera.IPreviewCallback
{
    private const int PickImage = 7101;
    private static TaskCompletionSource<string?>? _pending;
    private readonly QRCodeReader _reader = new();
    private readonly Dictionary<DecodeHintType, object> _hints = new() { [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }, [DecodeHintType.CHARACTER_SET] = "UTF-8" };
    private TextureView? _texture;
    private ScanOverlay? _overlay;
    private Camera? _camera;
    private Camera.Size? _size;
    private volatile bool _decoding;
    private bool _torch;
    private bool _done;

    /// <summary>Opens the scanner; the result is the QR text, or null when cancelled.</summary>
    public static Task<string?> ScanAsync(Activity host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StartActivity(new Intent(host, typeof(QrScanActivity)));
        return _pending.Task;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.Black);
        Window?.SetNavigationBarColor(Color.Black);
        var root = new FrameLayout(this) { LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent) };
        root.SetBackgroundColor(Color.Black);
        _texture = new TextureView(this) { SurfaceTextureListener = this };
        root.AddView(_texture, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _overlay = new ScanOverlay(this);
        root.AddView(_overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var hint = new TextView(this) { Text = Localization.L.Get("Qr_Hint"), TextSize = 15, Gravity = GravityFlags.Center };
        hint.SetTextColor(Color.White);
        root.AddView(hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top) { TopMargin = Dp(96) });

        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.Center);
        bar.AddView(Pill(Localization.L.Get("Qr_Torch"), () => ToggleTorch()));
        bar.AddView(Pill(Localization.L.Get("Qr_Gallery"), () =>
        {
            var pick = new Intent(Intent.ActionGetContent).SetType("image/*");
            StartActivityForResult(Intent.CreateChooser(pick, Localization.L.Get("Qr_Gallery")), PickImage);
        }));
        bar.AddView(Pill(Localization.L.Get("Cancel"), () => Finish(null)));
        root.AddView(bar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom) { BottomMargin = Dp(48) });
        SetContentView(root);
    }

    private TextView Pill(string text, Action onClick)
    {
        var b = new TextView(this) { Text = text, TextSize = 14, Gravity = GravityFlags.Center };
        b.SetTextColor(Color.White);
        b.SetPadding(Dp(18), Dp(12), Dp(18), Dp(12));
        var bg = new global::Android.Graphics.Drawables.GradientDrawable();
        bg.SetCornerRadius(Dp(24));
        bg.SetColor(Color.Argb(0x66, 0xFF, 0xFF, 0xFF));
        b.Background = bg;
        b.Click += (_, _) => onClick();
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(6), RightMargin = Dp(6) };
        b.LayoutParameters = lp;
        return b;
    }

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 2));

    // ------------------------------------------------------------------ camera

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        try
        {
            var id = Enumerable.Range(0, Camera.NumberOfCameras).FirstOrDefault(i =>
            {
                var info = new Camera.CameraInfo();
                Camera.GetCameraInfo(i, info);
                return info.Facing == CameraFacing.Back;
            });
            _camera = Camera.Open(id);
            var info = new Camera.CameraInfo();
            Camera.GetCameraInfo(id, info);
            _camera!.SetDisplayOrientation(info.Orientation);
            var p = _camera.GetParameters()!;
            _size = p.SupportedPreviewSizes!.OrderBy(s => Math.Abs((s.Width * s.Height) - (1280 * 720))).First();
            p.SetPreviewSize(_size.Width, _size.Height);
            if (p.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousPicture) == true)
            {
                p.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
            }

            _camera.SetParameters(p);
            FitPreview(width, height, info.Orientation % 180 != 0);
            _camera.SetPreviewTexture(surface);
            var bytes = _size.Width * _size.Height * ImageFormat.GetBitsPerPixel(ImageFormatType.Nv21) / 8;
            _camera.AddCallbackBuffer(new byte[bytes]);
            _camera.SetPreviewCallbackWithBuffer(this);
            _camera.StartPreview();
        }
        catch (Exception ex) when (ex is Java.Lang.RuntimeException or InvalidOperationException or ArgumentException)
        {
            Toast.MakeText(this, Localization.L.Get("Qr_NoCamera"), ToastLength.Long)?.Show();
        }
    }

    /// <summary>Centre-crop the preview into the view (no stretching).</summary>
    private void FitPreview(int viewW, int viewH, bool rotated)
    {
        if (_texture is null || _size is null)
        {
            return;
        }

        float previewW = rotated ? _size.Height : _size.Width;
        float previewH = rotated ? _size.Width : _size.Height;
        var scale = Math.Max(viewW / previewW, viewH / previewH);
        var m = new Matrix();
        m.SetScale(previewW * scale / viewW, previewH * scale / viewH, viewW / 2f, viewH / 2f);
        _texture.SetTransform(m);
    }

    public void OnPreviewFrame(byte[]? data, Camera? camera)
    {
        if (data is null || _size is null || _done)
        {
            return;
        }

        if (_decoding)
        {
            camera?.AddCallbackBuffer(data);
            return;
        }

        _decoding = true;
        var w = _size.Width;
        var h = _size.Height;
        _ = Task.Run(() =>
        {
            try
            {
                // Only the centre square (the scanning window) is decoded.
                var side = (int)(Math.Min(w, h) * 0.8);
                var source = new PlanarYUVLuminanceSource(data, w, h, (w - side) / 2, (h - side) / 2, side, side, false);
                var text = Decode(source);
                if (text is not null)
                {
                    RunOnUiThread(() => Finish(text));
                    return;
                }
            }
            catch (Exception ex) when (ex is ReaderException or ArgumentException or IndexOutOfRangeException)
            {
            }
            finally
            {
                _decoding = false;
            }

            try
            {
                camera?.AddCallbackBuffer(data);
            }
            catch (Java.Lang.RuntimeException)
            {
            }
        });
    }

    private string? Decode(LuminanceSource source)
    {
        foreach (var candidate in new[] { source, source.invert() })
        {
            var result = _reader.decode(new BinaryBitmap(new HybridBinarizer(candidate)), _hints);
            _reader.reset();
            if (result?.Text is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private void ToggleTorch()
    {
        if (_camera?.GetParameters() is not { } p || p.SupportedFlashModes?.Contains(Camera.Parameters.FlashModeTorch) != true)
        {
            return;
        }

        _torch = !_torch;
        p.FlashMode = _torch ? Camera.Parameters.FlashModeTorch : Camera.Parameters.FlashModeOff;
        _camera.SetParameters(p);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickImage || resultCode != Result.Ok || data?.Data is not { } uri)
        {
            return;
        }

        try
        {
            using var probe = ContentResolver!.OpenInputStream(uri);
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeStream(probe, null, bounds);
            var sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > 1600)
            {
                sample *= 2;
            }

            using var input = ContentResolver.OpenInputStream(uri);
            using var bmp = BitmapFactory.DecodeStream(input, null, new BitmapFactory.Options { InSampleSize = sample });
            if (bmp is null)
            {
                return;
            }

            var px = new int[bmp.Width * bmp.Height];
            bmp.GetPixels(px, 0, bmp.Width, 0, 0, bmp.Width, bmp.Height);
            var lum = new byte[px.Length];
            for (var i = 0; i < px.Length; i++)
            {
                var c = px[i];
                lum[i] = (byte)((((c >> 16) & 0xFF) * 77 + ((c >> 8) & 0xFF) * 150 + (c & 0xFF) * 29) >> 8);
            }

            var hard = new Dictionary<DecodeHintType, object>(_hints) { [DecodeHintType.TRY_HARDER] = true };
            var result = _reader.decode(new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(lum, bmp.Width, bmp.Height, RGBLuminanceSource.BitmapFormat.Gray8))), hard);
            _reader.reset();
            if (result?.Text is { Length: > 0 } text)
            {
                Finish(text);
            }
            else
            {
                Toast.MakeText(this, Localization.L.Get("Qr_NotFound"), ToastLength.Short)?.Show();
            }
        }
        catch (Exception ex) when (ex is Java.IO.IOException or Java.Lang.SecurityException or ReaderException or ArgumentException)
        {
            Toast.MakeText(this, Localization.L.Get("Qr_NotFound"), ToastLength.Short)?.Show();
        }
    }

    private void Finish(string? text)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        if (text is not null)
        {
            try
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
            }
            catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
            {
            }
        }

        ReleaseCamera();
        _pending?.TrySetResult(text);
        Finish();
    }

    private void ReleaseCamera()
    {
        if (_camera is null)
        {
            return;
        }

        try
        {
            _camera.SetPreviewCallbackWithBuffer(null);
            _camera.StopPreview();
            _camera.Release();
        }
        catch (Java.Lang.RuntimeException)
        {
        }

        _camera = null;
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        ReleaseCamera();
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
    }

    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
    }

    protected override void OnPause()
    {
        base.OnPause();
        if (IsFinishing)
        {
            ReleaseCamera();
        }
    }

    protected override void OnDestroy()
    {
        ReleaseCamera();
        _pending?.TrySetResult(null);
        base.OnDestroy();
    }

    /// <summary>Dimmed surroundings, a rounded window with corner marks and a sweeping scan line.</summary>
    private sealed class ScanOverlay : View
    {
        private readonly Paint _dim = new() { Color = Color.Argb(0x99, 0, 0, 0) };
        private readonly Paint _clear = new(PaintFlags.AntiAlias);
        private readonly Paint _corner = new(PaintFlags.AntiAlias) { Color = Color.White, StrokeWidth = 10, StrokeCap = Paint.Cap.Round! };
        private readonly Paint _line = new(PaintFlags.AntiAlias);
        private readonly long _start = SystemClock.UptimeMillis();

        public ScanOverlay(Context context)
            : base(context)
        {
            _clear.SetXfermode(new PorterDuffXfermode(PorterDuff.Mode.Clear!));
            _corner.SetStyle(Paint.Style.Stroke);
            SetLayerType(LayerType.Hardware, null);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var side = Math.Min(Width, Height) * 0.68f;
            var left = (Width - side) / 2f;
            var top = (Height - side) / 2.3f;
            var r = 36f;
            canvas.DrawRect(0, 0, Width, Height, _dim);
            canvas.DrawRoundRect(new RectF(left, top, left + side, top + side), r, r, _clear);
            var l = side * 0.18f;
            void Corner(float x, float y, float dx, float dy)
            {
                canvas.DrawLine(x, y + (dy * r), x, y + (dy * l), _corner);
                canvas.DrawLine(x + (dx * r), y, x + (dx * l), y, _corner);
                canvas.DrawArc(new RectF(Math.Min(x, x + (dx * 2 * r)), Math.Min(y, y + (dy * 2 * r)), Math.Max(x, x + (dx * 2 * r)), Math.Max(y, y + (dy * 2 * r))),
                    dx > 0 ? (dy > 0 ? 180 : 90) : (dy > 0 ? 270 : 0), 90, false, _corner);
            }

            Corner(left, top, 1, 1);
            Corner(left + side, top, -1, 1);
            Corner(left, top + side, 1, -1);
            Corner(left + side, top + side, -1, -1);

            // Scan line: eases down and up (2.4 s per sweep).
            var t = (SystemClock.UptimeMillis() - _start) % 2400 / 2400f;
            var e = (float)(0.5 - (0.5 * Math.Cos(t * Math.PI * 2)));
            var y0 = top + 24 + ((side - 48) * e);
            _line.SetShader(new LinearGradient(left, y0, left + side, y0, [Color.Transparent, Color.Argb(0xFF, 0x3D, 0x9B, 0xFF), Color.Transparent], null, Shader.TileMode.Clamp!));
            canvas.DrawRect(left + 20, y0 - 1.5f, left + side - 20, y0 + 1.5f, _line);
            PostInvalidateOnAnimation();
        }
    }
}
