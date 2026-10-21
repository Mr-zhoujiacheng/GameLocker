using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>电源菜单的操作项（顺序与主页电源菜单一致）</summary>
public enum PowerAction
{
    /// <summary>注销当前用户</summary>
    SignOut,
    /// <summary>睡眠（可被网络/设备唤醒）</summary>
    Sleep,
    /// <summary>休眠（写入磁盘后断电；需系统已开启休眠）</summary>
    Hibernate,
    /// <summary>重启</summary>
    Restart,
    /// <summary>关机</summary>
    Shutdown
}

/// <summary>
/// 系统电源操作：注销 / 睡眠 / 休眠 / 重启 / 关机。
/// - 注销、重启、关机走 ExitWindowsEx；重启/关机需临时启用 SE_SHUTDOWN_NAME 权限（普通交互用户可启用）
/// - 睡眠/休眠走 powrprof!SetSuspendState；休眠要求系统已开启 hiberfil（未开启会返回失败）
/// 电源操作不可逆，调用方必须先让用户在选择菜单里二次确认。
/// </summary>
public static class PowerService
{
    /// <summary>执行电源操作（阻塞调用；睡眠/休眠会一直阻塞到系统唤醒）。
    /// 返回 false 表示系统拒绝该操作，Message 是给用户看的原因。</summary>
    public static (bool Ok, string Message) Execute(PowerAction action)
    {
        try
        {
            switch (action)
            {
                case PowerAction.SignOut:
                    return ExitWindowsEx(EWX_LOGOFF, 0)
                        ? (true, "正在注销…")
                        : (false, "注销失败：可能被系统策略阻止");

                case PowerAction.Sleep:
                    // hibernate=false → 睡眠；forceCritical=false 让驱动有机会拒绝不安全的睡眠
                    return SetSuspendState(false, false, false)
                        ? (true, "")
                        : (false, "睡眠失败：当前设备不支持睡眠");

                case PowerAction.Hibernate:
                    if (!EnableShutdownPrivilege()) return (false, "休眠失败：权限不足");
                    return SetSuspendState(true, false, false)
                        ? (true, "")
                        : (false, "休眠失败：系统可能未开启休眠（设置 → 系统 → 电源）");

                case PowerAction.Restart:
                    if (!EnableShutdownPrivilege()) return (false, "重启失败：权限不足");
                    return ExitWindowsEx(EWX_REBOOT | EWX_FORCEIFHUNG, 0)
                        ? (true, "正在重启…")
                        : (false, "重启失败：可能被系统策略阻止");

                default:
                    if (!EnableShutdownPrivilege()) return (false, "关机失败：权限不足");
                    return ExitWindowsEx(EWX_POWEROFF | EWX_FORCEIFHUNG, 0)
                        ? (true, "正在关机…")
                        : (false, "关机失败：可能被系统策略阻止");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Power] 电源操作失败 {ex.GetType().Name}: {ex.Message}");
            return (false, "电源操作失败：系统拒绝执行");
        }
    }

    // ---- P/Invoke ----

    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_POWEROFF = 0x00000008;
    private const uint EWX_REBOOT = 0x00000002;
    private const uint EWX_FORCEIFHUNG = 0x00000010;

    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    /// <summary>临时启用当前进程的关机权限（退出/重启/休眠的前置条件）</summary>
    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0; // 权限不存在（非管理员）时 Adjust 仍返回 true
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
