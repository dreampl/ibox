namespace JackiVpn.Domain.Bar;

/// <summary>What a Bluetooth device is, for its icon.</summary>
public enum BluetoothKind
{
    Other = 0,
    Headphones,
    Speaker,
    Mouse,
    Keyboard,
    Controller,
    Pen,
    Phone,
    Watch,
}

/// <summary>One connected Bluetooth device that reports its battery.</summary>
/// <param name="Id">Stable key (the device's container id).</param>
/// <param name="Percent">0…100.</param>
/// <param name="Charging">True / false when the device says so; null when it does not tell.</param>
public sealed record BatteryDevice(string Id, string Name, BluetoothKind Kind, int Percent, bool? Charging = null)
{
    public bool IsLow => Percent <= BluetoothBattery.LowPercent && Charging != true;

    public bool IsCritical => Percent <= BluetoothBattery.CriticalPercent && Charging != true;
}

/// <summary>
/// Reading Bluetooth batteries: the device type from the Class of Device (classic) or the GAP Appearance (Low Energy),
/// the GATT Battery Service values, and when to warn. Pure, so it can be tested without radios.
/// </summary>
public static class BluetoothBattery
{
    public const int LowPercent = 20;

    public const int CriticalPercent = 10;

    /// <summary>A warned device is warned again only after it climbed this far above <see cref="LowPercent"/>.</summary>
    public const int RearmPercent = LowPercent + 10;

    /// <summary>The PnP property Windows fills for Bluetooth devices that report a battery (DEVPKEY_Bluetooth_Battery).</summary>
    public const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    /// <summary>Battery Service, Battery Level and Battery Level Status (Battery Service 1.1).</summary>
    public static readonly Guid BatteryService = new("0000180f-0000-1000-8000-00805f9b34fb");

    public static readonly Guid BatteryLevel = new("00002a19-0000-1000-8000-00805f9b34fb");

    public static readonly Guid BatteryLevelStatus = new("00002bed-0000-1000-8000-00805f9b34fb");

    /// <summary>The kind from a classic Class of Device (major class bits 8–12, minor bits 2–7).</summary>
    public static BluetoothKind FromClassOfDevice(uint cod)
    {
        var major = (cod >> 8) & 0x1F;
        var minor = (cod >> 2) & 0x3F;
        switch (major)
        {
            case 0x02:
                return BluetoothKind.Phone;
            case 0x04:
                // Audio/Video: loudspeakers and car audio are speakers; headsets, hands-free, headphones are worn.
                return minor is 0x05 or 0x07 or 0x08 or 0x0A ? BluetoothKind.Speaker : BluetoothKind.Headphones;
            case 0x05:
            {
                // Peripheral: bits 6–7 keyboard / pointing, bits 2–5 the device.
                var low = minor & 0x0F;
                if (low is 0x01 or 0x02)
                {
                    return BluetoothKind.Controller;
                }

                if (low is 0x05 or 0x07)
                {
                    return BluetoothKind.Pen;
                }

                return (minor >> 4) switch
                {
                    0x01 => BluetoothKind.Keyboard,
                    0x02 => BluetoothKind.Mouse,
                    0x03 => BluetoothKind.Keyboard,
                    _ => BluetoothKind.Other,
                };
            }

            case 0x07:
                return BluetoothKind.Watch;
            default:
                return BluetoothKind.Other;
        }
    }

    /// <summary>The kind from a Low Energy GAP Appearance (category = bits 6–15).</summary>
    public static BluetoothKind FromAppearance(ushort appearance)
    {
        var category = appearance >> 6;
        var sub = appearance & 0x3F;
        return category switch
        {
            0x001 => BluetoothKind.Phone,
            0x003 => BluetoothKind.Watch,
            0x00F => sub switch
            {
                0x01 => BluetoothKind.Keyboard,
                0x02 => BluetoothKind.Mouse,
                0x03 or 0x04 => BluetoothKind.Controller,
                0x07 => BluetoothKind.Pen,
                _ => BluetoothKind.Other,
            },
            0x021 => BluetoothKind.Speaker,
            0x025 => BluetoothKind.Headphones,
            _ => BluetoothKind.Other,
        };
    }

