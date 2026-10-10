using System.Text.Json;
using System.Text.Json.Serialization;
using JackiVpn.Domain.Common;
using JackiVpn.Security;

namespace JackiVpn.Networking.Journal;

[JsonConverter(typeof(JsonStringEnumConverter<JournalEntryKind>))]
public enum JournalEntryKind
{
    WinInetProxy = 0,
    WinHttpProxy,
    KillSwitch,
    TunAdapter,
    CoreProcess,
}

public sealed record JournalEntry
{
    public required string Id { get; init; }
    public required JournalEntryKind Kind { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Kind-specific payload (never contains credentials).</summary>
    public required JsonElement Data { get; init; }

    public T As<T>() => Data.Deserialize<T>(JsonDefaults.Storage)
        ?? throw new InvalidOperationException($"Journal entry {Id} has no {typeof(T).Name} payload.");
}

public sealed record JournalDocument
{
    public int FormatVersion { get; init; } = 1;
    public string? SessionId { get; init; }
    public int OwnerProcessId { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public List<JournalEntry> Entries { get; init; } = [];
}

/// <summary>
/// Write-ahead journal of every Windows state change we make. An entry is persisted (atomically, flushed)
/// <b>before</b> the change is applied and removed only after it was undone. If the process dies in
/// between, the next start finds the file and runs recovery. A missing or empty journal means the last
/// session ended cleanly.
/// </summary>
public sealed class NetworkStateJournal
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private JournalDocument _doc;

    private bool _corrupt;

    public NetworkStateJournal(string path)
    {
        _path = path;
        _doc = LoadFromDisk(path, out _corrupt) ?? new JournalDocument();
    }

    public string FilePath => _path;

    /// <summary>The journal file existed but could not be read: what was changed is unknown, so a full repair is needed.</summary>
    public bool WasCorrupt
    {
        get
        {
            lock (_gate)
            {
                return _corrupt;
            }
        }
    }

    /// <summary>True when a previous session left changes behind (crash / power loss / kill) or the journal was corrupt.</summary>
    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _doc.Entries.Count > 0 || _corrupt;
            }
        }
    }

    /// <summary>Called after a full repair handled a corrupt journal.</summary>
    public void AcknowledgeCorruption()
    {
        lock (_gate)
        {
            _corrupt = false;
        }
    }

    public IReadOnlyList<JournalEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _doc.Entries.ToArray();
            }
        }
    }

    public JournalDocument Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _doc with { Entries = [.. _doc.Entries] };
            }
        }
    }

    public string Record<T>(JournalEntryKind kind, T payload, string? id = null)
    {
        var entry = new JournalEntry
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Kind = kind,
            CreatedUtc = DateTimeOffset.UtcNow,
            Data = JsonSerializer.SerializeToElement(payload, JsonDefaults.Storage),
        };
        lock (_gate)
        {
            _doc.Entries.RemoveAll(e => e.Id == entry.Id);
            _doc.Entries.Add(entry);
            if (_doc.Entries.Count == 1)
            {
                _doc = _doc with { OwnerProcessId = Environment.ProcessId, StartedUtc = DateTimeOffset.UtcNow, SessionId = Guid.NewGuid().ToString("N") };
            }

            Persist();
        }

        return entry.Id;
    }

    public void Complete(string id)
    {
        lock (_gate)
        {
            if (_doc.Entries.RemoveAll(e => e.Id == id) > 0)
            {
                Persist();
            }
        }
    }

    public IReadOnlyList<JournalEntry> OfKind(JournalEntryKind kind)
    {
        lock (_gate)
        {
            return _doc.Entries.Where(e => e.Kind == kind).ToArray();
        }
    }

    /// <summary>Called after a clean shutdown/recovery when nothing is outstanding.</summary>
    public void ClearIfEmpty()
    {
        lock (_gate)
        {
            if (_doc.Entries.Count == 0 && File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    private void Persist()
    {
        if (_doc.Entries.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return;
        }

        SecureFileSystem.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_doc, JsonDefaults.Storage));
    }

    private static JournalDocument? LoadFromDisk(string path, out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JournalDocument>(File.ReadAllText(path), JsonDefaults.Storage);
        }
        catch (JsonException)
        {
            // A torn/corrupt journal still means "dirty": keep a copy for diagnostics and run a full repair.
            corrupt = true;
            var copy = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(path, copy, overwrite: true);
            return new JournalDocument { Entries = [] };
        }
    }
}
