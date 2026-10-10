using Android.Runtime;

namespace JackiVpn.Mobile.Platforms.Android;

/// <summary>
/// JNI bridge to hev-socks5-tunnel (MIT): reads IP packets from the VpnService TUN descriptor in-process and
/// forwards TCP and UDP (incl. DNS) to the local SOCKS5 inbound of the sing-box core.
/// Java API: <c>hev.htproxy.TProxyService.TProxyStartService(String configPath, int fd)</c> etc.
/// </summary>
internal static class HevTunnel
{
    private static readonly Lock Gate = new();
    private static IntPtr _class;
    private static IntPtr _start;
    private static IntPtr _stop;
    private static IntPtr _running;
    private static IntPtr _stats;

    private static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_class != IntPtr.Zero)
            {
                return;
            }

            // The app class loader (not the system one) knows classes from bundled AARs.
            using var cls = global::Android.App.Application.Context.ClassLoader!.LoadClass("hev.htproxy.TProxyService")
                ?? throw new InvalidOperationException("hev-socks5-tunnel is not packaged.");
            _class = JNIEnv.NewGlobalRef(cls.Handle);
            _start = JNIEnv.GetStaticMethodID(_class, "TProxyStartService", "(Ljava/lang/String;I)Z");
            _stop = JNIEnv.GetStaticMethodID(_class, "TProxyStopService", "()Z");
            _running = JNIEnv.GetStaticMethodID(_class, "TProxyIsRunning", "()Z");
            _stats = JNIEnv.GetStaticMethodID(_class, "TProxyGetStats", "()[J");
        }
    }

    public static bool Start(string configPath, int tunFd)
    {
        EnsureInitialized();
        var path = JNIEnv.NewString(configPath);
        try
        {
            return JNIEnv.CallStaticBooleanMethod(_class, _start, new JValue(path), new JValue(tunFd));
        }
        finally
        {
            JNIEnv.DeleteLocalRef(path);
        }
    }

    public static bool Stop()
    {
        EnsureInitialized();
        return JNIEnv.CallStaticBooleanMethod(_class, _stop);
    }

    public static bool IsRunning()
    {
        EnsureInitialized();
        return JNIEnv.CallStaticBooleanMethod(_class, _running);
    }

    /// <summary>[tx packets, tx bytes, rx packets, rx bytes] as seen on the TUN.</summary>
    public static long[] Stats()
    {
        EnsureInitialized();
        var array = JNIEnv.CallStaticObjectMethod(_class, _stats);
        try
        {
            return array == IntPtr.Zero ? [] : JNIEnv.GetArray<long>(array) ?? [];
        }
        finally
        {
            JNIEnv.DeleteLocalRef(array);
        }
    }

    /// <summary>
    /// tun2socks configuration (YAML). DNS and all UDP go through SOCKS5 UDP ASSOCIATE to the core. The
    /// credentials are the core's per-session SOCKS5 user (hex strings, so they need no YAML escaping).
    /// </summary>
    public static string Config(int socksPort, string username, string password, int mtu, string ipv4, string ipv6)
    {
        if (!IsHex(username.Replace("jacki-", string.Empty, StringComparison.Ordinal)) || !IsHex(password))
        {
            throw new ArgumentException("Unexpected SOCKS credential format.");
        }

        return $"""
        tunnel:
          mtu: {mtu}
          ipv4: {ipv4}
          ipv6: '{ipv6}'
        socks5:
          port: {socksPort}
          address: 127.0.0.1
          udp: 'udp'
          username: '{username}'
          password: '{password}'
        misc:
          task-stack-size: 86016
          connect-timeout: 10000
          read-write-timeout: 300000
          log-level: warn
        """;
    }

    private static bool IsHex(string s) => s.Length > 0 && s.All(char.IsAsciiHexDigit);
}
