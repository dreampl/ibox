using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Desktop.Composition;
using JackiVpn.Desktop.Services;
using JackiVpn.Domain.Connection;
using JackiVpn.Domain.Settings;
using JackiVpn.Ipc;

namespace JackiVpn.Desktop.ViewModels;

public sealed partial class NetworkViewModel : SettingsPageViewModel
{
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private string _serviceStatus = "Checking…";

    [ObservableProperty]
    private bool _serviceRunning;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _repairOutput;

    public NetworkViewModel(AppServices services, IDialogService dialogs)
        : base(services.Settings)
    {
        _s = services;
        _dialogs = dialogs;
    }

    public IReadOnlyList<Ipv6Mode> Ipv6Modes { get; } = Choices.Of<Ipv6Mode>();

    public bool IsTun
    {
        get => S.Connection.Mode == ConnectionMode.Tun;
        set
        {
            if (value)
            {
                Set(s => s with { Connection = s.Connection with { Mode = ConnectionMode.Tun } });
            }
        }
    }

    public bool IsProxy
    {
        get => S.Connection.Mode == ConnectionMode.SystemProxy;
        set
        {
            if (value)
            {
                Set(s => s with { Connection = s.Connection with { Mode = ConnectionMode.SystemProxy } });
            }
        }
    }

    public Ipv6Mode Ipv6
    {
        get => S.Ipv6;
        set => Set(s => s with { Ipv6 = value });
    }

    public bool KillSwitch
    {
        get => S.KillSwitch.Enabled;
        set => Set(s => s with { KillSwitch = s.KillSwitch with { Enabled = value } });
    }

    public bool ProtectOnDrop
    {
        get => S.KillSwitch.ProtectOnDrop;
        set => Set(s => s with { KillSwitch = s.KillSwitch with { ProtectOnDrop = value } });
    }

    public bool AllowLan
    {
        get => S.KillSwitch.AllowLan;
        set => Set(s => s with { KillSwitch = s.KillSwitch with { AllowLan = value } });
    }

    public bool SystemProxyInTun
    {
        get => S.Connection.SystemProxyInTun;
        set => Set(s => s with { Connection = s.Connection with { SystemProxyInTun = value } });
    }

    public bool SecureDnsRescue
    {
        get => S.Connectivity.SecureDnsRescue;
        set => Set(s => s with { Connectivity = s.Connectivity with { SecureDnsRescue = value } });
    }

    public bool CleanIpScan
    {
        get => S.Connectivity.CleanIpScan;
        set => Set(s => s with { Connectivity = s.Connectivity with { CleanIpScan = value } });
    }

    public bool FingerprintRotation
    {
        get => S.Connectivity.FingerprintRotation;
        set => Set(s => s with { Connectivity = s.Connectivity with { FingerprintRotation = value } });
    }
    public bool WinHttp
    {
        get => S.Connection.SetWinHttpProxy;
        set => Set(s => s with { Connection = s.Connection with { SetWinHttpProxy = value } });
    }

    public string BypassList
    {
        get => S.Connection.ProxyBypassList;
        set => Set(s => s with { Connection = s.Connection with { ProxyBypassList = value.Trim() } });
    }

    public int LocalPort
    {
        get => S.Connection.LocalProxyPort;
        set => Set(s => s with { Connection = s.Connection with { LocalProxyPort = value == 0 ? 0 : Clamp(value, 1024, 65535) } });
    }

    [ObservableProperty] private string? _portStatus;

    /// <summary>Who listens on the local port right now, and how to free it (our own session is not counted).</summary>
    [RelayCommand]
    private void CheckPort()
    {
        var port = LocalPort;
        if (port <= 0)
        {
            PortStatus = "Automatic port: a free one is chosen on every connection.";
            return;
        }

        var owner = JackiVpn.Networking.Probes.PortOwner.Find(port);
        PortStatus = owner is null
            ? $"Port {port} is free."
            : owner.IsIbox
                ? $"Port {port} is used by IBOX itself (an active or earlier session)."
                : $"Port {port} is used by {owner.Describe()}. {owner.HowToFree(port)}";
    }

