namespace OnDemandPerf.Core;

/// <summary>Machine identity from SMBIOS. Values the firmware leaves blank or as placeholders are null.</summary>
public sealed class HardwareInfo
{
    public string? Manufacturer { get; set; }
    /// <summary>Product name; on OEM laptops often a part number such as "21HD0012US".</summary>
    public string? Model { get; set; }
    /// <summary>Marketing name or family when the firmware provides one, e.g. "ThinkPad T14 Gen 4".</summary>
    public string? Family { get; set; }
    public string? BiosVendor { get; set; }
    public string? BiosVersion { get; set; }
    public string? BiosDate { get; set; }
}

public sealed class MemoryModuleInfo
{
    public string? Slot { get; set; }
    public ulong SizeBytes { get; set; }
    /// <summary>"DDR4", "DDR5", "LPDDR5" and so on; null when the firmware reports unknown.</summary>
    public string? Type { get; set; }
    /// <summary>"DIMM", "SODIMM", "CAMM" or "Soldered"; null when unknown.</summary>
    public string? FormFactor { get; set; }
    /// <summary>Speed the module is rated for, in MT/s.</summary>
    public uint? RatedMts { get; set; }
    /// <summary>Speed the module is actually running at, in MT/s.</summary>
    public uint? ConfiguredMts { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
}

public sealed class InstalledMemory
{
    public string? Type { get; set; }
    public ulong TotalBytes { get; set; }
    public int ModuleCount { get; set; }
    /// <summary>Populated plus empty slots reported by the firmware; null when there is no module data.</summary>
    public int? SlotCount { get; set; }
    public uint? ConfiguredMts { get; set; }
    public uint? RatedMts { get; set; }
    public List<MemoryModuleInfo> Modules { get; init; } = [];
}

/// <summary>The drive that holds the disk-test file.</summary>
public sealed class DriveIdentity
{
    public string? Volume { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    /// <summary>"NVMe", "SATA", "USB", "RAID", "SAS", ... or "Unknown".</summary>
    public string? BusType { get; set; }
    /// <summary>NVMe specification version the controller reports, e.g. "1.4".</summary>
    public string? NvmeVersion { get; set; }
    /// <summary>True for solid-state, false for a spinning disk, null when the device does not say.</summary>
    public bool? IsSolidState { get; set; }
    /// <summary>Capacity of the volume that holds the test file (not the raw disk, which needs administrator access to read).</summary>
    public ulong? SizeBytes { get; set; }
}
