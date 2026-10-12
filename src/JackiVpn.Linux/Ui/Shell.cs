using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Notifications;
using JackiVpn.Domain.Connection;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

public sealed class GuiApp : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // Fluent controls (toggles, combo highlights, focus) in the Windows app's accent; the palette follows the
        // saved choice (System / Light / Dark) as soon as the settings are loaded.
        Look.Apply(Domain.Settings.AppTheme.System);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Shell();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>One page of the window (built on first visit, refreshed on every visit).</summary>
public abstract class Page(Shell shell) : UserControl
{
    private readonly List<Action> _undo = [];
    private bool _built;

    /// <summary>Forget the built page (theme changed): undo its event subscriptions; it builds again on the next visit.</summary>
    public void Reset()
    {
        foreach (var undo in _undo)
        {
            undo();
        }

        _undo.Clear();
        _built = false;
        Content = null;
    }

    /// <summary>Registers how to undo a subscription made while building (run by <see cref="Reset"/>).</summary>
    protected void Track(Action undo) => _undo.Add(undo);

    protected Shell Shell { get; } = shell;

    protected LinuxServices S => Shell.Services;

    public abstract string Title { get; }

    public abstract string Glyph { get; }

    protected abstract Control Build();

    public virtual void OnShown()
    {
    }

    public void Show()
    {
        if (!_built)
        {
            _built = true;
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new Border { Padding = new Thickness(20, 8, 30, 30), Child = Build() },
            };
        }

        OnShown();
    }

    /// <summary>Runs UI work safely: errors become a warning toast instead of crashing the window.</summary>
    protected async Task Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Shell.Toast("Something went wrong", Security.SecretRedactor.Redact(ex.Message), warning: true);
        }
    }
}

