using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

public static class MemoryLatencyBenchmark
{
    public static void Run(byte[] buffer, MemoryLatencyResult result, CancellationToken token, Action<string> progress)
    {
        var total = Stopwatch.StartNew();
        try
        {
            progress("RAM latency: building and verifying a dependent random-access chain...");
            var links = MemoryMarshal.Cast<byte, int>(buffer.AsSpan());
            const int stride = 16;
            int nodes = links.Length / stride;
            if (nodes < 2) throw new ArgumentException("Working set too small.");
            for (int i = 0; i < nodes; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                links[i * stride] = i * stride;
            }
            var random = new Random(result.Seed);
            // Sattolo's algorithm makes exactly one cycle over all nodes, not independent short cycles.
            for (int i = nodes - 1; i > 0; i--)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                int j = random.Next(i);
                (links[i * stride], links[j * stride]) = (links[j * stride], links[i * stride]);
            }
            int index = 0;
            for (int i = 0; i < nodes; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                index = links[index];
                if (index == 0 && i != nodes - 1) throw new InvalidDataException("Latency chain closed early.");
            }
            if (index != 0) throw new InvalidDataException("Latency chain did not close.");
            result.CycleVerified = true;
            // Full-chain verification also warms the traversal before the timed passes.
            for (int pass = 1; pass <= 3; pass++)
            {
                progress($"RAM latency: dependent accesses, pass {pass} of 3...");
                long accesses = 0;
                var watch = Stopwatch.StartNew();
                do
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < 4096; i++) index = links[index];
                    accesses += 4096;
                } while (watch.ElapsedMilliseconds < 1250);
                watch.Stop();
                token.ThrowIfCancellationRequested();
                result.Passes.Add(new(pass, accesses, watch.Elapsed.TotalSeconds, watch.Elapsed.TotalNanoseconds / accesses));
            }
            GC.KeepAlive(index);
            result.NanosecondsPerAccess = result.Passes.Sum(p => p.DurationSeconds) * 1e9 / result.Passes.Sum(p => p.Accesses);
            double mean = result.Passes.Average(p => p.NanosecondsPerAccess);
            result.VariationPercent = Math.Sqrt(result.Passes.Average(p => Math.Pow(p.NanosecondsPerAccess - mean, 2))) / mean * 100;
            result.Status = "Success";
        }
        catch (OperationCanceledException) { result.Status = "CancelledOrTimedOut"; throw; }
        catch { result.Status = "Failed"; throw; }
        finally { result.DurationSeconds = total.Elapsed.TotalSeconds; }
    }
}
