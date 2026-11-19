using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Privacy;

/// <summary>
/// The platform's way to change the device time zone. Implementations keep the original durably (Windows: the
/// service's journal; Linux: a small file) so a crash is undone by <see cref="RestoreAsync"/> at the next start.
/// </summary>
public interface ITimeZoneControl
{
    Task ApplyAsync(string ianaId, CancellationToken ct);

    /// <summary>Puts the original back; a no-op when nothing was changed.</summary>
    Task RestoreAsync(CancellationToken ct);
}

/// <summary>
/// Follows the connection: while connected (and the setting is on) the device zone is the exit's; it goes back when
/// the session ends. Reconnects and server switches keep the zone (no flicker); a new exit zone is applied as soon
/// as the lookup reports it. Every change is serialized; failures are logged, never thrown at the connection.
/// </summary>
public sealed class TimeZoneSync(ITimeZoneControl control, Func<AppSettings> settings, ILogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _applied;

    /// <summary>The zone currently applied by IBOX (null = the user's own).</summary>
    public string? Applied => _applied;

    /// <summary>At app start: undo what a crashed run left behind.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await control.RestoreAsync(ct).ConfigureAwait(false);
            _applied = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Time zone recovery failed: {Error}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnStatusAsync(ConnectionStatus status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(status);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var want = Desired(status, settings().Privacy.MatchTimeZone, _applied);
            if (want == _applied)
            {
                return;
            }

            if (want is null)
            {
                await control.RestoreAsync(ct).ConfigureAwait(false);
                _applied = null;
                logger.LogInformation("Time zone back to the user's own.");
            }
            else
            {
                await control.ApplyAsync(want, ct).ConfigureAwait(false);
                _applied = want;
                logger.LogInformation("Time zone matched to the exit: {Zone}.", want);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Time zone change failed: {Error}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fire-and-forget hook for <c>StatusChanged</c>.</summary>
    public void Observe(ConnectionStatus status) => _ = OnStatusAsync(status, CancellationToken.None);

    /// <summary>
    /// The zone that should be in effect for <paramref name="status"/> (null = the user's own). Connected → the exit's
    /// zone (or keep the current one while the lookup is pending); reconnecting or switching → keep; else none.
    /// </summary>
    public static string? Desired(ConnectionStatus status, bool enabled, string? current)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!enabled)
        {
            return null;
        }

        return status.State switch
        {
            ConnectionState.Connected => status.ExitTimeZone is { } z && TimeZoneMap.IsIanaId(z) ? z : current,
            ConnectionState.Reconnecting or ConnectionState.SwitchingServer or ConnectionState.Recovering => current,
            _ => null,
        };
    }
}
