using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace JackiVpn.Networking.Probes;

/// <summary>A program that listens on a local TCP port.</summary>
public sealed record PortOwnerInfo(int ProcessId, string ProcessName, string? Path)
{
    /// <summary>Known proxy/VPN apps by their process name, with where their port setting lives.</summary>
    private static readonly (string Match, string Name, string Hint)[] Known =
    [
        ("karing", "Karing", "Karing › Settings › Advanced › port"),
        ("v2rayn", "v2rayN", "v2rayN › Settings › Option settings › Core: basic settings › local port"),
        ("nekoray", "NekoRay", "NekoRay › Preferences › Basic settings › port"),
        ("nekobox", "NekoBox", "NekoBox › Preferences › Basic settings › port"),
        ("clash-verge", "Clash Verge", "Clash Verge › Settings › Port config"),
        ("verge-mihomo", "Clash Verge (mihomo)", "Clash Verge › Settings › Port config"),
        ("mihomo", "mihomo / Clash Meta", "its config file (mixed-port)"),
        ("clash", "Clash", "Clash › Settings › port"),
        ("hiddify", "Hiddify", "Hiddify › Settings › Options › mixed port"),
        ("furious", "Furious", "Furious › Settings › port"),
        ("invisible", "Invisible Man XRay", "its settings"),
        ("psiphon", "Psiphon", "Psiphon › Settings › Local proxy ports"),
        ("outline", "Outline", "Outline (quit it from the tray)"),
        ("windscribe", "Windscribe", "Windscribe › Preferences › Connection"),
        ("xray", "an Xray core of another app", "the app that started it"),
        ("sing-box", "a sing-box core of another app", "the app that started it"),
        ("v2ray", "a V2Ray core of another app", "the app that started it"),
    ];

    /// <summary>A core started by IBOX itself (an earlier session that did not close in time).</summary>
    public bool IsIbox => Path is { } p && (p.Contains("JackiVpn", StringComparison.OrdinalIgnoreCase) || p.Contains("Jacki VPN", StringComparison.OrdinalIgnoreCase));

    /// <summary>"v2rayN (v2rayN.exe, process 4120)".</summary>
    public string Describe()
    {
        if (IsIbox)
        {
            return $"an earlier IBOX session (process {ProcessId})";
        }

        var known = Find();
        var file = Path is { } p ? System.IO.Path.GetFileName(p) : ProcessName;
        return known is { } k ? $"{k.Name} ({file}, process {ProcessId})" : $"{ProcessName} ({file}, process {ProcessId})";
    }

    /// <summary>What the user can do to free the port.</summary>
    public string HowToFree(int port)
    {
        if (IsIbox)
        {
            return "It is closed automatically on the next start; disconnect and connect again to get the usual port back.";
        }

        var known = Find();
        var close = OperatingSystem.IsWindows()
            ? $"close it (tray icon › Exit, or Task Manager › {ProcessName} › End task)"
            : $"close it (or: kill {ProcessId})";
        return known is { } k
            ? $"To free port {port}: {close}, or change its port in {k.Hint}. Or pick another port for IBOX in Network › Local port."
            : $"To free port {port}: {close}, or change the port it uses. Or pick another port for IBOX in Network › Local port.";
    }

    private (string Match, string Name, string Hint)? Find()
    {
        var name = ProcessName.ToLowerInvariant();
        foreach (var k in Known)
        {
            if (name.Contains(k.Match, StringComparison.Ordinal))
            {
                return k;
            }
        }

        return null;
    }
}

/// <summary>
/// Which program listens on a local TCP port: the owning process from the TCP listener table on Windows
/// (GetExtendedTcpTable), from /proc (socket inode → process) on Linux. Read-only; null when unknown.
/// </summary>
public static partial class PortOwner
{
    public static PortOwnerInfo? Find(int port)
    {
        try
        {
            var pid = OperatingSystem.IsWindows() ? FindWindows(port) : OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? FindLinux(port) : null;
            return pid is { } id ? Describe(id) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static PortOwnerInfo? Describe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string? path = null;
            try
            {
                path = p.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Elevated or protected process: the name is still known.
            }

            if (path is null && OperatingSystem.IsLinux())
            {
                try
                {
                    path = new FileInfo($"/proc/{pid}/exe").LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return new PortOwnerInfo(pid, p.ProcessName, path);
        }
        catch (ArgumentException)
        {
            return null; // exited meanwhile
        }
    }

    // ------------------------------------------------------------------ Windows

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    private static int? FindWindows(int port)
    {
        foreach (var (family, rowSize, portOffset, pidOffset) in new[] { (AfInet, 24, 8, 20), (AfInet6, 56, 20, 52) })
        {
            var size = 0;
            GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);
            if (size <= 0)
            {
                continue;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
                {
                    continue;
                }

                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + (i * rowSize);
                    var raw = Marshal.ReadInt32(row + portOffset);
                    var rowPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); // network byte order
                    if (rowPort == port)
                    {
                        return Marshal.ReadInt32(row + pidOffset);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ Linux

    private static int? FindLinux(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table))
            {
                continue;
            }

            foreach (var line in File.ReadLines(table).Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // f[1] local "ADDR:PORT" (hex), f[3] state (0A = LISTEN), f[9] inode.
                if (f.Length > 9 && f[3] == "0A" && f[1].LastIndexOf(':') is var colon and > 0
                    && int.TryParse(f[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) && p == port)
                {
                    inodes.Add(f[9]);
                }
            }
        }

        if (inodes.Count == 0)
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(System.IO.Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                foreach (var fd in Directory.EnumerateFiles(System.IO.Path.Combine(dir, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget is { } target && target.StartsWith("socket:[", StringComparison.Ordinal)
                        && inodes.Contains(target[8..^1]))
                    {
                        return pid;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process: not readable without root.
            }
        }

        return null;
    }
}
