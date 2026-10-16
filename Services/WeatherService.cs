using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace GameLocker.Services;

/// <summary>当前位置当天天气（界面只读快照）</summary>
public sealed class WeatherInfo
{
    /// <summary>定位到的城市名</summary>
    public string City { get; set; } = "";

    /// <summary>当前温度（摄氏度）</summary>
    public double Temp { get; set; }

    /// <summary>当天最高温</summary>
    public double TempMax { get; set; }

    /// <summary>当天最低温</summary>
    public double TempMin { get; set; }

    /// <summary>相对湿度（%）</summary>
    public int Humidity { get; set; }

    /// <summary>风速（km/h）</summary>
    public double Wind { get; set; }

    /// <summary>天气描述（中文）</summary>
    public string Text { get; set; } = "";

    /// <summary>天气图标字形（Segoe MDL2 Assets）</summary>
    public string Glyph { get; set; } = "\uE753";

    /// <summary>取得时间</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 天气服务：IP 定位（ipwho.is，失败回退 ipapi.co）→ Open-Meteo 取当天天气，两者均免密钥。
/// 结果缓存到 %APPDATA%\GameLocker\weather-cache.json，启动即有数据可显示；A 键 / 点击磁贴触发 Refresh。
/// 全部在后台线程完成，订阅方通过 StateChanged 事件自行 marshal 到 UI 线程刷新磁贴。
/// </summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLocker", "weather-cache.json");

    private static bool _fetching;

    /// <summary>当前天气（null=尚未取得任何数据）</summary>
    public static WeatherInfo? Current { get; private set; }

    /// <summary>正在联网获取</summary>
    public static bool Loading { get; private set; }

    /// <summary>最近一次获取失败原因（空=正常）</summary>
    public static string Error { get; private set; } = "";

    /// <summary>数据/加载状态变化（可能来自后台线程）</summary>
    public static event Action? StateChanged;

    static WeatherService()
    {
        try { Http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLocker/1.0"); } catch { }
        try
        {
            if (File.Exists(CachePath))
                Current = JsonSerializer.Deserialize<WeatherInfo>(File.ReadAllText(CachePath));
        }
        catch { /* 缓存损坏按无数据处理 */ }
    }

    /// <summary>无数据或数据超过 30 分钟时后台刷新一次（主页/磁贴集页显示时调用）</summary>
    public static void EnsureFresh()
    {
        if (Current is null || (DateTime.Now - Current.UpdatedAt).TotalMinutes >= 30) Refresh();
    }

    /// <summary>刷新天气（手柄 A / 点击磁贴）；已在获取中则不重复发起</summary>
    public static void Refresh()
    {
        if (_fetching) return;
        _ = FetchAsync();
    }

    private static async Task FetchAsync()
    {
        _fetching = true;
        Loading = true;
        Error = "";
        StateChanged?.Invoke();
        try
        {
            var (lat, lon, city) = await LocateAsync();
            var url = "https://api.open-meteo.com/v1/forecast"
                + "?latitude=" + lat.ToString("F4", CultureInfo.InvariantCulture)
                + "&longitude=" + lon.ToString("F4", CultureInfo.InvariantCulture)
                + "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m"
                + "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            var (text, glyph) = Describe(cur.GetProperty("weather_code").GetInt32());

            Current = new WeatherInfo
            {
                City = city,
                Temp = cur.GetProperty("temperature_2m").GetDouble(),
                Humidity = cur.GetProperty("relative_humidity_2m").GetInt32(),
                Wind = cur.GetProperty("wind_speed_10m").GetDouble(),
                TempMax = daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                TempMin = daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                Text = text,
                Glyph = glyph,
                UpdatedAt = DateTime.Now
            };
            SaveCache();
        }
        catch
        {
            Error = "天气获取失败";
        }
        finally
        {
            _fetching = false;
            Loading = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>IP 定位：返回 (纬度, 经度, 城市)；两个免密钥服务依次尝试</summary>
    private static async Task<(double Lat, double Lon, string City)> LocateAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(
                await Http.GetStringAsync("https://ipwho.is/?fields=success,city,latitude,longitude"));
            var r = doc.RootElement;
            if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                    r.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "");
        }
        catch { /* 换下一个服务 */ }

        using var fallback = JsonDocument.Parse(await Http.GetStringAsync("https://ipapi.co/json/"));
        var f = fallback.RootElement;
        return (f.GetProperty("latitude").GetDouble(), f.GetProperty("longitude").GetDouble(),
            f.TryGetProperty("city", out var fc) ? fc.GetString() ?? "" : "");
    }

    /// <summary>WMO 天气代码 →（中文描述, Segoe MDL2 图标字形）</summary>
    private static (string Text, string Glyph) Describe(int code) => code switch
    {
        0 => ("晴", "\uE706"),                              // Brightness
        1 => ("少云", "\uE706"),
        2 => ("多云", "\uE753"),                            // Cloud
        3 => ("阴", "\uE753"),
        45 or 48 => ("雾", "\uE753"),
        51 or 53 or 55 or 56 or 57 => ("毛毛雨", "\uEB42"),  // Drop
        61 or 63 or 65 or 66 or 67 => ("雨", "\uEB42"),
        80 or 81 or 82 => ("阵雨", "\uEB42"),
        95 or 96 or 99 => ("雷雨", "\uEB42"),
        71 or 73 or 75 or 77 => ("雪", "\uE9CA"),           // Frigid
        85 or 86 => ("阵雪", "\uE9CA"),
        _ => ("未知", "\uE753")
    };

    private static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(Current));
        }
        catch { /* 缓存写入失败不影响本次显示 */ }
    }
}
