using JackiVpn.Application.Connection;
using JackiVpn.Domain.Connection;

namespace JackiVpn.Application.Privacy;

/// <summary>
/// Hooks the privacy features to the connection on every platform: the time zone follows the session, and the
/// "already on our servers" check runs at start and after a network change (only while IBOX is off).
/// </summary>
public sealed class PrivacyFeatures
{
    /// <summary>Lets a new network settle (DHCP, the other app's tunnel) before its public address is read.</summary>
    public static TimeSpan NetworkSettleDelay { get; } = TimeSpan.FromSeconds(4);

    private readonly ConnectionOrchestrator _orchestrator;

    public PrivacyFeatures(ConnectionOrchestrator orchestrator, TimeZoneSync? timeZone, OurServerDetector detector)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(detector);
        _orchestrator = orchestrator;
        TimeZone = timeZone;
        Detector = detector;
        orchestrator.StatusChanged += (_, s) =>
        {
            timeZone?.Observe(s);
            if (s.State != ConnectionState.Disconnected)
            {
                detector.Clear();
            }
        };
        orchestrator.NetworkChanged += (_, _) => _ = CheckAsync(NetworkSettleDelay, CancellationToken.None);
    }

    /// <summary>Null where the platform cannot change the time zone (Android).</summary>
    public TimeZoneSync? TimeZone { get; }

    public OurServerDetector Detector { get; }

    /// <summary>App start: undo a crashed run's time zone, then one check for another app / the router.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (TimeZone is not null)
        {
            await TimeZone.RecoverAsync(ct).ConfigureAwait(false);
        }

        await CheckAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
    }

    /// <summary>A setting changed: turning time-zone matching off restores the zone at once.</summary>
    public void OnSettingsChanged()
    {
        TimeZone?.Observe(_orchestrator.Status);
        _ = CheckAsync(TimeSpan.Zero, CancellationToken.None);
    }

    private async Task CheckAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            await Detector.CheckAsync(_orchestrator.Status.State != ConnectionState.Disconnected, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
