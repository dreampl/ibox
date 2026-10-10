using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Visuals;
using JackiVpn.Mobile.Localization;
using Canvas = Android.Graphics.Canvas;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using PathMeasure = Android.Graphics.PathMeasure;
using Path = Android.Graphics.Path;
using View = Android.Views.View;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// The dynamic island: a small black floating island over other apps ("Display over other apps") with the IBOX
/// eye, the connection state and the live ping. Tap to expand (details), double-tap to open IBOX, drag anywhere —
/// the place is remembered. It fades after a few seconds; holding it for 1.5 s brings it back. The window is only as
/// big as the island, so touches around it reach the apps below.
/// </summary>
public sealed class IslandOverlay
{
    private readonly Context _context;
    private readonly IWindowManager _windows;
    private IslandView? _view;
    private WindowManagerLayoutParams? _layout;

    public IslandOverlay(Context context)
    {
        _context = context;
        _windows = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
    }

    /// <summary>The user may draw over other apps (granted in system settings).</summary>
    public static bool CanShow(Context context) => !OperatingSystem.IsAndroidVersionAtLeast(23) || global::Android.Provider.Settings.CanDrawOverlays(context);

    /// <summary>Opens the system screen where "Display over other apps" is granted.</summary>
    public static void RequestPermission(Context context)
    {
        var intent = new Intent(global::Android.Provider.Settings.ActionManageOverlayPermission, global::Android.Net.Uri.Parse("package:" + context.PackageName))
            .AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    public event EventHandler? OpenRequested;

    /// <summary>The user dragged it: new centre X and top Y (fractions of the screen).</summary>
    public event EventHandler<(double X, double Y)>? Moved;

    public bool IsShowing => _view is not null;

    public void Show(IslandSettings settings)
    {
        if (_view is not null || !CanShow(_context))
        {
            return;
        }

        _view = new IslandView(_context, this);
        _layout = new WindowManagerLayoutParams(
            1,
            1,
            OperatingSystem.IsAndroidVersionAtLeast(26) ? WindowManagerTypes.ApplicationOverlay : WindowManagerTypes.Phone,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.HardwareAccelerated,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
        };
        _view.Apply(settings);
        Resize(_view.WindowWidth, _view.WindowHeight);
        try
        {
            _windows.AddView(_view, _layout);
        }
        catch (Exception ex) when (ex is WindowManagerBadTokenException or Java.Lang.SecurityException or Java.Lang.IllegalStateException)
        {
            _view = null;
        }
    }

    public void Hide()
    {
        if (_view is null)
        {
            return;
        }

        try
        {
            _windows.RemoveView(_view);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
        }

        _view.Stop();
        _view = null;
    }

    public void Apply(IslandSettings settings)
    {
        _view?.Apply(settings);
        if (_view is not null)
        {
            Resize(_view.WindowWidth, _view.WindowHeight);
        }
    }

    public void Update(ConnectionStatus status, VisualMode mode) => _view?.Update(status, mode);

    internal void RaiseOpen() => OpenRequested?.Invoke(this, EventArgs.Empty);

    internal void RaiseMoved(double x, double y) => Moved?.Invoke(this, (x, y));

    /// <summary>Window = the island plus room for its shadow, centred on the island's centre X.</summary>
    internal void Resize(int width, int height)
    {
        if (_layout is null || _view is null)
        {
            return;
        }

        var screen = _context.Resources!.DisplayMetrics!;
        _layout.Width = width;
        _layout.Height = height;
        _layout.X = (int)Math.Clamp((_view.CenterX * screen.WidthPixels) - (width / 2.0), -width / 3.0, screen.WidthPixels - (width * 2 / 3.0));
        _layout.Y = (int)Math.Clamp(_view.TopY * screen.HeightPixels, 0, screen.HeightPixels - height);
        if (_view.IsAttachedToWindow)
        {
            _windows.UpdateViewLayout(_view, _layout);
        }
    }

    internal int ScreenWidth => _context.Resources!.DisplayMetrics!.WidthPixels;

    internal int ScreenHeight => _context.Resources!.DisplayMetrics!.HeightPixels;
}

/// <summary>The island itself: drawn by hand on a canvas, animated on springs with the Choreographer.</summary>
internal sealed class IslandView : View, Choreographer.IFrameCallback
{
    private const float Pad = 14; // dp around the island for the shadow
    private const float CollapsedH = 34;
    private const float ExpandedW = 316;
    private const float ExpandedH = 100;
    private const float EyeSmall = 20;
    private const float EyeBig = 50;
    private const float EyeGap = 12;
    private const float StatusIcon = 17;
    private const float Side = 22;

