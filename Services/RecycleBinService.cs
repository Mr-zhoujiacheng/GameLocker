using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 回收站小组件（WidgetKind.RecycleBin）的领域逻辑：A/点击 = 打开回收站；Y = 一键清空回收站。
/// 清空走 shell32 的 SHEmptyRecycleBin：静默执行（无确认框 / 无进度条 / 无声音）——
/// 系统原生清空，比枚举 shell 命名空间逐个删除快且不会漏掉"超大回收站"的项。
/// 失败不抛异常，只返回失败 HRESULT（多为回收站正被其它程序占用）；回收站本来就为空时同样算成功。
/// </summary>
public static class RecycleBinService
{
    // 必须显式指向带 W 后缀的导出名：shell32 只导出 SHEmptyRecycleBinA/W，
    // 若不加 EntryPoint 且 ExactSpelling=true，CLR 会按字面名 SHEmptyRecycleBin 查找而抛 EntryPointNotFoundException。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>SHQueryRecycleBin 的返回结构：回收站总大小与项目数（cbSize 须在调用前设为结构大小）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    // 同样显式指向 W 导出名（理由同 SHEmptyRecycleBin）
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>不弹确认框</summary>
    private const uint SherbNoConfirmation = 0x0001;
    /// <summary>不显示进度条</summary>
    private const uint SherbNoProgressUi = 0x0002;
    /// <summary>不播放清空音效</summary>
    private const uint SherbNoSound = 0x0004;

    /// <summary>用资源管理器打开回收站（等同双击桌面"回收站"）</summary>
    public static void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打开失败不影响启动器使用：记录后静默返回（调用方按 HideAfterLaunch 收起窗口）
            Debug.WriteLine($"[RecycleBin] 打开回收站失败: {ex.Message}");
        }
    }

    /// <summary>回收站当前项目数（pszRootPath=NULL 统计所有驱动器；查询失败返回 -1，调用方按"未知"处理）</summary>
    public static long ItemCount()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : -1;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecycleBin] 查询回收站失败 {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    /// <summary>一键清空回收站：静默、无确认。返回 HRESULT（0=S_OK 成功；非 0 为失败码，供调用方原样提示）</summary>
    public static int Empty()
    {
        try
        {
            return SHEmptyRecycleBin(IntPtr.Zero, null,
                SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        }
        catch (Exception ex)
        {
            // 异常（导出名不匹配 / 权限不足等）不中断启动器：记录类型+消息，返回 E_FAIL 供提示
            Debug.WriteLine($"[RecycleBin] 清空回收站异常 {ex.GetType().Name}: {ex.Message}");
            return unchecked((int)0x80004005); // E_FAIL
        }
    }
}
