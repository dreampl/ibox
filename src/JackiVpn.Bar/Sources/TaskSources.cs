using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using JackiVpn.Domain.Activity;

namespace JackiVpn.Bar.Sources;

/// <summary>
/// Everything that reports tasks to the bus without being asked: browser downloads seen as growing temporary files,
/// Explorer's copy dialog, a download manager's progress windows, Adobe apps' progress windows and Media Encoder's
/// log. Event-driven throughout (window events, file-system notifications); the only timer samples a growing file's
/// size once a second while such a file exists. Browser downloads with exact byte counts come through the extension
/// (<see cref="PluginHost"/> bridges), and then the temporary-file guess for that browser stands down.
/// </summary>
public sealed partial class TaskSources
{
    private readonly ActivityBus _bus;
    private readonly BarSettingsStore _settings;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly List<FileSystemWatcher> _folders = [];
    private readonly Dictionary<string, Growing> _growing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, Tracked> _windows = [];
    private DispatcherTimer? _sample;
    private FileSystemWatcher? _ameLog;
    private bool _events;
    private BarSettings? _now;

    public TaskSources(ActivityBus bus, BarSettingsStore settings)
    {
        _bus = bus;
        _settings = settings;
    }

    /// <summary>Browsers whose extension is connected (their exact numbers replace the file-size guess).</summary>
    public HashSet<string> Bridged { get; } = new(StringComparer.Ordinal);

    public void Sync(BarSettings s)
    {
        _now = s;
        SyncFolders(s.WatchDownloads);
        SyncAmeLog(s.WatchAdobe);
        var events = s.WatchDownloads || s.WatchCopies || s.WatchAdobe;
        if (events && !_events)
        {
            WindowEvents.Start();
            WindowEvents.Shown += OnShown;
            WindowEvents.Gone += OnGone;
            WindowEvents.Changed += OnChanged;
            _events = true;
        }
        else if (!events && _events)
        {
            WindowEvents.Shown -= OnShown;
            WindowEvents.Gone -= OnGone;
            WindowEvents.Changed -= OnChanged;
            WindowEvents.Stop();
            _events = false;
        }
    }

    // ------------------------------------------------------------------ apps' own pictures and windows

    private static readonly Dictionary<string, string> Exes = new(StringComparer.Ordinal)
    {
        ["chrome"] = @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
        ["edge"] = @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
        ["firefox"] = @"%ProgramFiles%\Mozilla Firefox\firefox.exe",
        ["opera"] = @"%LOCALAPPDATA%\Programs\Opera\opera.exe",
        ["brave"] = @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
        ["idm"] = @"%ProgramFiles(x86)%\Internet Download Manager\IDMan.exe",
        ["explorer"] = @"%WINDIR%\explorer.exe",
    };

    private static readonly Dictionary<string, string> RunningExe = new(StringComparer.Ordinal);

    /// <summary>The program behind a source, for its icon and to bring it forward.</summary>
    public static string? ExeOf(string source)
    {
        if (RunningExe.TryGetValue(source, out var running) && File.Exists(running))
        {
            return running;
        }

        return Exes.TryGetValue(source, out var path) && Environment.ExpandEnvironmentVariables(path) is { } full && File.Exists(full) ? full : null;
    }

