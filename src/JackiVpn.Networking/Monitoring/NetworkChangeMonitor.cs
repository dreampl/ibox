using System.Net.NetworkInformation;
using JackiVpn.Networking.Adapters;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Networking.Monitoring;

public sealed record NetworkSignature(string? DefaultInterface, string? Gateway, bool Available)
{
    public static NetworkSignature Capture(string? excludeAdapter)
    {
        var route = NetworkInspector.DefaultRouteV4(excludeAdapter);
        return new NetworkSignature(route?.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), route?.NextHop, NetworkInterface.GetIsNetworkAvailable());
    }
}

public sealed record NetworkChangedEventArgs(NetworkSignature Previous, NetworkSignature Current, string Reason)
{
    /// <summary>Default route moved to another interface/gateway (Wi-Fi ↔ Ethernet, new network).</summary>
    public bool DefaultRouteChanged => Previous.DefaultInterface != Current.DefaultInterface || Previous.Gateway != Current.Gateway;

    public bool BecameAvailable => !Previous.Available && Current.Available;

    public bool BecameUnavailable => Previous.Available && !Current.Available;
}

/// <summary>
/// Debounced network change detection. Windows raises bursts of address-change events during a single
/// transition (DHCP, IPv6 RA, TUN creation…); we wait for quiet, then compare the default-route signature
/// (ignoring our own TUN adapter) and raise one event only for real changes. Resume from sleep is fed in
/// explicitly by the host (service power events / desktop power-mode events).
/// </summary>
public sealed class NetworkChangeMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly string? _excludeAdapter;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private NetworkSignature _last;
    private string _pendingReason = "address change";
    private bool _started;

    public NetworkChangeMonitor(ILogger logger, string? excludeAdapter, TimeSpan? debounce = null)
    {
        _logger = logger;
        _excludeAdapter = excludeAdapter;
        _debounce = debounce ?? TimeSpan.FromSeconds(1.5);
        _last = NetworkSignature.Capture(excludeAdapter);
    }

    public event EventHandler<NetworkChangedEventArgs>? Changed;

    public NetworkSignature Current => _last;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _last = NetworkSignature.Capture(_excludeAdapter);
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        }
    }

    /// <summary>Called by the host on resume from sleep/hibernate.</summary>
    public void NotifyResume() => Schedule("resume from sleep", force: true);

    private void OnAddressChanged(object? sender, EventArgs e) => Schedule("address change", force: false);

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Schedule(e.IsAvailable ? "network available" : "network unavailable", force: false);

    private bool _forcePending;

    private void Schedule(string reason, bool force)
    {
        lock (_gate)
        {
            _pendingReason = reason;
            _forcePending |= force;
            _timer ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        NetworkSignature previous;
        NetworkSignature current;
        string reason;
        bool force;
        lock (_gate)
        {
            previous = _last;
            current = NetworkSignature.Capture(_excludeAdapter);
            _last = current;
            reason = _pendingReason;
            force = _forcePending;
            _forcePending = false;
        }

        if (!force && previous == current)
        {
            return;
        }

        _logger.LogInformation("Network change ({Reason}): if {Prev} → {Cur}, available {Avail}.", reason, previous.DefaultInterface, current.DefaultInterface, current.Available);
        Changed?.Invoke(this, new NetworkChangedEventArgs(previous, current, reason));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _timer?.Dispose();
    }
}
