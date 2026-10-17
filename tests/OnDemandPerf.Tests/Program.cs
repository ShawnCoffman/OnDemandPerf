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
        multi.Passes.Add(new CpuPass(pass, 4_000_000_000, 3.0, 1333.3) { ParticipatingWorkers = 8, WorkerBytesProcessed = new long[8], AverageBusyLogicalProcessors = 7.8, OutputVerified = true });
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
        Check(loaded.RunId == original.RunId && loaded.SchemaVersion == "2.2", "identity");
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
Test("A saved run's own text report survives a later run replacing Latest.txt (regression: Open Results showed the wrong run)", () =>
{
    string dir = Path.Combine(Path.GetTempPath(), "odp-store-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new ResultStore(dir);
        string first = store.Save(Sample());
        string firstText = File.ReadAllText(ResultStore.TextReportPath(first));
        string second = store.Save(new AnalysisResult { Timestamp = DateTimeOffset.Now.AddMinutes(1), Status = "Failed" });
        string latest = File.ReadAllText(Path.Combine(dir, "Latest.txt"));
        Check(ResultStore.TextReportPath(first) == Path.ChangeExtension(first, ".txt"), "text report sits beside the history JSON");
        Check(File.ReadAllText(ResultStore.TextReportPath(first)) == firstText, "the first run's report is unchanged");
        Check(latest == File.ReadAllText(ResultStore.TextReportPath(second)) && latest != firstText, "Latest.txt now holds the second run");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
});

// ---- CPU stage cancellation (a fake stage measurer; nothing is benchmarked) ----
CpuMeasurement FakeStage(int workers)
{
    var stage = new CpuMeasurement { RequestedWorkers = workers, RequestedMillisecondsPerPass = 1, MBps = 100, OnFastestCoresPercent = 100 };
    for (int pass = 1; pass <= 3; pass++)
        stage.Passes.Add(new CpuPass(pass, 1_000_000, 0.01, 100) { ParticipatingWorkers = workers, AverageBusyLogicalProcessors = workers, OutputVerified = true });
    return stage;
}
Test("Cancelling during multithread keeps the completed single-thread stage (regression: cpu was null)", () =>
{
    using var user = new CancellationTokenSource();
    var cpu = CpuBenchmark.Run(user.Token, _ => { }, null, (_, workers, _, name, token, _) =>
    {
        if (name == "Single-thread") return FakeStage(workers);
        user.Cancel(); // the user cancels while multithread is running
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("unreachable");
    });
    Check(cpu.Status == "Cancelled", "status " + cpu.Status);
    Check(cpu.SingleThread is { Passes.Count: 3 } && cpu.MultiThread is null, "single-thread kept, multithread unscored");
    Check(!cpu.OutputVerified, "an unfinished stage is not verified");
    Check(cpu.QualityNotes.Any(n => n.Contains("cancelled")), "the note says why multithread has no score");
});
Test("Cancelling before any CPU stage finishes gives an empty Cancelled result instead of throwing", () =>
{
    using var user = new CancellationTokenSource();
    user.Cancel();
    var cpu = CpuBenchmark.Run(user.Token, _ => { }, null, (_, _, _, _, token, _) => { token.ThrowIfCancellationRequested(); return FakeStage(1); });
    Check(cpu.Status == "Cancelled" && cpu.SingleThread is null && cpu.MultiThread is null, "status " + cpu.Status);
});
Test("A CPU stage that runs out of time is still TimedOut, not Cancelled", () =>
{
    var cpu = CpuBenchmark.Run(CancellationToken.None, _ => { }, TimeSpan.Zero, (_, workers, _, name, token, _) =>
    {
        if (name == "Single-thread") return FakeStage(workers);
        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)); // a zero time limit cancels the budget almost at once
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("the stage budget never expired");
    });
    Check(cpu.Status == "TimedOut", "status " + cpu.Status);
    Check(cpu.SingleThread is not null && cpu.MultiThread is null, "completed stage kept");
});
Test("A full fake CPU run succeeds and is verified", () =>
{
    var cpu = CpuBenchmark.Run(CancellationToken.None, _ => { }, null, (_, workers, _, _, _, _) => FakeStage(workers));
    Check(cpu.Status == "Success" && cpu.OutputVerified && cpu.QualityNotes.Count == 0, "status " + cpu.Status + "; " + string.Join("; ", cpu.QualityNotes));
});

// ---- panel sizing ----
Test("The panel never extends past the working area, and scrolls when it is shorter than its minimum", () =>
{
    // Review case: 200% scaling, 984 px working area less 2 x 24 px margins; the 520-logical minimum is 1,040 px.
    var (height, scroll) = PanelFit.Compute(preferred: 1180, minimum: 1040, available: 984 - 48);
    Check(height == 936 && scroll == 1040, $"high DPI: height {height}, scroll {scroll}");
    Check(PanelFit.Compute(590, 520, 1000) == (590, 0), "plenty of room: designed height, no scrolling");
    Check(PanelFit.Compute(590, 520, 560) == (560, 0), "short screen: shortened above the minimum, no scrolling");
    Check(PanelFit.Compute(590, 520, 520) == (520, 0), "exactly the minimum still fits");
    Check(PanelFit.Compute(590, 520, -10).Height == 1, "a nonsensical working area cannot give a zero or negative height");
});

