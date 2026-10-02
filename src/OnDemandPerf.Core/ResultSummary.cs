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
        "Success" => result.Warnings.Count > 0 ? "Test complete — review warnings" : "Test complete",
        "Cancelled" => "Test cancelled",
        "Partial" => "Test complete — review incomplete tests",
        _ => "Test failed"
    };

    public static CardValue CpuCard(AnalysisResult result)
    {
        if (result.Cpu is not { } cpu) return new("waiting", "—", "Not completed");
        if (cpu.Status == "Success" && cpu.MultiThread is { } multi)
            return new("success", multi.MBps.ToString("N1", CultureInfo.CurrentCulture), "Multi · MB/s");
        return new("partial", "—", cpu.Status == "Success" ? "Not completed" : cpu.Status);
    }

    public static CardValue RamCard(AnalysisResult result)
    {
        if (result.Memory is not { } memory) return new("waiting", "—", "Not completed");
        if (memory.Status == "Success" && memory.CopyGBps is { } copy)
            return new("success", copy.ToString("N1", CultureInfo.CurrentCulture), "Copy · GB/s");
        return new("partial", "—", memory.Status == "Success" ? "Not completed" : memory.Status);
    }

    public static CardValue DiskCard(AnalysisResult result)
    {
        if (result.Disk is not { } disk) return new("waiting", "—", "Not completed");
        var read = disk.Measurements.FirstOrDefault(m => m.Operation == "SequentialRead");
        if (disk.Status == "Success" && read is not null)
        {
            // Prefer the first full pass of the file: later passes can revisit cached data.
            double value = read.FirstPassMBps ?? read.MBps;
            return new("success", value.ToString("N1", CultureInfo.CurrentCulture), "Seq read · MB/s");
        }
        return new("partial", "—", disk.Status == "Success" ? "Not completed" : disk.Status);
    }

    private static string Number(double? value) => value is { } v ? v.ToString("F1", CultureInfo.CurrentCulture) : "—";

    public static IReadOnlyList<string> Lines(AnalysisResult result)
    {
        var lines = new List<string>();
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
