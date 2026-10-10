using JackiVpn.Domain.Settings;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Infrastructure.Updates;

/// <summary>
/// Periodic update check shared by Windows and Android: shortly after start, then every few hours while the app
/// runs (only when the user has not turned automatic checks off). It only <i>finds</i> updates; downloading and
/// installing always start from the user.
/// </summary>
public sealed class UpdateMonitor(AppUpdateService service, Func<UpdateSettings> settings, Version current, string platform, string? abi, ILogger logger)
{
    public static TimeSpan FirstCheckDelay { get; } = TimeSpan.FromSeconds(20);

    public static TimeSpan Interval { get; } = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? Changed;

    public AppUpdateService Service => service;

    public Version CurrentVersion => current;

    /// <summary>The newest update for this device (null = up to date or not checked yet).</summary>
    public AppUpdate? Available { get; private set; }

    public DateTimeOffset? LastCheckUtc { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Available and not skipped by the user (a skipped version is still installable from Settings).</summary>
    public bool ShouldOffer => Available is { } a && (a.Mandatory || !string.Equals(settings().SkippedVersion, a.VersionText, StringComparison.Ordinal));

    public async Task<AppUpdate?> CheckNowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Available = await service.CheckAsync(current, platform, abi, ct).ConfigureAwait(false);
            LastError = null;
        }
        catch (AppUpdateException ex)
        {
            LastError = SecretRedactor.Redact(ex.Message);
            logger.LogInformation("Update check failed: {Error}", LastError);
        }
        finally
        {
            LastCheckUtc = DateTimeOffset.UtcNow;
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Available;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (settings().AutoCheck)
                {
                    await CheckNowAsync(ct).ConfigureAwait(false);
                }

                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
