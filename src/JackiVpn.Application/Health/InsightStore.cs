using System.Text.Json;
using System.Text.Json.Serialization;
using JackiVpn.Domain.Common;
using JackiVpn.Security;

namespace JackiVpn.Application.Health;

/// <summary>Where traffic actually leaves the tunnel, as seen by an external service through the connection.</summary>
public sealed record ExitInfo(string Ip, string? CountryCode, string? CountryName, string? City, string? Isp, DateTimeOffset SeenUtc);

/// <summary>Learned, non-secret facts about one server configuration (keyed by its identity fingerprint).</summary>
public sealed record ProfileInsight
{
    [JsonConverter(typeof(JsonStringEnumConverter<FragmentVariant>))]
    public FragmentVariant? Fragment { get; init; }

    public double? ConfiguredMedianMs { get; init; }
    public double? RecordMedianMs { get; init; }

    /// <summary>WebSocket 0-RTT proved to work and be faster on this server.</summary>
    public bool? EarlyData { get; init; }

    public double? EarlyDataMedianMs { get; init; }
    public double? WithoutEarlyDataMedianMs { get; init; }
    public DateTimeOffset? TunedUtc { get; init; }
    public ExitInfo? Exit { get; init; }

    /// <summary>
    /// Networks (see <c>OrchestratorDependencies.NetworkKey</c>) on which a session through this server carried real
    /// traffic, with the last time. Iranian carriers throttle per network, so what worked here is tried first here.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset>? WorkedOn { get; init; }

    public bool WorkedRecentlyOn(string network, DateTimeOffset now) =>
        WorkedOn is { } w && w.TryGetValue(network, out var at) && now - at < TimeSpan.FromDays(14);

    /// <summary>Per network: the way around a block that made this server work there (see EndpointRescue).</summary>
    public IReadOnlyDictionary<string, EndpointFix>? Fixes { get; init; }

    public EndpointFix? FixFor(string network, DateTimeOffset now) =>
        Fixes is { } f && f.TryGetValue(network, out var fix) && now - fix.AtUtc < EndpointFix.Lifetime ? fix : null;
}

/// <summary>
/// How to reach a server on one network: another address (secure-DNS answer or clean CDN edge; SNI/Host keep the
/// original name) and/or another TLS fingerprint.
/// </summary>
public sealed record EndpointFix(string? Address, string? Fingerprint, string Reason, DateTimeOffset AtUtc)
{
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(3);
}

/// <summary>Small JSON-backed store (atomic writes, tolerant of a missing or corrupt file).</summary>
public sealed class InsightStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, ProfileInsight> _items;

    public InsightStore(string path)
    {
        _path = path;
        _items = Load(path);
    }

    public event EventHandler<string>? Changed;

    public ProfileInsight? Get(string fingerprint)
    {
        lock (_gate)
        {
            return _items.TryGetValue(fingerprint, out var v) ? v : null;
        }
    }

    public void Update(string fingerprint, Func<ProfileInsight, ProfileInsight> change)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentNullException.ThrowIfNull(change);
        string json;
        lock (_gate)
        {
            _items[fingerprint] = change(_items.TryGetValue(fingerprint, out var v) ? v : new ProfileInsight());
            json = JsonSerializer.Serialize(_items, JsonDefaults.Storage);
        }

        try
        {
            SecureFileSystem.WriteAllTextAtomic(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In-memory value still applies for this session.
        }

        Changed?.Invoke(this, fingerprint);
    }

    private static Dictionary<string, ProfileInsight> Load(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024)
            {
                return JsonSerializer.Deserialize<Dictionary<string, ProfileInsight>>(File.ReadAllText(path), JsonDefaults.Storage)
                    ?? new Dictionary<string, ProfileInsight>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt cache: start over (it only holds measurements).
        }

        return new Dictionary<string, ProfileInsight>(StringComparer.Ordinal);
    }
}
