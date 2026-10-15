using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OnDemandPerf.Core;

public static class EnvironmentCollector
{
    public static void Collect(TestEnvironment result, List<string> warnings, CancellationToken token)
    {
        // Process CPU times are snapshotted around the same two-second window as the system CPU sample.
        List<ProcessActivity.Entry>? processesBefore = null;
        var processClock = Stopwatch.StartNew();
        SystemInventory.TryCollect("Process activity", warnings, () => processesBefore = ProcessActivity.Snapshot());
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
        if (processesBefore is not null && processClock.Elapsed.TotalSeconds >= 1)
            SystemInventory.TryCollect("Process activity", warnings, () => result.TopProcesses.AddRange(ProcessActivity.Top(
                processesBefore, ProcessActivity.Snapshot(), processClock.Elapsed.TotalSeconds,
                (int)WindowsNative.GetActiveProcessorCount(ushort.MaxValue), 5, Environment.ProcessId)));
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
            // For GlobalMemoryStatusEx the "page file" fields are the commit limit and the commit still available.
            if (memory.TotalPageFile > 0 && memory.AvailablePageFile <= memory.TotalPageFile)
                (result.CommitLimitBytes, result.CommitBytes) = (memory.TotalPageFile, memory.TotalPageFile - memory.AvailablePageFile);
        });
        SystemInventory.TryCollect("Power source", warnings, () =>
        {
            if (!WindowsNative.GetSystemPowerStatus(out var power)) throw new Win32Exception(Marshal.GetLastWin32Error());
            result.PowerSource = power.AcLineStatus switch { 0 => "Battery", 1 => "AC", _ => "Unknown" };
            result.BatteryPresent = power.BatteryFlag == 255 ? null : (power.BatteryFlag & 128) == 0;
            if (result.BatteryPresent == true && power.BatteryLifePercent <= 100)
                result.BatteryChargePercent = power.BatteryLifePercent;
            result.EnergySaverOn = power.SystemStatusFlag == 1;
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
        if (result.PowerPlanId is { } plan)
        {
            result.PowerPlanType = PowerPolicy.PlanType(plan);
            (result.MaxProcessorStatePercent, result.ProcessorBoostMode) = PowerPolicy.ReadProcessorLimits(plan, result.PowerSource == "Battery");
        }
        SystemInventory.TryCollect("Power mode", warnings, () => result.PowerMode = PowerPolicy.ReadPowerMode());
        result.UptimeDays = Environment.TickCount64 / 86_400_000d;
        SystemInventory.TryCollect("Pending restart", warnings, () =>
        {
            var reasons = new List<string>();
            using (var servicing = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (servicing is not null) reasons.Add("Windows component servicing");
            using (var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (update is not null) reasons.Add("Windows Update");
            result.PendingRestart = reasons;
        });
        SystemInventory.TryCollect("System drive", warnings, () =>
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            result.SystemDrive = drive.Name;
            result.SystemDriveFreeBytes = (ulong)drive.AvailableFreeSpace;
            result.SystemDriveTotalBytes = (ulong)drive.TotalSize;
        });
    }
}

