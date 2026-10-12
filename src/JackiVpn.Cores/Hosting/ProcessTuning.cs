using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Cores.Hosting;

/// <summary>
/// Every packet of every application passes through the core, so its scheduling latency is everyone's latency.
/// On Windows the core runs above normal priority and is exempt from power throttling (EcoQoS), which otherwise
/// slows background processes on laptops and shows up as periodic lag spikes in games and calls.
/// </summary>
public static partial class ProcessTuning
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    public static void ForLowLatency(System.Diagnostics.Process process, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            process.PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug("Could not raise the core's priority: {Error}", ex.Message);
        }

        // ControlMask = execution speed, StateMask = 0: "never throttle this process".
        var state = new PowerThrottlingState { Version = PowerThrottlingCurrentVersion, ControlMask = PowerThrottlingExecutionSpeed, StateMask = 0 };
        if (!SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
        {
            logger.LogDebug("Could not exempt the core from power throttling (error {Error}).", Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Test and tuning cores measure servers in the background: below normal priority, so they never take CPU from
    /// the tunnel core or the user's apps. (No efficiency throttling: that would skew the latency they measure.)
    /// </summary>
    public static void ForBackground(System.Diagnostics.Process process, ILogger logger)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            logger.LogDebug("Could not lower the test core's priority: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Efficiency mode for this app's own process while nobody looks at it (Windows 11 "EcoQoS"): its timers and
    /// threads run on efficient cores at low clock. Never while the in-app traffic relay carries user traffic.
    /// </summary>
    public static void SetEfficiencyMode(System.Diagnostics.Process process, bool on, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed,
            StateMask = on ? PowerThrottlingExecutionSpeed : 0,
        };
        if (!SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
        {
            logger.LogDebug("Could not change efficiency mode (error {Error}).", Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);
}
