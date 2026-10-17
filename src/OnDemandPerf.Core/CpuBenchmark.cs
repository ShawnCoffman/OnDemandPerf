using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;

namespace OnDemandPerf.Core;

public static class CpuBenchmark
{
    internal const int InputBytes = 1024 * 1024;
    internal const int WarmupMilliseconds = 250;
    internal const int SingleMilliseconds = 1800;
    internal const int MultiMilliseconds = 3000;
    internal const int PassCount = 3;
    internal const int BudgetSeconds = 20;
    public const string DatasetId = "xorshift32-seed-12345678-repeated-64KiB-v1";
    public static string Profile { get; } = ProfileHash.Compute("deflate-v4", DatasetId, InputBytes, WarmupMilliseconds,
        SingleMilliseconds, MultiMilliseconds, PassCount, "Optimal");

    /// <param name="cancellationToken">User cancellation only; time limits come from the budget and <paramref name="available"/>.</param>
    /// <param name="available">Time left in the run for this stage; the stage stops at this or its own budget, whichever is sooner.</param>
    public static CpuResult Run(CancellationToken cancellationToken, Action<string> progress, TimeSpan? available = null) =>
        Run(cancellationToken, progress, available, MeasureStage);

    /// <summary>Measures one stage (all of its passes); the seam lets tests drive cancellation without running the benchmark.</summary>
    internal delegate CpuMeasurement StageMeasurer(byte[] data, int workers, int milliseconds, string name,
        CancellationToken token, Action<string> progress);

    internal static CpuResult Run(CancellationToken cancellationToken, Action<string> progress, TimeSpan? available, StageMeasurer measure)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = StageBudget.Limit(BudgetSeconds, available);
        budget.CancelAfter(limit);
        var timer = Stopwatch.StartNew();
        var result = new CpuResult { WorkerCount = Environment.ProcessorCount };
        var data = CreateDataset(InputBytes);
        try
        {
            // A stage is assigned only when all of its passes finish, so a timeout never exposes a partial aggregate.
            result.SingleThread = measure(data, 1, SingleMilliseconds, "Single-thread", budget.Token, progress);
            result.MultiThread = measure(data, result.WorkerCount, MultiMilliseconds, "Multithread", budget.Token, progress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Like RAM and disk: a cancelled stage keeps what finished. The worker still ends the run as Cancelled.
            result.Status = "Cancelled";
            result.QualityNotes.Add("CPU stage was cancelled; the unfinished stage has no score and completed stages are kept.");
        }
        catch (OperationCanceledException)
        {
            // A busy or slow machine is exactly when a partial answer is most useful: keep completed stages, flag the rest.
            result.Status = "TimedOut";
            result.QualityNotes.Add($"CPU stage reached its {limit.TotalSeconds:F1}-second time limit (its {BudgetSeconds}-second safety budget or the time left in the run); the unfinished stage has no score and completed stages are kept.");
        }
        result.OutputVerified = result.SingleThread is not null && result.MultiThread is not null;
        result.DurationSeconds = timer.Elapsed.TotalSeconds;
        foreach (var (name, stage) in new[] { ("Single-thread", result.SingleThread), ("Multithread", result.MultiThread) })
        {
            if (stage is null) continue;
            if (UtilizationNote(name, stage) is { } utilization) result.QualityNotes.Add(utilization);
            if (stage.VariationPercent > 5)
                result.QualityNotes.Add($"{name}: pass variation exceeded the provisional 5% noise threshold; consider retesting.");
            if (stage.OnFastestCoresPercent < 90)
                result.QualityNotes.Add($"{name}: ran on the fastest core type for only {stage.OnFastestCoresPercent:F0}% of samples ({stage.CorePlacement}); the score may understate the machine.");
            if (stage.FirstToLastChangePercent < -10)
                result.QualityNotes.Add($"{name}: throughput fell more than 10% from first to last pass; this does not establish a thermal cause.");
        }
        return result;
    }

    /// <summary>
    /// Flags a stage whose workers got far less CPU time than requested. In saved results, a multithread stage with 32 workers
    /// averaged only about 15.5 busy logical processors while its score halved, which is what running only on slower cores or
    /// under efficiency mode looks like.
    /// </summary>
    internal static string? UtilizationNote(string name, CpuMeasurement stage)
    {
        if (stage.Passes.Count == 0 || stage.RequestedWorkers < 1) return null;
        double busy = stage.Passes.Average(p => p.AverageBusyLogicalProcessors);
        if (busy >= stage.RequestedWorkers * 0.75) return null;
        return $"{name}: {stage.RequestedWorkers} worker(s) averaged only {busy:F1} busy logical processors; the process may have been throttled or restricted " +
            "(efficiency mode, affinity or CPU-set limits, or scheduling onto slower cores), so this score may understate the machine.";
    }

