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

    /// <summary>电量对应的字形与颜色（主页顶栏与设置页预览共用同一套分档）：
    /// 每 10% 一格，Battery0~9 = E850~E859 / Battery10 = E83F；≥80% 绿、≥20% 黄、其余红</summary>
    public static (string glyph, Windows.UI.Color color) Describe(int percent)
    {
        var level = Math.Clamp((int)Math.Ceiling(percent / 10.0), 0, 10);
        var glyph = level == 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();
        var color = percent >= 80 ? Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xC0, 0x5B)
                  : percent >= 20 ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D)
                  : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x50, 0x4A);
        return (glyph, color);
    }
}
