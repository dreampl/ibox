using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;
using static JackiVpn.Linux.Ui.Ui;

namespace JackiVpn.Linux.Ui;

/// <summary>The round connect button: grey when off, pulsing while connecting, green (magenta gaming, sky streaming) when on.</summary>
public sealed class ConnectButton : Grid
{
    private readonly Ellipse _halo = new() { Width = 214, Height = 214 };
    private readonly Ellipse _ring = new() { Width = 178, Height = 178, StrokeThickness = 2 };
    private readonly Ellipse _disc = new() { Width = 150, Height = 150 };
    private readonly TextBlock _caption = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.5, HorizontalAlignment = HorizontalAlignment.Center, Foreground = B(Text2) };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Viewbox _power = Icon(Icons.Power, 46, Colors.White, 2.2);
    private readonly OrbView _orb = new() { State = Domain.Visuals.OrbState.Connecting, Width = 92, Height = 92, IsVisible = false };
    private IDisposable? _pulse;
    private (ConnectionState, ActivityProfile)? _shown;

    public ConnectButton()
    {
        Width = 230;
        Height = 250;
        Cursor = new Cursor(StandardCursorType.Hand);
        var face = new Grid { Width = 230, Height = 230, RenderTransform = _scale, Children = { _halo, _ring, _disc, _power, _orb } };
        _disc.Transitions = [new BrushTransition { Property = Shape.FillProperty, Duration = TimeSpan.FromMilliseconds(450) }];
        _halo.Transitions = [new BrushTransition { Property = Shape.FillProperty, Duration = TimeSpan.FromMilliseconds(450) }];
        _caption.Margin = new Thickness(0, 230, 0, 0);
        Children.Add(face);
        Children.Add(_caption);
        SetState(ConnectionState.Disconnected, ActivityProfile.Normal);
    }

    public void SetState(ConnectionState state, ActivityProfile activity)
    {
        if (_shown == (state, activity))
        {
            return;
        }

        _shown = (state, activity);
        var connected = state == ConnectionState.Connected;
        var busy = !ConnectionStateRules.IsIdle(state) && !connected;
        var (a, b) = connected
            ? activity switch
            {
                ActivityProfile.Gaming => (Gaming, Color.Parse("#A21CAF")),
                ActivityProfile.Streaming => (Streaming, Color.Parse("#0284C7")),
                _ => (Success, Success2),
            }
            : busy ? (Accent, Accent2) : (Idle, Idle2);
        _disc.Fill = Gradient(a, b);
        _ring.Stroke = B(connected || busy ? a : Ui.Border);
        _halo.Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(connected ? (byte)90 : (byte)50, a.R, a.G, a.B), 0.55), new GradientStop(Colors.Transparent, 1) } };
        _caption.Text = connected
            ? activity switch { ActivityProfile.Gaming => "GAMING", ActivityProfile.Streaming => "STREAMING", _ => "CONNECTED" }
            : state == ConnectionState.Disconnecting ? "STOPPING…"
            : busy ? "CANCEL" // a click now cancels; the status line says what is in progress
            : "CONNECT";
        _caption.Foreground = B(connected ? a : Text2);
        // While connecting a "connecting" thinking orb takes the place of the power glyph.
        _power.IsVisible = !busy;
        _orb.IsVisible = busy;
        _pulse?.Dispose();
        _pulse = null;
        _scale.ScaleX = _scale.ScaleY = 1;
        if (busy)
        {
            var start = DateTime.UtcNow;
            _pulse = DispatcherTimer.Run(() =>
            {
                _scale.ScaleX = _scale.ScaleY = 1 + 0.035 * Math.Sin((DateTime.UtcNow - start).TotalSeconds * Math.PI * 1.5);
                return true;
            }, TimeSpan.FromMilliseconds(33));
        }
        else
        {
            _ = Pop();
        }
    }

    public async Task Pop()
    {
        foreach (var v in new[] { 0.95, 0.98, 1.03, 1.01, 1.0 })
        {
            _scale.ScaleX = _scale.ScaleY = v;
            await Task.Delay(45);
        }
    }
}

