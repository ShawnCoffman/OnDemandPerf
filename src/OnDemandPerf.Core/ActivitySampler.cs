using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

public sealed class ActivityResult
{
    public string Method { get; init; } = "Approximately one sample per second; system CPU includes the benchmark; observational context, not a thermal diagnosis";
    public List<ActivitySample> Samples { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}
public sealed record ActivitySample(double ElapsedSeconds, double IntervalSeconds, string Stage,
    double? SystemCpuPercent, ulong? AvailableMemoryBytes, string PowerSource)
{
    /// <summary>Average CPU clock as a percentage of its base clock (above 100 is boost); from "% Processor Performance".</summary>
    public double? ProcessorPerformancePercent { get; init; }
    /// <summary>Windows' "% Performance Limit": below 100 means firmware limited the clock (power, thermal or platform).</summary>
    public double? PerformanceLimitPercent { get; init; }
}

public sealed class ActivitySampler : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task task;
    private string stage = "Baseline";
    private bool disposed;
    public ActivityResult Result { get; } = new();
    public string Stage { set => Volatile.Write(ref stage, value); }
    public ActivitySampler()
    {
        task = Task.Run(async () =>
        {
            try
            {
                using var clockCounters = ClockCounters.TryOpen(Result.Notes);
                bool supported = WindowsNative.GetActiveProcessorGroupCount() == 1;
                if (!supported) Result.Notes.Add("System CPU sampling unavailable with multiple processor groups.");
                bool previousValid = WindowsNative.GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
                var clock = Stopwatch.StartNew();
                double previous = clock.Elapsed.TotalSeconds;
                while (true)
                {
                    await Task.Delay(1000, stop.Token).ConfigureAwait(false);
                    double now = clock.Elapsed.TotalSeconds;
                    double? cpu = null;
                    bool valid = WindowsNative.GetSystemTimes(out ulong nextIdle, out ulong nextKernel, out ulong nextUser);
                    if (supported && previousValid && valid && nextIdle >= idle && nextKernel >= kernel && nextUser >= user)
                    {
                        ulong total = nextKernel - kernel + nextUser - user, idleDelta = nextIdle - idle;
                        if (total > 0 && idleDelta <= total) cpu = 100d * (total - idleDelta) / total;
                    }
                    (idle, kernel, user, previousValid) = (nextIdle, nextKernel, nextUser, valid);
                    var memory = new WindowsNative.MemoryStatus { Length = (uint)Marshal.SizeOf<WindowsNative.MemoryStatus>() };
                    ulong? available = WindowsNative.GlobalMemoryStatusEx(ref memory) ? memory.AvailablePhysical : null;
                    string power = WindowsNative.GetSystemPowerStatus(out var state)
                        ? state.AcLineStatus switch { 0 => "Battery", 1 => "AC", _ => "Unknown" } : "Unavailable";
                    var (performance, limit) = clockCounters?.Read() ?? (null, null);
                    Result.Samples.Add(new(now, now - previous, Volatile.Read(ref stage), cpu, available, power)
                        { ProcessorPerformancePercent = performance, PerformanceLimitPercent = limit });
                    previous = now;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { Result.Notes.Add("Activity sampling stopped: " + ex.Message); }
        });
    }
    public void Dispose() { if (disposed) return; disposed = true; stop.Cancel(); task.GetAwaiter().GetResult(); stop.Dispose(); }

    /// <summary>
    /// Performance-counter view of the CPU clock across all processors, readable without administrator rights. Temperatures
    /// are not available this way, so throttling is inferred from the clock, never measured directly.
    /// </summary>
    private sealed class ClockCounters : IDisposable
    {
        private const uint FormatDouble = 0x200, NoCap100 = 0x8000;
        private readonly IntPtr query, performance, limit;
        private ClockCounters(IntPtr query, IntPtr performance, IntPtr limit) => (this.query, this.performance, this.limit) = (query, performance, limit);

        public static ClockCounters? TryOpen(List<string> notes)
        {
            if (WindowsNative.PdhOpenQuery(null, IntPtr.Zero, out var query) != 0) { notes.Add("CPU clock counters unavailable."); return null; }
            if (WindowsNative.PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out var performance) != 0 ||
                WindowsNative.PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Performance Limit", IntPtr.Zero, out var limit) != 0)
            {
                WindowsNative.PdhCloseQuery(query);
                notes.Add("CPU clock counters unavailable on this system.");
                return null;
            }
            WindowsNative.PdhCollectQueryData(query); // rate counters need a first sample
            return new ClockCounters(query, performance, limit);
        }

        public (double?, double?) Read()
        {
            if (WindowsNative.PdhCollectQueryData(query) != 0) return (null, null);
            return (Value(performance), Value(limit));
        }

        private static double? Value(IntPtr counter) =>
            WindowsNative.PdhGetFormattedCounterValue(counter, FormatDouble | NoCap100, out _, out var value) == 0 && value.Status <= 1
                ? Math.Round(value.Value, 1) : null;

        public void Dispose() => WindowsNative.PdhCloseQuery(query);
    }
}

