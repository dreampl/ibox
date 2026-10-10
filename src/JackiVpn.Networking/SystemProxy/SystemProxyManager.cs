using System.Globalization;
using JackiVpn.Networking.Journal;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Networking.SystemProxy;

public sealed record WinInetJournalData
{
    /// <summary>Exact settings before we changed anything (one per connection: LAN + RAS entries).</summary>
    public required IReadOnlyList<ProxySnapshot> Previous { get; init; }

    /// <summary>What we applied, so recovery can tell whether someone else changed it since.</summary>
    public required string AppliedProxyServer { get; init; }
}

public sealed record SystemProxyRestoreReport(int Restored, int SkippedChangedByOthers, IReadOnlyList<string> Errors);

/// <summary>
/// Sets and restores the Windows (WinINET) system proxy for the current user. Every change is journaled
/// before it is made; <see cref="Restore"/> and <see cref="Recover"/> put back the exact previous values
/// (flags, server, bypass list and PAC URL) for the LAN connection and every RAS/VPN entry.
/// </summary>
public sealed class SystemProxyManager
{
    public const string JournalId = "wininet-proxy";
    private readonly IWinInetProxyApi _api;
    private readonly NetworkStateJournal _journal;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    public SystemProxyManager(IWinInetProxyApi api, NetworkStateJournal journal, ILogger logger)
    {
        _api = api;
        _journal = journal;
        _logger = logger;
    }

    public bool IsApplied => _journal.OfKind(JournalEntryKind.WinInetProxy).Count > 0;

    public static string ProxyServerFor(int port) => string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}");

    public void Apply(int port, string bypassList, bool includeRasConnections = true)
    {
        lock (_gate)
        {
            var connections = new List<string?> { null };
            if (includeRasConnections)
            {
                connections.AddRange(_api.EnumerateRasConnections());
            }

            // If a previous apply is still active (e.g. port change), keep the ORIGINAL snapshots.
            var existing = _journal.OfKind(JournalEntryKind.WinInetProxy).FirstOrDefault();
            var previous = existing?.As<WinInetJournalData>().Previous
                ?? connections.Select(c => _api.Query(c)).ToArray();

            var server = ProxyServerFor(port);
            _journal.Record(JournalEntryKind.WinInetProxy, new WinInetJournalData { Previous = previous, AppliedProxyServer = server }, JournalId);

            foreach (var c in connections)
            {
                _api.Apply(new ProxySnapshot
                {
                    ConnectionName = c,
                    Flags = ProxySnapshot.FlagDirect | ProxySnapshot.FlagProxy,
                    ProxyServer = server,
                    ProxyBypass = bypassList,
                    AutoConfigUrl = null,
                });
            }

            _api.NotifyChanged();
            _logger.LogInformation("System proxy set to {Server} on {Count} connection(s).", server, connections.Count);
        }
    }

    /// <summary>Normal disconnect: restore exactly what was there before.</summary>
    public SystemProxyRestoreReport Restore() => RestoreCore(onlyIfStillOurs: false);

    /// <summary>
    /// Crash recovery: restore the backup, but never clobber a proxy the user (or another app) configured
    /// after our crash — only connections that still point at our proxy are reverted.
    /// </summary>
    public SystemProxyRestoreReport Recover() => RestoreCore(onlyIfStillOurs: true);

    private SystemProxyRestoreReport RestoreCore(bool onlyIfStillOurs)
    {
        lock (_gate)
        {
            var entry = _journal.OfKind(JournalEntryKind.WinInetProxy).FirstOrDefault();
            if (entry is null)
            {
                return new SystemProxyRestoreReport(0, 0, []);
            }

            var data = entry.As<WinInetJournalData>();
            var restored = 0;
            var skipped = 0;
            var errors = new List<string>();
            foreach (var snapshot in data.Previous)
            {
                try
                {
                    if (onlyIfStillOurs)
                    {
                        var current = _api.Query(snapshot.ConnectionName);
                        if (!current.ProxyEnabled || !string.Equals(current.ProxyServer, data.AppliedProxyServer, StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            continue;
                        }
                    }

                    _api.Apply(snapshot);
                    restored++;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    errors.Add($"{snapshot.ConnectionName ?? "LAN"}: {ex.Message}");
                }
            }

            _api.NotifyChanged();
            if (errors.Count == 0)
            {
                _journal.Complete(entry.Id);
            }

            _logger.LogInformation("System proxy restored on {Restored} connection(s); {Skipped} left untouched (changed by others); {Errors} error(s).", restored, skipped, errors.Count);
            return new SystemProxyRestoreReport(restored, skipped, errors);
        }
    }
}
