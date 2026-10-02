using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OnDemandPerf.Core;

/// <summary>
/// Identifies the physical drive behind the disk-test directory: model, firmware, bus type, SSD or HDD and, for NVMe, the
/// specification version the controller reports. Uses read-only device queries through a zero-access handle, so no
/// administrator rights are needed. The parsers take byte arrays so they can be unit tested without hardware.
/// </summary>
internal static class StorageIdentity
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const int PropertyDevice = 0, PropertySeekPenalty = 7, PropertyAdapterProtocol = 49;

    internal static DriveIdentity Collect(string directory, List<string> warnings)
    {
        var identity = new DriveIdentity();
        string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? "";
        if (root.Length < 2 || root[1] != ':') throw new NotSupportedException("The test path is not on a local drive letter.");
        identity.Volume = root[..2] + "\\";

        using var volume = Open(@"\\.\" + root[..2]);
        SafeFileHandle? disk = null;
        try
        {
            // Resolve volume -> physical disk so the queries describe the drive, not a partition-level stand-in.
            try
            {
                var number = Ioctl(volume, IoctlStorageGetDeviceNumber, null, 12, out int got);
                if (got >= 12 && BitConverter.ToUInt32(number, 0) == 7) // FILE_DEVICE_DISK
                    disk = Open($@"\\.\PhysicalDrive{BitConverter.ToUInt32(number, 4)}");
            }
            catch (Win32Exception ex)
            {
                warnings.Add("Drive identity: could not map the volume to a physical disk; querying the volume directly. " + ex.Message);
            }
            var target = disk ?? volume;

            SystemInventory.TryCollect("Drive model", warnings, () =>
            {
                var buffer = Ioctl(target, IoctlStorageQueryProperty, PropertyQuery(PropertyDevice), 1024, out int got);
                ParseDeviceDescriptor(buffer, got, identity);
            });
            SystemInventory.TryCollect("Drive type (SSD/HDD)", warnings, () =>
            {
                var buffer = Ioctl(target, IoctlStorageQueryProperty, PropertyQuery(PropertySeekPenalty), 64, out int got);
                if (got >= 9) identity.IsSolidState = buffer[8] == 0; // no seek penalty means solid state
            });
            // Raw disk length needs read access to the physical drive (administrator); the volume's size does not.
            SystemInventory.TryCollect("Volume size", warnings, () =>
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) identity.SizeBytes = (ulong)drive.TotalSize;
            });
            if (identity.BusType == "NVMe")
                SystemInventory.TryCollect("NVMe version", warnings, () =>
                {
                    var buffer = Ioctl(target, IoctlStorageQueryProperty, NvmeIdentifyQuery(), 8 + 40 + 4096, out int got);
                    var (version, model, firmware) = ParseNvmeIdentify(buffer, got);
                    identity.NvmeVersion = version;
                    identity.Model ??= model;
                    identity.Firmware ??= firmware;
                });
        }
        finally { disk?.Dispose(); }
        return identity;
    }

    private static SafeFileHandle Open(string path)
    {
        // Zero desired access: enough to send query IOCTLs, and it never reads or writes user data.
        var handle = WindowsNative.OpenDevice(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open {path}.");
        }
        return handle;
    }

    private static byte[] Ioctl(SafeFileHandle handle, uint code, byte[]? input, int outputSize, out int returned)
    {
        var output = new byte[outputSize];
        if (!WindowsNative.DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0), output, (uint)output.Length,
                out uint count, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        returned = (int)Math.Min(count, (uint)output.Length);
        return output;
    }

    /// <summary>STORAGE_PROPERTY_QUERY with PropertyStandardQuery and no additional parameters.</summary>
    internal static byte[] PropertyQuery(int propertyId)
    {
        var query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        return query;
    }

    /// <summary>Asks the NVMe controller for its Identify Controller data (CNS 1) through the adapter protocol property.</summary>
    internal static byte[] NvmeIdentifyQuery()
    {
        var query = new byte[8 + 40];
        BitConverter.GetBytes(PropertyAdapterProtocol).CopyTo(query, 0);
        // STORAGE_PROTOCOL_SPECIFIC_DATA starts at offset 8.
        BitConverter.GetBytes(3).CopyTo(query, 8);     // ProtocolTypeNvme
        BitConverter.GetBytes(1).CopyTo(query, 12);    // NVMeDataTypeIdentify
        BitConverter.GetBytes(1).CopyTo(query, 16);    // CNS 1: identify controller
        BitConverter.GetBytes(0).CopyTo(query, 20);
        BitConverter.GetBytes(40).CopyTo(query, 24);   // data follows the 40-byte specific-data header
        BitConverter.GetBytes(4096).CopyTo(query, 28); // data length
        return query;
    }

    internal static void ParseDeviceDescriptor(byte[] buffer, int returned, DriveIdentity identity)
    {
        if (returned < 36) throw new InvalidDataException("Storage descriptor is too short.");
        string? Text(int field)
        {
            uint offset = BitConverter.ToUInt32(buffer, field);
            if (offset == 0 || offset >= returned) return null;
            int end = (int)offset;
            while (end < returned && buffer[end] != 0) end++;
            var value = Encoding.ASCII.GetString(buffer, (int)offset, end - (int)offset).Trim();
            return value.Length == 0 ? null : value;
        }
        string? vendor = Text(12), product = Text(16);
        // Windows reports SATA drives as vendor "ATA" and NVMe drives often with no vendor; neither adds information.
        bool vendorUseful = vendor is not null && !vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase) &&
            !vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase) &&
            product?.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) != true;
        identity.Model = vendorUseful ? $"{vendor} {product}".Trim() : product;
        identity.Firmware = Text(20);
        identity.BusType = BusTypeName(BitConverter.ToInt32(buffer, 28));
    }

    internal static (string? Version, string? Model, string? Firmware) ParseNvmeIdentify(byte[] buffer, int returned)
    {
        // Output is STORAGE_PROTOCOL_DATA_DESCRIPTOR: 8-byte header, then specific data whose offset field locates the identify block.
        if (returned < 8 + 40) throw new InvalidDataException("NVMe identify response is too short.");
        uint dataOffset = BitConverter.ToUInt32(buffer, 24);
        uint dataLength = BitConverter.ToUInt32(buffer, 28);
        long start = 8L + dataOffset;
        if (dataOffset < 40 || dataLength < 84 || start + 84 > returned) throw new InvalidDataException("NVMe identify data is incomplete.");
        int s = (int)start;
        string? Field(int offset, int length)
        {
            var value = Encoding.ASCII.GetString(buffer, s + offset, length).Trim('\0', ' ');
            return value.Length == 0 ? null : value;
        }
        uint ver = BitConverter.ToUInt32(buffer, s + 80);
        string? version = ver == 0 ? null
            : (ver & 0xFF) == 0 ? $"{ver >> 16}.{(ver >> 8) & 0xFF}" : $"{ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}";
        return (version, Field(24, 40), Field(64, 8));
    }

    internal static string BusTypeName(int code) => code switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File-backed virtual",
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => "Unknown"
    };
}
