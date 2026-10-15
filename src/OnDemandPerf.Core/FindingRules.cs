using System.Globalization;

namespace OnDemandPerf.Core;

/// <summary>
/// Turns what a run collected into plain-language findings for the panel, report and support tickets. A power-saving plan,
/// power mode or energy saver makes the run Invalid; everything else is a Warning that names a common cause of slowness.
/// Thresholds are provisional, like the measurement-quality notes, and are listed in docs/methodology.md.
/// </summary>
public static class FindingRules
{
    public const string Invalid = "Invalid";
    public const string Warning = "Warning";

    internal const double BusyBeforeTestPercent = 20;
    internal const double BelowBaseClockPercent = 80;
    internal const double UptimeDays = 7;
    internal const double LowFreeDrivePercent = 10;
    internal const ulong LowFreeDriveBytes = 10UL * 1024 * 1024 * 1024;
    internal const ulong LowAvailableRamBytes = 1024UL * 1024 * 1024;
    internal const double LowAvailableRamPercent = 10;
    internal const double HighCommitPercent = 90;
    internal const ulong SmallRamBytes = 6UL * 1024 * 1024 * 1024;

    /// <summary>Replaces the run's findings and sets its validity.</summary>
    public static void Apply(AnalysisResult result)
    {
        result.Findings.Clear();
        result.Findings.AddRange(Evaluate(result));
        result.Validity = result.Findings.Any(f => f.Severity == Invalid) ? "Invalid" : "Valid";
    }

