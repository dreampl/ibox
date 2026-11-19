using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Island mode and getting out of the way.
/// <list type="bullet">
/// <item>Island mode: a capsule floating a few pixels below the top edge, rounded all round, morphing on the same
/// springs as the classic notch.</item>
/// <item>Only the island takes clicks: at rest the window's region is cut to the island (its shadow and the spare
/// room around it let clicks through to what is below).</item>
/// <item>Hold Ctrl over it: it turns see-through and clicks go to what is under it until Ctrl is let go or the
/// pointer leaves.</item>
/// <item>A faded notch steps aside for a pointer that only passes: it goes almost invisible and lets clicks
/// through; resting on it for half a second brings it back.</item>
/// <item>A maximised window in front: it shrinks to a thin line (a resting pointer opens it); full screen: a thin
/// line that takes no clicks at all.</item>
/// </list>
/// The pointer is only ever watched (10 times a second) while it is over the island in one of those states — never
/// while idle.
/// </summary>
public sealed partial class NotchWindow
{
    private const double IslandTop = 8;
    private const double ThinLineWidth = 110;
    private const double ThinLineHeight = 6;
    private const double SeeThroughOpacity = 0.14;
    private static readonly TimeSpan RestToWake = TimeSpan.FromMilliseconds(500);

    private enum SeeThrough
    {
        None = 0,

        /// <summary>Ctrl held over the island.</summary>
        Peek,

        /// <summary>A faded island stepped aside for a passing pointer.</summary>
        Dodge,
    }

    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _lineWake = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private SeeThrough _seeThrough;
    private NotchPresence _presence;
    private bool _lineOpen;
    private bool _clickThrough;
    private Point _restPoint;
    private DateTime _restSince;
    private bool _hasRegion;

    private bool Island => _settings.Style == NotchStyle.Island;

    /// <summary>Shown as a thin line now (a maximised or full-screen window is in front and it was not woken).</summary>
    private bool InLine => _presence != NotchPresence.Normal && !_lineOpen;

    private void InitIsland()
    {
        _watch.Tick += (_, _) => Watch();
        _lineWake.Tick += (_, _) =>
        {
            _lineWake.Stop();
            if (_pill.IsMouseOver && _presence == NotchPresence.Line && !_lineOpen)
            {
                _lineOpen = true;
                ApplyPresence();
                SetExpanded(true);
            }
        };
        _pill.MouseMove += (_, _) => CheckPeek();
        _pill.MouseEnter += (_, _) =>
        {
            if (_settings.PeekThrough)
            {
                // Ctrl may be pressed while the pointer rests: look at the key while it is over the island only.
                _watch.Start();
            }

            if (_presence == NotchPresence.Line && !_lineOpen)
            {
                _lineWake.Start();
            }
        };
        _pill.MouseLeave += (_, _) =>
        {
            _lineWake.Stop();
            if (_seeThrough == SeeThrough.None)
            {
                _watch.Stop();
            }

            if (_lineOpen && _panel == NotchPanel.None)
            {
                _lineOpen = false;
                ApplyPresence();
            }
        };
        Closed += (_, _) =>
        {
            _watch.Stop();
            _lineWake.Stop();
        };
    }

    /// <summary>Classic or island: the shape, where it sits, the corners.</summary>
    private void ApplyStyle()
    {
        _body.Floating = Island;
        _island.Margin = new Thickness(0, Island ? (InLine ? 2 : IslandTop) : 0, 0, 0);
        ApplySize();
        _body.Refresh();
    }

    /// <summary>
    /// Lower corners: ~14 px small, growing to ~32 px when the island is large. Island mode: a full capsule while
    /// small, corners settling to ~31 px as it opens.
    /// </summary>
    private double CornerRadiusFor(double height) => Island
        ? Math.Min(height / 2, 17 + (Math.Max(0, height - CollapsedHeight) * 0.14))
        : Math.Min(height / 2, Math.Clamp(14 + ((height - CollapsedHeight) * 18 / 120), 14, 32));

