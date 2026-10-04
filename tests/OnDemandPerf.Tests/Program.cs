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
        Check(loaded.RunId == original.RunId && loaded.SchemaVersion == "2.1", "identity");
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
Test("ResultSummary notification names only the stages that produced a score", () =>
{
    var r = Sample();
    Check(ResultSummary.Notification(r) == "Test complete. CPU 1,588.6 MB/s · RAM 5.9 GB/s · Disk 1,800.5 MB/s", ResultSummary.Notification(r));
    r.Status = "Partial"; r.Disk!.Status = "TimedOut"; r.Memory = null;
    string partial = ResultSummary.Notification(r);
    Check(partial.StartsWith("Test complete — review incomplete tests. CPU") && !partial.Contains("RAM") && !partial.Contains('—' + " MB/s"), partial);
    r.Status = "Failed"; r.Cpu = null; r.Disk = null;
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
    Check(full.Contains("CPU benchmark profile: deflate-v3 / " + CpuBenchmark.Profile), "profile line");
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
    Check(report.Contains("HARDWARE") && report.Contains("RAM module DIMM A") && report.Contains("Hardware note"), "report sections");
    Check(ResultSummary.Lines(loaded).Any(l => l.StartsWith("Note — ")), "hints in summary");
});

Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
