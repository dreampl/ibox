using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JackiVpn.Desktop.Views;

/// <summary>Liquid glass: off, drawn, or real (<see cref="GlassPipeline"/>; GDI snapshots where Windows cannot).</summary>
public sealed partial class NotchWindow
{
    private GlassLevel _glass;
    private GlassPipeline? _pipeline;
    private DispatcherTimer? _glassTimer;
    private WriteableBitmap? _glassBitmap;
    private ImageBrush? _glassBrush;
    private GlassFrame? _pendingGlass;
    private bool _glassPosted;
    private int _glassFrames;

    /// <summary>Pictures the real glass delivered (tests and measurements).</summary>
    public int GlassFrames => _glassFrames;

    /// <summary>The real glass runs on the GPU pipeline (false: GDI snapshots, or not running).</summary>
    public bool GlassOnGpu => _pipeline is not null;

    /// <summary>Liquid glass level and strength (0…1). Real glass reads what is behind only while the island is open.</summary>
    public void SetGlass(GlassLevel level, double strength)
    {
        _glass = level;
        _body.Glass = level;
        _body.GlassStrength = strength;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            // Real glass reads the screen behind: the island itself must not be in that picture.
            _ = SetWindowDisplayAffinity(hwnd, level == GlassLevel.Real ? 0x11u /* WDA_EXCLUDEFROMCAPTURE */ : 0u);
        }

        if (level != GlassLevel.Real)
        {
            _body.Backdrop = null;
        }

        SyncGlassCapture();
        _body.Refresh();
    }

    /// <summary>Real glass runs while the island is open or showing something; otherwise nothing is captured at all.</summary>
    private void SyncGlassCapture()
    {
        var run = _glass == GlassLevel.Real && IsVisible && (_expanded || _panel != NotchPanel.None);
        if (run && _pipeline is null && _glassTimer is null)
        {
            StartGlass();
        }
        else if (!run && (_pipeline is not null || _glassTimer is not null))
        {
            StopGlass();
        }
    }

    private void StartGlass()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var r))
        {
            return;
        }

        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info) && Environment.GetEnvironmentVariable("IBAR_GLASS_GDI") != "1")
        {
            _pipeline = GlassPipeline.TryStart(monitor, r.Left - info.Monitor.Left, r.Top - info.Monitor.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        if (_pipeline is not null)
        {
            _glassBitmap = new WriteableBitmap(Math.Max(8, r.Right - r.Left), Math.Max(8, r.Bottom - r.Top), 96, 96, PixelFormats.Bgra32, null);
            _glassBrush = new ImageBrush(_glassBitmap) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            _pipeline.Frame += OnGlassFrame;
            UpdateGlassShape();
            return;
        }

        // Older systems: a GDI snapshot five times a second, only while open.
        _glassTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _glassTimer.Tick += (_, _) => CaptureBackdrop();
        _glassTimer.Start();
        CaptureBackdrop();
    }

    private void StopGlass()
    {
        if (_pipeline is { } p)
        {
            p.Frame -= OnGlassFrame;
            _pipeline = null;
            p.Dispose();
        }

        _glassTimer?.Stop();
        _glassTimer = null;
        _glassBitmap = null;
        _glassBrush = null;
        _pendingGlass = null;
        _body.Backdrop = null;
        _body.Refresh();
    }

    /// <summary>The island's current outline for the lens (called as it changes size).</summary>
    private void UpdateGlassShape()
    {
        if (_pipeline is not { } p || !IsLoaded)
        {
            return;
        }

        var scale = ScreenScale();
        var origin = _pill.TranslatePoint(default, this);
        var h = _body.BodyHeight + Math.Max(0, _body.Membrane.Max) + 2;
        p.SetShape(new GlassShape(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            (int)Math.Round(_body.BodyWidth * scale),
            (int)Math.Round(h * scale),
            (float)(CornerRadiusFor(_body.BodyHeight) * scale),
            (float)Math.Clamp(_body.GlassStrength + 0.3, 0.3, 1),
            (float)scale));
    }

    private void OnGlassFrame(GlassFrame frame)
    {
        // Latest wins: a picture that arrives while the last one waits replaces it.
        var old = System.Threading.Interlocked.Exchange(ref _pendingGlass, frame);
        if (old is not null)
        {
            _pipeline?.Return(old);
        }

        if (Dispatcher.CheckAccess())
        {
            ShowGlass();
        }
        else if (!_glassPosted)
        {
            _glassPosted = true;
            Dispatcher.BeginInvoke(ShowGlass, DispatcherPriority.Render);
        }
    }

    private void ShowGlass()
    {
        _glassPosted = false;
        var frame = System.Threading.Interlocked.Exchange(ref _pendingGlass, null);
        if (frame is null || _glassBitmap is null || _glassBrush is null)
        {
            return;
        }

        try
        {
            if (frame.Black)
            {
                // Protected (DRM) content is captured as black: drawn glass instead, until it is gone.
                _body.Backdrop = null;
            }
            else
            {
                var w = Math.Min(frame.Width, _glassBitmap.PixelWidth);
                var h = Math.Min(frame.Height, _glassBitmap.PixelHeight);
                _glassBitmap.WritePixels(new Int32Rect(0, 0, w, h), frame.Pixels, frame.Stride, 0);
                _glassBrush.Viewbox = new Rect(0, 0, w, h);
                _body.Backdrop = _glassBrush;
                _body.BackdropAverage = Color.FromRgb(frame.Average.R, frame.Average.G, frame.Average.B);
            }

            _glassFrames++;
            _body.Refresh();
            if (_glassFrames % 30 == 1 && Environment.GetEnvironmentVariable("IBAR_GLASS_DUMP") is { Length: > 0 } dump && Environment.GetEnvironmentVariable("IBAR_DATA") is not null)
            {
                // Test copies: the lens output itself (the island is excluded from screenshots by design).
                var shot = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(shot));
                using var file = System.IO.File.Create(dump);
                png.Save(file);
            }
        }
        finally
        {
            _pipeline?.Return(frame);
        }
    }

    private void CaptureBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var r))
        {
            return;
        }

        var scale = ScreenScale();
        var pw = (int)(_pill.ActualWidth * scale);
        var ph = (int)((_pill.ActualHeight + 30) * scale);
        var left = r.Left + (((r.Right - r.Left) - pw) / 2);
        if (GlassCapture.Grab(left, r.Top, pw, ph) is { } picture)
        {
            var brush = new ImageBrush(picture);
            brush.Freeze();
            _body.Backdrop = brush;
        }

        _body.Refresh();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WinRect Monitor;
        public WinRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
