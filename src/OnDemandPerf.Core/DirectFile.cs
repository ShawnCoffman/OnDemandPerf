using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OnDemandPerf.Core;

// All native buffer sizes and offsets are checked here. No unsafe blocks; ownership is held by SafeHandles.
internal sealed class DirectFile : IDisposable
{
    private readonly SafeFileHandle file;
    private readonly AlignedBuffer buffer = new(1024 * 1024);
    internal uint LogicalSectorBytes { get; }
    internal uint PhysicalSectorBytes { get; }
    internal DirectFile(string path, byte[] pattern)
    {
        file = CreateFile(path, 0xC0000000, 0, IntPtr.Zero, 1, 0xA4000000, IntPtr.Zero);
        try
        {
            if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandleEx(file, 16, out StorageInfo storage, (uint)Marshal.SizeOf<StorageInfo>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            LogicalSectorBytes = storage.Logical;
            PhysicalSectorBytes = Math.Max(storage.PhysicalAtomic, storage.PhysicalPerformance);
            if (LogicalSectorBytes == 0 || PhysicalSectorBytes == 0 || 4096 % LogicalSectorBytes != 0 ||
                4096 % PhysicalSectorBytes != 0 || buffer.DangerousGetHandle().ToInt64() % PhysicalSectorBytes != 0)
                throw new NotSupportedException("Storage alignment cannot safely support this 4 KiB profile.");
            if ((File.GetAttributes(path) & (FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile)) != 0)
                throw new NotSupportedException("Compressed, encrypted, or sparse test files are not supported.");
            SetPattern(pattern);
        }
        catch { file.Dispose(); buffer.Dispose(); throw; }
    }
    internal void SetPattern(byte[] pattern)
    {
        if (pattern.Length != 1024 * 1024) throw new ArgumentException("Pattern must be 1 MiB.");
        Marshal.Copy(pattern, 0, buffer.DangerousGetHandle(), pattern.Length);
    }
    internal void Transfer(long offset, int count, bool write, int bufferOffset = 0)
    {
        if (offset < 0 || offset % 4096 != 0 || count <= 0 || count % 4096 != 0 || bufferOffset < 0 ||
            bufferOffset % 4096 != 0 || bufferOffset > 1024 * 1024 - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!SetFilePointerEx(file, offset, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var pointer = IntPtr.Add(buffer.DangerousGetHandle(), bufferOffset);
        uint transferred;
        bool success = write ? WriteFile(file, pointer, (uint)count, out transferred, IntPtr.Zero)
            : ReadFile(file, pointer, (uint)count, out transferred, IntPtr.Zero);
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (transferred != count) throw new IOException("Short direct I/O transfer.");
    }
    internal bool Matches(byte[] pattern, int patternOffset, int count)
    {
        var actual = new byte[count];
        Marshal.Copy(buffer.DangerousGetHandle(), actual, 0, count);
        return actual.AsSpan().SequenceEqual(pattern.AsSpan(patternOffset, count));
    }
    public void Dispose() { file.Dispose(); buffer.Dispose(); }

    private sealed class AlignedBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal AlignedBuffer(int size) : base(true)
        {
            SetHandle(VirtualAlloc(IntPtr.Zero, (nuint)size, 0x3000, 4));
            if (IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        protected override bool ReleaseHandle() => VirtualFree(handle, 0, 0x8000);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StorageInfo { internal uint Logical, PhysicalAtomic, PhysicalPerformance, EffectivePhysical, Flags, SectorOffset, PartitionOffset; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out StorageInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long offset, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint length, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint length, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);
}
