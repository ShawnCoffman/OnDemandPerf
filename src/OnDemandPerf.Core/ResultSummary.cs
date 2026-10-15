using System.Globalization;

namespace OnDemandPerf.Core;

/// <summary>What one stage card shows: State is "success", "partial" or "waiting"; the UI maps it to colors.</summary>
public sealed record CardValue(string State, string Value, string Caption);

/// <summary>
/// Presentation-neutral summary of a result, in Core so it is unit tested and the tray never parses JSON by hand.
/// </summary>
public static class ResultSummary
{
    public static string Headline(AnalysisResult result) => result.Status switch
    {
        "Cancelled" => "Test cancelled",
        "Failed" => "Test failed",
        // A power-saving mode makes the numbers meaningless for comparison, whatever else happened.
        _ when result.Validity == "Invalid" => "Test not valid — power saving was on",
        "Success" when result.Environment?.PowerSource == "Battery" => "Test complete — on battery power",
        "Success" when result.Findings.Count > 0 => "Test complete — review findings",
        "Success" => result.Warnings.Count > 0 ? "Test complete — review warnings" : "Test complete",
        "Partial" => "Test complete — review incomplete tests",
        _ => "Test failed"
    };

    /// <summary>One line per finding, "Area: message", most serious first; invalidating ones are marked.</summary>
    public static IReadOnlyList<string> FindingLines(AnalysisResult result) =>
        result.Findings.Select(f => (f.Severity == FindingRules.Invalid ? "NOT VALID — " : "") + f.Area + ": " + f.Message).ToList();

    public static CardValue CpuCard(AnalysisResult result)
    {
        if (result.Cpu is not { } cpu) return new("waiting", "—", "Not completed");
        // Single-thread speed is what responsiveness depends on; the multithread score stays in the report.
        if (cpu.SingleThread is { } single)
            return new("success", single.MBps.ToString("N1", CultureInfo.CurrentCulture), "Single · MB/s");
        return new("partial", "—", cpu.Status == "Success" ? "Not completed" : cpu.Status);
    }

    public static CardValue RamCard(AnalysisResult result)
    {
        if (result.Memory is not { } memory) return new("waiting", "—", "Not completed");
        // The multithread read is the figure that responds to single- versus dual-channel memory.
        if (memory.Status == "Success" && memory.MultiThreadReadGBps is { } multiRead)
            return new("success", multiRead.ToString("N1", CultureInfo.CurrentCulture), "Multi read · GB/s");
        if (memory.Status == "Success" && memory.ReadGBps is { } read)
            return new("success", read.ToString("N1", CultureInfo.CurrentCulture), "Read · GB/s");
        return new("partial", "—", memory.Status == "Success" ? "Not completed" : memory.Status);
    }

    public static CardValue DiskCard(AnalysisResult result)
    {
        if (result.Disk is not { } disk) return new("waiting", "—", "Not completed");
        // Small random reads at queue depth 1 are what opening apps and files feels like; sequential speed stays in the report.
        var random = disk.Measurements.FirstOrDefault(m => m.Operation == "RandomRead" && m.Status == "Success");
        if (disk.Status == "Success" && random is not null)
            return new("success", random.Iops.ToString("N0", CultureInfo.CurrentCulture), "4K random · IOPS");
        return new("partial", "—", disk.Status == "Success" ? "Not completed" : disk.Status);
    }

    /// <summary>One line for a tray notification: the headline plus the card values that completed.</summary>
    public static string Notification(AnalysisResult result)
    {
        var scores = new[] { ("CPU", CpuCard(result), "MB/s"), ("RAM", RamCard(result), "GB/s"), ("Disk", DiskCard(result), "IOPS") }
            .Where(s => s.Item2.State == "success")
            .Select(s => $"{s.Item1} {s.Item2.Value} {s.Item3}")
            .ToList();
        return scores.Count == 0 ? Headline(result) : $"{Headline(result)}. {string.Join(" · ", scores)}";
    }

