using System;
using System.Runtime.InteropServices;

namespace JackiVpn.Desktop.Services;

/// <summary>One reading of the machine: CPU use since the last reading, memory in use, battery (null = no battery).</summary>
public readonly record struct VitalsReading(double? Cpu, double Memory, int? Battery, bool Charging);

/// <summary>
/// CPU, memory and battery for the open notch. Nothing runs in the background: each <see cref="Read"/> is a few
/// system calls, and the CPU figure is the share of busy time since the previous call.
/// </summary>
public sealed partial class Vitals
{
    private ulong _idle;
    private ulong _total;

    public VitalsReading Read()
    {
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes idle time.
            var total = kernel + user;
            if (_total != 0 && total > _total)
            {
                cpu = Math.Clamp(1 - ((double)(idle - _idle) / (total - _total)), 0, 1);
            }

            _idle = idle;
            _total = total;
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var load = GlobalMemoryStatusEx(ref memory) ? memory.MemoryLoad / 100.0 : 0;
        int? battery = null;
        var charging = false;
        if (GetSystemPowerStatus(out var power) && (power.BatteryFlag & 128) == 0 && power.BatteryLifePercent <= 100)
        {
            battery = power.BatteryLifePercent;
            charging = power.AcLineStatus == 1;
        }

        return new VitalsReading(cpu, load, battery, charging);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);
}
