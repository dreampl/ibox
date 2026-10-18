using System.Diagnostics;
using JackiVpn.Application.Connectivity;
using JackiVpn.Domain.Network;

namespace JackiVpn.Linux;

/// <summary>
/// Adapter DNS through NetworkManager (nmcli): the servers are stored on the connection the device uses and the
/// device re-applies them at once. NetworkManager lets the desktop user change their own connections (polkit), so
/// no sudo is needed. Without NetworkManager the list is shown read-only.
/// </summary>
public sealed class LinuxAdapterDns : IAdapterDnsControl
{
    public async Task<IReadOnlyList<AdapterDns>> ListAsync(CancellationToken ct)
    {
        var devices = await NmcliAsync(ct, "-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device").ConfigureAwait(false);
        if (devices is null)
        {
            throw new InvalidOperationException("NetworkManager (nmcli) is not available: adapter DNS cannot be managed on this system.");
        }

        var list = new List<AdapterDns>();
        foreach (var line in devices.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = SplitTerse(line);
            if (f.Count < 4 || f[1] is not ("wifi" or "ethernet") || !AdapterDnsChange.LinuxDevice().IsMatch(f[0]))
            {
                continue;
            }

            var connection = f[3] is { Length: > 0 } c && c != "--" ? c : null;
            var current = await NmcliAsync(ct, "-t", "-g", "IP4.DNS,IP6.DNS", "device", "show", f[0]).ConfigureAwait(false) ?? string.Empty;
            var automatic = true;
            if (connection is not null)
            {
                var stored = await NmcliAsync(ct, "-t", "-g", "ipv4.dns,ipv6.dns", "connection", "show", connection).ConfigureAwait(false) ?? string.Empty;
                automatic = AdapterDnsChange.Parse(stored.Replace('|', ' ')).All(s => !AdapterDnsChange.IsAddress(s));
            }

            list.Add(new AdapterDns
            {
                Id = f[0],
                Name = f[0],
                Description = connection,
                Kind = f[1] == "wifi" ? "Wi-Fi" : "Ethernet",
                Up = f[2].StartsWith("connected", StringComparison.Ordinal),
                Automatic = automatic,
                Servers = AdapterDnsChange.Parse(current.Replace('|', ' ')).Where(AdapterDnsChange.IsAddress).Distinct().ToList(),
                Connection = connection,
            });
        }

        return list.OrderByDescending(a => a.Up).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    public async Task SetAsync(AdapterDnsChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var problem = change.Problem() ?? (AdapterDnsChange.LinuxDevice().IsMatch(change.AdapterId) ? null : "Unknown network adapter.");
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(change));
        }

        var adapter = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == change.AdapterId)
            ?? throw new ArgumentException("Unknown network adapter.", nameof(change));
        if (adapter.Connection is null)
        {
            throw new InvalidOperationException($"{adapter.Name} is not connected to a network: connect it first, then set its DNS.");
        }

        var v4 = string.Join(' ', change.Servers.Where(s => !s.Contains(':', StringComparison.Ordinal)));
        var v6 = string.Join(' ', change.Servers.Where(s => s.Contains(':', StringComparison.Ordinal)));
        var result = await NmcliAsync(ct, "connection", "modify", adapter.Connection,
            "ipv4.dns", v4, "ipv4.ignore-auto-dns", v4.Length > 0 ? "yes" : "no",
            "ipv6.dns", v6, "ipv6.ignore-auto-dns", v6.Length > 0 ? "yes" : "no").ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException("NetworkManager refused the change (permission or unknown connection).");
        }

        // Apply now without dropping the connection; older NetworkManager without reapply re-activates it.
        if (await NmcliAsync(ct, "device", "reapply", adapter.Id).ConfigureAwait(false) is null)
        {
            _ = await NmcliAsync(ct, "connection", "up", adapter.Connection).ConfigureAwait(false);
        }
    }

    /// <summary>Fields of one terse nmcli line (':' separates, '\:' and '\\' are escapes).</summary>
    internal static List<string> SplitTerse(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\\' && i + 1 < line.Length)
            {
                current.Append(line[++i]);
            }
            else if (ch == ':')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (ch != '\r')
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>Runs nmcli; null when it is missing or fails.</summary>
    private static async Task<string?> NmcliAsync(CancellationToken ct, params string[] args)
    {
        if (LinuxPrivilege.Nmcli(args, ct) is { } elevated)
        {
            // The session's helper runs it as root: no polkit prompt.
            var (code, text) = await elevated.ConfigureAwait(false);
            return code == 0 ? text : null;
        }

        try
        {
            var psi = new ProcessStartInfo("nmcli") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
