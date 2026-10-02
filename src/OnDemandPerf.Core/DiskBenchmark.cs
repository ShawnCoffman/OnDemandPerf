using System.Diagnostics;

namespace OnDemandPerf.Core;

public static class DiskBenchmark
{
    internal const int BlockBytes = 1024 * 1024;
    internal const int SeqWriteMaxOperations = 1024;
    internal const int SeqWriteMilliseconds = 2000;
    internal const int SeqReadMilliseconds = 6000;
    internal const int RandomBlockBytes = 4096;
    internal const int RandomWriteMaxOperations = 16384;
    internal const int RandomWriteMilliseconds = 2000;
    internal const int ObservationWindowMilliseconds = 16000;
    internal const int BudgetSeconds = 22;
    internal const uint RandomWriteSeed = 0x12345678;
    /// <summary>Deliberately different from the write seed: reusing it made the first 16,384 reads hit just-written blocks.</summary>
    internal const uint RandomReadSeed = 0x9E3779B9;
    public static string Profile { get; } = ProfileHash.Compute("disk-qd1-v3", BlockBytes, SeqWriteMaxOperations, SeqWriteMilliseconds,
        SeqReadMilliseconds, RandomBlockBytes, RandomWriteMaxOperations, RandomWriteMilliseconds, ObservationWindowMilliseconds,
        RandomWriteSeed, RandomReadSeed);
    public static string DefaultDirectory => Path.Combine(ResultStore.DefaultDirectory, "TestData");
    public static DiskResult Run(string directory, CancellationToken token, Action<string> progress)
    {
        var result = new DiskResult { TargetDirectory = Path.GetFullPath(directory) };
        var total = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(BudgetSeconds));
        string? path = null;
        bool owned = false;
        try
        {
            DriveInfo drive;
            try { drive = new DriveInfo(Path.GetPathRoot(result.TargetDirectory)!); }
            catch (ArgumentException) { throw new NotSupportedException("Only local fixed volumes are supported (network and UNC paths are not)."); }
            result.Volume = drive.Name; result.FileSystem = drive.DriveFormat; result.FreeBytesBefore = drive.AvailableFreeSpace;
            if (drive.DriveType != DriveType.Fixed) throw new NotSupportedException("Only local fixed volumes are supported.");
            const int block = BlockBytes;
            if (result.FreeBytesBefore < 2L * 1024 * block)
            { result.Status = "Skipped"; result.Notes.Add("At least 2 GiB free space is required; disk test skipped."); return result; }
            // Results may be redirected, but default testing must stay on the Windows volume.
            if (Path.GetFullPath(directory).Equals(Path.GetFullPath(DefaultDirectory), StringComparison.OrdinalIgnoreCase) &&
                !drive.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Default test folder is not on the Windows volume; specify /diskpath explicitly.");
            Directory.CreateDirectory(result.TargetDirectory);
            path = Path.Combine(result.TargetDirectory, "odp-" + Guid.NewGuid().ToString("N") + ".tmp");
            var pattern = new byte[block];
            uint state = 0x87654321;
            for (int i = 0; i < pattern.Length; i++) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; pattern[i] = (byte)state; }
            using (var file = new DirectFile(path, pattern))
            {
                owned = true;
                result.LogicalSectorBytes = file.LogicalSectorBytes; result.PhysicalSectorBytes = file.PhysicalSectorBytes;
                result.Notes.Add("Queue depth 1, write-through: measures application I/O, not vendor peak bandwidth. Hardware caches and repeated-block deduplication can still affect results.");
                result.Notes.Add("Writes capped at 1 GiB sequential plus 64 MiB random; unused write time goes to random reads; random offsets use fixed seeds, with reads on a different sequence from writes. Individual OS I/O requests may outlast cooperative deadlines.");
                result.Notes.Add("Sequential verification samples first/middle/last blocks; random read verifies its final block; random write verifies every completed write location. This is a performance screen, not a surface or endurance test.");
                progress("Disk sequential write: creating at most 1 GiB of test data...");
                var write = Measure("SequentialWrite", block, SeqWriteMaxOperations, SeqWriteMilliseconds, i => i * block, true, file, budget.Token,
                    bytes => { result.TotalBytesWritten += bytes; result.FileBytes += bytes; });
                result.FileBytes = write.BytesProcessed;
                VerifySamples(file, pattern, result.FileBytes, budget.Token);
                result.Measurements.Add(write with { Verified = true });
                if (result.FileBytes < 32L * block) throw new IOException("Too little data created for a meaningful storage sample.");
                progress("Disk sequential read: bypassing Windows file-data cache...");
                var read = Measure("SequentialRead", block, 1_000_000, SeqReadMilliseconds, i => i * block % result.FileBytes, false, file, budget.Token,
                    firstPassOperations: result.FileBytes / block);
                if (read.FilePasses is > 1.5)
                    result.Notes.Add($"SequentialRead revisited the {result.FileBytes / 1048576} MiB test file {read.FilePasses:F1} times in its window; later reads can be served from device or controller cache. FirstPassMBps is the first complete read and is the more conservative figure.");
                VerifySamples(file, pattern, result.FileBytes, budget.Token);
                result.Measurements.Add(read with { Verified = true });
                var originalPattern = (byte[])pattern.Clone();
                uint randomState = RandomWriteSeed;
                long lastRandomOffset = 0;
                long RandomOffset(long _) { randomState ^= randomState << 13; randomState ^= randomState >> 17; randomState ^= randomState << 5; lastRandomOffset = (randomState % (result.FileBytes / 4096)) * 4096; return lastRandomOffset; }
                progress("Disk random write: 4 KiB, queue depth 1, at most 64 MiB...");
                // Change the initial contents rather than repeatedly writing the existing bytes.
                for (int i = 0; i < pattern.Length; i++) pattern[i] ^= 0xFF;
                file.SetPattern(pattern);
                randomState = RandomWriteSeed;
                var randomWrite = Measure("RandomWrite", RandomBlockBytes, RandomWriteMaxOperations, RandomWriteMilliseconds, RandomOffset, true, file, budget.Token,
                    bytes => result.TotalBytesWritten += bytes);
                var modifiedOffsets = new HashSet<long>();
                // Replay every modified block outside timing and verify the persisted data.
                randomState = RandomWriteSeed;
                for (long i = 0; i < randomWrite.Operations; i++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    long offset = RandomOffset(i);
                    modifiedOffsets.Add(offset);
                    file.Transfer(offset, 4096, false);
                    if (!file.Matches(pattern, (int)(offset % block), 4096)) throw new InvalidDataException("Random write verification failed.");
                }
                result.Measurements.Add(randomWrite with { Verified = true });
                // Fill the remainder of the 16-second disk observation window with useful read samples.
                // The 22-second safety deadline still bounds unusually slow I/O and verification.
                int remainingReadMs = Math.Max(1000, ObservationWindowMilliseconds - (int)total.ElapsedMilliseconds);
                progress($"Disk random read: 4 KiB, queue depth 1, {remainingReadMs / 1000d:F1}-second observation...");
                randomState = RandomReadSeed;
                var randomRead = Measure("RandomRead", RandomBlockBytes, 1_000_000, remainingReadMs, RandomOffset, false, file, budget.Token);
                byte[] expected = modifiedOffsets.Contains(lastRandomOffset) ? pattern : originalPattern;
                if (!file.Matches(expected, (int)(lastRandomOffset % block), 4096)) throw new InvalidDataException("Random read sample verification failed.");
                result.Measurements.Add(randomRead with { Verified = true });
                foreach (var measurement in result.Measurements.Where(m => m.ShortBurst))
                    result.Notes.Add($"{measurement.Operation}: short burst sample ({measurement.DurationSeconds:F3}s) ({measurement.StopReason}); do not interpret as sustained performance.");
                if (result.Measurements.Any(m => m.Status != "Success")) result.Status = "Partial";
            }
        }
        catch (NotSupportedException ex) { result.Status = "Unsupported"; result.Notes.Add(ex.Message); }
        catch (OperationCanceledException) { result.Status = token.IsCancellationRequested ? "Cancelled" : "TimedOut"; result.Notes.Add("Disk stage cancelled or exceeded its 22-second budget; completed measurements retained."); }
        catch (Exception ex) { result.Status = "Failed"; result.Notes.Add(ex.Message); }
        finally
        {
            // Delete-on-close is the primary cleanup, including termination. Never delete a pre-existing file.
            try { if (owned && path is not null && File.Exists(path)) File.Delete(path); result.CleanupSucceeded = path is null || !File.Exists(path); }
            catch (Exception ex) { result.CleanupSucceeded = false; result.Status = "Failed"; result.Notes.Add("Cleanup: " + ex.Message); }
            result.DurationSeconds = total.Elapsed.TotalSeconds;
        }
        return result;
    }

    private static DiskMeasurement Measure(string operation, int block, int maxOperations, int milliseconds,
        Func<long, long> offset, bool write, DirectFile file, CancellationToken token, Action<int>? completed = null,
        long firstPassOperations = 0)
    {
        var latencies = new List<double>(Math.Min(maxOperations, 32768));
        var timer = Stopwatch.StartNew();
        double firstPassSeconds = 0;
        for (int i = 0; i < maxOperations && timer.ElapsedMilliseconds < milliseconds; i++)
        {
            token.ThrowIfCancellationRequested();
            long location = offset(i);
            long start = Stopwatch.GetTimestamp();
            file.Transfer(location, block, write, write && block == 4096 ? (int)(location % (1024 * 1024)) : 0);
            latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            completed?.Invoke(block);
            if (firstPassOperations > 0 && firstPassSeconds == 0 && latencies.Count == firstPassOperations)
                firstPassSeconds = timer.Elapsed.TotalSeconds;
        }
        timer.Stop();
        if (latencies.Count == 0) throw new IOException("No disk operations completed.");
        long bytes = (long)latencies.Count * block;
        double mean = latencies.Average();
        latencies.Sort();
        return new(operation, latencies.Count < 8 ? "InsufficientSample" : "Success", block, latencies.Count, bytes,
            timer.Elapsed.TotalSeconds, bytes / timer.Elapsed.TotalSeconds / 1_000_000,
            latencies.Count / timer.Elapsed.TotalSeconds, mean, latencies[(int)Math.Ceiling(latencies.Count * .95) - 1],
            false, latencies.Count == maxOperations ? (write ? "ByteCap" : "SampleCap") : "TimeBudget")
        {
            FirstPassMBps = firstPassSeconds > 0 ? firstPassOperations * (double)block / firstPassSeconds / 1_000_000 : null,
            FilePasses = firstPassOperations > 0 ? (double)latencies.Count / firstPassOperations : null
        };
    }
    private static void VerifySamples(DirectFile file, byte[] pattern, long length, CancellationToken token)
    {
        foreach (long offset in new[] { 0L, Math.Max(0, length / pattern.Length / 2 * pattern.Length), length - pattern.Length }.Distinct())
        {
            token.ThrowIfCancellationRequested();
            file.Transfer(offset, pattern.Length, false);
            if (!file.Matches(pattern, 0, pattern.Length)) throw new InvalidDataException("Sequential data sample verification failed.");
        }
    }
}

