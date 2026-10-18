using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Ipc;
using JackiVpn.Networking.Journal;
using JackiVpn.Networking.Recovery;
using JackiVpn.Networking.SystemProxy;
using JackiVpn.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// "Emergency network restore" — runs as its own process (<c>JackiVpn.exe --emergency-restore</c>, Start menu and
/// tray), so it works even when the main window is frozen:
/// <list type="number">
/// <item>closes a running IBOX window (its cores die with it);</item>
/// <item>asks the service to stop the tunnel — the service pre-empts or force-stops even a stuck session — and
/// to repair the network (kill-switch filters, leftover adapter);</item>
/// <item>restores the Windows proxy from the journal, and turns off a manual proxy only if it points at a
/// loopback port where nothing listens any more (a working proxy of another program is left alone).</item>
/// </list>
/// </summary>
public static class EmergencyRestore
{
    public const string Argument = "--emergency-restore";

    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        CloseOtherInstances(lines);

        var service = new ServiceControl(NullLogger.Instance);
        if (service.QueryScm() == ServiceAvailability.Running)
        {
            try
            {
                await using var client = await service.ConnectNewClientAsync(ct).ConfigureAwait(false);
                var status = await client.RequestAsync<ServiceStatus>(IpcMethods.StopTunnel, ct).ConfigureAwait(false);
                lines.Add(status.State == TunnelState.Stopped ? "VPN tunnel stopped." : $"VPN tunnel state: {status.State}.");
                var repair = await client.RequestAsync<RepairResponse>(IpcMethods.RepairNetwork, ct).ConfigureAwait(false);
                lines.AddRange(repair.Actions);
            }
            catch (Exception ex) when (ex is ServiceUnavailableException or IpcRemoteException or OperationCanceledException or System.IO.IOException)
            {
                lines.Add("Could not reach the IBOX service: " + SecretRedactor.Redact(ex.Message));
            }
        }
        else
        {
            lines.Add("The IBOX service is not running (nothing to stop there).");
        }

        try
        {
            var paths = AppPaths.Default();
            var journal = new NetworkStateJournal(paths.UserJournalPath);
            var winInet = new WinInetProxyApi();
            var proxy = new SystemProxyManager(winInet, journal, NullLogger.Instance);
            var recovery = new RecoveryCoordinator(journal, [new WinInetRecoveryHandler(proxy), new OrphanCoreHandler(NullLogger.Instance)], [new DnsFlushStep()], NullLogger.Instance);
            var report = await recovery.RepairAsync(ct).ConfigureAwait(false);
            lines.AddRange(report.Actions.Select(a => $"{(a.Success ? "OK" : "FAILED")} {a.Step}: {a.Detail}"));

            var finding = await SystemProxyInspector.InspectAsync(winInet, null, ct).ConfigureAwait(false);
            if (finding.Health == SystemProxyHealth.ForeignDead)
            {
                SystemProxyInspector.DisableManualProxy(winInet);
                lines.Add($"Turned off a dead system proxy ({finding.Server}:{finding.Port}) that blocked apps from reaching the internet.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lines.Add("Restoring the Windows proxy failed: " + SecretRedactor.Redact(ex.Message));
        }

        return lines;
    }

    private static void CloseOtherInstances(List<string> lines)
    {
        var self = Environment.ProcessId;
        var exe = Environment.ProcessPath;
        foreach (var p in Process.GetProcessesByName("IBOX").Concat(Process.GetProcessesByName("JackiVpn")))
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || exe is null || !string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    lines.Add("Closed the running IBOX window.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    lines.Add("Could not close a running IBOX window: " + ex.Message);
                }
            }
        }
    }
}
