using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Subscriptions;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Ecosystem;

public sealed record EcosystemPeer(string Id, string Name, string Platform, IPAddress Address, int Port, byte[] PublicKey, DateTimeOffset LastSeenUtc);

/// <summary>What the receiving user is asked to approve. <see cref="Code"/> must match the sender's screen.</summary>
public sealed record IncomingOffer(string FromName, string FromPlatform, IReadOnlyList<string> SubscriptionNames, string Code);

public sealed record TransferResult(bool Accepted, int Imported, IReadOnlyList<string> Messages);

/// <summary>A local IPv4 network this device is on (address + prefix length).</summary>
public readonly record struct LocalNetwork(IPAddress Address, int PrefixLength, bool Preferred = false);

public interface IEcosystemPrompt
{
    /// <summary>Ask the user on this device whether to accept (shows the 6-digit code). false = reject.</summary>
    Task<bool> ConfirmIncomingAsync(IncomingOffer offer, CancellationToken ct);
}

/// <summary>
/// "Sync device": sharing subscriptions between the user's own devices on the same local network (Windows ↔ Android).
/// <list type="bullet">
/// <item><b>Listening</b> (while the app is open and the user has not turned it off): a TCP listener that answers a
/// scan with this device's name, platform and a per-run ECDH P-256 public key, and receives offers.</item>
/// <item><b>Scanning</b>: on demand, every address of each local LAN (the /24 around this device, or the whole subnet
/// up to /22) is asked directly over TCP. Unlike broadcasts this also works on routers and phones that filter
/// broadcast/multicast; only Wi-Fi "client isolation" (guest networks) can still hide devices.</item>
/// <item><b>Transfer</b>: the sender offers subscription <i>names</i> with an ephemeral public key; both sides derive
/// the same key (ECDH + HKDF) and a 6-digit code shown on both screens, so a device in the middle is noticed. Only
/// after the receiving user accepts is the subscription link sent, AES-GCM encrypted. The receiver adds it with its
/// own device ID, so the provider's device limit is enforced and its answer is reported back to the sender.</item>
/// </list>
/// Everything is LAN-only: connections from or to public addresses are refused.
/// </summary>
public sealed class EcosystemService : IAsyncDisposable
{
    public const int TransferPort = 50456;
    private const int MaxFrame = 256 * 1024;
    private const int MaxScanTargets = 2048;
    private const string Magic = "jacki-eco";
    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("JackiVpn ecosystem v1 key");
    private static readonly byte[] CodeInfo = Encoding.ASCII.GetBytes("JackiVpn ecosystem v1 code");
    private static readonly string[] SkippedInterfacePrefixes = ["tun", "ppp", "rmnet", "ccmni", "v4-", "clat", "dummy", "wg", "utun", "ipsec", "veth", "docker", "br-"];
    private static readonly string[] VirtualAdapterHints = ["Hyper-V", "VirtualBox", "VMware", "Virtual Ethernet", "WSL", "TAP-", "Wintun", "WireGuard"];

    private readonly SubscriptionService _subscriptions;
    private readonly ISubscriptionRepository _repository;
    private readonly IEcosystemPrompt _prompt;
    private readonly Func<string> _deviceName;
    private readonly string _platform;
    private readonly string _deviceId;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _incoming = new(1, 1);
    private readonly SemaphoreSlim _hellos = new(16, 16);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private ECDiffieHellman? _key;
    private TcpListener? _listener;

    public EcosystemService(SubscriptionService subscriptions, ISubscriptionRepository repository, IEcosystemPrompt prompt, Func<string> deviceName, string platform, string deviceId, ILogger logger)
    {
        _subscriptions = subscriptions;
        _repository = repository;
        _prompt = prompt;
        _deviceName = deviceName;
        _platform = platform;
        _deviceId = deviceId;
        _logger = logger;
    }

    /// <summary>Where the listener binds (tests: loopback, so no firewall prompt and nothing on the LAN).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    public int ListenPort { get; init; } = TransferPort;

    /// <summary>The LANs to scan (Android supplies its Wi-Fi/Ethernet networks; the default reads the interfaces).</summary>
    public Func<IEnumerable<LocalNetwork>> LocalNetworks { get; init; } = DefaultLocalNetworks;

