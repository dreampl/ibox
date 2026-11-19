using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.Desktop.Views;

/// <summary>
/// Small tools in the open island, below the details: a focus timer (25 min work, 5 min break; start, pause,
/// resume, stop) and the machine's vitals (CPU, memory, battery). The timer is anchored to its deadline and wakes
/// only when something it shows changes (each second while open, each minute while collapsed); the vitals are read
/// every 3 s while the island is open and never otherwise.
/// </summary>
public sealed partial class NotchWindow
{
    private const double ToolsRowHeight = 44;

    private readonly Grid _tools = new() { Width = ExpandedWidth - 40, Height = ToolsRowHeight - 6, Opacity = 0 };
    private readonly FocusTimer _focus = new();
    private readonly DispatcherTimer _focusClock = new();
    private readonly TextBlock _focusText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
    private readonly Ellipse _focusRing = new() { Width = 16, Height = 16, StrokeThickness = 2, Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)) };
    private readonly Path _focusArc = new() { StrokeThickness = 2, Stroke = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly StackPanel _focusButtons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _vitalsText = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DispatcherTimer _vitalsClock = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Vitals _vitals = new();

    private bool ShowsTools => _settings.FocusTimer || _settings.Vitals;

    private double ToolsHeight => ShowsTools ? ToolsRowHeight : 0;

    /// <summary>The details (or the media card) and the tools row: the shelf and the plugin boards come below.</summary>
    private double ContentArea => DetailsArea + ToolsHeight;

    private void BuildTools()
    {
        _tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tools.ColumnDefinitions.Add(new ColumnDefinition());
        var focus = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dial = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), Children = { _focusRing, _focusArc } };
        focus.Children.Add(dial);
        focus.Children.Add(_focusText);
        focus.Children.Add(_focusButtons);
        _tools.Children.Add(focus);
        Grid.SetColumn(_vitalsText, 1);
        _tools.Children.Add(_vitalsText);
        _tools.RenderTransform = new TranslateTransform();
        _main.Children.Add(_tools);
        Canvas.SetLeft(_tools, 20);
        _focusClock.Tick += (_, _) => FocusTick();
        _vitalsClock.Tick += (_, _) => ReadVitals();
        RenderFocus();
    }

    private Button ToolButton(string text, Action click, string tip)
    {
        var b = PanelButton(text, Color.FromRgb(0x2A, 0x2F, 0x3A), Colors.White, click, tip);
        b.Margin = new Thickness(5, 0, 0, 0);
        return b;
    }

    private void FocusTick()
    {
        var now = DateTimeOffset.UtcNow;
        if (_focus.Advance(now) is { } ended)
        {
            // A phase is over: say so on the island (it comes back from faded for it).
            ShowAlert(new AiAlert("plugin", Brand, ended == FocusPhase.Work ? "Focus done" : "Break over",
                ended == FocusPhase.Work ? $"Take a {(int)_focus.Break.TotalMinutes}-minute break." : "Ready for the next round.", null, null));
            Services.Chime.PlayConnected();
        }

        RenderFocus();
        Render();
    }

    /// <summary>The timer's text, dial and buttons, and when it needs to wake next.</summary>
    private void RenderFocus()
    {
        var now = DateTimeOffset.UtcNow;
        var left = _focus.Remaining(now);
        _focusText.Text = _focus.Phase switch
        {
            FocusPhase.Idle => $"Focus {(int)_focus.Work.TotalMinutes} min",
            FocusPhase.Break => "Break " + FocusTimer.Clock(left),
            _ => "Focus " + FocusTimer.Clock(left),
        };
        _focusText.Opacity = _focus.Paused ? 0.6 : 1;
        _focusArc.Stroke = new SolidColorBrush(_focus.Phase == FocusPhase.Break ? Color.FromRgb(0x4A, 0xDE, 0x80) : Color.FromRgb(0xFB, 0xBF, 0x24));
        _focusArc.Data = Arc(_focus.Progress(now));

        _focusButtons.Children.Clear();
        if (_focus.Phase == FocusPhase.Idle)
        {
            _focusButtons.Children.Add(ToolButton("Start", () => { _focus.Start(DateTimeOffset.UtcNow); FocusChanged(); }, "Start a 25-minute focus"));
        }
        else
        {
            _focusButtons.Children.Add(_focus.Paused
                ? ToolButton("Resume", () => { _focus.Resume(DateTimeOffset.UtcNow); FocusChanged(); }, "Resume")
                : ToolButton("Pause", () => { _focus.Pause(DateTimeOffset.UtcNow); FocusChanged(); }, "Pause"));
            _focusButtons.Children.Add(ToolButton("Stop", () => { _focus.Stop(); FocusChanged(); }, "Stop the timer"));
        }

        ScheduleFocus(now, left);
    }

    private void FocusChanged()
    {
        RenderFocus();
        Render();
    }

    /// <summary>Open: every second. Collapsed: when the minute shown changes, or the phase ends.</summary>
    private void ScheduleFocus(DateTimeOffset now, TimeSpan left)
    {
        _focusClock.Stop();
        if (!_focus.Running)
        {
            return;
        }

        var wait = _expanded ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Max(1, (left.TotalSeconds % 60) + 0.05));
        if (_focus.NextDue is { } due && due - now < wait)
        {
            wait = due - now + TimeSpan.FromMilliseconds(20);
        }

        _focusClock.Interval = wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait;
        _focusClock.Start();
    }

    /// <summary>The collapsed line's part while the timer runs: minutes left ("18m").</summary>
    private string? FocusPart()
    {
        if (!_settings.FocusTimer || _focus.Phase == FocusPhase.Idle)
        {
            return null;
        }

        var minutes = (int)Math.Ceiling(_focus.Remaining(DateTimeOffset.UtcNow).TotalMinutes);
        return (_focus.Phase == FocusPhase.Break ? "☕ " : "◔ ") + minutes.ToString(CultureInfo.InvariantCulture) + "m" + (_focus.Paused ? " ‖" : string.Empty);
    }

    private static Geometry Arc(double fraction)
    {
        var f = Math.Clamp(fraction, 0, 0.9999);
        if (f <= 0)
        {
            return Geometry.Empty;
        }

        const double r = 7, c = 8;
        var angle = f * 2 * Math.PI;
        var end = new Point(c + (r * Math.Sin(angle)), c - (r * Math.Cos(angle)));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry([figure]);
        g.Freeze();
        return g;
    }

    private void ReadVitals()
    {
        var v = _vitals.Read();
        var parts = new System.Collections.Generic.List<string>(3);
        if (v.Cpu is { } cpu)
        {
            parts.Add($"CPU {cpu * 100:0}%");
        }

        parts.Add($"RAM {v.Memory * 100:0}%");
        if (v.Battery is { } b)
        {
            parts.Add((v.Charging ? "⚡" : "🔋") + b.ToString(CultureInfo.InvariantCulture) + "%");
        }

        _vitalsText.Text = string.Join("  ·  ", parts);
    }

    /// <summary>Opened or closed: the vitals are read only while open; the timer changes its pace.</summary>
    private void SyncTools()
    {
        _tools.Visibility = ShowsTools ? Visibility.Visible : Visibility.Collapsed;
        _focusText.Parent.SetValue(VisibilityProperty, _settings.FocusTimer ? Visibility.Visible : Visibility.Collapsed);
        _vitalsText.Visibility = _settings.Vitals ? Visibility.Visible : Visibility.Collapsed;
        var vitals = _settings.Vitals && _expanded && IsVisible;
        if (vitals && !_vitalsClock.IsEnabled)
        {
            ReadVitals(); // the first CPU figure needs a second reading; the clock brings it 3 s later
            _vitalsClock.Start();
        }
        else if (!vitals)
        {
            _vitalsClock.Stop();
        }

        ScheduleFocus(DateTimeOffset.UtcNow, _focus.Remaining(DateTimeOffset.UtcNow));
    }

    private void PlaceTools(double e)
    {
        Canvas.SetTop(_tools, DetailsArea - 6);
        var p = Math.Clamp((e - 0.5) / 0.45, 0, 1);
        var eased = p * p * (3 - (2 * p));
        _tools.Opacity = eased;
        _tools.IsHitTestVisible = eased > 0.5;
        ((TranslateTransform)_tools.RenderTransform).Y = 10 * (1 - eased);
    }

    private void StopTools()
    {
        _focusClock.Stop();
        _vitalsClock.Stop();
        _mediaClock.Stop();
    }
}
