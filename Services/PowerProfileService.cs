using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 性能面板可共用一套参数，或按供电方式分别保存，在插拔电源时后台重新应用。
/// 取值约定：-1 表示尚未保存过该项，应用时跳过，避免首次使用就覆盖系统现状。
/// </summary>
internal static class PowerProfileService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>当前是否由交流电供电（读取失败时按插电处理，保持原行为）</summary>
    public static bool OnAc
    {
        get
        {
            try { return GetSystemPowerStatus(out var status) && status.ACLineStatus == 1; }
            catch { return true; }
        }
    }

    /// <summary>当前供电方式对应的那套参数</summary>
    public static PowerProfile Active => !SettingsService.Current.SeparatePowerProfiles
        ? SettingsService.Current.SharedProfile
        : OnAc ? SettingsService.Current.AcProfile : SettingsService.Current.BatteryProfile;

    public static void SetSeparateProfiles(bool enabled)
    {
        if (enabled == SettingsService.Current.SeparatePowerProfiles) return;
        var current = Active.Copy();
        SettingsService.Update(s =>
        {
            if (enabled)
            {
                s.AcProfile = current.Copy();
                s.BatteryProfile = current.Copy();
            }
            else s.SharedProfile = current;
            s.SeparatePowerProfiles = enabled;
        });
    }

    /// <summary>把面板当前显示的全部参数保存到当前供电方式的配置里</summary>
    public static void CaptureFromPanel(
        int powerMode, int frameFps, bool cpuPowerEnabled,
        int pl1, int pl2, int fppt, int boost, int frequencyMhz,
        GameCoreMode coreMode, bool coreModeCaptured, int coreSelection)
    {
        var onAc = OnAc;
        SettingsService.Update(s =>
        {
            var p = !s.SeparatePowerProfiles ? s.SharedProfile : onAc ? s.AcProfile : s.BatteryProfile;
            p.PowerMode = powerMode;
            p.FrameLimitFps = frameFps;
            p.CpuPowerEnabled = cpuPowerEnabled;
            p.CpuPowerPl1 = pl1;
            p.CpuPowerPl2 = pl2;
            p.CpuPowerFppt = fppt;
            p.Boost = boost;
            p.MaxFrequencyMhz = frequencyMhz;
            p.CoreMode = coreMode;
            p.CoreModeCaptured = coreModeCaptured;
            p.CoreSelection = coreSelection;
        });
    }

    /// <summary>插拔电源后按新的供电方式重新应用参数；返回本次实际应用的项，供日志定位</summary>
    public static async Task<string> ApplyActiveAsync()
    {
        var onAc = OnAc;
        var profile = Active.Copy();
        if (!HasAny(profile)) return "该供电方式尚未保存性能参数";
        var applied = new List<string>();

        await Task.Run(() =>
        {
            // 1) Windows 电源模式
            if (profile.PowerMode is >= 0 and <= 2)
            {
                var (ok, message) = PerformanceService.ApplyLevel((PerformanceLevel)profile.PowerMode);
                applied.Add(ok ? "电源模式" : $"电源模式失败({message})");
            }

            // 2) 全局锁帧
            if (profile.FrameLimitFps >= 0)
            {
                var state = FrameLimitService.Read();
                if (state.Supported)
                {
                    var (ok, message) = FrameLimitService.Apply(state, profile.FrameLimitFps);
                    applied.Add(ok ? $"锁帧{profile.FrameLimitFps}" : $"锁帧失败({message})");
                }
            }

            // 3) CPU 功耗：先把目标写成“已保存值”，再交给助手或登录任务静默写回硬件
            if (profile.CpuPowerEnabled && profile.CpuPowerPl1 > 0)
            {
                SettingsService.Update(s =>
                {
                    s.CpuPowerTakeover = true;
                    s.CpuPowerPl1 = profile.CpuPowerPl1;
                    s.CpuPowerPl2 = profile.CpuPowerPl2;
                    s.CpuPowerFppt = profile.CpuPowerFppt;
                });
                CpuPowerAutoApply.ReapplyIfActive();
                applied.Add("CPU功耗");
            }

            // 4) 睿频 / 最高频率
            if (profile.Boost is 0 or 2)
            {
                var (ok, message) = PerformanceService.ApplyCpuSetting(true, profile.Boost);
                applied.Add(ok ? "睿频" : $"睿频失败({message})");
            }
            if (profile.MaxFrequencyMhz >= 0)
            {
                var (ok, message) = PerformanceService.ApplyCpuSetting(false, profile.MaxFrequencyMhz);
                applied.Add(ok ? "最高频率" : $"频率失败({message})");
            }

            if (profile.CoreSelection >= 0)
            {
                var count = profile.CoreSelection >= GameCpuService.Topology.CoreCount ? 0 : profile.CoreSelection;
                var result = PerformanceService.ApplyCoreSelection(count);
                applied.Add(result.Ok ? "核心选择" : $"核心选择失败({result.Message})");
            }

            // 5) 核心调度
            if (profile.CoreModeCaptured)
            {
                SettingsService.Update(s => s.GameCoreMode = profile.CoreMode);
                var result = GameCpuService.Configure(SettingsService.Current.GameCoreCount, profile.CoreMode);
                applied.Add(result.Ok ? "核心调度" : $"核心调度失败({result.Message})");
            }
        });

        var detail = string.Join("、", applied);
        DebugLog.Write($"[供电] 切换为{(onAc ? "插电" : "电池")}，已应用：{detail}");
        return detail;
    }

    private static bool HasAny(PowerProfile p) =>
        p.PowerMode >= 0 || p.FrameLimitFps >= 0
        || (p.CpuPowerEnabled && p.CpuPowerPl1 > 0)
        || p.Boost >= 0 || p.MaxFrequencyMhz >= 0 || p.CoreModeCaptured || p.CoreSelection >= 0;
}