    /// <summary>This run's public key (what scanning devices receive).</summary>
    public byte[]? PublicKey => _key?.PublicKey.ExportSubjectPublicKeyInfo();

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public string DeviceId => _deviceId;

    /// <summary>Starts answering scans and offers. Returns false if the port could not be opened.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return true;
            }

            _cts = new CancellationTokenSource();
            _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                _listener = new TcpListener(BindAddress, ListenPort);
                _listener.Start();
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Sync device could not open TCP {Port} ({Error}); another copy may be running.", ListenPort, ex.SocketErrorCode);
                StopLocked();
                return false;
            }

            var ct = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(ct), CancellationToken.None);
            _logger.LogInformation("Sync device listening on TCP {Port}.", ListenPort);
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            StopLocked();
        }

        return Task.CompletedTask;
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _listener?.Stop();
        _listener = null;
        _key?.Dispose();
        _key = null;
    }

    // ------------------------------------------------------------------ scanning

    /// <summary>
    /// Asks every address of the local networks (in parallel) whether IBOX is listening there.
    /// <paramref name="found"/> is called for each device as soon as it answers.
    /// </summary>
    public async Task<IReadOnlyList<EcosystemPeer>> ScanAsync(Action<EcosystemPeer>? found, CancellationToken ct)
    {
        var networks = LocalNetworks().ToList();
        var own = networks.Select(n => n.Address).ToHashSet();
        var targets = ScanTargets(networks);
        _logger.LogInformation("Sync device scan: {Count} address(es) on {Networks} network(s).", targets.Count, networks.Count);
        var peers = new Dictionary<string, EcosystemPeer>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 96, CancellationToken = ct }, async (ip, token) =>
        {
            if (own.Contains(ip) || await ProbeAsync(ip, TransferPort, token).ConfigureAwait(false) is not { } peer || peer.Id == _deviceId)
            {
                return;
            }

            bool added;
            lock (peers)
            {
                added = peers.TryAdd(peer.Id, peer);
            }

            if (added)
            {
                found?.Invoke(peer);
            }
        }).ConfigureAwait(false);
        return peers.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Asks one address directly; null when nothing (or not IBOX) answers.</summary>
    public async Task<EcosystemPeer?> ProbeAsync(IPAddress address, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!NetText.IsPrivateOrLocal(address))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var tcp = new TcpClient(address.AddressFamily);
            await tcp.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var stream = tcp.GetStream();
            await WriteFrameAsync(stream, new JsonObject { ["t"] = "hello", ["v"] = 1 }, timeout.Token).ConfigureAwait(false);
            return ParseInfo(await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false), address);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or InvalidOperationException or JsonException or ObjectDisposedException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// Host addresses to ask: the whole subnet when it is /22 or smaller, otherwise the /24 around this device
    /// (a big corporate/carrier range is never swept). Preferred (Wi-Fi/Ethernet) networks first.
    /// </summary>
    internal static IReadOnlyList<IPAddress> ScanTargets(IEnumerable<LocalNetwork> networks)
    {
        var result = new List<IPAddress>();
        var seen = new HashSet<uint>();
        foreach (var n in networks.OrderByDescending(n => n.Preferred))
        {
            if (n.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(n.Address) || !NetText.IsPrivateOrLocal(n.Address))
            {
                continue;
            }

            var prefix = n.PrefixLength is >= 22 and <= 30 ? n.PrefixLength : 24;
            var ip = BinaryPrimitives.ReadUInt32BigEndian(n.Address.GetAddressBytes());
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var first = (ip & mask) + 1;
            var last = (ip | ~mask) - 1;
            for (var a = first; a <= last && result.Count < MaxScanTargets; a++)
            {
                if (a != ip && seen.Add(a))
                {
                    var bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, a);
                    result.Add(new IPAddress(bytes));
                }
            }
        }

        return result;
    }

    /// <summary>Up, private IPv4 LAN interfaces; never the VPN tunnel, mobile data or virtual machine adapters.</summary>
    public static IEnumerable<LocalNetwork> DefaultLocalNetworks()
    {
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                || nic.Name.Contains(Domain.ProductInfo.TunInterfaceName, StringComparison.OrdinalIgnoreCase)
                || SkippedInterfacePrefixes.Any(p => nic.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || VirtualAdapterHints.Any(h => (nic.Description ?? string.Empty).Contains(h, StringComparison.OrdinalIgnoreCase) || nic.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var preferred = nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address) || !NetText.IsPrivateOrLocal(ua.Address)
                    || ua.Address.ToString().StartsWith("172.19.0.", StringComparison.Ordinal))
                {
                    continue;
                }

                int prefix;
                try
                {
                    prefix = ua.PrefixLength;
                }
                catch (PlatformNotSupportedException)
                {
                    prefix = 24;
                }

                yield return new LocalNetwork(ua.Address, prefix, preferred);
            }
        }
    }

    internal static EcosystemPeer? ParseInfo(JsonObject o, IPAddress from)
    {
        try
        {
            if (o["t"]?.GetValue<string>() != "info" || o["m"]?.GetValue<string>() != Magic || o["v"]?.GetValue<int>() != 1)
            {
                return null;
            }

            var id = o["id"]?.GetValue<string>();
            var name = o["name"]?.GetValue<string>();
            var platform = o["platform"]?.GetValue<string>();
            var port = o["port"]?.GetValue<int>() ?? 0;
            var key = Convert.FromBase64String(o["key"]?.GetValue<string>() ?? string.Empty);
            if (id is not { Length: > 0 and <= 64 } || string.IsNullOrWhiteSpace(name) || platform is not ("Windows" or "Android")
                || !NetText.IsValidPort(port) || key.Length is < 64 or > 128 || NetText.HasControlChars(name))
            {
                return null;
            }

            return new EcosystemPeer(id, Clip(name, 48), platform, from, port, key, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ sending

    /// <summary>
    /// Offers subscriptions to a peer. <paramref name="showCode"/> receives the 6-digit code to show while the other
    /// device asks its user. The link only leaves this device after they accept.
    /// </summary>
    public async Task<TransferResult> SendAsync(EcosystemPeer peer, IReadOnlyList<string> subscriptionIds, Action<string> showCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(showCode);
        if (!NetText.IsPrivateOrLocal(peer.Address))
        {
            throw new InvalidOperationException("Only devices on the local network can receive subscriptions.");
        }

        var items = new List<(string Name, string Url)>();
        foreach (var id in subscriptionIds)
        {
            var sub = await _repository.GetAsync(id, ct).ConfigureAwait(false);
            var url = await _repository.GetSourceUrlAsync(id, ct).ConfigureAwait(false);
            if (sub is not null && url is not null)
            {
                items.Add((sub.Name, url));
            }
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException("Nothing to send.");
        }

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(peer.PublicKey, out _);
        var nonce = RandomNumberGenerator.GetBytes(16);
        var senderPub = ephemeral.PublicKey.ExportSubjectPublicKeyInfo();
        var (key, code) = Derive(ephemeral.DeriveRawSecretAgreement(peerKey.PublicKey), senderPub, peer.PublicKey, nonce);
        showCode(code);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var tcp = new TcpClient(peer.Address.AddressFamily);
        await tcp.ConnectAsync(peer.Address, peer.Port, timeout.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();
        await WriteFrameAsync(stream, new JsonObject
        {
            ["t"] = "offer",
            ["v"] = 1,
            ["id"] = _deviceId,
            ["name"] = Clip(_deviceName(), 48),
            ["platform"] = _platform,
            ["key"] = Convert.ToBase64String(senderPub),
            ["nonce"] = Convert.ToBase64String(nonce),
            ["subs"] = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(Clip(i.Name, 64))).ToArray()),
        }, timeout.Token).ConfigureAwait(false);

        var answer = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        if (answer["accept"]?.GetValue<bool>() != true)
        {
            return new TransferResult(false, 0, [answer["reason"]?.GetValue<string>() ?? "The other device declined."]);
        }

        var payload = new JsonArray(items.Select(i => (JsonNode?)new JsonObject { ["name"] = i.Name, ["url"] = i.Url }).ToArray());
        var (iv, cipher, tag) = Seal(key, Encoding.UTF8.GetBytes(payload.ToJsonString()), Aad(_deviceId, peer.Id));
        CryptographicOperations.ZeroMemory(key);
        await WriteFrameAsync(stream, new JsonObject
        {
            ["t"] = "payload",
            ["iv"] = Convert.ToBase64String(iv),
            ["ct"] = Convert.ToBase64String(cipher),
            ["tag"] = Convert.ToBase64String(tag),
        }, timeout.Token).ConfigureAwait(false);

        var result = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        var messages = (result["messages"] as JsonArray)?.Select(m => Clip(m?.GetValue<string>() ?? string.Empty, 300)).ToArray() ?? [];
        return new TransferResult(true, result["imported"]?.GetValue<int>() ?? 0, messages);
    }

    // ------------------------------------------------------------------ receiving

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or NullReferenceException)
            {
                return;
            }

            _ = Task.Run(() => HandleIncomingAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote is null || !NetText.IsPrivateOrLocal(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            {
                return; // LAN only
            }

            JsonObject first;
            var stream = client.GetStream();
            if (!await _hellos.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return; // a flood of connections is dropped instead of queued
            }

            try
            {
                using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                firstTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                first = await ReadFrameAsync(stream, firstTimeout.Token).ConfigureAwait(false);
                if (first["t"]?.GetValue<string>() == "hello" && _key is { } helloKey)
                {
                    await WriteFrameAsync(stream, new JsonObject
                    {
                        ["t"] = "info",
                        ["m"] = Magic,
                        ["v"] = 1,
                        ["id"] = _deviceId,
                        ["name"] = Clip(_deviceName(), 48),
                        ["platform"] = _platform,
                        ["port"] = ListenPort,
                        ["key"] = Convert.ToBase64String(helloKey.PublicKey.ExportSubjectPublicKeyInfo()),
                    }, firstTimeout.Token).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or InvalidOperationException or ObjectDisposedException)
            {
                return;
            }
            finally
            {
                _hellos.Release();
            }

            if (!await _incoming.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return; // one request at a time; a second one is dropped (no prompt spam)
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                await HandleOfferAsync(first, stream, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or FormatException or CryptographicException or InvalidOperationException)
            {
                _logger.LogInformation("Sync device transfer from {Remote} ended: {Error}", remote, SecretRedactor.Redact(ex.Message));
            }
            finally
            {
                _incoming.Release();
            }
        }
    }

    private async Task HandleOfferAsync(JsonObject offer, Stream stream, CancellationToken ct)
    {
        if (offer["t"]?.GetValue<string>() != "offer" || offer["v"]?.GetValue<int>() != 1 || _key is not { } myKey)
        {
            return;
        }

        var senderId = offer["id"]?.GetValue<string>() ?? string.Empty;
        var fromName = Clip(offer["name"]?.GetValue<string>() ?? "Unknown device", 48);
        var platform = offer["platform"]?.GetValue<string>() is "Android" ? "Android" : "Windows";
        var senderPub = Convert.FromBase64String(offer["key"]?.GetValue<string>() ?? string.Empty);
        var nonce = Convert.FromBase64String(offer["nonce"]?.GetValue<string>() ?? string.Empty);
        var names = (offer["subs"] as JsonArray)?.Select(n => Clip(n?.GetValue<string>() ?? "?", 64)).Take(20).ToArray() ?? [];
        if (senderId.Length is 0 or > 64 || senderPub.Length is < 64 or > 128 || nonce.Length != 16 || names.Length == 0 || NetText.HasControlChars(fromName))
        {
            return;
        }

        using var sender = ECDiffieHellman.Create();
        sender.ImportSubjectPublicKeyInfo(senderPub, out _);
        var (key, code) = Derive(myKey.DeriveRawSecretAgreement(sender.PublicKey), senderPub, myKey.PublicKey.ExportSubjectPublicKeyInfo(), nonce);
        try
        {
            var accepted = await _prompt.ConfirmIncomingAsync(new IncomingOffer(fromName, platform, names, code), ct).ConfigureAwait(false);
            await WriteFrameAsync(stream, new JsonObject { ["t"] = "answer", ["accept"] = accepted, ["reason"] = accepted ? null : "Declined on the other device." }, ct).ConfigureAwait(false);
            if (!accepted)
            {
                return;
            }

            var sealedFrame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            var plain = Open(key,
                Convert.FromBase64String(sealedFrame["iv"]?.GetValue<string>() ?? string.Empty),
                Convert.FromBase64String(sealedFrame["ct"]?.GetValue<string>() ?? string.Empty),
                Convert.FromBase64String(sealedFrame["tag"]?.GetValue<string>() ?? string.Empty),
                Aad(senderId, _deviceId));
            var (imported, messages) = await ImportAsync(plain, ct).ConfigureAwait(false);
            await WriteFrameAsync(stream, new JsonObject
            {
                ["t"] = "result",
                ["imported"] = imported,
                ["messages"] = new JsonArray(messages.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<(int Imported, List<string> Messages)> ImportAsync(byte[] plain, CancellationToken ct)
    {
        var messages = new List<string>();
        var imported = 0;
        if (JsonNode.Parse(plain) is not JsonArray items)
        {
            return (0, ["Nothing was received."]);
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in await _repository.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (await _repository.GetSourceUrlAsync(s.Id, ct).ConfigureAwait(false) is { } u)
            {
                existing.Add(u);
            }
        }

        foreach (var item in items.OfType<JsonObject>().Take(20))
        {
            var name = Clip(item["name"]?.GetValue<string>() ?? "Subscription", 64);
            var url = item["url"]?.GetValue<string>() ?? string.Empty;
            if (existing.Contains(url))
            {
                messages.Add($"\"{name}\" is already on this device.");
                continue;
            }

            try
            {
                var sub = await _subscriptions.AddAsync(url, name, null, ct).ConfigureAwait(false);
                imported++;
                messages.Add(sub.DeviceLimit switch
                {
                    DeviceLimitState.LimitReached => $"\"{sub.Name}\" was added, but the provider refused this device: device limit reached.",
                    DeviceLimitState.DeviceIdRequired => $"\"{sub.Name}\" was added, but the provider needs a device ID (turn on \"Send device ID\").",
                    _ when sub.LastError is { } e => $"\"{sub.Name}\" was added, but its first update failed: {e}",
                    _ => $"\"{sub.Name}\" added with {sub.ProfileCount} server(s).",
                });
            }
            catch (Exception ex) when (ex is SubscriptionException or JackiVpn.Subscriptions.Fetching.FetchException or InvalidOperationException)
            {
                messages.Add($"\"{name}\" could not be added: {SecretRedactor.Redact(ex.Message)}");
            }
        }

        return (imported, messages);
    }

    // ------------------------------------------------------------------ crypto & framing

    /// <summary>Key and 6-digit code from the ECDH secret, bound to both public keys and the sender's nonce.</summary>
    internal static (byte[] Key, string Code) Derive(byte[] secret, byte[] senderPub, byte[] receiverPub, byte[] nonce)
    {
        var salt = SHA256.HashData([.. senderPub, .. receiverPub, .. nonce]);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, KeyInfo);
        var codeBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 4, salt, CodeInfo);
        CryptographicOperations.ZeroMemory(secret);
        var code = (BinaryPrimitives.ReadUInt32BigEndian(codeBytes) % 1_000_000).ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
        return (key, code[..3] + " " + code[3..]);
    }

    private static byte[] Aad(string senderId, string receiverId) => Encoding.UTF8.GetBytes($"jacki-eco-v1|{senderId}|{receiverId}");

    private static (byte[] Iv, byte[] Cipher, byte[] Tag) Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(iv, plain, cipher, tag, aad);
        CryptographicOperations.ZeroMemory(plain);
        return (iv, cipher, tag);
    }

    private static byte[] Open(byte[] key, byte[] iv, byte[] cipher, byte[] tag, byte[] aad)
    {
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(iv, cipher, tag, plain, aad);
        return plain;
    }

    private static async Task WriteFrameAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrame)
        {
            throw new InvalidOperationException("Invalid sync frame.");
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("Invalid sync message.");
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            StopLocked();
        }

        _incoming.Dispose();
        _hellos.Dispose();
        return ValueTask.CompletedTask;
    }
}
