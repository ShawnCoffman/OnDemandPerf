using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OnDemandPerf.Core;

public static class EnvironmentCollector
{
    public static void Collect(TestEnvironment result, List<string> warnings, CancellationToken token)
    {
        SystemInventory.TryCollect("CPU activity", warnings, () =>
        {
            if (WindowsNative.GetActiveProcessorGroupCount() != 1)
            {
                result.CpuSampleStatus = "Unsupported";
                warnings.Add("CPU activity: machine-wide sampling is unavailable on systems with multiple processor groups.");
                return;
            }
            if (!WindowsNative.GetSystemTimes(out var idleBefore, out var kernelBefore, out var userBefore))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var timer = Stopwatch.StartNew();
            if (token.WaitHandle.WaitOne(2000)) return;
            if (!WindowsNative.GetSystemTimes(out var idleAfter, out var kernelAfter, out var userAfter))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            timer.Stop();
            if (idleAfter < idleBefore || kernelAfter < kernelBefore || userAfter < userBefore)
                throw new InvalidDataException("CPU counters moved backwards.");
            var total = (kernelAfter - kernelBefore) + (userAfter - userBefore);
            var idle = idleAfter - idleBefore;
            if (total == 0 || idle > total) throw new InvalidDataException("Invalid CPU sample delta.");
            result.CpuUtilizationPercent = 100d * (total - idle) / total;
            result.CpuSampleDurationSeconds = timer.Elapsed.TotalSeconds;
            result.CpuSampleStatus = "Success";
        });
        token.ThrowIfCancellationRequested();
        result.CapturedAt = DateTimeOffset.Now;
        SystemInventory.TryCollect("RAM", warnings, () =>
        {
            var memory = new WindowsNative.MemoryStatus { Length = (uint)Marshal.SizeOf<WindowsNative.MemoryStatus>() };
            if (!WindowsNative.GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (memory.TotalPhysical == 0 || memory.AvailablePhysical > memory.TotalPhysical)
                throw new InvalidDataException("Invalid physical memory values.");
            result.TotalPhysicalBytes = memory.TotalPhysical;
            result.AvailablePhysicalBytes = memory.AvailablePhysical;
            result.UsedPhysicalBytes = memory.TotalPhysical - memory.AvailablePhysical;
            result.RamUtilizationPercent = 100d * result.UsedPhysicalBytes.Value / memory.TotalPhysical;
        });
        SystemInventory.TryCollect("Power source", warnings, () =>
        {
            if (!WindowsNative.GetSystemPowerStatus(out var power)) throw new Win32Exception(Marshal.GetLastWin32Error());
            result.PowerSource = power.AcLineStatus switch { 0 => "Battery", 1 => "AC", _ => "Unknown" };
            result.BatteryPresent = power.BatteryFlag == 255 ? null : (power.BatteryFlag & 128) == 0;
            if (result.BatteryPresent == true && power.BatteryLifePercent <= 100)
                result.BatteryChargePercent = power.BatteryLifePercent;
        });
        SystemInventory.TryCollect("Power plan", warnings, () =>
        {
            uint error = WindowsNative.PowerGetActiveScheme(IntPtr.Zero, out var pointer);
            if (error != 0) throw new Win32Exception((int)error);
            try
            {
                if (pointer == IntPtr.Zero) throw new InvalidDataException("Power plan pointer unavailable.");
                var scheme = Marshal.PtrToStructure<Guid>(pointer);
                result.PowerPlanId = scheme;
                uint length = 0;
                error = WindowsNative.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref length);
                if (error is not (0 or 234) || length < 2 || length > 65536)
                    throw new InvalidDataException($"Power plan name unavailable ({error}).");
                var buffer = new byte[length];
                error = WindowsNative.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref length);
                if (error != 0) throw new Win32Exception((int)error);
                result.PowerPlan = Encoding.Unicode.GetString(buffer, 0, checked((int)length)).TrimEnd('\0');
            }
            finally { if (pointer != IntPtr.Zero) WindowsNative.LocalFree(pointer); }
        });
    }
}