public sealed class DashboardPage(Shell shell) : Page(shell)
{
    private readonly ConnectButton _button = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = T("Not connected", 22, FontWeight.SemiBold);
    private readonly TextBlock _detail = Caption("Choose a server or let smart connect pick the fastest.");
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _server = T("—", 13, FontWeight.SemiBold);
    private readonly TextBlock _reason = Hint(string.Empty);
    private readonly Border _error = new() { Background = B(Danger, 0.12), BorderBrush = B(Danger, 0.4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), IsVisible = false };
    private readonly TextBlock _flag = new() { FontSize = 34 };
    private readonly TextBlock _country = T("—", 17, FontWeight.SemiBold);
    private readonly TextBlock _place = Caption(string.Empty);
    private readonly TextBlock _ip = Caption(string.Empty);
    private readonly TextBlock _down = T("0 B/s", 20, FontWeight.SemiBold);
    private readonly TextBlock _up = T("0 B/s", 20, FontWeight.SemiBold);
    private readonly TextBlock _totals = Hint(string.Empty);
    private readonly TextBlock _ping = T("—", 20, FontWeight.SemiBold);
    private readonly TextBlock _timer = T("00:00", 20, FontWeight.SemiBold);
    private readonly Canvas _chart = new() { Height = 90, ClipToBounds = true };
    private readonly Queue<(double Down, double Up)> _history = new();
    private readonly TextBlock _speedDown = T("—", 22, FontWeight.SemiBold);
    private readonly TextBlock _speedUp = T("—", 22, FontWeight.SemiBold);
    private readonly TextBlock _speedPing = T("—", 22, FontWeight.SemiBold);
    private readonly TextBlock _speedPhase = Hint("Measures download, upload and latency through the VPN (Cloudflare).");
    private readonly ProgressBar _speedBar = new() { Maximum = 1, Height = 4, IsVisible = false };
    private readonly OrbView _speedOrb = new() { State = Domain.Visuals.OrbState.Listening, Tint = Success, Width = 34, Height = 34, IsVisible = false, Margin = new Thickness(0, 0, 10, 0) };
    private ToggleSwitch? _smart;
    private Button? _speedButton;
    private CancellationTokenSource? _speedCts;
    private ConnectionStatus _status = new();
    private DispatcherTimer? _clock;

    public override string Title => "Dashboard";

    public override string Glyph => Icons.Home;

