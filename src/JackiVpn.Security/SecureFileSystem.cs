#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
#endif
using System.Text;

namespace JackiVpn.Security;

public enum PrivateScope
{
    /// <summary>Current user + SYSTEM + Administrators.</summary>
    CurrentUser,

    /// <summary>SYSTEM + Administrators only (service state).</summary>
    ServiceOnly,
}

/// <summary>Directories/files with explicit, non-inherited ACLs and crash-safe writes.</summary>
public static class SecureFileSystem
{
#if !WINDOWS
    /// <summary>
    /// Android/Linux: the app's own data directory is already private to the app's UID (sandbox), so a plain
    /// directory is equivalent to the explicit Windows ACL.
    /// </summary>
    public static DirectoryInfo CreatePrivateDirectory(string path, PrivateScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _ = scope;
        return Directory.CreateDirectory(path);
    }
#else
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static DirectoryInfo CreatePrivateDirectory(string path, PrivateScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (scope == PrivateScope.CurrentUser)
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Cannot determine current user SID.");
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            dir = FileSystemAclExtensions.CreateDirectory(security, path);
        }
        else
        {
            try
            {
                dir.SetAccessControl(security);
            }
            catch (UnauthorizedAccessException)
            {
                // Not the owner (e.g. pre-existing dir created elevated). Keep existing ACL; caller decides.
            }
        }

        return dir;
    }
#endif

    /// <summary>Writes via a temp file in the same directory and an atomic replace (no torn files on crash).</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    /// <summary>Best-effort overwrite with zeros, then delete. Used for any file that ever held credentials.</summary>
    public static void SecureDelete(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                return;
            }

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[Math.Min(fi.Length, 64 * 1024)];
                long written = 0;
                while (written < fi.Length)
                {
                    var n = (int)Math.Min(zeros.Length, fi.Length - written);
                    fs.Write(zeros, 0, n);
                    written += n;
                }

                fs.Flush(flushToDisk: true);
            }

            File.Delete(path);
        }
        catch (IOException)
        {
            TryDelete(path);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
