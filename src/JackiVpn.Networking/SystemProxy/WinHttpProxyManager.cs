using System.ComponentModel;
using System.Runtime.InteropServices;
using JackiVpn.Networking.Journal;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Networking.SystemProxy;

public sealed record WinHttpProxyConfig(int AccessType, string? Proxy, string? Bypass)
{
    public const int DefaultProxy = 0;
    public const int NoProxy = 1;
    public const int NamedProxy = 3;
}

public interface IWinHttpProxyApi
{
    WinHttpProxyConfig Get();

    void Set(WinHttpProxyConfig config);
}

/// <summary>Machine-wide WinHTTP default proxy (requires administrator; used by the service only on opt-in).</summary>
public sealed partial class WinHttpProxyApi : IWinHttpProxyApi
{
    public WinHttpProxyConfig Get()
    {
        if (!WinHttpGetDefaultProxyConfiguration(out var info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WinHttpGetDefaultProxyConfiguration failed.");
        }

        try
        {
            return new WinHttpProxyConfig((int)info.dwAccessType, PtrToString(info.lpszProxy), PtrToString(info.lpszProxyBypass));
        }
        finally
        {
            if (info.lpszProxy != IntPtr.Zero)
            {
                GlobalFree(info.lpszProxy);
            }

            if (info.lpszProxyBypass != IntPtr.Zero)
            {
                GlobalFree(info.lpszProxyBypass);
            }
        }
    }

    public void Set(WinHttpProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var proxy = config.Proxy is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(config.Proxy);
        var bypass = config.Bypass is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(config.Bypass);
        try
        {
            var info = new WINHTTP_PROXY_INFO { dwAccessType = (uint)config.AccessType, lpszProxy = proxy, lpszProxyBypass = bypass };
            if (!WinHttpSetDefaultProxyConfiguration(ref info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "WinHttpSetDefaultProxyConfiguration failed (administrator required).");
            }
        }
        finally
        {
            if (proxy != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(proxy);
            }

            if (bypass != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(bypass);
            }
        }
    }

    private static string? PtrToString(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_PROXY_INFO
    {
        public uint dwAccessType;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpGetDefaultProxyConfiguration(out WINHTTP_PROXY_INFO pProxyInfo);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpSetDefaultProxyConfiguration(ref WINHTTP_PROXY_INFO pProxyInfo);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr hMem);
}

public sealed class WinHttpProxyManager(IWinHttpProxyApi api, NetworkStateJournal journal, ILogger logger)
{
    public const string JournalId = "winhttp-proxy";

    public void Apply(int port, string bypass)
    {
        var existing = journal.OfKind(JournalEntryKind.WinHttpProxy).FirstOrDefault();
        var previous = existing?.As<WinHttpProxyConfig>() ?? api.Get();
        journal.Record(JournalEntryKind.WinHttpProxy, previous, JournalId);
        api.Set(new WinHttpProxyConfig(WinHttpProxyConfig.NamedProxy, SystemProxyManager.ProxyServerFor(port), bypass.Replace(';', ' ')));
        logger.LogInformation("WinHTTP proxy set to 127.0.0.1:{Port}.", port);
    }

    public bool Restore()
    {
        var entry = journal.OfKind(JournalEntryKind.WinHttpProxy).FirstOrDefault();
        if (entry is null)
        {
            return false;
        }

        api.Set(entry.As<WinHttpProxyConfig>());
        journal.Complete(entry.Id);
        logger.LogInformation("WinHTTP proxy restored.");
        return true;
    }
}
