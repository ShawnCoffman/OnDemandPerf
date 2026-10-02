using System.Globalization;
using System.Text.Json;
using OnDemandPerf.Core;

// Pure-logic tests only: nothing here runs a benchmark or touches hardware, so they are fast and deterministic.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
int passed = 0, failed = 0;

void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Near(double actual, double expected, double tolerance, string what) =>
    Check(Math.Abs(actual - expected) <= tolerance, $"{what}: expected {expected}, got {actual}");

AnalysisResult Sample()
{
    var single = new CpuMeasurement { RequestedWorkers = 1, RequestedMillisecondsPerPass = 1800, MBps = 96.5, MedianMBps = 95, VariationPercent = 2.5 };
    var multi = new CpuMeasurement { RequestedWorkers = 8, RequestedMillisecondsPerPass = 3000, MBps = 1588.6, MedianMBps = 1590, VariationPercent = 1.5 };
    for (int pass = 1; pass <= 3; pass++)
    {
        single.Passes.Add(new CpuPass(pass, 100_000_000, 1.8, 55.5) { ParticipatingWorkers = 1, WorkerBytesProcessed = [100_000_000], OutputVerified = true });
        multi.Passes.Add(new CpuPass(pass, 4_000_000_000, 3.0, 1333.3) { ParticipatingWorkers = 8, WorkerBytesProcessed = new long[8], OutputVerified = true });
    }
    var memory = new MemoryResult
    {
        BufferBytes = 256 * 1024 * 1024, CopyGBps = 5.9, ReadGBps = 11.2, WriteGBps = 10.4,
        MultiThreadWorkers = 8, MultiThreadReadGBps = 60.1, MultiThreadCopyGBps = 40.2,
        Latency = new MemoryLatencyResult { Status = "Success", NanosecondsPerAccess = 100.2, CycleVerified = true }
    };
    memory.Passes.Add(new MemoryPass("Copy", 1, 1_000_000_000, 1.0, 1.0, true));
    memory.VariationPercent["Copy"] = 1.2;
    var disk = new DiskResult { TargetDirectory = @"C:\Temp", Volume = @"C:\", FileSystem = "NTFS", FileBytes = 1 << 30, CleanupSucceeded = true };
    disk.Measurements.Add(new DiskMeasurement("SequentialRead", "Success", 1048576, 15405, 15405L * 1048576, 6.0, 2692.1, 2567, 0.3, 0.4, true, "TimeBudget")
        { FirstPassMBps = 1800.5, FilePasses = 15.0 });
    disk.Measurements.Add(new DiskMeasurement("RandomRead", "Success", 4096, 119380, 119380L * 4096, 8.2, 59.5, 14516, 0.069, 0.082, true, "TimeBudget"));
    disk.Measurements.Add(new DiskMeasurement("RandomWrite", "Success", 4096, 16384, 16384L * 4096, 0.21, 320, 78135, 0.013, 0.015, true, "ByteCap"));
    return new AnalysisResult
    {
        Device = new DeviceInfo("PC1", "Windows 11 Pro", "X64", "Test CPU", 8, ".NET 10") { PhysicalCores = 4, WindowsBuild = "26200.1" },
        Environment = new TestEnvironment { PowerSource = "AC", PowerPlan = "High performance" },
        Cpu = new CpuResult { WorkerCount = 8, SingleThread = single, MultiThread = multi, OutputVerified = true },
        Memory = memory, Disk = disk
    };
}

// ---- statistics ----
Test("Stats.SampleStdDev uses n-1", () =>
{
    double[] values = [2, 4, 4, 4, 5, 5, 7, 9];
    Near(Stats.SampleStdDev(values), Math.Sqrt(32.0 / 7), 1e-9, "sample SD");
    Near(Stats.VariationPercent(values), Math.Sqrt(32.0 / 7) / 5 * 100, 1e-9, "variation");
});
Test("Stats handles degenerate input", () =>
{
    Check(Stats.SampleStdDev([3.0]) == 0, "single value has no spread");
    Check(Stats.VariationPercent([]) == 0, "empty variation");
    Check(Stats.VariationPercent([0.0, 0.0]) == 0, "zero mean must not divide by zero");
});
Test("Stats.Median odd, even and unsorted", () =>
{
    Check(Stats.Median([3.0, 1.0, 2.0]) == 2, "odd");
    Check(Stats.Median([4.0, 1.0, 3.0, 2.0]) == 2.5, "even");
    Check(Stats.Median([]) == 0, "empty");
});

// ---- profiles and versioning ----
Test("ProfileHash is stable, short and sensitive to every parameter", () =>
{
    string a = ProfileHash.Compute("deflate-v3", 1800, 3000);
    Check(a == ProfileHash.Compute("deflate-v3", 1800, 3000), "stable");
    Check(a.Length == 8 && a.All(Uri.IsHexDigit), "8 hex characters");
    Check(a != ProfileHash.Compute("deflate-v3", 1800, 3001), "a changed duration must change the fingerprint");
});
Test("Benchmark profiles are present on results", () =>
{
    Check(new CpuResult().ProfileHash == CpuBenchmark.Profile, "cpu");
    Check(new MemoryResult().ProfileHash == MemoryBenchmark.Profile, "memory");
    Check(new DiskResult().ProfileHash == DiskBenchmark.Profile, "disk");
});
Test("AppInfo.Version looks like a version and the result carries it", () =>
{
    Check(System.Text.RegularExpressions.Regex.IsMatch(AppInfo.Version, @"^\d+\.\d+\.\d+"), AppInfo.Version);
    Check(new AnalysisResult().ApplicationVersion == AppInfo.Version, "result version");
});
Test("Disk random reads use a different seed from random writes (regression: read/write overlap)", () =>
    Check(DiskBenchmark.RandomReadSeed != DiskBenchmark.RandomWriteSeed && DiskBenchmark.RandomReadSeed != 0, "seeds"));

Test("CPU utilization note flags a stage that got far less CPU than requested (the saved v0.5.0 pattern)", () =>
{
    CpuMeasurement Stage(int workers, double busy)
    {
        var stage = new CpuMeasurement { RequestedWorkers = workers };
        for (int pass = 1; pass <= 3; pass++) stage.Passes.Add(new CpuPass(pass, 1, 1, 1) { AverageBusyLogicalProcessors = busy });
        return stage;
    }
    Check(CpuBenchmark.UtilizationNote("Multithread", Stage(32, 15.5)) is not null, "15.5 of 32 should be flagged");
    Check(CpuBenchmark.UtilizationNote("Multithread", Stage(32, 30.0)) is null, "30 of 32 is healthy");
    Check(CpuBenchmark.UtilizationNote("Single-thread", Stage(1, 0.98)) is null, "one busy worker is healthy");
    Check(CpuBenchmark.UtilizationNote("Single-thread", new CpuMeasurement { RequestedWorkers = 1 }) is null, "no passes, no note");
});

// ---- argument parsing ----
Test("WorkerArguments parses switches case-insensitively and keeps path case", () =>
{
    var a = WorkerArguments.Parse(["/RUN", "/Output", @"C:\Some\Dir", "/DISKPATH", @"D:\Test", "/Compact"]);
    Check(a.Error is null && a.Mode == "/run" && a.Compact, "mode/compact");
    Check(a.OutputDirectory == @"C:\Some\Dir" && a.DiskDirectory == @"D:\Test", "paths keep their case");
});
Test("WorkerArguments rejects bad input", () =>
{
    Check(WorkerArguments.Parse(["/bogus"]).Error is not null, "unknown switch");
    Check(WorkerArguments.Parse(["/cpu", "/disk"]).Error is not null, "two modes");
    Check(WorkerArguments.Parse(["/cpu", "/output"]).Error is not null, "missing value");
    Check(WorkerArguments.Parse(["/compact"]).Error is not null, "no mode");
    Check(WorkerArguments.Parse([]).Help, "no arguments shows help");
    Check(WorkerArguments.Parse(["/HELP"]).Help, "help is case-insensitive");
});

// ---- typed JSON round trip (the tray now depends on this) ----
Test("Result survives a JSON round trip with records and init-only lists", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "odp-test-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var original = Sample();
        original.Warnings.Add("something optional failed");
        File.WriteAllText(path, JsonSerializer.Serialize(original, ResultStore.JsonOptions));
        var loaded = ResultStore.Load(path);
        Check(loaded.RunId == original.RunId && loaded.SchemaVersion == "2.0", "identity");
        Check(loaded.Device?.ComputerName == "PC1" && loaded.Device.PhysicalCores == 4, "device record");
        Near(loaded.Cpu!.MultiThread!.MBps, 1588.6, 1e-9, "multithread MB/s");
        Check(loaded.Cpu.MultiThread.Passes.Count == 3 && loaded.Cpu.MultiThread.Passes[0].WorkerBytesProcessed.Length == 8, "cpu passes");
        Check(loaded.Memory!.MultiThreadWorkers == 8 && loaded.Memory.Passes.Count == 1, "memory");
        Check(loaded.Memory.VariationPercent["Copy"] == 1.2, "variation dictionary");
        Check(loaded.Memory.Latency!.NanosecondsPerAccess == 100.2, "latency");
        Check(loaded.Disk!.Measurements.Count == 3 && loaded.Disk.Measurements[0].FirstPassMBps == 1800.5, "disk measurements");
        Check(loaded.Warnings.Count == 1, "warnings list");
    }
    finally { File.Delete(path); }
});
Test("JSON keeps the camelCase names the PowerShell scripts read", () =>
{
    string json = JsonSerializer.Serialize(Sample(), ResultStore.JsonOptions);
    foreach (string name in new[] { "\"mBps\"", "\"gBps\"", "\"multiThread\"", "\"profileHash\"", "\"firstPassMBps\"", "\"multiThreadReadGBps\"" })
        Check(json.Contains(name), name + " missing");
});

