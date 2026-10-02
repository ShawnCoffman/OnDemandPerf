using System.Runtime.InteropServices;

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
}
