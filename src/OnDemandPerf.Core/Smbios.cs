using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OnDemandPerf.Core;

/// <summary>
/// Reads machine and memory identity from the firmware's SMBIOS table (no administrator rights, no WMI). The parser takes a
/// plain byte array so it can be unit tested; every read is bounds-checked because firmware tables are untrusted input.
/// </summary>
internal static class Smbios
{
    private const uint RawSmbiosSignature = 0x52534D42; // 'RSMB'
    private const int RawHeaderBytes = 8;

    private static readonly string[] Placeholders =
    [
        "to be filled by o.e.m.", "to be filled by oem", "system product name", "system manufacturer", "system version",
        "default string", "not specified", "not applicable", "n/a", "none", "unknown", "o.e.m.", "oem", "undefined", "type1productconfigid"
    ];

    internal static (HardwareInfo Hardware, InstalledMemory Memory) Collect()
    {
        uint size = WindowsNative.GetSystemFirmwareTable(RawSmbiosSignature, 0, null, 0);
        if (size < RawHeaderBytes || size > 16 * 1024 * 1024)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SMBIOS table unavailable.");
        var raw = new byte[size];
        uint read = WindowsNative.GetSystemFirmwareTable(RawSmbiosSignature, 0, raw, size);
        if (read < RawHeaderBytes || read > size) throw new Win32Exception(Marshal.GetLastWin32Error(), "SMBIOS table could not be read.");
        uint declared = BitConverter.ToUInt32(raw, 4);
        if (declared == 0 || declared > read - RawHeaderBytes) throw new InvalidDataException("SMBIOS table length is inconsistent.");
        var table = new byte[declared];
        Array.Copy(raw, RawHeaderBytes, table, 0, declared);
        return Parse(table);
    }

    internal static (HardwareInfo Hardware, InstalledMemory Memory) Parse(byte[] table)
    {
        var hardware = new HardwareInfo();
        var memory = new InstalledMemory();
        string? boardManufacturer = null, boardProduct = null, systemVersion = null;
        int slots = 0;
        int offset = 0;
        while (offset + 4 <= table.Length)
        {
            byte type = table[offset];
            int length = table[offset + 1];
            if (length < 4 || offset + length > table.Length) break; // malformed: keep what was parsed so far
            int stringStart = offset + length;
            int stringEnd = stringStart;
            while (stringEnd + 1 < table.Length && !(table[stringEnd] == 0 && table[stringEnd + 1] == 0)) stringEnd++;
            var strings = ReadStrings(table, stringStart, Math.Min(stringEnd, table.Length));
            var entry = new Entry(table, offset, length, strings);
            string? Str(int field) => entry.Str(field);

            switch (type)
            {
                case 0:
                    hardware.BiosVendor = Str(4);
                    hardware.BiosVersion = Str(5);
                    hardware.BiosDate = Str(8);
                    break;
                case 1:
                    hardware.Manufacturer = Str(4);
                    hardware.Model = Str(5);
                    systemVersion = Str(6);
                    hardware.Family = Str(0x1A);
                    break;
                case 2:
                    boardManufacturer ??= Str(4);
                    boardProduct ??= Str(5);
                    break;
                case 17:
                    slots++;
                    var module = ParseMemoryDevice(entry);
                    if (module is not null) memory.Modules.Add(module);
                    break;
            }
            if (type == 127) break;
            offset = stringEnd + 2;
        }

        // Home-built desktops leave the system strings as placeholders; the motherboard identifies them instead.
        hardware.Manufacturer ??= boardManufacturer;
        hardware.Model ??= boardProduct;
        hardware.Family ??= systemVersion;

        foreach (var module in memory.Modules) memory.TotalBytes += module.SizeBytes;
        memory.ModuleCount = memory.Modules.Count;
        memory.SlotCount = slots > 0 ? slots : null;
        memory.Type = memory.Modules.Select(m => m.Type).FirstOrDefault(t => t is not null);
        memory.RatedMts = memory.Modules.Where(m => m.RatedMts is > 0).Select(m => m.RatedMts).Min();
        memory.ConfiguredMts = memory.Modules.Where(m => m.ConfiguredMts is > 0).Select(m => m.ConfiguredMts).Min();
        return (hardware, memory);
    }

