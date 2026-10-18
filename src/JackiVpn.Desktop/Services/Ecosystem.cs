using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using JackiVpn.Desktop.Composition;
using JackiVpn.Domain.Agent;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// IBOX's side of the ecosystem with IBAR. IBAR is recognised by the program on the other end of the agent pipe —
/// IBAR.exe in its per-user install folder (or next to IBOX) — and then sees the connection, usage, traffic mode,
/// Dual VPN and network, and may connect, cancel or switch. Only this user's processes can reach the pipe at all;
/// the service (LocalSystem) is never involved. Same-user programs could imitate IBAR's file location, so nothing on
/// this pipe ever reveals credentials or server addresses.
/// </summary>
public static partial class Ecosystem
{
    /// <summary>Named mutex held by a running IBAR (IBOX hides its own notch while it exists).</summary>
    public const string IbarMutex = @"Local\IBAR.Notch";

    /// <summary>Signalled by IBOX once its pipe is up, so a waiting IBAR connects at once (no polling).</summary>
    public const string ReadyEvent = @"Local\IBOX.Ecosystem.Ready";

    public static string IbarFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "IBAR");

    public static bool IbarRunning()
    {
        try
        {
            using var m = System.Threading.Mutex.OpenExisting(IbarMutex);
            return true;
        }
        catch (Exception ex) when (ex is System.Threading.WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return ex is UnauthorizedAccessException;
        }
    }

    /// <summary>Whether the program at the other end of the pipe is IBAR.</summary>
    public static bool IsIbar(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            {
                return false;
            }

            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            if (path is null || !string.Equals(Path.GetFileName(path), "IBAR.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            return dir.StartsWith(IbarFolder, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || (Environment.GetEnvironmentVariable("JACKIVPN_SANDBOX") == "1" && Environment.GetEnvironmentVariable("JACKIVPN_TRUST_IBAR") is { Length: > 0 } dev && dir.StartsWith(dev, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What IBAR shows besides the connection itself.</summary>
    public static AgentEco Extras(AppServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var dual = s.Settings.Current.DualVpn;
        var facts = s.Workplace.Facts;
        var today = s.UsageBook.DayTotals(DateOnly.FromDateTime(DateTime.Now));
        var mode = s.Settings.Current.Traffic.Mode;
        return new AgentEco
        {
            Status = new AgentStatus { State = string.Empty },
            TrafficMode = s.Traffic.Mode.ToString().ToLowerInvariant() + (mode == TrafficMode.Normal && s.Traffic.Mode != TrafficMode.Normal ? " (auto)" : string.Empty),
            UsedTodayBytes = today.Length > 1 ? today[0] + today[1] : 0,
            DualVpnName = dual.IsConfigured ? dual.Name : null,
            DualVpnOn = dual.Enabled && dual.IsConfigured,
            DualVpnText = dual.IsConfigured ? s.DualVpn.Describe(s.Orchestrator.Status.State) : null,
            Network = facts.Ssid ?? facts.AdDomain ?? facts.DnsSuffix,
        };
    }

    /// <summary>traffic.set: normal, eco, gaming, streaming or work.</summary>
    public static string? SetTraffic(AppServices s, string mode)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!Enum.TryParse<TrafficMode>(mode, ignoreCase: true, out var m) || !Enum.IsDefined(m))
        {
            return "Unknown traffic mode.";
        }

        s.Settings.Update(x => x with { Traffic = x.Traffic with { Mode = m } });
        return null;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
}
