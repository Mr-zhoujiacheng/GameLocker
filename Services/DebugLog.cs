using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 轻量诊断日志：追加写入 %APPDATA%\GameLocker\debug.log，用于在故障机上定位
/// 「修改器搜索不到 / 存档找不到」这类只在个别机器上复现的问题。
/// 超过 1MB 轮转为 debug.log.1；任何写入失败都静默忽略，绝不影响主流程。
/// </summary>
public static class DebugLog
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");

    private static readonly string LogPath = Path.Combine(Dir, "debug.log");
    private static readonly string RotatedPath = Path.Combine(Dir, "debug.log.1");

    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>写一行带时间戳的日志（线程安全；失败静默）</summary>
    public static void Write(string text)
    {
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
}
