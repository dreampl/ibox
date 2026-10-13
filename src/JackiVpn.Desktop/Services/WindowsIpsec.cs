using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Application.Workplace;
using JackiVpn.Domain.Usage;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// IPsec for Dual VPN through Windows' own VPN client: a per-user connection (IKEv2 with user name and password, or
/// L2TP/IPsec with a pre-shared key) with split tunnelling and routes for the company subnets only, dialled with
/// rasdial. No administrator rights needed; the connection also shows in Windows' VPN settings under its name.
/// </summary>
internal sealed class WindowsIpsec : ISecondVpnSystem
{
    public async Task<string?> SetUpIpsecAsync(string entry, string host, IpsecType type, string? psk, string? user, string? password, IReadOnlyList<string> cidrs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$name = {Quote(entry)}");
        sb.AppendLine("if (Get-VpnConnection -Name $name -ErrorAction SilentlyContinue) { rasdial $name /disconnect | Out-Null; Remove-VpnConnection -Name $name -Force }");
        sb.AppendLine(type == IpsecType.L2tp
            ? $"Add-VpnConnection -Name $name -ServerAddress {Quote(host)} -TunnelType L2tp -L2tpPsk {Quote(psk ?? string.Empty)} -AuthenticationMethod MSChapv2 -EncryptionLevel Required -SplitTunneling -RememberCredential -Force -WarningAction SilentlyContinue"
            : $"Add-VpnConnection -Name $name -ServerAddress {Quote(host)} -TunnelType Ikev2 -AuthenticationMethod Eap -EncryptionLevel Required -SplitTunneling -RememberCredential -Force -WarningAction SilentlyContinue");
        foreach (var cidr in cidrs)
        {
            sb.AppendLine($"Add-VpnConnectionRoute -ConnectionName $name -DestinationPrefix {Quote(cidr)} | Out-Null");
        }

        sb.AppendLine("'ok'");
        var (code, output) = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "-"], sb.ToString(), ct).ConfigureAwait(false);
        return code == 0 && output.Contains("ok", StringComparison.Ordinal) ? null : "Windows could not create the VPN connection: " + Last(output);
    }

    public async Task<string?> DialAsync(string entry, string? user, string? password, CancellationToken ct)
    {
        var args = new List<string> { entry };
        if (!string.IsNullOrEmpty(user))
        {
            args.Add(user);
            args.Add(password ?? string.Empty);
        }

        var (code, output) = await RunAsync("rasdial.exe", args, null, ct).ConfigureAwait(false);
        return code switch
        {
            0 => null,
            691 => "The VPN refused the user name or password.",
            809 or 789 => "The VPN gateway did not answer (IPsec ports UDP 500/4500 may be blocked on this network).",
            868 => "The VPN gateway's name could not be found.",
            _ => "The VPN did not connect: " + Last(output),
        };
    }

    public Task HangUpAsync(string entry, CancellationToken ct) => RunAsync("rasdial.exe", [entry, "/disconnect"], null, ct);

    public Task RemoveAsync(string entry, CancellationToken ct) =>
        RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "-"], $"Remove-VpnConnection -Name {Quote(entry)} -Force -ErrorAction SilentlyContinue", ct);

    /// <summary>A Windows VPN connection's adapter carries its name.</summary>
    public string? AdapterOf(string entry) => entry;

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Length > 0) ?? "unknown error";

    private static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> args, string? stdin, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, "could not start " + file);
            }

            if (stdin is not null)
            {
                // Scripts (with the pre-shared key) go in on standard input, not on a command line.
                await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                p.StandardInput.Close();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (p.ExitCode, await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return (-1, ex.Message);
        }
    }
}