    private static readonly Color Blue = Color.Rgb(0x5A, 0xAC, 0xFF);
    private static readonly Color Grey = Color.Rgb(0x9C, 0xA3, 0xAF);
    private static readonly Dictionary<(MorphIcon, int), List<(double, double)>> Outlines = [];

    private readonly IslandOverlay _host;
    private readonly float _d;
    private readonly Paint _pill = new(PaintFlags.AntiAlias) { Color = Color.Black };
    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _text = new(PaintFlags.AntiAlias) { Color = Color.White };
    private readonly Path _frame = new();
    private readonly EyeMotion _eye = new();
    private readonly OrbFrame _orb = new();
    private readonly ParticleMorph _statusDots = new(34);
    private readonly ParticleMorph _badgeDots = new(72, 11);
    private readonly Spring _w;
    private readonly Spring _h;
    private readonly Spring _expand;
    private readonly Spring _statusW;
    private readonly Spring _alpha = new(1, 90, 1.0);
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly long _epoch = SystemClock.UptimeMillis();
    private IslandSettings _settings = new();
    private ConnectionStatus _status = new();
    private VisualMode _mode;
    private VisualMode _lastMode;
    private bool _expanded;
    private bool _faded;
    private bool _statusAsIcon;
    private MorphIcon _statusIcon = MorphIcon.Link;
    private double _statusProgress;
    private MorphIcon? _badge;
    private Color _badgeColor = Color.White;
    private double _badgeProgress;
    private double _badgeTarget;
    private bool _running;
    private long _lastFrame;
    private float _downX;
    private float _downY;
    private int _downLeft;
    private int _downTop;
    private bool _dragging;
    private long _lastTap;
    private Java.Lang.IRunnable? _fadeRun;
    private Java.Lang.IRunnable? _holdRun;
    private Java.Lang.IRunnable? _statusRun;
    private Java.Lang.IRunnable? _badgeRun;
    private Java.Lang.IRunnable? _collapseRun;

