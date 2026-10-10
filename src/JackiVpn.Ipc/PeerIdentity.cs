using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using JackiVpn.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JackiVpn.Ipc;

public sealed record IpcClientContext(int ProcessId, string? ImagePath, SecurityIdentifier? UserSid, int SessionId);

/// <summary>Decides whether a connected pipe client may talk to the service.</summary>
public interface IClientAuthorizer
{
    (bool Allowed, string Reason) Authorize(IpcClientContext client);
}

/// <summary>Native helpers to identify the process on the other end of a pipe.</summary>
public static partial class PeerIdentity
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static int ClientProcessId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : -1;
    }

    public static int ClientSessionId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sid) ? (int)sid : -1;
    }

    public static int ServerProcessId(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : -1;
    }

    public static string? ImagePath(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    /// <summary>The Windows user of the pipe client (impersonation, query only).</summary>
    public static SecurityIdentifier? ClientUser(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        SecurityIdentifier? sid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                sid = identity.User;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Anonymous/limited impersonation or a vanished client: identity unknown → caller rejects.
            sid = null;
        }

        return sid;
    }

    public static IpcClientContext Describe(NamedPipeServerStream pipe)
    {
        var pid = ClientProcessId(pipe);
        return new IpcClientContext(pid, ImagePath(pid), ClientUser(pipe), ClientSessionId(pipe));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] buffer, ref uint size);
}

/// <summary>
/// Production authorizer: the client executable must be exactly the Desktop binary registered by the
/// (elevated) installer in HKLM — same full path and same SHA-256 — and must run as a real local user
/// (not anonymous, not a network logon). HKLM is writable only by administrators, so a normal process
/// cannot register itself.
/// </summary>
public sealed class TrustedImageAuthorizer : IClientAuthorizer
{
    public const string TrustedPathValue = "TrustedClientPath";
    public const string TrustedHashValue = "TrustedClientSha256";

    /// <summary>
    /// SHA-256 of the client's main assembly (<c>JackiVpn.dll</c> next to the exe). A framework-dependent
    /// .exe is only a launcher stub, so the code that actually runs must be pinned too.
    /// </summary>
    public const string TrustedAssemblyHashValue = "TrustedClientAssemblySha256";

    private readonly Func<(string? Path, string? Sha256)> _trusted;
    private readonly Func<string?> _trustedAssembly;
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<string, (long Length, DateTime Write, string Hash)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TrustedImageAuthorizer(Func<(string? Path, string? Sha256)>? trusted = null, Func<string?>? trustedAssemblySha256 = null)
    {
        _trusted = trusted ?? ReadRegistry;
        _trustedAssembly = trustedAssemblySha256 ?? (trusted is null ? ReadAssemblyRegistry : () => null);
    }

    public (bool Allowed, string Reason) Authorize(IpcClientContext client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.UserSid is null || client.UserSid.IsWellKnown(WellKnownSidType.AnonymousSid) || client.UserSid.IsWellKnown(WellKnownSidType.NetworkSid))
        {
            return (false, "Client identity could not be established.");
        }

        var (trustedPath, trustedHash) = _trusted();
        if (string.IsNullOrEmpty(trustedPath) || string.IsNullOrEmpty(trustedHash))
        {
            return (false, "No trusted client is registered; reinstall/repair the service.");
        }

        if (client.ImagePath is null
            || !string.Equals(Path.GetFullPath(client.ImagePath), Path.GetFullPath(trustedPath), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Client executable is not the registered application.");
        }

        if (!FileIntegrity.DigestEquals(HashCached(client.ImagePath), trustedHash))
        {
            return (false, "Client executable hash does not match the registered application (rebuilt or tampered? run Repair Service).");
        }

        if (_trustedAssembly() is { Length: > 0 } assemblyHash)
        {
            var assembly = Path.ChangeExtension(client.ImagePath, ".dll");
            if (!File.Exists(assembly) || !FileIntegrity.DigestEquals(HashCached(assembly), assemblyHash))
            {
                return (false, "Client assembly does not match the registered application (updated or tampered? run Repair Service).");
            }
        }

        return (true, "Trusted client.");
    }

    private string HashCached(string path)
    {
        var fi = new FileInfo(path);
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(path, out var c) && c.Length == fi.Length && c.Write == fi.LastWriteTimeUtc)
            {
                return c.Hash;
            }
        }

        var hash = FileIntegrity.ComputeSha256(path);
        lock (_cacheGate)
        {
            _cache[path] = (fi.Length, fi.LastWriteTimeUtc, hash);
        }

        return hash;
    }

    private static (string? Path, string? Sha256) ReadRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Domain.ProductInfo.ServiceRegistryKey);
        return (key?.GetValue(TrustedPathValue) as string, key?.GetValue(TrustedHashValue) as string);
    }

    private static string? ReadAssemblyRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Domain.ProductInfo.ServiceRegistryKey);
        return key?.GetValue(TrustedAssemblyHashValue) as string;
    }
}

/// <summary>Test-only authorizer: accepts clients running as the same user as the server.</summary>
public sealed class SameUserAuthorizer : IClientAuthorizer
{
    private readonly SecurityIdentifier _me = WindowsIdentity.GetCurrent().User!;

    public (bool Allowed, string Reason) Authorize(IpcClientContext client) =>
        client?.UserSid == _me ? (true, "same user") : (false, "different user");
}

internal static class SidText
{
    public static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return "<unknown>";
        }

        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
        catch (SystemException)
        {
            return sid.Value;
        }
    }
}
