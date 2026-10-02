namespace OnDemandPerf.Core;

public sealed class AnalysisResult
{
    public string SchemaVersion { get; init; } = "1.4";
    public string ApplicationVersion { get; init; } = "0.5.0";
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
}

public sealed class TestEnvironment
{
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public ulong? TotalPhysicalBytes { get; set; }
    public ulong? AvailablePhysicalBytes { get; set; }
    public ulong? UsedPhysicalBytes { get; set; }
    public double? RamUtilizationPercent { get; set; }
    public string PowerSource { get; set; } = "Unavailable";
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
    public string Status { get; set; } = "Success";
    public string Algorithm { get; init; } = "Deflate";
    public string CompressionLevel { get; init; } = "Optimal";
    public string Dataset { get; init; } = "xorshift32-seed-12345678-repeated-64KiB-v1";
    public int InputBytesPerOperation { get; init; } = 1024 * 1024;
    public int WorkerCount { get; init; }
    public int RequestedMillisecondsPerPass { get; init; } = 3000;
    public string ThroughputUnit { get; init; } = "MB/s (1 MB = 1,000,000 bytes)";
    public double CompressionMBps { get; set; }
    public List<CpuPass> Passes { get; init; } = [];
    public CpuMeasurement? SingleThread { get; set; }
    public CpuMeasurement? MultiThread { get; set; }
    public double DurationSeconds { get; set; }
    public int WarmupMillisecondsPerPass { get; init; } = 250;
    public bool OutputVerified { get; set; }
    public List<string> QualityNotes { get; init; } = [];
}

public sealed class CpuMeasurement
{
    public int RequestedWorkers { get; init; }
    public int RequestedMillisecondsPerPass { get; init; }
    public double MBps { get; set; }
    public double VariationPercent { get; set; }
    public double FirstToLastChangePercent { get; set; }
    public List<CpuPass> Passes { get; } = [];
}

public sealed record CpuPass(int Pass, long BytesProcessed, double DurationSeconds, double MBps)
{
    public int ParticipatingWorkers { get; init; }
    public long[] WorkerBytesProcessed { get; init; } = [];
    public double ProcessCpuSeconds { get; init; }
    public double AverageBusyLogicalProcessors { get; init; }
    public bool OutputVerified { get; init; }
}

