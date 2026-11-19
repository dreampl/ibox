using System.Diagnostics;
using JackiVpn.Networking.Journal;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Networking.Privacy;

/// <summary>The machine's time zone, by Windows id (e.g. "W. Europe Standard Time").</summary>
public interface ITimeZoneApi
{
    string GetCurrentId();

    void Set(string windowsId);

    bool Exists(string windowsId);
}

/// <summary>The journaled original, so a crash or uninstall puts the user's own zone back.</summary>
public sealed record TimeZoneJournalData(string OriginalId);

/// <summary>
/// Windows time zone through <c>tzutil</c> (the service runs as LocalSystem, which holds the time-zone privilege).
/// tzutil fills the full dynamic time-zone record from the registry, so daylight-saving rules stay correct.
/// </summary>
public sealed class WindowsTimeZoneApi : ITimeZoneApi
{
    public string GetCurrentId()
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local.Id;
    }

    public void Set(string windowsId)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowsId);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tzutil.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add(windowsId);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("tzutil could not be started.");
        var error = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(15_000) || p.ExitCode != 0)
        {
            throw new InvalidOperationException($"tzutil /s failed ({(p.HasExited ? p.ExitCode : -1)}): {error.Trim()}");
        }

        TimeZoneInfo.ClearCachedData();
    }

    public bool Exists(string windowsId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId).Id.Equals(windowsId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

/// <summary>
/// Sets the machine time zone for a session and puts the original back. Write-ahead like every other change: the
/// original is journaled before the first change, kept across server switches (a second Apply never overwrites
/// it), and removed only after it was restored.
/// </summary>
public sealed class TimeZoneManager(ITimeZoneApi api, NetworkStateJournal journal, ILogger logger)
{
    public const string JournalId = "time-zone";

    /// <summary>Applies <paramref name="windowsId"/>; false when it is unknown on this machine.</summary>
    public bool Apply(string windowsId)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowsId);
        if (!api.Exists(windowsId))
        {
            return false;
        }

        var existing = journal.OfKind(JournalEntryKind.TimeZone).FirstOrDefault();
        var original = existing?.As<TimeZoneJournalData>().OriginalId ?? api.GetCurrentId();
        if (existing is null)
        {
            journal.Record(JournalEntryKind.TimeZone, new TimeZoneJournalData(original), JournalId);
        }

        if (!string.Equals(api.GetCurrentId(), windowsId, StringComparison.OrdinalIgnoreCase))
        {
            api.Set(windowsId);
            logger.LogInformation("Time zone set to {Zone} for the session (was {Original}).", windowsId, original);
        }

        return true;
    }

    /// <summary>Puts the journaled original back; false when nothing was changed.</summary>
    public bool Restore()
    {
        var entry = journal.OfKind(JournalEntryKind.TimeZone).FirstOrDefault();
        if (entry is null)
        {
            return false;
        }

        var original = entry.As<TimeZoneJournalData>().OriginalId;
        if (api.Exists(original) && !string.Equals(api.GetCurrentId(), original, StringComparison.OrdinalIgnoreCase))
        {
            api.Set(original);
        }

        journal.Complete(entry.Id);
        logger.LogInformation("Time zone restored to {Zone}.", original);
        return true;
    }
}
