using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JackiVpn.Domain.Agent;
using Microsoft.Extensions.Logging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace JackiVpn.Desktop.Services;

/// <summary>A notification for the notch from an AI assistant.</summary>
public sealed record AiAlert(string Source, string App, string Title, string? Body, ImageSource? Icon, string? AppId)
{
    /// <summary>What a click does instead of opening the app (a plugin's declared action).</summary>
    public Action? Open { get; init; }

    /// <summary>Seconds on screen (null = the usual).</summary>
    public double? Seconds { get; init; }
}

/// <summary>
/// The AI assistants on the notch. Claude and ChatGPT (and Codex) desktop notifications are read from Windows'
/// notification list — only those apps' own, nothing else is looked at — and shown briefly on the notch. Claude Code
/// and Codex hooks reach IBOX over a pipe only this OS user can open: their permission requests wait for Allow / Deny
/// on the notch. Nothing is logged of what they say.
/// </summary>
public sealed class AiAssistants : IAsyncDisposable
{
    private const int Listeners = 4;
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _appIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _running;
    private long _lastClaudeHookTicks;
    private readonly SemaphoreSlim _toastShown = new(0, 1);
    private bool _signalled;

    /// <summary>Waits for a notification window, then lets its text arrive in the list.</summary>
    private async Task<bool> NextToastAsync(TimeSpan safetyNet, CancellationToken ct)
    {
        // A notification window appeared — or, as a safety net (Focus assist may hide the window), 20 s passed.
        if (await _toastShown.WaitAsync(safetyNet, ct).ConfigureAwait(false))
        {
            await Task.Delay(450, ct).ConfigureAwait(false);
        }

        return true;
    }

    public AiAssistants(ILogger logger, string pipeSuffix = "")
    {
        _logger = logger;
        // IBOX_NOTCH_PIPE: a test copy listens on its own pipe (the hooks of the real assistants never reach it).
        _pipeName = Environment.GetEnvironmentVariable("IBOX_NOTCH_PIPE") is { Length: > 0 } test ? test : NotchBridge.PipeName(Environment.UserName) + pipeSuffix;
    }

    /// <summary>A notification to show (raised on a background thread).</summary>
    public event EventHandler<AiAlert>? Alert;

    /// <summary>What Claude ("claude") or ChatGPT / Codex ("chatgpt") is doing now (raised on a background thread).</summary>
    public event EventHandler<(string Source, Views.AgentActivity Activity, ImageSource? Icon)>? Activity;

    /// <summary>Claude / ChatGPT notifications are shown (off: only other apps' notifications, if allowed).</summary>
    public bool Assistants { get; set; } = true;

    /// <summary>Whether another app's notifications come to the notch (by its AppUserModelId); null = none.</summary>
    public Func<string, bool>? AllowApp { get; set; }

    /// <summary>An app sent a notification (its id and name, for the list in the settings). Background thread.</summary>
    public event Action<string, string>? AppSeen;

    /// <summary>Shows a request on the notch and returns allow / deny / ask (raised on a background thread).</summary>
    public Func<NotchMessage, ImageSource?, CancellationToken, Task<string>>? Ask { get; set; }

    public string PipeName => _pipeName;

    /// <summary>Whether Windows lets IBOX read notifications (null = not checked yet).</summary>
    public UserNotificationListenerAccessStatus? NotificationAccess { get; private set; }

    /// <summary>Starts or stops both halves to follow the setting.</summary>
    public void Sync(bool enabled)
    {
        lock (_gate)
        {
            if (enabled && _running is null)
            {
                _running = new CancellationTokenSource();
                var token = _running.Token;
                _signalled = ToastSignal.Start(() =>
                {
                    if (_toastShown.CurrentCount == 0)
                    {
                        _toastShown.Release();
                    }
                });
                _ = Task.Run(() => WatchToastsAsync(token), CancellationToken.None);
                for (var i = 0; i < Listeners; i++)
                {
                    _ = Task.Run(() => ListenAsync(token), CancellationToken.None);
                }

                _ = Task.Run(() => FindAppsAsync(), CancellationToken.None);
                _logger.LogInformation("AI assistants on the notch: on.");
            }
            else if (!enabled && _running is not null)
            {
                _running.Cancel();
                _running.Dispose();
                _running = null;
                ToastSignal.Stop();
                _logger.LogInformation("AI assistants on the notch: off.");
            }
        }
    }

