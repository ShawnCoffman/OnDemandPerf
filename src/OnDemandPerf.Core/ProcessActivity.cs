using System.Runtime.InteropServices;
using System.Text;

namespace OnDemandPerf.Core;

/// <summary>
/// Which processes kept the PC busy just before the test. SystemProcessInformation lists every process's CPU time without
/// opening it, so services and protected processes (antivirus, updaters, indexers) are included for a standard user.
/// </summary>
public static class ProcessActivity
{
    internal sealed record Entry(int ProcessId, long CreateTime, string Name, long CpuTicks);

    private const int SystemProcessInformation = 5;
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);
    /// <summary>Fixed part of SYSTEM_PROCESS_INFORMATION on 64-bit Windows, up to and including UniqueProcessId.</summary>
    private const int RecordBytes = 88;

    internal static List<Entry> Snapshot()
    {
        // The record layout below is the 64-bit one; a 32-bit worker would need different offsets.
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Process CPU listing needs a 64-bit worker.");
        int size = 1024 * 1024;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = WindowsNative.NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == InfoLengthMismatch) { size = Math.Max(size * 2, needed + 256 * 1024); continue; }
                if (status != 0) throw new InvalidOperationException($"Process list unavailable (status 0x{status:X8}).");
                var copy = new byte[size];
                Marshal.Copy(buffer, copy, 0, size);
                // Image names point into the same buffer, so they are found by their offset from its address.
                return Parse(copy, buffer.ToInt64());
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Process list kept growing while it was read.");
    }

    internal static List<Entry> Parse(byte[] data, long baseAddress)
    {
        var entries = new List<Entry>();
        for (int offset = 0; ;)
        {
            if (data.Length - offset < RecordBytes) throw new InvalidDataException("Truncated process record.");
            uint next = BitConverter.ToUInt32(data, offset);
            long create = BitConverter.ToInt64(data, offset + 32);
            long cpu = BitConverter.ToInt64(data, offset + 40) + BitConverter.ToInt64(data, offset + 48); // user + kernel, 100 ns
            int nameLength = BitConverter.ToUInt16(data, offset + 56);
            long namePointer = BitConverter.ToInt64(data, offset + 64);
            int id = (int)BitConverter.ToInt64(data, offset + 80);
            string name = id == 0 ? "Idle" : id == 4 ? "System" : "Process " + id;
            long nameOffset = namePointer - baseAddress;
            if (nameLength > 0 && nameOffset >= 0 && nameOffset <= data.Length - nameLength)
                name = Encoding.Unicode.GetString(data, (int)nameOffset, nameLength);
            entries.Add(new(id, create, name, cpu));
            if (next == 0) break;
            if (next < RecordBytes || next > data.Length - offset) throw new InvalidDataException("Invalid process record offset.");
            offset += (int)next;
        }
        return entries;
    }

    /// <summary>Busiest processes between two snapshots, as a percentage of every logical processor; Idle is never listed.</summary>
    internal static List<ProcessCpu> Top(IEnumerable<Entry> before, IEnumerable<Entry> after, double seconds,
        int logicalProcessors, int count, int excludeProcessId)
    {
        var earlier = new Dictionary<(int, long), long>();
        foreach (var entry in before) earlier.TryAdd((entry.ProcessId, entry.CreateTime), entry.CpuTicks);
        double capacity = seconds * 1e7 * Math.Max(1, logicalProcessors);
        if (capacity <= 0) return [];
        return after
            .Where(e => e.ProcessId != 0 && e.ProcessId != excludeProcessId)
            // A process started during the sample spent all of its CPU time inside it.
            .Select(e => (Entry: e, Delta: earlier.TryGetValue((e.ProcessId, e.CreateTime), out long ticks) ? e.CpuTicks - ticks : e.CpuTicks))
            .Where(x => x.Delta > 0)
            .OrderByDescending(x => x.Delta)
            .Take(count)
            .Select(x => new ProcessCpu(x.Entry.Name, x.Entry.ProcessId, Math.Round(100d * x.Delta / capacity, 1)))
            .ToList();
    }
}
