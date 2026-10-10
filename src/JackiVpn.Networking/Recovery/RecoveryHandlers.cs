using System.Diagnostics;
using JackiVpn.Networking.Adapters;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.KillSwitch;
using JackiVpn.Networking.SystemProxy;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Networking.Recovery;

public sealed class WinInetRecoveryHandler(SystemProxyManager manager) : IRecoveryHandler
{
    public JournalEntryKind Kind => JournalEntryKind.WinInetProxy;

    public RecoveryAction Recover(JournalEntry entry)
    {
        var report = manager.Recover();
        return new RecoveryAction(
            "System proxy",
            report.Errors.Count == 0,
            report.Errors.Count == 0
                ? $"Restored {report.Restored} connection(s); {report.SkippedChangedByOthers} changed by others since and left untouched."
                : string.Join("; ", report.Errors));
    }
}

public sealed class WinHttpRecoveryHandler(WinHttpProxyManager manager) : IRecoveryHandler
{
    public JournalEntryKind Kind => JournalEntryKind.WinHttpProxy;

    public RecoveryAction Recover(JournalEntry entry) => new("WinHTTP proxy", true, manager.Restore() ? "Restored." : "Nothing to restore.");
}

public sealed class KillSwitchRecoveryHandler(IKillSwitch killSwitch) : IRecoveryHandler
{
    public JournalEntryKind Kind => JournalEntryKind.KillSwitch;

    public RecoveryAction Recover(JournalEntry entry)
    {
        killSwitch.Disable();
        return new RecoveryAction("Kill switch", true, "Kill-switch filters removed.");
    }
}

/// <summary>Removes a Wintun adapter left behind by a crashed core (normally removed automatically).</summary>
public sealed class TunAdapterRecoveryHandler(ILogger logger) : IRecoveryHandler
{
    public JournalEntryKind Kind => JournalEntryKind.TunAdapter;

    public RecoveryAction Recover(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return StaleAdapterCleaner.Remove(entry.As<TunAdapterJournalData>().InterfaceName, logger);
    }
}

public static class StaleAdapterCleaner
{
    public static RecoveryAction Remove(string interfaceName, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var adapter = NetworkInspector.FindAdapter(interfaceName);
        if (adapter is null)
        {
            return new RecoveryAction("TUN adapter", true, $"No '{interfaceName}' adapter present.");
        }

        var instanceId = NetworkInspector.FindPnpInstanceId(interfaceName);
        if (instanceId is null || !NetworkInspector.IsSafePnpInstanceId(instanceId))
        {
            return new RecoveryAction("TUN adapter", false, $"Adapter '{interfaceName}' exists but its device id could not be determined safely.");
        }

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/remove-device");
        psi.ArgumentList.Add(instanceId);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        var ok = p.ExitCode == 0;
        logger.Log(ok ? LogLevel.Information : LogLevel.Warning, "pnputil /remove-device {Id} → {Code}", instanceId, p.ExitCode);
        return new RecoveryAction("TUN adapter", ok, ok ? $"Removed stale adapter '{interfaceName}'." : $"pnputil failed ({p.ExitCode}): {output.Trim()}");
    }
}

/// <summary>Repair sweep: removes kill-switch filters even if the journal was lost.</summary>
public sealed class KillSwitchSweep(IKillSwitch killSwitch) : IRepairStep
{
    public string Name => "Kill switch sweep";

    public RecoveryAction Run()
    {
        var engaged = killSwitch.IsEngaged();
        killSwitch.Disable();
        return new RecoveryAction(Name, true, engaged ? "Leftover kill-switch filters removed." : "No kill-switch filters present.");
    }
}

public sealed class StaleTunSweep(string interfaceName, Func<bool> coreRunning, ILogger logger) : IRepairStep
{
    public string Name => "TUN adapter sweep";

    public RecoveryAction Run() => coreRunning()
        ? new RecoveryAction(Name, true, "Tunnel is active; adapter is in use.")
        : StaleAdapterCleaner.Remove(interfaceName, logger);
}

public sealed class DnsFlushStep : IRepairStep
{
    public string Name => "DNS cache";

    public RecoveryAction Run() => new(Name, NetworkInspector.FlushDnsCache(), "Windows DNS cache flushed.");
}

/// <summary>
/// Report-only check: a loopback system proxy with no journal entry might be a leftover from a very old
/// crash — or another app's proxy (e.g. another VPN client). We never change it without the user's say.
/// </summary>
public sealed class SuspiciousProxyReport(IWinInetProxyApi api, string ourBypassList) : IRepairStep
{
    public string Name => "System proxy check";

    public RecoveryAction Run()
    {
        var lan = api.Query(null);
        if (lan.ProxyEnabled && lan.ProxyServer?.StartsWith("127.0.0.1:", StringComparison.Ordinal) == true
            && string.Equals(lan.ProxyBypass, ourBypassList, StringComparison.OrdinalIgnoreCase))
        {
            return new RecoveryAction(Name, true, $"System proxy {lan.ProxyServer} looks like a leftover from {Domain.ProductInfo.DisplayName}; it was NOT changed. Disable it in Windows proxy settings if it is not in use.");
        }

        return new RecoveryAction(Name, true, lan.ProxyEnabled ? $"System proxy {lan.ProxyServer} belongs to another configuration; left untouched." : "No system proxy set.");
    }
}
