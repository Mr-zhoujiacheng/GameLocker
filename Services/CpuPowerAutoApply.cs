namespace GameLocker.Services;

/// <summary>
/// 把已保存的 CPU 功耗限制写回硬件。登录时由计划任务调用（此时已是管理员权限，不弹 UAC），
/// 睡眠唤醒后由主进程复用同一套逻辑重新写入。
/// </summary>
internal static class CpuPowerAutoApply
{
    private const int StartupWaitSeconds = 60;

    /// <summary>登录计划任务入口：写入后直接退出，不恢复，让设置持续生效。</summary>
    public static void RunFromTask()
    {
        var settings = SettingsService.Current;
        if (settings.CpuPowerPl1 <= 0) return;   // 没有保存过功耗，任务什么都不做
        var deadline = DateTime.UtcNow.AddSeconds(StartupWaitSeconds);
        while (true)
        {
            try
            {
                using var backend = new CpuPowerBackend();
                var state = backend.Read();
                if (state.Supported && state.Limits.Length > 0)
                {
                    RememberOriginals(state);
                    var targets = BuildTargets(state, settings.CpuPowerPl1, settings.CpuPowerPl2, settings.CpuPowerFppt);
                    ApplyAll(targets, state.Limits.Select(l => l.Watts).ToArray(), backend.Apply);
                    return;
                }
                DebugLog.Write("[功耗] 登录恢复暂不可用：" + state.Message);
            }
            catch (Exception ex) { DebugLog.Write("[功耗] 登录恢复异常：" + ex.Message); }
            // 登录初期驱动 / RAPL 可能还没就绪，等一会儿再试
            if (DateTime.UtcNow >= deadline) return;
            Thread.Sleep(3000);
        }
    }

    /// <summary>睡眠唤醒后重新写回。没有提权助手时交给登录计划任务（管理员权限，不弹 UAC）静默写回。</summary>
    public static void ReapplyIfActive()
    {
        var settings = SettingsService.Current;
        if (settings.CpuPowerPl1 <= 0) return;
        if (!CpuPowerService.Active)
        {
            CpuPowerService.TriggerLogonTask();
            return;
        }
        try
        {
            var state = CpuPowerService.Read();
            if (!state.Supported || state.Limits.Length == 0) return;
            var targets = BuildTargets(state, settings.CpuPowerPl1, settings.CpuPowerPl2, settings.CpuPowerFppt);
            if (targets.Length == 0) return;
            ApplyAll(targets, state.Limits.Select(l => l.Watts).ToArray(), CpuPowerService.Apply);
            _ = CpuPowerService.RefreshCacheAsync();
        }
        catch (Exception ex) { DebugLog.Write("[功耗] 唤醒恢复失败：" + ex.Message); }
    }

    /// <summary>记录固件允许的上限，供后续会话判断可写范围（登录任务写入限制后尤其需要）。</summary>
    public static void RememberOriginals(CpuPowerService.State state)
    {
        if (state.Limits.Length == 0) return;
        var pl1 = (int)Math.Floor(state.Limits[0].Maximum);
        var pl2 = state.Limits.Length > 1 ? (int)Math.Floor(state.Limits[1].Maximum) : 0;
        if (pl1 <= SettingsService.Current.CpuPowerOriginalPl1 && pl2 <= SettingsService.Current.CpuPowerOriginalPl2) return;
        SettingsService.Update(s =>
        {
            if (pl1 > s.CpuPowerOriginalPl1) s.CpuPowerOriginalPl1 = pl1;
            if (pl2 > s.CpuPowerOriginalPl2) s.CpuPowerOriginalPl2 = pl2;
        });
    }

    /// <summary>按保存值算出要写的目标（夹在可写范围内，并保证 PL1 ≤ PL2 ≤ FPPT）。</summary>
    private static (int Index, double Watts)[] BuildTargets(
        CpuPowerService.State state, int pl1, int pl2, int fppt)
    {
        var limits = state.Limits;
        if (pl1 <= 0 || limits.Length == 0) return [];
        var values = new double[limits.Length];
        values[0] = Math.Clamp(pl1, limits[0].Minimum, limits[0].Maximum);
        if (limits.Length > 1)
            values[1] = pl2 > 0 ? Math.Clamp(pl2, limits[1].Minimum, limits[1].Maximum) : values[0];
        if (limits.Length > 2)
            values[2] = fppt > 0 ? Math.Clamp(fppt, limits[2].Minimum, limits[2].Maximum) : values[1];
        for (var i = 1; i < limits.Length; i++) values[i] = Math.Max(values[i], values[i - 1]);
        return Enumerable.Range(0, limits.Length)
            .Where(i => limits[i].Writable && Math.Abs(values[i] - limits[i].Watts) > 0.05)
            .Select(i => (i, values[i]))
            .ToArray();
    }

    /// <summary>先抬高的项按索引从大到小写、再降低的项从小到大写，避免中间状态 PL1 &gt; PL2 被固件拒绝。</summary>
    private static void ApplyAll(
        (int Index, double Watts)[] targets, double[] current, Func<int, double, CpuPowerService.Result> apply)
    {
        var raises = targets.Where(t => t.Watts > current[t.Index] + 0.05).OrderByDescending(t => t.Index).ToArray();
        var lowers = targets.Where(t => t.Watts < current[t.Index] - 0.05).OrderBy(t => t.Index).ToArray();
        foreach (var (index, watts) in raises.Concat(lowers))
        {
            var result = apply(index, watts);
            DebugLog.Write($"[功耗] 写回 PL{index + 1} {watts:0.##}W：{result.Ok} {result.Message}");
            if (!result.Ok) return;
        }
    }
}
