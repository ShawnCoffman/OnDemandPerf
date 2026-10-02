using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

/// <summary>
/// Windows 11 can run background or hidden processes in "efficiency mode" (EcoQoS), steering their threads to slow cores at
/// low clocks. A benchmark must not be silently throttled, so the worker opts itself out. This changes only this process.
/// </summary>
public static class ProcessQos
{
    private const int ProcessPowerThrottling = 4;   // PROCESS_INFORMATION_CLASS
    private const uint CurrentVersion = 1;          // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    private const uint ExecutionSpeed = 1;          // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    /// <summary>Returns "OptedOut" on success, otherwise "Failed: reason". Never throws.</summary>
    public static string OptOutOfPowerThrottling()
    {
        try
        {
            // ControlMask selects the policy we are setting; StateMask 0 means "do not throttle".
            var state = new WindowsNative.ProcessPowerThrottlingState { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
            return WindowsNative.SetProcessInformation(WindowsNative.GetCurrentProcess(), ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf<WindowsNative.ProcessPowerThrottlingState>())
                ? "OptedOut" : "Failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        catch (Exception ex) { return "Failed: " + ex.Message; }
    }
}