    /// <summary>
    /// A short, paste-ready block for a support ticket: who, whether the run counts, the headline and secondary numbers, and the
    /// findings. Invariant culture, so every ticket reads the same; the panel's Copy Summary and the top of the report use it.
    /// </summary>
    public static IReadOnlyList<string> TicketSummary(AnalysisResult result)
    {
        var c = CultureInfo.InvariantCulture;
        string N(double? value, string format, string unit) => value is { } v ? v.ToString(format, c) + unit : "not completed";
        var device = result.Device;
        var environment = result.Environment;
        var randomRead = result.Disk?.Measurements.FirstOrDefault(m => m.Operation == "RandomRead" && m.Status == "Success");
        var sequentialRead = result.Disk?.Measurements.FirstOrDefault(m => m.Operation == "SequentialRead");
        var lines = new List<string>
        {
            string.Join(" · ", new[] { "OnDemand Perf " + result.ApplicationVersion, result.Timestamp.ToString("yyyy-MM-dd HH:mm", c), device?.ComputerName }
                .Where(p => !string.IsNullOrEmpty(p))),
            "Result: " + Headline(result) + (result.Validity == "Invalid" ? " (NOT VALID: do not compare these numbers)" : ""),
            "System: " + HardwareSummary.Machine(device),
            "CPU: " + HardwareSummary.Cpu(device, c),
            "RAM: " + HardwareSummary.Ram(device?.InstalledMemory, c),
            "Disk: " + HardwareSummary.Disk(device?.TestDrive, c),
            "Power: " + string.Join(" · ", new[]
            {
                environment?.PowerSource is "Battery" && environment.BatteryChargePercent is { } charge ? $"Battery ({charge}%)" : environment?.PowerSource,
                environment?.PowerPlan is { } plan ? "plan " + plan : null,
                // The power mode only applies on top of Balanced (or a custom plan derived from it).
                environment?.PowerPlanType is "Balanced" or "Custom" && environment.PowerMode is { } mode ? "mode " + mode : null
            }.Where(p => p is not null)),
            $"Scores: CPU single-thread {N(result.Cpu?.SingleThread?.MBps, "N1", " MB/s")} · RAM multithread read {N(result.Memory?.MultiThreadReadGBps, "N1", " GB/s")} · Disk 4K random read {N(randomRead?.Iops, "N0", " IOPS")}",
            $"Also: CPU multithread {N(result.Cpu?.MultiThread?.MBps, "N1", " MB/s")} · RAM latency {N(result.Memory?.Latency?.NanosecondsPerAccess, "F1", " ns")} · Disk sequential read {N(sequentialRead?.FirstPassMBps ?? sequentialRead?.MBps, "N0", " MB/s")}"
        };
        if (result.Errors.Count > 0) lines.Add("Errors: " + string.Join("; ", result.Errors));
        var findings = FindingLines(result);
        lines.Add(findings.Count == 0 ? "Findings: none" : $"Findings ({findings.Count}):");
        lines.AddRange(findings.Select(f => "- " + f));
        return lines;
    }

    private static string Number(double? value) => value is { } v ? v.ToString("F1", CultureInfo.CurrentCulture) : "—";

    public static IReadOnlyList<string> Lines(AnalysisResult result)
    {
        var lines = new List<string>();
        if (result.Findings.Count > 0)
        {
            lines.Add("FINDINGS");
            lines.AddRange(FindingLines(result));
            lines.Add("");
        }
        // Results saved before findings existed still show their hardware hints.
        else foreach (var hint in HardwareSummary.Hints(result.Device)) lines.Add("Note — " + hint);
        if (result.Cpu is { } cpu)
        {
            if (cpu.SingleThread is { } single) lines.Add($"CPU single-thread: {Number(single.MBps)} MB/s");
            if (cpu.MultiThread is { } multi) lines.Add($"CPU multithread: {Number(multi.MBps)} MB/s");
            if (cpu.Status != "Success") lines.Add("CPU: " + cpu.Status);
        }
        if (result.Memory is { } memory)
        {
            lines.Add($"RAM: copy/read/write {Number(memory.CopyGBps)} / {Number(memory.ReadGBps)} / {Number(memory.WriteGBps)} GB/s");
            if (memory.MultiThreadWorkers is { } workers)
                lines.Add($"RAM, {workers} workers: read/copy {Number(memory.MultiThreadReadGBps)} / {Number(memory.MultiThreadCopyGBps)} GB/s");
            if (memory.Latency?.NanosecondsPerAccess is { } ns) lines.Add($"RAM dependent-access latency: {Number(ns)} ns");
            if (memory.Status != "Success") lines.Add("RAM: " + memory.Status);
        }
        if (result.Disk is { } disk)
        {
            if (disk.Status != "Success") lines.Add("Disk: " + disk.Status);
            foreach (var m in disk.Measurements.OrderBy(x => x.Operation switch { "RandomRead" => 0, "SequentialRead" => 1, _ => 2 }))
            {
                string label = m.Operation switch
                {
                    "SequentialWrite" => "sequential write", "SequentialRead" => "sequential read",
                    "RandomRead" => "random read", "RandomWrite" => "random write", _ => "measurement"
                };
                string note = m.Status == "Success" ? "" : $" ({m.Status})";
                if (m.ShortBurst) note += " · short burst";
                if (m.Operation == "RandomRead")
                    lines.Add($"Disk random read: {m.Iops.ToString("N0", CultureInfo.CurrentCulture)} IOPS · p95 {m.P95LatencyMs.ToString("F3", CultureInfo.CurrentCulture)} ms{note}");
                else if (m.Operation == "SequentialRead" && m.FirstPassMBps is { } first)
                    lines.Add($"Disk: sequential read {Number(first)} MB/s (first full pass){note}");
                else lines.Add($"Disk: {label} {Number(m.MBps)} MB/s{note}");
            }
        }
        foreach (var error in result.Errors) lines.Add("Error: " + error);
        if (result.Warnings.Count > 0) lines.Add($"{result.Warnings.Count} warning(s). Open Results for details.");
        lines.Add("");
        lines.Add("Open Results for the full report, measurement notes and test conditions.");
        return lines;
    }
}
