using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Domain;
using JackiVpn.Ipc;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Desktop.Services;

public enum ServiceAvailability
{
    Unknown = 0,
    NotInstalled,
    Stopped,
    Running,

    /// <summary>Running, but it refused this app (e.g. app updated without re-registering): run Repair.</summary>
    Rejected,
}

/// <summary>
/// Desktop side of the privileged service: state queries (via the SCM, no admin needed), IPC clients, and
/// install / repair / uninstall through a UAC-elevated child process of the service executable.
/// </summary>
public sealed partial class ServiceControl(ILogger logger) : IAsyncDisposable
{
    private const uint SC_MANAGER_CONNECT = 0x1;
    private const uint SERVICE_QUERY_STATUS = 0x4;
    private const uint SERVICE_RUNNING = 4;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_CANCELLED = 1223;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IpcClient? _client;

    public static string ClientVersion => typeof(ServiceControl).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string ServiceExecutablePath => Path.Combine(AppContext.BaseDirectory, "IBOX.Service.exe");

    public static bool ServiceExecutableAvailable => File.Exists(ServiceExecutablePath);

    public ServiceAvailability LastKnown { get; private set; } = ServiceAvailability.Unknown;

    public string? LastError { get; private set; }

    public unsafe ServiceAvailability QueryScm()
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return ServiceAvailability.Unknown;
        }

        try
        {
            var svc = OpenServiceW(scm, ProductInfo.ServiceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                return Marshal.GetLastPInvokeError() == ERROR_SERVICE_DOES_NOT_EXIST ? ServiceAvailability.NotInstalled : ServiceAvailability.Unknown;
            }

            try
            {
                var status = stackalloc uint[7];
                return QueryServiceStatus(svc, status) && status[1] == SERVICE_RUNNING ? ServiceAvailability.Running : ServiceAvailability.Stopped;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>Checks SCM state and, when running, that the service accepts this app.</summary>
    public async Task<ServiceAvailability> RefreshAsync(CancellationToken ct)
    {
        var scm = QueryScm();
        if (scm != ServiceAvailability.Running)
        {
            LastKnown = scm;
            return scm;
        }

        try
        {
            await GetClientAsync(ct).ConfigureAwait(false);
            LastKnown = ServiceAvailability.Running;
            LastError = null;
        }
        catch (IpcRemoteException ex) when (ex.Code is IpcErrorCodes.Unauthorized or IpcErrorCodes.VersionMismatch)
        {
            LastKnown = ServiceAvailability.Rejected;
            LastError = ex.Message;
        }
        catch (ServiceUnavailableException ex)
        {
            LastKnown = ex.InnerException is IpcRemoteException ? ServiceAvailability.Rejected : ServiceAvailability.Stopped;
            LastError = ex.Message;
        }

        return LastKnown;
    }

    /// <summary>A shared, lazily (re)connected client for management calls.</summary>
    public async Task<IpcClient> GetClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true })
            {
                return _client;
            }

            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }

            _client = await ConnectNewClientAsync(ct).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The current management client if connected (never blocks).</summary>
    public IpcClient? ConnectedClient => _client is { IsConnected: true } c ? c : null;

    public async Task<IpcClient> ConnectNewClientAsync(CancellationToken ct)
    {
        var client = new IpcClient(new IpcClientOptions { ClientVersion = ClientVersion });
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<(bool Success, string Output)> InstallOrRepairAsync(bool developerLocation) =>
        RunElevatedAsync(developerLocation
            ? ["install", "--client", Environment.ProcessPath ?? string.Empty, "--dev"]
            : ["install", "--client", Environment.ProcessPath ?? string.Empty]);

    public Task<(bool Success, string Output)> UninstallAsync() => RunElevatedAsync(["uninstall"]);

    private async Task<(bool Success, string Output)> RunElevatedAsync(string[] args)
    {
        if (!ServiceExecutableAvailable)
        {
            return (false, $"IBOX.Service.exe was not found next to the app ({AppContext.BaseDirectory}). Use the packaged build (scripts\\build.ps1).");
        }

        var resultFile = Path.Combine(Path.GetTempPath(), $"jackivpn-svc-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo(ServiceExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add("--result");
        psi.ArgumentList.Add(resultFile);

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "The installer could not be started.");
            }

            await p.WaitForExitAsync().ConfigureAwait(false);
            var output = File.Exists(resultFile) ? await File.ReadAllTextAsync(resultFile).ConfigureAwait(false) : string.Empty;
            logger.LogInformation("Service {Action} exited with {Code}.", args[0], p.ExitCode);
            return (p.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? $"Exit code {p.ExitCode}." : output.Trim());
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return (false, "The administrator prompt was cancelled.");
        }
        finally
        {
            try
            {
                File.Delete(resultFile);
            }
            catch (IOException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string serviceName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceStatus(IntPtr service, uint* status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
