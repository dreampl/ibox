using System.Collections.ObjectModel;
using System.Net;
using Android.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JackiVpn.Application.Ecosystem;
using JackiVpn.Mobile.Services;
using JackiVpn.Security;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Mobile.ViewModels;

public sealed partial class SharableSubscription(string id, string name) : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;

    public string Id { get; } = id;

    public string Name { get; } = name;
}

/// <summary>A device found by a scan (phone or PC running IBOX on the same Wi-Fi).</summary>
public sealed partial class SyncDevice(EcosystemPeer peer) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public EcosystemPeer Peer { get; } = peer;

    public string Name => Peer.Name;

    public string Detail => (Peer.Platform == "Android" ? "Android phone" : "Windows PC") + " · " + Peer.Address;

    public string Glyph => Peer.Platform == "Android" ? "📱" : "💻";
}

/// <summary>
/// "Sync device" on the phone. While the app is open (and the user has not turned it off) the phone answers scans
/// from the user's other devices; a scan asks every address of the Wi-Fi network directly, so nothing has to be
/// switched on beforehand except having IBOX open on the other device.
/// </summary>
public sealed partial class EcosystemViewModel : ObservableObject, IEcosystemPrompt
{
    private readonly MobileServices _s;
    private readonly Lazy<EcosystemService> _service;
    private CancellationTokenSource? _scan;
    private bool _foreground;

    [ObservableProperty] private SyncDevice? _selectedDevice;
    [ObservableProperty] private string? _code;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanText = "Looking for your devices…";

    public EcosystemViewModel(MobileServices services)
    {
        _s = services;
        _service = new(Create);
        services.Settings.Changed += (_, _) => Fmt.OnUi(() =>
        {
            OnPropertyChanged(nameof(Enabled));
            Apply();
        });
    }

    public ObservableCollection<SyncDevice> Devices { get; } = [];

    public ObservableCollection<SharableSubscription> Subscriptions { get; } = [];

    /// <summary>This phone answers scans from the user's other devices while the app is open.</summary>
    public bool Enabled
    {
        get => _s.Settings.Current.Ecosystem.Enabled;
        set => _s.Settings.Update(x => x with { Ecosystem = x.Ecosystem with { Enabled = value } });
    }

    public string DeviceName => MobileServices.PhoneModel;

    public bool HasDevices => Devices.Count > 0;

    public bool NoDevices => !IsScanning && Devices.Count == 0;

    public bool HasSubscriptions => Subscriptions.Count > 0;

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(NoDevices));

    /// <summary>Called by the activity: listening only while the app is on screen (no background battery use).</summary>
    public void SetForeground(bool foreground)
    {
        _foreground = foreground;
        Apply();
    }

    private void Apply()
    {
        if (Enabled && _foreground)
        {
            _service.Value.Start();
        }
        else if (_service.IsValueCreated)
        {
            _ = _service.Value.StopAsync();
        }
    }

    private EcosystemService Create()
    {
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("eco|" + _s.Settings.Current.Device.Hwid)))[..24];
        return new EcosystemService(_s.SubscriptionService, _s.Subscriptions, this, () => DeviceName, "Android", id, _s.LoggerFactory.CreateLogger("JackiVpn.Sync"))
        {
            LocalNetworks = () => LocalNetworks(_s.Context),
        };
    }

    /// <summary>Wi-Fi/Ethernet networks from Android (never mobile data or the VPN), plus a hotspot this phone runs.</summary>
    private static IEnumerable<LocalNetwork> LocalNetworks(global::Android.Content.Context context)
    {
        var result = new List<LocalNetwork>();
        try
        {
            if (context.GetSystemService(global::Android.Content.Context.ConnectivityService) is ConnectivityManager cm)
            {
#pragma warning disable CA1422 // GetAllNetworks is deprecated but is the only way to list non-default networks
                foreach (var network in cm.GetAllNetworks())
#pragma warning restore CA1422
                {
                    var caps = cm.GetNetworkCapabilities(network);
                    if (caps is null || caps.HasTransport(TransportType.Vpn) || !(caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Ethernet)))
                    {
                        continue;
                    }

                    foreach (var la in cm.GetLinkProperties(network)?.LinkAddresses ?? [])
                    {
                        if (la.Address is Java.Net.Inet4Address v4 && IPAddress.TryParse(v4.HostAddress, out var ip))
                        {
                            result.Add(new LocalNetwork(ip, la.PrefixLength, Preferred: true));
                        }
                    }
                }
            }
        }
        catch (Java.Lang.Exception)
        {
        }

        try
        {
            // A hotspot run by this phone is not an Android "network"; its interface (wlan1/ap0/swlan0) still shows.
            result.AddRange(EcosystemService.DefaultLocalNetworks()
                .Where(n => result.All(r => !r.Address.Equals(n.Address))));
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    public async Task LoadAsync()
    {
        await _s.InitializeAsync();
        var keep = Subscriptions.Where(s => !s.IsSelected).Select(s => s.Id).ToHashSet();
        Subscriptions.Clear();
        foreach (var sub in await _s.Subscriptions.GetAllAsync(_s.Lifetime))
        {
            Subscriptions.Add(new SharableSubscription(sub.Id, sub.Name) { IsSelected = !keep.Contains(sub.Id) });
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
    public async Task ScanAsync()
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
        OnPropertyChanged(nameof(NoDevices));
        ScanText = "Searching this Wi-Fi…";
        try
        {
            var service = _service.Value;
            await Task.Run(() => service.ScanAsync(peer => Fmt.OnUi(() =>
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
                0 => "No devices found",
                1 => "1 device found",
                var n => $"{n} devices found",
            };
        }
        catch (OperationCanceledException)
        {
            ScanText = "Scan stopped";
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(NoDevices));
        }
    }

    public void CancelScan() => _scan?.Cancel();

    [RelayCommand]
    private async Task SendAsync()
    {
        var ids = Subscriptions.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        if (SelectedDevice is not { } device || ids.Count == 0 || IsSending)
        {
            Message = "Tap a device and choose at least one subscription.";
            return;
        }

        IsSending = true;
        Message = $"Waiting for \"{device.Name}\" to accept… It shows the same code.";
        try
        {
            var service = _service.Value;
            var result = await Task.Run(() => service.SendAsync(device.Peer, ids, c => Fmt.OnUi(() => Code = c), _s.Lifetime));
            Message = result.Accepted
                ? string.Join("\n", result.Messages.DefaultIfEmpty($"{result.Imported} subscription(s) added."))
                : string.Join("\n", result.Messages);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidOperationException)
        {
            Message = "Sending failed: " + SecretRedactor.Redact(ex.Message) + " Scan again and keep IBOX open on the other device.";
        }
        finally
        {
            IsSending = false;
            Code = null;
        }
    }

    public async Task<bool> ConfirmIncomingAsync(IncomingOffer offer, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var ok = await Shell.Current.DisplayAlertAsync("Incoming subscription",
                    $"\"{offer.FromName}\" ({offer.FromPlatform}) wants to send you:\n• {string.Join("\n• ", offer.SubscriptionNames)}\n\nCode: {offer.Code}\n\nAccept only if the other device shows the same code.",
                    "Accept", "Decline");
                tcs.TrySetResult(ok);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                tcs.TrySetResult(false);
            }
        });
        return await tcs.Task.WaitAsync(ct);
    }
}