    public int Mtu
    {
        get => S.Connection.TunMtu;
        set => Set(s => s with { Connection = s.Connection with { TunMtu = Clamp(value, 1280, 9000) } });
    }

    public int MaxReconnect
    {
        get => S.Connection.MaxReconnectAttempts;
        set => Set(s => s with { Connection = s.Connection with { MaxReconnectAttempts = Clamp(value, 1, 10) } });
    }

    public async Task RefreshServiceAsync()
    {
        var state = await _s.Service.RefreshAsync(_s.Lifetime);
        ServiceRunning = state == ServiceAvailability.Running;
        ServiceStatus = state switch
        {
            ServiceAvailability.Running => $"Running (service {_s.Service.ConnectedClient?.Hello?.ServiceVersion ?? "?"}). TUN mode and the kill switch are available.",
            ServiceAvailability.NotInstalled => "Not installed. TUN mode and the kill switch need it; System Proxy mode works without it.",
            ServiceAvailability.Stopped => "Installed but not running. Use Repair Service to start it.",
            ServiceAvailability.Rejected => "Running, but it does not accept this copy of the app (updated or moved). Use Repair Service.",
            _ => "Unknown.",
        };
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshServiceAsync();

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, output) = await _s.Service.InstallOrRepairAsync(developerLocation: false);
            if (!ok && output.Contains("writable by non-administrators", StringComparison.OrdinalIgnoreCase)
                && _dialogs.Confirm(
                    "Insecure install location",
                    "The app is in a folder ordinary users can modify, so a SYSTEM service installed from here could be hijacked.\n\n" +
                    "Install IBOX into Program Files for real use. Install anyway (developer machines only)?",
                    danger: true))
            {
                (ok, output) = await _s.Service.InstallOrRepairAsync(developerLocation: true);
            }

            _dialogs.Info(ok ? "Service installed" : "Service installation failed", output);
        }
        finally
        {
            IsBusy = false;
            await RefreshServiceAsync();
        }
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        if (!_dialogs.Confirm("Remove service", "Disconnect and remove the IBOX service? TUN mode and the kill switch will stop working.", danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _s.Orchestrator.DisconnectAsync();
            var (ok, output) = await _s.Service.UninstallAsync();
            _dialogs.Info(ok ? "Service removed" : "Removal failed", output);
        }
        finally
        {
            IsBusy = false;
            await RefreshServiceAsync();
        }
    }

    /// <summary>
    /// Repair Network: user-level recovery (system proxy from our journal, DNS flush, leftover report) plus,
    /// when the service is available, the privileged sweep (kill-switch filters, stale TUN adapter, WinHTTP).
    /// Nothing that belongs to other software is changed.
    /// </summary>
    [RelayCommand]
    private async Task RepairNetworkAsync()
    {
        if (!ConnectionStateRules.IsIdle(_s.Orchestrator.State))
        {
            if (!_dialogs.Confirm("Repair network", "Repairing disconnects the VPN first. Continue?"))
            {
                return;
            }

            await _s.Orchestrator.DisconnectAsync();
        }

        IsBusy = true;
        var lines = new List<string>();
        try
        {
            var report = await _s.UserRecovery.RepairAsync(_s.Lifetime);
            lines.AddRange(report.Actions.Select(a => $"{(a.Success ? "✔" : "✖")} {a.Step}: {a.Detail}"));
            try
            {
                var client = await _s.Service.GetClientAsync(_s.Lifetime);
                var svc = await client.RequestAsync<RepairResponse>(IpcMethods.RepairNetwork, _s.Lifetime);
                lines.AddRange(svc.Actions.Select(a => "[service] " + a));
            }
            catch (Exception ex) when (ex is ServiceUnavailableException or IpcRemoteException)
            {
                lines.Add("[service] not available — privileged steps (kill switch, TUN adapter, WinHTTP) skipped.");
            }

            RepairOutput = string.Join(Environment.NewLine, lines);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
