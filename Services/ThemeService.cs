using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>Windows 应用/系统深浅色模式切换。</summary>
internal static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    /// <summary>切换深浅色，返回切换后的“是否深色”。</summary>
    public static bool Toggle()
    {
        var dark = !IsDark();
        using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
        {
            key?.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            key?.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }

        try
        {
            SendMessageTimeout(new IntPtr(0xffff), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero,
                "ImmersiveColorSet", 0x0002, 1000, out _);
        }
        catch { }

        return dark;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);
}
