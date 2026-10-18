using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JackiVpn.Bar.Sources;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.Views;
using JackiVpn.Domain.Activity;
using JackiVpn.Domain.Agent;
using JackiVpn.Domain.Bar;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Visuals;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.Bar;

/// <summary>
/// IBAR: the IBOX notch as an app of its own. One island at the top centre of every monitor (shared state), the
/// shelf, Claude / ChatGPT notifications, requests and state circles, plugins (Alita…), chat, and — while IBOX runs —
/// the VPN: state and ping, cancel, switch server, usage, traffic mode, Dual VPN and network. Per-user, starts with
/// Windows; nothing runs while nothing changes.
/// </summary>
public static partial class BarApp
{
    private static readonly Dictionary<string, NotchWindow> Notches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (Color Color, string Text)> Chips = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PluginBoard> Boards = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ImageSource?> PluginIcons = new(StringComparer.Ordinal);
    private static BarSettingsStore _settings = null!;
    private static NotchShelf _shelf = null!;
    private static AiAssistants _assistants = null!;
    private static IboxLink _ibox = null!;
    private static PluginHost _plugins = null!;
    private static MediaSessionSource _media = null!;
    private static TrayIcon? _tray;
    private static ControlPanel? _panel;
    private static ChatWindow? _chat;
    private static DispatcherTimer? _trim;
    private static readonly ActivityBus Bus = new();
    private static readonly Dictionary<string, ImageSource?> ActivityIcons = new(StringComparer.OrdinalIgnoreCase);
    private static DispatcherTimer? _sweep;
    private static TaskSources? _sources;
    private static long _lastSound;

