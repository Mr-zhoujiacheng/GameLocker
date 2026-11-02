using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// Windows 效率模式（EcoQoS）：通过 PROCESS_POWER_THROTTLING_EXECUTION_SPEED
/// 让系统在调度时降低本进程的执行优先级，从而减少后台功耗与占用。
/// 任务管理器里显示为绿色的"效率模式"。
/// </summary>
public static class PowerModeService
{
    private const int ProcessPowerThrottling = 4;   // PROCESS_INFORMATION_CLASS
    private const uint ThrottlingVersion = 1;
    private const uint ExecutionSpeed = 0x1;        // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private const uint IdlePriorityClass = 0x00000040;      // 任务管理器效率模式把优先级降到"低"
    private const uint NormalPriorityClass = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass,
        ref ProcessPowerThrottlingState info, uint infoSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

    private static bool _enabled;

    /// <summary>切换效率模式（低优先级 + EcoQoS，与任务管理器一致）；返回是否生效</summary>
    public static bool SetEfficiencyMode(bool on)
    {
        if (_enabled == on) return true;
        var state = new ProcessPowerThrottlingState
        {
            Version = ThrottlingVersion,
            ControlMask = ExecutionSpeed,
            StateMask = on ? ExecutionSpeed : 0
        };
        var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
        var throttled = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size);
        // 任务管理器只在"低优先级 + EcoQoS"同时成立时才点亮绿色叶子，两项必须一起切换
        var priority = SetPriorityClass(GetCurrentProcess(),
            on ? IdlePriorityClass : NormalPriorityClass);
        if (!throttled || !priority) return false;
        _enabled = on;
        return true;
    }
}