// ---- summaries and report text ----
Test("ResultSummary cards for a complete result", () =>
{
    var r = Sample();
    Check(ResultSummary.CpuCard(r) == new CardValue("success", "96.5", "Single · MB/s"), "cpu card shows single-thread");
    Check(ResultSummary.RamCard(r) == new CardValue("success", "60.1", "Multi read · GB/s"), "ram card shows multithread read");
    Check(ResultSummary.DiskCard(r) == new CardValue("success", "14,516", "4K random · IOPS"), "disk card shows 4K random-read IOPS");
    r.Memory!.MultiThreadReadGBps = null;
    Check(ResultSummary.RamCard(r) == new CardValue("success", "11.2", "Read · GB/s"), "ram card falls back to single-worker read");
});
Test("ResultSummary reports incomplete stages instead of scores", () =>
{
    var r = Sample();
    r.Cpu!.Status = "TimedOut"; r.Cpu.MultiThread = null;
    Check(ResultSummary.CpuCard(r).Value == "96.5", "a completed single-thread stage still shows when multithread timed out");
    r.Cpu.SingleThread = null;
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
Test("ResultSummary notification names only the stages that produced a score", () =>
{
    var r = Sample();
    Check(ResultSummary.Notification(r) == "Test complete. CPU 96.5 MB/s · RAM 60.1 GB/s · Disk 14,516 IOPS", ResultSummary.Notification(r));
    r.Status = "Partial"; r.Disk!.Status = "TimedOut"; r.Memory = null;
    string partial = ResultSummary.Notification(r);
    Check(partial.StartsWith("Test complete — review incomplete tests. CPU") && !partial.Contains("RAM") && !partial.Contains('—' + " MB/s"), partial);
    r.Status = "Failed"; r.Cpu = null; r.Disk = null; r.Memory = null;
    Check(ResultSummary.Notification(r) == "Test failed", "no scores, headline only");
});
Test("StageBudget uses the sooner of the stage budget and the time left in the run", () =>
{
    Check(StageBudget.Limit(22, null) == TimeSpan.FromSeconds(22), "no run deadline");
    Check(StageBudget.Limit(22, TimeSpan.FromSeconds(30)) == TimeSpan.FromSeconds(22), "plenty of time");
    Check(StageBudget.Limit(22, TimeSpan.FromSeconds(9.5)) == TimeSpan.FromSeconds(9.5), "short of time");
    Check(StageBudget.Limit(22, TimeSpan.FromSeconds(-3)) == TimeSpan.Zero, "already late");
});
Test("TextReportWriter handles full and mostly empty results", () =>
{
    string full = TextReportWriter.Format(Sample());
    Check(full.Contains("Memory Read, 8 workers") && full.Contains("First full pass of test file"), "new lines present");
    Check(full.Contains("CPU benchmark profile: deflate-v4 / " + CpuBenchmark.Profile), "profile line");
    Check(full.StartsWith("ONDEMAND PERF — SUMMARY") && full.Contains("Findings: none") && full.Contains("DETAILS"), "summary first");
    Check(full.Contains("Validity: Valid") && !full.Contains("Battery charge") && !full.Contains("Manufacturer:"), "trimmed lines");
    string empty = TextReportWriter.Format(new AnalysisResult { Status = "Failed" });
    Check(empty.Contains("Status: Failed"), "empty result");
});

// ---- hardware identity ----
// Builds one SMBIOS structure: formatted area of `length` bytes (type, length, handle, then fields) plus its string set.
byte[] Structure(byte type, int length, Action<byte[]> fill, params string[] strings)
{
    var formatted = new byte[length];
    formatted[0] = type; formatted[1] = (byte)length;
    fill(formatted);
    var bytes = new List<byte>(formatted);
    foreach (var text in strings) { bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text)); bytes.Add(0); }
    if (strings.Length == 0) bytes.Add(0);
    bytes.Add(0);
    return bytes.ToArray();
}
byte[] Dimm(string slot, ushort sizeMiB, byte type, ushort rated, ushort configured, byte formFactor = 9)
{
    return Structure(17, 0x28, f =>
    {
        BitConverter.GetBytes(sizeMiB).CopyTo(f, 0x0C);
        f[0x0E] = formFactor; f[0x10] = 1; f[0x12] = type;
        BitConverter.GetBytes(rated).CopyTo(f, 0x15);
        f[0x17] = 2; f[0x1A] = 3;
        BitConverter.GetBytes(configured).CopyTo(f, 0x20);
    }, slot, "Kingston", "KF556C40-16");
}
byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

