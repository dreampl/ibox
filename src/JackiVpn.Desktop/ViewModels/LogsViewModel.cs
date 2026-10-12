using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Desktop.Composition;
using JackiVpn.Infrastructure.Diagnostics;
using JackiVpn.Infrastructure.Logging;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Desktop.ViewModels;

public sealed record LogLine(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    public string LevelText => Level switch
    {
        LogLevel.Critical => "CRIT",
        LogLevel.Error => "ERR ",
        LogLevel.Warning => "WARN",
        LogLevel.Information => "INFO",
        LogLevel.Debug => "DBG ",
        _ => "TRC ",
    };

    public string Source => Category.StartsWith("JackiVpn.", StringComparison.Ordinal) ? Category[9..] : Category;
}

/// <summary>
/// Live log viewer over the in-memory ring buffer. Entries are already redacted by the logger; arrivals are
/// batched on a timer so log bursts never flood the UI thread.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 3000;
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;
    private readonly ConcurrentQueue<LogRecord> _incoming = new();
    private readonly DispatcherTimer _flush;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string _minimumLevel = "Information";

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string? _message;

    public LogsViewModel(AppServices services, IDialogService dialogs)
    {
        _s = services;
        _dialogs = dialogs;
        foreach (var r in services.LogBuffer.Snapshot().TakeLast(MaxLines))
        {
            Lines.Add(ToLine(r));
        }

        View = CollectionViewSource.GetDefaultView(Lines);
        View.Filter = o => o is LogLine l && Matches(l);
        // Batched every 300 ms, but only while lines are arriving: an idle log costs no timer ticks.
        _flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _flush.Tick += (_, _) =>
        {
            _flush.Stop();
            Interlocked.Exchange(ref _scheduled, 0);
            Flush();
        };
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        services.LogBuffer.Appended += (_, r) =>
        {
            _incoming.Enqueue(r);
            if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            {
                dispatcher.BeginInvoke(new Action(_flush.Start), DispatcherPriority.Background);
            }
        };
    }

    private int _scheduled;

    public event EventHandler? LinesAppended;

    public ObservableCollection<LogLine> Lines { get; } = [];

    public ICollectionView View { get; }

    public IReadOnlyList<string> Levels { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];

    partial void OnSearchChanged(string value) => View.Refresh();

    partial void OnMinimumLevelChanged(string value) => View.Refresh();

    private bool Matches(LogLine l) =>
        l.Level >= (Enum.TryParse<LogLevel>(MinimumLevel, out var min) ? min : LogLevel.Information)
        && (string.IsNullOrWhiteSpace(Search) || l.Message.Contains(Search, StringComparison.OrdinalIgnoreCase) || l.Category.Contains(Search, StringComparison.OrdinalIgnoreCase));

    private static LogLine ToLine(LogRecord r) => new(r.TimestampUtc, r.Level, r.Category, r.Message);

    private void Flush()
    {
        var added = false;
        while (_incoming.TryDequeue(out var r))
        {
            Lines.Add(ToLine(r));
            added = true;
        }

        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }

        if (added)
        {
            LinesAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void Clear() => Lines.Clear();

    [RelayCommand]
    private void OpenFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_s.Paths.UserLogsDir}\"") { UseShellExecute = false });

    [RelayCommand]
    private void CopyVisible()
    {
        var text = string.Join(Environment.NewLine, View.Cast<LogLine>().Select(l => $"{l.TimeText} {l.LevelText} [{l.Source}] {l.Message}"));
        System.Windows.Clipboard.SetText(text);
        Message = "Visible log lines copied (already redacted).";
    }

    /// <summary>
    /// Support bundle: logs (desktop + service if readable), settings, core versions and the journal — all
    /// redacted and verified; the export aborts rather than writing anything that still looks secret.
    /// </summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = _dialogs.SaveFile("ZIP archive|*.zip", $"jackivpn-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (path is null)
        {
            return;
        }

        try
        {
            var secrets = new List<string>();
            foreach (var sub in await _s.Subscriptions.GetAllAsync(_s.Lifetime))
            {
                if (await _s.Subscriptions.GetSourceUrlAsync(sub.Id, _s.Lifetime) is { } url)
                {
                    secrets.Add(url);
                }
            }

            var versions = new Dictionary<string, string>();
            foreach (var kind in new[] { Domain.Connection.CoreKind.SingBox, Domain.Connection.CoreKind.Xray })
            {
                versions[kind.ToString()] = _s.Catalog.ManifestEntry(kind)?.Version ?? "not installed";
            }

            var settings = _s.Settings.Current;
            var result = await Task.Run(() => DiagnosticBundleExporter.Export(new DiagnosticInputs
            {
                LogsDirectory = _s.Paths.UserLogsDir,
                ServiceLogsDirectory = _s.Paths.ServiceLogsDir,
                Settings = settings,
                CoreVersions = versions,
                JournalPath = _s.Paths.UserJournalPath,
                KnownSecrets = secrets,
                ExtraNotes = [$"App {typeof(LogsViewModel).Assembly.GetName().Version}", $"OS {Environment.OSVersion}", $"Connection state {_s.Orchestrator.State}"],
            }, path));
            Message = $"Diagnostics exported ({result.Files} files, redaction verified): {Path.GetFileName(result.Path)}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = "Export aborted: " + SecretRedactor.Redact(ex.Message);
        }
    }
}
