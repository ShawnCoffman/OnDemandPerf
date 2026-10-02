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
    double? SystemCpuPercent, ulong? AvailableMemoryBytes, string PowerSource);

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
                    Result.Samples.Add(new(now, now - previous, Volatile.Read(ref stage), cpu, available, power));
                    previous = now;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { Result.Notes.Add("Activity sampling stopped: " + ex.Message); }
        });
    }
    public void Dispose() { if (disposed) return; disposed = true; stop.Cancel(); task.GetAwaiter().GetResult(); stop.Dispose(); }
}

