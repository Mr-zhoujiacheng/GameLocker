using System.Reflection;
using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 桌面拖入项 → 快捷项识别：
/// .lnk 快捷方式（解析真实目标）、.url 网页快捷方式（读 URL）、exe/其他程序文件、文件夹、图片等文档。
/// 图标统一交给 IconExtractService（对 .lnk/.url 走 shell 缩略图，即目标图标）。
/// </summary>
public static class DropImportService
{
    /// <summary>把拖入的文件/文件夹路径解析为快捷项（Name/Target/Kind 已填，图标由调用方异步提取）；无法识别返回 null</summary>
    public static LaunchItem? FromDroppedPath(string path)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (Directory.Exists(path))
                return new LaunchItem { Name = name, Target = path, Kind = LaunchKind.Folder, Size = TileSize.Small };

            if (!File.Exists(path)) return null;

            // 网页快捷方式：读 [InternetShortcut] URL=
            if (ext == ".url")
            {
                var url = ReadUrlShortcut(path);
                if (url != null)
                    return new LaunchItem { Name = string.IsNullOrEmpty(name) ? url : name, Target = url, Kind = LaunchKind.Url, Size = TileSize.Small };
                return null;
            }

            // 程序快捷方式：解析真实目标
            if (ext == ".lnk")
            {
                var (target, description) = ResolveShortcut(path);
                if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
                {
                    var displayName = !string.IsNullOrEmpty(name) ? name
                        : Path.GetFileNameWithoutExtension(target);
                    var kind = Directory.Exists(target) ? LaunchKind.Folder : LaunchKind.App;
                    // 有描述（如 "Google Chrome"）优先用，更友好
                    if (!string.IsNullOrEmpty(description) && string.IsNullOrEmpty(name))
                        displayName = description;
                    return new LaunchItem { Name = displayName, Target = target, Kind = kind, Size = TileSize.Small };
                }
                // 解析失败退回直接启动 .lnk 本身（shell 能打开）
                return new LaunchItem { Name = name, Target = path, Kind = LaunchKind.App, Size = TileSize.Small };
            }

            // 网站 URL 文件（某些浏览器保存的 .website 等）不处理；其余文件按 shell 启动
            return new LaunchItem { Name = name, Target = path, Kind = LaunchKind.App, Size = TileSize.Small };
        }
        catch { return null; }
    }

    private static string? ReadUrlShortcut(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = trimmed[4..].Trim();
                    return url.Length > 0 ? url : null;
                }
            }
        }
        catch { }
        return null;
    }

    // ---- .lnk 解析（shell32 IShellLink + IPersistFile 强类型 COM，不依赖 WScript/IDispatch）----

    /// <summary>公开入口：解析 .lnk 快捷方式的真实目标路径（失败返回 null）</summary>
    public static string? ResolveShortcutTarget(string lnkPath)
    {
        var (target, _) = ResolveShortcut(lnkPath);
        return string.IsNullOrEmpty(target) ? null : target;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    private static (string Target, string Description) ResolveShortcut(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0); // STGM_READ

            var sb = new System.Text.StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0); // SLGP_RAWPATH
            var target = sb.ToString();

            // 解析环境变量/MUI 引用等，取真实文件路径
            link.Resolve(IntPtr.Zero, 0);
            sb.Clear();
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var resolved = sb.ToString();
            if (!string.IsNullOrEmpty(resolved) && (File.Exists(resolved) || Directory.Exists(resolved)))
                target = resolved;

            var descSb = new System.Text.StringBuilder(260);
            link.GetDescription(descSb, descSb.Capacity);
            return (target, descSb.ToString());
        }
        catch { return ("", ""); }
    }
}