    /// <summary>The icon for an assistant (its installed app's own picture when there is one).</summary>
    public async Task<ImageSource?> IconAsync(string source)
    {
        var key = source == NotchSources.Codex ? "codex" : source == "chatgpt" ? "chatgpt" : "claude";
        if (_icons.TryGetValue(key, out var icon) && icon is not null)
        {
            return icon;
        }

        if (!_appIds.TryGetValue(key, out var appId))
        {
            await FindAppsAsync().ConfigureAwait(false);
            _appIds.TryGetValue(key, out appId);
        }

        icon = appId is null ? null : await ShellImages.GetAsync(@"shell:AppsFolder\" + appId, 64).ConfigureAwait(false);
        _icons[key] = icon;
        return icon;
    }

    private async Task FindAppsAsync()
    {
        if (!_appIds.ContainsKey("claude") && await ShellImages.FindAppAsync("Claude").ConfigureAwait(false) is { } claude)
        {
            _appIds["claude"] = claude;
        }

        if (!_appIds.ContainsKey("chatgpt") && await ShellImages.FindAppAsync("ChatGPT").ConfigureAwait(false) is { } chatgpt)
        {
            _appIds["chatgpt"] = chatgpt;
        }

        if (!_appIds.ContainsKey("codex") && await ShellImages.FindAppAsync("Codex", "ChatGPT").ConfigureAwait(false) is { } codex)
        {
            _appIds["codex"] = codex;
        }
    }

    // ------------------------------------------------------------------ desktop notifications

    /// <summary>Which assistant a notification belongs to (null = another app, never read).</summary>
    public static string? SourceOf(string? displayName, string? appId)
    {
        var name = displayName ?? string.Empty;
        var id = appId ?? string.Empty;
        if (name.Equals("Claude", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) || id.StartsWith("AnthropicClaude", StringComparison.OrdinalIgnoreCase))
        {
            return "claude";
        }

        if (name.Equals("Codex", StringComparison.OrdinalIgnoreCase))
        {
            return "codex";
        }

        if (name.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) || id.StartsWith("OpenAI.", StringComparison.OrdinalIgnoreCase))
        {
            return "chatgpt";
        }

        return null;
    }

    private async Task WatchToastsAsync(CancellationToken ct)
    {
        UserNotificationListener listener;
        try
        {
            listener = UserNotificationListener.Current;
            NotificationAccess = listener.GetAccessStatus();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidCastException or TypeLoadException)
        {
            _logger.LogInformation("Reading notifications is not available here: {Error}", ex.Message);
            return;
        }

        if (NotificationAccess != UserNotificationListenerAccessStatus.Allowed)
        {
            _logger.LogInformation("Windows does not let apps read notifications ({Status}); Claude / ChatGPT notifications stay off the notch.", NotificationAccess);
            return;
        }

        // Windows offers no change event to an app like IBOX: the list is read every second and a half.
        var seen = new HashSet<uint>();
        var first = true;
        // Read when Windows shows a notification (ToastSignal); only if that hook is impossible, every 1.5 s.
        using var timer = _signalled ? null : new PeriodicTimer(TimeSpan.FromSeconds(1.5));
        var safetyNet = TimeSpan.FromSeconds(20);
        do
        {
            IReadOnlyList<UserNotification> list;
            try
            {
                list = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var n in list)
            {
                if (!seen.Add(n.Id) || first)
                {
                    continue;
                }

                try
                {
                    await ShowToastAsync(n).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
                {
                    // one unreadable notification
                }
            }

            seen.IntersectWith(list.Select(n => n.Id));
            first = false;
        }
        while (timer is not null ? await timer.WaitForNextTickAsync(ct).ConfigureAwait(false) : await NextToastAsync(safetyNet, ct).ConfigureAwait(false));
    }

    private async Task ShowToastAsync(UserNotification n)
    {
        var app = n.AppInfo;
        var name = app?.DisplayInfo?.DisplayName;
        var appId = app?.AppUserModelId;
        var source = SourceOf(name, appId);
        if (source is null || !Assistants)
        {
            // Another app: only if the user picked it (its name and id are offered in the settings either way).
            if (appId is null)
            {
                return;
            }

            AppSeen?.Invoke(appId, name ?? appId);
            if (AllowApp?.Invoke(appId) != true)
            {
                return;
            }

            var lines = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            if (lines.Count == 0)
            {
                return;
            }

            Alert?.Invoke(this, new AiAlert(
                "app",
                name ?? appId,
                NotchBridge.Clean(lines[0], 90) ?? name ?? appId,
                NotchBridge.Clean(string.Join("\n", lines.Skip(1)), 220, lines: 3),
                await ToastIconAsync(appId, app).ConfigureAwait(false),
                appId));
            return;
        }

        // Claude Code's own request already is on the notch: its desktop echo would say the same twice.
        if (source == "claude" && Environment.TickCount64 - Interlocked.Read(ref _lastClaudeHookTicks) < 10_000)
        {
            return;
        }

        var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
        if (texts.Count == 0)
        {
            return;
        }

        if (appId is not null)
        {
            _appIds.TryAdd(source, appId);
        }

        var icon = await ToastIconAsync(source, app).ConfigureAwait(false);
        if (source != "claude")
        {
            // A ChatGPT / Codex notification usually means an answer is ready.
            Activity?.Invoke(this, ("chatgpt", Views.AgentActivity.Done, icon));
        }

        Alert?.Invoke(this, new AiAlert(
            source,
            name ?? source,
            NotchBridge.Clean(texts[0], 90) ?? name ?? source,
            NotchBridge.Clean(string.Join("\n", texts.Skip(1)), 220, lines: 3),
            icon,
            appId));
    }

    private async Task<ImageSource?> ToastIconAsync(string source, Windows.ApplicationModel.AppInfo? app, bool assistant = true)
    {
        if (_icons.TryGetValue(source, out var cached) && cached is not null)
        {
            return cached;
        }

        ImageSource? icon = null;
        try
        {
            if (app?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64)) is { } logo)
            {
                using var stream = await logo.OpenReadAsync().AsTask().ConfigureAwait(false);
                using var copy = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(copy).ConfigureAwait(false);
                copy.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = copy;
                bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                icon = bitmap;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            icon = null;
        }

        if (assistant && source is "claude" or "chatgpt" or "codex")
        {
            icon ??= await IconAsync(source).ConfigureAwait(false);
        }

        _icons[source] = icon;
        return icon;
    }

