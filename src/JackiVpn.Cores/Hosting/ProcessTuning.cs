using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Cores.Hosting;

/// <summary>
/// Every packet of every application passes through the core, so its scheduling latency is everyone's latency.
/// On Windows the core runs above normal priority and is exempt from power throttling (EcoQoS), which otherwise
/// slows background processes on laptops and shows up as periodic lag spikes in games and calls.
/// </summary>
internal static partial class ProcessTuning
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
