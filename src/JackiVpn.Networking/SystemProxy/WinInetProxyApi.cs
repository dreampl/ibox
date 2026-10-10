using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace JackiVpn.Networking.SystemProxy;

/// <summary>Exact WinINET per-connection proxy state (what the Windows proxy settings UI shows).</summary>
public sealed record ProxySnapshot
{
    public const int FlagDirect = 0x1;
    public const int FlagProxy = 0x2;
    public const int FlagAutoProxyUrl = 0x4;
    public const int FlagAutoDetect = 0x8;

    /// <summary>Null = the LAN / default connection; otherwise a RAS (dial-up/VPN) entry name.</summary>
    public string? ConnectionName { get; init; }

    public int Flags { get; init; } = FlagDirect;
    public string? ProxyServer { get; init; }
    public string? ProxyBypass { get; init; }
    public string? AutoConfigUrl { get; init; }

    [JsonIgnore]
    public bool ProxyEnabled => (Flags & FlagProxy) != 0;

    [JsonIgnore]
    public bool PacEnabled => (Flags & FlagAutoProxyUrl) != 0;

    [JsonIgnore]
    public bool AutoDetect => (Flags & FlagAutoDetect) != 0;
}

/// <summary>Abstraction over the WinINET per-connection proxy API (fakeable for tests).</summary>
public interface IWinInetProxyApi
{
    ProxySnapshot Query(string? connectionName);

    void Apply(ProxySnapshot snapshot);

    /// <summary>Tells running applications that the proxy settings changed.</summary>
    void NotifyChanged();

    IReadOnlyList<string> EnumerateRasConnections();
}

/// <summary>
/// Real implementation using <c>InternetQueryOption</c>/<c>InternetSetOption</c> with
/// <c>INTERNET_OPTION_PER_CONNECTION_OPTION</c> — the supported API, rather than poking the registry,
/// so the change is picked up by WinINET clients and the Settings app consistently.
/// </summary>
public sealed partial class WinInetProxyApi : IWinInetProxyApi
{
    private const int INTERNET_OPTION_REFRESH = 37;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
    private const int INTERNET_OPTION_PROXY_SETTINGS_CHANGED = 95;
    private const int INTERNET_PER_CONN_FLAGS = 1;
    private const int INTERNET_PER_CONN_PROXY_SERVER = 2;
    private const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
    private const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;
    private const int INTERNET_PER_CONN_FLAGS_UI = 10;

    public ProxySnapshot Query(string? connectionName)
    {
        var flags = QueryFlags(connectionName, INTERNET_PER_CONN_FLAGS_UI) ?? QueryFlags(connectionName, INTERNET_PER_CONN_FLAGS) ?? ProxySnapshot.FlagDirect;
        return new ProxySnapshot
        {
            ConnectionName = connectionName,
            Flags = flags,
            ProxyServer = QueryString(connectionName, INTERNET_PER_CONN_PROXY_SERVER),
            ProxyBypass = QueryString(connectionName, INTERNET_PER_CONN_PROXY_BYPASS),
            AutoConfigUrl = QueryString(connectionName, INTERNET_PER_CONN_AUTOCONFIG_URL),
        };
    }