// ---- persistence ----
Test("ResultStore keeps 50 history pairs, returns the saved path, and can write compact JSON", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "odp-store-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new ResultStore(dir);
        string last = "";
        for (int i = 0; i < 53; i++) last = store.Save(new AnalysisResult { Timestamp = DateTimeOffset.Now.AddSeconds(i) });
        Check(File.Exists(last), "returned path exists");
        Check(Directory.GetFiles(Path.Combine(dir, "Results"), "*.json").Length == ResultStore.MaxHistory, "json pruned to 50");
        Check(Directory.GetFiles(Path.Combine(dir, "Results"), "*.txt").Length == ResultStore.MaxHistory, "txt pruned to 50");
        Check(File.Exists(Path.Combine(dir, "Latest.json")) && File.Exists(Path.Combine(dir, "Latest.txt")), "latest files");
        string compact = new ResultStore(dir, compact: true).Save(new AnalysisResult { Timestamp = DateTimeOffset.Now.AddMinutes(5) }); // newest, so pruning keeps it
        Check(!File.ReadAllText(compact).Contains('\n'), "compact JSON is one line");
        Check(ResultStore.Load(compact).Status == "Success", "compact JSON loads");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});

// ---- summaries and report text ----
Test("ResultSummary cards for a complete result", () =>
{
    var r = Sample();
    Check(ResultSummary.CpuCard(r) == new CardValue("success", "1,588.6", "Multi · MB/s"), "cpu card");
    Check(ResultSummary.RamCard(r).Value == "5.9", "ram card");
    var disk = ResultSummary.DiskCard(r);
    Check(disk.State == "success" && disk.Value == "1,800.5", "disk card must prefer the first full pass, got " + disk.Value);
});
Test("ResultSummary reports incomplete stages instead of scores", () =>
{
    var r = Sample();
    r.Cpu!.Status = "TimedOut"; r.Cpu.MultiThread = null;
    r.Memory = null;
    r.Disk!.Status = "Cancelled";
    Check(ResultSummary.CpuCard(r) == new CardValue("partial", "—", "TimedOut"), "timed-out cpu");
    Check(ResultSummary.RamCard(r).State == "waiting", "missing ram");
    Check(ResultSummary.DiskCard(r).Caption == "Cancelled", "cancelled disk");
});
Test("ResultSummary headline and lines", () =>
{
    var r = Sample();
    Check(ResultSummary.Headline(r) == "Test complete", "success");
    r.Warnings.Add("w");
    Check(ResultSummary.Headline(r) == "Test complete — review warnings", "warnings");
    r.Status = "Partial"; Check(ResultSummary.Headline(r).Contains("incomplete"), "partial");
    r.Status = "Cancelled"; Check(ResultSummary.Headline(r) == "Test cancelled", "cancelled");
    r.Status = "Failed"; Check(ResultSummary.Headline(r) == "Test failed", "failed");
    var lines = ResultSummary.Lines(r);
    Check(lines.Any(l => l.StartsWith("RAM, 8 workers")), "multithread RAM line");
    Check(lines.Any(l => l.Contains("(first full pass)")), "first-pass line");
    Check(lines.Any(l => l.StartsWith("Disk random read: 14,516 IOPS")), "random read line");
});
Test("TextReportWriter handles full and mostly empty results", () =>
{
    string full = TextReportWriter.Format(Sample());
    Check(full.Contains("Memory Read, 8 workers") && full.Contains("First full pass of test file"), "new lines present");
    Check(full.Contains("CPU benchmark profile: deflate-v3 / " + CpuBenchmark.Profile), "profile line");
    string empty = TextReportWriter.Format(new AnalysisResult { Status = "Failed" });
    Check(empty.Contains("Status: Failed"), "empty result");
});

Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
