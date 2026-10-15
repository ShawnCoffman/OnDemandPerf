using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

public static class MemoryBenchmark
{
    internal const int BudgetSeconds = 21;
    internal const int PassMilliseconds = 900;
    internal const int PassesPerOperation = 3;
    internal const int MultiPassMilliseconds = 400;
    internal const int MaxMultiWorkers = 8;
    /// <summary>
    /// Each of the two buffers. Fixed, so results compare across machines: at least twice the largest consumer CPU cache
    /// (128 MB), where a buffer that shrank on low-memory machines could fit in cache and look faster than RAM.
    /// </summary>
    internal const int BufferBytes = 256 * 1024 * 1024;
    /// <summary>Available RAM that must remain beyond the buffers, so the test does not push the PC into paging.</summary>
    internal const long HeadroomBytes = 1024L * 1024 * 1024;
    internal const long RequiredAvailableBytes = 2L * BufferBytes + HeadroomBytes;
    public static string Profile { get; } = ProfileHash.Compute("memory-v4", BufferBytes, PassMilliseconds,
        PassesPerOperation, MultiPassMilliseconds, MaxMultiWorkers, MemoryLatencyBenchmark.Profile);

    /// <param name="token">User cancellation only; time limits come from the budget and <paramref name="available"/>.</param>
    /// <param name="available">Time left in the run for this stage; the stage stops at this or its own budget, whichever is sooner.</param>
    public static MemoryResult Run(CancellationToken token, Action<string> progress, TimeSpan? available = null)
    {
        var result = new MemoryResult();
        var timer = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(StageBudget.Limit(BudgetSeconds, available));
        try
        {
            var status = new WindowsNative.MemoryStatus { Length = (uint)Marshal.SizeOf<WindowsNative.MemoryStatus>() };
            if (!WindowsNative.GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("Available RAM unavailable.");
            const int size = BufferBytes;
            if (status.AvailablePhysical < RequiredAvailableBytes)
            {
                result.Status = "Skipped";
                result.Notes.Add(FormattableString.Invariant($"Only {status.AvailablePhysical / 1073741824d:F1} GiB of RAM was available; the RAM test needs {RequiredAvailableBytes / 1073741824d:F1} GiB free so it does not push the PC into paging. Close programs and run the test again."));
                return result;
            }
            byte[] source, destination;
            try { source = new byte[size]; destination = new byte[size]; }
            catch (OutOfMemoryException)
            {
                result.Status = "Skipped";
                result.Notes.Add("The RAM test could not allocate its two 256 MiB buffers; the PC is short of memory.");
                return result;
            }
            result.BufferBytes = size;
            source.AsSpan().Fill(0x5A);
            destination.AsSpan().Fill(0x5A); // Commit pages outside measurement.
            // A single worker on an efficiency core measures that core, not the memory, so steer it like the CPU test.
            using (var placement = CoreSelection.PreferFastest())
            {
                foreach (string operation in new[] { "Copy", "Read", "Write" })
                {
                    progress($"RAM {operation.ToLowerInvariant()}: warmup and {PassesPerOperation} passes...");
                    Perform(operation, source, destination, budget.Token);
                    for (int pass = 1; pass <= PassesPerOperation; pass++)
                    {
                        long bytes = 0;
                        var watch = Stopwatch.StartNew();
                        do { Perform(operation, source, destination, budget.Token); bytes += size; placement.Sample(); }
                        while (watch.ElapsedMilliseconds < PassMilliseconds);
                        watch.Stop();
                        budget.Token.ThrowIfCancellationRequested();
                        bool verified = operation == "Read" || (operation == "Write"
                            ? destination.AsSpan().IndexOfAnyExcept((byte)0xA5) < 0 : destination.AsSpan().SequenceEqual(source));
                        if (!verified) throw new InvalidDataException("RAM output verification failed.");
                        result.Passes.Add(new(operation, pass, bytes, watch.Elapsed.TotalSeconds,
                            bytes / watch.Elapsed.TotalSeconds / 1_000_000_000d, true));
                    }
                }
                (result.CorePlacement, result.OnFastestCoresPercent) = (placement.Description, placement.OnFastestPercent);
            }
            RecordRates(result, ["Copy", "Read", "Write"]);
            result.CopyGBps = Rate(result, "Copy"); result.ReadGBps = Rate(result, "Read"); result.WriteGBps = Rate(result, "Write");

            // Single-threaded loops cannot saturate the memory system, so they are blind to channel-count problems.
            // A bounded multi-worker read/copy pass gives a number that responds to dual vs single channel.
            int workers = Math.Min(Environment.ProcessorCount, MaxMultiWorkers);
            if (workers >= 2)
            {
                result.MultiThreadWorkers = workers;
                foreach (string operation in new[] { "Read", "Copy" })
                {
                    progress($"RAM multithread {operation.ToLowerInvariant()}: {workers} workers, {PassesPerOperation} passes...");
                    string name = operation + "MT";
                    for (int pass = 1; pass <= PassesPerOperation; pass++)
                    {
                        var (bytes, seconds) = MeasureParallel(operation, source, destination, workers, MultiPassMilliseconds, budget.Token);
                        result.Passes.Add(new(name, pass, bytes, seconds, bytes / seconds / 1_000_000_000d, true));
                    }
                }
                RecordRates(result, ["ReadMT", "CopyMT"]);
                result.MultiThreadReadGBps = Rate(result, "ReadMT");
                result.MultiThreadCopyGBps = Rate(result, "CopyMT");
            }
            else result.Notes.Add("Multithread bandwidth skipped: fewer than two logical processors.");

            result.Latency = new MemoryLatencyResult { WorkingSetBytes = size };
            MemoryLatencyBenchmark.Run(source, result.Latency, budget.Token, progress);
        }
        catch (OperationCanceledException) { result.Status = token.IsCancellationRequested ? "Cancelled" : "TimedOut"; result.Notes.Add("RAM time limit or cancellation reached; only completed measurements are reported."); }
        catch (Exception ex) { result.Status = "Failed"; result.Notes.Add(ex.Message); }
        finally { result.DurationSeconds = timer.Elapsed.TotalSeconds; }
        return result;
    }

    private static double Rate(MemoryResult result, string operation)
    {
        var passes = result.Passes.Where(p => p.Operation == operation).ToArray();
        return passes.Sum(p => p.BytesProcessed) / passes.Sum(p => p.DurationSeconds) / 1_000_000_000d;
    }

    private static void RecordRates(MemoryResult result, string[] operations)
    {
        foreach (var operation in operations)
        {
            var rates = result.Passes.Where(p => p.Operation == operation).Select(p => p.GBps).ToArray();
            double variation = Stats.VariationPercent(rates);
            result.VariationPercent[operation] = variation;
            if (variation > 5) result.Notes.Add($"{operation}: pass variation {variation:F1}% exceeded the provisional 5% noise threshold.");
        }
    }

    private static void Perform(string operation, byte[] source, byte[] destination, CancellationToken token)
        => Perform(operation, source.AsSpan(), destination.AsSpan(), token);

    private static void Perform(string operation, ReadOnlySpan<byte> source, Span<byte> destination, CancellationToken token)
    {
        const int chunk = 1024 * 1024;
        for (int offset = 0; offset < source.Length; offset += chunk)
        {
            token.ThrowIfCancellationRequested();
            var input = source.Slice(offset, Math.Min(chunk, source.Length - offset));
            var output = destination.Slice(offset, input.Length);
            if (operation == "Copy") input.CopyTo(output);
            else if (operation == "Write") output.Fill(0xA5);
            else
            {
                // Consume every byte using a vector OR reduction; result checks prevent dead-code elimination.
                var vectors = MemoryMarshal.Cast<byte, Vector<byte>>(input);
                var value = Vector<byte>.Zero;
                foreach (var vector in vectors) value |= vector;
                if (!Vector.EqualsAll(value, new Vector<byte>(0x5A))) throw new InvalidDataException("RAM read checksum failed.");
            }
        }
    }

    /// <summary>
    /// Runs one timed pass with several dedicated workers, each streaming its own 4 KiB-aligned slice of the shared buffers.
    /// Workers warm up on their slice, rendezvous, then run until a common deadline. Copy output is verified after timing.
    /// </summary>
    private static (long Bytes, double Seconds) MeasureParallel(string operation, byte[] source, byte[] destination,
        int workers, int milliseconds, CancellationToken token)
    {
        int slice = source.Length / workers / 4096 * 4096;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var ready = new CountdownEvent(workers);
        using var gate = new ManualResetEventSlim(false);
        var failures = new ConcurrentQueue<Exception>();
        var bytes = new long[workers];
        var ends = new long[workers];
        var threads = new List<Thread>();
        long start = 0;
        try
        {
            for (int worker = 0; worker < workers; worker++)
            {
                int index = worker;
                var thread = new Thread(() =>
                {
                    bool signalled = false;
                    try
                    {
                        var input = source.AsSpan(index * slice, slice);
                        var output = destination.AsSpan(index * slice, slice);
                        Perform(operation, input, output, stop.Token); // untimed warmup over this worker's slice
                        ready.Signal(); signalled = true;
                        gate.Wait(stop.Token);
                        long local = 0;
                        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds)
                        {
                            Perform(operation, input, output, stop.Token);
                            local += slice;
                        }
                        bytes[index] = local;
                        ends[index] = Stopwatch.GetTimestamp();
                    }
                    catch (Exception ex) { failures.Enqueue(ex); stop.Cancel(); }
                    finally { if (!signalled) ready.Signal(); }
                }) { IsBackground = true, Name = $"OnDemandPerf RAM {index}" };
                thread.Start();
                threads.Add(thread);
            }
            ready.Wait(stop.Token);
            start = Stopwatch.GetTimestamp();
            gate.Set();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (start == 0) stop.Cancel();
            gate.Set();
            foreach (var thread in threads) thread.Join();
        }
        token.ThrowIfCancellationRequested();
        if (!failures.IsEmpty) throw new InvalidOperationException("RAM worker failed.", failures.First());
        if (bytes.Any(b => b == 0)) throw new InvalidOperationException("Not every RAM worker completed timed work.");
        if (operation == "Copy" && !destination.AsSpan(0, slice * workers).SequenceEqual(source.AsSpan(0, slice * workers)))
            throw new InvalidDataException("RAM multithread copy verification failed.");
        return (bytes.Sum(), Stopwatch.GetElapsedTime(start, ends.Max()).TotalSeconds);
    }
}
