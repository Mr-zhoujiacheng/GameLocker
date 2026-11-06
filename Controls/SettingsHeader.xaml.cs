using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameLocker.Controls;

/// <summary>
/// 设置子页统一标题栏：返回按钮 + 渐变竖条 + 中文标题/英文副标题 + 渐隐分隔线。
/// 所有设置子页的返回行为一致（回到设置主页），因此由控件直接处理。
/// </summary>
public sealed partial class SettingsHeader : UserControl
{
    public SettingsHeader()
    {
        InitializeComponent();
    }

    /// <summary>页面中文标题</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsHeader),
        new PropertyMetadata("", (d, e) => ((SettingsHeader)d).TitleText.Text = (string)e.NewValue));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>标题下方的英文副标题（宽字距小字，留空则不占位）</summary>
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SettingsHeader),
        new PropertyMetadata("", (d, e) => ((SettingsHeader)d).ApplySubtitle((string)e.NewValue)));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    private void ApplySubtitle(string text)
    {
        SubtitleText.Text = text;
        SubtitleText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => App.Main?.ShowSettingsHub();
}
