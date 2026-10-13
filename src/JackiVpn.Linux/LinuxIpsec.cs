using System.Diagnostics;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Linux;

/// <summary>
/// IPsec for Dual VPN through NetworkManager: L2TP/IPsec (network-manager-l2tp) or IKEv2 (network-manager-strongswan),
/// never the default route, with routes for the company subnets only.
/// </summary>
public sealed class LinuxIpsec : ISecondVpnSystem
{
    private static readonly string[] PluginDirs = ["/usr/lib/NetworkManager/VPN", "/etc/NetworkManager/VPN", "/usr/lib64/NetworkManager/VPN"];

    public async Task<string?> SetUpIpsecAsync(string entry, string host, IpsecType type, string? psk, string? user, string? password, IReadOnlyList<string> cidrs, CancellationToken ct)
    {
        var plugin = type == IpsecType.L2tp ? "nm-l2tp-service.name" : "nm-strongswan-service.name";
        if (!PluginDirs.Any(d => File.Exists(Path.Combine(d, plugin))))
        {
            return type == IpsecType.L2tp
                ? "L2TP/IPsec needs NetworkManager's L2TP plugin: sudo apt install network-manager-l2tp"
                : "IKEv2 needs NetworkManager's strongSwan plugin: sudo apt install network-manager-strongswan";
        }

        _ = await NmcliAsync(ct, "connection", "delete", "id", entry).ConfigureAwait(false);
        string Esc(string v) => v.Replace(",", string.Empty, StringComparison.Ordinal).Replace("=", string.Empty, StringComparison.Ordinal).Trim();
        var data = type == IpsecType.L2tp
            ? $"gateway={Esc(host)}, user={Esc(user ?? string.Empty)}, ipsec-enabled=yes, ipsec-psk={Esc(psk ?? string.Empty)}, password-flags=0"
            : $"address={Esc(host)}, method=eap, user={Esc(user ?? string.Empty)}, virtual=yes, encap=yes, password-flags=0";
        var args = new List<string>
        {
            "connection", "add", "type", "vpn", "con-name", entry, "vpn-type", type == IpsecType.L2tp ? "l2tp" : "strongswan",
            "vpn.data", data, "vpn.secrets", "password=" + (password ?? string.Empty),
            "ipv4.never-default", "yes", "ipv6.never-default", "yes", "connection.autoconnect", "no",
        };
        if (cidrs.Count > 0)
        {
            args.AddRange(["ipv4.routes", string.Join(", ", cidrs.Where(c => !c.Contains(':', StringComparison.Ordinal)))]);
        }

        return await NmcliAsync(ct, [.. args]).ConfigureAwait(false) is null ? "NetworkManager could not create the VPN connection." : null;
    }

    public async Task<string?> DialAsync(string entry, string? user, string? password, CancellationToken ct) =>
        await NmcliAsync(ct, "connection", "up", "id", entry).ConfigureAwait(false) is null ? "The VPN did not connect (check the gateway, user name and password)." : null;

    public Task HangUpAsync(string entry, CancellationToken ct) => NmcliAsync(ct, "connection", "down", "id", entry);

    public Task RemoveAsync(string entry, CancellationToken ct) => NmcliAsync(ct, "connection", "delete", "id", entry);

    /// <summary>L2TP brings up a ppp interface; IKEv2 is policy-based (no interface).</summary>
    public string? AdapterOf(string entry)
    {
        var output = NmcliAsync(CancellationToken.None, "-g", "GENERAL.IP-IFACE", "connection", "show", "id", entry).GetAwaiter().GetResult()?.Trim();
        return string.IsNullOrEmpty(output) ? null : output.Split('\n')[0].Trim();
    }

    private static async Task<string?> NmcliAsync(CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("nmcli") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
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
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }
}
