using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using JackiVpn.Domain.Connection;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Cores.Hosting;

public sealed class CoreStartException(string message, IReadOnlyList<string> recentOutput) : Exception(message)
{
    public IReadOnlyList<string> RecentOutput { get; } = recentOutput;
}

/// <summary>
/// A running core. Created only from a <see cref="VerifiedCoreBinary"/>; configuration is written to stdin
/// and never touches disk. The process belongs to a kill-on-close job object, output is captured line by
/// line (redacted before logging), and <see cref="Exited"/> completes on any exit so supervisors can react
/// to crashes immediately.
/// </summary>
public sealed partial class CoreProcess : IAsyncDisposable
{
    private const int RingSize = 300;
    private readonly System.Diagnostics.Process _process;
    private readonly JobObject? _job;
    private readonly ILogger _logger;
    private readonly Queue<string> _ring = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<string> _secrets;
    private int _stopping;

    private CoreProcess(CoreKind kind, System.Diagnostics.Process process, JobObject? job, ILogger logger, IReadOnlyList<string> secrets)
    {
        Kind = kind;
        _process = process;
        _job = job;
        _logger = logger;
        _secrets = secrets;
        StartedUtc = DateTimeOffset.UtcNow;
    }

    public CoreKind Kind { get; }

    public int ProcessId { get; private set; }

    public string ExecutablePath => _process.StartInfo.FileName;

    public DateTimeOffset StartedUtc { get; }

    /// <summary>Completes with the exit code when the process exits for any reason.</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>True when the exit was requested through <see cref="StopAsync"/>.</summary>
    public bool StopRequested => Volatile.Read(ref _stopping) == 1;

    public event EventHandler<string>? OutputLine;

    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_ring)
            {
                return _ring.ToArray();
            }
        }
    }

    public static async Task<CoreProcess> StartAsync(
        VerifiedCoreBinary binary,
        CoreLaunchSpec spec,
        GeneratedCoreConfig config,
        ILogger logger,
        CancellationToken ct,
        bool background = false)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(config);
        if (!string.Equals(binary.ExecutablePath, spec.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new CoreIntegrityException("Launch spec does not match the verified binary.");
        }

        CoreCommandRunner.EnsureSafeArguments(spec.Arguments);
        Directory.CreateDirectory(spec.WorkingDirectory);
        var psi = new ProcessStartInfo(binary.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var a in spec.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in spec.Environment)
        {
            psi.Environment[k] = v;
        }

        foreach (var s in config.Secrets)
        {
            SecretRedactor.Register(s);
        }

        // Windows: kill-on-close job object. Android/Linux: the core dies with our process group / is killed on dispose.
        var job = OperatingSystem.IsWindows() ? new JobObject() : null;
        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var core = new CoreProcess(spec.Kind, process, job, logger, config.Secrets);
        process.OutputDataReceived += (_, e) => core.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => core.OnLine(e.Data);
        process.Exited += (_, _) => core.OnExited();

        try
        {
            if (!process.Start())
            {
                throw new CoreStartException("The core process did not start.", []);
            }

            core.ProcessId = process.Id;
            job?.Assign(process);
            if (background)
            {
                ProcessTuning.ForBackground(process, logger);
            }
            else
            {
                ProcessTuning.ForLowLatency(process, logger);
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.StandardInput.WriteAsync(config.Json.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await core.DisposeAsync().ConfigureAwait(false);
            throw new CoreStartException("Failed to start the core: " + SecretRedactor.Redact(ex.Message), core.RecentOutput);
        }
        catch
        {
            await core.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        logger.LogInformation("{Core} started (pid {Pid}).", spec.Kind, core.ProcessId);
        return core;
    }

    /// <summary>Waits until a loopback TCP port accepts connections, failing fast if the process exits.</summary>
    public async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (HasExited)
            {
                throw new CoreStartException($"{Kind} exited during startup (code {Exited.Result}): {LastError()}", RecentOutput);
            }

            using var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
                await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await Task.Delay(75, ct).ConfigureAwait(false);
        }

        throw new CoreStartException($"{Kind} did not open port {port} within {timeout.TotalSeconds:0.#}s. {LastError()}", RecentOutput);
    }

    /// <summary>The most relevant error line from recent output (already redacted).</summary>
    public string LastError()
    {
        var lines = RecentOutput;
        var err = lines.LastOrDefault(l => l.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("Failed", StringComparison.OrdinalIgnoreCase));
        return err ?? lines.LastOrDefault() ?? string.Empty;
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1 && HasExited)
        {
            return;
        }

        if (!HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogWarning("Could not kill {Core} (pid {Pid}): {Error}", Kind, ProcessId, ex.Message);
            }
        }

        await Task.WhenAny(_exited.Task, Task.Delay(timeout)).ConfigureAwait(false);
    }

    private void OnLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        var clean = SecretRedactor.Redact(AnsiRegex().Replace(line, string.Empty));
        lock (_ring)
        {
            _ring.Enqueue(clean);
            while (_ring.Count > RingSize)
            {
                _ring.Dequeue();
            }
        }

        if (clean.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || clean.Contains("ERROR", StringComparison.Ordinal))
        {
            _logger.LogWarning("[{Core}] {Line}", Kind, clean);
        }
        else
        {
            _logger.LogDebug("[{Core}] {Line}", Kind, clean);
        }

        OutputLine?.Invoke(this, clean);
    }

    private void OnExited()
    {
        int code;
        try
        {
            code = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        if (!StopRequested)
        {
            _logger.LogError("{Core} (pid {Pid}) exited unexpectedly with code {Code}. Last output: {Last}", Kind, ProcessId, code, LastError());
        }

        _exited.TrySetResult(code);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _process.Dispose();
        _job?.Dispose();
        foreach (var s in _secrets)
        {
            SecretRedactor.Unregister(s);
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}
