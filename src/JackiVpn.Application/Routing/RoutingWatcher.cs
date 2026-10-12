using System.Text.Json;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Settings;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Routing;

/// <summary>
/// Routing rules live in the core configuration, so an edit used to wait for the next connection and looked like
/// "the rule does nothing". While connected, a change of the rules (or of the final action, LAN bypass, sniffing,
/// rule-sets) restarts the session on the same server once the user stops editing for a moment.
/// </summary>
public sealed class RoutingWatcher : IDisposable
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    private readonly SettingsStore _settings;
    private readonly ConnectionOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly TimeSpan _settle;
    private readonly Lock _gate = new();
    private string _seen;
    private CancellationTokenSource? _pending;

    public RoutingWatcher(SettingsStore settings, ConnectionOrchestrator orchestrator, ILogger logger, TimeSpan? settle = null)
    {
        _settings = settings;
        _orchestrator = orchestrator;
        _logger = logger;
        _settle = settle ?? Settle;
        _seen = Key(settings.Current);
        settings.Changed += OnChanged;
    }

    /// <summary>Raised when a change was applied to the running connection (true) or could not be (false).</summary>
    public event EventHandler<bool>? Applied;

    public static string Key(AppSettings s) => JsonSerializer.Serialize(s.Routing, JsonDefaults.Storage);

    /// <summary>Applies the current routing and DNS settings to the running connection now (e.g. the DNS page's Apply).</summary>
    public Task<bool> ApplyNowAsync(string reason) => ApplyAsync(reason, CancellationToken.None);

    private void OnChanged(object? sender, AppSettings s)
    {
        var key = Key(s);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (key == _seen)
            {
                return;
            }

            _seen = key;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }

        if (_orchestrator.State != ConnectionState.Connected)
        {
            return; // used by the next connection as it is
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_settle, cts.Token).ConfigureAwait(false);
                await ApplyAsync("routing rules changed", cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // a newer edit follows
            }
        });
    }

    private async Task<bool> ApplyAsync(string reason, CancellationToken ct)
    {
        try
        {
            var ok = await _orchestrator.ReconnectAsync(reason, ct, refreshRouting: true).ConfigureAwait(false);
            Applied?.Invoke(this, ok);
            return ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _logger.LogWarning("Routing change not applied: {Error}", ex.Message);
            Applied?.Invoke(this, false);
            return false;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnChanged;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
