using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Background;

/// <summary>What the device says about power and data cost (read by the platform; cheap, called rarely).</summary>
public interface IDeviceConditions
{
    /// <summary>Battery saver / power saver is on.</summary>
    bool PowerSaver { get; }

    /// <summary>The active network is metered (mobile data, a Wi-Fi marked as metered, roaming, over the data limit).</summary>
    bool Metered { get; }
}

/// <summary>A device that is never constrained (tests, platforms without the information).</summary>
public sealed class UnconstrainedDevice : IDeviceConditions
{
    public bool PowerSaver => false;

    public bool Metered => false;
}

/// <summary>The kinds of work the app does on its own, from most to least important.</summary>
public enum BackgroundWork
{
    /// <summary>Small upkeep the connection depends on: subscription refresh, account check, update check, DNS search.</summary>
    Upkeep,

    /// <summary>Optional and bursty: handshake tuning, pool re-tests, rule-set downloads (many connections or megabytes).</summary>
    Heavy,
}

/// <summary>
/// One place that decides when the app's own work may run, so it never competes with what the user is doing:
/// <list type="bullet">
/// <item>Upkeep waits while power saver is on and while the user's own traffic is busy (a game, a stream, a big download).</item>
/// <item>Heavy work additionally waits while the app is hidden (minimized / tray / background) and on metered networks.</item>
/// <item>Polling (usage samples, health checks) slows down while hidden or saving power.</item>
/// </list>
/// The live connection's own checks are never paused, only slowed. Waiting is event-driven (a change of window
/// state, power or network wakes the waiters) with a slow re-check, so a paused loop costs nothing.
/// </summary>
public sealed class BackgroundPolicy
{
    /// <summary>How long a reading of the device conditions is trusted (they also refresh on every change event).</summary>
    public static TimeSpan ConditionsTtl { get; } = TimeSpan.FromSeconds(30);

    /// <summary>A paused loop looks again at least this often (conditions without change events, e.g. the user going idle).</summary>
    public static TimeSpan RecheckInterval { get; } = TimeSpan.FromSeconds(60);

    private readonly IDeviceConditions _device;
    private readonly Func<bool> _userBusy;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private bool _hidden;
    private bool _powerSaver;
    private bool _metered;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public BackgroundPolicy(IDeviceConditions device, Func<bool> userBusy, ILogger logger, TimeProvider? time = null)
    {
        _device = device;
        _userBusy = userBusy;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when the window state or the device conditions changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The app is not on screen (minimized, in the tray, or in the background on a phone).</summary>
    public bool Hidden
    {
        get
        {
            lock (_gate)
            {
                return _hidden;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_hidden == value)
                {
                    return;
                }

                _hidden = value;
            }

            Signal();
        }
    }

    public bool PowerSaver
    {
        get
        {
            Read();
            lock (_gate)
            {
                return _powerSaver;
            }
        }
    }

    public bool Metered
    {
        get
        {
            Read();
            lock (_gate)
            {
                return _metered;
            }
        }
    }

    /// <summary>The user's own traffic needs the line right now (game, stream, heavy download).</summary>
    public bool UserBusy
    {
        get
        {
            try
            {
                return _userBusy();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    public bool Allows(BackgroundWork work) => work switch
    {
        BackgroundWork.Upkeep => !PowerSaver && !UserBusy,
        _ => !Hidden && !PowerSaver && !Metered && !UserBusy,
    };

    /// <summary>Why <paramref name="work"/> is waiting (for logs), or null.</summary>
    public string? WaitReason(BackgroundWork work) =>
        PowerSaver ? "power saver"
        : UserBusy ? "busy"
        : work == BackgroundWork.Heavy && Hidden ? "hidden"
        : work == BackgroundWork.Heavy && Metered ? "metered network"
        : null;

    /// <summary>A polling interval for the current state: ×4 while hidden, ×6 on power saver.</summary>
    public TimeSpan Stretch(TimeSpan interval) => interval * (PowerSaver ? 6 : Hidden ? 4 : 1);

    /// <summary>Waits (without polling) until <paramref name="work"/> may run.</summary>
    public async Task WaitUntilAllowedAsync(BackgroundWork work, CancellationToken ct)
    {
        var logged = false;
        while (!Allows(work))
        {
            if (!logged)
            {
                logged = true;
                _logger.LogDebug("{Work} work waits ({Reason}).", work, WaitReason(work));
            }

            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }

            using (var recheck = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await Task.WhenAny(changed, Task.Delay(RecheckInterval, _time, recheck.Token)).ConfigureAwait(false);
                await recheck.CancelAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Re-reads the device conditions now (the platform calls this on power and network change events).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _readAt = DateTimeOffset.MinValue;
        }

        Read();
        Signal();
    }

    private void Read()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (now - _readAt < ConditionsTtl)
            {
                return;
            }

            _readAt = now;
        }

        bool saver, metered;
        try
        {
            saver = _device.PowerSaver;
            metered = _device.Metered;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Device conditions unavailable: {Error}", ex.Message);
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = saver != _powerSaver || metered != _metered;
            _powerSaver = saver;
            _metered = metered;
        }

        if (changed)
        {
            _logger.LogInformation("Device: power saver {Saver}, metered network {Metered}.", saver ? "on" : "off", metered ? "yes" : "no");
            Signal();
        }
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = NewSignal();
        }

        old.TrySetResult();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
