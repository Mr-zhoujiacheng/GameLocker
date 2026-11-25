using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>节能模式：在“节能”和“平衡”电源方案之间切换。</summary>
internal static class EnergySaverService
{
    private static readonly Guid EnergySaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static bool IsEnergySaver() => ReadActiveScheme() == EnergySaver;

    internal static Guid? ReadActiveScheme()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
        }
        catch { return null; }
    }

    public static bool SetEnergySaver(bool on)
    {
        try
        {
            var scheme = on ? EnergySaver : Balanced;
            return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
        }
        catch { return false; }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
