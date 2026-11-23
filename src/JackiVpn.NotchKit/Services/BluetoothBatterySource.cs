using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using JackiVpn.Domain.Bar;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Batteries of the Bluetooth devices connected to Windows, without polling: one watcher over paired devices
/// (connected or not, their class and appearance), and for each connected one a watcher over its PnP nodes for the
/// battery Windows itself reads (<see cref="BluetoothBattery.BatteryProperty"/>, classic and Low Energy). A Low
/// Energy device is also asked once through the GATT Battery Service and then notifies changes (level and, when it
/// has it, Battery Level Status for charging). Nothing runs while nothing changes.
/// </summary>
public sealed class BluetoothBatterySource : IDisposable
{
    private const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string ContainerId = "System.Devices.Aep.ContainerId";
    private const string ProtocolId = "System.Devices.Aep.ProtocolId";
    private const string CodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Container> _containers = [];
    private DeviceWatcher? _paired;
    private Timer? _debounce;
    private IReadOnlyList<BatteryDevice> _current = [];

    /// <summary>The list changed (raised on a background thread, at most every 300 ms).</summary>
    public event EventHandler<IReadOnlyList<BatteryDevice>>? Changed;

    public IReadOnlyList<BatteryDevice> Current => Volatile.Read(ref _current);

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _paired is not null;
            }
        }
    }

    private sealed class Endpoint
    {
        public required string Id { get; init; }
        public string Name { get; set; } = string.Empty;
        public bool LowEnergy { get; init; }
        public bool Connected { get; set; }
        public Guid Container { get; set; }
        public uint? Cod { get; set; }
        public ushort? Appearance { get; set; }
    }

    /// <summary>One physical device: its PnP nodes' battery and, for Low Energy, its GATT battery.</summary>
    private sealed class Container : IDisposable
    {
        public DeviceWatcher? Nodes { get; set; }
        public ConcurrentDictionary<string, int> NodeLevels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int? GattLevel { get; set; }
        public bool? Charging { get; set; }
        public BluetoothLEDevice? Device { get; set; }
        public List<GattCharacteristic> Subscribed { get; } = [];
        public GattDeviceService? Service { get; set; }

        public void Dispose()
        {
            if (Nodes is { } w)
            {
                try
                {
                    if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    {
                        w.Stop();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // already stopping
                }
            }

            Nodes = null;
            Subscribed.Clear();
            Service?.Dispose();
            Service = null;
            Device?.Dispose();
            Device = null;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_paired is not null)
            {
                return;
            }

            _debounce = new Timer(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);
            var aqs = $"(System.Devices.Aep.ProtocolId:=\"{ClassicProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{LowEnergyProtocol}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            try
            {
                var w = DeviceInformation.CreateWatcher(aqs, [IsConnected, ContainerId, ProtocolId, CodMajor, CodMinor, Appearance], DeviceInformationKind.AssociationEndpoint);
                w.Added += (_, d) => OnEndpoint(d.Id, d.Name, d.Properties);
                w.Updated += (_, u) => OnEndpoint(u.Id, null, u.Properties);
                w.Removed += (_, u) => OnRemoved(u.Id);
                w.Start();
                _paired = w;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
            {
                // No Bluetooth stack (or no permission): nothing to show.
                _paired = null;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_paired is { } w)
            {
                try
                {
                    w.Stop();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // already stopping
                }
            }

            _paired = null;
            foreach (var c in _containers.Values)
            {
                c.Dispose();
            }

            _containers.Clear();
            _endpoints.Clear();
            _debounce?.Dispose();
            _debounce = null;
        }

        Volatile.Write(ref _current, []);
        Changed?.Invoke(this, []);
    }

    public void Dispose() => Stop();

    private static T? Get<T>(IReadOnlyDictionary<string, object> props, string key)
        where T : struct => props.TryGetValue(key, out var v) && v is T t ? t : null;

    private void OnEndpoint(string id, string? name, IReadOnlyDictionary<string, object> props)
    {
        lock (_gate)
        {
            if (_paired is null)
            {
                return;
            }

            if (!_endpoints.TryGetValue(id, out var e))
            {
                var protocol = props.TryGetValue(ProtocolId, out var p) ? p?.ToString() : null;
                e = new Endpoint { Id = id, LowEnergy = string.Equals(protocol?.Trim('{', '}'), LowEnergyProtocol.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) };
                _endpoints[id] = e;
            }

            if (name is { Length: > 0 })
            {
                e.Name = name;
            }

            if (Get<bool>(props, IsConnected) is { } connected)
            {
                e.Connected = connected;
            }

            if (Get<Guid>(props, ContainerId) is { } container && container != Guid.Empty)
            {
                e.Container = container;
            }

            if (Get<ushort>(props, CodMajor) is { } major)
            {
                // Windows hands the class as its major and (6-bit) minor parts: put them back where the CoD has them.
                e.Cod = ((uint)major << 8) | ((uint)(Get<ushort>(props, CodMinor) ?? 0) << 2);
            }

            e.Appearance = Get<ushort>(props, Appearance) ?? e.Appearance;
            SyncContainers();
        }

        Soon();
    }

    private void OnRemoved(string id)
    {
        lock (_gate)
        {
            _endpoints.Remove(id);
            SyncContainers();
        }

        Soon();
    }

    /// <summary>Watches the containers of connected devices only; a disconnected one lets go of everything. Under the lock.</summary>
    private void SyncContainers()
    {
        var wanted = _endpoints.Values.Where(e => e.Connected && e.Container != Guid.Empty).Select(e => e.Container).ToHashSet();
        foreach (var gone in _containers.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _containers[gone].Dispose();
            _containers.Remove(gone);
        }

        foreach (var key in wanted.Where(k => !_containers.ContainsKey(k)))
        {
            var c = new Container();
            _containers[key] = c;
            WatchNodes(key, c);
            if (_endpoints.Values.FirstOrDefault(e => e.Container == key && e.LowEnergy && e.Connected) is { } le)
            {
                _ = Task.Run(() => ReadGattAsync(key, c, le.Id));
            }
        }
    }

    private void WatchNodes(Guid key, Container c)
    {
        try
        {
            var w = DeviceInformation.CreateWatcher($"System.Devices.ContainerId:=\"{{{key}}}\"", [BluetoothBattery.BatteryProperty], DeviceInformationKind.Device);
            void Take(string id, IReadOnlyDictionary<string, object> props)
            {
                if (!props.ContainsKey(BluetoothBattery.BatteryProperty))
                {
                    return;
                }

                if (BluetoothBattery.FromProperty(props[BluetoothBattery.BatteryProperty]) is { } level)
                {
                    c.NodeLevels[id] = level;
                }
                else
                {
                    c.NodeLevels.TryRemove(id, out _);
                }

                Soon();
            }

            w.Added += (_, d) => Take(d.Id, d.Properties);
            w.Updated += (_, u) => Take(u.Id, u.Properties);
            w.Removed += (sender, u) =>
            {
                c.NodeLevels.TryRemove(u.Id, out _);
                Soon();
            };
            w.Start();
            c.Nodes = w;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // This device's nodes cannot be read: its GATT battery may still be.
        }
    }

    /// <summary>Low Energy: the Battery Service once, then its notifications. Devices that refuse simply have none.</summary>
    private async Task ReadGattAsync(Guid key, Container c, string endpointId)
    {
        try
        {
            var device = await BluetoothLEDevice.FromIdAsync(endpointId).AsTask().ConfigureAwait(false);
            if (device is null)
            {
                return;
            }

            lock (_gate)
            {
                if (!_containers.TryGetValue(key, out var still) || !ReferenceEquals(still, c))
                {
                    device.Dispose();
                    return;
                }

                c.Device = device;
            }

            // Cached: never wakes or connects the device just to ask.
            var services = await device.GetGattServicesForUuidAsync(BluetoothBattery.BatteryService, BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                return;
            }

            var service = services.Services[0];
            c.Service = service;
            await AttachAsync(c, service, BluetoothBattery.BatteryLevel, bytes => c.GattLevel = BluetoothBattery.ParseLevel(bytes) ?? c.GattLevel).ConfigureAwait(false);
            await AttachAsync(c, service, BluetoothBattery.BatteryLevelStatus, bytes => c.Charging = BluetoothBattery.ParseCharging(bytes) ?? c.Charging).ConfigureAwait(false);
            Soon();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            // Windows keeps some devices' services to itself: the PnP battery is all there is.
        }
    }

    private async Task AttachAsync(Container c, GattDeviceService service, Guid uuid, Action<byte[]> take)
    {
        var found = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Cached).AsTask().ConfigureAwait(false);
        if (found.Status != GattCommunicationStatus.Success || found.Characteristics.Count == 0)
        {
            return;
        }

        var ch = found.Characteristics[0];
        if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
        {
            var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
            if (read.Status == GattCommunicationStatus.Success && read.Value is { } buffer)
            {
                take(buffer.ToArray());
            }
        }

        if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
        {
            ch.ValueChanged += (_, args) =>
            {
                take(args.CharacteristicValue.ToArray());
                Soon();
            };
            var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().ConfigureAwait(false);
            if (status == GattCommunicationStatus.Success)
            {
                c.Subscribed.Add(ch);
            }
        }
    }

    /// <summary>Many events arrive together (a device connects: several nodes): one update for all of them.</summary>
    private void Soon()
    {
        lock (_gate)
        {
            _debounce?.Change(300, Timeout.Infinite);
        }
    }

    private void Publish()
    {
        List<BatteryDevice> list = [];
        lock (_gate)
        {
            foreach (var (key, c) in _containers)
            {
                int? level = c.GattLevel ?? (c.NodeLevels.IsEmpty ? null : c.NodeLevels.Values.Min());
                if (level is not { } percent)
                {
                    continue;
                }

                var ends = _endpoints.Values.Where(e => e.Container == key).ToList();
                var name = ends.Select(e => e.Name).FirstOrDefault(n => n.Length > 0);
                var kind = BluetoothBattery.Kind(ends.Select(e => e.Cod).FirstOrDefault(v => v is not null), ends.Select(e => e.Appearance).FirstOrDefault(v => v is not null), name);
                list.Add(new BatteryDevice(key.ToString("N"), BluetoothBattery.CleanName(name), kind, percent, c.Charging));
            }
        }

        var sorted = BluetoothBattery.Sort(list);
        var old = Volatile.Read(ref _current);
        if (old.SequenceEqual(sorted))
        {
            return;
        }

        Volatile.Write(ref _current, sorted);
        Changed?.Invoke(this, sorted);
    }
}
