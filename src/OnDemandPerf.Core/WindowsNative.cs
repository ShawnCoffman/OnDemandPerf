using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OnDemandPerf.Core;

// Native interop is isolated here. Buffers have explicit sizes; no unsafe blocks or raw pointer arithmetic.
internal static class WindowsNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatus
    {
        internal uint Length, MemoryLoad;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerStatus
    {
        internal byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        internal uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessPowerThrottlingState
    {
        internal uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref ProcessPowerThrottlingState information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(int relationship, [Out] byte[]? buffer, ref uint length);
    [DllImport("kernel32.dll")]
    internal static extern uint GetActiveProcessorCount(ushort groupNumber);
    [DllImport("kernel32.dll")]
    internal static extern ushort GetActiveProcessorGroupCount();
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup,
        IntPtr setting, [Out] byte[]? buffer, ref uint length);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetSystemFirmwareTable(uint signature, uint tableId, [Out] byte[]? buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle OpenDevice(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    /// <summary>The Settings power mode in effect (Windows 10 1709+); not in the SDK headers, so callers handle a missing export.</summary>
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessorNumber
    {
        internal ushort Group;
        internal byte Number, Reserved;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemCpuSetInformation([Out] byte[]? information, uint length, out uint returned,
        IntPtr process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[]? cpuSetIds, uint count);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    internal static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

    /// <summary>Class 5 (SystemProcessInformation) lists every process with its CPU times without opening any of them.</summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhCounterValue
    {
        internal uint Status;
        internal double Value;
    }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    internal static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    /// <summary>English counter paths work on every display language.</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    internal static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhCounterValue value);
    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