byte[] LaptopTable() => Concat(
    Structure(0, 0x18, f => { f[4] = 1; f[5] = 2; f[8] = 3; }, "LENOVO", "N3XET50W (1.25 )", "01/05/2024"),
    Structure(1, 0x1B, f => { f[4] = 1; f[5] = 2; f[6] = 3; f[0x1A] = 4; }, "LENOVO", "21HD0012US", "ThinkPad T14 Gen 4", "ThinkPad T14 Gen 4"),
    Dimm("DIMM A", 16384, 0x22, 5600, 4800),
    Dimm("DIMM B", 16384, 0x22, 5600, 4800),
    Dimm("DIMM C", 0, 0x22, 0, 0),
    Structure(127, 4, _ => { }));

Test("SMBIOS parser reads machine identity, DDR type, speeds and empty slots", () =>
{
    var (hardware, memory) = Smbios.Parse(LaptopTable());
    Check(hardware.Manufacturer == "LENOVO" && hardware.Model == "21HD0012US", "system strings");
    Check(hardware.Family == "ThinkPad T14 Gen 4", "family");
    Check(hardware.BiosVersion == "N3XET50W (1.25 )" && hardware.BiosDate == "01/05/2024", "bios");
    Check(memory.ModuleCount == 2 && memory.SlotCount == 3, $"modules/slots {memory.ModuleCount}/{memory.SlotCount}");
    Check(memory.Type == "DDR5" && memory.TotalBytes == 32UL * 1024 * 1024 * 1024, "type and size");
    Check(memory.RatedMts == 5600 && memory.ConfiguredMts == 4800, "speeds");
    Check(memory.Modules[0].Manufacturer == "Kingston" && memory.Modules[0].PartNumber == "KF556C40-16" && memory.Modules[0].Slot == "DIMM A", "module strings");
});
Test("SMBIOS parser ignores firmware placeholders and falls back to the motherboard", () =>
{
    var table = Concat(
        Structure(1, 0x1B, f => { f[4] = 1; f[5] = 2; }, "To Be Filled By O.E.M.", "System Product Name"),
        Structure(2, 0x08, f => { f[4] = 1; f[5] = 2; }, "ASUSTeK COMPUTER INC.", "ROG STRIX Z790-E GAMING WIFI"));
    var (hardware, memory) = Smbios.Parse(table);
    Check(hardware.Manufacturer == "ASUSTeK COMPUTER INC." && hardware.Model == "ROG STRIX Z790-E GAMING WIFI", "board fallback");
    Check(memory.ModuleCount == 0 && memory.SlotCount is null, "no memory structures");
});
Test("SMBIOS parser handles extended size and speed, KB sizes and malformed tables", () =>
{
    byte[] big = Structure(17, 0x5C, f =>
    {
        BitConverter.GetBytes((ushort)0x7FFF).CopyTo(f, 0x0C);
        BitConverter.GetBytes(65536u).CopyTo(f, 0x1C);                 // 64 GiB extended size
        f[0x0E] = 13; f[0x12] = 0x22;
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(f, 0x15);
        BitConverter.GetBytes(6400u).CopyTo(f, 0x54);                  // extended rated speed
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(f, 0x20);
        BitConverter.GetBytes(6000u).CopyTo(f, 0x58);                  // extended configured speed
    }, "SODIMM 1");
    var (_, memory) = Smbios.Parse(big);
    Check(memory.TotalBytes == 64UL * 1024 * 1024 * 1024 && memory.RatedMts == 6400 && memory.ConfiguredMts == 6000, "extended fields");
    Check(memory.Modules[0].FormFactor == "SODIMM", "form factor");
    var kb = Smbios.Parse(Dimm("A", 0, 0x1A, 3200, 3200).Select((b, i) => b).ToArray()); // size 0 = empty
    Check(kb.Memory.ModuleCount == 0, "empty slot is not a module");
    var truncated = LaptopTable()[..60];
    Smbios.Parse(truncated);                 // must not throw
    Smbios.Parse([]);
    Smbios.Parse([17, 0xFF, 0, 0, 0]);       // declared length past the end
});
Test("Smbios.Clean normalizes strings", () =>
{
    Check(Smbios.Clean("  Samsung   SSD  ") == "Samsung SSD", "whitespace");
    Check(Smbios.Clean("Default string") is null && Smbios.Clean("To be filled by O.E.M.") is null && Smbios.Clean("  ") is null, "placeholders");
});

