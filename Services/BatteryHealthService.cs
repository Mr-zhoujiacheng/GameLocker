using System.Management;

namespace GameLocker.Services;

/// <summary>
/// 电池健康（WMI root\WMI）：设计容量 / 满充容量 / 循环次数。
/// 台式机或无电池设备返回 null；读取较重，缓存 5 分钟，调用方在后台线程刷新。
/// </summary>
internal static class BatteryHealthService
{
    public sealed record BatteryHealthInfo(int DesignCapacity, int FullChargeCapacity, int? CycleCount)
    {
        /// <summary>健康度百分比；设计容量异常时返回 null</summary>
        public int? HealthPercent => DesignCapacity > 0
            ? (int)Math.Round(FullChargeCapacity * 100.0 / DesignCapacity)
            : null;
    }

    private static BatteryHealthInfo? _cache;
    private static DateTime _cachedAt;

    public static async Task<BatteryHealthInfo?> ReadAsync(bool force = false)
    {
        if (!force && _cache is not null && DateTime.Now - _cachedAt < TimeSpan.FromMinutes(5))
            return _cache;

        var value = await Task.Run(static () =>
        {
            try
            {
                var design = Query("BatteryStaticData", "DesignedCapacity");
                var full = Query("BatteryFullChargedCapacity", "FullChargedCapacity");
                if (design is null || full is null || design <= 0 || full <= 0) return null;

                uint? cycle = null;
                try { cycle = Query("BatteryCycleCount", "CycleCount"); }
                catch { /* 部分设备没有循环次数字段，不影响健康度 */ }

                return new BatteryHealthInfo((int)design.Value, (int)full.Value,
                    cycle is null ? null : (int)cycle.Value);
            }
            catch
            {
                return null;
            }
        });

        _cache = value;
        _cachedAt = DateTime.Now;
        return value;
    }

    private static uint? Query(string className, string property)
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI",
            $"SELECT {property} FROM {className}");
        foreach (ManagementBaseObject item in searcher.Get())
        {
            using (item)
            {
                var value = item[property];
                if (value is null) continue;
                try { return Convert.ToUInt32(value); }
                catch { /* 个别固件返回字符串/空值，跳过 */ }
            }
        }
        return null;
    }
}
