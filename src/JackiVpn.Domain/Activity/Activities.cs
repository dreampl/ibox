using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Activity;

/// <summary>Where a task stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivityState>))]
public enum ActivityState
{
    Running = 0,
    Paused,

    /// <summary>Waiting for the user (a permission request, a dialog).</summary>
    Waiting,
    Done,
    Failed,
}

/// <summary>
/// How much the shown progress can be trusted. Unknown never shows a number (a spinning ring instead); estimate is
/// derived (file growth against a known size, elapsed time against a typical duration), good is read from the app's
/// own window or dialog, exact comes from the app itself (a browser's byte counts, a plugin's own figure).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProgressAccuracy>))]
public enum ProgressAccuracy
{
    Unknown = 0,
    Estimate,
    Good,
    Exact,
}

/// <summary>One task on the notch: a download, a render, a copy, an assistant at work, a plugin's job.</summary>
public sealed record ActivityItem
{
    /// <summary>Unique within its source.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Who reports it: chrome, edge, firefox, opera, idm, explorer, aftereffects, premiere, mediaencoder, photoshop, illustrator, claude, chatgpt, plugin:&lt;id&gt;…</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>download, render, export, save, copy, move, agent, task.</summary>
    public string Kind { get; init; } = "task";

    public string Title { get; init; } = string.Empty;

    /// <summary>One short line (size, speed, what is left).</summary>
    public string? Detail { get; init; }

    /// <summary>0…1, or null when unknown (then the ring spins and no number is shown).</summary>
    public double? Progress { get; init; }

    public ProgressAccuracy Accuracy { get; init; }

    public ActivityState State { get; init; }

    /// <summary>#RRGGBB (null = the source's own colour).</summary>
    public string? Color { get; init; }

    /// <summary>An icon key: a source name or a full path to a local .png / .ico / .exe.</summary>
    public string? Icon { get; init; }

    /// <summary>Bytes so far and in all (downloads, copies) — shown, never invented.</summary>
    public long? Done { get; init; }

    public long? Total { get; init; }

    /// <summary>Bytes (or units) per second, when measured.</summary>
    public double? Rate { get; init; }

    public DateTimeOffset Started { get; init; }

    public DateTimeOffset Updated { get; init; }

    public bool Finished => State is ActivityState.Done or ActivityState.Failed;
}

/// <summary>What changed on the bus.</summary>
public enum ActivityChange
{
    Started,
    Updated,
    Ended,
    Removed,
}

/// <summary>
/// The one place every source reports to. It keeps the rules: progress only with a stated accuracy and within 0…1
/// (an unknown amount is never turned into a number), a finished task stays a few seconds and leaves, at most
/// <see cref="MaxItems"/> at a time (the oldest finished go first), and a source can only touch its own tasks.
/// Thread-safe; <see cref="Changed"/> is raised outside the lock on the reporting thread.
/// </summary>
public sealed class ActivityBus(Func<DateTimeOffset>? clock = null)
{
    public const int MaxItems = 12;
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, ActivityItem> _items = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public event EventHandler<(ActivityChange Change, ActivityItem Item)>? Changed;

    public static string Key(string source, string id) => source + "\u001f" + id;

