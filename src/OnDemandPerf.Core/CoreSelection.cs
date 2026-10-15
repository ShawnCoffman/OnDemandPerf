using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

/// <summary>
/// Hybrid CPUs mix performance and efficiency cores, and Windows may run a lone thread on either: on the dev machine that moved
/// single-thread results by about 2x. Single-worker measurements therefore ask for the fastest cores (the highest efficiency
/// class reported by CPU sets) and record where they actually ran.
/// </summary>
public static class CoreSelection
{
    internal sealed record CpuSet(uint Id, ushort Group, byte LogicalProcessor, byte EfficiencyClass, bool Allocated);

    private static readonly Lazy<IReadOnlyList<CpuSet>> sets = new(Read);

    /// <summary>Restricts the calling thread to the fastest cores until the placement is disposed. Never throws.</summary>
    public static CorePlacement PreferFastest()
    {
        IReadOnlyList<CpuSet> all;
        try { all = sets.Value; }
        catch (Exception ex) { return new CorePlacement("Not applied: CPU sets unavailable (" + ex.Message + ")", null, 0, false); }
        if (all.Count == 0) return new CorePlacement("Not applied: no CPU sets reported", null, 0, false);
        var classes = new Dictionary<int, byte>();
        foreach (var set in all) classes[Key(set.Group, set.LogicalProcessor)] = set.EfficiencyClass;
        if (all.Select(s => s.EfficiencyClass).Distinct().Count() == 1)
            return new CorePlacement("Not needed: all cores are the same type", null, 0, false);
        var ids = Fastest(all, Usable());
        byte top = all.Max(s => s.EfficiencyClass);
        if (ids.Length == 0) return new CorePlacement("Not applied: the fastest cores are not available to this process", classes, top, false);
        if (!WindowsNative.SetThreadSelectedCpuSets(WindowsNative.GetCurrentThread(), ids, (uint)ids.Length))
            return new CorePlacement("Not applied: " + new Win32Exception(Marshal.GetLastWin32Error()).Message, classes, top, false);
        return new CorePlacement($"Fastest cores (efficiency class {top}, {ids.Length} logical processors)", classes, top, true);
    }

    /// <summary>CPU set ids of the highest efficiency class among the usable, unreserved sets.</summary>
    internal static uint[] Fastest(IReadOnlyList<CpuSet> all, Func<CpuSet, bool> usable)
    {
        var candidates = all.Where(s => !s.Allocated && usable(s)).ToList();
        if (candidates.Count == 0) return [];
        byte top = candidates.Max(s => s.EfficiencyClass);
        // Only the machine's fastest class counts: if those cores are all unusable, steering to a slower class would mislead.
        if (top != all.Max(s => s.EfficiencyClass)) return [];
        return candidates.Where(s => s.EfficiencyClass == top).Select(s => s.Id).ToArray();
    }

    /// <summary>Parses SYSTEM_CPU_SET_INFORMATION records (variable size, type 0 = CPU set).</summary>
    internal static List<CpuSet> Parse(byte[] buffer, int length)
    {
        var result = new List<CpuSet>();
        for (int offset = 0; offset < length;)
        {
            if (length - offset < 8) throw new InvalidDataException("Truncated CPU set record.");
            uint size = BitConverter.ToUInt32(buffer, offset);
            if (size < 8 || size > length - offset) throw new InvalidDataException("Invalid CPU set record size.");
            // Id at 8, Group at 12, LogicalProcessorIndex at 14, EfficiencyClass at 18, flags at 19 (bit 1 = allocated).
            if (BitConverter.ToUInt32(buffer, offset + 4) == 0 && size >= 20)
                result.Add(new(BitConverter.ToUInt32(buffer, offset + 8), BitConverter.ToUInt16(buffer, offset + 12),
                    buffer[offset + 14], buffer[offset + 18], (buffer[offset + 19] & 2) != 0));
            offset += (int)size;
        }
        return result;
    }

    internal static int Key(ushort group, byte number) => group << 8 | number;

    private static IReadOnlyList<CpuSet> Read()
    {
        WindowsNative.GetSystemCpuSetInformation(null, 0, out uint length, IntPtr.Zero, 0);
        if (length == 0 || length > 16 * 1024 * 1024) throw new InvalidOperationException("Unable to size the CPU set buffer.");
        var buffer = new byte[length];
        if (!WindowsNative.GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Parse(buffer, (int)length);
    }

    /// <summary>Respects the process affinity on single-group machines; with several groups every set is offered.</summary>
    private static Func<CpuSet, bool> Usable()
    {
        if (WindowsNative.GetActiveProcessorGroupCount() != 1) return _ => true;
        using var process = Process.GetCurrentProcess();
        ulong mask = (ulong)process.ProcessorAffinity.ToInt64();
        return set => set.Group == 0 && set.LogicalProcessor < 64 && (mask >> set.LogicalProcessor & 1) != 0;
    }
}

/// <summary>A thread's core preference plus a tally of the core types it was observed on. Use and dispose on one thread.</summary>
public sealed class CorePlacement : IDisposable
{
    private readonly Dictionary<int, byte>? classes;
    private readonly byte fastest;
    private readonly bool applied;
    private long onFastest, samples;

    internal CorePlacement(string description, Dictionary<int, byte>? classes, byte fastest, bool applied)
        => (Description, this.classes, this.fastest, this.applied) = (description, classes, fastest, applied);

    public string Description { get; }
    /// <summary>Share of samples taken on the fastest core type; null on CPUs with one core type or with no samples.</summary>
    public double? OnFastestPercent => samples == 0 ? null : 100d * onFastest / samples;

    /// <summary>Records which core type the thread is on now. Cheap enough to call between units of timed work.</summary>
    public void Sample()
    {
        if (classes is null) return;
        WindowsNative.GetCurrentProcessorNumberEx(out var number);
        samples++;
        if (classes.TryGetValue(CoreSelection.Key(number.Group, number.Number), out byte type) && type == fastest) onFastest++;
    }

    public void Dispose()
    {
        if (applied) WindowsNative.SetThreadSelectedCpuSets(WindowsNative.GetCurrentThread(), null, 0);
    }
}
