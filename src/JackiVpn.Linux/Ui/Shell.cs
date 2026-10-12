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
        RequestedThemeVariant = ThemeVariant.Dark;
        // Fluent controls (toggles, combo highlights, focus) in the Windows app's accent.
        Resources["SystemAccentColor"] = Accent;
        Resources["SystemAccentColorLight1"] = Color.Parse("#5B97F8");
        Resources["SystemAccentColorDark1"] = Color.Parse("#2F6FD6");
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
    private bool _built;

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
                Content = new Border { Padding = new Thickness(32, 26, 32, 32), Child = Build() },
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
/// The main window, laid out like the Windows app: sidebar with the brand, page list and connection status, the
/// selected page on the right, toasts in the bottom-right corner.
/// </summary>
public sealed class Shell : Window, INotificationSink
{
    private readonly Dictionary<Page, Border> _nav = [];
    private readonly ContentControl _host = new();
    private readonly StackPanel _toasts = new() { Spacing = 8, Width = 350, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 22, 22) };
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Fill = B(Muted) };
    private readonly TextBlock _statusLine = new() { FontSize = 12, Foreground = B(Text2), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 165, Text = "Not connected" };
    private bool _closing;

    public Shell()
    {
        Title = "IBOX";
        Width = 1220;
        Height = 820;
        MinWidth = 960;
        MinHeight = 640;
        Background = B(Ui.Window);
        Foreground = B(Text);
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Services = new LinuxServices(this, () => true);
        Lifetime = new CancellationTokenSource();

        Dashboard = new DashboardPage(this);
        Servers = new ServersPage(this);
        Page[] pages =
        [
            Dashboard, Servers, new SubscriptionsPage(this), new UsagePage(this), new RoutingPage(this), new DnsPage(this),
            new NetworkPage(this), new AdvancedPage(this), new LogsPage(this), new SettingsPage(this), new AboutPage(this),
        ];

        var navList = new StackPanel { Spacing = 2 };
        foreach (var page in pages)
        {
            var item = NavItem(page);
            _nav[page] = item;
            navList.Children.Add(item);
        }

        var logo = new StackPanel
        {
            Margin = new Thickness(10, 2, 0, 26),
            Spacing = 7,
            Children = { Wordmark(22), Caption("Secure · Smart · Fast") },
        };
        var status = new Border
        {
            Background = B(CardBg),
            BorderBrush = B(Ui.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _dot, _statusLine } },
        };
        _dot.VerticalAlignment = VerticalAlignment.Center;
        var sidebarDock = new DockPanel { Margin = new Thickness(14, 20, 14, 14) };
        DockPanel.SetDock(logo, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        sidebarDock.Children.Add(logo);
        sidebarDock.Children.Add(status);
        sidebarDock.Children.Add(navList);

        var sidebar = new Border { Background = B(Ui.Sidebar), BorderBrush = B(Ui.Border), BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebarDock };
        var glow = new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.85, 0, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.85, 0, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(34, Accent.R, Accent.G, Accent.B), 0), new GradientStop(Colors.Transparent, 1) },
            },
        };
        var content = new Grid { Children = { glow, _host, _toasts } };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("236,*") };
        root.Children.Add(sidebar);
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;

        Services.Orchestrator.StatusChanged += (_, st) => Dispatcher.UIThread.Post(() => ShowStatus(st));
        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Navigate(Dashboard);
    }

    public LinuxServices Services { get; }

    public CancellationTokenSource Lifetime { get; }

    public DashboardPage Dashboard { get; }

    public ServersPage Servers { get; }

    public event EventHandler? Ready;

    public bool IsReady { get; private set; }

    public void Navigate(Page page)
    {
        foreach (var (p, item) in _nav)
        {
            var selected = p == page;
            item.Background = selected ? B(CardBg) : Brushes.Transparent;
            item.BorderBrush = selected ? B(Ui.Border) : Brushes.Transparent;
            ((Border)((Grid)item.Child!).Children[0]).Opacity = selected ? 1 : 0;
        }

        _host.Content = page;
        if (IsReady)
        {
            page.Show();
        }
    }

    public void Show(AppNotification notification) => Dispatcher.UIThread.Post(() => Toast(notification.Title, notification.Message));

    public void Toast(string title, string message, bool warning = false)
    {
        var color = warning ? Warning : Accent;
        var toast = new Border
        {
            Background = B(CardBg),
            BorderBrush = B(Ui.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 22, OffsetY = 6, Color = Color.FromArgb(100, 0, 0, 0) }),
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
        var accentBar = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), Background = AccentGradient, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-1, 0, 0, 0), Opacity = 0 };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Icon(page.Glyph, 17, Text2, 1.8), new TextBlock { Text = page.Title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center } },
        };
        var item = new Border
        {
            Height = 38,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid { Children = { accentBar, row } },
        };
        item.PointerEntered += (_, _) =>
        {
            if (_host.Content != page)
            {
                item.Background = B(CardBg, 0.5);
            }
        };
        item.PointerExited += (_, _) =>
        {
            if (_host.Content != page)
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
        Avalonia.Controls.Shapes.Path P(string data) => new() { Data = Geometry.Parse(data), Fill = Brushes.White };
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
        _dot.Fill = B(st.State switch
        {
            ConnectionState.Connected => Success,
            ConnectionState.Faulted => Danger,
            ConnectionState.Disconnected => Muted,
            _ => Warning,
        });
        _statusLine.Text = st.State == ConnectionState.Connected ? $"Connected · {st.ProfileName}" : Spaced(st.State.ToString());
    }

    private async Task StartAsync()
    {
        try
        {
            await Services.InitializeAsync(Lifetime.Token);
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