    public IslandView(Context context, IslandOverlay host)
        : base(context)
    {
        _host = host;
        _d = context.Resources!.DisplayMetrics!.Density;
        // Drawn on a canvas scaled to dp, so the shadow is in dp too.
        _pill.SetShadowLayer(12, 0, 3, Color.Argb(170, 0, 0, 0));
        _text.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Bold));
        _w = new Spring(160, 230, 0.8);
        _h = new Spring(CollapsedH, 225, 0.78);
        _expand = new Spring(0, 165, 0.9);
        _statusW = new Spring(80, 230, 0.8);
        _frame.MoveTo(30, 0);
        _frame.LineTo(126, 0);
        _frame.LineTo(126, 127);
        _frame.LineTo(8, 127);
        _frame.QuadTo(0, 127, 0, 119);
        _frame.LineTo(0, 30);
        _frame.LineTo(27, 30);
        _frame.LineTo(27, 100);
        _frame.LineTo(99, 100);
        _frame.LineTo(99, 27);
        _frame.LineTo(30, 27);
        _frame.Close();
        _statusDots.SetOutline(Sample(MorphIcon.Link, 34));
    }

    public double CenterX => _settings.X;

    public double TopY => _settings.Y;

    /// <summary>Window size for the island's largest current size (plus shadow room), in pixels.</summary>
    public int WindowWidth => (int)((Math.Max(_w.Value, _w.Target) + (2 * Pad)) * _d);

    public int WindowHeight => (int)((Math.Max(_h.Value, _h.Target) + (2 * Pad)) * _d);

    public void Apply(IslandSettings settings)
    {
        _settings = settings;
        Relayout(snap: !IsAttachedToWindow);
        RestartFade();
    }

    private bool _first = true;

    public void Update(ConnectionStatus status, VisualMode mode)
    {
        // The very first state counts as a change too (it is said, then becomes its icon).
        var changed = status.State != _status.State || _first;
        _first = false;
        _status = status;
        _mode = mode;
        _eye.State = status.State == ConnectionState.Connected ? EyeState.Awake : ConnectionStateRules.IsIdle(status.State) ? EyeState.Asleep : EyeState.Seeking;
        if (changed)
        {
            // The new state in words; two seconds later it becomes an icon of orb dots (link / broken link).
            Cancel(ref _statusRun);
            _statusAsIcon = false;
            _statusProgress = 0;
            if (status.State is ConnectionState.Connected or ConnectionState.Disconnected)
            {
                _statusRun = Later(2000, () =>
                {
                    _statusIcon = _status.State == ConnectionState.Connected ? MorphIcon.Link : MorphIcon.Unlink;
                    _statusDots.SetOutline(Sample(_statusIcon, 34));
                    _statusAsIcon = true;
                    Relayout(snap: false);
                });
            }

            SetFaded(false);
            RestartFade();
        }

        var modeChanged = mode != _lastMode;
        _lastMode = mode;
        if (status.State == ConnectionState.Connected && (modeChanged || changed) && MorphIcons.ForMode(mode) is { } icon)
        {
            // The mode shows itself for ten seconds: the eye's dots become its icon, then the eye again.
            _badge = icon;
            var c = MorphIcons.ColorOf(mode);
            _badgeColor = Color.Argb((int)(c >> 24), (int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));
            _badgeDots.SetOutline(Sample(icon, 72));
            _badgeProgress = 0;
            _badgeTarget = 1;
            Cancel(ref _badgeRun);
            _badgeRun = Later(10_000, () => _badgeTarget = 0);
            SetFaded(false);
            RestartFade();
        }
        else if (_badge is not null && (mode == VisualMode.Normal || status.State != ConnectionState.Connected))
        {
            _badgeTarget = 0;
        }

        Relayout(snap: false);
    }

    public void Stop()
    {
        _running = false;
        Cancel(ref _fadeRun);
        Cancel(ref _holdRun);
        Cancel(ref _statusRun);
        Cancel(ref _badgeRun);
        Cancel(ref _collapseRun);
    }

    // ------------------------------------------------------------------ content and layout

    private string StateText => _status.State switch
    {
        ConnectionState.Connected => L.Get("Island_Connected"),
        ConnectionState.Disconnected => L.Get("Island_NotConnected"),
        ConnectionState.Faulted => L.Get("Island_Failed"),
        ConnectionState.Reconnecting => L.Get("Island_Reconnecting"),
        _ => L.Get("Island_Connecting"),
    };

    private Color StateColor => _status.State switch
    {
        ConnectionState.Connected => Blue,
        ConnectionState.Faulted => Color.Rgb(0xF8, 0x71, 0x71),
        ConnectionState.Disconnected => Grey,
        _ => Color.Rgb(0x7D, 0xB8, 0xFF),
    };

    private bool ShowStatus => _settings.ShowStatus || _status.State != ConnectionState.Connected || Rest().Count == 0;

    /// <summary>Ping, server, country, time — as chosen; each with its colour.</summary>
    private List<(string Text, Color Color)> Rest()
    {
        var list = new List<(string, Color)>();
        var st = _status;
        if (st.State != ConnectionState.Connected)
        {
            return list;
        }

        if (_settings.ShowPing && st.LatencyMs is { } ms)
        {
            list.Add(($"{ms:0} ms", ms < 150 ? Color.Rgb(0x4A, 0xDE, 0x80) : ms < 300 ? Color.Rgb(0xFB, 0xBF, 0x24) : Color.Rgb(0xF8, 0x71, 0x71)));
        }

        if (_settings.ShowServer && st.ProfileName is { } name)
        {
            list.Add((name.Length > 22 ? name[..21] + "…" : name, Color.White));
        }

        if (_settings.ShowCountry && (st.ExitCountryName ?? st.ExitCountryCode) is { } country)
        {
            list.Add((country, Color.White));
        }

        if (_settings.ShowDuration && st.ConnectedSinceUtc is { } since)
        {
            var d = DateTimeOffset.UtcNow - since;
            list.Add((d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m" : $"{(int)d.TotalMinutes} min", Color.White));
        }

        return list;
    }

    private float TextWidth(string s, float sp)
    {
        _text.TextSize = sp * _d;
        return _text.MeasureText(s) / _d;
    }

    private float RestWidth()
    {
        var rest = Rest();
        var w = 0f;
        for (var i = 0; i < rest.Count; i++)
        {
            w += ((i > 0 || ShowStatus) ? TextWidth("  ·  ", 13) : 0) + TextWidth(rest[i].Text, 13);
        }

        return w;
    }

    private float StatusWidth() => !ShowStatus ? 0 : _statusAsIcon ? StatusIcon : TextWidth(StateText, 13);

    private float GroupWidth() => EyeSmall + EyeGap + StatusWidth() + RestWidth();

    private void Relayout(bool snap)
    {
        _statusW.To(StatusWidth());
        _w.To(_expanded ? ExpandedW : Math.Clamp(GroupWidth() + (2 * Side), 120, 330));
        _h.To(_expanded ? ExpandedH : CollapsedH);
        _expand.To(_expanded ? 1 : 0);
        if (snap)
        {
            _w.Snap();
            _h.Snap();
            _expand.Snap();
            _statusW.Snap();
        }

        _host.Resize(WindowWidth, WindowHeight);
        Run();
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var d = _d;
        var w = (float)_w.Value;
        var h = (float)_h.Value;
        var e = (float)Math.Clamp(_expand.Value, 0, 1.15);
        var k = Math.Clamp(e, 0, 1);
        var alpha = (int)(255 * Math.Clamp(_alpha.Value, 0, 1));
        var left = ((Width / d) - w) / 2;
        var top = Pad;
        canvas.Save();
        canvas.Scale(d, d);
        _pill.Alpha = alpha;
        var r = Math.Min(h / 2, 22);
        canvas.DrawRoundRect(left, top, left + w, top + h, r, r, _pill);
        canvas.ClipRect(left, top, left + w, top + h);

        // Collapsed: eye + state + rest as one centred group; expanded: big eye on the left, centred in height.
        var groupLeft = left + Math.Max(Side * 0.6f, (w - (EyeSmall + EyeGap + (float)_statusW.Value + RestWidth())) / 2);
        var eyeSize = EyeSmall + ((EyeBig - EyeSmall) * e);
        var eyeLeft = groupLeft + ((left + 22 - groupLeft) * k);
        var eyeTop = top + ((h - eyeSize) / 2);
        var badgeVisible = _badge is not null && _badgeProgress > 0.001;
        DrawEye(canvas, eyeLeft, eyeTop, eyeSize, alpha, badgeVisible ? (float)Math.Clamp(1 - (_badgeProgress * 1.6), 0, 1) : 1);
        if (badgeVisible)
        {
            DrawDots(canvas, _badgeDots, eyeLeft, eyeTop, eyeSize, _badgeColor, _badgeProgress, alpha);
        }

        var lineAlpha = (int)(alpha * Math.Clamp(1 - (e * 2.2), 0, 1));
        if (lineAlpha > 2)
        {
            var x = groupLeft + EyeSmall + EyeGap;
            var baseline = top + (h / 2) + (4.6f) - (4 * k);
            if (ShowStatus)
            {
                if (_statusAsIcon)
                {
                    DrawDots(canvas, _statusDots, x, top + ((h - StatusIcon) / 2), StatusIcon, _status.State == ConnectionState.Connected ? Blue : Grey, _statusProgress, lineAlpha);
                    var textFade = (int)(lineAlpha * Math.Clamp(1 - (_statusProgress * 3), 0, 1));
                    if (textFade > 2)
                    {
                        Text(canvas, StateText, x, baseline, 13, StateColor, textFade);
                    }
                }
                else
                {
                    Text(canvas, StateText, x, baseline, 13, StateColor, lineAlpha);
                }

                x += (float)_statusW.Value;
            }

            var rest = Rest();
            for (var i = 0; i < rest.Count; i++)
            {
                if (i > 0 || ShowStatus)
                {
                    x += Text(canvas, "  ·  ", x, baseline, 13, Color.Rgb(0x6B, 0x72, 0x80), lineAlpha);
                }

                x += Text(canvas, rest[i].Text, x, baseline, 13, rest[i].Color, lineAlpha);
            }
        }

        var detailAlpha = (int)(alpha * Math.Clamp((e - 0.35) / 0.65, 0, 1));
        if (detailAlpha > 2)
        {
            var dx = left + 22 + EyeBig + 20;
            var rowY = top + 30 + (6 * (1 - k));
            Text(canvas, "IBOX  ", dx, rowY, 11, Grey, detailAlpha);
            Text(canvas, StateText, dx + TextWidth("IBOX  ", 11), rowY, 13, StateColor, detailAlpha);
            if (_status.State == ConnectionState.Connected && _status.LatencyMs is { } ms)
            {
                var ping = $"{ms:0} ms";
                Text(canvas, ping, left + w - 18 - TextWidth(ping, 19), rowY + 2, 19, Color.Rgb(0x4A, 0xDE, 0x80), detailAlpha);
            }

            var server = _status.State == ConnectionState.Connected ? _status.ProfileName ?? "—" : L.Get("Island_TapToOpen");
            Text(canvas, Fit(server, 13, w - (dx - left) - 18), dx, rowY + 21, 13, Color.Rgb(0xE5, 0xE7, 0xEB), detailAlpha);
            if (_status.State == ConnectionState.Connected)
            {
                var since = _status.ConnectedSinceUtc is { } t ? DateTimeOffset.UtcNow - t : TimeSpan.Zero;
                var where = string.Join("  ·  ", new[]
                {
                    _status.ExitCountryName ?? _status.ExitCountryCode,
                    since > TimeSpan.Zero ? (since.TotalHours >= 1 ? $"{(int)since.TotalHours}h {since.Minutes:00}m" : $"{(int)since.TotalMinutes} min") : null,
                    _mode is VisualMode.Normal ? null : ModePalette.Label(_mode).ToLowerInvariant(),
                }.Where(x => !string.IsNullOrEmpty(x)));
                Text(canvas, Fit(where, 11.5f, w - (dx - left) - 18), dx, rowY + 39, 11.5f, Grey, detailAlpha);
            }
        }

        canvas.Restore();
    }

    private string Fit(string s, float sp, float max)
    {
        if (TextWidth(s, sp) <= max)
        {
            return s;
        }

        while (s.Length > 1 && TextWidth(s + "…", sp) > max)
        {
            s = s[..^1];
        }

        return s + "…";
    }

    private float Text(Canvas canvas, string s, float x, float baseline, float sp, Color color, int alpha)
    {
        _text.TextSize = sp;
        _text.Color = color;
        _text.Alpha = alpha;
        canvas.DrawText(s, x, baseline, _text);
        return _text.MeasureText(s);
    }

    private void DrawEye(Canvas canvas, float x, float y, float size, int alpha, float visibility)
    {
        if (visibility <= 0.01f)
        {
            return;
        }

        var a = (int)(alpha * visibility);
        var scale = size / 127f;
        canvas.Save();
        canvas.Translate(x + (float)(_eye.DriftX / _d), y + (float)(_eye.DriftY / _d));
        canvas.Scale(scale, scale);
        var state = _eye.State;
        _fill.Color = state switch { EyeState.Awake => Color.White, EyeState.Seeking => Color.Rgb(0xC9, 0xCE, 0xD8), _ => Color.Rgb(0x5E, 0x64, 0x72) };
        _fill.Alpha = a;
        canvas.DrawPath(_frame, _fill);
        if (state == EyeState.Seeking)
        {
            DrawOrb(canvas, a);
        }
        else
        {
            var (px, py) = EyeMark.PupilAt(_eye.LookX, _eye.LookY);
            var ph = (float)(EyeMark.PupilSize * _eye.Openness);
            var top = (float)(py + ((EyeMark.PupilSize - ph) / 2));
            _fill.Color = state == EyeState.Awake ? Color.Rgb(0x3D, 0x9B, 0xFF) : Color.Rgb(0x4A, 0x55, 0x68);
            _fill.Alpha = a;
            canvas.DrawRoundRect((float)px, top, (float)(px + EyeMark.PupilSize), top + ph, 1.5f, 1.5f, _fill);
        }

        canvas.Restore();
    }

    private void DrawOrb(Canvas canvas, int alpha)
    {
        var size = (float)EyeMark.WindowWidth + 8;
        var preset = ThinkingOrbs.Resolve(OrbState.Connecting, 64);
        ThinkingOrbs.Compute(preset, 64, (SystemClock.UptimeMillis() - _epoch) / 1000.0 * preset.Speed, _orb);
        var k = size / 64f;
        var ox = (float)EyeMark.WindowLeft - 4;
        var oy = (float)EyeMark.WindowTop - 4;
        foreach (var dot in _orb.Dots)
        {
            var g = (0.35 + (0.65 * (ThinkingOrbs.Gray(dot.White, dark: true) / 255.0))) * dot.A;
            _fill.Color = Color.Rgb(0x9C, 0xCF, 0xFF);
            _fill.Alpha = (int)(alpha * Math.Clamp(g, 0, 1));
            canvas.DrawCircle(ox + ((float)dot.X * k), oy + ((float)dot.Y * k), (float)dot.R * k, _fill);
        }
    }

    private void DrawDots(Canvas canvas, ParticleMorph dots, float x, float y, float size, Color color, double progress, int alpha)
    {
        dots.Compute((SystemClock.UptimeMillis() - _epoch) / 1000.0, progress, size);
        for (var i = 0; i < dots.Count; i++)
        {
            _fill.Color = color;
            _fill.Alpha = (int)(alpha * Math.Clamp(dots.Alpha[i], 0, 1));
            canvas.DrawCircle(x + (float)dots.X[i], y + (float)dots.Y[i], (float)dots.Radius[i], _fill);
        }
    }

    /// <summary>Points spaced evenly along an icon's outline (AndroidX path parser + PathMeasure), 0…1 of its 24 px box.</summary>
    private static List<(double, double)> Sample(MorphIcon icon, int count)
    {
        if (Outlines.TryGetValue((icon, count), out var cached))
        {
            return cached;
        }

        var path = AndroidX.Core.Graphics.PathParser.CreatePathFromPathData(MorphIcons.Path(icon))!;
        var lengths = new List<float>();
        using (var measure = new PathMeasure(path, false))
        {
            do
            {
                lengths.Add(measure.Length);
            }
            while (measure.NextContour());
        }

        var total = lengths.Sum();
        var result = new List<(double, double)>(count);
        if (total > 0)
        {
            var step = total / count;
            var next = step / 2;
            var walked = 0f;
            var pos = new float[2];
            using var measure = new PathMeasure(path, false);
            var contour = 0;
            do
            {
                var length = lengths[contour++];
                while (next <= walked + length && result.Count < count)
                {
                    measure.GetPosTan(next - walked, pos, null);
                    result.Add((pos[0] / 24.0, pos[1] / 24.0));
                    next += step;
                }

                walked += length;
            }
            while (measure.NextContour() && contour < lengths.Count);
        }

        Outlines[(icon, count)] = result;
        return result;
    }

    // ------------------------------------------------------------------ frames

    private void Run()
    {
        if (!_running && IsAttachedToWindow)
        {
            _running = true;
            _lastFrame = 0;
            Choreographer.Instance!.PostFrameCallback(this);
        }
    }

    public void DoFrame(long frameTimeNanos)
    {
        if (!_running || !IsAttachedToWindow)
        {
            _running = false;
            return;
        }

        var dt = _lastFrame == 0 ? 1 / 60.0 : Math.Clamp((frameTimeNanos - _lastFrame) / 1e9, 0, 0.05);
        _lastFrame = frameTimeNanos;
        _eye.Step(dt);
        var moving = _w.Step(dt) | _h.Step(dt) | _expand.Step(dt) | _statusW.Step(dt) | _alpha.Step(dt);
        if (_statusAsIcon && _statusProgress < 1)
        {
            _statusProgress = Math.Min(1, _statusProgress + (dt / 0.9));
            moving = true;
        }

        if (_badge is not null && _badgeProgress != _badgeTarget)
        {
            _badgeProgress = _badgeTarget > _badgeProgress ? Math.Min(1, _badgeProgress + (dt / 1.15)) : Math.Max(0, _badgeProgress - (dt / 1.0));
            if (_badgeProgress <= 0 && _badgeTarget <= 0)
            {
                _badge = null;
            }

            moving = true;
        }

        if (!moving && Math.Abs(_w.Value - _w.Target) < 0.5 && Math.Abs(_h.Value - _h.Target) < 0.5)
        {
            // The window may shrink back now that the island settled.
            _host.Resize(WindowWidth, WindowHeight);
        }

        Invalidate();
        // Full rate while anything moves (springs, orb, badge, a blink or glance); at rest a slow 4 fps for the eye's
        // next glance and the sleeping breath — and nothing at all while the screen is off (no vsync).
        var busy = moving || _eye.IsMoving || _eye.State == EyeState.Seeking || _badge is not null;
        if (busy)
        {
            Choreographer.Instance!.PostFrameCallback(this);
        }
        else
        {
            Choreographer.Instance!.PostFrameCallbackDelayed(this, 250);
        }
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        Run();
    }

    protected override void OnDetachedFromWindow()
    {
        base.OnDetachedFromWindow();
        _running = false;
    }

    // ------------------------------------------------------------------ touch: tap, double tap, drag, hold

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = e.RawX;
                _downY = e.RawY;
                var screenW = _host.ScreenWidth;
                var screenH = _host.ScreenHeight;
                _downLeft = (int)((_settings.X * screenW) - (Width / 2.0));
                _downTop = (int)(_settings.Y * screenH);
                _dragging = false;
                if (_faded)
                {
                    // Faded: hold for 1.5 s to bring it back.
                    _alpha.To(0.6);
                    Run();
                    Cancel(ref _holdRun);
                    _holdRun = Later(1500, () =>
                    {
                        SetFaded(false);
                        SetExpanded(true);
                    });
                }

                return true;
            case MotionEventActions.Move:
                var dx = e.RawX - _downX;
                var dy = e.RawY - _downY;
                if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 10 * _d)
                {
                    _dragging = true;
                    Cancel(ref _holdRun);
                    SetExpanded(false);
                }

                if (_dragging)
                {
                    var w = _host.ScreenWidth;
                    var h = _host.ScreenHeight;
                    _settings = _settings with
                    {
                        X = Math.Clamp((_downLeft + dx + (Width / 2.0)) / w, 0.05, 0.95),
                        Y = Math.Clamp((_downTop + dy) / h, 0, 0.95),
                    };
                    _host.Resize(WindowWidth, WindowHeight);
                }

                return true;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                Cancel(ref _holdRun);
                if (_dragging)
                {
                    _host.RaiseMoved(_settings.X, _settings.Y);
                }
                else if (e.ActionMasked == MotionEventActions.Up)
                {
                    var now = SystemClock.UptimeMillis();
                    if (now - _lastTap < 320)
                    {
                        _host.RaiseOpen();
                    }
                    else if (!_faded)
                    {
                        _eye.Poke();
                        SetExpanded(!_expanded);
                    }
                    else
                    {
                        _alpha.To(0.32);
                    }

                    _lastTap = now;
                }

                _dragging = false;
                RestartFade();
                Run();
                return true;
        }

        return base.OnTouchEvent(e);
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Cancel(ref _collapseRun);
        if (expanded)
        {
            // Opened by a tap: closes by itself after a few seconds.
            _collapseRun = Later(5000, () => SetExpanded(false));
        }

        Relayout(snap: false);
    }

    private void RestartFade()
    {
        Cancel(ref _fadeRun);
        if (_settings.FadeAfterSeconds > 0)
        {
            _fadeRun = Later(_settings.FadeAfterSeconds * 1000 + (_expanded ? 5000 : 0), () => SetFaded(true));
        }
    }

    private void SetFaded(bool faded)
    {
        _faded = faded;
        _alpha.To(faded ? 0.32 : 1);
        Run();
    }

    private Java.Lang.IRunnable Later(long ms, Action action)
    {
        var run = new Java.Lang.Runnable(action);
        _handler.PostDelayed(run, ms);
        return run;
    }

    private void Cancel(ref Java.Lang.IRunnable? run)
    {
        if (run is not null)
        {
            _handler.RemoveCallbacks(run);
            run = null;
        }
    }

    /// <summary>A damped spring (semi-implicit Euler).</summary>
    private sealed class Spring(double value, double stiffness, double ratio)
    {
        private readonly double _damping = 2 * Math.Sqrt(stiffness) * ratio;
        private double _v;

        public double Value { get; private set; } = value;

        public double Target { get; private set; } = value;

        public void To(double target) => Target = target;

        public void Snap()
        {
            Value = Target;
            _v = 0;
        }

        public bool Step(double dt)
        {
            var a = (stiffness * (Target - Value)) - (_damping * _v);
            _v += a * dt;
            Value += _v * dt;
            if (Math.Abs(_v) < 0.02 && Math.Abs(Target - Value) < 0.02)
            {
                Snap();
                return false;
            }

            return true;
        }
    }
}
