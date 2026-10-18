using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Linux;

/// <summary>
/// Whole-system (TUN) mode without sudo. sing-box has the network capability (set by the package), but it sets the
/// tunnel's DNS through systemd-resolved, and polkit asked for the password for each of those calls on every
/// connect. A one-time setup (one password prompt) adds the user to the <c>jackivpn</c> group, which a polkit rule
/// lets set DNS for network links; installs from the .tar.gz also get the capability for sing-box there.
/// </summary>
public static class LinuxTunAccess
{
    public const string Group = "jackivpn";

    private static readonly string[] RuleFiles =
    [
        "/etc/polkit-1/rules.d/49-jackivpn.rules",
        "/usr/share/polkit-1/rules.d/49-jackivpn.rules",
        "/etc/polkit-1/localauthority/50-local.d/49-jackivpn.pkla",
        "/var/lib/polkit-1/localauthority/10-vendor.d/49-jackivpn.pkla",
    ];

    /// <summary>polkit 0.106+ (JavaScript rules).</summary>
    public static readonly string Rules = Unix("""
        // IBOX: the VPN tunnel sets its own DNS (systemd-resolved) without asking for the password every time.
        polkit.addRule(function(action, subject) {
            if ((action.id.indexOf("org.freedesktop.resolve1.set-") == 0 || action.id == "org.freedesktop.resolve1.revert")
                && subject.local && subject.isInGroup("jackivpn")) {
                return polkit.Result.YES;
            }
        });

        """);

    /// <summary>Older polkit (Ubuntu 22.04 and earlier: .pkla files).</summary>
    public static readonly string Pkla = Unix("""
        [IBOX tunnel DNS]
        Identity=unix-group:jackivpn
        Action=org.freedesktop.resolve1.set-dns-servers;org.freedesktop.resolve1.set-domains;org.freedesktop.resolve1.set-default-route;org.freedesktop.resolve1.set-llmnr;org.freedesktop.resolve1.set-mdns;org.freedesktop.resolve1.set-dns-over-tls;org.freedesktop.resolve1.set-dnssec;org.freedesktop.resolve1.set-dnssec-negative-trust-anchors;org.freedesktop.resolve1.revert
        ResultAny=yes
        ResultInactive=yes
        ResultActive=yes

        """);

    private static int _asked;

    /// <summary>Scripts and config files for Linux: plain line feeds whatever this source file uses.</summary>
    private static string Unix(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>True when nothing is left to set up (or this is not a Linux desktop session).</summary>
    public static bool IsReady(string singBoxPath) =>
        !OperatingSystem.IsLinux() || Environment.UserName == "root" || (InGroup() && RuleFiles.Any(File.Exists) && HasCapability(singBoxPath));

    /// <summary>
    /// Asks once (pkexec) and sets everything up. Declining is remembered for this run: the tunnel still works, sing-box
    /// then asks for DNS itself as before.
    /// </summary>
    public static async Task EnsureAsync(string singBoxPath, ILogger logger, CancellationToken ct)
    {
        if (IsReady(singBoxPath) || Interlocked.Exchange(ref _asked, 1) == 1)
        {
            return;
        }

        var script = Unix("""
            set -e
            getent group jackivpn >/dev/null || groupadd -r jackivpn
            usermod -aG jackivpn "$1"
            mkdir -p /etc/polkit-1/rules.d
            printf '%s' "$3" > /etc/polkit-1/rules.d/49-jackivpn.rules
            chmod 644 /etc/polkit-1/rules.d/49-jackivpn.rules
            if [ -d /etc/polkit-1/localauthority ]; then
              mkdir -p /etc/polkit-1/localauthority/50-local.d
              printf '%s' "$4" > /etc/polkit-1/localauthority/50-local.d/49-jackivpn.pkla
            fi
            if command -v setcap >/dev/null 2>&1; then setcap cap_net_admin,cap_net_raw,cap_net_bind_service+ep "$2" || true; fi
            """);
        try
        {
            var psi = new ProcessStartInfo("pkexec") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "/bin/sh", "-c", script, "sh", Environment.UserName, singBoxPath, Rules, Pkla })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return;
            }

            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode == 0)
            {
                logger.LogInformation("Whole-system mode is set up: connecting no longer asks for the password.");
            }
            else
            {
                logger.LogWarning("Whole-system setup was declined or failed (exit {Code}): {Error}", p.ExitCode, (await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim());
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            logger.LogInformation("pkexec is not available; sing-box asks for DNS permission itself.");
        }
    }

    private static bool InGroup()
    {
        try
        {
            var line = File.ReadLines("/etc/group").FirstOrDefault(l => l.StartsWith(Group + ":", StringComparison.Ordinal));
            return line is not null && line.Split(':').ElementAtOrDefault(3)?.Split(',').Contains(Environment.UserName, StringComparer.Ordinal) == true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasCapability(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("getcap", [path]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null)
            {
                return true;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.Contains("cap_net_admin", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No getcap (libcap2-bin): cannot tell; do not ask for nothing.
            return true;
        }
    }
}
