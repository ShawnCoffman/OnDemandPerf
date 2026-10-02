namespace OnDemandPerf.Core;

public sealed class AnalysisResult
{
    public string SchemaVersion { get; init; } = "2.1";
    public string ApplicationVersion { get; init; } = AppInfo.Version;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public double DurationSeconds { get; set; }
    public string Status { get; set; } = "Success";
    public DeviceInfo? Device { get; set; }
    public CpuResult? Cpu { get; set; }
    public MemoryResult? Memory { get; set; }
    public DiskResult? Disk { get; set; }
    public ActivityResult? Activity { get; set; }
    public TestEnvironment? Environment { get; set; }
    public List<string> Warnings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public sealed record DeviceInfo(string ComputerName, string OperatingSystem, string Architecture,
    string CpuName, int LogicalProcessors, string RuntimeVersion)
{
    public string? WindowsEdition { get; set; }
    public string? WindowsRelease { get; set; }
    public string? WindowsBuild { get; set; }
    public int? PhysicalCores { get; set; }
    public uint? SystemLogicalProcessors { get; set; }
    /// <summary>Nominal (base) CPU clock from the registry, in MHz. Not a live or turbo frequency.</summary>
    public uint? CpuBaseMhz { get; set; }
    public HardwareInfo? Hardware { get; set; }
    public InstalledMemory? InstalledMemory { get; set; }
    /// <summary>The drive holding the disk-test directory, i.e. the drive the disk numbers describe.</summary>
    public DriveIdentity? TestDrive { get; set; }
}

public sealed class TestEnvironment
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public ulong? TotalPhysicalBytes { get; set; }
    public ulong? AvailablePhysicalBytes { get; set; }
    public ulong? UsedPhysicalBytes { get; set; }
    public double? RamUtilizationPercent { get; set; }
    public string PowerSource { get; set; } = "Unavailable";
    /// <summary>Whether the worker opted out of Windows efficiency mode (EcoQoS): "OptedOut", "Failed: ..." or "NotAttempted".</summary>
    public string ProcessPowerThrottling { get; set; } = "NotAttempted";
    public bool? BatteryPresent { get; set; }
    public int? BatteryChargePercent { get; set; }
    public string? PowerPlan { get; set; }
    public Guid? PowerPlanId { get; set; }
    public double? CpuUtilizationPercent { get; set; }
    public double? CpuSampleDurationSeconds { get; set; }
    public string CpuSampleStatus { get; set; } = "Unavailable";
    public string CpuSampleMethod { get; init; } = "GetSystemTimes busy-time percentage before benchmark warmup";
}

public sealed class CpuResult
{
    public string BenchmarkVersion { get; init; } = "deflate-v3";
    /// <summary>Fingerprint of durations, pass counts and sizes. Compare only runs with the same version and fingerprint.</summary>
    public string ProfileHash { get; init; } = CpuBenchmark.Profile;
    public string Status { get; set; } = "Success";
    public string Algorithm { get; init; } = "Deflate";
    public string CompressionLevel { get; init; } = "Optimal";
    public string Dataset { get; init; } = CpuBenchmark.DatasetId;
    public int InputBytesPerOperation { get; init; } = CpuBenchmark.InputBytes;
    public int WorkerCount { get; init; }
    public string ThroughputUnit { get; init; } = "MB/s (1 MB = 1,000,000 bytes)";
    public string VariationMethod { get; init; } = Stats.VariationMethod;
    public CpuMeasurement? SingleThread { get; set; }
    public CpuMeasurement? MultiThread { get; set; }
    public double DurationSeconds { get; set; }
    public int WarmupMillisecondsPerPass { get; init; } = CpuBenchmark.WarmupMilliseconds;
    /// <summary>True only when both stages completed and every worker's final output round-tripped.</summary>
    public bool OutputVerified { get; set; }
    public List<string> QualityNotes { get; init; } = [];
}

public sealed class CpuMeasurement
{
    public int RequestedWorkers { get; init; }
    public int RequestedMillisecondsPerPass { get; init; }
    /// <summary>Aggregate: total bytes / total measured time. This is the headline score.</summary>
    public double MBps { get; set; }
    /// <summary>Median of the per-pass rates; less sensitive to one disturbed pass than the aggregate.</summary>
    public double MedianMBps { get; set; }
    public double VariationPercent { get; set; }
    public double FirstToLastChangePercent { get; set; }
    public List<CpuPass> Passes { get; init; } = [];
}

public sealed record CpuPass(int Pass, long BytesProcessed, double DurationSeconds, double MBps)
{
    public int ParticipatingWorkers { get; init; }
    public long[] WorkerBytesProcessed { get; init; } = [];
    public double ProcessCpuSeconds { get; init; }
    public double AverageBusyLogicalProcessors { get; init; }
    public bool OutputVerified { get; init; }
}

