using System.Diagnostics;
using JackiVpn.Application.Connection;
using JackiVpn.Application.Health;
using JackiVpn.Application.Settings;
using JackiVpn.Application.Subscriptions;
using JackiVpn.Domain.Abstractions;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Health;
using JackiVpn.Domain.Settings;
using JackiVpn.Domain.Subscriptions;

namespace JackiVpn.Application.Automations;

/// <summary>The real app behind <see cref="IAutomationEnvironment"/> (the same services on Windows, Android and Linux).</summary>
public sealed class AppAutomationEnvironment : IAutomationEnvironment, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ConnectionOrchestrator _orchestrator;
    private readonly HealthCheckService _health;
    private readonly SubscriptionService _subscriptionService;
    private readonly IProfileRepository _profiles;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly Action<NotificationCategory, string, string> _notify;
    private readonly Func<NetworkInfo> _network;

    public AppAutomationEnvironment(
        SettingsStore settings,
        ConnectionOrchestrator orchestrator,
        HealthCheckService health,
        SubscriptionService subscriptionService,
        IProfileRepository profiles,
        ISubscriptionRepository subscriptions,
        Action<NotificationCategory, string, string> notify,
        Func<NetworkInfo>? network = null)
    {
        _settings = settings;
        _orchestrator = orchestrator;
        _health = health;
        _subscriptionService = subscriptionService;
        _profiles = profiles;
        _subscriptions = subscriptions;
        _notify = notify;
        _network = network ?? NetworkIdentity.Current;
        _orchestrator.StatusChanged += OnStatus;
        _orchestrator.NetworkChanged += OnNetwork;
    }

    public TimeProvider Time => TimeProvider.System;

    public AutomationSettings Settings => _settings.Current.Automations;

    public ConnectionStatus Status => _orchestrator.Status;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public event EventHandler? NetworkChanged;

    public NetworkInfo CurrentNetwork() => _network();

    public IReadOnlyCollection<NodeHealthSnapshot> Health() => _health.AllSnapshots;

    public async Task<IReadOnlyDictionary<string, string>> ServerNamesAsync(CancellationToken ct) =>
        (await _profiles.GetAllAsync(ct).ConfigureAwait(false)).ToDictionary(p => p.Id, p => p.DisplayName, StringComparer.Ordinal);

    public Task<IReadOnlyList<Subscription>> SubscriptionsAsync(CancellationToken ct) => _subscriptions.GetAllAsync(ct);

    public Task RefreshSubscriptionAsync(string id, CancellationToken ct) => _subscriptionService.RefreshAsync(id, ct);

    public Task DeleteServerAsync(string profileId, CancellationToken ct) => _profiles.DeleteAsync(profileId, ct);

    public Task<bool> ConnectAsync(CancellationToken ct) =>
        _orchestrator.ConnectAsync(new ConnectRequest { ProfileId = _settings.Current.Connection.SelectedProfileId }, ct);

    public Task DisconnectAsync(CancellationToken ct) => _orchestrator.DisconnectAsync(ct);

    public Task<bool> SwitchToAsync(string profileId, CancellationToken ct) => _orchestrator.SwitchToAsync(profileId, ct);

    public void Notify(NotificationCategory category, string title, string message) => _notify(category, title, message);

    private void OnStatus(object? sender, ConnectionStatus e) => StatusChanged?.Invoke(this, e);

    private void OnNetwork(object? sender, string e) => NetworkChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _orchestrator.StatusChanged -= OnStatus;
        _orchestrator.NetworkChanged -= OnNetwork;
    }
}

/// <summary>Wi-Fi name on Windows (<c>netsh wlan</c>) and Linux (<c>nmcli</c>). Android passes its own provider.</summary>
public static class NetworkIdentity
{
    public static NetworkInfo Current()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ParseNetsh(Run("netsh", "wlan show interfaces"));
            }

            if (OperatingSystem.IsLinux())
            {
                return ParseNmcli(Run("nmcli", "-t -f ACTIVE,SSID dev wifi"));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            // No Wi-Fi tool: unknown network, the trusted-network automation then does nothing.
        }

        return NetworkInfo.Unknown;
    }

    /// <summary>Reads the connected SSID from <c>netsh wlan show interfaces</c>. The labels are localized: on a non-English Windows this reports an unknown network and the automation stays idle.</summary>
    public static NetworkInfo ParseNetsh(string output)
    {
        string? state = null, ssid = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("State", StringComparison.OrdinalIgnoreCase))
            {
                state = value;
            }
            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
            {
                ssid = value;
            }
        }

        return string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ssid)
            ? new NetworkInfo(NetworkKind.Wifi, ssid)
            : NetworkInfo.Unknown;
    }

    public static NetworkInfo ParseNmcli(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("yes:", StringComparison.Ordinal) && line.Length > 4)
            {
                return new NetworkInfo(NetworkKind.Wifi, line[4..].Trim().Replace("\\:", ":", StringComparison.Ordinal));
            }
        }

        return NetworkInfo.Unknown;
    }

    private static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("not started");
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(3000))
        {
            p.Kill();
            throw new TimeoutException();
        }

        return output.GetAwaiter().GetResult();
    }
}
