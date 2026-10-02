using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

public static class MemoryBenchmark
{
    public static MemoryResult Run(CancellationToken token, Action<string> progress)
    {
        var result = new MemoryResult();
        var timer = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            var status = new WindowsNative.MemoryStatus { Length = (uint)Marshal.SizeOf<WindowsNative.MemoryStatus>() };
            if (!WindowsNative.GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("Available RAM unavailable.");
            // Two buffers together use at most one eighth of currently available RAM, capped at 512 MiB.
            int size = 256 * 1024 * 1024;
            while ((ulong)size * 16 > status.AvailablePhysical && size >= 32 * 1024 * 1024) size /= 2;
            if (size < 32 * 1024 * 1024) { result.Status = "Skipped"; result.Notes.Add("Insufficient available RAM for a low-pressure test."); return result; }
            byte[] source, destination;
            while (true)
            {
                try { source = new byte[size]; destination = new byte[size]; break; }
                catch (OutOfMemoryException) when (size > 32 * 1024 * 1024) { size /= 2; }
            }
            result.BufferBytes = size;
            result.Notes.Add("Compare matching buffer sizes; reduced buffers and large CPU caches can influence throughput.");
            source.AsSpan().Fill(0x5A);
            destination.AsSpan().Fill(0x5A); // Commit pages outside measurement.
            foreach (string operation in new[] { "Copy", "Read", "Write" })
            {
                progress($"RAM {operation.ToLowerInvariant()}: warmup and three passes...");
                Perform(operation, source, destination, budget.Token);
                for (int pass = 1; pass <= 3; pass++)
                {
                    long bytes = 0;
                    var watch = Stopwatch.StartNew();
                    do { Perform(operation, source, destination, budget.Token); bytes += size; }
                    while (watch.ElapsedMilliseconds < 900);
                    watch.Stop();
                    budget.Token.ThrowIfCancellationRequested();
                    bool verified = operation == "Read" || (operation == "Write"
                        ? destination.AsSpan().IndexOfAnyExcept((byte)0xA5) < 0 : destination.AsSpan().SequenceEqual(source));
                    if (!verified) throw new InvalidDataException("RAM output verification failed.");
                    result.Passes.Add(new(operation, pass, bytes, watch.Elapsed.TotalSeconds,
                        bytes / watch.Elapsed.TotalSeconds / 1_000_000_000d, true));
                }
            }
            double Rate(string operation) => result.Passes.Where(p => p.Operation == operation).Sum(p => p.BytesProcessed)
                / result.Passes.Where(p => p.Operation == operation).Sum(p => p.DurationSeconds) / 1_000_000_000d;
            result.CopyGBps = Rate("Copy"); result.ReadGBps = Rate("Read"); result.WriteGBps = Rate("Write");
            foreach (var operation in new[] { "Copy", "Read", "Write" })
            {
                var rates = result.Passes.Where(p => p.Operation == operation).Select(p => p.GBps).ToArray();
                double mean = rates.Average();
                double variation = Math.Sqrt(rates.Average(r => Math.Pow(r - mean, 2))) / mean * 100;
                result.VariationPercent[operation] = variation;
                if (variation > 5) result.Notes.Add($"{operation}: pass variation {variation:F1}% exceeded the provisional 5% noise threshold.");
            }
            result.Latency = new MemoryLatencyResult { WorkingSetBytes = size };
            MemoryLatencyBenchmark.Run(source, result.Latency, budget.Token, progress);
        }
        catch (OperationCanceledException) { result.Status = token.IsCancellationRequested ? "Cancelled" : "TimedOut"; result.Notes.Add("RAM deadline or cancellation reached; only completed measurements are reported."); }
        catch (Exception ex) { result.Status = "Failed"; result.Notes.Add(ex.Message); }
        finally { result.DurationSeconds = timer.Elapsed.TotalSeconds; }
        return result;
    }

    private static void Perform(string operation, byte[] source, byte[] destination, CancellationToken token)
    {
        const int chunk = 1024 * 1024;
        for (int offset = 0; offset < source.Length; offset += chunk)
        {
            token.ThrowIfCancellationRequested();
            var input = source.AsSpan(offset, Math.Min(chunk, source.Length - offset));
            var output = destination.AsSpan(offset, input.Length);
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
}

