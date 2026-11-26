using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;

namespace GameLocker;

public sealed partial class AddItemPage
{
    private OptiCaptureHostRelease? _optiCaptureAvailableRelease;
    private CancellationTokenSource? _optiCaptureUpdateCancellation;

    private void LayoutOptiCaptureOptions(double width)
    {
        const double columnGap = 16;
        const double minimumColumnWidth = 175;
        var twoColumns = width >= 2 * minimumColumnWidth + columnGap;
        OptiCaptureColumns.ColumnSpacing = twoColumns ? columnGap : 0;
        OptiCaptureRightColumn.Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(OptiCaptureOverlayColumn, twoColumns ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(OptiCaptureOverlayColumn, twoColumns ? 0 : 1);
    }

    private void LoadOptiCaptureOptions(LaunchItem item)
    {
        SelectComboByTag(OptiCaptureVSyncCombo, OptiCaptureProfile.NormalizeToggle(item.OptiCaptureVSync));
        SelectComboByTag(OptiCaptureFpsOverlayCombo, OptiCaptureProfile.NormalizeToggle(item.OptiCaptureFpsOverlay));
        SelectComboByTag(OptiCaptureMenuScaleCombo, OptiCaptureProfile.NormalizeMenuScale(item.OptiCaptureMenuScale));
        SelectComboByTag(OptiCaptureSharpnessCombo, OptiCaptureProfile.NormalizeSharpness(item.OptiCaptureSharpness));
        SelectComboByTag(OptiCaptureMenuLanguageCombo, OptiCaptureMenuLocalization.Normalize(item.OptiCaptureMenuLanguage));
    }

    private void SaveOptiCaptureOptions(LaunchItem item)
    {
        item.OptiCaptureVSync = ComboTag(OptiCaptureVSyncCombo, "inherit");
        item.OptiCaptureFpsOverlay = ComboTag(OptiCaptureFpsOverlayCombo, "inherit");
        item.OptiCaptureMenuScale = ComboTag(OptiCaptureMenuScaleCombo, "inherit");
        item.OptiCaptureSharpness = ComboTag(OptiCaptureSharpnessCombo, "inherit");
        item.OptiCaptureMenuLanguage = ComboTag(OptiCaptureMenuLanguageCombo, "zh-CN");
    }

    private static string InstalledOptiCaptureStatus()
    {
        var updater = LosslessScalingService.OptiCaptureUpdates;
        return updater.GetInstalledVersion() is { } version ? $"已安装 v{version}" : "未安装组件";
    }

    private void ResetOptiCaptureUpdateUi()
    {
        _optiCaptureAvailableRelease = null;
        OptiCaptureVersionText.Text = InstalledOptiCaptureStatus();
        OptiCaptureLanguageStatus.Text = OptiCaptureMenuLocalization.IsAvailable(
            LosslessScalingService.OptiCaptureUpdates.GetInstalledDirectory()) ? "简体中文可用" : "当前组件无兼容汉化";
        OptiCaptureUpdateButton.Content = "检查更新";
    }

    private async void OnOptiCaptureUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_optiCaptureUpdateCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _optiCaptureUpdateCancellation = cancellation;
        var release = _optiCaptureAvailableRelease;
        var updater = LosslessScalingService.OptiCaptureUpdates;
        var current = InstalledOptiCaptureStatus();
        OptiCaptureUpdateButton.IsEnabled = false;
        OptiCaptureUpdateProgress.Value = 0;
        OptiCaptureUpdateProgress.IsIndeterminate = true;
        OptiCaptureUpdateProgress.Visibility = Visibility.Visible;
        RebuildFormControls();
        try
        {
            if (release is null)
            {
                OptiCaptureVersionText.Text = $"{current} · 正在检查更新…";
                release = await updater.GetLatestReleaseAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var installed = updater.GetInstalledVersion();
                if (installed is not null && (release.Version < installed ||
                    (release.Version == installed && updater.IsFullRuntimeInstalled())))
                {
                    OptiCaptureVersionText.Text = $"{InstalledOptiCaptureStatus()} · 已是最新正式版";
                    return;
                }

                _optiCaptureAvailableRelease = release;
                var repair = installed == release.Version;
                OptiCaptureVersionText.Text = repair ? $"{current} · 可补齐可选运行库" : $"{current} · 可用版本 {release.Tag}";
                OptiCaptureUpdateButton.Content = repair ? "下载完整组件" : installed is null ? "下载并安装" : "下载并更新";
                return;
            }

            OptiCaptureVersionText.Text = $"正在准备 {release.Tag}…";
            await LosslessScalingService.InstallOptiCaptureAsync(release, (bytes, total) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_optiCaptureUpdateCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                    var hasTotal = total is > 0;
                    var finishedDownload = hasTotal && bytes >= total!.Value;
                    OptiCaptureUpdateProgress.IsIndeterminate = !hasTotal || finishedDownload;
                    if (hasTotal) OptiCaptureUpdateProgress.Value = Math.Clamp(bytes * 100.0 / total!.Value, 0, 100);
                    OptiCaptureVersionText.Text = finishedDownload
                        ? $"正在校验并安装 {release.Tag}…"
                        : hasTotal
                            ? $"正在下载 {release.Tag}… {DownloadProgress.Mb(bytes)} / {DownloadProgress.Mb(total!.Value)} MB"
                            : $"正在下载 {release.Tag}… {DownloadProgress.Mb(bytes)} MB";
                });
            }, cancellation.Token);
            ResetOptiCaptureUpdateUi();
            OptiCaptureVersionText.Text += " · 下次启动时生效";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ResetOptiCaptureUpdateUi();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiCaptureHost update: {ex}");
            var reason = ex switch
            {
                OperationCanceledException => "连接超时，请重试。",
                HttpRequestException => "无法访问 GitHub，请检查网络后重试。",
                InvalidDataException => "上游发布包不兼容或校验未通过。",
                FileNotFoundException => "上游发布包不完整（缺少运行文件）。",
                UnauthorizedAccessException => "组件目录不可写。",
                IOException => "无法读写组件目录，请检查可用空间或文件占用。",
                OptiCaptureBusyException => "请先关闭 OptiScaler，并等待正在进行的操作结束。",
                _ => "发生异常，详情见日志。"
            };
            OptiCaptureVersionText.Text = $"{InstalledOptiCaptureStatus()} · {(release is null ? "检查" : "安装")}失败：{reason}";
        }
        finally
        {
            _optiCaptureUpdateCancellation = null;
            OptiCaptureUpdateProgress.Visibility = Visibility.Collapsed;
            OptiCaptureUpdateButton.IsEnabled = true;
            RebuildFormControls();
        }
    }
}
