using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>电池状态（kernel32 GetSystemPowerStatus，无额外依赖）</summary>
public static class BatteryService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;       // 0=电池 1=交流电
        public byte BatteryFlag;
        public byte BatteryLifePercent; // 0~100，255=无电池/未知
        public byte SystemStatusStatus;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus sps);

    /// <summary>当前电量百分比；无电池（台式机）/不可读返回 null</summary>
    public static int? GetPercent()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        if (s.BatteryLifePercent > 100) return null; // 255：无电池或未知
        return s.BatteryLifePercent;
    }
}
