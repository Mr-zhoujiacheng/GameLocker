using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GameLocker.Services;

/// <summary>
/// 手柄导航的系统焦点框控制。页面用自绘光标/选框时，残留的系统焦点框不会跟着光标走，
/// 会出现"上一项一个框、当前项一个框"；因此切换光标时先隐掉其它控件的焦点框，
/// 需要该控件的焦点框当光标（文本框输入、按钮）时再恢复，避免来回移动后光标消失。
///
/// 另外两个入口解决"同一项上同时出现两个框"（典型：主页音乐组件的播放按钮上多一圈白框）：
/// ① <see cref="Attach"/>：页面接入后，子控件被焦点（尤其是页面加载/呼出时 WinUI 自动聚焦
///    到第一个可聚焦控件）时不再画系统焦点框，只有页面自己用 <see cref="FocusWithRing"/> 指定的光标控件保留；
/// ② <see cref="HideCurrent"/>：切光标前主动清一次，避免旧框残留。
/// 文本输入控件（TextBox/PasswordBox/RichEditBox）始终保留焦点框：输入法/光标反馈依赖它。
/// </summary>
internal static class FocusRing
{
    /// <summary>被隐藏过焦点框的控件：再次聚焦时必须恢复，否则回到同一项时光标框不见</summary>
    private static readonly ConditionalWeakTable<Control, object> Hidden = new();

    /// <summary>页面明确用作"光标"的控件（FocusWithRing）：页面接入 Attach 后仍保留它们的焦点框</summary>
    private static readonly ConditionalWeakTable<Control, object> Managed = new();

    /// <summary>给控件键盘焦点并确保焦点框可见，同时清掉上一个控件的焦点框</summary>
    public static void FocusWithRing(Control c)
    {
        HideStale(c);
        Managed.AddOrUpdate(c, 0);
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

    /// <summary>
    /// 页面接入：该页用手绘光标（卡片/磁贴/行描边）表示选中，子控件拿到焦点时不再画系统焦点框，
    /// 避免"自绘光标 + 控件焦点框"两个框同时出现（页面加载/呼出时 WinUI 会自动聚焦第一个可聚焦控件）。
    /// 页面用 <see cref="FocusWithRing"/> 指定的光标控件、以及文本输入控件不受影响。
    /// </summary>
    public static void Attach(FrameworkElement host)
    {
        host.GotFocus += (_, e) =>
        {
            if (IsTextInput(e.OriginalSource)) return;
            // 页面自己指定的光标控件（含其模板内部元素）不要撤掉：设置页有些光标就是系统焦点框
            if (HasManagedAncestor(e.OriginalSource)) return;
            HideCurrent(host.XamlRoot);
        };
    }

    /// <summary>从焦点源向上找页面指定的光标控件（模板元素拿到焦点时也能认出来）</summary>
    private static bool HasManagedAncestor(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Control c && Managed.TryGetValue(c, out _)) return true;
        return false;
    }

    /// <summary>需要保留焦点框的文本输入控件（光标/输入法反馈依赖它）</summary>
    private static bool IsTextInput(object? element) =>
        element is TextBox or PasswordBox or RichEditBox;

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
