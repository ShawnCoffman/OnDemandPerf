using System.Globalization;

namespace OnDemandPerf.Core;

/// <summary>One row of the "this machine" block: a short label and a single line of text.</summary>
public sealed record SystemLine(string Label, string Text);

/// <summary>
/// Presentation-neutral description of the machine for the top of the panel and the report. Lives in Core so the wording is
/// unit tested and the tray never formats hardware identity itself.
/// </summary>
public static class HardwareSummary
{
    private const string Unavailable = "Unavailable";
    private const string Separator = " · ";

    public static IReadOnlyList<SystemLine> Lines(DeviceInfo? device) =>
    [
        new("SYSTEM", Machine(device)),
        new("CPU", Cpu(device)),
        new("RAM", Ram(device?.InstalledMemory)),
        new("DISK", Disk(device?.TestDrive))
    ];

    public static string Machine(DeviceInfo? device)
    {
        var hardware = device?.Hardware;
        if (hardware is null || (hardware.Manufacturer is null && hardware.Model is null && hardware.Family is null))
            return device is null ? Unavailable : device.ComputerName;
        // OEM laptops put a part number in the model ("21HD0012US") and the marketing name in the family.
        string? name = hardware.Model, secondary = null;
        if (hardware.Family is not null && (name is null || !name.Contains(' ')))
        {
            if (name is not null && !hardware.Family.Contains(name, StringComparison.OrdinalIgnoreCase)) secondary = name;
            name = hardware.Family;
        }
        if (hardware.Manufacturer is not null && name is not null && !name.Contains(hardware.Manufacturer, StringComparison.OrdinalIgnoreCase))
            name = hardware.Manufacturer + " " + name;
        name ??= hardware.Manufacturer;
        var parts = new List<string> { name ?? Unavailable };
        if (secondary is not null) parts.Add(secondary);
        if (hardware.BiosVersion is not null)
            parts.Add("BIOS " + hardware.BiosVersion + (hardware.BiosDate is null ? "" : $" ({hardware.BiosDate})"));
        return string.Join(Separator, parts);
    }

    public static string Cpu(DeviceInfo? device)
    {
        if (device is null || device.CpuName == "Unavailable") return Unavailable;
        string name = device.CpuName.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        int clock = name.IndexOf('@');
        if (clock > 0) name = name[..clock];
        name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var parts = new List<string> { name };
        uint threads = device.SystemLogicalProcessors ?? (uint)device.LogicalProcessors;
        parts.Add(device.PhysicalCores is { } cores ? $"{cores} cores / {threads} threads" : $"{threads} threads");
        if (device.CpuBaseMhz is { } mhz)
            parts.Add((mhz / 1000d).ToString("F1", CultureInfo.CurrentCulture) + " GHz base");
        return string.Join(Separator, parts);
    }

    public static string Ram(InstalledMemory? memory)
    {
        if (memory is null || memory.ModuleCount == 0) return "Module details unavailable";
        var parts = new List<string>();
        string? type = memory.Type;
        uint? speed = memory.ConfiguredMts ?? memory.RatedMts;
        string headline = FormatBytes(memory.TotalBytes);
        if (type is not null) headline += " " + type + (speed is { } s ? "-" + s : "");
        else if (speed is { } only) headline += " " + only + " MT/s";
        parts.Add(headline);
        var sizes = memory.Modules.Select(m => m.SizeBytes).Distinct().ToList();
        parts.Add(sizes.Count == 1 ? $"{memory.ModuleCount} × {FormatBytes(sizes[0])}" : $"{memory.ModuleCount} modules (mixed sizes)");
        if (memory.Modules.All(m => m.FormFactor == "Soldered")) parts.Add("soldered");
        else if (memory.SlotCount is { } slots && slots >= memory.ModuleCount) parts.Add($"{memory.ModuleCount} of {slots} slots");
        if (IsBelowRated(memory)) parts.Add($"rated {memory.RatedMts}");
        return string.Join(Separator, parts);
    }

    public static string Disk(DriveIdentity? drive)
    {
        if (drive is null || (drive.Model is null && drive.BusType is null)) return Unavailable;
        var parts = new List<string> { drive.Model ?? "Unknown model" };
        string? bus = drive.BusType is "Unknown" ? null : drive.BusType;
        if (bus == "NVMe") parts.Add(drive.NvmeVersion is null ? "NVMe" : "NVMe " + drive.NvmeVersion);
        else
        {
            string kind = drive.IsSolidState switch { true => "SSD", false => "HDD", _ => "" };
            parts.Add(string.Join(' ', new[] { bus, kind }.Where(x => !string.IsNullOrEmpty(x))));
        }
        string? volume = drive.Volume?.TrimEnd('\\');
        if (drive.SizeBytes is { } size) parts.Add(volume is null ? FormatCapacity(size) : $"{volume} ({FormatCapacity(size)})");
        else if (volume is not null) parts.Add(volume);
        return string.Join(Separator, parts.Where(p => p.Length > 0));
    }

    /// <summary>Plain-language observations from identity data alone. They are prompts to look closer, not diagnoses.</summary>
    public static IReadOnlyList<string> Hints(DeviceInfo? device)
    {
        var hints = new List<string>();
        if (device?.InstalledMemory is { ModuleCount: > 0 } memory)
        {
            if (memory.ModuleCount == 1 && memory.SlotCount is > 1 && memory.Modules[0].FormFactor != "Soldered")
                hints.Add("RAM: a single module in a multi-slot machine usually means single-channel memory and lower bandwidth.");
            if (IsBelowRated(memory))
                hints.Add($"RAM: running at {memory.ConfiguredMts} MT/s but rated for {memory.RatedMts}; an XMP/EXPO profile may be off.");
        }
        if (device?.TestDrive is { } drive)
        {
            if (drive.IsSolidState == false)
                hints.Add("Disk: the test drive is a spinning hard disk, so slow random reads are expected.");
            else if (drive.BusType == "USB")
                hints.Add("Disk: the test drive is connected over USB, which limits its speed.");
            else if (drive.BusType == "NVMe" && drive.NvmeVersion is null && drive.Model is null)
                hints.Add("Disk: the NVMe drive did not report its identity.");
        }
        return hints;
    }

    private static bool IsBelowRated(InstalledMemory memory) =>
        memory.ConfiguredMts is { } configured && memory.RatedMts is { } rated && configured * 100 < rated * 95L;

    private static string FormatBytes(ulong bytes)
    {
        double gib = bytes / 1073741824d;
        return (gib >= 1 ? Math.Round(gib).ToString("F0", CultureInfo.CurrentCulture) + " GB"
            : Math.Round(bytes / 1048576d).ToString("F0", CultureInfo.CurrentCulture) + " MB");
    }

    private static string FormatCapacity(ulong bytes)
    {
        double gb = bytes / 1e9;
        return gb >= 1000 ? (gb / 1000).ToString("0.#", CultureInfo.CurrentCulture) + " TB"
            : Math.Round(gb).ToString("F0", CultureInfo.CurrentCulture) + " GB";
    }
}