    /// <summary>Brings the source's main window forward.</summary>
    public static void Focus(string source)
    {
        if (ExeOf(source) is not { } exe)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(exe);
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (p.MainWindowHandle != 0)
                {
                    WindowEvents.Focus(p.MainWindowHandle);
                    return;
                }
            }
        }
    }

    public static string DownloadsFolder()
    {
        if (Environment.GetEnvironmentVariable("IBAR_DOWNLOADS") is { Length: > 0 } test && Environment.GetEnvironmentVariable("IBAR_DATA") is not null && Directory.Exists(test))
        {
            // Test copies watch a test folder, never the user's own.
            return test;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string value && Environment.ExpandEnvironmentVariables(value) is { } path && Directory.Exists(path))
            {
                return path;
            }
        }
        catch (System.Security.SecurityException)
        {
            // the usual place
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // ------------------------------------------------------------------ windows: Explorer copies, IDM, Adobe

    /// <summary>A window followed by its title (and progress bar values).</summary>
    private sealed record Tracked(string Source, string Id, uint Pid, string Kind, string Name)
    {
        public double? Last { get; set; }
    }

    private static readonly Regex Percent = new(@"(?<!\d)(\d{1,3}(?:[.,]\d+)?)\s?%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? AdobeOf(string process) => process switch
    {
        "afterfx" => "aftereffects",
        "adobe premiere pro" => "premiere",
        "adobe media encoder" => "mediaencoder",
        "photoshop" => "photoshop",
        "illustrator" => "illustrator",
        _ => null,
    };

    private void OnShown(nint hwnd, uint pid)
    {
        var s = _now;
        if (s is null || _windows.ContainsKey(hwnd))
        {
            return;
        }

        var process = WindowEvents.ProcessName(pid);
        var cls = WindowEvents.ClassName(hwnd);
        Tracked? t = null;
        if (s.WatchCopies && cls == "OperationStatusWindow")
        {
            t = new Tracked("explorer", "op-" + hwnd.ToString("x", CultureInfo.InvariantCulture), pid, "copy", "Files");
        }
        else if (s.WatchDownloads && process == "idman" && cls == "#32770")
        {
            t = new Tracked("idm", "dl-" + hwnd.ToString("x", CultureInfo.InvariantCulture), pid, "download", "IDM");
        }
        else if (s.WatchAdobe && AdobeOf(process) is { } adobe && !string.IsNullOrEmpty(WindowEvents.Title(hwnd)))
        {
            // Adobe apps: a window of theirs that shows a percentage (a progress or render dialog).
            t = new Tracked(adobe, "w-" + hwnd.ToString("x", CultureInfo.InvariantCulture), pid, adobe is "aftereffects" or "premiere" or "mediaencoder" ? "render" : "export", AdobeName(adobe));
            if (WindowEvents.ProcessPath(pid) is { } path)
            {
                RunningExe[adobe] = path;
            }
        }

        if (t is null)
        {
            return;
        }

        _windows[hwnd] = t;
        WindowEvents.Watch(pid);
        Read(hwnd, t);
    }

    private void OnChanged(nint hwnd, uint pid)
    {
        if (_windows.TryGetValue(hwnd, out var t))
        {
            Read(hwnd, t);
            return;
        }

        // A progress bar inside a followed window changed its value.
        foreach (var (top, tracked) in _windows)
        {
            if (tracked.Pid == pid && IsProgressBar(hwnd) && IsChildOf(hwnd, top))
            {
                ReadBar(hwnd, tracked);
                return;
            }
        }
    }

    private void OnGone(nint hwnd, uint pid)
    {
        if (!_windows.Remove(hwnd, out var t))
        {
            return;
        }

        WindowEvents.Unwatch(pid);
        if (t.Last is >= 0.99 || t.Source == "explorer")
        {
            // The dialog closed: done (Explorer closes it when the copy ends; a cancel cannot be told apart).
            _bus.End(t.Source, t.Id, ok: true);
        }
        else if (t.Last is not null)
        {
            _bus.End(t.Source, t.Id, ok: true, detail: "closed");
        }
        else
        {
            _bus.Remove(t.Source, t.Id);
        }
    }

    private void Read(nint hwnd, Tracked t)
    {
        var title = WindowEvents.Title(hwnd);
        var m = Percent.Match(title);
        if (!m.Success)
        {
            return;
        }

        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) || pct > 100)
        {
            return;
        }

        t.Last = pct / 100;
        var name = title.Remove(m.Index, m.Length).Replace(" complete", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(' ', '-', '–', '·', ':');
        _bus.Report(new ActivityItem
        {
            Id = t.Id,
            Source = t.Source,
            Kind = t.Kind,
            Title = t.Source == "explorer" ? "Copying files" : name.Length > 0 ? name : t.Name,
            Detail = t.Source == "explorer" ? null : t.Name,
            Progress = t.Last,
            // The app's own figure, read from its window title.
            Accuracy = ProgressAccuracy.Good,
            State = ActivityState.Running,
            Icon = WindowEvents.ProcessPath(t.Pid),
        });
    }

    private void ReadBar(nint bar, Tracked t)
    {
        var range = (int)SendMessage(bar, 0x0407 /* PBM_GETRANGE */, 0, 0);
        var low = (int)SendMessage(bar, 0x0407, 1, 0);
        var pos = (int)SendMessage(bar, 0x0408 /* PBM_GETPOS */, 0, 0);
        if (range <= low)
        {
            return;
        }

        t.Last = Math.Clamp((pos - low) / (double)(range - low), 0, 1);
        _bus.Report(new ActivityItem
        {
            Id = t.Id,
            Source = t.Source,
            Kind = t.Kind,
            Title = WindowEvents.Title(WindowEvents.Root(bar)) is { Length: > 0 } title ? title : t.Name,
            Detail = t.Name,
            Progress = t.Last,
            Accuracy = ProgressAccuracy.Good,
            State = ActivityState.Running,
            Icon = WindowEvents.ProcessPath(t.Pid),
        });
    }

    private static string AdobeName(string source) => source switch
    {
        "aftereffects" => "After Effects",
        "premiere" => "Premiere Pro",
        "mediaencoder" => "Media Encoder",
        "photoshop" => "Photoshop",
        _ => "Illustrator",
    };

    // ------------------------------------------------------------------ Media Encoder's log (every finished job)

    private void SyncAmeLog(bool on)
    {
        if (!on)
        {
            _ameLog?.Dispose();
            _ameLog = null;
            return;
        }

        if (_ameLog is not null)
        {
            return;
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Adobe", "Adobe Media Encoder");
        if (!Directory.Exists(root))
        {
            return;
        }

        _ameLog = new FileSystemWatcher(root, "AMEEncodingLog.txt") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
        _ameLog.Changed += (_, e) => _ui.BeginInvoke(() => OnAmeLog(e.FullPath));
        _ameLog.EnableRaisingEvents = true;
    }

    private readonly Dictionary<string, long> _ameRead = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The new lines of the log: each finished job is a task that ends (done or failed) — never its content.</summary>
    private void OnAmeLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var from = _ameRead.TryGetValue(path, out var known) && known <= stream.Length ? known : Math.Max(0, stream.Length - 4096);
            stream.Position = from;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            _ameRead[path] = stream.Length;
            foreach (var block in AmeLog.Jobs(text))
            {
                var id = "ame-" + Math.Abs(StringComparer.Ordinal.GetHashCode(block.Output + block.Ok)).ToString(CultureInfo.InvariantCulture);
                _bus.Report(new ActivityItem
                {
                    Id = id,
                    Source = "mediaencoder",
                    Kind = "render",
                    Title = block.Output,
                    Detail = "Media Encoder",
                    State = block.Ok ? ActivityState.Done : ActivityState.Failed,
                    Progress = block.Ok ? 1 : null,
                    Accuracy = block.Ok ? ProgressAccuracy.Exact : ProgressAccuracy.Unknown,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // written right now; the next change reads it
        }
    }

    // ------------------------------------------------------------------ growing temporary files (no extension)

    private sealed class Growing(string id, string source, string name, string path)
    {
        public string Id { get; } = id;

        public string Source { get; } = source;

        public string Name { get; } = name;

        public string Path { get; } = path;

        public RateMeter Meter { get; } = new(TimeSpan.FromSeconds(4));

        public long Size { get; set; }
    }

    private void SyncFolders(bool on)
    {
        foreach (var w in _folders)
        {
            w.Dispose();
        }

        _folders.Clear();
        if (!on)
        {
            return;
        }

        var folder = DownloadsFolder();
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var pattern in new[] { "*.crdownload", "*.part", "*.opdownload", "*.download" })
        {
            var w = new FileSystemWatcher(folder, pattern) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
            w.Created += (_, e) => _ui.BeginInvoke(() => Begin(e.FullPath));
            w.Changed += (_, e) => _ui.BeginInvoke(() => Begin(e.FullPath));
            w.Renamed += (_, e) => _ui.BeginInvoke(() => Finish(e.OldFullPath, e.FullPath));
            w.Deleted += (_, e) => _ui.BeginInvoke(() => Finish(e.FullPath, null));
            w.EnableRaisingEvents = true;
            _folders.Add(w);
        }
    }

    /// <summary>Which browser leaves a temporary file of this kind (the running one, if only one of that family runs).</summary>
    public static string BrowserFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".part")
        {
            return "firefox";
        }

        if (ext == ".opdownload")
        {
            return "opera";
        }

        var running = new[] { ("chrome", "chrome"), ("msedge", "edge"), ("opera", "opera"), ("brave", "brave") }
            .Where(b => Process.GetProcessesByName(b.Item1) is { Length: > 0 } list && list.All(p => { p.Dispose(); return true; }))
            .Select(b => b.Item2)
            .ToList();
        return running.Count == 1 ? running[0] : "chromium";
    }

    private void Begin(string path)
    {
        if (_growing.ContainsKey(path))
        {
            Sample();
            return;
        }

        var browser = BrowserFor(path);
        if (Bridged.Contains(browser) || (browser == "chromium" && Bridged.Overlaps(["chrome", "edge", "opera", "brave"])))
        {
            // The extension reports this download with exact numbers.
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("Unconfirmed ", StringComparison.Ordinal))
        {
            name = "Download";
        }

        var g = new Growing("tmp-" + Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(path)).ToString(CultureInfo.InvariantCulture), browser, name, path);
        _growing[path] = g;
        Sample();
        if (_sample is null)
        {
            // Only while a temporary file grows: one look a second at its size.
            _sample = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _sample.Tick += (_, _) => Sample();
            _sample.Start();
        }
    }

    private void Sample()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var g in _growing.Values.ToList())
        {
            long size;
            try
            {
                size = new FileInfo(g.Path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            g.Size = size;
            var rate = g.Meter.Add(now, size);
            _bus.Report(new ActivityItem
            {
                Id = g.Id,
                Source = g.Source,
                Kind = "download",
                Title = g.Name,
                // No total from a file on disk: the size so far and the speed, never a percentage.
                Done = size,
                Rate = rate,
                Detail = BrowserName(g.Source),
                Accuracy = ProgressAccuracy.Unknown,
                State = ActivityState.Running,
            });
        }
    }

    private void Finish(string temp, string? final)
    {
        if (!_growing.Remove(temp, out var g))
        {
            return;
        }

        if (final is not null && !IsTemp(final))
        {
            long size = 0;
            try
            {
                size = new FileInfo(final).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                size = g.Size;
            }

            _bus.Report(new ActivityItem
            {
                Id = g.Id,
                Source = g.Source,
                Kind = "download",
                Title = Path.GetFileName(final),
                Total = size > 0 ? size : null,
                Done = size > 0 ? size : null,
                Detail = BrowserName(g.Source),
                State = ActivityState.Done,
                Progress = 1,
                Accuracy = ProgressAccuracy.Exact,
            });
        }
        else if (final is not null)
        {
            // Renamed to another temporary name ("Unconfirmed 123.crdownload" → "name.crdownload"): keep following.
            _ui.BeginInvoke(() => Begin(final));
            _bus.Remove(g.Source, g.Id);
        }
        else
        {
            // Deleted: cancelled (or moved away by the browser right before the rename was seen).
            _bus.Remove(g.Source, g.Id);
        }

        if (_growing.Count == 0)
        {
            _sample?.Stop();
            _sample = null;
        }
    }

    private static bool IsTemp(string path) => Path.GetExtension(path).ToLowerInvariant() is ".crdownload" or ".part" or ".opdownload" or ".download" or ".tmp";

    public static string BrowserName(string source) => source switch
    {
        "chrome" => "Chrome",
        "edge" => "Edge",
        "firefox" => "Firefox",
        "opera" => "Opera",
        "brave" => "Brave",
        "vivaldi" => "Vivaldi",
        _ => "Browser",
    };

    // ------------------------------------------------------------------ plumbing

    private static bool IsProgressBar(nint hwnd) => WindowEvents.ClassName(hwnd) == "msctls_progress32";

    private static bool IsChildOf(nint hwnd, nint top) => WindowEvents.Root(hwnd) == top;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
