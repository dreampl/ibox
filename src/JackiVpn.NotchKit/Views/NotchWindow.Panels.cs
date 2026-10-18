using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Desktop.Views;

/// <summary>What the island shows instead of the connection.</summary>
internal enum NotchPanel
{
    None = 0,

    /// <summary>Something is being dragged over it.</summary>
    Drop,

    /// <summary>A notification from Claude or ChatGPT.</summary>
    Alert,

    /// <summary>Claude Code or Codex asks for permission.</summary>
    Ask,
}

/// <summary>
/// Notifications and requests from the AI assistants. A notification drops in from the top edge (the island takes it
/// with a small wobble), shows the app's icon, title and text for a few seconds — longer while the pointer rests on
/// it, click it to open the app. A permission request (Claude Code, Codex) drops in the same way and waits: what will
/// run, in which project, and Allow / Deny / "Answer there" (the assistant then asks in its own window). The buttons
/// arm after a moment so a click meant for something else never answers; requests queue one after another.
/// </summary>
public sealed partial class NotchWindow
{
    private const double AlertHeight = 88;
    private const double AskWidth = 440;
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(650);

    private NotchPanel _panel;

    // Notification panel.
    private readonly Grid _alertPanel = new() { Width = ExpandedWidth, Height = AlertHeight, Opacity = 0, IsHitTestVisible = false, RenderTransform = new TranslateTransform(), Cursor = Cursors.Hand };
    private readonly Border _alertIcon = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _alertIconPop = new(1, 1);
    private readonly TextBlock _alertApp = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _alertTitle = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _alertBody = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32 };
    private readonly Queue<AiAlert> _alerts = new();
    private readonly DispatcherTimer _alertTimer = new();
    private AiAlert? _alertShown;

    // Request panel.
    private readonly Grid _askPanel = new() { Width = AskWidth, Opacity = 0, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
    private readonly StackPanel _askContent = new() { Margin = new Thickness(22, 14, 22, 16) };
    private readonly Border _askIcon = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(7) };
    private readonly TextBlock _askApp = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _askMore = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xAC, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _askTitle = new() { FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 9, 0, 7), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _askDetail = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 47 };
    private readonly Border _askDetailBox = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 7, 10, 7), Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26)) };
    private readonly TextBlock _askNote = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _askButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ScaleTransform _askClock = new(1, 1);
    private readonly List<PendingAsk> _asks = [];
    private readonly DispatcherTimer _armTimer = new() { Interval = ArmDelay };
    private double _askHeight = 170;

    private sealed class PendingAsk(NotchMessage message, ImageSource? icon)
    {
        public NotchMessage Message { get; } = message;

        public ImageSource? Icon { get; } = icon;

        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }

    private void BuildPanels()
    {
        // Notification: icon, app line, title, two lines of text.
        _alertIcon.RenderTransform = _alertIconPop;
        _alertPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _alertPanel.ColumnDefinitions.Add(new ColumnDefinition());
        _alertIcon.Margin = new Thickness(22, 0, 14, 0);
        _alertIcon.VerticalAlignment = VerticalAlignment.Center;
        _alertPanel.Children.Add(_alertIcon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 22, 0) };
        words.Children.Add(_alertApp);
        words.Children.Add(_alertTitle);
        words.Children.Add(_alertBody);
        Grid.SetColumn(words, 1);
        _alertPanel.Children.Add(words);
        _alertPanel.Background = Brushes.Transparent;
        _alertPanel.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_alertShown?.Open is { } open)
            {
                open();
            }
            else if (_alertShown?.AppId is { } appId)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + appId) { UseShellExecute = false });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // nothing to open
                }
            }

            _alertTimer.Stop();
            NextAlert();
        };
        _alertTimer.Tick += (_, _) =>
        {
            _alertTimer.Stop();
            if (_pill.IsMouseOver)
            {
                _alertTimer.Interval = TimeSpan.FromMilliseconds(1200);
                _alertTimer.Start();
                return;
            }

            NextAlert();
        };

        // Request: header, question, what exactly, buttons, and a thin line running out with the time to answer.
        var header = new DockPanel();
        DockPanel.SetDock(_askMore, Dock.Right);
        header.Children.Add(_askMore);
        header.Children.Add(_askIcon);
        header.Children.Add(_askApp);
        _askContent.Children.Add(header);
        _askContent.Children.Add(_askTitle);
        _askDetailBox.Child = _askDetail;
        _askContent.Children.Add(_askDetailBox);
        _askContent.Children.Add(_askNote);
        _askButtons.Children.Add(PanelButton("Answer there", Color.FromArgb(0, 0, 0, 0), Color.FromRgb(0x9C, 0xA3, 0xAF), () => Answer(NotchBridge.AskThere), "Let the assistant ask in its own window"));
        _askButtons.Children.Add(PanelButton("Deny", Color.FromRgb(0x2A, 0x2F, 0x3A), Colors.White, () => Answer(NotchBridge.Deny), "Do not allow this"));
        _askButtons.Children.Add(PanelButton("Allow", Color.FromRgb(0x3D, 0x8B, 0xFF), Colors.White, () => Answer(NotchBridge.Allow), "Allow this once"));
        _askContent.Children.Add(_askButtons);
        _askPanel.Children.Add(_askContent);
        var clock = new Rectangle
        {
            Height = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0x5A, 0xAC, 0xFF)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(26, 0, 26, 3),
            RadiusX = 1,
            RadiusY = 1,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _askClock,
        };
        _askPanel.Children.Add(clock);
        _armTimer.Tick += (_, _) =>
        {
            _armTimer.Stop();
            _askButtons.IsHitTestVisible = true;
            _askButtons.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Motion.Reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(180)));
        };

        _surface.Children.Add(BuildDropPanel());
        _surface.Children.Add(_alertPanel);
        _surface.Children.Add(_askPanel);
    }

    private static Button PanelButton(string text, Color background, Color foreground, Action click, string tip)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(background));
        border.SetValue(Border.PaddingProperty, new Thickness(14, 6, 14, 6));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true, Setters = { new Setter(OpacityProperty, 0.82) } });
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(foreground) },
            Template = template,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = tip,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The panels stay centred on the island while it changes size.</summary>
    private void PlacePanels(double width)
    {
        Canvas.SetLeft(_dropPanel, (width - DropWidth) / 2);
        Canvas.SetLeft(_alertPanel, (width - ExpandedWidth) / 2);
        Canvas.SetLeft(_askPanel, (width - AskWidth) / 2);
    }

    private FrameworkElement? PanelElement(NotchPanel panel) => panel switch
    {
        NotchPanel.Drop => _dropPanel,
        NotchPanel.Alert => _alertPanel,
        NotchPanel.Ask => _askPanel,
        _ => null,
    };

    /// <summary>Cross-fades to <paramref name="panel"/> (or back to the connection) while the island takes its size.</summary>
    private void ShowPanel(NotchPanel panel, double widthKick = 0, double heightKick = 0)
    {
        if (_panel == panel)
        {
            Resize(widthKick, heightKick);
            return;
        }

        var reduced = Motion.Reduced;
        TimeSpan Ms(int ms) => reduced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (PanelElement(_panel) is { } old)
        {
            old.IsHitTestVisible = false;
            old.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(140)));
        }

        _panel = panel;
        if (PanelElement(panel) is { } now)
        {
            now.IsHitTestVisible = true;
            now.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(260)) { BeginTime = Ms(90), EasingFunction = ease });
            ((TranslateTransform)now.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, Ms(420)) { BeginTime = Ms(60), EasingFunction = ease });
        }

        _main.IsHitTestVisible = panel == NotchPanel.None;
        Dispatcher.BeginInvoke(SyncGlassCapture);
        _main.BeginAnimation(OpacityProperty, new DoubleAnimation(panel == NotchPanel.None ? 1 : 0, Ms(panel == NotchPanel.None ? 300 : 120)) { BeginTime = panel == NotchPanel.None ? Ms(80) : TimeSpan.Zero });
        if (panel == NotchPanel.None)
        {
            if (!_pill.IsMouseOver && _expanded)
            {
                _expanded = false;
                _expand.To(0);
            }

            RestartFade();
        }
        else
        {
            _fadeTimer.Stop();
            _holdTimer.Stop();
            SetFaded(false);
        }

        Resize(widthKick, heightKick);
    }

    /// <summary>Something arrives from above: the island drops a little further than its size and wobbles back.</summary>
    private void Arrive(double strength)
    {
        _body.Membrane.Impulse(0.5, strength, 0.3);
        Animate();
    }

    // ------------------------------------------------------------------ notifications

    /// <summary>A notification from Claude / ChatGPT (or a hook) for a few seconds; they queue.</summary>
    public void ShowAlert(AiAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if ((!_settings.AiAssistants && alert.Source != "plugin") || _hiddenForFullScreen || !IsVisible)
        {
            return;
        }

        if (_alerts.Count >= 6)
        {
            _alerts.Dequeue();
        }

        _alerts.Enqueue(alert);
        if (_panel is NotchPanel.None)
        {
            NextAlert();
        }
    }

    private void NextAlert()
    {
        if (_panel is NotchPanel.Ask or NotchPanel.Drop)
        {
            return;
        }

        if (!_alerts.TryDequeue(out var alert))
        {
            _alertShown = null;
            if (_panel == NotchPanel.Alert)
            {
                ShowPanel(NotchPanel.None);
            }

            return;
        }

        _alertShown = alert;
        SetIcon(_alertIcon, alert.Icon, alert.Source, 18, alert.App);
        _alertApp.Text = alert.App;
        _alertTitle.Text = alert.Title;
        _alertBody.Text = alert.Body ?? string.Empty;
        _alertBody.Visibility = string.IsNullOrEmpty(alert.Body) ? Visibility.Collapsed : Visibility.Visible;
        var first = _panel != NotchPanel.Alert;
        ShowPanel(NotchPanel.Alert, first ? 120 : 0, first ? 260 : 120);
        Arrive(first ? 260 : 150);
        if (!Motion.Reduced)
        {
            var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(560)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = new ElasticEase { Oscillations = 2, Springiness = 6 } };
            _alertIconPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            _alertIconPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        _alertTimer.Stop();
        _alertTimer.Interval = TimeSpan.FromSeconds(alert.Seconds ?? (_alerts.Count > 0 ? 3.2 : 5.5));
        _alertTimer.Start();
    }

    /// <summary>The app's own icon, or its initial on its colour when Windows has none.</summary>
    private static void SetIcon(Border target, ImageSource? icon, string source, double fontSize, string? name = null)
    {
        if (icon is not null)
        {
            target.Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill };
            target.Child = null;
            return;
        }

        var (color, letter) = source switch
        {
            "chatgpt" or NotchSources.Codex => (Color.FromRgb(0x10, 0xA3, 0x7F), source == NotchSources.Codex ? "X" : "G"),
            "plugin" => (Color.FromRgb(0x7C, 0x5C, 0xF6), string.IsNullOrEmpty(name) ? "•" : name[..1].ToUpperInvariant()),
            _ => (Color.FromRgb(0xD9, 0x77, 0x57), "C"),
        };
        target.Background = new SolidColorBrush(color);
        target.Child = new TextBlock { Text = letter, FontSize = fontSize, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    // ------------------------------------------------------------------ permission requests

    /// <summary>
    /// Shows a Claude Code / Codex request and returns the answer: allow, deny, or ask (the assistant asks itself —
    /// also when the notch is hidden, the request is withdrawn or the time runs out).
    /// </summary>
    public Task<string> AskAsync(NotchMessage message, ImageSource? icon, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if ((!_settings.AiAssistants && message.Source != "plugin") || _hiddenForFullScreen || !IsVisible || _asks.Count >= 8)
        {
            return Task.FromResult(NotchBridge.AskThere);
        }

        var pending = new PendingAsk(message, icon);
        pending.Registration = ct.Register(() => Dispatcher.BeginInvoke(() => Withdraw(pending)));
        _asks.Add(pending);
        if (_asks.Count == 1)
        {
            ShowAsk(pending);
        }
        else
        {
            UpdateAskCount();
        }

        return pending.Done.Task;
    }

    private void ShowAsk(PendingAsk pending)
    {
        var m = pending.Message;
        SetIcon(_askIcon, pending.Icon, m.Source, 13, m.Project);
        _askApp.Text = NotchBridge.AppName(m.Source) + (m.Project is { } project ? "  ·  " + project : string.Empty);
        _askTitle.Text = m.Title;
        _askDetail.Text = m.Detail ?? m.Tool ?? string.Empty;
        _askDetailBox.Visibility = string.IsNullOrEmpty(_askDetail.Text) ? Visibility.Collapsed : Visibility.Visible;
        _askNote.Text = m.Note ?? string.Empty;
        _askNote.Visibility = string.IsNullOrEmpty(m.Note) ? Visibility.Collapsed : Visibility.Visible;
        UpdateAskCount();

        // Its height follows what it says (measured free of the previous request's height).
        _askPanel.Height = double.NaN;
        _askContent.InvalidateMeasure();
        _askPanel.UpdateLayout();
        _askHeight = Math.Ceiling(Math.Max(_askPanel.DesiredSize.Height, _askContent.DesiredSize.Height));
        _askPanel.Height = _askHeight;

        // Not clickable for a moment: the island may have appeared right under a click meant for something else.
        _askButtons.IsHitTestVisible = false;
        _askButtons.BeginAnimation(OpacityProperty, null);
        _askButtons.Opacity = 0.4;
        _armTimer.Stop();
        _armTimer.Start();
        var run = new DoubleAnimation(1, 0, NotchBridge.AnswerTimeout - TimeSpan.FromSeconds(2));
        _askClock.BeginAnimation(ScaleTransform.ScaleXProperty, run);

        var first = _panel != NotchPanel.Ask;
        _alertTimer.Stop();
        if (_panel == NotchPanel.Alert && _alertShown is { } interrupted)
        {
            // The notification comes back after the request.
            _alerts.Enqueue(interrupted);
            _alertShown = null;
        }

        ShowPanel(NotchPanel.Ask, first ? 150 : 0, first ? 320 : 140);
        Arrive(first ? 340 : 180);
    }

    private void UpdateAskCount() => _askMore.Text = _asks.Count > 1 ? "+" + (_asks.Count - 1) + " more" : string.Empty;

    private void Answer(string decision)
    {
        if (_asks.Count == 0 || !_askButtons.IsHitTestVisible)
        {
            return;
        }

        var current = _asks[0];
        Settle(current, decision);
        if (decision == NotchBridge.Allow)
        {
            // A nod: the island dips once.
            Arrive(160);
        }

        ShowNextAsk();
    }

    private void Withdraw(PendingAsk pending)
    {
        var index = _asks.IndexOf(pending);
        if (index < 0)
        {
            return;
        }

        Settle(pending, NotchBridge.AskThere);
        if (index == 0)
        {
            ShowNextAsk();
        }
        else
        {
            UpdateAskCount();
        }
    }

    private void Settle(PendingAsk pending, string decision)
    {
        _asks.Remove(pending);
        pending.Registration.Dispose();
        pending.Done.TrySetResult(decision);
    }

    private void ShowNextAsk()
    {
        _armTimer.Stop();
        if (_asks.Count > 0)
        {
            ShowAsk(_asks[0]);
            return;
        }

        _askClock.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (_alerts.Count > 0)
        {
            NextAlert();
        }
        else
        {
            ShowPanel(NotchPanel.None);
        }
    }

    /// <summary>Every open request goes back to its assistant (notch off, assistants turned off, closing).</summary>
    private void DropAllRequests()
    {
        foreach (var pending in _asks.ToArray())
        {
            Settle(pending, NotchBridge.AskThere);
        }

        _alerts.Clear();
        _alertTimer.Stop();
        _armTimer.Stop();
        if (_panel is NotchPanel.Ask or NotchPanel.Alert)
        {
            ShowPanel(NotchPanel.None);
        }
    }
}