    private static MemoryModuleInfo? ParseMemoryDevice(Entry e)
    {
        if (e.Length < 0x15) return null;
        ushort rawSize = e.Word(0x0C);
        ulong sizeBytes;
        if (rawSize is 0 or 0xFFFF) return null; // empty slot or unknown
        if (rawSize == 0x7FFF) sizeBytes = (ulong)(e.Dword(0x1C) & 0x7FFFFFFF) * 1024 * 1024;
        else if ((rawSize & 0x8000) != 0) sizeBytes = (ulong)(rawSize & 0x7FFF) * 1024;
        else sizeBytes = (ulong)rawSize * 1024 * 1024;
        if (sizeBytes == 0) return null;
        uint? Speed(int field, int extendedField)
        {
            uint value = e.Word(field);
            if (value == 0xFFFF) value = e.Dword(extendedField) & 0x7FFFFFFF;
            return value is 0 or 0xFFFF ? null : value;
        }
        return new MemoryModuleInfo
        {
            Slot = e.Str(0x10),
            SizeBytes = sizeBytes,
            Type = MemoryTypeName(e.Byte(0x12)),
            FormFactor = FormFactorName(e.Byte(0x0E)),
            RatedMts = Speed(0x15, 0x54),
            ConfiguredMts = Speed(0x20, 0x58),
            Manufacturer = e.Str(0x17),
            PartNumber = e.Str(0x1A)
        };
    }

    /// <summary>One SMBIOS structure; reads past the formatted area return 0/null instead of throwing.</summary>
    private sealed class Entry(byte[] table, int offset, int length, List<string> strings)
    {
        internal int Length => length;
        internal byte Byte(int field) => field < length ? table[offset + field] : (byte)0;
        internal ushort Word(int field) => field + 2 <= length ? BitConverter.ToUInt16(table, offset + field) : (ushort)0;
        internal uint Dword(int field) => field + 4 <= length ? BitConverter.ToUInt32(table, offset + field) : 0;
        internal string? Str(int field)
        {
            int index = Byte(field);
            return index > 0 && index <= strings.Count ? Clean(strings[index - 1]) : null;
        }
    }

    internal static string? MemoryTypeName(int code) => code switch
    {
        0x12 => "DDR", 0x13 => "DDR2", 0x14 => "DDR2 FB-DIMM", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR",
        0x1C => "LPDDR2", 0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x20 => "HBM", 0x21 => "HBM2", 0x22 => "DDR5",
        0x23 => "LPDDR5", 0x24 => "HBM3", _ => null
    };

    internal static string? FormFactorName(int code) => code switch
    {
        9 => "DIMM", 13 => "SODIMM", 15 => "FB-DIMM", 17 => "CAMM", 5 or 10 or 11 or 16 => "Soldered", _ => null
    };

    private static List<string> ReadStrings(byte[] table, int start, int end)
    {
        var list = new List<string>();
        int from = start;
        for (int i = start; i < end; i++)
        {
            if (table[i] != 0) continue;
            list.Add(Encoding.ASCII.GetString(table, from, i - from));
            from = i + 1;
        }
        if (from < end) list.Add(Encoding.ASCII.GetString(table, from, end - from));
        return list;
    }

    /// <summary>Trims, collapses whitespace and turns firmware placeholders ("To Be Filled By O.E.M.") into null.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Any(char.IsControl)) cleaned = new string(cleaned.Where(c => !char.IsControl(c)).ToArray());
        if (cleaned.Length == 0) return null;
        return Placeholders.Contains(cleaned, StringComparer.OrdinalIgnoreCase) ? null : cleaned;
    }
}
