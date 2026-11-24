using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 底栏的文件拖入（OLE 放置目标）。
/// 难点：WinUI 的内容实际托管在 <c>Microsoft.UI.Content.DesktopChildSiteBridge</c> 窗口里，
/// 鼠标命中的是它，所以 <c>RegisterDragDrop</c> 必须注册在**那个**窗口上，注册在顶层窗口无效。
/// WinUI 可能已经自己注册过，此时会返回 <c>DRAGDROP_E_ALREADYREGISTERED</c>。
/// </summary>
internal static class DockDropTarget
{
    private const string BridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

    private static DropTarget? _target;
    private static readonly List<IntPtr> Registered = [];

    /// <summary>给底栏窗口注册拖放目标；返回是否至少成功注册一个窗口。</summary>
    public static bool Attach(IntPtr dockHwnd)
    {
        try
        {
            // RegisterDragDrop 要求调用线程已 OleInitialize（WinUI 只保证 CoInitialize）
            OleInitialize(IntPtr.Zero);
            _target ??= new DropTarget();
            var ok = false;

            foreach (var candidate in Candidates(dockHwnd))
            {
                if (Registered.Contains(candidate)) { ok = true; continue; }

                var hr = RegisterDragDrop(candidate, _target);
                var cls = ClassOf(candidate);

                if (hr == 0)
                {
                    Registered.Add(candidate);
                    ok = true;
                    DebugLog.Write($"[底栏] 拖放目标已注册 class={cls} hwnd=0x{candidate.ToInt64():X}");
                }
                else
                {
                    DebugLog.Write($"[底栏] 拖放目标注册失败 class={cls} hr=0x{hr:X8}");
                }
            }

            if (!ok) DebugLog.Write("[底栏] 未找到可注册拖放的窗口");
            return ok;
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 拖放目标初始化异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>底栏自身窗口 + 它名下的 WinUI 内容桥窗口。</summary>
    private static List<IntPtr> Candidates(IntPtr dockHwnd)
    {
        var list = new List<IntPtr> { dockHwnd };

        // 桥窗口通常是子窗口，递归找一遍
        EnumChildWindows(dockHwnd, (hwnd, _) =>
        {
            if (ClassOf(hwnd) == BridgeClass && IsWindowVisible(hwnd)) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if ((int)pid != Environment.ProcessId) return true;
            if (!IsWindowVisible(hwnd)) return true;
            if (ClassOf(hwnd) != BridgeClass) return true;

            // 只取与底栏窗口重叠的桥窗口（状态栏也有自己的桥）
            if (!GetWindowRect(hwnd, out var bridge) || !GetWindowRect(dockHwnd, out var dock)) return true;
            var overlaps = bridge.Left < dock.Right && bridge.Right > dock.Left
                        && bridge.Top < dock.Bottom && bridge.Bottom > dock.Top;
            if (overlaps) list.Add(hwnd);

            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>从 IDataObject 取出 CF_HDROP 文件列表。</summary>
    internal static List<string> ExtractFiles(object dataObject)
    {
        var files = new List<string>();
        IntPtr medium = IntPtr.Zero;
        try
        {
            if (dataObject is not System.Runtime.InteropServices.ComTypes.IDataObject com) return files;

            var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
            {
                cfFormat = CF_HDROP,
                ptd = IntPtr.Zero,
                dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
            };

            // ComTypes 的 GetData 返回 void（失败会抛异常）
            com.GetData(ref format, out var stg);
            // .NET 的 STGMEDIUM 用 unionmember 暴露 hGlobal
            medium = stg.unionmember;
            if (medium == IntPtr.Zero) return files;

            var count = DragQueryFile(medium, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(medium, i, null, 0);
                var builder = new StringBuilder((int)length + 1);
                if (DragQueryFile(medium, i, builder, builder.Capacity) > 0)
                    files.Add(builder.ToString());
            }
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 读取拖入文件失败：" + ex.Message); }
        finally
        {
            if (medium != IntPtr.Zero) GlobalFree(medium);
        }
        return files;
    }

    private const short CF_HDROP = 15;

    [ComVisible(true)]
    private sealed class DropTarget : IDropTarget
    {
        public int DragEnter(object data, uint keys, POINT point, ref uint effect)
        {
            // 只接受文件拖入，其它（文本等）不显示可放置
            var files = ExtractFiles(data);
            effect = files.Count > 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            return 0;
        }

        public int DragOver(uint keys, POINT point, ref uint effect) => 0;

        public int DragLeave() => 0;

        public int Drop(object data, uint keys, POINT point, ref uint effect)
        {
            var files = ExtractFiles(data);
            effect = files.Count > 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;

            if (files.Count > 0)
            {
                // 拖放回调在 OLE 的线程上，落盘与界面刷新交给 UI 线程
                DebugLog.Write($"[底栏] 拖入 {files.Count} 个条目：{string.Join(" | ", files.Take(5))}");
                DockDropTargetHandler?.Invoke(files);
            }
            return 0;
        }
    }

    /// <summary>真正处理拖入条目（由 DockWindow 提供，切到 UI 线程）。</summary>
    public static Action<List<string>>? DockDropTargetHandler;

    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_NONE = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object data, uint keys, POINT point, ref uint effect);
        [PreserveSig] int DragOver(uint keys, POINT point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object data, uint keys, POINT point, ref uint effect);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
