using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GameLocker.Services;

/// <summary>
/// 手柄导航的系统焦点框控制。页面用自绘光标/选框时，残留的系统焦点框不会跟着光标走，
/// 会出现"上一项一个框、当前项一个框"；因此切换光标时先隐掉其它控件的焦点框，
/// 需要该控件的焦点框当光标（文本框输入、按钮）时再恢复，避免来回移动后光标消失。
/// </summary>
internal static class FocusRing
{
    /// <summary>被隐藏过焦点框的控件：再次聚焦时必须恢复，否则回到同一项时光标框不见</summary>
    private static readonly ConditionalWeakTable<Control, object> Hidden = new();

    /// <summary>给控件键盘焦点并确保焦点框可见，同时清掉上一个控件的焦点框</summary>
    public static void FocusWithRing(Control c)
    {
        HideStale(c);
        if (Hidden.Remove(c)) c.UseSystemFocusVisuals = true;
        c.Focus(FocusState.Keyboard);
    }

    /// <summary>清掉当前焦点控件的焦点框（页面改用自绘光标时调用）；控件仍保持焦点，键盘输入不受影响</summary>
    public static void HideCurrent(XamlRoot? root)
    {
        if (root is null) return;
        if (FocusManager.GetFocusedElement(root) is Control focused && Hide(focused))
            Hidden.AddOrUpdate(focused, 0);
    }

    /// <summary>隐藏除 keep 之外的当前焦点控件的焦点框</summary>
    private static void HideStale(Control keep)
    {
        if (keep.XamlRoot is not { } root) return;
        if (FocusManager.GetFocusedElement(root) is Control focused &&
            !ReferenceEquals(focused, keep) && Hide(focused))
            Hidden.AddOrUpdate(focused, 0);
    }

    /// <summary>只处理本来显示焦点框的控件：本来就关着（如手柄映射热区）的不要记录，免得被误恢复</summary>
    private static bool Hide(Control c)
    {
        if (!c.UseSystemFocusVisuals) return false;
        c.UseSystemFocusVisuals = false;
        return true;
    }
}