    private static CpuMeasurement MeasureStage(byte[] data, int workers, int milliseconds, string name,
        CancellationToken token, Action<string> progress)
    {
        var stage = new CpuMeasurement { RequestedWorkers = workers, RequestedMillisecondsPerPass = milliseconds };
        for (int pass = 1; pass <= PassCount; pass++)
        {
            token.ThrowIfCancellationRequested();
            progress($"{name} compression: pass {pass} of {PassCount} (warmup, measure, verify)...");
            stage.Passes.Add(Measure(data, workers, milliseconds, pass, token));
        }
        var rates = stage.Passes.Select(p => p.MBps).ToArray();
        stage.MBps = stage.Passes.Sum(p => p.BytesProcessed) / stage.Passes.Sum(p => p.DurationSeconds) / 1_000_000;
        stage.MedianMBps = Stats.Median(rates);
        stage.VariationPercent = Stats.VariationPercent(rates);
        stage.FirstToLastChangePercent = (rates[^1] / rates[0] - 1) * 100;
        stage.CorePlacement = stage.Passes[0].CorePlacement;
        var placed = stage.Passes.Where(p => p.OnFastestCoresPercent.HasValue).ToArray();
        stage.OnFastestCoresPercent = placed.Length == 0 ? null : placed.Average(p => p.OnFastestCoresPercent!.Value);
        return stage;
    }

    private static CpuPass Measure(byte[] data, int workers, int milliseconds, int pass, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var ready = new CountdownEvent(workers);
        using var gate = new ManualResetEventSlim(false);
        using var verifyGate = new ManualResetEventSlim(false);
        using var finished = new CountdownEvent(workers);
        using var process = Process.GetCurrentProcess();
        var failures = new ConcurrentQueue<Exception>();
        var threads = new List<Thread>();
        var bytes = new long[workers];
        var ends = new long[workers];
        long start = 0;
        double cpuBefore = 0, cpuAfter = 0;
        string? placementDescription = null;
        double? onFastest = null;
        try
        {
            for (int worker = 0; worker < workers; worker++)
            {
                int index = worker;
                var thread = new Thread(() =>
                {
                    bool signalledFinished = false;
                    // A lone worker is steered to the fastest cores; a full set of workers already covers every core.
                    using var placement = workers == 1 ? CoreSelection.PreferFastest() : null;
                    try
                    {
                        using var output = new MemoryStream(data.Length + 65536);
                        var warmup = Stopwatch.StartNew();
                        do { Compress(data, output, stop.Token); } while (warmup.ElapsedMilliseconds < WarmupMilliseconds);
                        ready.Signal();
                        gate.Wait(stop.Token);
                        long localBytes = 0;
                        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds)
                        {
                            Compress(data, output, stop.Token);
                            localBytes += data.Length;
                            placement?.Sample();
                        }
                        bytes[index] = localBytes;
                        if (placement is not null) (placementDescription, onFastest) = (placement.Description, placement.OnFastestPercent);
                        ends[index] = Stopwatch.GetTimestamp();
                        finished.Signal();
                        signalledFinished = true;
                        // No verification until every timed worker and CPU-time sampling have finished.
                        verifyGate.Wait(stop.Token);
                        Verify(data, output, stop.Token);
                    }
                    catch (Exception ex) { failures.Enqueue(ex); stop.Cancel(); }
                    finally { if (!signalledFinished) finished.Signal(); }
                }) { IsBackground = true, Name = $"OnDemandPerf CPU {index}" };
                thread.Start();
                threads.Add(thread);
            }
            ready.Wait(stop.Token);
            cpuBefore = process.TotalProcessorTime.TotalSeconds;
            start = Stopwatch.GetTimestamp();
            gate.Set();
            finished.Wait(stop.Token);
            cpuAfter = process.TotalProcessorTime.TotalSeconds;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (start == 0) stop.Cancel();
            gate.Set();
            verifyGate.Set();
            foreach (var thread in threads) thread.Join();
        }
        token.ThrowIfCancellationRequested();
        if (!failures.IsEmpty) throw new InvalidOperationException("Compression worker or output verification failed.", failures.First());
        if (bytes.Any(b => b == 0)) throw new InvalidOperationException("Not every requested worker completed timed work.");
        double seconds = Stopwatch.GetElapsedTime(start, ends.Max()).TotalSeconds;
        long totalBytes = bytes.Sum();
        return new CpuPass(pass, totalBytes, seconds, totalBytes / seconds / 1_000_000)
        {
            ParticipatingWorkers = bytes.Count(b => b > 0), WorkerBytesProcessed = bytes,
            ProcessCpuSeconds = Math.Max(0, cpuAfter - cpuBefore),
            AverageBusyLogicalProcessors = Math.Max(0, cpuAfter - cpuBefore) / seconds,
            OutputVerified = true, CorePlacement = placementDescription, OnFastestCoresPercent = onFastest
        };
    }

    private static void Compress(byte[] data, MemoryStream output, CancellationToken token)
    {
        output.Position = 0;
        output.SetLength(0);
        using var compressor = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        for (int offset = 0; offset < data.Length; offset += 65536)
        {
            token.ThrowIfCancellationRequested();
            compressor.Write(data, offset, Math.Min(65536, data.Length - offset));
        }
    }

    private static void Verify(byte[] data, MemoryStream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        output.Position = 0;
        using var decompressor = new DeflateStream(output, CompressionMode.Decompress, leaveOpen: true);
        var restored = new byte[data.Length];
        decompressor.ReadExactly(restored);
        if (!data.AsSpan().SequenceEqual(restored) || decompressor.ReadByte() != -1)
            throw new InvalidDataException("Compression round-trip verification failed.");
    }

    private static byte[] CreateDataset(int size)
    {
        var data = new byte[size];
        uint state = 0x12345678;
        for (int i = 0; i < 65536; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            data[i] = i % 128 < 96 ? (byte)('A' + i % 26) : (byte)state;
        }
        for (int offset = 65536; offset < size; offset += 65536)
            Buffer.BlockCopy(data, 0, data, offset, Math.Min(65536, size - offset));
        return data;
    }
}

