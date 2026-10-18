using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.Views;

namespace JackiVpn.Bar;

/// <summary>
/// IBAR's control panel (tray icon): glass level and strength, which monitors have a notch, the shelf, Claude Code /
/// Codex, plugins (with removal), chat's work folder, start with Windows, full-screen hiding, language and panel theme.
/// Built in code, rebuilt when the language or theme changes.
/// </summary>
public sealed class ControlPanel : Window
{
    private readonly BarSettingsStore _settings;
    private readonly Func<IReadOnlyList<MonitorArea>> _monitors;
    private readonly Action _quit;
    private Brush _fg = Brushes.White;
    private Brush _muted = Brushes.Gray;
    private Brush _card = Brushes.Black;

    public ControlPanel(BarSettingsStore settings, Func<IReadOnlyList<MonitorArea>> monitors, Action quit)
    {
        _settings = settings;
        _monitors = monitors;
        _quit = quit;
        Width = 460;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        Build();
    }

    private void Build()
    {
        var s = _settings.Current;
        T.Persian = s.Language == "fa";
        Title = T.S("IBAR settings");
        FlowDirection = T.Persian ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Background = s.LightPanel ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)) : new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14));
        _fg = s.LightPanel ? new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)) : Brushes.White;
        _muted = s.LightPanel ? new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)) : new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        _card = s.LightPanel ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x15, 0x1A, 0x24));
        Foreground = _fg;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "IBAR", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = _fg, Margin = new Thickness(0, 0, 0, 12) });

        // Glass.
        var glass = Card(T.S("Glass"), T.S("Real glass bends what is behind the open notch, on the GPU, only when it changes."));
        var levels = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var (level, name) in new[] { (GlassLevel.Off, "Off"), (GlassLevel.Drawn, "Drawn"), (GlassLevel.Real, "Real") })
        {
            var radio = new RadioButton { Content = T.S(name), GroupName = "glass", IsChecked = s.Glass == level, Foreground = _fg, Margin = new Thickness(0, 0, 16, 0) };
            radio.Checked += (_, _) => _settings.Update(x => x with { Glass = level });
            levels.Children.Add(radio);
        }

        glass.Children.Add(levels);
        var slider = new Slider { Minimum = 0.1, Maximum = 1, Value = s.GlassStrength, SmallChange = 0.05, LargeChange = 0.1 };
        slider.ValueChanged += (_, e) => _settings.Update(x => x with { GlassStrength = Math.Round(e.NewValue, 2) });
        glass.Children.Add(Row(T.S("Strength"), slider));
        root.Children.Add(Wrap(glass));

        // Monitors.
        var monitors = Card(T.S("Monitors"), null);
        foreach (var m in _monitors())
        {
            var on = !s.HiddenMonitors.Contains(m.Key);
            monitors.Children.Add(Check($"{T.S("Notch on")} {m.Key.TrimStart('\\', '.')}  ({m.Width}×{m.Height})", on, v =>
                _settings.Update(x => x with { HiddenMonitors = v ? x.HiddenMonitors.Where(k => k != m.Key).ToList() : [.. x.HiddenMonitors, m.Key] })));
        }

        root.Children.Add(Wrap(monitors));

        // Shelf and assistants.
        var tools = Card(T.S("Shelf"), null);
        tools.Children.Add(Check(T.S("Keep dropped files, text and links on the notch"), s.Shelf, v => _settings.Update(x => x with { Shelf = v })));
        tools.Children.Add(new TextBlock { Text = T.S("AI assistants"), FontWeight = FontWeights.SemiBold, Foreground = _fg, Margin = new Thickness(0, 12, 0, 2) });
        tools.Children.Add(Check(T.S("Claude and ChatGPT notifications and requests on the notch"), s.AiAssistants, v => _settings.Update(x => x with { AiAssistants = v })));
        var hooks = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var note = new TextBlock { Foreground = _muted, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        hooks.Children.Add(Toggle(() => AgentHooksSetup.ClaudeConnected, "Claude Code", AgentHooksSetup.ConnectClaude, AgentHooksSetup.DisconnectClaude, note));
        hooks.Children.Add(Toggle(() => AgentHooksSetup.CodexConnected, "Codex", AgentHooksSetup.ConnectCodex, AgentHooksSetup.DisconnectCodex, note));
        tools.Children.Add(hooks);
        tools.Children.Add(note);
        root.Children.Add(Wrap(tools));

        // Tasks: downloads, copies, Adobe.
        var tasks = Card(T.S("Tasks on the notch"), T.S("One circle beside the notch shows how far a task is; click it for the others."));
        tasks.Children.Add(Check(T.S("Browser downloads and download managers"), s.WatchDownloads, v => _settings.Update(x => x with { WatchDownloads = v })));
        tasks.Children.Add(Check(T.S("Copying and moving files (Explorer)"), s.WatchCopies, v => _settings.Update(x => x with { WatchCopies = v })));
        tasks.Children.Add(Check(T.S("Adobe renders and exports"), s.WatchAdobe, v => _settings.Update(x => x with { WatchAdobe = v })));
        var registered = NativeHost.RegisteredManifest() is { } manifest && System.IO.File.Exists(manifest);
        tasks.Children.Add(new TextBlock { Text = T.S("Browser extension") + (registered ? "  ✓" : "  —"), FontWeight = FontWeights.SemiBold, Foreground = _fg, Margin = new Thickness(0, 12, 0, 2) });
        tasks.Children.Add(new TextBlock { Text = T.S("Exact sizes and speed from the browser itself. Load it once: the browser's extensions page opens with the folder to load."), Foreground = _muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var browsers = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (key, name) in new[] { ("chrome", "Chrome"), ("edge", "Edge"), ("opera", "Opera"), ("brave", "Brave"), ("firefox", "Firefox") })
        {
            if (Sources.TaskSources.ExeOf(key) is null)
            {
                continue;
            }

            var install = new Button { Content = T.S("Install extension") + " · " + name, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 6) };
            install.Click += (_, _) => NativeHost.OpenInstall(AppContext.BaseDirectory, key);
            browsers.Children.Add(install);
        }

        tasks.Children.Add(browsers);
        root.Children.Add(Wrap(tasks));

        // Sounds.
        var sounds = Card(T.S("Sounds"), T.S("Soft start, done and error sounds; silent while a full-screen app or game is in front."));
        sounds.Children.Add(Check(T.S("Play sounds"), s.Sounds, v => _settings.Update(x => x with { Sounds = v })));
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = s.SoundVolume, SmallChange = 0.05, LargeChange = 0.1 };
        volume.ValueChanged += (_, e) => _settings.Update(x => x with { SoundVolume = Math.Round(e.NewValue, 2) });
        sounds.Children.Add(Row(T.S("Volume"), volume));
        var try1 = new WrapPanel();
        foreach (var (kind, label) in new[] { (ChimeKind.Start, "Start"), (ChimeKind.Done, "Done"), (ChimeKind.Error, "Error") })
        {
            var play = new Button { Content = "▶ " + T.S(label), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 4, 8, 0) };
            play.Click += (_, _) => Chime.Play(kind);
            try1.Children.Add(play);
        }

        sounds.Children.Add(try1);
        root.Children.Add(Wrap(sounds));

        // Windows notifications from other apps.
        var notes = Card(T.S("Windows notifications"), T.S("Notifications of the apps you tick appear on the notch (title, text, icon; a click opens the app). Windows does not let another app press their buttons."));
        notes.Children.Add(Check(T.S("Show other apps' notifications on the notch"), s.WindowsNotifications, v =>
        {
            _settings.Update(x => x with { WindowsNotifications = v });
            Build();
        }));
        if (s.WindowsNotifications)
        {
            if (s.SeenApps.Count == 0)
            {
                notes.Children.Add(new TextBlock { Text = T.S("Apps appear here after they send a notification."), Foreground = _muted, FontSize = 11 });
            }

            foreach (var app in s.SeenApps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var id = app.Id;
                var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(Check(app.Name, s.NotifyApps.Contains(id, StringComparer.OrdinalIgnoreCase), v =>
                    _settings.Update(x => x with { NotifyApps = v ? [.. x.NotifyApps.Where(a => !string.Equals(a, id, StringComparison.OrdinalIgnoreCase)), id] : x.NotifyApps.Where(a => !string.Equals(a, id, StringComparison.OrdinalIgnoreCase)).ToList() })));
                var quiet = Check(T.S("hide Windows banner"), s.QuietBanners.Contains(id, StringComparer.OrdinalIgnoreCase), v =>
                    _settings.Update(x => x with { QuietBanners = v ? [.. x.QuietBanners.Where(a => !string.Equals(a, id, StringComparison.OrdinalIgnoreCase)), id] : x.QuietBanners.Where(a => !string.Equals(a, id, StringComparison.OrdinalIgnoreCase)).ToList() }));
                quiet.Margin = new Thickness(16, 4, 0, 4);
                row.Children.Add(quiet);
                notes.Children.Add(row);
            }

            notes.Children.Add(new TextBlock { Text = T.S("Hiding the banner writes Windows' own per-app switch (undone when unticked); Windows may apply it only after you sign in again."), Foreground = _muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }

        root.Children.Add(Wrap(notes));

        // Plugins.
        var plugins = Card(T.S("Plugins"), null);
        if (s.Plugins.Count == 0)
        {
            plugins.Children.Add(new TextBlock { Text = T.S("No plugins yet."), Foreground = _muted });
        }

        foreach (var p in s.Plugins)
        {
            var remove = new Button { Content = T.S("Remove"), Padding = new Thickness(10, 2, 10, 2) };
            remove.Click += (_, _) =>
            {
                _settings.Update(x => x with { Plugins = x.Plugins.Where(q => q.Manifest.Id != p.Manifest.Id).ToList() });
                Build();
            };
            var actions = string.Join(", ", p.Manifest.Actions.Select(a => a.Title));
            plugins.Children.Add(Row(p.Manifest.Name + (p.Manifest.Publisher is { } pub ? " — " + pub : string.Empty) + (actions.Length > 0 ? "  (" + actions + ")" : string.Empty), remove));
        }

        root.Children.Add(Wrap(plugins));

        // Chat.
        var chat = Card(T.S("Chat"), null);
        var folder = new TextBlock { Text = s.ChatFolder ?? "—", Foreground = _muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
        var choose = new Button { Content = T.S("Choose…"), Padding = new Thickness(10, 2, 10, 2) };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog(this) == true)
            {
                _settings.Update(x => x with { ChatFolder = dialog.FolderName });
                folder.Text = dialog.FolderName;
            }
        };
        chat.Children.Add(Row(T.S("Work folder"), choose));
        chat.Children.Add(folder);
        chat.Children.Add(Check(T.S("May propose changes (each asks Allow / Deny)"), s.ChatMayChange, v => _settings.Update(x => x with { ChatMayChange = v })));
        root.Children.Add(Wrap(chat));

        // General.
        var general = Card("IBAR", null);
        general.Children.Add(Check(T.S("Start with Windows"), s.StartWithWindows, v => _settings.Update(x => x with { StartWithWindows = v })));
        general.Children.Add(Check(T.S("Hide while a full-screen app is in front"), s.HideInFullScreen, v => _settings.Update(x => x with { HideInFullScreen = v })));
        general.Children.Add(Check(T.S("Light panel"), s.LightPanel, v =>
        {
            _settings.Update(x => x with { LightPanel = v });
            Build();
        }));
        var language = new ComboBox { Width = 120 };
        language.Items.Add("English");
        language.Items.Add("فارسی");
        language.SelectedIndex = s.Language == "fa" ? 1 : 0;
        language.SelectionChanged += (_, _) =>
        {
            _settings.Update(x => x with { Language = language.SelectedIndex == 1 ? "fa" : "en" });
            Build();
        };
        general.Children.Add(Row(T.S("Language"), language));
        var quit = new Button { Content = T.S("Quit IBAR"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        quit.Click += (_, _) => _quit();
        general.Children.Add(quit);
        root.Children.Add(Wrap(general));

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
    }

    private StackPanel Card(string title, string? hint)
    {
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = _fg });
        if (hint is not null)
        {
            card.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = _muted, TextWrapping = TextWrapping.Wrap });
        }

        return card;
    }

    private Border Wrap(StackPanel card) => new() { Background = _card, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = card };

    private DockPanel Row(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };
        DockPanel.SetDock(control, Dock.Right);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is Slider slider)
        {
            slider.Width = 200;
        }

        row.Children.Add(control);
        row.Children.Add(new TextBlock { Text = label, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    private CheckBox Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = new TextBlock { Text = label, Foreground = _fg, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    private Button Toggle(Func<bool> connected, string name, Func<string?> connect, Func<string?> disconnect, TextBlock note)
    {
        var button = new Button { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        void Label() => button.Content = T.S((connected() ? "Disconnect " : "Connect ") + name);
        Label();
        button.Click += (_, _) =>
        {
            var was = connected();
            note.Text = (was ? disconnect() : connect()) ?? (was ? name + " ✓ −" : name + " ✓" + (name == "Codex" ? " — Codex: /hooks → trust" : string.Empty));
            Label();
        };
        return button;
    }
}
