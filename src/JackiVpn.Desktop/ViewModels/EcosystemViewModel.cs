using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Ecosystem;
using JackiVpn.Desktop.Composition;
using JackiVpn.Security;

namespace JackiVpn.Desktop.ViewModels;

public sealed partial class SharableSubscription(string id, string name) : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;

    public string Id { get; } = id;

    public string Name { get; } = name;
}

/// <summary>A device found by a scan (phone or PC running IBOX on the same network).</summary>
public sealed partial class SyncDevice(EcosystemPeer peer) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public EcosystemPeer Peer { get; } = peer;

    public string Name => Peer.Name;

    public string Detail => (Peer.Platform == "Android" ? "Android phone" : "Windows PC") + " · " + Peer.Address;

    /// <summary>Segoe MDL2: CellPhone / Laptop.</summary>
    public string Glyph => ((char)(Peer.Platform == "Android" ? 0xE8EA : 0xE7F8)).ToString();
}

/// <summary>
/// "Sync device" (from the Subscriptions page): scans the local network for the user's other devices running
/// IBOX and sends them subscriptions. While the app runs (and the user has not turned it off) this PC answers
/// such scans itself; an incoming subscription always needs approval with a matching code.
/// </summary>
public sealed partial class EcosystemViewModel : ObservableObject, IEcosystemPrompt
{
    private readonly AppServices _s;
    private readonly EcosystemService _service;
    private CancellationTokenSource? _scan;

    [ObservableProperty] private SyncDevice? _selectedDevice;
    [ObservableProperty] private string? _code;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanText = "Press Scan to find your phone or other PC on this network.";

    public EcosystemViewModel(AppServices services)
    {
        _s = services;
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("eco|" + services.Settings.Current.Device.Hwid)))[..24];
        _service = new EcosystemService(services.SubscriptionService, services.Subscriptions, this, () => Environment.MachineName, "Windows", id,
            services.LoggerFactory.CreateLogger("JackiVpn.Sync"));
        services.Settings.Changed += (_, _) => Ui.Post(() =>
        {
            OnPropertyChanged(nameof(Enabled));
            ApplyEnabled();
        });
    }

    public ObservableCollection<SyncDevice> Devices { get; } = [];

    public ObservableCollection<SharableSubscription> Subscriptions { get; } = [];

    /// <summary>This PC answers scans from the user's other devices.</summary>
    public bool Enabled
    {
        get => _s.Settings.Current.Ecosystem.Enabled;
        set => _s.Settings.Update(x => x with { Ecosystem = x.Ecosystem with { Enabled = value } });
    }

    public string DeviceName => Environment.MachineName;

    public bool HasDevices => Devices.Count > 0;

    public bool HasSubscriptions => Subscriptions.Count > 0;

    public void Start() => ApplyEnabled();

    private void ApplyEnabled()
    {
        if (Enabled)
        {
            _service.Start();
        }
        else
        {
            _ = _service.StopAsync();
        }
    }

    public async Task LoadAsync()
    {
        var subs = await _s.Subscriptions.GetAllAsync(_s.Lifetime);
        Subscriptions.Clear();
        foreach (var sub in subs)
        {
            Subscriptions.Add(new SharableSubscription(sub.Id, sub.Name));
        }

        OnPropertyChanged(nameof(HasSubscriptions));
    }

    [RelayCommand]
    private void SelectDevice(SyncDevice? device)
    {
        foreach (var d in Devices)
        {
            d.IsSelected = d == device;
        }

        SelectedDevice = device;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        _scan?.Dispose();
        _scan = CancellationTokenSource.CreateLinkedTokenSource(_s.Lifetime);
        IsScanning = true;
        Message = null;
        Devices.Clear();
        SelectedDevice = null;
        OnPropertyChanged(nameof(HasDevices));
        ScanText = "Searching this network…";
        try
        {
            await Task.Run(() => _service.ScanAsync(peer => Ui.Post(() =>
            {
                var device = new SyncDevice(peer);
                Devices.Add(device);
                OnPropertyChanged(nameof(HasDevices));
                if (Devices.Count == 1)
                {
                    SelectDevice(device);
                }
            }), _scan.Token));
            ScanText = Devices.Count switch
            {
                0 => "No devices found. Open IBOX on your phone or other PC (same Wi-Fi/LAN) and scan again. Guest Wi-Fi networks often block devices from seeing each other.",
                1 => "1 device found.",
                var n => $"{n} devices found.",
            };
        }
        catch (OperationCanceledException)
        {
            ScanText = "Scan stopped.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var ids = Subscriptions.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        if (SelectedDevice is not { } device || ids.Count == 0 || IsSending)
        {
            Message = "Choose a device and at least one subscription.";
            return;
        }

        IsSending = true;
        Message = $"Waiting for \"{device.Name}\" to accept… Check that it shows the same code.";
        try
        {
            var result = await Task.Run(() => _service.SendAsync(device.Peer, ids, code => Ui.Post(() => Code = code), _s.Lifetime));
            Message = result.Accepted
                ? string.Join("\n", result.Messages.DefaultIfEmpty($"{result.Imported} subscription(s) added."))
                : string.Join("\n", result.Messages);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidOperationException)
        {
            Message = "Sending failed: " + SecretRedactor.Redact(ex.Message) + " Scan again and make sure IBOX is open on the other device.";
        }
        finally
        {
            IsSending = false;
            Code = null;
        }
    }

    public void CancelScan() => _scan?.Cancel();

    /// <summary>Incoming request from another device: asked on the UI thread, with the code to compare.</summary>
    public Task<bool> ConfirmIncomingAsync(IncomingOffer offer, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var owner = System.Windows.Application.Current.MainWindow;
            if (owner is { IsVisible: true })
            {
                owner.Activate();
            }

            var accepted = Views.DialogService.Show("Incoming subscription",
                $"\"{offer.FromName}\" ({offer.FromPlatform}) wants to send you:\n\n• {string.Join("\n• ", offer.SubscriptionNames)}\n\n" +
                $"Code:  {offer.Code}\n\nAccept only if the other device shows the same code.",
                null, "Accept", "Decline", danger: false, width: 460, focus: null);
            tcs.TrySetResult(accepted);
        });
        return tcs.Task.WaitAsync(ct);
    }
}
