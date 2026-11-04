using System.IO.MemoryMappedFiles;
using System.Text;

namespace GameLocker.Services;

/// <summary>只读 HWiNFO 实时共享内存中的 CPU GT 温度，不使用封装温度或历史文件。</summary>
internal static class HwInfoGtTemperature
{
    private static readonly object Gate = new();
    public static string Status { get; private set; } = "未尝试";

    public static double Read()
    {
        lock (Gate)
        {
            try
            {
                using var memory = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
                using var view = memory.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                using var mutex = OpenMutex();
                var acquired = false;
                try
                {
                    if (mutex is not null)
                    {
                        try { acquired = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) return Unavailable("HWiNFO 正在更新传感器");
                    }
                    if (view.Capacity < 44) return Unavailable("共享内存头不完整");
                    var header = new byte[44];
                    view.ReadArray(0, header, 0, header.Length);
                    if (BitConverter.ToUInt32(header, 0) != 0x53695748 || BitConverter.ToUInt32(header, 4) != 2)
                        return Unavailable("共享内存签名或版本不可用");
                    var age = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - BitConverter.ToInt64(header, 12);
                    if (age is < -5 or > 10) return Unavailable("HWiNFO 数据已过期，请检查采样及共享内存支持");

                    long sensors = BitConverter.ToUInt32(header, 20);
                    long sensorSize = BitConverter.ToUInt32(header, 24);
                    long sensorCount = BitConverter.ToUInt32(header, 28);
                    long readings = BitConverter.ToUInt32(header, 32);
                    long readingSize = BitConverter.ToUInt32(header, 36);
                    long readingCount = BitConverter.ToUInt32(header, 40);
                    if (!ValidSection(sensors, sensorSize, sensorCount, 264, view.Capacity)
                        || !ValidSection(readings, readingSize, readingCount, 316, view.Capacity))
                        return Unavailable("共享内存区段布局不可用");

                    var result = double.NaN;
                    var matches = 0;
                    var source = "";
                    for (long i = 0; i < readingCount; i++)
                    {
                        var offset = readings + i * readingSize;
                        if (view.ReadUInt32(offset) != 1) continue; // SENSOR_TYPE_TEMP
                        var label = ReadText(view, offset + 12, 128);
                        // 使用原始英文名称，避免用户重命名把 CPU Package 误认为 GT。
                        if (!label.Equals("CPU GT Cores", StringComparison.OrdinalIgnoreCase)
                            && !label.Equals("CPU GT Cores (Graphics)", StringComparison.OrdinalIgnoreCase)
                            && !label.Equals("CPU GT", StringComparison.OrdinalIgnoreCase)) continue;
                        var sensorIndex = view.ReadUInt32(offset + 4);
                        if (sensorIndex >= sensorCount) continue;
                        var sensorName = ReadText(view, sensors + sensorIndex * sensorSize + 8, 128);
                        if (!sensorName.StartsWith("CPU [", StringComparison.OrdinalIgnoreCase)
                            || !sensorName.Contains("Intel", StringComparison.OrdinalIgnoreCase)) continue;
                        var unit = ReadText(view, offset + 268, 16);
                        if (unit != "°C" && unit != "C") continue;
                        var value = view.ReadDouble(offset + 284);
                        if (!double.IsFinite(value) || value is <= 0 or >= 150) continue;
                        result = value;
                        source = $"{sensorName} / {label}";
                        matches++;
                    }
                    var after = new byte[44];
                    view.ReadArray(0, after, 0, after.Length);
                    if (!header.AsSpan().SequenceEqual(after)) return Unavailable("HWiNFO 采样期间布局或时间变化");
                    if (matches != 1) return Unavailable($"可确认的 CPU GT 温度项数量={matches}，要求唯一");
                    Status = $"HWiNFO 共享内存：{source} = {result:0.#} ℃，数据年龄 {age:0} 秒";
                    return result;
                }
                finally
                {
                    if (acquired) mutex!.ReleaseMutex();
                }
            }
            catch (FileNotFoundException) { return Unavailable("请运行 HWiNFO 并开启 Shared Memory Support（共享内存支持）"); }
            catch (Exception ex) { return Unavailable("读取失败：" + ex.Message); }
        }
    }

    private static Mutex? OpenMutex()
    {
        try { return Mutex.OpenExisting(@"Global\HWiNFO_SM2_MUTEX"); }
        catch (WaitHandleCannotBeOpenedException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool ValidSection(long offset, long size, long count, int minimum, long capacity) =>
        offset >= 44 && size >= minimum && count is > 0 and <= 100000
        && offset <= capacity && size <= (capacity - offset) / count;

    private static string ReadText(MemoryMappedViewAccessor view, long offset, int size)
    {
        var bytes = new byte[size];
        view.ReadArray(offset, bytes, 0, size);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? size : end);
    }

    private static double Unavailable(string reason)
    {
        Status = reason;
        return double.NaN;
    }
}