    protected override Control Build()
    {
        _button.PointerPressed += async (_, _) => await ToggleAsync();
        _smart = Toggle(S.Settings.Current.Connection.SmartMode, v => S.Settings.Update(x => x with { Connection = x.Connection with { SmartMode = v } }));
        var choose = Ghost("Choose server", () => Shell.Navigate(Shell.Servers));
        var left = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _button,
                new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4, Children = { Center(_title), Center(_detail) } },
                _chips,
                new Border { Height = 1, Background = B(Ui.Border), Margin = new Thickness(0, 6) },
                Row("Smart connect", "Picks the fastest working server and moves away from failing ones.", _smart),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { new StackPanel { Spacing = 2, Children = { Hint("SERVER"), _server, _reason } }, Col(choose, 1) },
                },
            },
        }, new Thickness(22, 20));

        var location = Card(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Hint("LOCATION"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 6, 0, 0), Children = { _flag, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _country, _place, _ip } } } },
            },
        });

        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        stats.Children.Add(Stat("Download", _down, Icons.Down, Success, 0));
        stats.Children.Add(Stat("Upload", _up, Icons.Up, Accent2, 1));
        stats.Children.Add(Stat("Ping", _ping, Icons.Clock, Warning, 2));
        stats.Children.Add(Stat("Session", _timer, Icons.Clock, Text2, 3));
        var traffic = Card(new StackPanel { Spacing = 10, Children = { Hint("LIVE TRAFFIC"), stats, _chart, _totals } });

        _speedButton = Primary("Run test", () => _ = RunSpeedTestAsync());
        var speedGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        speedGrid.Children.Add(Stat("Download · Mbps", _speedDown, Icons.Down, Success, 0));
        speedGrid.Children.Add(Stat("Upload · Mbps", _speedUp, Icons.Up, Accent2, 1));
        speedGrid.Children.Add(Stat("Ping · ms", _speedPing, Icons.Clock, Warning, 2));
        var speed = Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { _speedOrb, Col(new StackPanel { Children = { Hint("SPEED TEST"), _speedPhase } }, 1), Col(_speedButton, 2) } },
                _speedBar,
                speedGrid,
            },
        });

        _error.Child = T(string.Empty, 12.5);
        var right = new StackPanel { Spacing = 16, Children = { _error, location, traffic, speed } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("400,*"), ColumnSpacing = 20 };
        grid.Children.Add(new StackPanel { Children = { left } });
        grid.Children.Add(Col(right, 1));

        S.Orchestrator.StatusChanged += (_, st) => Dispatcher.UIThread.Post(() => Apply(st));
        S.Settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = ShowSelectionAsync());
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ShowTimer();
        Apply(S.Orchestrator.Status);
        return new StackPanel { Children = { Header("Dashboard", "Connection, location and live traffic."), grid } };
    }

    public override void OnShown()
    {
        if (_smart is not null)
        {
            _smart.IsChecked = S.Settings.Current.Connection.SmartMode;
        }

        _ = ShowSelectionAsync();
    }

    public async Task ToggleAsync()
    {
        _ = _button.Pop();
        await Guard(async () =>
        {
            var state = S.Orchestrator.State;
            if (state == ConnectionState.Disconnecting)
            {
                return;
            }

            // While connecting this cancels.
            if (!ConnectionStateRules.IsIdle(state))
            {
                await S.Orchestrator.DisconnectAsync();
                return;
            }

            var c = S.Settings.Current.Connection;
            if (!c.SmartMode && c.SelectedProfileId is null)
            {
                Shell.Toast("Choose a server", "Pick a server on the Servers page, or turn on smart connect.", warning: true);
                Shell.Navigate(Shell.Servers);
                return;
            }

            await S.Orchestrator.ConnectAsync(new ConnectRequest(), Shell.Lifetime.Token);
        });
    }

    private void Apply(ConnectionStatus st)
    {
        _status = st;
        _button.SetState(st.State, st.State == ConnectionState.Connected ? st.Activity : ActivityProfile.Normal);
        var connected = st.State == ConnectionState.Connected;
        _title.Text = st.State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Disconnected => "Not connected",
            ConnectionState.Faulted => "Connection failed",
            _ => Spaced(st.State.ToString()) + "…",
        };
        _detail.Text = connected
            ? (st.ProfileName ?? string.Empty)
            : st.State == ConnectionState.Disconnected ? "Choose a server or let smart connect pick the fastest." : st.ProfileName ?? string.Empty;
        _chips.Children.Clear();
        if (connected)
        {
            _chips.Children.Add(Badge(st.Mode == ConnectionMode.Tun ? "TUN · all apps" : $"System proxy · 127.0.0.1:{st.LocalProxyPort}", Accent));
            if (st.JackiSmartConnectActive)
            {
                _chips.Children.Add(Badge("Jacki Smart Connect", Accent2));
            }

            if (st.Activity != ActivityProfile.Normal)
            {
                _chips.Children.Add(Badge(st.Activity == ActivityProfile.Gaming ? "Gaming" : "Streaming", st.Activity == ActivityProfile.Gaming ? Gaming : Streaming));
            }
        }

        _reason.Text = st.SelectionReason ?? string.Empty;
        _error.IsVisible = !connected && !string.IsNullOrEmpty(st.LastError) && ConnectionStateRules.IsIdle(st.State);
        ((TextBlock)_error.Child!).Text = st.LastError ?? string.Empty;

        _flag.Text = connected ? Flag(st.ExitCountryCode) : "🌐";
        _country.Text = connected ? st.ExitCountryName ?? (st.PublicIp is null ? "Locating…" : "Unknown country") : "Not connected";
        _place.Text = connected ? string.Join(" · ", new[] { st.ExitCity, st.ExitIsp }.Where(x => !string.IsNullOrWhiteSpace(x))) : "Your traffic is not protected.";
        _ip.Text = connected && st.PublicIp is { } ip ? "IP " + ip : string.Empty;
        _ping.Text = connected && st.LatencyMs is { } ms ? $"{ms:0} ms" : "—";
        _ping.Foreground = B(st.LatencyMs switch { null => Text, < 150 => Success, < 300 => Warning, _ => Danger });

        if (connected && st.Traffic is { } t)
        {
            _down.Text = Speed(t.DownloadBytesPerSecond);
            _up.Text = Speed(t.UploadBytesPerSecond);
            _totals.Text = $"Total ↓ {Bytes(t.DownloadBytesTotal)}   ↑ {Bytes(t.UploadBytesTotal)}";
            _history.Enqueue((t.DownloadBytesPerSecond, t.UploadBytesPerSecond));
            while (_history.Count > 60)
            {
                _history.Dequeue();
            }

            DrawChart();
        }
        else if (!connected)
        {
            _down.Text = _up.Text = "0 B/s";
            _totals.Text = string.Empty;
            _history.Clear();
            DrawChart();
        }

        if (connected)
        {
            _clock?.Start();
        }
        else
        {
            _clock?.Stop();
            _timer.Text = "00:00";
        }

        ShowTimer();
        if (_speedButton is not null)
        {
            _speedButton.IsEnabled = connected || _speedCts is not null;
        }
    }

    private void ShowTimer()
    {
        if (_status.State == ConnectionState.Connected && _status.ConnectedSinceUtc is { } since)
        {
            var d = DateTimeOffset.UtcNow - since;
            _timer.Text = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : d.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        var width = Math.Max(200, _chart.Bounds.Width);
        var max = Math.Max(16 * 1024, _history.Count == 0 ? 0 : _history.Max(h => Math.Max(h.Down, h.Up)));
        Polyline Line(Func<(double Down, double Up), double> pick, Color color)
        {
            var points = _history.Select((h, i) => new Point(i * width / 59.0, 86 - pick(h) / max * 80)).ToList();
            return new Polyline { Points = points, Stroke = B(color), StrokeThickness = 2, StrokeJoin = PenLineJoin.Round };
        }

        if (_history.Count > 1)
        {
            _chart.Children.Add(Line(h => h.Up, Accent2));
            _chart.Children.Add(Line(h => h.Down, Success));
        }
    }

    private async Task ShowSelectionAsync()
    {
        var c = S.Settings.Current.Connection;
        if (c.SmartMode)
        {
            _server.Text = "Smart: fastest working server";
            return;
        }

        var p = c.SelectedProfileId is { } id ? await S.Profiles.GetAsync(id, Shell.Lifetime.Token) : null;
        _server.Text = p?.DisplayName ?? "No server selected";
    }

    private async Task RunSpeedTestAsync()
    {
        if (_speedCts is not null)
        {
            await _speedCts.CancelAsync();
            return;
        }

        var st = S.Orchestrator.Status;
        if (st.State != ConnectionState.Connected || st.Mode is not { } mode)
        {
            return;
        }

        _speedCts = CancellationTokenSource.CreateLinkedTokenSource(Shell.Lifetime.Token);
        _speedButton!.Content = "Cancel";
        _speedBar.IsVisible = true;
        _speedOrb.IsVisible = true;
        _speedDown.Text = _speedUp.Text = _speedPing.Text = "—";
        var progress = new Progress<SpeedTestProgress>(p =>
        {
            _speedBar.Value = p.Phase switch
            {
                SpeedTestPhase.Latency => 0.05,
                SpeedTestPhase.Download => 0.05 + p.Fraction * 0.55,
                SpeedTestPhase.Upload => 0.6 + p.Fraction * 0.4,
                _ => 1,
            };
            _speedPhase.Text = p.Phase switch
            {
                SpeedTestPhase.Latency => "Measuring latency…",
                SpeedTestPhase.Download => $"Download {p.CurrentMbps:0.0} Mbps",
                SpeedTestPhase.Upload => $"Upload {p.CurrentMbps:0.0} Mbps",
                _ => "Done",
            };
        });
        try
        {
            var r = await Task.Run(() => S.SpeedTest.RunAsync(mode, st.LocalProxyPort, progress, _speedCts.Token));
            _speedDown.Text = r.DownloadMbps is { } d ? $"{d:0.0}" : "—";
            _speedUp.Text = r.UploadMbps is { } u ? $"{u:0.0}" : "—";
            _speedPing.Text = r.IdleLatencyMs is { } l ? $"{l:0}" : "—";
            _speedPhase.Text = r.Error is { } e ? "Test incomplete: " + e : $"Cloudflare {r.Server}" + (r.LoadedLatencyMs is { } ll ? $" · {ll:0} ms under load" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            _speedPhase.Text = "Speed test cancelled.";
        }
        finally
        {
            _speedCts.Dispose();
            _speedCts = null;
            _speedButton.Content = "Run test";
            _speedBar.IsVisible = false;
            _speedOrb.IsVisible = false;
        }
    }

    private static Control Stat(string label, TextBlock value, string icon, Color color, int column)
    {
        var cell = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(icon, 14, color), Hint(label) } },
                value,
            },
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static T Col<T>(T control, int column)
        where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static TextBlock Center(TextBlock t)
    {
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        return t;
    }
}
