using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using JackiVpn.Domain;
using JackiVpn.Ipc;
using JackiVpn.Security;
using Microsoft.Win32;

namespace JackiVpn.Service;

public sealed class InstallException(string message) : Exception(message);

/// <summary>
/// Install / repair / uninstall through the Service Control Manager (no sc.exe string building).
/// A LocalSystem service must never run from a location ordinary users can write to — that would let any
/// user replace the binary (or a core it launches) and gain SYSTEM. Installation refuses such locations.
/// </summary>
public static unsafe partial class ServiceInstaller
{
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const uint SERVICE_AUTO_START = 0x2;
    private const uint SERVICE_ERROR_NORMAL = 0x1;
    private const uint SERVICE_CONTROL_STOP = 0x1;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
    private const uint SERVICE_STOPPED = 1;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int SC_ACTION_RESTART = 1;

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static IReadOnlyList<string> Install(string serviceExe, string clientExe, bool allowInsecureLocation, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!IsElevated())
        {
            throw new InstallException("Administrator rights are required (run from an elevated prompt or via the app's Install Service button).");
        }

        serviceExe = Path.GetFullPath(serviceExe);
        clientExe = Path.GetFullPath(clientExe);
        if (!File.Exists(clientExe))
        {
            throw new InstallException($"Client executable not found: {clientExe}");
        }

        var warnings = new List<string>();
        var installDir = Path.GetDirectoryName(serviceExe)!;
        var insecure = FindUserWritable([installDir, serviceExe, Path.Combine(installDir, "cores"), clientExe, Path.ChangeExtension(clientExe, ".dll")]);
        if (insecure.Count > 0)
        {
            var msg = "These locations are writable by non-administrators: " + string.Join("; ", insecure)
                + ". A SYSTEM service must be installed in a protected folder such as Program Files.";
            if (!allowInsecureLocation)
            {
                throw new InstallException(msg);
            }

            warnings.Add("WARNING (developer install): " + msg);
        }

        var clientHash = FileIntegrity.ComputeSha256(clientExe);
        var clientAssembly = Path.ChangeExtension(clientExe, ".dll");
        using (var key = Registry.LocalMachine.CreateSubKey(ProductInfo.ServiceRegistryKey, writable: true))
        {
            key.SetValue(TrustedImageAuthorizer.TrustedPathValue, clientExe, RegistryValueKind.String);
            key.SetValue(TrustedImageAuthorizer.TrustedHashValue, clientHash, RegistryValueKind.String);
            if (File.Exists(clientAssembly))
            {
                // Framework-dependent app: the exe is a launcher; pin the assembly that actually runs.
                key.SetValue(TrustedImageAuthorizer.TrustedAssemblyHashValue, FileIntegrity.ComputeSha256(clientAssembly), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(TrustedImageAuthorizer.TrustedAssemblyHashValue, throwOnMissingValue: false);
            }

            key.SetValue("InstallRoot", installDir, RegistryValueKind.String);
        }

        log($"Trusted client registered: {clientExe} (sha256 {clientHash[..16]}…).");

        var scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenSCManager failed");
        }

        try
        {
            var binPath = "\"" + serviceExe + "\"";
            var svc = OpenServiceW(scm, ProductInfo.ServiceName, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                svc = CreateServiceW(scm, ProductInfo.ServiceName, ProductInfo.ServiceDisplayName, SERVICE_ALL_ACCESS,
                    SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, binPath, null, IntPtr.Zero, null, null, null);
                if (svc == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateService failed");
                }

                log("Service created.");
            }
            else
            {
                StopAndWait(svc, log);
                if (!ChangeServiceConfigW(svc, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, binPath, null, IntPtr.Zero, null, null, null, ProductInfo.ServiceDisplayName))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "ChangeServiceConfig failed");
                }

