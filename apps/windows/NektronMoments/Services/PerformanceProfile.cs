using System.Runtime.InteropServices;
using NektronMoments.Models;

namespace NektronMoments.Services;

public static class PerformanceProfile
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static readonly ulong PhysicalBytes = ReadMemory();
    public static readonly GalleryBudget Current = GalleryBudget.ForMemory(PhysicalBytes);
    private static ulong ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 8UL * 1024 * 1024 * 1024;
    }
}
