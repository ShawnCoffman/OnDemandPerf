namespace OnDemandPerf.Core;

public sealed class AnalysisResult
{
    public string SchemaVersion { get; init; } = "2.2";
    public string ApplicationVersion { get; init; } = AppInfo.Version;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public double DurationSeconds { get; set; }
    public string Status { get; set; } = "Success";
    /// <summary>
    /// "Valid", or "Invalid" when the PC was in a power-saving mode: the measurements are kept but must not be compared.
    /// Set from <see cref="Findings"/> by <see cref="FindingRules.Apply"/>.
    /// </summary>
    public string Validity { get; set; } = "Valid";
    /// <summary>Plain-language conditions that invalidate the run or commonly make a PC slow, most serious first.</summary>
    public List<Finding> Findings { get; init; } = [];
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
    /// <summary>"Balanced", "HighPerformance", "UltimatePerformance", "PowerSaver" or "Custom" (any other plan).</summary>
    public string? PowerPlanType { get; set; }
    /// <summary>
    /// Settings power mode in effect: "Balanced", "BestPerformance", "BestPowerEfficiency", "BatterySaver" or "Other {guid}".
    /// Windows applies it only on top of the Balanced plan (or a plan derived from it).
    /// </summary>
    public string? PowerMode { get; set; }
    /// <summary>Energy saver (battery saver) state from the system power status.</summary>
    public bool? EnergySaverOn { get; set; }
    /// <summary>The active plan's "Maximum processor state" for the current power source, in percent.</summary>
    public uint? MaxProcessorStatePercent { get; set; }
    /// <summary>The active plan's processor boost mode for the current power source; 0 means boost (turbo) is disabled.</summary>
    public uint? ProcessorBoostMode { get; set; }
    /// <summary>Time since Windows last started, including sleep. Shut down with Fast Startup does not reset it.</summary>
    public double? UptimeDays { get; set; }
    /// <summary>Why a restart is pending (empty when none); null when it could not be checked.</summary>
    public List<string>? PendingRestart { get; set; }
    /// <summary>Committed memory and the commit limit (RAM plus page file).</summary>
    public ulong? CommitBytes { get; set; }
    public ulong? CommitLimitBytes { get; set; }
    public string? SystemDrive { get; set; }
    public ulong? SystemDriveFreeBytes { get; set; }
    public ulong? SystemDriveTotalBytes { get; set; }
    /// <summary>Busiest processes during the pre-test CPU sample, as a percentage of the whole machine.</summary>
    public List<ProcessCpu> TopProcesses { get; init; } = [];
    public double? CpuUtilizationPercent { get; set; }
    public double? CpuSampleDurationSeconds { get; set; }
    public string CpuSampleStatus { get; set; } = "Unavailable";
    public string CpuSampleMethod { get; init; } = "GetSystemTimes busy-time percentage before benchmark warmup";
}

public sealed record ProcessCpu(string Name, int ProcessId, double CpuPercent);

/// <summary>Severity is "Invalid" (the run must not be compared) or "Warning"; Area is Power, CPU, RAM, Disk or System.</summary>
public sealed record Finding(string Severity, string Area, string Message);

public sealed class CpuResult
{
    public string BenchmarkVersion { get; init; } = "deflate-v4";
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
    /// <summary>Single-thread only: how the worker was steered (see <see cref="CoreSelection"/>).</summary>
    public string? CorePlacement { get; set; }
    /// <summary>Single-thread only: share of samples on the fastest core type; null on CPUs with one core type.</summary>
    public double? OnFastestCoresPercent { get; set; }
    public List<CpuPass> Passes { get; init; } = [];
}

public sealed record CpuPass(int Pass, long BytesProcessed, double DurationSeconds, double MBps)
{
    public int ParticipatingWorkers { get; init; }
    public long[] WorkerBytesProcessed { get; init; } = [];
    public double ProcessCpuSeconds { get; init; }
    public double AverageBusyLogicalProcessors { get; init; }
    public bool OutputVerified { get; init; }
    public string? CorePlacement { get; init; }
    public double? OnFastestCoresPercent { get; init; }
}

