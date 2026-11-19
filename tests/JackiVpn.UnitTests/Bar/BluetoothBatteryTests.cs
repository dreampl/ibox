using FluentAssertions;
using JackiVpn.Domain.Bar;
using Xunit;

namespace JackiVpn.UnitTests.Bar;

public sealed class BluetoothBatteryTests
{
    // Class of Device as Windows rebuilds it from Cod.Major / Cod.Minor: (major << 8) | (minor << 2).
    private static uint Cod(uint major, uint minor) => (major << 8) | (minor << 2);

    [Theory]
    [InlineData(4u, 1u, BluetoothKind.Headphones)] // wearable headset (AirPods, Galaxy Buds on this host)
    [InlineData(4u, 6u, BluetoothKind.Headphones)]
    [InlineData(4u, 5u, BluetoothKind.Speaker)]
    [InlineData(4u, 8u, BluetoothKind.Speaker)]
    [InlineData(5u, 0x10u, BluetoothKind.Keyboard)]
    [InlineData(5u, 0x20u, BluetoothKind.Mouse)]
    [InlineData(5u, 0x02u, BluetoothKind.Controller)]
    [InlineData(5u, 0x01u, BluetoothKind.Controller)]
    [InlineData(5u, 0x07u, BluetoothKind.Pen)]
    [InlineData(2u, 3u, BluetoothKind.Phone)]
    [InlineData(7u, 1u, BluetoothKind.Watch)]
    [InlineData(1u, 3u, BluetoothKind.Other)]
    public void Class_of_device_gives_the_kind(uint major, uint minor, BluetoothKind kind) =>
        BluetoothBattery.FromClassOfDevice(Cod(major, minor)).Should().Be(kind);

    [Theory]
    [InlineData(961, BluetoothKind.Keyboard)] // 0x03C1, "BT5.0 KB" on this host
    [InlineData(0x03C2, BluetoothKind.Mouse)]
    [InlineData(0x03C4, BluetoothKind.Controller)]
    [InlineData(0x03C7, BluetoothKind.Pen)]
    [InlineData(0x0941, BluetoothKind.Headphones)]
    [InlineData(0x0841, BluetoothKind.Speaker)]
    [InlineData(0x00C1, BluetoothKind.Watch)]
    [InlineData(0x0000, BluetoothKind.Other)]
    public void Appearance_gives_the_kind(int appearance, BluetoothKind kind) =>
        BluetoothBattery.FromAppearance((ushort)appearance).Should().Be(kind);

    [Fact]
    public void Kind_falls_back_to_the_name()
    {
        BluetoothBattery.Kind(null, null, "Xbox Wireless Controller").Should().Be(BluetoothKind.Controller);
        BluetoothBattery.Kind(null, null, "MX Master 3S").Should().Be(BluetoothKind.Mouse);
        BluetoothBattery.Kind(Cod(1, 0), null, "Galaxy Buds Pro").Should().Be(BluetoothKind.Headphones);
        BluetoothBattery.Kind(Cod(5, 0x20), 0x03C1, "whatever").Should().Be(BluetoothKind.Mouse, "the class wins");
    }

    [Fact]
    public void Gatt_level_and_status_are_parsed()
    {
        BluetoothBattery.ParseLevel([57]).Should().Be(57);
        BluetoothBattery.ParseLevel([101]).Should().BeNull();
        BluetoothBattery.ParseLevel([]).Should().BeNull();

        // Battery Level Status: flags, power state LE; charge state in bits 5–6.
        BluetoothBattery.ParseCharging([0x00, 0b0010_0001, 0x00]).Should().BeTrue();
        BluetoothBattery.ParseCharging([0x00, 0b0100_0001, 0x00]).Should().BeFalse();
        BluetoothBattery.ParseCharging([0x00, 0b0110_0001, 0x00]).Should().BeFalse();
        BluetoothBattery.ParseCharging([0x00, 0x01, 0x00]).Should().BeNull();
        BluetoothBattery.ParseCharging([0x00]).Should().BeNull();
    }

    [Fact]
    public void Pnp_property_values_are_read_in_any_integer_box()
    {
        BluetoothBattery.FromProperty((byte)90).Should().Be(90);
        BluetoothBattery.FromProperty((uint)40).Should().Be(40);
        BluetoothBattery.FromProperty((byte)200).Should().BeNull();
        BluetoothBattery.FromProperty("90").Should().BeNull();
        BluetoothBattery.FromProperty(null).Should().BeNull();
    }

    [Theory]
    [InlineData("soundcore R50i Hands-Free AG", "soundcore R50i")]
    [InlineData("arsham’s AirPods Pro Hands-Free AG Audio", "arsham’s AirPods Pro")]
    [InlineData("BT5.0 KB", "BT5.0 KB")]
    [InlineData("  ", "Bluetooth device")]
    public void Names_lose_the_profile_endings(string raw, string clean) => BluetoothBattery.CleanName(raw).Should().Be(clean);

    [Fact]
    public void Emptiest_comes_first_and_charging_last()
    {
        var list = BluetoothBattery.Sort(
        [
            new BatteryDevice("a", "Keyboard", BluetoothKind.Keyboard, 80),
            new BatteryDevice("b", "Buds", BluetoothKind.Headphones, 15),
            new BatteryDevice("c", "Pad", BluetoothKind.Controller, 5, Charging: true),
        ]);

        list.Select(d => d.Id).Should().Equal("b", "a", "c");
        list[0].IsLow.Should().BeTrue();
        list[2].IsLow.Should().BeFalse("a charging device is not low");
    }

    [Fact]
    public void Low_battery_warns_once_and_again_only_after_charging()
    {
        var warnings = new LowBatteryWarnings();
        BatteryDevice Buds(int percent, bool? charging = null) => new("b", "Buds", BluetoothKind.Headphones, percent, charging);

        warnings.Update([Buds(50)]).Should().BeEmpty();
        warnings.Update([Buds(20)]).Should().ContainSingle();
        warnings.Update([Buds(18)]).Should().BeEmpty("already warned");
        warnings.Update([Buds(25)]).Should().BeEmpty("not high enough to re-arm");
        warnings.Update([Buds(19)]).Should().BeEmpty();
        warnings.Update([Buds(19, charging: true)]).Should().BeEmpty();
        warnings.Update([Buds(15)]).Should().ContainSingle("charging re-armed it");
        warnings.Update([]).Should().BeEmpty();
        warnings.Update([Buds(15)]).Should().ContainSingle("it went away and came back low");
    }
}
