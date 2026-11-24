using System.Diagnostics;

namespace GameLocker.Services;

/// <summary>
/// 临时周转组：存放"本次工作"的临时文件，程序启动时清空。
/// 与速记一样属于底栏的附属功能，不参与 items.json。
/// </summary>
internal static class StagingService
{
    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameLocker", "dock-staging");

    /// <summary>启动时清空（只删自己目录下的内容，不跟随符号链接）。</summary>
    public static void ClearOnStartup()
    {
        try
        {
            if (!Directory.Exists(FolderPath)) { Directory.CreateDirectory(FolderPath); return; }

            foreach (var file in Directory.GetFiles(FolderPath))
            {
                try { File.Delete(file); } catch { }
            }

            foreach (var dir in Directory.GetDirectories(FolderPath))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 清理临时周转组失败：" + ex.Message); }
    }

    /// <summary>在资源管理器里打开。</summary>
    public static void Open()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            Process.Start(new ProcessStartInfo { FileName = FolderPath, UseShellExecute = true });
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 打开临时周转组失败：" + ex.Message); }
    }
}
