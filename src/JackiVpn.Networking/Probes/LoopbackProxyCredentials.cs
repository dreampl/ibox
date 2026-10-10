using System.Collections.Concurrent;
using System.Net;

namespace JackiVpn.Networking.Probes;

/// <summary>
/// Credentials of loopback proxy inbounds owned by this process (Android: the per-session SOCKS5 user of the
/// core's mixed inbound). Keyed by port, so every in-process client of that inbound — live ping, exit lookup,
/// speed test, watchdog — authenticates without each call site carrying the secret around.
/// </summary>
public static class LoopbackProxyCredentials
{
    private static readonly ConcurrentDictionary<int, NetworkCredential> ByPort = new();

    public static void Set(int port, string username, string password) => ByPort[port] = new NetworkCredential(username, password);

    public static void Clear(int port) => ByPort.TryRemove(port, out _);

    public static NetworkCredential? Get(int port) => ByPort.TryGetValue(port, out var c) ? c : null;

    /// <summary>
    /// The proxy for a loopback inbound: SOCKS5 with username/password when the inbound requires it (auth is
    /// negotiated up front, no 407 round trip), otherwise the address as given.
    /// </summary>
    public static IWebProxy Resolve(Uri proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return proxy.IsLoopback && Get(proxy.Port) is { } credential
            ? new FixedProxy(new Uri($"socks5://127.0.0.1:{proxy.Port}")) { Credentials = credential }
            : new FixedProxy(proxy);
    }

    private sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
