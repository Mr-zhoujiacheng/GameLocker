using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 读取前台窗口的输入法转换态（中/EN）。
/// 走 IMM：ImmGetDefaultIMEWnd(前台窗口) → WM_IME_CONTROL / IMC_GETCONVERSIONMODE，
/// 只看 IME_CMODE_NATIVE 位；读取失败返回 null，顶栏显示占位而不是猜。
/// 只在「输入法段」挂载时轮询，见 StatusBarWindow。
/// </summary>
internal static class ImeStateWatcher
{
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const int IMC_SETCONVERSIONMODE = 0x0002;
    private const uint IME_CMODE_NATIVE = 0x0001;
    private const uint IME_CMODE_KATAKANA = 0x0002;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>切换前台窗口的中/英输入态。优先直接改 IMM 转换态，失败再回退 Shift 键。</summary>
    public static bool Toggle()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            var context = ImmGetContext(foreground);
            if (context != IntPtr.Zero)
            {
                try
                {
                    if (ImmGetConversionStatus(context, out var conversion, out var sentence))
                    {
                        conversion ^= IME_CMODE_NATIVE;
                        return ImmSetConversionStatus(context, conversion, sentence);
                    }
                }
                finally
                {
                    ImmReleaseContext(foreground, context);
                }
            }

            // 回退：给默认 IME 窗口发 IMC_SETCONVERSIONMODE
            var ime = ImmGetDefaultIMEWnd(foreground);
            if (ime == IntPtr.Zero) return false;
            var current = IsChinese();
            if (current is null) return false;
            var mode = current.Value ? 0 : IME_CMODE_NATIVE;
            SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_SETCONVERSIONMODE,
                (IntPtr)mode, SMTO_ABORTIFHUNG, 120, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>前台处于中文（本地语言）输入态返回 true，英文返回 false，无法判断返回 null。</summary>
    public static bool? IsChinese()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return null;

            // 首选 ImmGetContext + ImmGetConversionStatus：不走消息队列，不会因为目标线程忙而超时
            var context = ImmGetContext(foreground);
            if (context != IntPtr.Zero)
            {
                try
                {
                    if (ImmGetConversionStatus(context, out var conversion, out _))
                        return (conversion & IME_CMODE_NATIVE) != 0
                               && (conversion & IME_CMODE_KATAKANA) == 0;
                }
                finally
                {
                    ImmReleaseContext(foreground, context);
                }
            }

            // 回退：向默认 IME 窗口查询转换态
            var ime = ImmGetDefaultIMEWnd(foreground);
            if (ime == IntPtr.Zero) return null;

            if (SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE,
                    IntPtr.Zero, SMTO_ABORTIFHUNG, 120, out var result) == IntPtr.Zero)
                return null;

            var mode = (uint)result.ToInt64();
            return (mode & IME_CMODE_NATIVE) != 0 && (mode & IME_CMODE_KATAKANA) == 0;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetConversionStatus(IntPtr hImc, uint conversion, uint sentence);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(IntPtr hImc, out uint conversion, out uint sentence);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr hImc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
