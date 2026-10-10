using System.Globalization;
using System.Text;
using System.Text.Json;
using JackiVpn.Domain.Common;
using JackiVpn.Domain.Usage;
using JackiVpn.Security;

namespace JackiVpn.Application.Usage;

/// <summary>Persisted usage counters (one JSON file, written atomically; dates are local yyyy-MM-dd).</summary>
public sealed class UsageData
{
    public int V { get; set; } = 1;

    /// <summary>date → [download, upload, lanDown, lanUp, tunnelDown, tunnelUp] (device totals from the network adapters).</summary>
    public Dictionary<string, long[]> Days { get; set; } = new(StringComparer.Ordinal);

    /// <summary>date → 48 values: download/upload per hour.</summary>
    public Dictionary<string, long[]> Hours { get; set; } = new(StringComparer.Ordinal);

    /// <summary>date → app → [download, upload, tunnelDown, tunnelUp].</summary>
    public Dictionary<string, Dictionary<string, long[]>> Apps { get; set; } = new(StringComparer.Ordinal);

    /// <summary>date → site → [download, upload].</summary>
    public Dictionary<string, Dictionary<string, long[]>> Sites { get; set; } = new(StringComparer.Ordinal);

    /// <summary>date → "app\tsite" → [download, upload] (kept shorter).</summary>
    public Dictionary<string, Dictionary<string, long[]>> AppSites { get; set; } = new(StringComparer.Ordinal);

    /// <summary>app → first seen (unix seconds).</summary>
    public Dictionary<string, long> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>app → executable path.</summary>
    public Dictionary<string, string> Exes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<UsageEvent> Events { get; set; } = [];

    /// <summary>alert key → date it fired (each alert once per day/period).</summary>
    public Dictionary<string, string> Alerted { get; set; } = new(StringComparer.Ordinal);

    /// <summary>adapter → [rx, tx] last read (usage while the app was closed is added on start).</summary>
    public Dictionary<string, long[]> Last { get; set; } = new(StringComparer.Ordinal);

    public long FirstRun { get; set; }
    public long EventId { get; set; }

    /// <summary>Highest download speed seen (bytes/s).</summary>
    public long Peak { get; set; }
}

public sealed record UsageEvent
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }

    /// <summary>cap, daily, app-limit, spike, new-app, upload, updates, info.</summary>
    public string Kind { get; init; } = "info";

    /// <summary>info, warn, crit.</summary>
    public string Level { get; init; } = "info";

    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? App { get; init; }
    public bool Seen { get; init; }
}

public enum UsageLevel
{
    Good = 0,
    Warning,
    Serious,
    Critical,
}

/// <summary>The period's numbers (null cap = no cap set).</summary>
public sealed record UsageBudget(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    long Used,
    long? Cap,
    double Percent,
    long Remaining,
    long DailyAllowance,
    long Average7,
    long Forecast,
    DateOnly? RunOut,
    UsageLevel Level,
    long Today,
    long? DailyCap);

public sealed record UsageRow(string Key, long Down, long Up, long TunnelDown, long TunnelUp)
{
    public long Total => Down + Up;
}

public sealed record UsagePoint(string Label, long Down, long Up);

