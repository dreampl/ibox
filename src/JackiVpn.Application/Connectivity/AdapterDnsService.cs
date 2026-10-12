using System.Text.Json;
using JackiVpn.Domain.Network;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Connectivity;

/// <summary>Reads and changes the DNS servers of the system's network adapters (platform side: Windows service, nmcli).</summary>
public interface IAdapterDnsControl
{
    /// <summary>Physical adapters (never IBOX's own tunnel or loopback).</summary>
    Task<IReadOnlyList<AdapterDns>> ListAsync(CancellationToken ct);

    /// <summary>Sets the servers of one adapter; an empty list makes it automatic again.</summary>
    Task SetAsync(AdapterDnsChange change, CancellationToken ct);
}

/// <summary>
/// Per-adapter DNS on top of <see cref="IAdapterDnsControl"/>: before IBOX changes an adapter for the first time it
/// remembers what the adapter had, so "Restore" puts it back exactly (automatic, or the servers set by hand before).
/// </summary>
public sealed class AdapterDnsService(IAdapterDnsControl control, string originalsPath, ILogger logger)
{
    private readonly Lock _gate = new();
    private Dictionary<string, Original>? _originals;

    public sealed record Original(bool Automatic, IReadOnlyList<string> Servers);

    public Task<IReadOnlyList<AdapterDns>> ListAsync(CancellationToken ct) => control.ListAsync(ct);

    /// <summary>True when IBOX changed this adapter and can put the old setting back.</summary>
    public bool CanRestore(string adapterId)
    {
        lock (_gate)
        {
            return Originals().ContainsKey(adapterId);
        }
    }

    /// <summary>Sets the adapter's servers (empty = automatic). Returns what is wrong instead when the input is not usable.</summary>
    public async Task<string?> SetAsync(AdapterDns adapter, IReadOnlyList<string> servers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(servers);
        var change = new AdapterDnsChange(adapter.Id, servers);
        if (change.Problem() is { } problem)
        {
            return problem;
        }

        lock (_gate)
        {
            if (Originals().TryAdd(adapter.Id, new Original(adapter.Automatic, adapter.Automatic ? [] : adapter.Servers)))
            {
                Save();
            }
        }

        await control.SetAsync(change, ct).ConfigureAwait(false);
        logger.LogInformation("DNS of {Adapter} set to {Servers}.", adapter.Name, servers.Count == 0 ? "automatic" : string.Join(", ", servers));
        return null;
    }

    /// <summary>Puts back what the adapter had before IBOX first changed it.</summary>
    public async Task RestoreAsync(AdapterDns adapter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        Original? original;
        lock (_gate)
        {
            Originals().TryGetValue(adapter.Id, out original);
        }

        if (original is null)
        {
            return;
        }

        await control.SetAsync(new AdapterDnsChange(adapter.Id, original.Automatic ? [] : original.Servers), ct).ConfigureAwait(false);
        lock (_gate)
        {
            Originals().Remove(adapter.Id);
            Save();
        }

        logger.LogInformation("DNS of {Adapter} restored.", adapter.Name);
    }

    private Dictionary<string, Original> Originals()
    {
        if (_originals is not null)
        {
            return _originals;
        }

        try
        {
            if (File.Exists(originalsPath))
            {
                _originals = JsonSerializer.Deserialize<Dictionary<string, Original>>(File.ReadAllText(originalsPath));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug("Adapter DNS originals unreadable: {Error}", ex.Message);
        }

        return _originals ??= new Dictionary<string, Original>(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(originalsPath)!);
            File.WriteAllText(originalsPath, JsonSerializer.Serialize(_originals));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Adapter DNS originals not saved: {Error}", ex.Message);
        }
    }
}

/// <summary>Ready-made server pairs for an adapter (primary and secondary of the resolvers IBOX knows), plus the user's own.</summary>
public static class AdapterDnsPresets
{
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Servers)> BuiltIn { get; } =
    [
        ("Cloudflare", ["1.1.1.1", "1.0.0.1"]),
        ("Google", ["8.8.8.8", "8.8.4.4"]),
        ("Quad9", ["9.9.9.9", "149.112.112.112"]),
        ("AdGuard", ["94.140.14.14", "94.140.15.15"]),
        ("OpenDNS", ["208.67.222.222", "208.67.220.220"]),
        ("Shecan (Iran)", ["178.22.122.100", "185.51.200.2"]),
        ("Electro (Iran)", ["78.157.42.100", "78.157.42.101"]),
        ("403.online (Iran)", ["10.202.10.202", "10.202.10.102"]),
        ("Radar Game (Iran)", ["10.202.10.10", "10.202.10.11"]),
        ("Begzar (Iran)", ["185.55.226.26", "185.55.225.25"]),
    ];

    /// <summary>Built-in pairs, then the user's own resolvers (by address; DoH-only details do not apply to adapters).</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Servers)> All(IEnumerable<Domain.Settings.CustomDnsResolver> custom) =>
        BuiltIn.Concat(custom.Select(c => (c.Name + " (yours)", (IReadOnlyList<string>)[c.Address.Trim()]))).ToList();
}