byte[] DeviceDescriptor(string? vendor, string product, string revision, int busType)
{
    var buffer = new byte[256];
    int next = 40;
    int Put(string? text)
    {
        if (text is null) return 0;
        int at = next;
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(buffer, at);
        next += text.Length + 1;
        return at;
    }
    BitConverter.GetBytes(Put(vendor)).CopyTo(buffer, 12);
    BitConverter.GetBytes(Put(product)).CopyTo(buffer, 16);
    BitConverter.GetBytes(Put(revision)).CopyTo(buffer, 20);
    BitConverter.GetBytes(busType).CopyTo(buffer, 28);
    return buffer;
}
Test("Storage descriptor parser extracts model, firmware and bus type", () =>
{
    var nvme = new DriveIdentity();
    StorageIdentity.ParseDeviceDescriptor(DeviceDescriptor(null, "Samsung SSD 990 PRO 1TB", "4B2QJXD7", 17), 256, nvme);
    Check(nvme.Model == "Samsung SSD 990 PRO 1TB" && nvme.Firmware == "4B2QJXD7" && nvme.BusType == "NVMe", "nvme");
    var sata = new DriveIdentity();
    StorageIdentity.ParseDeviceDescriptor(DeviceDescriptor("ATA", "CT500MX500SSD1", "M3CR046", 11), 256, sata);
    Check(sata.Model == "CT500MX500SSD1" && sata.BusType == "SATA", "ATA vendor dropped");
    var scsi = new DriveIdentity();
    StorageIdentity.ParseDeviceDescriptor(DeviceDescriptor("WDC", "WD10EZEX-08WN4A0", "01.01A01", 11), 256, scsi);
    Check(scsi.Model == "WDC WD10EZEX-08WN4A0", "useful vendor kept");
    bool threw = false;
    try { StorageIdentity.ParseDeviceDescriptor(new byte[256], 10, new DriveIdentity()); } catch (InvalidDataException) { threw = true; }
    Check(threw, "short buffer rejected");
    Check(StorageIdentity.BusTypeName(7) == "USB" && StorageIdentity.BusTypeName(999) == "Unknown", "bus names");
});
Test("NVMe identify parser reads version, model and firmware", () =>
{
    var buffer = new byte[8 + 40 + 4096];
    BitConverter.GetBytes(40u).CopyTo(buffer, 24);      // ProtocolDataOffset
    BitConverter.GetBytes(4096u).CopyTo(buffer, 28);    // ProtocolDataLength
    int data = 8 + 40;
    System.Text.Encoding.ASCII.GetBytes("Samsung SSD 990 PRO 1TB".PadRight(40)).CopyTo(buffer, data + 24);
    System.Text.Encoding.ASCII.GetBytes("4B2QJXD7").CopyTo(buffer, data + 64);
    BitConverter.GetBytes(0x00010400u).CopyTo(buffer, data + 80); // NVMe 1.4
    var (version, model, firmware) = StorageIdentity.ParseNvmeIdentify(buffer, buffer.Length);
    Check(version == "1.4" && model == "Samsung SSD 990 PRO 1TB" && firmware == "4B2QJXD7", $"{version}/{model}/{firmware}");
    BitConverter.GetBytes(0x00020001u).CopyTo(buffer, data + 80);
    Check(StorageIdentity.ParseNvmeIdentify(buffer, buffer.Length).Version == "2.0.1", "tertiary version");
    BitConverter.GetBytes(0u).CopyTo(buffer, data + 80);
    Check(StorageIdentity.ParseNvmeIdentify(buffer, buffer.Length).Version is null, "version zero is unknown");
    bool threw = false;
    try { StorageIdentity.ParseNvmeIdentify(buffer, 30); } catch (InvalidDataException) { threw = true; }
    Check(threw, "short response rejected");
    var query = StorageIdentity.NvmeIdentifyQuery();
    Check(query.Length == 48 && BitConverter.ToInt32(query, 0) == 49 && BitConverter.ToInt32(query, 24) == 40, "query layout");
});

