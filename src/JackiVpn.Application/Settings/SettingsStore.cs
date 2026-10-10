using System;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Application.Settings;

/// <summary>
/// Single in-memory copy of the immutable settings record. Updates are applied atomically with
/// <c>with</c>-expressions and persisted in the background (coalesced), so UI edits never block on disk.
/// </summary>
public sealed class SettingsStore(ISettingsRepository repository, ILogger logger)
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _current = new();
    private int _pendingSave;

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var loaded = await repository.LoadAsync(ct).ConfigureAwait(false);
        var upgraded = SettingsMigrations.Upgrade(loaded);
        lock (_gate)
        {
            _current = upgraded;
        }

        if (upgraded != loaded)
        {
            logger.LogInformation("Settings upgraded from schema {From} to {To}.", loaded.SchemaVersion, upgraded.SchemaVersion);
            await FlushAsync().ConfigureAwait(false);
        }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        lock (_gate)
        {
            updated = change(_current);
            if (ReferenceEquals(updated, _current) || updated == _current)
            {
                return;
            }

            _current = updated;
        }

        Changed?.Invoke(this, updated);
        if (Interlocked.Exchange(ref _pendingSave, 1) == 0)
        {
            _ = Task.Run(SaveSoonAsync);
        }
    }

    private async Task SaveSoonAsync()
    {
        await Task.Delay(400).ConfigureAwait(false);
        Interlocked.Exchange(ref _pendingSave, 0);
        await FlushAsync().ConfigureAwait(false);
    }

    public async Task FlushAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await repository.SaveAsync(Current, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError("Saving settings failed: {Error}", ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
