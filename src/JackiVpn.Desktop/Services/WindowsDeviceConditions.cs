using System;
using System.Runtime.InteropServices;
using JackiVpn.Application.Background;

namespace JackiVpn.Desktop.Services;

/// <summary>
/// Windows power and data-cost signals: battery saver from <c>GetSystemPowerStatus</c>, metered networks from the
/// Network List Manager's cost (mobile broadband, a Wi-Fi marked metered, roaming, over the data limit).
/// </summary>
internal sealed partial class WindowsDeviceConditions : IDeviceConditions
{
    public bool PowerSaver => GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;

    public bool Metered
    {
        get
        {
            object? manager = null;
            try
            {
                manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: true)!);
                if (manager is not INetworkCostManager cost)
                {
                    return false;
                }

                cost.GetCost(out var flags, IntPtr.Zero);
                return (flags & (CostFixed | CostVariable | CostOverDataLimit | CostRoaming)) != 0;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                if (manager is not null && Marshal.IsComObject(manager))
                {
                    Marshal.ReleaseComObject(manager);
                }
            }
        }
    }

    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const uint CostFixed = 0x2;
    private const uint CostVariable = 0x4;
    private const uint CostOverDataLimit = 0x10000;
    private const uint CostRoaming = 0x40000;

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destIpAddr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