                log("Existing service updated.");
            }

            try
            {
                Configure(svc);
                if (!StartServiceW(svc, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != ERROR_SERVICE_ALREADY_RUNNING)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "StartService failed");
                }

                log("Service started.");
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

        return warnings;
    }

    public static void Uninstall(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!IsElevated())
        {
            throw new InstallException("Administrator rights are required.");
        }

        var scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenSCManager failed");
        }

        try
        {
            var svc = OpenServiceW(scm, ProductInfo.ServiceName, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                if (Marshal.GetLastPInvokeError() != ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenService failed");
                }

                log("Service is not installed.");
            }
            else
            {
                try
                {
                    // Stopping makes the service tear down the tunnel and remove its kill-switch filters.
                    StopAndWait(svc, log);
                    if (!DeleteService(svc))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "DeleteService failed");
                    }

                    log("Service removed.");
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }

        Registry.LocalMachine.DeleteSubKeyTree(ProductInfo.ServiceRegistryKey, throwOnMissingSubKey: false);
        log("Trusted client registration removed.");
    }

    private static void StopAndWait(IntPtr svc, Action<string> log)
    {
        SERVICE_STATUS status;
        if (!ControlService(svc, SERVICE_CONTROL_STOP, &status))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err != ERROR_SERVICE_NOT_ACTIVE)
            {
                throw new Win32Exception(err, "Stopping the service failed");
            }

            return;
        }

        log("Stopping service…");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (QueryServiceStatus(svc, &status) && status.dwCurrentState == SERVICE_STOPPED)
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new InstallException("The service did not stop within 45 seconds.");
    }

    private static void Configure(IntPtr svc)
    {
        fixed (char* desc = "Privileged helper for IBOX: TUN tunnel, kill switch and network recovery. Talks only to the registered IBOX app.")
        {
            var d = new SERVICE_DESCRIPTION { lpDescription = desc };
            ChangeServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, &d);
        }

        // Restart after crashes (5s, 10s, 30s; counter resets daily). Startup recovery cleans up after each crash.
        var actions = stackalloc SC_ACTION[3];
        actions[0] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 5_000 };
        actions[1] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 10_000 };
        actions[2] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 30_000 };
        var fa = new SERVICE_FAILURE_ACTIONS { dwResetPeriod = 86_400, cActions = 3, lpsaActions = actions };
        if (!ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, &fa))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Configuring failure actions failed");
        }

        var flag = new SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = 1 };
        ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, &flag);
    }

    /// <summary>
    /// Returns the paths whose DACL (or ownership) lets a non-administrative principal modify them.
    /// Trusted principals: SYSTEM, Administrators, TrustedInstaller, CREATOR OWNER (resolved by owner check).
    /// </summary>
    public static IReadOnlyList<string> FindUserWritable(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        const FileSystemRights writeRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var trusted = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
        };

        var result = new List<string>();
        foreach (var path in paths)
        {
            FileSystemSecurity? security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : File.Exists(path) ? new FileInfo(path).GetAccessControl() : null;
            if (security is null)
            {
                continue;
            }

            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && !trusted.Contains(owner))
            {
                result.Add($"{path} (owned by {owner.Value})");
                continue;
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow
                    && rule.IdentityReference is SecurityIdentifier sid
                    && !trusted.Contains(sid)
                    && (rule.FileSystemRights & writeRights) != 0
                    && !(rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)))
                {
                    result.Add($"{path} ({sid.Value} can modify)");
                    break;
                }
            }
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DESCRIPTION
    {
        public char* lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public char* lpRebootMsg;
        public char* lpCommand;
        public uint cActions;
        public SC_ACTION* lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS_FLAG
    {
        public int fFailureActionsOnNonCrashFailures;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string serviceName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateServiceW(IntPtr scm, string serviceName, string displayName, uint access, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, void* info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(IntPtr service, uint argCount, IntPtr args);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, SERVICE_STATUS* status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr service, SERVICE_STATUS* status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(IntPtr service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
