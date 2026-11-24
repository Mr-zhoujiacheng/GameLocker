using GameLocker.Services;
using Microsoft.UI.Xaml;

namespace GameLocker;

public sealed partial class MainPage
{
    /// <summary>标题区在编辑态/主页态之间的过渡。黑板与"猫娘敲黑板"动画已移除，
    /// 进入/退出编辑只换提示条内容，标题区不再做位移，猫娘也留在原位。</summary>
    private void ApplyEditHeader(bool animate)
    {
        HomeHintSlide.Y = 0;
        HomeHintShell.Visibility = SettingsService.Current.TabletMode ? Visibility.Collapsed : Visibility.Visible;
        MascotLayer.Opacity = 1;
        MascotLayer.IsHitTestVisible = true;
        PositionMascot();
        Mascot.SetPageActive(!_moving && _pageWorkActive);
        RefreshHomeHints(force: true);
        // "编辑模式"大字的弹入动画保留（原先挂在黑板的入场流程里）
        if (_moving && animate) PopInEditModeText();
    }

    /// <summary>系统是否允许动画（供提示条等轻量动效判断）</summary>
    private static bool HeaderMotionEnabled => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
}
