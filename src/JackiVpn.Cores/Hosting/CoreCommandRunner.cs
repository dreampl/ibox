using System.Diagnostics;
using System.Text;
using JackiVpn.Domain.Common;

namespace JackiVpn.Cores.Hosting;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : StdOut + Environment.NewLine + StdErr;
}

/// <summary>
/// Runs a short-lived core command (version, config check). Arguments are passed through
/// <see cref="ProcessStartInfo.ArgumentList"/> (never string-concatenated) and validated first, so user text
/// can never become extra command-line switches. Config content goes through stdin (UTF-8, no BOM).
/// </summary>
public static class CoreCommandRunner
{
    public const int MaxOutputChars = 256 * 1024;

    public static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? stdin,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        EnsureSafeArguments(arguments);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                psi.Environment[k] = v;
            }
        }

        using var job = OperatingSystem.IsWindows() ? new JobObject() : null;
        using var process = new System.Diagnostics.Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        process.Start();
        job?.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Process exited before reading stdin; its output explains why.
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            TryKill(process);
            if (!timedOut)
            {
                throw;
            }
        }

        // Flush async output readers.
        if (!timedOut)
        {
            process.WaitForExit();
        }

        return new CommandResult(timedOut ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }

    /// <summary>Rejects arguments that could be interpreted as something other than a single literal argument.</summary>
    public static void EnsureSafeArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (var a in arguments)
        {
            if (a is null || a.Length > 4096 || NetText.HasControlChars(a) || a.Contains('"', StringComparison.Ordinal))
            {
                throw new ArgumentException("Unsafe process argument rejected.", nameof(arguments));
            }
        }
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sb)
        {
            if (sb.Length < MaxOutputChars)
            {
                sb.AppendLine(line);
            }
        }
    }

    private static void TryKill(System.Diagnostics.Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