/// <summary>In-memory usage book with the queries the screens need.</summary>
public sealed class UsageBook
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private bool _dirty;

    public UsageBook(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        Data = Load(path);
        if (Data.FirstRun == 0)
        {
            Data.FirstRun = _time.GetUtcNow().ToUnixTimeSeconds();
            _dirty = true;
        }
    }

    internal UsageData Data { get; private set; }

    internal Lock Gate => _gate;

    public DateTime LocalNow => _time.GetLocalNow().DateTime;

    public static string Day(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal void MarkDirty() => _dirty = true;

    /// <summary>Highest download speed seen (bytes/s).</summary>
    public long Peak => Data.Peak;

    // ------------------------------------------------------------------ recording (callers hold Gate)

    internal void AddDevice(DateTime local, long down, long up)
    {
        var day = Day(local);
        var d = Row(Data.Days, day, 6);
        d[0] += down;
        d[1] += up;
        var h = Row(Data.Hours, day, 48);
        h[local.Hour * 2] += down;
        h[(local.Hour * 2) + 1] += up;
        _dirty = true;
    }

    internal void AddLan(DateTime local, long down, long up)
    {
        var d = Row(Data.Days, Day(local), 6);
        d[2] += down;
        d[3] += up;
    }

    internal void AddTunnel(DateTime local, long down, long up)
    {
        var d = Row(Data.Days, Day(local), 6);
        d[4] += down;
        d[5] += up;
    }

    internal void AddApp(DateTime local, string app, string? exe, string site, long down, long up, bool tunnel)
    {
        var day = Day(local);
        var a = Row(Map(Data.Apps, day), app, 4);
        a[0] += down;
        a[1] += up;
        if (tunnel)
        {
            a[2] += down;
            a[3] += up;
        }

        var s = Row(Map(Data.Sites, day), site, 2);
        s[0] += down;
        s[1] += up;
        var x = Row(Map(Data.AppSites, day), app + "\t" + site, 2);
        x[0] += down;
        x[1] += up;
        if (!Data.Known.ContainsKey(app))
        {
            Data.Known[app] = _time.GetUtcNow().ToUnixTimeSeconds();
        }

        if (exe is not null && !Data.Exes.ContainsKey(app) && exe.Length < 400)
        {
            Data.Exes[app] = exe;
        }

        _dirty = true;
    }

    internal UsageEvent AddEvent(string kind, string level, string title, string message, string? app = null)
    {
        var e = new UsageEvent { Id = ++Data.EventId, At = _time.GetUtcNow(), Kind = kind, Level = level, Title = title, Message = message, App = app };
        Data.Events.Add(e);
        if (Data.Events.Count > 300)
        {
            Data.Events.RemoveRange(0, Data.Events.Count - 300);
        }

        _dirty = true;
        return e;
    }

    private static long[] Row(Dictionary<string, long[]> map, string key, int size)
    {
        if (!map.TryGetValue(key, out var row) || row.Length < size)
        {
            var grown = new long[size];
            row?.CopyTo(grown, 0);
            map[key] = row = grown;
        }

        return row;
    }

    private static Dictionary<string, long[]> Map(Dictionary<string, Dictionary<string, long[]>> map, string day)
    {
        if (!map.TryGetValue(day, out var m))
        {
            map[day] = m = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        }

        return m;
    }

    /// <summary>Drops detail older than the retention (daily totals are kept forever).</summary>
    internal void Prune(int keepDays)
    {
        var today = DateOnly.FromDateTime(LocalNow);
        static void Cut<T>(Dictionary<string, T> map, DateOnly before)
        {
            foreach (var k in map.Keys.Where(k => DateOnly.TryParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < before).ToList())
            {
                map.Remove(k);
            }
        }

        Cut(Data.Hours, today.AddDays(-Math.Max(14, keepDays)));
        Cut(Data.Apps, today.AddDays(-Math.Max(14, keepDays)));
        Cut(Data.Sites, today.AddDays(-Math.Min(60, Math.Max(14, keepDays))));
        Cut(Data.AppSites, today.AddDays(-30));
        foreach (var k in Data.Alerted.Where(kv => DateOnly.TryParseExact(kv.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < today.AddDays(-62)).Select(kv => kv.Key).ToList())
        {
            Data.Alerted.Remove(k);
        }

        _dirty = true;
    }

    // ------------------------------------------------------------------ persistence

    public void Save(bool force = false)
    {
        string json;
        lock (_gate)
        {
            if (!_dirty && !force)
            {
                return;
            }

            json = JsonSerializer.Serialize(Data, JsonDefaults.Storage);
            _dirty = false;
        }

        try
        {
            SecureFileSystem.WriteAllTextAtomic(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
        }
    }

    private static UsageData Load(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 64 * 1024 * 1024)
            {
                var data = JsonSerializer.Deserialize<UsageData>(File.ReadAllText(path), JsonDefaults.Storage);
                if (data is not null)
                {
                    // Case-insensitive lookups after a round trip.
                    data.Known = new Dictionary<string, long>(data.Known ?? [], StringComparer.OrdinalIgnoreCase);
                    data.Exes = new Dictionary<string, string>(data.Exes ?? [], StringComparer.OrdinalIgnoreCase);
                    data.Apps = Rekey(data.Apps);
                    data.Sites = Rekey(data.Sites);
                    data.AppSites = Rekey(data.AppSites);
                    data.Days ??= new(StringComparer.Ordinal);
                    data.Hours ??= new(StringComparer.Ordinal);
                    data.Events ??= [];
                    data.Alerted ??= new(StringComparer.Ordinal);
                    data.Last ??= new(StringComparer.Ordinal);
                    return data;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A corrupt file only loses history.
        }

        return new UsageData();
    }

    private static Dictionary<string, Dictionary<string, long[]>> Rekey(Dictionary<string, Dictionary<string, long[]>>? map) =>
        (map ?? []).ToDictionary(kv => kv.Key, kv => new Dictionary<string, long[]>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);

    // ------------------------------------------------------------------ queries

    /// <summary>Days of the last <paramref name="days"/> (today included); 0 = the current period.</summary>
    public IReadOnlyList<string> DaysOf(int days, UsageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var today = DateOnly.FromDateTime(LocalNow);
        var start = days <= 0 ? Period(settings, today).Start : today.AddDays(1 - days);
        var list = new List<string>();
        for (var d = start; d <= today; d = d.AddDays(1))
        {
            list.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return list;
    }

    public IReadOnlyList<UsageRow> Apps(int days, UsageSettings settings) => Sum(Data.Apps, DaysOf(days, settings));

    public IReadOnlyList<UsageRow> Sites(int days, UsageSettings settings) => Sum(Data.Sites, DaysOf(days, settings));

    /// <summary>Sites one app used.</summary>
    public IReadOnlyList<UsageRow> SitesOf(string app, int days, UsageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);
        var prefix = app + "\t";
        lock (_gate)
        {
            var acc = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in DaysOf(days, settings))
            {
                if (!Data.AppSites.TryGetValue(day, out var m))
                {
                    continue;
                }

                foreach (var (k, v) in m)
                {
                    if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var r = Row(acc, k[prefix.Length..], 2);
                        r[0] += v[0];
                        r[1] += v[1];
                    }
                }
            }

            return acc.Select(kv => new UsageRow(kv.Key, kv.Value[0], kv.Value[1], 0, 0)).OrderByDescending(r => r.Total).ToList();
        }
    }

    /// <summary>Apps that used one site.</summary>
    public IReadOnlyList<UsageRow> AppsOf(string site, int days, UsageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(site);
        var suffix = "\t" + site;
        lock (_gate)
        {
            var acc = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in DaysOf(days, settings))
            {
                if (!Data.AppSites.TryGetValue(day, out var m))
                {
                    continue;
                }

                foreach (var (k, v) in m)
                {
                    if (k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var r = Row(acc, k[..^suffix.Length], 2);
                        r[0] += v[0];
                        r[1] += v[1];
                    }
                }
            }

            return acc.Select(kv => new UsageRow(kv.Key, kv.Value[0], kv.Value[1], 0, 0)).OrderByDescending(r => r.Total).ToList();
        }
    }

    private IReadOnlyList<UsageRow> Sum(Dictionary<string, Dictionary<string, long[]>> source, IReadOnlyList<string> days)
    {
        lock (_gate)
        {
            var acc = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in days)
            {
                if (!source.TryGetValue(day, out var m))
                {
                    continue;
                }

                foreach (var (k, v) in m)
                {
                    var r = Row(acc, k, 4);
                    for (var i = 0; i < Math.Min(4, v.Length); i++)
                    {
                        r[i] += v[i];
                    }
                }
            }

            return acc.Select(kv => new UsageRow(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3])).OrderByDescending(r => r.Total).ToList();
        }
    }

    /// <summary>Daily device totals for the last n days (oldest first).</summary>
    public IReadOnlyList<UsagePoint> Daily(int n)
    {
        var today = DateOnly.FromDateTime(LocalNow);
        lock (_gate)
        {
            var list = new List<UsagePoint>(n);
            for (var i = n - 1; i >= 0; i--)
            {
                var key = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var d = Data.Days.TryGetValue(key, out var v) ? v : null;
                list.Add(new UsagePoint(key, d?[0] ?? 0, d?[1] ?? 0));
            }

            return list;
        }
    }

    /// <summary>One app's daily usage for the last n days.</summary>
    public IReadOnlyList<UsagePoint> AppDaily(string app, int n)
    {
        var today = DateOnly.FromDateTime(LocalNow);
        lock (_gate)
        {
            var list = new List<UsagePoint>(n);
            for (var i = n - 1; i >= 0; i--)
            {
                var key = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var v = Data.Apps.TryGetValue(key, out var m) && m.TryGetValue(app, out var r) ? r : null;
                list.Add(new UsagePoint(key, v?[0] ?? 0, v?[1] ?? 0));
            }

            return list;
        }
    }

    /// <summary>Hour-by-hour usage of a day (24 points).</summary>
    public IReadOnlyList<UsagePoint> Hourly(DateOnly day)
    {
        lock (_gate)
        {
            var h = Data.Hours.TryGetValue(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var v) ? v : new long[48];
            return Enumerable.Range(0, 24).Select(i => new UsagePoint(i.ToString("00", CultureInfo.InvariantCulture), h[i * 2], h[(i * 2) + 1])).ToList();
        }
    }

    /// <summary>Totals of a day: [down, up, lanDown, lanUp, tunnelDown, tunnelUp].</summary>
    public long[] DayTotals(DateOnly day)
    {
        lock (_gate)
        {
            return Data.Days.TryGetValue(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var v) ? (long[])v.Clone() : new long[6];
        }
    }

    public IReadOnlyList<UsageEvent> Events()
    {
        lock (_gate)
        {
            return Data.Events.AsEnumerable().Reverse().ToList();
        }
    }

    public int UnseenEvents()
    {
        lock (_gate)
        {
            return Data.Events.Count(e => !e.Seen);
        }
    }

    public void MarkEventsSeen()
    {
        lock (_gate)
        {
            for (var i = 0; i < Data.Events.Count; i++)
            {
                Data.Events[i] = Data.Events[i] with { Seen = true };
            }

            _dirty = true;
        }
    }

    public void ClearEvents()
    {
        lock (_gate)
        {
            Data.Events.Clear();
            _dirty = true;
        }
    }

    public bool IsNewApp(string app, TimeSpan within)
    {
        lock (_gate)
        {
            return Data.Known.TryGetValue(app, out var first) && _time.GetUtcNow().ToUnixTimeSeconds() - first < within.TotalSeconds
                && first - Data.FirstRun > 86400;
        }
    }

    public string? ExeOf(string app)
    {
        lock (_gate)
        {
            return Data.Exes.TryGetValue(app, out var p) ? p : null;
        }
    }

    public void Reset(string scope)
    {
        lock (_gate)
        {
            switch (scope)
            {
                case "today":
                    var day = Day(LocalNow);
                    Data.Days.Remove(day);
                    Data.Hours.Remove(day);
                    Data.Apps.Remove(day);
                    Data.Sites.Remove(day);
                    Data.AppSites.Remove(day);
                    break;
                case "apps":
                    Data.Apps.Clear();
                    Data.Sites.Clear();
                    Data.AppSites.Clear();
                    Data.Known.Clear();
                    break;
                default:
                    var last = Data.Last;
                    Data = new UsageData { FirstRun = _time.GetUtcNow().ToUnixTimeSeconds(), Last = last };
                    break;
            }

            _dirty = true;
        }
    }

    /// <summary>CSV export: days | hours | apps | sites | events.</summary>
    public string ExportCsv(string kind, int days, UsageSettings settings)
    {
        var sb = new StringBuilder();
        switch (kind)
        {
            case "apps":
            case "sites":
                sb.AppendLine(kind == "apps" ? "app,download,upload,tunnel_download,tunnel_upload" : "site,download,upload");
                foreach (var r in kind == "apps" ? Apps(days, settings) : Sites(days, settings))
                {
                    sb.Append(Csv(r.Key)).Append(',').Append(r.Down).Append(',').Append(r.Up);
                    if (kind == "apps")
                    {
                        sb.Append(',').Append(r.TunnelDown).Append(',').Append(r.TunnelUp);
                    }

                    sb.AppendLine();
                }

                break;
            case "hours":
                sb.AppendLine("date,hour,download,upload");
                lock (_gate)
                {
                    foreach (var (day, h) in Data.Hours.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        for (var i = 0; i < 24; i++)
                        {
                            sb.Append(day).Append(',').Append(i).Append(',').Append(h[i * 2]).Append(',').Append(h[(i * 2) + 1]).AppendLine();
                        }
                    }
                }

                break;
            case "events":
                sb.AppendLine("time,kind,level,title,message");
                foreach (var e in Events())
                {
                    sb.Append(e.At.ToString("o", CultureInfo.InvariantCulture)).Append(',').Append(e.Kind).Append(',').Append(e.Level).Append(',').Append(Csv(e.Title)).Append(',').Append(Csv(e.Message)).AppendLine();
                }

                break;
            default:
                sb.AppendLine("date,download,upload,lan_download,lan_upload,tunnel_download,tunnel_upload");
                lock (_gate)
                {
                    foreach (var (day, d) in Data.Days.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        sb.Append(day);
                        foreach (var v in d)
                        {
                            sb.Append(',').Append(v);
                        }

                        sb.AppendLine();
                    }
                }

                break;
        }

        return sb.ToString();
    }

    private static string Csv(string s) => s.Contains(',', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal) || s.Contains('\n', StringComparison.Ordinal)
        ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : s;

    // ------------------------------------------------------------------ budget

    /// <summary>The usage period that contains <paramref name="today"/>.</summary>
    public static (DateOnly Start, DateOnly End) Period(UsageSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (settings.Cycle)
        {
            case UsageCycle.Gregorian:
                var g = new DateOnly(today.Year, today.Month, 1);
                return (g, g.AddMonths(1).AddDays(-1));
            case UsageCycle.Custom when settings.CycleStart is { } first:
                var len = Math.Clamp(settings.CycleLength, 1, 366);
                var start = first;
                if (start > today)
                {
                    while (start > today)
                    {
                        start = start.AddDays(-len);
                    }
                }
                else
                {
                    var periods = (today.DayNumber - start.DayNumber) / len;
                    start = start.AddDays(periods * len);
                }

                return (start, start.AddDays(len - 1));
            default:
                var pc = new PersianCalendar();
                var dt = today.ToDateTime(TimeOnly.MinValue);
                var y = pc.GetYear(dt);
                var m = pc.GetMonth(dt);
                var s = DateOnly.FromDateTime(pc.ToDateTime(y, m, 1, 0, 0, 0, 0));
                var e = DateOnly.FromDateTime(pc.AddMonths(pc.ToDateTime(y, m, 1, 0, 0, 0, 0), 1)).AddDays(-1);
                return (s, e);
        }
    }

    /// <summary>Counted usage of a day: free hours excluded, local-network traffic excluded when asked.</summary>
    private long Counted(string day, UsageSettings settings)
    {
        if (!Data.Days.TryGetValue(day, out var d))
        {
            return 0;
        }

        long total;
        if (settings.FreeFromHour != settings.FreeToHour && Data.Hours.TryGetValue(day, out var h))
        {
            total = 0;
            for (var i = 0; i < 24; i++)
            {
                var free = settings.FreeFromHour < settings.FreeToHour
                    ? i >= settings.FreeFromHour && i < settings.FreeToHour
                    : i >= settings.FreeFromHour || i < settings.FreeToHour;
                if (!free)
                {
                    total += h[i * 2] + h[(i * 2) + 1];
                }
            }
        }
        else
        {
            total = d[0] + d[1];
        }

        if (settings.ExcludeLan && d.Length >= 4)
        {
            total = Math.Max(0, total - d[2] - d[3]);
        }

        return total;
    }

    public UsageBudget Budget(UsageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var today = DateOnly.FromDateTime(LocalNow);
        var (start, end) = Period(settings, today);
        lock (_gate)
        {
            long used = 0;
            for (var d = start; d <= today; d = d.AddDays(1))
            {
                used += Counted(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), settings);
            }

            long last7 = 0;
            var counted = 0;
            for (var i = 1; i <= 7; i++)
            {
                var key = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (Data.Days.ContainsKey(key))
                {
                    last7 += Counted(key, settings);
                    counted++;
                }
            }

            var todayUse = Counted(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), settings);
            var avg = counted > 0 ? last7 / counted : todayUse;
            var daysLeft = end.DayNumber - today.DayNumber;
            var forecast = used + (avg * daysLeft);
            long? cap = settings.CapGb > 0 ? (long)(settings.CapGb * 1024 * 1024 * 1024) : null;
            long? dailyCap = settings.DailyGb > 0 ? (long)(settings.DailyGb * 1024 * 1024 * 1024) : null;
            var remaining = cap is { } c ? Math.Max(0, c - used) : 0;
            var pct = cap is { } c2 && c2 > 0 ? used * 100.0 / c2 : 0;
            DateOnly? runOut = cap is not null && avg > 0 ? today.AddDays((int)Math.Min(3650, remaining / avg)) : null;
            var level = cap is null
                ? UsageLevel.Good
                : pct >= 100 ? UsageLevel.Critical
                : pct >= 90 ? UsageLevel.Serious
                : pct >= 70 || forecast > cap ? UsageLevel.Warning
                : UsageLevel.Good;
            return new UsageBudget(start, end, used, cap, pct, remaining, cap is null ? 0 : remaining / Math.Max(1, daysLeft + 1), avg, forecast, runOut, level, todayUse, dailyCap);
        }
    }

    /// <summary>"www.google.com" → "google.com", "a.b.co.uk" → "b.co.uk"; IPs stay as they are.</summary>
    public static string SiteOf(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "?";
        }

        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (NetText.IsIpLiteral(h))
        {
            return h;
        }

        var labels = h.Split('.');
        if (labels.Length <= 2)
        {
            return h;
        }

        var sld = labels[^2];
        var take = labels[^1].Length == 2 && sld is "co" or "com" or "net" or "org" or "ac" or "gov" or "edu" or "sch" or "id" ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }
}