DeviceInfo LaptopDevice()
{
    var (hardware, memory) = Smbios.Parse(LaptopTable());
    return new DeviceInfo("PC1", "Windows 11 Pro", "X64", "13th Gen Intel(R) Core(TM) i7-1355U", 12, ".NET 10")
    {
        PhysicalCores = 10, SystemLogicalProcessors = 12, CpuBaseMhz = 1700, Hardware = hardware, InstalledMemory = memory,
        TestDrive = new DriveIdentity { Volume = @"C:\", Model = "Samsung SSD 990 PRO 1TB", BusType = "NVMe", NvmeVersion = "1.4", IsSolidState = true, SizeBytes = 1_000_204_886_016 }
    };
}
Test("HardwareSummary writes one readable line each for system, CPU, RAM and disk", () =>
{
    var lines = HardwareSummary.Lines(LaptopDevice());
    Check(lines.Select(l => l.Label).SequenceEqual(["SYSTEM", "CPU", "RAM", "DISK"]), "labels");
    Check(lines[0].Text == "LENOVO ThinkPad T14 Gen 4 · 21HD0012US · BIOS N3XET50W (1.25 ) (01/05/2024)", lines[0].Text);
    Check(lines[1].Text == "13th Gen Intel Core i7-1355U · 10 cores / 12 threads · 1.7 GHz base", lines[1].Text);
    Check(lines[2].Text == "32 GB DDR5-4800 · 2 × 16 GB · 2 of 3 slots · rated 5600", lines[2].Text);
    Check(lines[3].Text == "Samsung SSD 990 PRO 1TB · NVMe 1.4 · C: (1 TB)", lines[3].Text);
});
Test("HardwareSummary degrades gracefully when nothing is known", () =>
{
    var lines = HardwareSummary.Lines(null);
    Check(lines.All(l => l.Text.Length > 0), "every line has text");
    var bare = new DeviceInfo("PC2", "Windows", "X64", "Unavailable", 4, ".NET 10");
    Check(HardwareSummary.Machine(bare) == "PC2" && HardwareSummary.Cpu(bare) == "Unavailable", "bare device");
    Check(HardwareSummary.Ram(new InstalledMemory()) == "Module details unavailable", "no modules");
});
Test("HardwareSummary hints flag single-channel RAM, low RAM speed and spinning disks", () =>
{
    var single = LaptopDevice();
    single.InstalledMemory!.Modules.RemoveAt(1); single.InstalledMemory.ModuleCount = 1;
    single.TestDrive!.IsSolidState = false; single.TestDrive.BusType = "SATA"; single.TestDrive.NvmeVersion = null;
    var hints = HardwareSummary.Hints(single);
    Check(hints.Any(h => h.Contains("single module")), "single channel");
    Check(hints.Any(h => h.Contains("XMP/EXPO")), "below rated");
    Check(hints.Any(h => h.Contains("spinning")), "hdd");
    Check(HardwareSummary.Disk(single.TestDrive).Contains("SATA HDD"), "sata hdd label");
    var healthy = LaptopDevice();
    healthy.InstalledMemory!.ConfiguredMts = 5600;
    Check(HardwareSummary.Hints(healthy).Count == 0, "no hints for a healthy configuration");
});
Test("HardwareSummary does not suggest XMP/EXPO for laptop memory or on absurd firmware speeds", () =>
{
    foreach (byte formFactor in new byte[] { 13, 11, 17 }) // SODIMM, soldered (row of chips), CAMM
    {
        var laptop = LaptopDevice();
        foreach (var module in laptop.InstalledMemory!.Modules)
            module.FormFactor = Smbios.FormFactorName(formFactor);
        Check(!HardwareSummary.Hints(laptop).Any(h => h.Contains("XMP")), $"form factor {formFactor}");
        Check(HardwareSummary.Ram(laptop.InstalledMemory).Contains("rated 5600"), "the rated speed is still shown");
    }
    Check(Smbios.FormFactorName(17) == "CAMM", "CAMM name");
    var huge = LaptopDevice();
    huge.InstalledMemory!.ConfiguredMts = 50_000_000; huge.InstalledMemory.RatedMts = 50_000_000; // overflowed 32-bit math
    Check(!HardwareSummary.Hints(huge).Any(h => h.Contains("XMP")), "equal speeds are never below rated");
});
Test("Text report numbers do not depend on the user's culture", () =>
{
    var original = CultureInfo.CurrentCulture;
    try
    {
        var r = Sample();
        r.Device = LaptopDevice();
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        string report = TextReportWriter.Format(r);
        Check(report.Contains("1.7 GHz base") && !report.Contains("1,7 GHz"), "hardware line");
        Check(report.Contains("operations in 0.210s") && report.Contains("78135 / 0.013 ms / 0.015 ms"), "disk lines");
        Check(HardwareSummary.Cpu(r.Device).Contains("1,7 GHz"), "the panel still follows the user's culture");
    }
    finally { CultureInfo.CurrentCulture = original; }
});
Test("Hardware identity survives the JSON round trip and reaches the report", () =>
{
    var original = Sample();
    original.Device!.Hardware = new HardwareInfo { Manufacturer = "LENOVO", Model = "21HD0012US" };
    original.Device.InstalledMemory = LaptopDevice().InstalledMemory;
    original.Device.TestDrive = LaptopDevice().TestDrive;
    original.Device.CpuBaseMhz = 1700;
    string json = JsonSerializer.Serialize(original, ResultStore.JsonOptions);
    Check(json.Contains("\"nvmeVersion\": \"1.4\"") && json.Contains("\"configuredMts\": 4800"), "camelCase keys");
    var loaded = JsonSerializer.Deserialize<AnalysisResult>(json, ResultStore.JsonOptions)!;
    Check(loaded.Device!.InstalledMemory!.Modules.Count == 2 && loaded.Device.TestDrive!.NvmeVersion == "1.4" && loaded.Device.CpuBaseMhz == 1700, "round trip");
    string report = TextReportWriter.Format(loaded);
    Check(report.Contains("HARDWARE") && report.Contains("RAM module DIMM A"), "report sections");
    Check(ResultSummary.Lines(loaded).Any(l => l.StartsWith("Note — ")), "results without findings still show hardware hints");
    FindingRules.Apply(loaded);
    Check(loaded.Findings.Any(f => f.Area == "RAM" && f.Message.StartsWith("Running at 4800")), "hardware hints become findings with their area");
    Check(TextReportWriter.Format(loaded).Contains("- RAM: Running at 4800"), "findings reach the report");
    Check(ResultSummary.Lines(loaded)[0] == "FINDINGS" && !ResultSummary.Lines(loaded).Any(l => l.StartsWith("Note — ")), "findings replace hints in the summary");
});

// ---- support-test changes: power validity, findings, core steering, fixed sizes ----
Test("Power plans and power modes are named from their GUIDs", () =>
{
    Check(PowerPolicy.PlanType(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")) == "Balanced", "balanced");
    Check(PowerPolicy.PlanType(new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")) == "HighPerformance", "high");
    Check(PowerPolicy.PlanType(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")) == "PowerSaver", "saver");
    Check(PowerPolicy.PlanType(Guid.NewGuid()) == "Custom", "custom");
    Check(PowerPolicy.PowerMode(Guid.Empty) == "Balanced", "no overlay is balanced");
    Check(PowerPolicy.PowerMode(new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")) == "BestPowerEfficiency", "efficiency");
    Check(PowerPolicy.PowerMode(new Guid("ded574b5-45a0-4f42-8737-46345c09c238")) == "BestPerformance", "performance");
});

AnalysisResult Conditions(Action<TestEnvironment> change)
{
    var r = Sample();
    r.Environment = new TestEnvironment
    {
        PowerSource = "AC", PowerPlan = "Balanced", PowerPlanType = "Balanced", PowerMode = "Balanced", EnergySaverOn = false,
        MaxProcessorStatePercent = 100, ProcessorBoostMode = 2, UptimeDays = 1, PendingRestart = [],
        TotalPhysicalBytes = 32UL << 30, AvailablePhysicalBytes = 20UL << 30, CommitBytes = 20UL << 30, CommitLimitBytes = 40UL << 30,
        SystemDrive = @"C:\", SystemDriveFreeBytes = 200UL << 30, SystemDriveTotalBytes = 1000UL << 30, CpuUtilizationPercent = 3
    };
    change(r.Environment);
    FindingRules.Apply(r);
    return r;
}

Test("A healthy run has no findings and is valid", () =>
{
    var r = Conditions(_ => { });
    Check(r.Findings.Count == 0 && r.Validity == "Valid", string.Join(" | ", r.Findings));
});
Test("Power-saving plans, modes and energy saver make the run invalid", () =>
{
    Check(Conditions(e => { e.PowerPlanType = "PowerSaver"; e.PowerPlan = "Power saver"; }).Validity == "Invalid", "power saver plan");
    Check(Conditions(e => { e.PowerPlanType = "Custom"; e.PowerPlan = "Contoso Battery Saver"; }).Validity == "Invalid", "custom plan named saver");
    Check(Conditions(e => e.PowerMode = "BestPowerEfficiency").Validity == "Invalid", "best power efficiency on Balanced");
    Check(Conditions(e => e.EnergySaverOn = true).Validity == "Invalid", "energy saver");
    // Windows ignores the power mode on High performance, so a stale overlay must not invalidate it.
    Check(Conditions(e => { e.PowerPlanType = "HighPerformance"; e.PowerMode = "BestPowerEfficiency"; }).Validity == "Valid", "overlay ignored");
    Check(Conditions(e => e.PowerPlanType = "UltimatePerformance").Validity == "Valid", "ultimate");
    var custom = Conditions(e => { e.PowerPlanType = "Custom"; e.PowerPlan = "Company standard"; });
    Check(custom.Validity == "Valid" && custom.Findings.Single().Area == "Power", "unknown custom plan is a warning");
    Check(Conditions(e => { e.PowerPlanType = "Custom"; e.PowerPlan = "Ultimate Performance"; }).Findings.Count == 0, "duplicated Ultimate plan");
});
Test("Battery is shown but does not invalidate; invalid findings come first", () =>
{
    var r = Conditions(e => { e.PowerSource = "Battery"; e.BatteryChargePercent = 64; });
    Check(r.Validity == "Valid" && r.Findings.Single().Message.StartsWith("Running on battery (64% charged)"), "battery warning");
    Check(ResultSummary.Headline(r) == "Test complete — on battery power", ResultSummary.Headline(r));
    var both = Conditions(e => { e.PowerSource = "Battery"; e.EnergySaverOn = true; });
    Check(both.Findings[0].Severity == "Invalid" && ResultSummary.Headline(both) == "Test not valid — power saving was on", "invalid first");
    Check(ResultSummary.FindingLines(both)[0].StartsWith("NOT VALID — Power: Energy saver"), ResultSummary.FindingLines(both)[0]);
    Check(TextReportWriter.Format(both).Contains("Validity: NOT VALID"), "report validity");
    var unplugged = Conditions(_ => { });
    unplugged.Activity = new ActivityResult();
    unplugged.Activity.Samples.Add(new ActivitySample(1, 1, "Disk", 10, null, "Battery"));
    FindingRules.Apply(unplugged);
    Check(unplugged.Findings.Single().Message.Contains("unplugged"), "unplugged during the test");
});
Test("Slowness findings: background load, clock, memory, disk space, restarts, plan limits", () =>
{
    string Areas(AnalysisResult r) => string.Join(",", r.Findings.Select(f => f.Area));
    var busy = Conditions(e => { e.CpuUtilizationPercent = 45; e.TopProcesses.Add(new ProcessCpu("MsMpEng.exe", 4000, 31.2)); });
    Check(busy.Findings.Single().Message.Contains("45% busy") && busy.Findings[0].Message.Contains("MsMpEng.exe 31%"), busy.Findings[0].Message);
    var hot = Conditions(_ => { });
    hot.Device!.CpuBaseMhz = 2000;
    hot.Activity = new ActivityResult();
    foreach (double clock in new[] { 60.0, 64.0, 62.0 })
        hot.Activity.Samples.Add(new ActivitySample(1, 1, "CPU", 99, null, "AC") { ProcessorPerformancePercent = clock });
    hot.Activity.Samples.Add(new ActivitySample(1, 1, "CPU", 20, null, "AC") { ProcessorPerformancePercent = 10 }); // not under load
    FindingRules.Apply(hot);
    Check(hot.Findings.Single().Message.Contains("about 62% of its base clock (about 1.2 GHz against a 2.0 GHz base)"), hot.Findings.Single().Message);
    Check(Areas(Conditions(e => e.AvailablePhysicalBytes = 512UL << 20)) == "RAM", "low free RAM");
    Check(Areas(Conditions(e => { e.TotalPhysicalBytes = 4UL << 30; e.AvailablePhysicalBytes = 2UL << 30; })) == "RAM", "small RAM");
    Check(Areas(Conditions(e => e.CommitBytes = 38UL << 30)) == "RAM", "commit near limit");
    Check(Areas(Conditions(e => e.SystemDriveFreeBytes = 8UL << 30)) == "Disk", "low drive space");
    Check(Areas(Conditions(e => { e.UptimeDays = 12; e.PendingRestart = ["Windows Update"]; })) == "System,System", "uptime and restart");
    Check(Areas(Conditions(e => { e.MaxProcessorStatePercent = 70; e.ProcessorBoostMode = 0; })) == "Power,Power", "plan CPU limits");
    var reduced = Conditions(_ => { });
    reduced.Disk!.ReducedForFreeSpace = true; reduced.Disk.FileLimitBytes = 300L << 20;
    FindingRules.Apply(reduced);
    Check(reduced.Findings.Single().Message.Contains("300 MiB"), "reduced disk file");
    var parked = Conditions(_ => { });
    parked.Cpu!.SingleThread!.OnFastestCoresPercent = 40;
    FindingRules.Apply(parked);
    Check(parked.Findings.Single().Message.Contains("fastest cores only 40%"), "single thread off the fastest cores");
});
Test("Ticket summary is short, culture-independent and complete", () =>
{
    var original = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var r = Conditions(e => { e.PowerSource = "Battery"; e.BatteryChargePercent = 64; });
        r.Device!.CpuBaseMhz = 3200;
        var lines = ResultSummary.TicketSummary(r);
        Check(lines.Count <= 14, "fits in a ticket: " + lines.Count);
        Check(lines[1] == "Result: Test complete — on battery power", lines[1]);
        Check(lines.Contains("Power: Battery (64%) · plan Balanced · mode Balanced"), "power line");
        Check(lines.Contains("Scores: CPU single-thread 96.5 MB/s · RAM multithread read 60.1 GB/s · Disk 4K random read 14,516 IOPS"), "scores");
        Check(lines.Any(l => l.StartsWith("Also: CPU multithread 1,588.6 MB/s · RAM latency 100.2 ns · Disk sequential read 1,800 MB/s")), "secondary");
        Check(lines.Contains("Findings (1):") && lines[^1].StartsWith("- Power: Running on battery"), "findings");
        var invalid = Conditions(e => e.EnergySaverOn = true);
        Check(ResultSummary.TicketSummary(invalid)[1].Contains("NOT VALID"), "invalid is unmissable");
        var empty = ResultSummary.TicketSummary(new AnalysisResult { Status = "Failed" });
        Check(empty.Any(l => l.Contains("CPU single-thread not completed")) && empty[^1] == "Findings: none", "empty result");
    }
    finally { CultureInfo.CurrentCulture = original; }
});
Test("Busy-process lines appear in the report only when the PC was busy", () =>
{
    var quiet = Conditions(e => e.TopProcesses.Add(new ProcessCpu("idle-ish.exe", 1, 0.7)));
    Check(!TextReportWriter.Format(quiet).Contains("Busy before test"), "quiet PC");
    var busy = Conditions(e => { e.CpuUtilizationPercent = 40; e.TopProcesses.Add(new ProcessCpu("scan.exe", 1, 30)); });
    Check(TextReportWriter.Format(busy).Contains("Busy before test: scan.exe (PID 1) 30.0% of all CPUs"), "busy PC");
});
Test("Findings survive the JSON round trip", () =>
{
    var r = Conditions(e => e.EnergySaverOn = true);
    var loaded = JsonSerializer.Deserialize<AnalysisResult>(JsonSerializer.Serialize(r, ResultStore.JsonOptions), ResultStore.JsonOptions)!;
    Check(loaded.Validity == "Invalid" && loaded.Findings[0] == r.Findings[0], "findings and validity");
});

byte[] CpuSetRecord(uint id, byte logical, byte efficiencyClass, byte flags = 0)
{
    var record = new byte[32];
    BitConverter.GetBytes(32u).CopyTo(record, 0);
    BitConverter.GetBytes(id).CopyTo(record, 8);
    record[14] = logical; record[18] = efficiencyClass; record[19] = flags;
    return record;
}
Test("CPU set parser and fastest-core selection", () =>
{
    var buffer = Concat(CpuSetRecord(256, 0, 1), CpuSetRecord(257, 1, 1), CpuSetRecord(258, 2, 0), CpuSetRecord(259, 3, 1, flags: 2));
    var sets = CoreSelection.Parse(buffer, buffer.Length);
    Check(sets.Count == 4 && sets[2].EfficiencyClass == 0 && sets[3].Allocated, "parsed");
    Check(CoreSelection.Fastest(sets, _ => true).SequenceEqual(new uint[] { 256, 257 }), "fastest, unreserved");
    Check(CoreSelection.Fastest(sets, s => s.LogicalProcessor == 1).SequenceEqual(new uint[] { 257 }), "respects affinity");
    Check(CoreSelection.Fastest(sets, s => s.LogicalProcessor == 2).Length == 0, "never steers to a slower class");
    bool threw = false;
    try { CoreSelection.Parse(new byte[12], 12); } catch (InvalidDataException) { threw = true; }
    Check(threw, "truncated buffer is rejected");
});
Test("Process list parser and busiest-process ranking", () =>
{
    const long baseAddress = 0x10000;
    byte[] Record(int id, long create, long cpu, string name)
    {
        var record = new byte[88 + name.Length * 2];
        BitConverter.GetBytes(create).CopyTo(record, 32);
        BitConverter.GetBytes(cpu).CopyTo(record, 40);
        BitConverter.GetBytes((ushort)(name.Length * 2)).CopyTo(record, 56);
        BitConverter.GetBytes((long)id).CopyTo(record, 80);
        System.Text.Encoding.Unicode.GetBytes(name).CopyTo(record, 88);
        return record;
    }
    var first = Record(0, 0, 999, "");
    BitConverter.GetBytes((uint)first.Length).CopyTo(first, 0); // next-entry offset
    var second = Record(1234, 77, 5_000_000, "scan.exe");
    BitConverter.GetBytes(baseAddress + first.Length + 88).CopyTo(second, 64); // the name lives in the same buffer
    var entries = ProcessActivity.Parse(Concat(first, second), baseAddress);
    Check(entries.Count == 2 && entries[0].Name == "Idle" && entries[1].Name == "scan.exe" && entries[1].CpuTicks == 5_000_000, "parsed");
    var before = new[] { new ProcessActivity.Entry(1234, 77, "scan.exe", 1_000_000), new ProcessActivity.Entry(50, 1, "self", 0) };
    var after = new[] { new ProcessActivity.Entry(1234, 77, "scan.exe", 9_000_000), new ProcessActivity.Entry(50, 1, "self", 9_000_000),
        new ProcessActivity.Entry(0, 0, "Idle", 99_000_000), new ProcessActivity.Entry(77, 5, "new.exe", 2_000_000) };
    var top = ProcessActivity.Top(before, after, 2, 4, 5, excludeProcessId: 50);
    Check(top.Count == 2 && top[0] == new ProcessCpu("scan.exe", 1234, 10.0) && top[1].Name == "new.exe" && top[1].CpuPercent == 2.5,
        string.Join(", ", top));
});
Test("RAM buffers are fixed and the disk test file shrinks to fit free space", () =>
{
    Check(MemoryBenchmark.BufferBytes == 256 * 1024 * 1024 && MemoryBenchmark.RequiredAvailableBytes == 1536L * 1024 * 1024, "ram sizes");
    Check(DiskBenchmark.FileLimit(100L << 30) == 1L << 30, "plenty of space: 1 GiB");
    Check(DiskBenchmark.FileLimit((512L + 300) << 20) == 300L << 20, "low space: what is left after the reserve");
    Check(DiskBenchmark.FileLimit((512L + 63) << 20) == 0, "too little space to test");
});

Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