    /// <summary>The tasks now, running first (newest first), then finished.</summary>
    public IReadOnlyList<ActivityItem> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items.Values.OrderBy(i => i.Finished).ThenByDescending(i => i.Started)];
        }
    }

    /// <summary>Starts or updates a task (the same source + id updates). Returns what was stored.</summary>
    public ActivityItem Report(ActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("An activity needs a source and an id.", nameof(item));
        }

        var now = _clock();
        ActivityChange change;
        ActivityItem stored;
        List<ActivityItem> evicted = [];
        lock (_gate)
        {
            var key = Key(item.Source, item.Id);
            var known = _items.GetValueOrDefault(key);
            stored = Normalize(item) with { Started = known?.Started ?? now, Updated = now };
            change = known is null ? ActivityChange.Started : stored.Finished && !known.Finished ? ActivityChange.Ended : ActivityChange.Updated;
            _items[key] = stored;
            while (_items.Count > MaxItems)
            {
                var oldest = _items.Values.OrderByDescending(i => i.Finished).ThenBy(i => i.Updated).First();
                _items.Remove(Key(oldest.Source, oldest.Id));
                evicted.Add(oldest);
            }
        }

        foreach (var e in evicted)
        {
            Changed?.Invoke(this, (ActivityChange.Removed, e));
        }

        Changed?.Invoke(this, (change, stored));
        return stored;
    }

    /// <summary>Ends a task (done or failed); it stays <see cref="Linger"/> and is then removed by <see cref="Sweep"/>.</summary>
    public ActivityItem? End(string source, string id, bool ok = true, string? detail = null)
    {
        ActivityItem? known;
        lock (_gate)
        {
            known = _items.GetValueOrDefault(Key(source, id));
        }

        if (known is null || known.Finished)
        {
            return known;
        }

        return Report(known with
        {
            State = ok ? ActivityState.Done : ActivityState.Failed,
            Progress = ok && known.Progress is not null ? 1 : known.Progress,
            Detail = detail ?? known.Detail,
        });
    }

    /// <summary>Removes one task at once.</summary>
    public bool Remove(string source, string id)
    {
        ActivityItem? removed;
        lock (_gate)
        {
            var key = Key(source, id);
            if (!_items.Remove(key, out removed))
            {
                return false;
            }
        }

        Changed?.Invoke(this, (ActivityChange.Removed, removed));
        return true;
    }

    /// <summary>Removes every task of a source (a browser closed, a plugin removed).</summary>
    public void RemoveSource(string source)
    {
        foreach (var item in Snapshot().Where(i => i.Source == source))
        {
            Remove(item.Source, item.Id);
        }
    }

    /// <summary>
    /// Finished tasks older than <see cref="Linger"/> leave, running ones silent for <see cref="Stale"/> too (a source
    /// that died). Returns when the next one is due (null = nothing waiting), so the caller arms one timer, not a loop.
    /// </summary>
    public TimeSpan? Sweep()
    {
        var now = _clock();
        List<ActivityItem> gone;
        TimeSpan? next = null;
        lock (_gate)
        {
            gone = [.. _items.Values.Where(i => now - i.Updated >= (i.Finished ? Linger : Stale))];
            foreach (var g in gone)
            {
                _items.Remove(Key(g.Source, g.Id));
            }

            foreach (var i in _items.Values)
            {
                var due = i.Updated + (i.Finished ? Linger : Stale) - now;
                next = next is null || due < next ? due : next;
            }
        }

        foreach (var g in gone)
        {
            Changed?.Invoke(this, (ActivityChange.Removed, g));
        }

        return next;
    }

    /// <summary>The rules every report passes through.</summary>
    public static ActivityItem Normalize(ActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var progress = item.Progress is { } p && double.IsFinite(p) ? Math.Clamp(p, 0, 1) : (double?)null;
        var accuracy = item.Accuracy;
        if (progress is null)
        {
            accuracy = ProgressAccuracy.Unknown;
        }
        else if (accuracy == ProgressAccuracy.Unknown)
        {
            // A number without a stated accuracy is not shown as a number.
            progress = null;
        }

        // Byte counts make a progress only when the total is known and sensible.
        if (progress is null && item.Total is > 0 && item.Done is >= 0 && item.Done <= item.Total && item.Accuracy != ProgressAccuracy.Unknown)
        {
            progress = (double)item.Done.Value / item.Total.Value;
            accuracy = item.Accuracy;
        }

        return item with
        {
            Title = Trim(item.Title, 80) ?? item.Source,
            Detail = Trim(item.Detail, 120),
            Progress = progress,
            Accuracy = accuracy,
            Done = item.Done is < 0 ? null : item.Done,
            Total = item.Total is <= 0 ? null : item.Total,
            Rate = item.Rate is { } r && double.IsFinite(r) && r >= 0 ? r : null,
            Color = item.Color is { Length: 7 } c && c[0] == '#' && c.Skip(1).All(char.IsAsciiHexDigit) ? c : null,
        };
    }

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var clean = new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }

    /// <summary>Bytes as people read them (1.4 MB, 820 KB).</summary>
    public static string Bytes(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (1L << 10):0} KB",
        _ => $"{bytes:0} B",
    };
}

/// <summary>A rate from samples of a growing count (file sizes, bytes copied): a short moving window, never a guess.</summary>
public sealed class RateMeter(TimeSpan window)
{
    private readonly Queue<(DateTimeOffset At, long Value)> _samples = new();

    public double? Add(DateTimeOffset at, long value)
    {
        _samples.Enqueue((at, value));
        while (_samples.Count > 2 && at - _samples.Peek().At > window)
        {
            _samples.Dequeue();
        }

        var first = _samples.Peek();
        var seconds = (at - first.At).TotalSeconds;
        return seconds >= 0.4 && value >= first.Value ? (value - first.Value) / seconds : null;
    }
}