    public void Apply(ProxySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var strings = new List<IntPtr>();
        var options = new INTERNET_PER_CONN_OPTION[4];
        try
        {
            options[0] = new INTERNET_PER_CONN_OPTION { dwOption = INTERNET_PER_CONN_FLAGS, Value = new OptionValue { dwValue = snapshot.Flags } };
            options[1] = StringOption(INTERNET_PER_CONN_PROXY_SERVER, snapshot.ProxyServer, strings);
            options[2] = StringOption(INTERNET_PER_CONN_PROXY_BYPASS, snapshot.ProxyBypass, strings);
            options[3] = StringOption(INTERNET_PER_CONN_AUTOCONFIG_URL, snapshot.AutoConfigUrl, strings);
            SetOptions(snapshot.ConnectionName, options);
        }
        finally
        {
            foreach (var p in strings)
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }

    public void NotifyChanged()
    {
        InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_PROXY_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    public IReadOnlyList<string> EnumerateRasConnections()
    {
        var entrySize = Marshal.SizeOf<RASENTRYNAME>();
        var count = 16;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bufferSize = entrySize * count;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                Marshal.WriteInt32(buffer, entrySize);
                var cb = bufferSize;
                var rc = RasEnumEntriesW(IntPtr.Zero, IntPtr.Zero, buffer, ref cb, out var entries);
                if (rc == 603 /* ERROR_BUFFER_TOO_SMALL */)
                {
                    count = Math.Max(count * 2, (cb / entrySize) + 1);
                    continue;
                }

                if (rc != 0)
                {
                    return [];
                }

                var names = new List<string>(entries);
                for (var i = 0; i < entries; i++)
                {
                    var e = Marshal.PtrToStructure<RASENTRYNAME>(buffer + (i * entrySize));
                    if (!string.IsNullOrWhiteSpace(e.szEntryName))
                    {
                        names.Add(e.szEntryName);
                    }
                }

                return names;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private static int? QueryFlags(string? connection, int option)
    {
        var opts = new INTERNET_PER_CONN_OPTION[] { new() { dwOption = option } };
        return QueryOptions(connection, opts) ? opts[0].Value.dwValue : null;
    }

    private static string? QueryString(string? connection, int option)
    {
        var opts = new INTERNET_PER_CONN_OPTION[] { new() { dwOption = option } };
        if (!QueryOptions(connection, opts))
        {
            return null;
        }

        var ptr = opts[0].Value.pszValue;
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var s = Marshal.PtrToStringUni(ptr);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            GlobalFree(ptr);
        }
    }

    private static unsafe bool QueryOptions(string? connection, INTERNET_PER_CONN_OPTION[] options)
    {
        var connPtr = connection is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(connection);
        try
        {
            fixed (INTERNET_PER_CONN_OPTION* pOptions = options)
            {
                var list = new INTERNET_PER_CONN_OPTION_LIST
                {
                    dwSize = (uint)sizeof(INTERNET_PER_CONN_OPTION_LIST),
                    pszConnection = connPtr,
                    dwOptionCount = (uint)options.Length,
                    pOptions = (IntPtr)pOptions,
                };
                var size = (uint)sizeof(INTERNET_PER_CONN_OPTION_LIST);
                return InternetQueryOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, (IntPtr)(&list), ref size);
            }
        }
        finally
        {
            if (connPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(connPtr);
            }
        }
    }

    private static unsafe void SetOptions(string? connection, INTERNET_PER_CONN_OPTION[] options)
    {
        var connPtr = connection is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(connection);
        try
        {
            fixed (INTERNET_PER_CONN_OPTION* pOptions = options)
            {
                var list = new INTERNET_PER_CONN_OPTION_LIST
                {
                    dwSize = (uint)sizeof(INTERNET_PER_CONN_OPTION_LIST),
                    pszConnection = connPtr,
                    dwOptionCount = (uint)options.Length,
                    pOptions = (IntPtr)pOptions,
                };
                if (!InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, (IntPtr)(&list), (uint)sizeof(INTERNET_PER_CONN_OPTION_LIST)))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "InternetSetOption(PER_CONNECTION_OPTION) failed.");
                }
            }
        }
        finally
        {
            if (connPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(connPtr);
            }
        }
    }

    private static INTERNET_PER_CONN_OPTION StringOption(int option, string? value, List<IntPtr> owned)
    {
        var ptr = value is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(value);
        if (ptr != IntPtr.Zero)
        {
            owned.Add(ptr);
        }

        return new INTERNET_PER_CONN_OPTION { dwOption = option, Value = new OptionValue { pszValue = ptr } };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION_LIST
    {
        public uint dwSize;
        public IntPtr pszConnection;
        public uint dwOptionCount;
        public uint dwOptionError;
        public IntPtr pOptions;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct OptionValue
    {
        [FieldOffset(0)]
        public int dwValue;

        [FieldOffset(0)]
        public IntPtr pszValue;

        [FieldOffset(0)]
        public long ftValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION
    {
        public int dwOption;
        public OptionValue Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RASENTRYNAME
    {
        public int dwSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szEntryName;

        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)]
        public string szPhonebookPath;
    }

    [LibraryImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOptionW(IntPtr hInternet, int dwOption, IntPtr lpBuffer, uint dwBufferLength);

    [LibraryImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetQueryOptionW(IntPtr hInternet, int dwOption, IntPtr lpBuffer, ref uint lpdwBufferLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr hMem);

    [LibraryImport("rasapi32.dll")]
    private static partial int RasEnumEntriesW(IntPtr reserved, IntPtr lpszPhonebook, IntPtr lprasentryname, ref int lpcb, out int lpcEntries);
}