    public static List<Finding> Evaluate(AnalysisResult result)
    {
        var findings = new List<Finding>();
        void Add(string severity, string area, FormattableString message) =>
            findings.Add(new(severity, area, message.ToString(CultureInfo.InvariantCulture)));
        var environment = result.Environment;

        // ---- power: the only conditions that invalidate a run ----
        if (environment is not null)
        {
            string plan = environment.PowerPlan ?? "Unknown";
            bool namedSaver = plan.Contains("saver", StringComparison.OrdinalIgnoreCase);
            if (environment.PowerPlanType == "PowerSaver" || (environment.PowerPlanType == "Custom" && namedSaver))
                Add(Invalid, "Power", $"Power plan is \"{plan}\". Switch to Balanced or High performance and run the test again.");
            else if (environment.PowerPlanType == "Custom" && !plan.Contains("balanced", StringComparison.OrdinalIgnoreCase) &&
                     !plan.Contains("performance", StringComparison.OrdinalIgnoreCase))
                Add(Warning, "Power", $"Custom power plan \"{plan}\": make sure it is not a power-saving plan. Results are comparable only on Balanced or High performance.");
            // Windows applies the power mode only on top of the Balanced plan or one derived from it.
            if (environment.PowerPlanType is "Balanced" or "Custom" && environment.PowerMode is "BestPowerEfficiency" or "BatterySaver")
                Add(Invalid, "Power", $"Windows power mode is \"{(environment.PowerMode == "BatterySaver" ? "Battery saver" : "Best power efficiency")}\" (Settings > System > Power). Set it to Balanced or Best performance and run the test again.");
            if (environment.EnergySaverOn == true)
                Add(Invalid, "Power", $"Energy saver (battery saver) is on, which slows the CPU. Turn it off and run the test again.");
        }
        var battery = environment?.PowerSource == "Battery";
        var unplugged = !battery && result.Activity?.Samples.Any(s => s.PowerSource == "Battery") == true;
        if (battery)
            Add(Warning, "Power", $"Running on battery{(environment!.BatteryChargePercent is { } charge ? $" ({charge}% charged)" : "")}. Laptops usually run slower on battery: compare only with other battery runs, or plug in and run again.");
        else if (unplugged)
            Add(Warning, "Power", $"The charger was unplugged during the test, so later results may be lower. Plug in and run again.");
        if (environment?.MaxProcessorStatePercent is < 100 and var maxState)
            Add(Warning, "Power", $"The power plan limits the CPU to {maxState}% (Maximum processor state), so it cannot reach full speed.");
        if (environment?.ProcessorBoostMode == 0)
            Add(Warning, "Power", $"The power plan turns off processor boost (turbo), so the CPU stays at or below its base clock.");

        // ---- CPU ----
        var loaded = result.Activity?.Samples
            .Where(s => s.Stage == "CPU" && s.SystemCpuPercent >= 90 && s.ProcessorPerformancePercent.HasValue).ToArray() ?? [];
        if (loaded.Length >= 2)
        {
            double clock = loaded.Average(s => s.ProcessorPerformancePercent!.Value);
            if (clock < BelowBaseClockPercent)
            {
                string ghz = result.Device?.CpuBaseMhz is { } mhz
                    ? FormattableString.Invariant($" (about {mhz * clock / 100_000:F1} GHz against a {mhz / 1000d:F1} GHz base)") : "";
                Add(Warning, "CPU", $"Under full load the CPU ran at about {clock:F0}% of its base clock{ghz}. This usually means it is overheating or held back by a power limit: check vents, fans and dust.");
            }
        }
        if (environment?.CpuUtilizationPercent is > BusyBeforeTestPercent and var busy)
        {
            string top = environment.TopProcesses.Count == 0 ? ""
                : "; busiest: " + string.Join(", ", environment.TopProcesses.Take(3).Select(p => FormattableString.Invariant($"{p.Name} {p.CpuPercent:F0}%")));
            Add(Warning, "CPU", $"The PC was already {busy:F0}% busy before the test started{top}. Background work slows the PC and lowers these results.");
        }
        if (result.Cpu?.MultiThread is { } multi && CpuBenchmark.UtilizationNote("Multithread", multi) is not null)
            Add(Warning, "CPU", $"The CPU test got much less processor time than it asked for (efficiency mode, a CPU limit or other software). The CPU score may understate the machine.");
        if (result.Cpu?.SingleThread?.OnFastestCoresPercent is < 90 and var share)
            Add(Warning, "CPU", $"The single-thread test ran on the fastest cores only {share:F0}% of the time, so its score may understate the machine.");

        // ---- RAM ----
        if (environment is { TotalPhysicalBytes: { } total, AvailablePhysicalBytes: { } available } && total > 0)
        {
            if (total < SmallRamBytes)
                Add(Warning, "RAM", $"Only {total / 1073741824d:F1} GB of RAM is usable. Windows 11 and modern apps are often slow with this little memory.");
            if (available < LowAvailableRamBytes || 100d * available / total < LowAvailableRamPercent)
                Add(Warning, "RAM", $"Only {available / 1073741824d:F1} GB of {total / 1073741824d:F1} GB RAM was free before the test. Windows is likely moving memory to disk, which makes everything slower.");
        }
        if (environment is { CommitBytes: { } commit, CommitLimitBytes: { } limit } && limit > 0 && 100d * commit / limit > HighCommitPercent)
            Add(Warning, "RAM", $"Memory in use is {100d * commit / limit:F0}% of the limit ({commit / 1073741824d:F1} of {limit / 1073741824d:F1} GB, RAM plus page file). The PC is running out of memory.");
        if (result.Memory is { Status: "Skipped" } skippedRam)
            Add(Warning, "RAM", $"RAM test skipped: {skippedRam.Notes.FirstOrDefault() ?? "not enough free memory"}");

        // ---- disk ----
        if (environment is { SystemDriveFreeBytes: { } free, SystemDriveTotalBytes: { } size } && size > 0 &&
            (free < LowFreeDriveBytes || 100d * free / size < LowFreeDrivePercent))
            Add(Warning, "Disk", $"{environment.SystemDrive ?? "The Windows drive"} has only {free / 1073741824d:F1} GB free ({100d * free / size:F0}%). A nearly full drive slows Windows and blocks updates.");
        if (result.Disk is { Status: "Skipped" } skippedDisk)
            Add(Warning, "Disk", $"Disk test skipped: {skippedDisk.Notes.FirstOrDefault() ?? "not enough free space"}");
        else if (result.Disk?.ReducedForFreeSpace == true)
            Add(Warning, "Disk", $"Low free space limited the disk test file to {result.Disk.FileLimitBytes / 1048576} MiB, so disk numbers are not comparable with full-size runs.");

        // ---- system ----
        if (environment?.UptimeDays is > UptimeDays and var days)
            Add(Warning, "System", $"Windows has not restarted for {days:F0} days. Use Restart (with Fast Startup on, Shut down does not count) and test again if the PC is still slow.");
        if (environment?.PendingRestart is { Count: > 0 } reasons)
            Add(Warning, "System", $"A restart is pending ({string.Join(", ", reasons)}). Updates may be working in the background until the PC restarts.");

        // Hardware hints read "RAM: ..." or "Disk: ..."; the prefix becomes the area.
        foreach (var hint in HardwareSummary.Hints(result.Device))
        {
            int colon = hint.IndexOf(": ", StringComparison.Ordinal);
            findings.Add(colon > 0 && hint.Length > colon + 2
                ? new(Warning, hint[..colon], char.ToUpperInvariant(hint[colon + 2]) + hint[(colon + 3)..])
                : new(Warning, "Hardware", hint));
        }
        return findings.OrderBy(f => f.Severity == Invalid ? 0 : 1).ToList(); // stable: keeps area order within a severity
    }
}
