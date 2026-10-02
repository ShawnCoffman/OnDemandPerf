namespace OnDemandPerf.Core;

public sealed class MemoryResult
{
    public string BenchmarkVersion { get; init; } = "memory-v3";
    public string ProfileHash { get; init; } = MemoryBenchmark.Profile;
    public string Status { get; set; } = "Success";
    public int BufferBytes { get; set; }
    public int BufferCount { get; init; } = 2;
    public string Method { get; init; } = "Single worker; vectorized read checksum, Span.Fill write, Span.CopyTo copy; logical payload GB/s (decimal), not total bus traffic";
    public List<MemoryPass> Passes { get; init; } = [];
    public MemoryLatencyResult? Latency { get; set; }
    public Dictionary<string, double> VariationPercent { get; init; } = [];
    public double? CopyGBps { get; set; }
    public double? ReadGBps { get; set; }
    public double? WriteGBps { get; set; }
    /// <summary>Workers used by the multithread pass (null when it did not run). Passes named ReadMT/CopyMT.</summary>
    public int? MultiThreadWorkers { get; set; }
    public double? MultiThreadReadGBps { get; set; }
    public double? MultiThreadCopyGBps { get; set; }
    public string VariationMethod { get; init; } = Stats.VariationMethod;
    public double DurationSeconds { get; set; }
    public List<string> Notes { get; init; } = [];
}
public sealed record MemoryPass(string Operation, int Pass, long BytesProcessed, double DurationSeconds,
    double GBps, bool Verified);

public sealed class DiskResult
{
    public string BenchmarkVersion { get; init; } = "disk-qd1-v3";
    public string ProfileHash { get; init; } = DiskBenchmark.Profile;
    public string Status { get; set; } = "Success";
    public string? TargetDirectory { get; set; }
    public string? Volume { get; set; }
    public string? FileSystem { get; set; }
    public long FreeBytesBefore { get; set; }
    public long FileBytes { get; set; }
    public long TotalBytesWritten { get; set; }
    public uint LogicalSectorBytes { get; set; }
    public uint PhysicalSectorBytes { get; set; }
    public int QueueDepth { get; init; } = 1;
    public string CachePolicy { get; init; } = "FILE_FLAG_NO_BUFFERING + WRITE_THROUGH; device caches may remain; no buffered fallback";
    public string Dataset { get; init; } = "Fixed pseudo-random 1 MiB block repeated; filesystem/device deduplication can affect results";
    public List<DiskMeasurement> Measurements { get; init; } = [];
    public bool CleanupSucceeded { get; set; }
    public double DurationSeconds { get; set; }
    public List<string> Notes { get; init; } = [];
}
public sealed record DiskMeasurement(string Operation, string Status, int BlockBytes, long Operations,
    long BytesProcessed, double DurationSeconds, double MBps, double Iops, double MeanLatencyMs,
    double P95LatencyMs, bool Verified, string StopReason)
{
    public bool ShortBurst => DurationSeconds < 1;
    /// <summary>Sequential read only: how many times the test file was read in full. Above 1, later reads revisit the same data.</summary>
    public double? FilePasses { get; init; }
    /// <summary>Sequential read only: MB/s over the first complete read of the file, before any data is revisited.</summary>
    public double? FirstPassMBps { get; init; }
}

public sealed class MemoryLatencyResult
{
    public string BenchmarkVersion { get; init; } = "dependent-access-v1";
    public string ProfileHash { get; init; } = MemoryLatencyBenchmark.Profile;
    public string Status { get; set; } = "Incomplete";
    public string Method { get; init; } = "Single dependent index chain, seeded Sattolo permutation, 64-byte node spacing; managed bounds/loop overhead included; cache and scheduling affect results; not DRAM-only latency";
    public int WorkingSetBytes { get; set; }
    public int NodeSpacingBytes { get; init; } = 64;
    public int Seed { get; init; } = 314159;
    public bool CycleVerified { get; set; }
    public double? NanosecondsPerAccess { get; set; }
    public double? VariationPercent { get; set; }
    public double DurationSeconds { get; set; }
    public List<MemoryLatencyPass> Passes { get; init; } = [];
}
public sealed record MemoryLatencyPass(int Pass, long Accesses, double DurationSeconds, double NanosecondsPerAccess);

