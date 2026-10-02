using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

public static class SystemInventory
{
    public static DeviceInfo Collect(List<string> warnings)
    {
        string cpuName = "Unavailable";
        TryCollect("CPU name", warnings, () =>
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpuName = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unavailable";
        });
        var device = new DeviceInfo(Environment.MachineName, RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            cpuName,
            Environment.ProcessorCount, RuntimeInformation.FrameworkDescription);
        TryCollect("Windows version", warnings, () =>
        {
            using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
                ?? throw new InvalidOperationException("Windows version registry key unavailable.");
            using var product = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            var name = version.GetValue("ProductName") as string ?? RuntimeInformation.OSDescription;
            var build = version.GetValue("CurrentBuildNumber") as string;
            // Windows 11 can retain a Windows 10 ProductName. Never relabel Windows Server.
            if (product?.GetValue("ProductType") as string == "WinNT" &&
                int.TryParse(build, out int number) && number >= 22000)
                name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            device = device with { OperatingSystem = name };
            device.WindowsEdition = version.GetValue("EditionID") as string;
            device.WindowsRelease = version.GetValue("DisplayVersion") as string;
            device.WindowsBuild = build is null ? null : $"{build}.{version.GetValue("UBR") ?? 0}";
        });
        TryCollect("CPU topology", warnings, () =>
        {
            uint length = 0;
            WindowsNative.GetLogicalProcessorInformationEx(0, null, ref length);
            if (Marshal.GetLastWin32Error() != 122 || length < 8 || length > 16 * 1024 * 1024)
                throw new InvalidOperationException("Unable to size CPU topology buffer.");
            var buffer = new byte[length];
            if (!WindowsNative.GetLogicalProcessorInformationEx(0, buffer, ref length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            int cores = 0;
            for (int offset = 0; offset < length;)
            {
                if (length - offset < 8) throw new InvalidDataException("Truncated CPU topology record.");
                uint relationship = BitConverter.ToUInt32(buffer, offset);
                uint size = BitConverter.ToUInt32(buffer, offset + 4);
                if (size < 8 || size > length - offset) throw new InvalidDataException("Invalid CPU topology record size.");
                if (relationship == 0) cores++;
                offset += checked((int)size);
            }
            if (cores == 0) throw new InvalidDataException("No active cores reported.");
            device.PhysicalCores = cores;
            var logical = WindowsNative.GetActiveProcessorCount(ushort.MaxValue);
            if (logical == 0) throw new InvalidOperationException("Logical processor count unavailable.");
            device.SystemLogicalProcessors = logical;
        });
        return device;
    }

    internal static void TryCollect(string name, List<string> warnings, Action collect)
    {
        try { collect(); }
        catch (Exception ex) { warnings.Add($"{name}: {ex.Message}"); }
    }
}
