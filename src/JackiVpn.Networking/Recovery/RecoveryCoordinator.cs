using System.Diagnostics;
using JackiVpn.Networking.Journal;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Networking.Recovery;

public sealed record RecoveryAction(string Step, bool Success, string Detail);

public sealed record RecoveryReport(bool WasDirty, IReadOnlyList<RecoveryAction> Actions)
{
    public bool Success => Actions.All(a => a.Success);

    public static RecoveryReport Clean { get; } = new(false, []);
}

public interface IRecoveryHandler
{
    JournalEntryKind Kind { get; }

    RecoveryAction Recover(JournalEntry entry);
}

/// <summary>An unconditional sweep run by "Repair Network" even when the journal is clean.</summary>
public interface IRepairStep
{
    string Name { get; }

    RecoveryAction Run();
}

public sealed record CoreProcessJournalData(int Pid, string ImagePath, DateTimeOffset StartTimeUtc);

public sealed record TunAdapterJournalData(string InterfaceName);

/// <summary>Kills a core process recorded in the journal — only if PID, image path and start time all match.</summary>
public sealed class OrphanCoreHandler(ILogger logger) : IRecoveryHandler
{
    public JournalEntryKind Kind => JournalEntryKind.CoreProcess;

    public RecoveryAction Recover(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var data = entry.As<CoreProcessJournalData>();
        try
        {
            using var p = Process.GetProcessById(data.Pid);
            var path = p.MainModule?.FileName;
            var startDelta = Math.Abs((p.StartTime.ToUniversalTime() - data.StartTimeUtc.UtcDateTime).TotalSeconds);
            if (!string.Equals(path, data.ImagePath, StringComparison.OrdinalIgnoreCase) || startDelta > 5)
            {
                return new RecoveryAction("Orphan core", true, $"PID {data.Pid} now belongs to another process; left alone.");
            }

            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            logger.LogWarning("Killed orphaned core {Path} (pid {Pid}) from a previous session.", Path.GetFileName(path), data.Pid);
            return new RecoveryAction("Orphan core", true, $"Killed orphaned {Path.GetFileName(path)} (pid {data.Pid}).");
        }
        catch (ArgumentException)
        {
            return new RecoveryAction("Orphan core", true, $"PID {data.Pid} is no longer running.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new RecoveryAction("Orphan core", false, $"Could not inspect/kill pid {data.Pid}: {ex.Message}");
        }
    }
}

/// <summary>
/// Runs journal-driven recovery at startup (dirty previous session) and the full "Repair Network" command.
/// Each handler undoes one kind of change; entries are completed only when their handler succeeded, so a
/// failed step is retried next time instead of being forgotten.
/// </summary>
public sealed class RecoveryCoordinator(NetworkStateJournal journal, IEnumerable<IRecoveryHandler> handlers, IEnumerable<IRepairStep> repairSteps, ILogger logger)
{
    private readonly Dictionary<JournalEntryKind, IRecoveryHandler> _handlers = handlers.ToDictionary(h => h.Kind);
    private readonly IReadOnlyList<IRepairStep> _repairSteps = repairSteps.ToList();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool NeedsRecovery => journal.IsDirty;

    public async Task<RecoveryReport> RecoverAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!journal.IsDirty)
            {
                return RecoveryReport.Clean;
            }

            logger.LogWarning("Previous session did not shut down cleanly ({Count} pending change(s), corrupt journal: {Corrupt}); recovering.", journal.Entries.Count, journal.WasCorrupt);
            var actions = RunJournal();
            if (journal.WasCorrupt)
            {
                // Unknown prior state: run every unconditional sweep as well.
                actions.AddRange(RunRepairSteps());
                journal.AcknowledgeCorruption();
            }

            journal.ClearIfEmpty();
            return new RecoveryReport(true, actions);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecoveryReport> RepairAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dirty = journal.IsDirty;
            var actions = RunJournal();
            actions.AddRange(RunRepairSteps());
            journal.AcknowledgeCorruption();
            journal.ClearIfEmpty();
            logger.LogInformation("Repair Network finished: {Ok}/{Total} steps succeeded.", actions.Count(a => a.Success), actions.Count);
            return new RecoveryReport(dirty, actions);
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<RecoveryAction> RunRepairSteps()
    {
        var actions = new List<RecoveryAction>();
        foreach (var step in _repairSteps)
        {
            try
            {
                actions.Add(step.Run());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                actions.Add(new RecoveryAction(step.Name, false, ex.Message));
            }
        }

        return actions;
    }

    private List<RecoveryAction> RunJournal()
    {
        var actions = new List<RecoveryAction>();

        // Order matters: stop orphaned cores first, then restore settings, remove the firewall last-but-one
        // (after cores are gone so nothing leaks), then adapters.
        var order = new[] { JournalEntryKind.CoreProcess, JournalEntryKind.WinInetProxy, JournalEntryKind.WinHttpProxy, JournalEntryKind.KillSwitch, JournalEntryKind.TunAdapter };
        foreach (var kind in order)
        {
            foreach (var entry in journal.OfKind(kind))
            {
                if (!_handlers.TryGetValue(kind, out var handler))
                {
                    continue;
                }

                RecoveryAction action;
                try
                {
                    action = handler.Recover(entry);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    action = new RecoveryAction(kind.ToString(), false, ex.Message);
                }

                actions.Add(action);
                if (action.Success)
                {
                    journal.Complete(entry.Id);
                }
            }
        }

        return actions;
    }
}