    /// <summary>A guess from the name when the device gives no class ("Xbox Wireless Controller", "MX Keys").</summary>
    public static BluetoothKind FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BluetoothKind.Other;
        }

        bool Has(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        return Has("controller", "gamepad", "xbox", "dualsense", "dualshock", "joy-con")
            ? BluetoothKind.Controller
            : Has("mouse", "trackpad", "mx master", "mx anywhere")
                ? BluetoothKind.Mouse
                : Has("keyboard", "keys")
                    ? BluetoothKind.Keyboard
                    : Has("buds", "airpods", "headphone", "headset", "wh-", "wf-", "earbud", "hands-free")
                        ? BluetoothKind.Headphones
                        : Has("speaker", "soundbar", "boom", "flip", "charge ")
                            ? BluetoothKind.Speaker
                            : Has("pen", "stylus")
                                ? BluetoothKind.Pen
                                : Has("watch", "band")
                                    ? BluetoothKind.Watch
                                    : BluetoothKind.Other;
    }

    /// <summary>The first kind that says something (class, then appearance, then the name).</summary>
    public static BluetoothKind Kind(uint? cod, ushort? appearance, string? name)
    {
        if (cod is { } c && FromClassOfDevice(c) is var byClass and not BluetoothKind.Other)
        {
            return byClass;
        }

        if (appearance is { } a && FromAppearance(a) is var byLook and not BluetoothKind.Other)
        {
            return byLook;
        }

        return FromName(name);
    }

    /// <summary>The Battery Level characteristic (one byte, 0…100); null when it is out of range.</summary>
    public static int? ParseLevel(ReadOnlySpan<byte> value) => value.Length >= 1 && value[0] <= 100 ? value[0] : null;

    /// <summary>
    /// Whether Battery Level Status says the battery charges: flags (1 byte), then the power state (uint16 LE),
    /// whose bits 5–6 are the charge state (1 = charging, 2/3 = discharging). Null when unknown.
    /// </summary>
    public static bool? ParseCharging(ReadOnlySpan<byte> value)
    {
        if (value.Length < 3)
        {
            return null;
        }

        var power = value[1] | (value[2] << 8);
        return ((power >> 5) & 0x3) switch
        {
            1 => true,
            2 or 3 => false,
            _ => null,
        };
    }

    /// <summary>The PnP battery property as Windows hands it (a byte; sometimes boxed as another integer type).</summary>
    public static int? FromProperty(object? value) => value switch
    {
        byte b when b <= 100 => b,
        sbyte s when s is >= 0 and <= 100 => s,
        ushort u when u <= 100 => u,
        short s when s is >= 0 and <= 100 => s,
        uint u when u <= 100 => (int)u,
        int i when i is >= 0 and <= 100 => i,
        _ => null,
    };

    /// <summary>A friendly name without Windows' per-profile endings ("WH-1000XM4 Hands-Free AG" → "WH-1000XM4").</summary>
    public static string CleanName(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        foreach (var tail in new[] { " Hands-Free AG Audio", " Hands-Free AG", " Hands-Free", " Avrcp Transport", " Stereo", " Audio" })
        {
            if (n.Length > tail.Length && n.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                n = n[..^tail.Length].TrimEnd();
            }
        }

        return n.Length == 0 ? "Bluetooth device" : n.Length > 40 ? n[..40] : n;
    }

    /// <summary>The order on the notch: the emptiest first (it is the one to charge), then by name.</summary>
    public static IReadOnlyList<BatteryDevice> Sort(IEnumerable<BatteryDevice> devices) =>
        devices.OrderBy(d => d.Charging == true ? 1 : 0).ThenBy(d => d.Percent).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// When to warn about a low battery: once per device when it falls to <see cref="BluetoothBattery.LowPercent"/>, and
/// again only after it charged (or rose above <see cref="BluetoothBattery.RearmPercent"/>). Not thread-safe.
/// </summary>
public sealed class LowBatteryWarnings
{
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>The devices that just became low (warn about these now).</summary>
    public IReadOnlyList<BatteryDevice> Update(IEnumerable<BatteryDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var now = new List<BatteryDevice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in devices)
        {
            seen.Add(d.Id);
            if (d.Charging == true || d.Percent >= BluetoothBattery.RearmPercent)
            {
                _warned.Remove(d.Id);
            }
            else if (d.IsLow && _warned.Add(d.Id))
            {
                now.Add(d);
            }
        }

        // A device that went away and comes back low is warned again: it was probably used elsewhere.
        _warned.RemoveWhere(id => !seen.Contains(id));
        return now;
    }
}