    // ------------------------------------------------------------------ maximised / full screen: a thin line

    /// <summary>Driven by <see cref="FullScreenWatcher"/>'s window events, not a timer; each monitor for itself.</summary>
    private void CheckFullScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var front = IsLoaded ? FullScreenWatcher.Front(hwnd) : FrontWindow.Normal;
        var presence = NotchPresenceRules.For(front, _settings.HideInFullScreen, _settings.LineWhenMaximized);
        if (presence == _presence)
        {
            return;
        }

        var wasNormal = _presence == NotchPresence.Normal;
        _presence = presence;
        _lineOpen = false;
        _hiddenForFullScreen = presence == NotchPresence.GhostLine;
        if (_hiddenForFullScreen)
        {
            // Nobody can answer a hidden request: it goes back to the assistant at once instead of waiting out its time unseen.
            DropAllRequests();
        }

        if (wasNormal && presence != NotchPresence.Normal)
        {
            EndSeeThrough();
            SetExpanded(false);
        }

        if (presence == NotchPresence.Normal)
        {
            // Back in front of everything after a full-screen app had the top.
            Topmost = false;
            Topmost = true;
        }

        ApplyPresence();
    }

    private void ApplyPresence()
    {
        var line = InLine;
        var d = Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(line ? 180 : 320);
        _main.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(line ? 0 : 1, d) { BeginTime = line ? TimeSpan.Zero : TimeSpan.FromMilliseconds(120) });
        _island.Margin = new Thickness(0, Island ? (line ? 2 : IslandTop) : 0, 0, 0);
        SetClickThrough(_presence == NotchPresence.GhostLine || _seeThrough != SeeThrough.None);
        if (_presence == NotchPresence.GhostLine)
        {
            FadeTo(0.55, 300);
        }
        else if (_seeThrough == SeeThrough.None)
        {
            FadeTo(_faded ? FadedOpacity : 1, 220);
        }

        Resize();
        SyncMediaClocks();
    }

    // ------------------------------------------------------------------ Ctrl: see and click through

    private void CheckPeek()
    {
        if (_settings.PeekThrough && _seeThrough == SeeThrough.None && !_dragging && CtrlDown())
        {
            BeginSeeThrough(SeeThrough.Peek);
        }
    }

    private void BeginSeeThrough(SeeThrough kind)
    {
        _seeThrough = kind;
        _holdTimer.Stop();
        _fadeTimer.Stop();
        SetExpanded(false);
        ScreenCursor.TryGet(out _restPoint);
        _restSince = DateTime.UtcNow;
        SetClickThrough(true);
        FadeTo(kind == SeeThrough.Peek ? SeeThroughOpacity : SeeThroughOpacity * 0.7, 140);
        _watch.Start();
        SyncMediaClocks();
    }

    private void EndSeeThrough()
    {
        if (_seeThrough == SeeThrough.None)
        {
            return;
        }

        _seeThrough = SeeThrough.None;
        SetClickThrough(_presence == NotchPresence.GhostLine);
        FadeTo(_faded ? FadedOpacity : 1, 260);
        if (!_pill.IsMouseOver)
        {
            _watch.Stop();
        }

        RestartFade();
        SyncMediaClocks();
    }

    /// <summary>While see-through the window gets no pointer messages: the pointer and Ctrl are looked at here.</summary>
    private void Watch()
    {
        if (_seeThrough == SeeThrough.None)
        {
            // Plain hover: only waiting for Ctrl.
            if (!_pill.IsMouseOver)
            {
                _watch.Stop();
                return;
            }

            CheckPeek();
            return;
        }

        if (!ScreenCursor.TryGet(out var p) || !Near(p))
        {
            EndSeeThrough();
            return;
        }

        if (_seeThrough == SeeThrough.Peek)
        {
            if (!CtrlDown())
            {
                EndSeeThrough();
            }

            return;
        }

        // Dodging: a pointer that rests on the island for half a second wants it.
        if ((p - _restPoint).Length > 6 * ScreenScale())
        {
            _restPoint = p;
            _restSince = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - _restSince >= RestToWake && !CtrlDown())
        {
            EndSeeThrough();
            SetFaded(false);
            SetExpanded(true);
        }
    }

    /// <summary>The pointer is over the island's spot on the screen (with a little room around it).</summary>
    private bool Near(Point screen)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var r))
        {
            return false;
        }

        var scale = ScreenScale();
        var origin = _pill.TranslatePoint(default, this);
        const double margin = 12;
        var left = r.Left + ((origin.X - margin) * scale);
        var top = r.Top + ((origin.Y - margin) * scale);
        var right = r.Left + ((origin.X + _pill.ActualWidth + margin) * scale);
        var bottom = r.Top + ((origin.Y + _pill.ActualHeight + margin) * scale);
        return screen.X >= left && screen.X <= right && screen.Y >= Math.Min(top, r.Top) && screen.Y <= bottom;
    }

    private static bool CtrlDown() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    private void SetClickThrough(bool on)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || on == _clickThrough)
        {
            return;
        }

        _clickThrough = on;
        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)(on ? style | WsExTransparent : style & ~WsExTransparent));
    }

    // ------------------------------------------------------------------ the window region

    /// <summary>
    /// At rest, only the island (and the circles beside it) take the pointer: the window region is cut to it, so
    /// its shadow and the transparent room around it let clicks through. While something moves the whole window is
    /// kept (the island stretches and grows into that room); it is cut again when it settles.
    /// </summary>
    private void UpdateHitRegion(bool atRest)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        // The activity circles and the shelf's floating item move around outside the island: no cut while shown.
        var free = !atRest || _body.Cluster?.Visible == true || _overlay.Children.Count > 0 || _panel == NotchPanel.Drop;
        if (free)
        {
            if (_hasRegion)
            {
                _hasRegion = false;
                _ = SetWindowRgn(hwnd, 0, true);
            }

            return;
        }

        var scale = ScreenScale();
        var origin = _pill.TranslatePoint(default, this);
        var w = Math.Max(1, _body.BodyWidth);
        var h = Math.Max(1, _body.BodyHeight);

        // Chips out of the left side.
        var leftReach = 0.0;
        foreach (var s in _body.Satellites)
        {
            if (s.Left)
            {
                leftReach = Math.Max(leftReach, s.Out + (s.Radius * s.Scale) + 3);
            }
        }

        int Px(double v) => (int)Math.Round(v * scale);
        var radius = CornerRadiusFor(h);
        var l = Px(origin.X - leftReach);
        var t = Px(origin.Y);
        var r = Px(origin.X + w) + 1;
        var b = Px(origin.Y + h) + 1;
        nint region;
        if (_body.Floating)
        {
            region = CreateRoundRectRgn(Px(origin.X), t, r, b, Px(radius * 2), Px(radius * 2));
        }
        else
        {
            // Square at the top edge (the top corners are pushed above the window), the ears beside it.
            region = CreateRoundRectRgn(Px(origin.X), t - Px(radius * 2), r, b, Px(radius * 2), Px(radius * 2));
            var ears = CreateRectRgn(Px(origin.X - 9), t, Px(origin.X + w + 9), t + Px(9));
            _ = CombineRgn(region, region, ears, 2 /* RGN_OR */);
            _ = DeleteObject(ears);
        }

        if (leftReach > 0)
        {
            var chips = CreateRectRgn(l, t, Px(origin.X) + 1, Px(origin.Y + Math.Max(h, CollapsedHeight)));
            _ = CombineRgn(region, region, chips, 2);
            _ = DeleteObject(chips);
        }

        // The system owns the region from here on (it frees the old one).
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            _ = DeleteObject(region);
            _hasRegion = false;
            return;
        }

        _hasRegion = true;
    }

    private const long WsExTransparent = 0x00000020;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(nint dest, nint a, nint b, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}
