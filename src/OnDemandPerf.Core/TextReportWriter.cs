using System.Globalization;
using System.Text;

namespace OnDemandPerf.Core;

public static class TextReportWriter
{
    public static string Format(AnalysisResult result)
    {
        // Every number in the report uses the invariant culture so reports read the same on every machine.
        var text = new StringBuilder();
        void Line(string label, object? value) => text.AppendLine(FormattableString.Invariant($"{label}: {value ?? "Unavailable"}"));
        static string? Number(double? value, string unit) => value.HasValue
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture) + unit : null;
        static string? Memory(ulong? bytes) => Number(bytes / (double?)(1024 * 1024 * 1024), " GiB");
        // The summary is what a support ticket needs; everything after DETAILS is for whoever investigates further.
        text.AppendLine("ONDEMAND PERF — SUMMARY (paste into a support ticket)");
        foreach (var line in ResultSummary.TicketSummary(result)) text.AppendLine(line);
        text.AppendLine();
        text.AppendLine("DETAILS");
        Line("Version", result.ApplicationVersion);
        Line("Date", result.Timestamp.ToString("O"));
        Line("Status", result.Status);
        Line("Validity", result.Validity == "Invalid"
            ? "NOT VALID: power saving was on; do not compare these numbers (see findings in the summary)" : result.Validity);
        Line("Computer", result.Device?.ComputerName);
        Line("OS", result.Device?.OperatingSystem);
        Line("Windows edition", result.Device?.WindowsEdition);
        Line("Windows release", result.Device?.WindowsRelease);
        Line("Windows build", result.Device?.WindowsBuild);
        Line("Architecture", result.Device?.Architecture);
        Line("CPU", result.Device?.CpuName);
        Line("Physical cores", result.Device?.PhysicalCores);
        Line("System logical processors", result.Device?.SystemLogicalProcessors);
        Line("Logical processors available to worker", result.Device?.LogicalProcessors);
        Line("CPU base clock", result.Device?.CpuBaseMhz is { } mhz ? $"{mhz} MHz" : null);
        text.AppendLine();
        text.AppendLine("HARDWARE");
        foreach (var line in HardwareSummary.Lines(result.Device, CultureInfo.InvariantCulture)) Line(line.Label, line.Text);
        if (result.Device?.InstalledMemory is { } installed)
            foreach (var module in installed.Modules)
                Line("RAM module " + (module.Slot ?? "?"),
                    string.Join(" · ", new[]
                    {
                        FormattableString.Invariant($"{module.SizeBytes / 1073741824d:F0} GiB {module.Type ?? "unknown type"} {module.FormFactor}").Trim(),
                        $"running {module.ConfiguredMts?.ToString() ?? "?"} / rated {module.RatedMts?.ToString() ?? "?"} MT/s",
                        $"{module.Manufacturer} {module.PartNumber}".Trim()
                    }.Where(part => part.Length > 0)));
        if (result.Device?.TestDrive is { } drive)
        {
            Line("Drive firmware", drive.Firmware);
            Line("Drive solid state", drive.IsSolidState is { } ssd ? (ssd ? "Yes" : "No") : null);
        }
        text.AppendLine();
        text.AppendLine("CONDITIONS BEFORE TEST");
        var environment = result.Environment;
        Line("RAM total (OS usable)", Memory(environment?.TotalPhysicalBytes));
        Line("RAM available", Memory(environment?.AvailablePhysicalBytes));
        Line("RAM used", Memory(environment?.UsedPhysicalBytes));
        Line("RAM utilization", Number(environment?.RamUtilizationPercent, "%"));
        Line("CPU activity", Number(environment?.CpuUtilizationPercent, "%"));
        Line("CPU sample duration", Number(environment?.CpuSampleDurationSeconds, " seconds"));
        Line("Power source", environment?.PowerSource);
        Line("Battery present", environment?.BatteryPresent is { } present ? (present ? "Yes" : "No") : null);
        if (environment?.BatteryPresent == true) Line("Battery charge", environment.BatteryChargePercent is { } charge ? $"{charge}%" : null);
        Line("Power plan", environment?.PowerPlan is null ? null : $"{environment.PowerPlan} ({environment.PowerPlanType})");
        Line("Power mode", environment?.PowerMode);
        Line("Energy saver", environment?.EnergySaverOn is { } saver ? (saver ? "On" : "Off") : null);
        Line("Maximum processor state", environment?.MaxProcessorStatePercent is { } maxState ? $"{maxState}%" : null);
        Line("Processor boost", environment?.ProcessorBoostMode is { } boost ? (boost == 0 ? "Disabled" : $"Enabled (mode {boost})") : null);
        Line("Committed memory", environment is { CommitBytes: { } commit, CommitLimitBytes: { } limit }
            ? $"{Memory(commit)} of {Memory(limit)} limit" : null);
        Line("Windows drive free", environment is { SystemDriveFreeBytes: { } free, SystemDriveTotalBytes: { } size }
            ? $"{environment.SystemDrive} {Memory(free)} of {Memory(size)}" : null);
        Line("Since last restart", Number(environment?.UptimeDays, " days"));
        Line("Restart pending", environment?.PendingRestart is { } pending ? (pending.Count == 0 ? "No" : string.Join(", ", pending)) : null);
        // The busiest processes only matter when the PC was busy; the JSON always keeps them.
        if (environment?.CpuUtilizationPercent > FindingRules.BusyBeforeTestPercent)
            foreach (var process in environment.TopProcesses)
                Line("Busy before test", FormattableString.Invariant($"{process.Name} (PID {process.ProcessId}) {process.CpuPercent:F1}% of all CPUs"));
        Line("Process efficiency-mode opt-out", environment?.ProcessPowerThrottling);
        text.AppendLine();
        Line("CPU benchmark profile", result.Cpu is null ? null : $"{result.Cpu.BenchmarkVersion} / {result.Cpu.ProfileHash}");
        Line("CPU stage status", result.Cpu?.Status);
        Line("CPU Compression (single-thread)", Number(result.Cpu?.SingleThread?.MBps, " MB/s"));
        Line("CPU Compression (multithread)", Number(result.Cpu?.MultiThread?.MBps, " MB/s"));
        Line("CPU single-thread median pass", Number(result.Cpu?.SingleThread?.MedianMBps, " MB/s"));
        Line("CPU multithread median pass", Number(result.Cpu?.MultiThread?.MedianMBps, " MB/s"));
        Line("Single-thread core placement", result.Cpu?.SingleThread?.CorePlacement);
        Line("Single-thread on fastest cores", Number(result.Cpu?.SingleThread?.OnFastestCoresPercent, "% of samples"));
        Line("Single-thread pass variation (CV)", Number(result.Cpu?.SingleThread?.VariationPercent, "%"));
        Line("Multithread pass variation (CV)", Number(result.Cpu?.MultiThread?.VariationPercent, "%"));
        Line("Multithread first-to-last change", Number(result.Cpu?.MultiThread?.FirstToLastChangePercent, "%"));
        Line("CPU stage duration", Number(result.Cpu?.DurationSeconds, " seconds"));
        Line("Compression output verified", result.Cpu is null ? "Not completed" : result.Cpu.OutputVerified ? "Yes" : "No");
        if (result.Cpu is not null)
            foreach (var note in result.Cpu.QualityNotes) Line("Measurement note", note);
        if (result.Memory is { } ram)
        {
            text.AppendLine(); text.AppendLine("RAM — APPLICATION THROUGHPUT");
            Line("Status", ram.Status);
            Line("RAM profile", $"{ram.BenchmarkVersion} / {ram.ProfileHash}");
            Line("Buffer size (each of two)", Number(ram.BufferBytes / 1048576d, " MiB"));
            Line("Memory Copy", Number(ram.CopyGBps, " GB/s"));
            Line("Memory Read", Number(ram.ReadGBps, " GB/s"));
            Line("Memory Write", Number(ram.WriteGBps, " GB/s"));
            Line("Single-worker core placement", ram.CorePlacement);
            Line("Single worker on fastest cores", Number(ram.OnFastestCoresPercent, "% of samples"));
            if (ram.MultiThreadWorkers is { } mtWorkers)
            {
                Line($"Memory Read, {mtWorkers} workers", Number(ram.MultiThreadReadGBps, " GB/s"));
                Line($"Memory Copy, {mtWorkers} workers", Number(ram.MultiThreadCopyGBps, " GB/s"));
            }
            foreach (var (operation, variation) in ram.VariationPercent) Line($"RAM {operation} pass variation (CV)", Number(variation, "%"));
            if (ram.Latency is { } latency)
            {
                Line("Application-observed memory latency", Number(latency.NanosecondsPerAccess, " ns/access"));
                Line("Latency status", latency.Status);
                Line("Latency working set", Number(latency.WorkingSetBytes / 1048576d, " MiB"));
                Line("Latency pass variation (CV)", Number(latency.VariationPercent, "%"));
                Line("Latency cycle verified", latency.CycleVerified);
                Line("Latency on fastest cores", Number(latency.OnFastestCoresPercent, "% of samples"));
                Line("Latency method", latency.Method);
            }
            Line("RAM duration", Number(ram.DurationSeconds, " seconds"));
            foreach (var note in ram.Notes) Line("RAM note", note);
        }
        if (result.Disk is { } disk)
        {
            text.AppendLine(); text.AppendLine("STORAGE — DIRECT I/O, QUEUE DEPTH 1");
            Line("Status", disk.Status); Line("Disk profile", $"{disk.BenchmarkVersion} / {disk.ProfileHash}"); Line("Test volume", disk.Volume); Line("File system", disk.FileSystem);
            Line("Test file", Number(disk.FileBytes / 1048576d, " MiB") + (disk.ReducedForFreeSpace ? " (reduced: low free space)" : ""));
            Line("Total written", Number(disk.TotalBytesWritten / 1048576d, " MiB"));
            foreach (var test in disk.Measurements)
            {
                Line(test.Operation, FormattableString.Invariant($"{Number(test.MBps, " MB/s")}; {test.Status}; {test.Operations} operations in {test.DurationSeconds:F3}s; {test.StopReason}"));
                if (test.FirstPassMBps is { } firstPass)
                    Line("  First full pass of test file", $"{Number(firstPass, " MB/s")} (file read {test.FilePasses?.ToString("F1", CultureInfo.InvariantCulture)} times in total)");
                if (test.ShortBurst) Line("  Sample quality", "Short burst (<1 second); not sustained performance");
                if (test.BlockBytes == 4096)
                    Line("  IOPS / average / p95 latency", FormattableString.Invariant($"{test.Iops:F0} / {test.MeanLatencyMs:F3} ms / {test.P95LatencyMs:F3} ms"));
            }
            Line("Disk duration", Number(disk.DurationSeconds, " seconds"));
            Line("Test file cleanup", disk.CleanupSucceeded ? "Complete" : "Failed");
            foreach (var note in disk.Notes) Line("Disk note", note);
        }
        if (result.Activity is { } activity)
        {
            text.AppendLine(); text.AppendLine("CONDITIONS DURING TEST");
            Line("Sampling", activity.Method);
            Line("Samples", activity.Samples.Count);
            var cpuSamples = activity.Samples.Where(s => s.SystemCpuPercent.HasValue).ToArray();
            double duration = cpuSamples.Sum(s => s.IntervalSeconds);
            Line("System CPU (time-weighted mean)", Number(duration > 0 ? cpuSamples.Sum(s => s.SystemCpuPercent!.Value * s.IntervalSeconds) / duration : null, "%"));
            Line("System CPU peak sample", Number(cpuSamples.Length > 0 ? cpuSamples.Max(s => s.SystemCpuPercent) : null, "%"));
            var memorySamples = activity.Samples.Where(s => s.AvailableMemoryBytes.HasValue).ToArray();
            Line("Lowest available RAM", Memory(memorySamples.Length > 0 ? memorySamples.Min(s => s.AvailableMemoryBytes) : null));
            Line("Power sources observed", string.Join(", ", activity.Samples.Select(s => s.PowerSource).Distinct()));
            var loaded = activity.Samples.Where(s => s.Stage == "CPU" && s.SystemCpuPercent >= 90 && s.ProcessorPerformancePercent.HasValue).ToArray();
            Line("CPU clock under full load", Number(loaded.Length > 0 ? loaded.Average(s => s.ProcessorPerformancePercent!.Value) : null, "% of base clock"));
            var limits = activity.Samples.Where(s => s.PerformanceLimitPercent.HasValue).ToArray();
            Line("Lowest Windows performance limit", Number(limits.Length > 0 ? limits.Min(s => s.PerformanceLimitPercent) : null, "%"));
            foreach (var note in activity.Notes) Line("Sampling note", note);
        }
        foreach (var warning in result.Warnings) Line("Warning", warning);
        foreach (var error in result.Errors) Line("Error", error);
        text.AppendLine();
        Line("Total analysis duration (before report output)", Number(result.DurationSeconds, " seconds"));
        return text.ToString();
    }
}

