using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EveryCopy.Services;

/// <summary>Tells solid-state drives (no seek penalty) from spinning disks, to choose how many files to copy at once.</summary>
public static class DriveKind
{
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery { public int PropertyId; public int QueryType; public byte AdditionalParameters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor { public uint Version, Size; [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inSize,
        out SeekPenaltyDescriptor output, int outSize, out int returned, IntPtr overlapped);

    private static readonly Dictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True only when Windows reports the drive holding <paramref name="path"/> has no seek penalty; false if unknown.</summary>
    public static bool IsSolidState(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
            string letter = root[..2];
            lock (Cache)
            {
                if (Cache.TryGetValue(letter, out bool known)) return known;
                bool ssd = Query(letter);
                Cache[letter] = ssd;
                return ssd;
            }
        }
        catch { return false; }
    }

    private static bool Query(string drive)
    {
        using var device = CreateFileW(@"\\.\" + drive, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device.IsInvalid) return false;
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = 0 };
        return DeviceIoControl(device, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<StoragePropertyQuery>(),
            out var result, Marshal.SizeOf<SeekPenaltyDescriptor>(), out _, IntPtr.Zero) && !result.IncursSeekPenalty;
    }
}