    // ------------------------------------------------------------------ Claude Code / Codex hooks

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, Listeners, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                _logger.LogDebug("Notch pipe: {Error}", ex.Message);
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var line = await ReadLineAsync(reader, ct).ConfigureAwait(false);
        var message = line is null ? null : JsonSerializer.Deserialize<NotchMessage>(line, NotchBridge.Json);
        if (message is null || message.V != NotchBridge.Version || message.Source is not (NotchSources.Claude or NotchSources.Codex))
        {
            await Reply(writer, NotchBridge.AskThere).ConfigureAwait(false);
            return;
        }

        if (message.Source == NotchSources.Claude)
        {
            Interlocked.Exchange(ref _lastClaudeHookTicks, Environment.TickCount64);
        }

        var icon = await IconAsync(message.Source).ConfigureAwait(false);
        var who = message.Source == NotchSources.Codex ? "chatgpt" : "claude";
        if (message.Kind == NotchMessage.StateKind)
        {
            Activity?.Invoke(this, (who, message.State switch
            {
                NotchMessage.Working => Views.AgentActivity.Working,
                NotchMessage.Done => Views.AgentActivity.Done,
                _ => Views.AgentActivity.Error,
            }, icon));
            await Reply(writer, "ok").ConfigureAwait(false);
            return;
        }

        if (message.IsAsk)
        {
            Activity?.Invoke(this, (who, Views.AgentActivity.Waiting, icon));
        }

        if (!message.IsAsk)
        {
            Alert?.Invoke(this, new AiAlert(message.Source, NotchBridge.AppName(message.Source) + (message.Project is { } p ? " · " + p : string.Empty), message.Title, message.Detail, icon, null));
            await Reply(writer, "ok").ConfigureAwait(false);
            return;
        }

        if (Ask is not { } ask)
        {
            await Reply(writer, NotchBridge.AskThere).ConfigureAwait(false);
            return;
        }

        // The request stays on the notch only while the hook waits: if the assistant gives up (its own timeout, or
        // the user answered there), the pipe closes and the notch lets go.
        using var answering = CancellationTokenSource.CreateLinkedTokenSource(ct);
        answering.CancelAfter(NotchBridge.AnswerTimeout - TimeSpan.FromSeconds(2));
        var gone = WatchClosedAsync(reader, answering);
        string decision;
        try
        {
            decision = await ask(message, icon, answering.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            decision = NotchBridge.AskThere;
        }

        _logger.LogInformation("{App} request answered on the notch: {Decision}.", NotchBridge.AppName(message.Source), decision);
        Activity?.Invoke(this, (who, decision == NotchBridge.Deny ? Views.AgentActivity.Done : Views.AgentActivity.Working, icon));
        if (message.Source == NotchSources.Claude)
        {
            Interlocked.Exchange(ref _lastClaudeHookTicks, Environment.TickCount64);
        }

        if (!(gone.IsCompleted && gone.Result))
        {
            await Reply(writer, decision is NotchBridge.Allow or NotchBridge.Deny ? decision : NotchBridge.AskThere).ConfigureAwait(false);
        }
    }

    /// <summary>True when the hook closed its end (the assistant stopped waiting).</summary>
    private static async Task<bool> WatchClosedAsync(StreamReader reader, CancellationTokenSource answering)
    {
        try
        {
            var buffer = new char[1];
            if (await reader.ReadAsync(buffer.AsMemory(), answering.Token).ConfigureAwait(false) == 0)
            {
                await answering.CancelAsync().ConfigureAwait(false);
                return true;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // done
        }

        return false;
    }

    private static Task Reply(StreamWriter writer, string decision) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(new NotchReply(decision), NotchBridge.Json));

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var sb = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false) > 0)
        {
            if (buffer[0] == '\n')
            {
                return sb.ToString();
            }

            if (sb.Length >= NotchBridge.MaxLineBytes)
            {
                return null;
            }

            sb.Append(buffer[0]);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? running;
        lock (_gate)
        {
            running = _running;
            _running = null;
        }

        if (running is not null)
        {
            await running.CancelAsync().ConfigureAwait(false);
            running.Dispose();
        }
    }
}
