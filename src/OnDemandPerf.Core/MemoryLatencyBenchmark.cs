using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnDemandPerf.Core;

public static class MemoryLatencyBenchmark
{
    internal const int PassCount = 3;
    internal const int PassMilliseconds = 1250;
    internal const int NodeStride = 16; // ints, i.e. 64 bytes
    public static string Profile { get; } = ProfileHash.Compute("dependent-access-v2", PassCount, PassMilliseconds, NodeStride * 4);

    public static void Run(byte[] buffer, MemoryLatencyResult result, CancellationToken token, Action<string> progress)
    {
        var total = Stopwatch.StartNew();
        // One dependent chain is one thread; steer it to the fastest cores like the other single-worker measurements.
        using var placement = CoreSelection.PreferFastest();
        try
        {
            progress("RAM latency: building and verifying a dependent random-access chain...");
            var links = MemoryMarshal.Cast<byte, int>(buffer.AsSpan());
            const int stride = NodeStride;
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
            for (int pass = 1; pass <= PassCount; pass++)
            {
                progress($"RAM latency: dependent accesses, pass {pass} of {PassCount}...");
                long accesses = 0;
                var watch = Stopwatch.StartNew();
                do
                {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < 4096; i++) index = links[index];
                    accesses += 4096;
                    placement.Sample();
                } while (watch.ElapsedMilliseconds < PassMilliseconds);
                watch.Stop();
                token.ThrowIfCancellationRequested();
                result.Passes.Add(new(pass, accesses, watch.Elapsed.TotalSeconds, watch.Elapsed.TotalNanoseconds / accesses));
            }
            GC.KeepAlive(index);
            (result.CorePlacement, result.OnFastestCoresPercent) = (placement.Description, placement.OnFastestPercent);
            result.NanosecondsPerAccess = result.Passes.Sum(p => p.DurationSeconds) * 1e9 / result.Passes.Sum(p => p.Accesses);
            result.VariationPercent = Stats.VariationPercent(result.Passes.Select(p => p.NanosecondsPerAccess).ToArray());
            result.Status = "Success";
        }
        catch (OperationCanceledException) { result.Status = "CancelledOrTimedOut"; throw; }
        catch { result.Status = "Failed"; throw; }
        finally { result.DurationSeconds = total.Elapsed.TotalSeconds; }
    }
}
