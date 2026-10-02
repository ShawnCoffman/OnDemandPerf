using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;

namespace OnDemandPerf.Core;

public static class CpuBenchmark
{
    public static CpuResult Run(CancellationToken cancellationToken, Action<string> progress)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var timer = Stopwatch.StartNew();
        var result = new CpuResult { WorkerCount = Environment.ProcessorCount };
        var data = CreateDataset(result.InputBytesPerOperation);
        try
        {
            result.SingleThread = MeasureStage(data, 1, 1800, "Single-thread", budget.Token, progress);
            result.MultiThread = MeasureStage(data, result.WorkerCount, 3000, "Multithread", budget.Token, progress);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("CPU stage exceeded its 20-second safety budget; no complete score was produced.");
        }
        result.CompressionMBps = result.MultiThread.MBps;
        result.Passes.AddRange(result.MultiThread.Passes);
        result.OutputVerified = true;
        result.DurationSeconds = timer.Elapsed.TotalSeconds;
        foreach (var (name, stage) in new[] { ("Single-thread", result.SingleThread), ("Multithread", result.MultiThread) })
        {
            if (stage.VariationPercent > 5)
                result.QualityNotes.Add($"{name}: pass variation exceeded the provisional 5% noise threshold; consider retesting.");
            if (stage.FirstToLastChangePercent < -10)
                result.QualityNotes.Add($"{name}: throughput fell more than 10% from first to last pass; this does not establish a thermal cause.");
        }
        return result;
    }

    private static CpuMeasurement MeasureStage(byte[] data, int workers, int milliseconds, string name,
        CancellationToken token, Action<string> progress)
    {
        var stage = new CpuMeasurement { RequestedWorkers = workers, RequestedMillisecondsPerPass = milliseconds };
        for (int pass = 1; pass <= 3; pass++)
        {
            token.ThrowIfCancellationRequested();
            progress($"{name} compression: pass {pass} of 3 (warmup, measure, verify)...");
            stage.Passes.Add(Measure(data, workers, milliseconds, pass, token));
        }
        stage.MBps = stage.Passes.Sum(p => p.BytesProcessed) / stage.Passes.Sum(p => p.DurationSeconds) / 1_000_000;
        double mean = stage.Passes.Average(p => p.MBps);
        stage.VariationPercent = Math.Sqrt(stage.Passes.Average(p => Math.Pow(p.MBps - mean, 2))) / mean * 100;
        stage.FirstToLastChangePercent = (stage.Passes[^1].MBps / stage.Passes[0].MBps - 1) * 100;
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
        try
        {
            for (int worker = 0; worker < workers; worker++)
            {
                int index = worker;
                var thread = new Thread(() =>
                {
                    bool signalledFinished = false;
                    try
                    {
                        using var output = new MemoryStream(data.Length + 65536);
                        var warmup = Stopwatch.StartNew();
                        do { Compress(data, output, stop.Token); } while (warmup.ElapsedMilliseconds < 250);
                        ready.Signal();
                        gate.Wait(stop.Token);
                        long localBytes = 0;
                        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds)
                        {
                            Compress(data, output, stop.Token);
                            localBytes += data.Length;
                        }
                        bytes[index] = localBytes;
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
            OutputVerified = true
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

