using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JackiVpn.Desktop.Services;
using JackiVpn.Desktop.Views;
using JackiVpn.Domain.Agent;

namespace JackiVpn.Bar;

/// <summary>
/// Chat from the notch, drawn like the notch itself: black, hanging from the top edge, rounded below, opening with a
/// spring, simple bubbles. A question goes to the user's own Claude Code (<c>claude -p</c>, its answer streamed as it
/// is written) or Codex (<c>codex exec</c>), in one fixed work folder. Read-only by default (Claude plan mode, Codex
/// read-only sandbox); with "may propose changes" every change still has to pass Allow / Deny on the notch (IBAR's
/// hooks). The question is written to the tool's input, never into a command line, and chat can only be typed here
/// (nothing on any pipe can start it).
/// </summary>
public sealed class ChatWindow : Window
{
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0, 0, 0)));
    private static readonly Brush UserBubble = Frozen(new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x26)));
    private static readonly Brush Field = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)));
    private static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
    private static readonly Brush Warn = Frozen(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0x9B, 0xFF)));

    private readonly BarSettingsStore _settings;
    private readonly StackPanel _talk = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new() { FontSize = 13.5, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 110, Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), CaretBrush = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 6, 2, 6) };
    private readonly TextBlock _sendGlyph = new() { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "", FontSize = 13, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _send;
    private readonly TextBlock _mode = new() { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _where = new() { FontSize = 10.5, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 8, 6, 0) };
    private readonly Border _claudeTab;
    private readonly Border _codexTab;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 0, 2), Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _scale = new(0.55, 0.12);
    private Process? _running;
    private string _agent;
    private bool _closing;

    public ChatWindow(BarSettingsStore settings)
    {
        _settings = settings;
        _agent = settings.Current.ChatAgent == "codex" ? "codex" : "claude";
        Title = "IBAR " + T.S("Chat");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        FlowDirection = T.Persian ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        _claudeTab = Tab("Claude Code", "claude");
        _codexTab = Tab("Codex", "codex");
        var tabs = new Border { Background = Field, CornerRadius = new CornerRadius(14), Padding = new Thickness(3), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _claudeTab, _codexTab } } };
        var close = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "", FontSize = 11, Foreground = Muted, Margin = new Thickness(12, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        close.MouseLeftButtonUp += (_, _) => CloseSoftly();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_mode, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(_mode);
        head.Children.Add(tabs);

        foreach (var i in Enumerable.Range(0, 3))
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Brushes.White, Margin = new Thickness(0, 0, 5, 0), Opacity = 0.25 };
            _dots.Children.Add(dot);
        }

        _talk.Children.Add(Note(T.S("Ask about the files in the work folder. Read-only unless changes are allowed in the settings.")));
        _scroll = new ScrollViewer { Content = new StackPanel { Children = { _talk, _dots } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };

        _send = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brushes.White, Child = _sendGlyph, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 3) };
        _send.MouseLeftButtonUp += (_, _) => _ = SendAsync();
        var field = new DockPanel();
        DockPanel.SetDock(_send, Dock.Right);
        field.Children.Add(_send);
        field.Children.Add(_input);
        var inputBox = new Border { Background = Field, CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 2, 4, 2), Margin = new Thickness(0, 10, 0, 0), Child = field };
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = SendAsync();
            }
            else if (e.Key == Key.Escape)
            {
                CloseSoftly();
            }
        };

        var stack = new StackPanel { Children = { head, _scroll, inputBox, _where } };

        // The island's black, rounded below (continuous-looking corners at this size), a drawn glass sheen at the
        // bottom; no live effects.
        var sheen = new Border
        {
            CornerRadius = new CornerRadius(0, 0, 30, 30),
            IsHitTestVisible = false,
            Background = Frozen(new LinearGradientBrush([new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6), new GradientStop(Color.FromArgb(0x10, 255, 255, 255), 1)], new Point(0, 0), new Point(0, 1))),
        };
        var body = new Grid
        {
            Children =
            {
                new Border { Background = Ink, CornerRadius = new CornerRadius(0, 0, 30, 30), Padding = new Thickness(20, 14, 20, 14), Child = stack },
                sheen,
            },
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = _scale,
            Opacity = 0,
        };
        Content = body;
        Loaded += (_, _) =>
        {
            Refresh();
            Open(body);
            _input.Focus();
            if (Environment.GetEnvironmentVariable("IBAR_TEST_CHAT") is { Length: > 0 } question && Environment.GetEnvironmentVariable("IBAR_DATA") is not null)
            {
                // Test copies: ask without typing.
                _input.Text = question;
                _ = SendAsync();
            }
        };
        Closed += (_, _) => Stop();
    }

    /// <summary>Under the notch of the monitor at the pointer, joined to the top edge like the island.</summary>
    public void PlaceUnder(Rect notchArea)
    {
        Left = notchArea.Left + ((notchArea.Width - Width) / 2);
        Top = notchArea.Top;
    }

    // ------------------------------------------------------------------ look

    private Border Tab(string text, string agent)
    {
        var tab = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(12, 4, 12, 4), Cursor = Cursors.Hand, Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold } };
        tab.MouseLeftButtonUp += (_, _) =>
        {
            if (_running is null)
            {
                _agent = agent;
                _settings.Update(s => s with { ChatAgent = agent });
                Refresh();
            }
        };
        return tab;
    }

    private void Refresh()
    {
        foreach (var (tab, agent) in new[] { (_claudeTab, "claude"), (_codexTab, "codex") })
        {
            var on = _agent == agent;
            tab.Background = on ? Brushes.White : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = on ? Brushes.Black : Muted;
        }

        var mayChange = MayChange;
        _mode.Text = mayChange ? "Allow / Deny" : T.S("Read-only (plan)");
        _mode.Foreground = mayChange ? Warn : Muted;
        _where.Text = "\U0001F4C1  " + Folder;
    }

    /// <summary>The spring the island opens with: from a sliver at the top edge, a little past full size, settling.</summary>
    private void Open(FrameworkElement body)
    {
        if (Motion.Reduced)
        {
            _scale.ScaleX = _scale.ScaleY = 1;
            body.Opacity = 1;
            return;
        }

        var spring = new ElasticEase { Oscillations = 1, Springiness = 7, EasingMode = EasingMode.EaseOut };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = spring });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.12, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = spring });
        body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void CloseSoftly()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (Motion.Reduced || Content is not FrameworkElement body)
        {
            Close();
            return;
        }


        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.6, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Close();
        body.BeginAnimation(OpacityProperty, fade);
    }

    private static TextBlock Note(string text, Brush? brush = null) => new() { Text = text, FontSize = 11.5, Foreground = brush ?? Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 8) };

    private void AddUser(string text)
    {
        var bubble = new Border
        {
            Background = UserBubble,
            CornerRadius = new CornerRadius(18, 18, 6, 18),
            Padding = new Thickness(13, 8, 13, 8),
            Margin = new Thickness(80, 4, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13.5, TextWrapping = TextWrapping.Wrap },
        };
        Pop(bubble);
        _talk.Children.Add(bubble);
        _scroll.ScrollToEnd();
    }

    private TextBlock AddAnswer()
    {
        var text = new TextBlock { Foreground = Brushes.White, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 30, 8), LineHeight = 20 };
        Pop(text);
        _talk.Children.Add(text);
        return text;
    }

    private static void Pop(FrameworkElement e)
    {
        if (Motion.Reduced)
        {
            return;
        }

        var shift = new TranslateTransform(0, 8);
        e.RenderTransform = shift;
        e.Opacity = 0;
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>Three dots breathing while the assistant works (stopped as soon as it answers).</summary>
    private void Thinking(bool on)
    {
        _dots.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _dots.Children.Count; i++)
        {
            var dot = (System.Windows.Shapes.Ellipse)_dots.Children[i];
            dot.BeginAnimation(OpacityProperty, on && !Motion.Reduced
                ? new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(i * 140), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }

        _sendGlyph.Text = on ? "" : "";
    }

    // ------------------------------------------------------------------ talking

    private bool MayChange => _settings.Current.ChatMayChange && (_agent == "claude" || AgentHooksSetup.CodexConnected);

    private string Folder => _settings.Current.ChatFolder is { Length: > 0 } f && Directory.Exists(f)
        ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "IBAR work");

    private async Task SendAsync()
    {
        if (_running is not null)
        {
            Stop();
            return;
        }

        var question = _input.Text.Trim();
        if (question.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        var exe = _agent == "codex" ? FindCodex() : FindClaude();
        AddUser(question);
        _input.Clear();
        if (exe is null)
        {
            _talk.Children.Add(Note((_agent == "codex" ? "Codex" : "Claude Code") + ": " + T.S("Not found on this computer"), Warn));
            return;
        }

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        IReadOnlyList<string> args = _agent == "codex" ? ChatCli.CodexArguments(MayChange) : ChatCli.ClaudeArguments(MayChange, HooksForThisRun());
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm installs a .cmd shim; the fixed arguments go through cmd, the question never does.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            foreach (var a in new[] { "/d", "/s", "/c", exe }.Concat(args))
            {
                psi.ArgumentList.Add(a);
            }
        }
        else
        {
            psi.FileName = exe;
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
        }

        var answer = AddAnswer();
        var sawDelta = false;
        var login = false;
        var errors = new StringBuilder();
        Thinking(true);
        try
        {
            _running = Process.Start(psi);
            if (_running is null)
            {
                return;
            }

            await _running.StandardInput.WriteAsync(question);
            _running.StandardInput.Close();
            var stderr = Task.Run(async () =>
            {
                while (await _running.StandardError.ReadLineAsync() is { } line)
                {
                    lock (errors)
                    {
                        if (errors.Length < 4000)
                        {
                            errors.AppendLine(line);
                        }
                    }
                }
            });
            while (await _running.StandardOutput.ReadLineAsync() is { } line)
            {
                if (_agent == "codex")
                {
                    Thinking(false);
                    answer.Text += (answer.Text.Length > 0 ? "\n" : string.Empty) + line;
                }
                else if (ChatCli.Read(line) is { } piece)
                {
                    switch (piece.Kind)
                    {
                        case "delta":
                            Thinking(false);
                            sawDelta = true;
                            answer.Text += piece.Text;
                            break;
                        case "message" when !sawDelta && piece.Text is { Length: > 0 } whole:
                            Thinking(false);
                            answer.Text += whole;
                            break;
                        case "result" when piece.Error:
                            login |= ChatCli.NeedsLogin(piece.Text);
                            if (!login)
                            {
                                _talk.Children.Add(Note(piece.Text ?? "Error", Warn));
                            }

                            break;
                        case "text":
                            login |= ChatCli.NeedsLogin(piece.Text);
                            if (!login)
                            {
                                answer.Text += piece.Text + "\n";
                            }

                            break;
                    }
                }

                _scroll.ScrollToEnd();
            }

            await Task.WhenAll(stderr, _running.WaitForExitAsync());
            if (_running.ExitCode != 0 && !login && answer.Text.Length == 0)
            {
                string tail;
                lock (errors)
                {
                    tail = string.Join("\n", errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
                }

                login = ChatCli.NeedsLogin(tail);
                if (!login)
                {
                    _talk.Children.Add(Note(tail.Length > 0 ? tail : "Exit code " + _running.ExitCode, Warn));
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _talk.Children.Add(Note(ex.Message, Warn));
        }
        finally
        {
            _running?.Dispose();
            _running = null;
            Thinking(false);
            if (answer.Text.Length == 0)
            {
                _talk.Children.Remove(answer);
            }

            if (login)
            {
                ShowSignIn(exe);
            }

            _scroll.ScrollToEnd();
        }
    }

    /// <summary>Not signed in on its own: a button opens Claude Code in a console to sign in once (/login).</summary>
    private void ShowSignIn(string exe)
    {
        var button = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Child = new TextBlock { Text = T.S("Sign in"), Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold, FontSize = 12.5 } };
        button.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Folder, Arguments = "/login" });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // nothing to open
            }
        };
        var row = new StackPanel { Margin = new Thickness(4, 0, 4, 8) };
        row.Children.Add(Note(T.S("Claude Code on this computer is signed in only inside the Claude app. Sign in once for chat on the notch:"), Warn));
        row.Children.Add(button);
        _talk.Children.Add(row);
    }

    /// <summary>IBAR's hooks for this run only, when Claude Code is not connected to the notch in its own settings.</summary>
    private string? HooksForThisRun()
    {
        if (!MayChange || AgentHooksSetup.ClaudeConnected || AgentHooksSetup.CtlPath is not { } ctl)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(_settings.Folder, "chat-hooks.json");
            File.WriteAllText(path, AgentHookConfig.ClaudeInstall(null, ctl));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Stop()
    {
        try
        {
            if (_running is { HasExited: false } p)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    // ------------------------------------------------------------------ where the tools are

    /// <summary>Claude Code: the PATH, its own installers' places, then the copy inside the Claude desktop app.</summary>
    public static string? FindClaude()
    {
        if (OnPath("claude") is { } onPath)
        {
            return onPath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var known in new[] { Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(home, ".claude", "local", "claude.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd") })
        {
            if (File.Exists(known))
            {
                return known;
            }
        }

        var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code") };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            if (Directory.Exists(packages))
            {
                roots.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude-code")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no packages
        }

        var copies = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                copies.AddRange(Directory.EnumerateFiles(root, "claude.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable
            }
        }

        return ChatCli.Newest(copies);
    }

    /// <summary>Codex: the PATH, npm's shim, then the ChatGPT / Codex app's own copy (the newest build).</summary>
    public static string? FindCodex()
    {
        if (OnPath("codex") is { } onPath)
        {
            return onPath;
        }

        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");
        if (File.Exists(npm))
        {
            return npm;
        }

        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        try
        {
            return Directory.Exists(bin)
                ? Directory.EnumerateFiles(bin, "codex.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true }).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A tool on the PATH (an .exe first, then npm's .cmd).</summary>
    public static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                try
                {
                    var path = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
        }

        return null;
    }

    private static T Frozen<T>(T f)
        where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
