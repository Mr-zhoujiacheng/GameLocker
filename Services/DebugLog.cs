using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 轻量诊断日志：追加写入 %APPDATA%\GameLocker\debug.log，用于在故障机上定位
/// 「修改器搜索不到 / 存档找不到」这类只在个别机器上复现的问题。
/// 默认关闭（见 <see cref="Enabled"/>，由设置页「记录运行日志」控制），关闭时不写任何内容；
/// 开启后可通过「导出日志」把运行日志与显卡/温度诊断合并成一个文本文件存到桌面，方便反馈问题。
/// 超过 1MB 轮转为 debug.log.1；任何写入失败都静默忽略，绝不影响主流程。
/// </summary>
public static class DebugLog
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");

    private static readonly string LogPath = Path.Combine(Dir, "debug.log");
    private static readonly string RotatedPath = Path.Combine(Dir, "debug.log.1");

    /// <summary>性能监控的显卡/温度诊断，随监控开启写一次（导出日志时一并附上）</summary>
    private static readonly string DiagPath = Path.Combine(Dir, "monitor-diag.txt");

    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>日志开关，默认关闭；由 SettingsService 在读取/保存设置时同步</summary>
    public static bool Enabled { get; set; }

    /// <summary>写一行带时间戳的日志（线程安全；关闭或失败时静默）</summary>
    public static void Write(string text)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    if (File.Exists(RotatedPath)) File.Delete(RotatedPath);
                    File.Move(LogPath, RotatedPath);
                }
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}", Utf8);
            }
        }
        catch { /* 日志写失败不影响主流程 */ }
    }

    /// <summary>
    /// 刚打开日志开关时调用：清空旧日志并写一行抬头，导出的就是「开启之后复现的那段」，
    /// 不会被历史内容淹没。
    /// </summary>
    public static void Reset()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.Delete(RotatedPath);
                File.Delete(LogPath);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] === 日志已开启，等待复现操作 ==={Environment.NewLine}", Utf8);
            }
        }
        catch { }
    }

    /// <summary>
    /// 导出日志到桌面（单个 txt：系统/机型信息 + 运行日志 + 显卡温度诊断），返回文件路径；
    /// 桌面不可写时返回 null。
    /// </summary>
    public static string? Export()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return null;

            var sb = new StringBuilder();
            sb.AppendLine("GameLocker 运行日志");
            sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"程序版本：{typeof(DebugLog).Assembly.GetName().Version}");
            sb.AppendLine($"系统：{Environment.OSVersion.VersionString} / {RuntimeInformation.OSArchitecture}"
                + $" / 64 位进程：{(Environment.Is64BitProcess ? "是" : "否")}");
            sb.AppendLine($"管理员权限：{(IsElevated() ? "是" : "否")}");
            sb.AppendLine($"设备：{SystemInfo()}");
            sb.AppendLine($"日志开关：{(Enabled ? "开（导出时仍开启）" : "关（导出的是上次开启期间的记录）")}");
            sb.AppendLine();

            sb.AppendLine("============== 运行日志 ==============");
            var log = ReadText(LogPath);
            var rotated = ReadText(RotatedPath);
            if (log.Length == 0 && rotated.Length == 0)
            {
                sb.AppendLine("（没有日志内容：日志功能未开启，或开启后还没有操作记录）");
            }
            else
            {
                if (rotated.Length > 0)
                {
                    sb.AppendLine("---- debug.log.1（较早的记录）----");
                    sb.AppendLine(rotated);
                }
                if (log.Length > 0)
                {
                    sb.AppendLine("---- debug.log ----");
                    sb.AppendLine(log);
                }
            }
            sb.AppendLine();

            sb.AppendLine("=========== 显卡 / 温度诊断 ===========");
            var diag = ReadText(DiagPath);
            sb.AppendLine(diag.Length > 0 ? diag : "（本次启动没有开启性能监控，或诊断文件不存在）");

            var path = Path.Combine(desktop, $"GameLocker日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, sb.ToString(), Utf8);
            return path;
        }
        catch { return null; }
    }

    private static string ReadText(string path)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(path)) return "";
                return File.ReadAllText(path, Utf8).TrimEnd();
            }
        }
        catch { return ""; }
    }

    /// <summary>机型 + CPU 型号（掌机型号对排查很重要）；查询失败时只给机器名</summary>
    private static string SystemInfo()
    {
        try
        {
            using var computer = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var cpu = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");
            var vendor = "";
            var model = "";
            foreach (var o in computer.Get())
            {
                vendor = o["Manufacturer"]?.ToString()?.Trim() ?? "";
                model = o["Model"]?.ToString()?.Trim() ?? "";
                break;
            }
            var name = "";
            foreach (var o in cpu.Get())
            {
                name = o["Name"]?.ToString()?.Trim() ?? "";
                break;
            }
            var parts = new[] { vendor, model, name }.Where(p => p.Length > 0).ToArray();
            return parts.Length > 0 ? string.Join(" / ", parts) : Environment.MachineName;
        }
        catch { return Environment.MachineName; }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
