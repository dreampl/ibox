using System.Diagnostics;
using JackiVpn.Application.Privacy;
using JackiVpn.Security;

namespace JackiVpn.Linux;

/// <summary>
/// System time zone through systemd-timedated (<c>timedatectl set-timezone</c>; polkit may ask once). The original
/// zone is written to a file before the first change and removed after it was put back, so the next start undoes
/// a crash. Without timedatectl the feature does nothing.
/// </summary>
public sealed class LinuxTimeZoneControl(string originalPath) : ITimeZoneControl
{
    public async Task ApplyAsync(string ianaId, CancellationToken ct)
    {
        if (!TimeZoneMap.IsIanaId(ianaId) || !File.Exists(Path.Combine("/usr/share/zoneinfo", ianaId)))
        {
            throw new ArgumentException("Unknown time zone.", nameof(ianaId));
        }

        if (!File.Exists(originalPath))
        {
            var current = (await RunAsync(ct, "show", "-p", "Timezone", "--value").ConfigureAwait(false)).Output.Trim();
            if (!TimeZoneMap.IsIanaId(current))
            {
                throw new InvalidOperationException("The current time zone could not be read.");
            }

            SecureFileSystem.WriteAllTextAtomic(originalPath, current);
        }

        var (code, output) = await RunAsync(ct, "set-timezone", ianaId).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"timedatectl set-timezone failed: {output.Trim()}");
        }
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(originalPath))
        {
            return;
        }

        var original = (await File.ReadAllTextAsync(originalPath, ct).ConfigureAwait(false)).Trim();
        if (TimeZoneMap.IsIanaId(original))
        {
            var (code, output) = await RunAsync(ct, "set-timezone", original).ConfigureAwait(false);
            if (code != 0)
            {
                // Keep the file: the next start tries again.
                throw new InvalidOperationException($"timedatectl set-timezone failed: {output.Trim()}");
            }
        }

        File.Delete(originalPath);
    }

    private static async Task<(int Code, string Output)> RunAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("timedatectl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("timedatectl could not be started.");
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false) + await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("timedatectl is not available on this system.", ex);
        }
    }
}
