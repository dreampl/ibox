using System.Diagnostics;
using System.Text;
using JackiVpn.Cores.Hosting;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Linux;

/// <summary>
/// One password for the whole session. When the app opens in whole-system (TUN) mode — or on the first TUN connect
/// otherwise — it starts its privileged helper with a single <c>pkexec</c>. From then on sing-box runs through the
/// helper as root, so systemd-resolved and polkit ask nothing more for connecting, disconnecting or switching
/// servers. The helper lives only as long as the app (it reads the app's commands on its input and exits when that
/// closes), accepts only the installed sing-box and a few nmcli calls, and refuses to run a sing-box that is not
/// owned by root (an install in the home folder then keeps the earlier setcap + polkit route).
///
/// Before: up to four polkit prompts per connect came from sing-box's own calls to systemd-resolved (link DNS
/// servers, search domains, default route, and revert on disconnect) whenever the one-time group rule was not in
/// effect, plus nmcli prompts for Dual VPN / adapter DNS, and <c>sudo gsettings</c> when started with sudo.
/// </summary>
public static class LinuxPrivilege
{
    private static HelperClient? _client;
    private static Process? _helper;
    private static int _asked;

    public static bool Active => _client is { Alive: true };

    /// <summary>Asks once per app run (pkexec); declining keeps the earlier behaviour.</summary>
    public static async Task<bool> EnsureAsync(ILogger logger, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            return false;
        }

        if (Active)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _asked, 1) == 1 || Environment.ProcessPath is not { } self)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo("pkexec") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false) };
            psi.ArgumentList.Add(self);
            psi.ArgumentList.Add("helper");
            _helper = Process.Start(psi);
            if (_helper is null)
            {
                return false;
            }

            _client = new HelperClient(_helper.StandardOutput, _helper.StandardInput);
            var hello = await _client.NmcliAsync(["connection", "up", "IBOX ping"], ct).WaitAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            if (!_client.Alive)
            {
                logger.LogWarning("The helper was declined or refused: {Error}", (await _helper.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim());
                return false;
            }

            _ = hello;
            CoreHosting.Host = (spec, config) => Active && spec.Kind == Domain.Connection.CoreKind.SingBox && config.Json.Contains("\"type\":\"tun\"", StringComparison.Ordinal) ? _client!.Core() : null;
            logger.LogInformation("Privileged helper is running for this session: no more password prompts.");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            logger.LogInformation("pkexec is not available ({Error}); the earlier one-time setup is used.", ex.Message);
            return false;
        }
    }

    /// <summary>nmcli through the helper when it runs (no prompt), otherwise null (the caller runs nmcli itself).</summary>
    public static Task<(int Code, string Output)>? Nmcli(IReadOnlyList<string> args, CancellationToken ct) =>
        Active && HelperRules.NmcliProblem(args) is null ? _client!.NmcliAsync(args, ct) : null;

    public static void Stop()
    {
        _client?.Dispose();
        _helper?.Dispose();
        _client = null;
        CoreHosting.Host = null;
    }

    // ------------------------------------------------------------------ the helper itself (root)

    /// <summary><c>jackivpn helper</c>, started by pkexec: serves the app on standard input / output, then exits.</summary>
    public static async Task<int> RunHelperAsync()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            await Console.Error.WriteLineAsync("helper: must be started through pkexec.").ConfigureAwait(false);
            return 2;
        }

        var core = Path.Combine(AppContext.BaseDirectory, "cores", "sing-box");
        if (!RootOwned(core) || !RootOwned(AppContext.BaseDirectory.TrimEnd('/')))
        {
            // A binary the user can replace must never run as root.
            await Console.Error.WriteLineAsync("helper: the installed sing-box is not owned by root (install the .deb).").ConfigureAwait(false);
            return 3;
        }

        var uid = Environment.GetEnvironmentVariable("PKEXEC_UID");
        var home = uid is null ? null : HomeOf(uid);
        if (home is null)
        {
            await Console.Error.WriteLineAsync("helper: unknown user.").ConfigureAwait(false);
            return 2;
        }

        var runDir = Path.Combine("/run/jackivpn", uid!);
        Directory.CreateDirectory(runDir);
        File.SetUnixFileMode(runDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var server = new HelperServer(core, runDir, Path.Combine(home, ".local", "share", "jackivpn"), (exe, a, cwd) => new LocalHostedProcess(exe, a, cwd), RunNmcliAsync);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await server.RunAsync(input, output, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task<(int, string)> RunNmcliAsync(IReadOnlyList<string> args)
    {
        if (args is ["connection", "up", "IBOX ping"])
        {
            return (0, "helper ready");
        }

        var psi = new ProcessStartInfo("nmcli") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false) + await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return (p.ExitCode, text.Trim());
    }

    private static bool RootOwned(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("stat", ["-c", "%u %a", path]) { UseShellExecute = false, RedirectStandardOutput = true })!;
            var parts = p.StandardOutput.ReadToEnd().Trim().Split(' ');
            p.WaitForExit();
            return parts is [var owner, var mode] && owner == "0" && mode.Length >= 3 && (mode[^2] - '0' & 2) == 0 && (mode[^1] - '0' & 2) == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    private static string? HomeOf(string uid)
    {
        foreach (var line in File.ReadLines("/etc/passwd"))
        {
            var f = line.Split(':');
            if (f.Length >= 6 && f[2] == uid)
            {
                return f[5];
            }
        }

        return null;
    }
}
