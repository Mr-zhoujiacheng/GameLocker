using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 顶栏资源段用的轻量采样：CPU（GetSystemTimes 差值）与内存（GlobalMemoryStatusEx）。
/// 只在「资源」段挂载时每 2 秒调用一次；不创建性能计数器、不轮询进程列表。
/// </summary>
internal static class SystemResourceSampler
{
    private static ulong _lastIdle;
    private static ulong _lastKernel;
    private static ulong _lastUser;
    private static bool _primed;

    /// <summary>CPU 占用百分比；第一次调用只建立基线并返回 null</summary>
    public static int? CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

        var idleTicks = ToTicks(idle);
        var kernelTicks = ToTicks(kernel);
        var userTicks = ToTicks(user);

        if (!_primed)
        {
            _lastIdle = idleTicks;
            _lastKernel = kernelTicks;
            _lastUser = userTicks;
            _primed = true;
            return null;
        }

        var idleDelta = idleTicks - _lastIdle;
        var totalDelta = (kernelTicks - _lastKernel) + (userTicks - _lastUser);
        _lastIdle = idleTicks;
        _lastKernel = kernelTicks;
        _lastUser = userTicks;

        if (totalDelta == 0) return 0;
        var busy = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
        return (int)Math.Clamp(Math.Round(busy * 100.0 / totalDelta), 0, 100);
    }

    /// <summary>物理内存占用百分比；读取失败返回 null</summary>
    public static int? MemoryPercent()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        return (int)Math.Clamp(status.dwMemoryLoad, 0, 100);
    }

    private static ulong ToTicks(FILETIME time) => ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