/// <summary>
/// The main window, laid out like the Windows app: an icon rail (logo, page tiles, profile), a top bar with the page
/// title, the protection line, the status pill, notifications and the light/dark switch, the page below, toasts in
/// the bottom-right corner. A theme change rebuilds the chrome and the pages with the new palette.
/// </summary>
public sealed class Shell : Window, INotificationSink
{
    private readonly Dictionary<Page, Border> _nav = [];
    private readonly ContentControl _host = new();
    private readonly StackPanel _toasts = new() { Spacing = 8, Width = 350, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 22, 22) };
    private readonly List<(string Title, string Message, bool Warning, DateTime At)> _notices = [];
    private Page[] _pages;
    private Ellipse _dot = new();
    private Ellipse _taglineDot = new();
    private TextBlock _pill = new();
    private TextBlock _title = new();
    private TextBlock _tagline = new();
    private Border _badge = new();
    private TextBlock _badgeText = new();
    private int _unread;
    private Page? _current;
    private bool _closing;

    public Shell()
    {
        Title = "IBOX";
        Width = 1320;
        Height = 880;
        MinWidth = 1024;
        MinHeight = 700;
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Services = new LinuxServices(this, () => true);
        Lifetime = new CancellationTokenSource();

        _pages = CreatePages();
        BuildChrome();
        Look.Changed += (_, _) =>
        {
            // Pages keep controls made with the old palette: make them again with the new one.
            var index = _current is { } shown ? Array.IndexOf(_pages, shown) : 0;
            foreach (var page in _pages)
            {
                page.Reset();
            }

            _pages = CreatePages();
            BuildChrome();
            Navigate(_pages[Math.Max(0, index)]);
        };
        Services.Orchestrator.StatusChanged += (_, st) => Dispatcher.UIThread.Post(() => ShowStatus(st));
        Services.Settings.Changed += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            if (s.Ui.Theme != Look.Choice)
            {
                Look.Apply(s.Ui.Theme);
            }
        });
        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Navigate(_pages[0]);
    }

    public LinuxServices Services { get; }

    public CancellationTokenSource Lifetime { get; }

    public DashboardPage Dashboard { get; private set; } = null!;

    public ServersPage Servers { get; private set; } = null!;

    public SubscriptionsPage Subscriptions { get; private set; } = null!;

    private Page[] CreatePages()
    {
        Dashboard = new DashboardPage(this);
        Servers = new ServersPage(this);
        Subscriptions = new SubscriptionsPage(this);
        return
        [
            Dashboard, Servers, Subscriptions, new UsagePage(this), new RoutingPage(this), new DnsPage(this),
            new NetworkPage(this), new AdvancedPage(this), new LogsPage(this), new SettingsPage(this), new AboutPage(this),
        ];
    }

    public event EventHandler? Ready;

    public bool IsReady { get; private set; }

    private void BuildChrome()
    {
        // The page host and the toast stack survive a rebuild: take them out of the old layout first.
        _toasts.Children.Clear();
        (_toasts.Parent as Panel)?.Children.Remove(_toasts);
        (_host.Parent as Panel)?.Children.Remove(_host);
        Background = B(Ui.Window);
        Foreground = B(Text);
        _nav.Clear();

        // Icon rail
        var navList = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var page in _pages)
        {
            var item = NavItem(page);
            _nav[page] = item;
            navList.Children.Add(item);
        }

        var profile = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(6),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = B(TileBg), BorderBrush = B(Ui.Border), BorderThickness = new Thickness(1), Child = Icon(Icons.User, 17, Text2, 1.8) },
                    Icon(Icons.Chevron, 10, Muted, 2),
                },
            },
        };
        ToolTip.SetTip(profile, "Profile");
        profile.Click += (_, _) => ProfileRequested?.Invoke(this, EventArgs.Empty);
        var railDock = new DockPanel();
        DockPanel.SetDock(profile, Dock.Bottom);
        railDock.Children.Add(profile);
        railDock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, Content = navList });
        var rail = Clay(railDock, new Thickness(0, 18, 0, 12), 28);
        rail.Margin = new Thickness(16, 0, 16, 18);
        var logo = Wordmark(19);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        logo.Margin = new Thickness(0, 28, 0, 22);
        var left = new DockPanel { Width = 112 };
        DockPanel.SetDock(logo, Dock.Top);
        left.Children.Add(logo);
        left.Children.Add(rail);

        // Top bar
        _dot = new Ellipse { Width = 9, Height = 9, Fill = B(Muted), VerticalAlignment = VerticalAlignment.Center };
        _pill = new TextBlock { Text = "VPN Off", FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var pill = Pill(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _dot, _pill } });
        pill.Margin = new Thickness(0, 0, 12, 0);
        _badgeText = new TextBlock { FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _badge = new Border { Background = B(Danger), CornerRadius = new CornerRadius(8), MinWidth = 16, Height = 16, Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0), IsHitTestVisible = false, IsVisible = _unread > 0, Child = _badgeText };
        _badgeText.Text = _unread.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Button? bell = null;
        bell = RoundButton(Icons.Bell, () => ShowNotices(bell!), "Notifications");
        var bellHost = new Grid { Margin = new Thickness(0, 0, 12, 0), Children = { bell, _badge } };
        var theme = RoundButton(Look.P.IsLight ? Icons.Moon : Icons.Sun, () =>
        {
            var next = Look.P.IsLight ? Domain.Settings.AppTheme.Dark : Domain.Settings.AppTheme.Light;
            Services.Settings.Update(x => x with { Ui = x.Ui with { Theme = next } });
        }, "Light / dark");
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Children = { pill, bellHost, theme } };
        _title = new TextBlock { FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = B(Text), Text = _current?.Title ?? "Dashboard" };
        _taglineDot = new Ellipse { Width = 9, Height = 9, Fill = B(Muted), Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _tagline = new TextBlock { FontSize = 14, Foreground = B(Text2), Text = "Not protected" };
        var titles = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            Children = { _title, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 4, 0, 0), Children = { _taglineDot, _tagline } } },
        };
        var top = new DockPanel { Margin = new Thickness(20, 24, 30, 10) };
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(titles);
        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(_host);

        var glow = new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.75, 0.05, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.75, 0.05, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops = { new GradientStop(WithAlpha(Accent, Look.P.IsLight ? 0.06 : 0.09), 0), new GradientStop(Colors.Transparent, 1) },
            },
        };
        var content = new Grid { Children = { glow, main, _toasts } };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        root.Children.Add(left);
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;
        ShowStatus(Services.Orchestrator.Status);
    }

    /// <summary>The profile button at the bottom of the rail.</summary>
    public event EventHandler? ProfileRequested;

    public void Navigate(Page page)
    {
        _current = page;
        foreach (var (p, item) in _nav)
        {
            StyleNav(item, p == page);
        }

        _title.Text = page.Title;
        _host.Content = page;
        if (IsReady)
        {
            page.Show();
        }
    }

    private void ShowNotices(Control anchor)
    {
        _unread = 0;
        _badge.IsVisible = false;
        var list = new StackPanel { Spacing = 12, Width = 330 };
        list.Children.Add(new DockPanel
        {
            Children =
            {
                new TextBlock { Text = "Notifications", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = B(Text) },
            },
        });
        if (_notices.Count == 0)
        {
            list.Children.Add(Hint("Nothing new."));
        }

        foreach (var n in Enumerable.Reverse(_notices).Take(20))
        {
            var color = n.Warning ? Warning : Accent;
            list.Children.Add(new DockPanel
            {
                Children =
                {
                    new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), Background = B(color, 0.16), VerticalAlignment = VerticalAlignment.Top, [DockPanel.DockProperty] = Dock.Left, Child = new TextBlock { Text = n.Warning ? "!" : "i", Foreground = B(color), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                    new TextBlock { Text = n.At.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, Foreground = B(Muted), [DockPanel.DockProperty] = Dock.Right, Margin = new Thickness(8, 2, 0, 0) },
                    new StackPanel { Children = { T(n.Title, 13, FontWeight.SemiBold), T(n.Message, 12, color: Text2) } },
                },
            });
        }

        var flyout = new Flyout { Content = new Border { Padding = new Thickness(4), Child = new ScrollViewer { MaxHeight = 420, Content = list } }, Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.ShowAt(anchor);
    }

    private void StyleNav(Border item, bool selected)
    {
        item.Background = selected ? AccentGradientVertical() : Brushes.Transparent;
        item.BorderBrush = selected ? B(Accent, 0.6) : Brushes.Transparent;
        item.BoxShadow = selected ? new BoxShadows(new BoxShadow { OffsetY = 6, Blur = 18, Spread = -2, Color = WithAlpha(Accent, Look.P.IsLight ? 0.35 : 0.5) }) : default;
        if (item.Child is Viewbox icon && icon.Child is Canvas c && c.Children[0] is Avalonia.Controls.Shapes.Path path)
        {
            path.Stroke = selected ? Brushes.White : B(Text2);
        }
    }

    private static LinearGradientBrush AccentGradientVertical() => new()
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Look.P.AccentHover, 0), new GradientStop(Color.Parse("#2563EB"), 1) },
    };

    public void Show(AppNotification notification) => Dispatcher.UIThread.Post(() => Toast(notification.Title, notification.Message));

    public void Toast(string title, string message, bool warning = false)
    {
        _notices.Add((title, message, warning, DateTime.Now));
        if (_notices.Count > 50)
        {
            _notices.RemoveAt(0);
        }

        _unread++;
        _badgeText.Text = _unread.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _badge.IsVisible = true;
        var color = warning ? Warning : Accent;
        var toast = new Border
        {
            Background = CardFill,
            BorderBrush = B(Ui.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12),
            BoxShadow = ClayShadow(raised: true),
            Child = new DockPanel
            {
                Children =
                {
                    new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 12, 0), Background = B(color, 0.16), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = warning ? "!" : "i", Foreground = B(color), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                    new StackPanel { Children = { T(title, 13, FontWeight.SemiBold), T(message, 12, color: Text2) } },
                },
            },
        };
        _toasts.Children.Add(toast);
        DispatcherTimer.RunOnce(() => _toasts.Children.Remove(toast), TimeSpan.FromSeconds(warning ? 7 : 4.5));
    }

    /// <summary>A small modal text prompt (rename, …). Null when cancelled.</summary>
    public async Task<string?> PromptAsync(string title, string label, string? value)
    {
        var box = new TextBox { Text = value ?? string.Empty, Width = 360 };
        var dialog = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Background = B(CardBg), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        string? result = null;
        var ok = Primary("OK", () => { result = box.Text; dialog.Close(); });
        var cancel = Ghost("Cancel", () => dialog.Close());
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children = { T(label), box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } },
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = box.Text;
                dialog.Close();
            }
        };
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string yes)
    {
        var dialog = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Background = B(CardBg), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var result = false;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            MaxWidth = 420,
            Children =
            {
                T(message),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { Ghost("Cancel", () => dialog.Close()), Primary(yes, () => { result = true; dialog.Close(); }) },
                },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }

    private Border NavItem(Page page)
    {
        var item = new Border
        {
            Width = 58,
            Height = 58,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = Icon(page.Glyph, 21, Text2, 1.8),
        };
        ToolTip.SetTip(item, page.Title);
        item.PointerEntered += (_, _) =>
        {
            if (_current != page)
            {
                item.Background = B(TileBg);
            }
        };
        item.PointerExited += (_, _) =>
        {
            if (_current != page)
            {
                item.Background = Brushes.Transparent;
            }
        };
        item.PointerPressed += (_, _) => Navigate(page);
        return item;
    }

    /// <summary>The IBOX wordmark (vector, from assets/brand/ibox-wordmark.svg): four bars for the I, the blue pixel in the O.</summary>
    public static Viewbox Wordmark(double height)
    {
        Avalonia.Controls.Shapes.Path P(string data) => new() { Data = Geometry.Parse(data), Fill = B(Text) };
        return new Viewbox
        {
            Height = height,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Canvas
            {
                Width = 515,
                Height = 127,
                Children =
                {
                    P("M1 0H13V127H1Z M19 0H29V127H19Z M38 0H45V127H38Z M62 0H68V127H62Z"),
                    P("F0 M96 0H176Q190 0 190 14V50H204Q218 50 218 64V97Q218 127 188 127H96ZM123 27H163V50H123ZM123 73H191V100H123Z"),
                    P("M272 0H368V127H250Q242 127 242 119V30H269V100H341V27H272Z"),
                    // Both strokes of the X wound the same way so their overlap stays filled.
                    P("F1 M416 0H448L514 127H482Z M376 127L479 0H511L408 127Z"),
                    new Rectangle { Width = 22, Height = 22, Fill = B(Color.Parse("#3D9BFF")), [Canvas.LeftProperty] = 309, [Canvas.TopProperty] = 53 },
                },
            },
        };
    }

    private void ShowStatus(ConnectionStatus st)
    {
        var color = st.State switch
        {
            ConnectionState.Connected => Success,
            ConnectionState.Faulted => Danger,
            ConnectionState.Disconnected => Muted,
            _ => Warning,
        };
        _dot.Fill = _taglineDot.Fill = B(color);
        (_tagline.Text, _pill.Text) = st.State switch
        {
            ConnectionState.Connected => ("Secure • Fast • Stable", "VPN Connected"),
            ConnectionState.Faulted => ("Connection problem — open the dashboard", "VPN Error"),
            ConnectionState.Disconnected => ("Not protected", "VPN Off"),
            ConnectionState.Disconnecting => ("Disconnecting…", "Stopping…"),
            _ => ("Connecting…", "Connecting…"),
        };
    }

    private async Task StartAsync()
    {
        try
        {
            await Services.InitializeAsync(Lifetime.Token);
            Look.Apply(Services.Settings.Current.Ui.Theme);
            IsReady = true;
            (_host.Content as Page)?.Show();
            Ready?.Invoke(this, EventArgs.Empty);
            _ = Task.Run(() => Services.SubscriptionService.RunAutoUpdateLoopAsync(TimeSpan.FromMinutes(1), Lifetime.Token));
            _ = Task.Run(() => Services.SubscriptionService.RunAccountMonitorAsync(Lifetime.Token));
            Services.SearchDnsIfNeeded();
            if (Services.Settings.Current.Startup.AutoConnect)
            {
                await Dashboard.ToggleAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Toast("Could not start", Security.SecretRedactor.Redact(ex.Message) + " — details in " + Services.LogsDir, warning: true);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        try
        {
            await Services.Orchestrator.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.Logger.LogWarningSafe(ex.Message);
        }
        finally
        {
            await Lifetime.CancelAsync();
            await Services.DisposeAsync();
            Close();
        }
    }
}

internal static class LoggerExtensions
{
    public static void LogWarningSafe(this Microsoft.Extensions.Logging.ILogger logger, string message) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "{Message}", Security.SecretRedactor.Redact(message));
}