    [STAThread]
    public static int Main(string[] args)
    {
        // A test copy (its own data folder) runs beside the installed IBAR.
        var testData = Environment.GetEnvironmentVariable("IBAR_DATA");
        using var single = new Mutex(true, testData is { Length: > 0 } ? @"Local\IBAR.Test." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(testData.ToUpperInvariant())))[..16] : @"Local\IBAR.Notch", out var first);
        if (!first)
        {
            return 0;
        }

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var folder = Environment.GetEnvironmentVariable("IBAR_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IBAR");
        _settings = new BarSettingsStore(folder);
        T.Persian = _settings.Current.Language == "fa";
        Motion.Reduced = !SystemParameters.ClientAreaAnimation;
        AgentHooksSetup.CtlName = "ibar-ctl.exe";
        _shelf = new NotchShelf(Path.Combine(folder, "shelf"));
        _assistants = new AiAssistants(NullLogger.Instance);
        _ibox = new IboxLink();
        _plugins = new PluginHost(_settings);
        _media = new MediaSessionSource();

        app.Startup += (_, _) =>
        {
            // The entrance plays when Windows starts IBAR (and the very first time).
            var intro = args.Contains("--startup") || !File.Exists(Path.Combine(folder, "settings.json"));
            BuildNotches(intro);
            Wire();
            _settings.Changed += (_, s) => app.Dispatcher.BeginInvoke(() => Apply(s));
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => app.Dispatcher.BeginInvoke(() => BuildNotches());
            _tray = new TrayIcon("IBAR");
            _tray.Clicked += (_, _) => ShowPanel();
            _tray.MenuRequested += (_, _) => TrayMenu();
            SyncAutostart(_settings.Current.StartWithWindows);
            if (Environment.GetEnvironmentVariable("IBAR_DATA") is null || Environment.GetEnvironmentVariable("IBAR_REGISTER_HOST") == "1")
            {
                // The browser extension's host, from where this IBAR is (HKCU only).
                NativeHost.Register(AppContext.BaseDirectory);
            }

            _ibox.Start();
            _plugins.Start();
            _media.Start();
            _assistants.Assistants = _settings.Current.AiAssistants;
            _assistants.Sync(_settings.Current.AiAssistants || _settings.Current.WindowsNotifications);
            Chime.Enabled = _settings.Current.Sounds;
            Chime.Volume = _settings.Current.SoundVolume;
            _sources = new TaskSources(Bus, _settings);
            _sources.Sync(_settings.Current);

            if (intro)
            {
                _settings.Update(s => s);
            }

            if (Environment.GetEnvironmentVariable("IBAR_TOAST_TRACE") is { Length: > 0 } trace && Environment.GetEnvironmentVariable("IBAR_DATA") is not null)
            {
                ToastSignal.Seen += () => File.AppendAllText(trace, DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " toast window" + Environment.NewLine);
            }

            if (Environment.GetEnvironmentVariable("IBAR_TEST_OPEN") == "1" && Environment.GetEnvironmentVariable("IBAR_DATA") is not null)
            {
                // Measurements and screenshots of a test copy: keep the island open without a pointer.
                foreach (var n in Notches.Values)
                {
                    n.Dispatcher.BeginInvoke(() => n.DemoExpand(true), DispatcherPriority.ApplicationIdle);
                }
            }

            if (Environment.GetEnvironmentVariable("IBAR_TEST_STACK") is { Length: > 0 } stackAfter && Environment.GetEnvironmentVariable("IBAR_DATA") is not null)
            {
                // Test copies: open the circles behind the lead after a while (screenshots without a pointer).
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(double.Parse(stackAfter, System.Globalization.CultureInfo.InvariantCulture)) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    foreach (var n in Notches.Values)
                    {
                        n.DemoToggleActivity();
                    }
                };
                timer.Start();
            }

            ArmTrim();
        };
        app.Exit += (_, _) =>
        {
            _tray?.Dispose();
            _ = _assistants.DisposeAsync();
        };
        return app.Run();
    }

    // ------------------------------------------------------------------ one island per monitor

    private static void BuildNotches(bool playIntro = false)
    {
        var monitors = Monitors();
        var wanted = monitors.Where(m => !_settings.Current.HiddenMonitors.Contains(m.Key)).ToList();
        foreach (var key in Notches.Keys.Where(k => wanted.All(m => m.Key != k)).ToList())
        {
            Notches[key].Close();
            Notches.Remove(key);
        }

        foreach (var m in wanted)
        {
            if (Notches.TryGetValue(m.Key, out var existing))
            {
                existing.Monitor = m;
                existing.Dispatcher.BeginInvoke(() => existing.DemoExpand(false));
                continue;
            }

            var notch = new NotchWindow { Fixed = true, Monitor = m, Brand = "IBAR", ConnectionVisible = _ibox.Current is not null, IdleText = "IBOX is not running" };
            if (playIntro)
            {
                notch.PlayIntro();
            }

            notch.AttachShelf(_shelf);
            notch.OpenRequested += (_, _) => OpenIbox();
            notch.ToggleRequested += (_, _) => _ = ToggleAsync();
            notch.CustomizeRequested += (_, _) => ShowPanel();
            notch.HideRequested += (_, _) => _settings.Update(s => s with { HiddenMonitors = [.. s.HiddenMonitors, m.Key] });
            notch.MediaCommand += (_, c) => _ = c switch { < 0 => _media.PreviousAsync(), > 0 => _media.NextAsync(), _ => _media.PlayPauseAsync() };
            notch.PluginAction += (_, a) => RunAction(a.Plugin, a.Action);
            notch.MenuExtras = MenuItems;
            notch.MouseEnter += (_, _) => ArmTrim();
            Notches[m.Key] = notch;
            notch.Apply(NotchSettingsFrom(_settings.Current));
            notch.SetNowPlaying(_media.Current);
            Mirror(notch, _ibox.Current);
            notch.Show();
            notch.SetGlass(_settings.Current.Glass, _settings.Current.GlassStrength);
            notch.SetChips(Chips.Values.ToList());
            notch.SetBoards(Boards.Values.ToList());
            notch.ActivityIcon = ActivityIcon;
            notch.ActivityClicked += (_, item) => OpenFor(item);
            notch.SetActivities(Bus.Snapshot());
        }
    }

    private static NotchSettings NotchSettingsFrom(BarSettings s) => new()
    {
        Enabled = true,
        Shelf = s.Shelf,
        AiAssistants = s.AiAssistants,
        HideInFullScreen = s.HideInFullScreen,
        ShowNowPlaying = s.ShowNowPlaying,
        Sound = false,
    };

    private static void Apply(BarSettings s)
    {
        T.Persian = s.Language == "fa";
        Chime.Enabled = s.Sounds;
        Chime.Volume = s.SoundVolume;
        _sources?.Sync(s);
        _assistants.Assistants = s.AiAssistants;
        _assistants.Sync(s.AiAssistants || s.WindowsNotifications);
        Banners.Sync(s.WindowsNotifications ? s.QuietBanners : [], s.SeenApps.Select(a => a.Id));
        SyncAutostart(s.StartWithWindows);
        if (Notches.Keys.Except(Monitors().Where(m => !s.HiddenMonitors.Contains(m.Key)).Select(m => m.Key), StringComparer.OrdinalIgnoreCase).Any()
            || Monitors().Count(m => !s.HiddenMonitors.Contains(m.Key)) != Notches.Count)
        {
            BuildNotches();
        }

        foreach (var n in Notches.Values)
        {
            n.Apply(NotchSettingsFrom(s));
            n.SetGlass(s.Glass, s.GlassStrength);
        }
    }

    /// <summary>Notifications and requests go to the island on the monitor the pointer is on.</summary>
    private static NotchWindow? Active()
    {
        if (!GetCursorPos(out var p))
        {
            return Notches.Values.FirstOrDefault();
        }

        return Notches.Values.FirstOrDefault(n => n.Monitor is { } m && p.X >= m.Left && p.X < m.Left + m.Width && p.Y >= m.Top && p.Y < m.Top + m.Height)
            ?? Notches.Values.FirstOrDefault();
    }

    private static void Wire()
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        _shelf.Changed += (_, _) => ArmTrim();
        _media.Changed += (_, item) => ui.BeginInvoke(() =>
        {
            foreach (var n in Notches.Values)
            {
                n.SetNowPlaying(item);
            }
        });

        _assistants.Alert += (_, alert) => ui.BeginInvoke(() =>
        {
            Active()?.ShowAlert(alert);
            ArmTrim();
        });
        _assistants.Activity += (_, a) =>
        {
            // Claude / ChatGPT at work are tasks like any other (an amount nobody knows: the ring turns).
            var key = a.Source == "claude" ? "claude" : "chatgpt";
            if (a.Icon is not null)
            {
                ui.BeginInvoke(() => ActivityIcons[key] = a.Icon);
            }

            if (a.Activity == AgentActivity.None)
            {
                Bus.Remove(key, key);
            }
            else
            {
                Bus.Report(NotchWindow.AgentItem(key, a.Activity));
            }
        };
        Bus.Changed += (_, c) => ui.BeginInvoke(() => OnActivity(c.Change, c.Item));
        _plugins.Activity += (_, a) => Bus.Report(PluginRules.ActivityFrom(a.Request, a.Source, a.Icon));
        _plugins.Sound += (_, s) => ui.BeginInvoke(() => Chime.Play(s switch { "done" => ChimeKind.Done, "error" => ChimeKind.Error, _ => ChimeKind.Start }));
        _plugins.BridgeOpened += (_, bridge) => ui.BeginInvoke(() => _sources?.Bridged.Add(bridge));
        _plugins.BridgeClosed += (_, bridge) =>
        {
            Bus.RemoveSource(bridge);
            ui.BeginInvoke(() => _sources?.Bridged.Remove(bridge));
        };
        // Other apps' notifications: only those the user picked; every app that sends one is offered in the list.
        _assistants.AllowApp = id => _settings.Current.WindowsNotifications && _settings.Current.NotifyApps.Contains(id, StringComparer.OrdinalIgnoreCase);
        _assistants.AppSeen += (id, name) => ui.BeginInvoke(() =>
        {
            if (!_settings.Current.SeenApps.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.Update(s => s with { SeenApps = [.. s.SeenApps.TakeLast(39), new KnownApp(id, NotchBridge.Clean(name, 40) ?? id)] });
            }
        });
        _assistants.Ask = (message, icon, ct) =>
            ui.InvokeAsync(() => Active()?.AskAsync(message, icon, ct) ?? Task.FromResult(NotchBridge.AskThere)).Task.Unwrap();

        _ibox.Changed += (_, eco) => ui.BeginInvoke(() =>
        {
            foreach (var n in Notches.Values)
            {
                Mirror(n, eco);
            }

            ArmTrim();
        });

        _plugins.Approve = async (manifest, ct) =>
        {
            var icon = await ui.InvokeAsync(() => PluginIcon(manifest));
            var actions = manifest.Actions.Count == 0 ? "no actions" : string.Join("; ", manifest.Actions.Select(a => a.Title + ": " + (a.Url ?? a.Command + " " + string.Join(' ', a.Args))));
            var message = new NotchMessage
            {
                Kind = NotchMessage.Ask,
                Source = "plugin",
                Project = manifest.Name,
                Title = $"Allow “{manifest.Name}” on IBAR?",
                Detail = NotchBridge.Clean("Notifications, a status chip and a small dashboard. Actions: " + actions, 600, lines: 4),
                Note = manifest.Publisher is { } p ? "From " + p : null,
            };
            var decision = await ui.InvokeAsync(() => Active()?.AskAsync(message, icon, ct) ?? Task.FromResult(NotchBridge.AskThere)).Task.Unwrap();
            return decision == NotchBridge.Allow;
        };
        _plugins.Message += (_, m) => ui.BeginInvoke(() => OnPlugin(m.Plugin, m.Request));
    }

    // ------------------------------------------------------------------ activity

    /// <summary>A task started, changed or ended: every island shows the same list; a start or an end may sound.</summary>
    private static void OnActivity(ActivityChange change, ActivityItem item)
    {
        var all = Bus.Snapshot();
        foreach (var n in Notches.Values)
        {
            n.SetActivities(all);
        }

        // One sound at a time (a burst of downloads is one start); assistants stay quiet (they have their own).
        var now = Environment.TickCount64;
        if (item.Kind != "agent" && now - _lastSound > 1500)
        {
            ChimeKind? kind = change switch
            {
                ActivityChange.Started when !item.Finished => ChimeKind.Start,
                ActivityChange.Ended or ActivityChange.Started when item.State == ActivityState.Done => ChimeKind.Done,
                ActivityChange.Ended or ActivityChange.Started when item.State == ActivityState.Failed => ChimeKind.Error,
                _ => null,
            };
            if (kind is { } k && Chime.Play(k))
            {
                _lastSound = now;
            }
        }

        ArmSweep();
        ArmTrim();
    }

    /// <summary>Finished tasks leave after a moment: one timer for the next one due, never a repeating tick.</summary>
    private static void ArmSweep()
    {
        _sweep ??= new DispatcherTimer(DispatcherPriority.Background);
        _sweep.Stop();
        if (Bus.Sweep() is { } next)
        {
            _sweep.Interval = next < TimeSpan.FromMilliseconds(300) ? TimeSpan.FromMilliseconds(300) : next;
            _sweep.Tick -= OnSweep;
            _sweep.Tick += OnSweep;
            _sweep.Start();
        }
    }

    private static void OnSweep(object? sender, EventArgs e)
    {
        _sweep?.Stop();
        ArmSweep();
    }

    private static ImageSource? ActivityIcon(ActivityItem item)
    {
        var key = item.Icon ?? item.Source;
        if (ActivityIcons.TryGetValue(key, out var icon))
        {
            return icon;
        }

        ActivityIcons[key] = null;
        _ = LoadIconAsync(key, item);
        return null;
    }

    private static async Task LoadIconAsync(string key, ActivityItem item)
    {
        ImageSource? icon = null;
        try
        {
            if (item.Source is "claude" or "chatgpt")
            {
                icon = await _assistants.IconAsync(item.Source);
            }
            else if (item.Icon is { } path && Path.IsPathFullyQualified(path) && File.Exists(path))
            {
                icon = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? await ShellImages.GetAsync(path, 48)
                    : LoadPicture(path);
            }
            else if (TaskSources.ExeOf(item.Source) is { } exe)
            {
                icon = await ShellImages.GetAsync(exe, 48);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            icon = null;
        }

        ActivityIcons[key] = icon;
        if (icon is not null)
        {
            var all = Bus.Snapshot();
            foreach (var n in Notches.Values)
            {
                n.SetActivities(all);
            }
        }
    }

    /// <summary>A click on a task's circle: its app comes forward (a download: the downloads folder).</summary>
    private static void OpenFor(ActivityItem item)
    {
        if (item.Kind == "download")
        {
            try
            {
                Process.Start(new ProcessStartInfo(TaskSources.DownloadsFolder()) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // no folder
            }

            return;
        }

        TaskSources.Focus(item.Source);
    }

    // ------------------------------------------------------------------ IBOX

    private static void Mirror(NotchWindow notch, AgentEco? eco)
    {
        notch.ConnectionVisible = eco is not null;
        if (eco is null)
        {
            notch.ExtraLine = null;
            notch.Update(new ConnectionStatus(), VisualMode.Normal);
            return;
        }

        var st = eco.Status;
        var state = Enum.TryParse<ConnectionState>(st.State, out var s) ? s : ConnectionState.Disconnected;
        var status = new ConnectionStatus
        {
            State = state,
            ProfileName = st.Server,
            LatencyMs = st.LatencyMs,
            ExitCountryName = st.Country,
            ExitCity = st.City,
            Mode = st.Mode == "tun" ? ConnectionMode.Tun : st.Mode == "proxy" ? ConnectionMode.SystemProxy : null,
            ConnectedSinceUtc = st.ConnectedSinceUtc,
            LastError = st.LastError,
            Traffic = st.DownloadBytesPerSecond is { } down ? new TrafficSnapshot(0, 0, st.UploadBytesPerSecond ?? 0, down, DateTimeOffset.UtcNow) : null,
        };
        var mode = eco.TrafficMode.Split(' ')[0] switch
        {
            "eco" => VisualMode.Eco,
            "gaming" => VisualMode.Gaming,
            "streaming" => VisualMode.Streaming,
            "work" => VisualMode.Work,
            _ => VisualMode.Normal,
        };
        notch.ExtraLine = string.Join("  ·  ", new[]
        {
            "Today " + Desktop.ViewModels.Format.Bytes(eco.UsedTodayBytes),
            eco.DualVpnName is { } dual ? "Dual VPN " + (eco.DualVpnOn ? "on" : "off") : null,
            eco.Network,
        }.Where(x => !string.IsNullOrEmpty(x)));
        notch.Update(status, mode);
    }

    private static async Task ToggleAsync()
    {
        if (_ibox.Current is not { } eco)
        {
            OpenIbox();
            return;
        }

        var idle = eco.Status.State is "Disconnected" or "Faulted";
        await _ibox.CallAsync(idle ? AgentMethods.Connect : AgentMethods.Disconnect, idle ? new AgentConnectRequest() : null);
    }

    private static IEnumerable<MenuItem> MenuItems()
    {
        var items = new List<MenuItem>();
        MenuItem Item(string header, Action action, bool check = false)
        {
            var item = new MenuItem { Header = header, IsChecked = check };
            item.Click += (_, _) => action();
            return item;
        }

        items.Add(Item(T.S("Chat…"), OpenChat));
        if (_ibox.Current is { } eco)
        {
            var busy = eco.Status.State is not ("Disconnected" or "Faulted" or "Connected");
            if (busy)
            {
                items.Add(Item("Cancel connecting", () => _ = _ibox.CallAsync(AgentMethods.Disconnect, null)));
            }

            var servers = new MenuItem { Header = "Switch server" };
            servers.SubmenuOpened += async (_, _) =>
            {
                if (servers.Items.Count > 0)
                {
                    return;
                }

                foreach (var server in (await _ibox.ServersAsync()).Take(30))
                {
                    var id = server.Id;
                    servers.Items.Add(Item(server.Name + (server.LatencyMs is { } ms ? $"  ·  {ms:0} ms" : string.Empty), () => _ = _ibox.CallAsync(AgentMethods.Connect, new AgentConnectRequest(Server: id)), server.Active));
                }
            };
            servers.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
            servers.SubmenuOpened += (_, _) =>
            {
                if (servers.Items.Count > 1 && servers.Items[0] is MenuItem { IsEnabled: false } placeholder)
                {
                    servers.Items.Remove(placeholder);
                }
            };
            items.Add(servers);

            var traffic = new MenuItem { Header = "Traffic mode" };
            foreach (var mode in new[] { "normal", "eco", "gaming", "streaming", "work" })
            {
                traffic.Items.Add(Item(char.ToUpperInvariant(mode[0]) + mode[1..], () => _ = _ibox.CallAsync(AgentMethods.SetTraffic, new { mode }), eco.TrafficMode.StartsWith(mode, StringComparison.Ordinal)));
            }

            items.Add(traffic);
            if (eco.DualVpnName is { } dual)
            {
                items.Add(Item("Dual VPN · " + dual, () => _ = _ibox.CallAsync(AgentMethods.SetDualVpn, new { on = !eco.DualVpnOn }), eco.DualVpnOn));
            }
        }

        items.Add(Item(T.S("Settings…"), ShowPanel));
        return items;
    }

    private static void OpenIbox()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IBOX", "IBOX.exe");
        try
        {
            if (File.Exists(installed))
            {
                Process.Start(new ProcessStartInfo(installed) { UseShellExecute = false });
            }
            else
            {
                ShowPanel();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowPanel();
        }
    }

    // ------------------------------------------------------------------ plugins

    private static ImageSource? PluginIcon(PluginManifest manifest)
    {
        if (PluginIcons.TryGetValue(manifest.Id, out var known))
        {
            return known;
        }

        var icon = manifest.Icon is { } path ? LoadPicture(path) : null;
        PluginIcons[manifest.Id] = icon;
        return icon;
    }

    /// <summary>A small local .png / .ico (at most 512 KB), or null.</summary>
    private static ImageSource? LoadPicture(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 512 * 1024)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.DecodePixelWidth = 48;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static void OnPlugin(ApprovedPlugin plugin, PluginRequest r)
    {
        var id = plugin.Manifest.Id;
        switch (r.Op)
        {
            case "notify":
                Active()?.ShowAlert(new AiAlert("plugin", plugin.Manifest.Name, r.Title!, r.Body, PluginIcon(plugin.Manifest), null)
                {
                    Open = r.Link is { } link ? () => RunAction(id, link) : null,
                    Seconds = r.Priority == "high" ? 30 : r.Duration,
                });
                break;
            case "chip":
                Chips[id] = ((Color)ColorConverter.ConvertFromString(r.Color!), r.Value ?? string.Empty);
                break;
            case "panel":
                var buttons = (r.Buttons ?? []).Select(b => (b, plugin.Manifest.Actions.First(a => a.Id == b).Title)).ToList();
                Boards[id] = new PluginBoard(id, plugin.Manifest.Name, PluginIcon(plugin.Manifest), r.Widgets!, buttons);
                break;
            case "clear":
                Chips.Remove(id);
                Boards.Remove(id);
                break;
        }

        if (r.Op is "chip" or "panel" or "clear")
        {
            foreach (var n in Notches.Values)
            {
                n.SetChips(Chips.Values.ToList());
                n.SetBoards(Boards.Values.ToList());
            }
        }

        ArmTrim();
    }

    /// <summary>Runs an action exactly as the approved manifest declared it (an .exe with fixed arguments, or a web address).</summary>
    private static void RunAction(string pluginId, string actionId)
    {
        var plugin = _settings.Current.Plugins.FirstOrDefault(p => p.Manifest.Id == pluginId);
        var action = plugin?.Manifest.Actions.FirstOrDefault(a => a.Id == actionId);
        if (plugin is null || action is null || BarPlugins.Hash(plugin.Manifest) != plugin.Hash)
        {
            return;
        }

        try
        {
            if (action.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            {
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            }
            else if (action.Command is { } command && File.Exists(command))
            {
                var psi = new ProcessStartInfo(command) { UseShellExecute = false };
                foreach (var a in action.Args)
                {
                    psi.ArgumentList.Add(a);
                }

                Process.Start(psi);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // the plugin's program is gone
        }
    }

    // ------------------------------------------------------------------ windows, tray, autostart

    private static void ShowPanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Activate();
            return;
        }

        _panel = new ControlPanel(_settings, Monitors, () => System.Windows.Application.Current.Shutdown());
        _panel.Closed += (_, _) =>
        {
            _panel = null;
            ArmTrim();
        };
        _panel.Show();
        _panel.Activate();
    }

    private static void OpenChat()
    {
        if (_chat is { IsVisible: true })
        {
            _chat.Activate();
            return;
        }

        _chat = new ChatWindow(_settings);
        _chat.Closed += (_, _) =>
        {
            _chat = null;
            ArmTrim();
        };
        if (Active() is { Monitor: { } m } notch)
        {
            var scale = VisualTreeHelper.GetDpi(notch).DpiScaleX;
            _chat.PlaceUnder(new Rect(m.Left / scale, m.Top / scale, m.Width / scale, 40));
        }

        _chat.Show();
        _chat.Activate();
    }

    private static void TrayMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var (header, action) in new (string, Action)[] { (T.S("Settings…"), ShowPanel), (T.S("Chat…"), OpenChat), (T.S("Quit IBAR"), () => System.Windows.Application.Current.Shutdown()) })
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Starts with Windows for this user only (HKCU Run; no service, no administrator rights).</summary>
    private static void SyncAutostart(bool on)
    {
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (on && Environment.ProcessPath is { } exe && Environment.GetEnvironmentVariable("IBAR_DATA") is null)
            {
                run.SetValue("IBAR", $"\"{exe}\" --startup");
            }
            else if (!on)
            {
                run.DeleteValue("IBAR", throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // stays as it was
        }
    }

    /// <summary>
    /// After a quiet minute IBAR hands its idle memory back to Windows (the pages return when something happens).
    /// One shot, re-armed by activity — not a repeating timer.
    /// </summary>
    private static void ArmTrim()
    {
        if (Environment.GetEnvironmentVariable("IBAR_NO_TRIM") == "1")
        {
            return;
        }

        _trim ??= new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(60) };
        _trim.Tick -= Trim;
        _trim.Tick += Trim;
        _trim.Stop();
        _trim.Start();
    }

    private static void Trim(object? sender, EventArgs e)
    {
        _trim?.Stop();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var self = Process.GetCurrentProcess();
        _ = EmptyWorkingSet(self.Handle);
    }

    // ------------------------------------------------------------------ monitors

    public static IReadOnlyList<MonitorArea> Monitors()
    {
        var list = new List<MonitorArea>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var r = info.Monitor;
                list.Add(new MonitorArea(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, info.Device));
            }

            return true;
        }, 0);
        return list;
    }

    private delegate bool MonitorEnum(nint monitor, nint dc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // delegate and by-value string marshalling
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(nint process);
}
