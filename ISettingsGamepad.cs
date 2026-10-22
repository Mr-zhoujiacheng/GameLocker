using GameLocker.Services;

namespace GameLocker;

/// <summary>
/// 设置相关页面（主页 + 各分类子页）统一的手柄交互接口，
/// MainWindow 按此接口转发按键，无需逐类型判断。
/// </summary>
public interface ISettingsGamepad
{
    /// <summary>手柄按键（MainWindow 转发，已在 UI 线程）</summary>
    void HandleGamepad(GamepadButtons buttons);

    /// <summary>是否处于呼出键位捕获模式（MainWindow 据此转发原始按键）</summary>
    bool IsCapturing { get; }

    /// <summary>本次按键是否已被页面消费（如下拉框展开时 B 只收起下拉）→ MainWindow 不再执行返回</summary>
    bool ConsumedBack => false;

    /// <summary>捕获模式下累积按下的按键（边沿）</summary>
    void CaptureButtons(GamepadButtons pressed);

    /// <summary>所有键松开 → 结束捕获</summary>
    void NotifyAllReleased();
}
