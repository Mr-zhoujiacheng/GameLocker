using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>系统小组件（WidgetKind.System）可切换的面板项：右摇杆 ←/→ 循环切换，A = 开关/打开，LB/RB = 调整</summary>
public enum SystemPanel
{
    /// <summary>Windows 设置（默认项）：A = 打开系统设置</summary>
    WindowsSettings,
    /// <summary>我的电脑（此电脑）：A = 打开</summary>
    ThisPc,
    /// <summary>音量：A = 静音/取消静音，LB/RB = 减小/增大</summary>
    Volume,
    /// <summary>亮度：A = 熄屏（降到最低）/ 还原，LB/RB = 减小/增大</summary>
    Brightness,
    /// <summary>WiFi：A = 开/关射频（系统拒绝时退化为打开设置页）</summary>
    Wifi,
    /// <summary>蓝牙：A = 开/关射频（系统拒绝时退化为打开设置页）</summary>
    Bluetooth,
    /// <summary>移动热点：A = 开/关（系统拒绝时退化为打开设置页）</summary>
    MobileHotspot,
    /// <summary>夜间模式：A = 开/关暖色滤镜（显示伽马表实现，等效降低蓝光）</summary>
    NightLight
}

/// <summary>
/// 系统小组件（WidgetKind.System）的领域逻辑：把常用的系统入口与开关集中到一个磁贴里，
/// 右摇杆 ←/→ 切换面板项，A = 打开入口 / 切换开关，LB/RB = 调整（音量、亮度）。
/// - 音量：CoreAudio（IAudioEndpointVolume）主音量与静音，无需管理员、非打包应用可用
/// - 亮度：DXVA2（GetMonitorBrightness/SetMonitorBrightness），笔记本/掌机内置屏与支持 DDC/CI 的外接屏
/// - WiFi / 蓝牙：WinRT Radio API 开关射频；非打包应用无包标识时系统可能拒绝，届时打开对应系统设置页
/// </summary>
public static class SystemPanelService
{
    /// <summary>切换顺序（与磁贴显示顺序一致）</summary>
    public static readonly SystemPanel[] Order =
    [
        SystemPanel.WindowsSettings, SystemPanel.ThisPc,
        SystemPanel.Volume, SystemPanel.Brightness, SystemPanel.Wifi, SystemPanel.Bluetooth,
        SystemPanel.MobileHotspot, SystemPanel.NightLight
    ];

    /// <summary>由持久化的选项序号取面板项（越界自动收敛到合法范围）</summary>
    public static SystemPanel FromOption(int option) => Order[Math.Clamp(option, 0, Order.Length - 1)];

    /// <summary>全部面板项都启用的位掩码</summary>
    public static int AllMask => (1 << Order.Length) - 1;

    /// <summary>规范化启用掩码：0 / 空集 / 越界位都按"全部启用"处理（避免编辑后切不出任何项）</summary>
    public static int NormalizeMask(int mask) => (mask & AllMask) == 0 ? AllMask : mask & AllMask;

    /// <summary>某一项是否参与右摇杆切换</summary>
    public static bool IsEnabled(int mask, int option) => (NormalizeMask(mask) & (1 << option)) != 0;

    /// <summary>按方向在当前启用的项里循环切换（回绕）；当前项未启用时落到第一个启用项。
    /// 返回新的选项序号（写回 LaunchItem.SystemOption）</summary>
    public static int CycleOption(int mask, int current, int delta)
    {
        var enabled = new List<int>(Order.Length);
        var m = NormalizeMask(mask);
        for (var i = 0; i < Order.Length; i++)
            if ((m & (1 << i)) != 0) enabled.Add(i);
        if (enabled.Count <= 1) return enabled.Count == 1 ? enabled[0] : 0;
        var pos = enabled.IndexOf(current);
        if (pos < 0) return enabled[0];
        return enabled[((pos + delta) % enabled.Count + enabled.Count) % enabled.Count];
    }

    /// <summary>当前选项是否仍启用；未启用时回落到第一个启用项（编辑里删掉了当前项时用）</summary>
    public static int NormalizeOption(int mask, int current) => IsEnabled(mask, current) ? current : CycleOption(mask, -1, 1);

    /// <summary>是否为"打开某个入口"的项（打开后按设置可收起启动器窗口）</summary>
    public static bool IsLauncher(SystemPanel p) =>
        p is SystemPanel.WindowsSettings or SystemPanel.ThisPc;

    /// <summary>是否支持 LB/RB 调整</summary>
    public static bool SupportsAdjust(SystemPanel p) => p is SystemPanel.Volume or SystemPanel.Brightness;

    /// <summary>面板项显示名（磁贴主标题）</summary>
    public static string Label(SystemPanel p) => p switch
    {
        SystemPanel.WindowsSettings => "Win 设置",
        SystemPanel.ThisPc => "我的电脑",
        SystemPanel.Volume => "音量",
        SystemPanel.Brightness => "亮度",
        SystemPanel.Wifi => "WiFi",
        SystemPanel.Bluetooth => "蓝牙",
        SystemPanel.MobileHotspot => "移动热点",
        _ => "夜间模式"
    };

    /// <summary>面板项图标字形（Segoe MDL2 Assets）</summary>
    public static string Glyph(SystemPanel p) => p switch
    {
        SystemPanel.WindowsSettings => "\uE713", // 齿轮
        SystemPanel.ThisPc => "\uE7F8",          // 电脑
        SystemPanel.Volume => "\uE767",          // 音量
        SystemPanel.Brightness => "\uE706",      // 亮度
        SystemPanel.Wifi => "\uE701",            // 无线网络
        SystemPanel.Bluetooth => "\uE702",       // 蓝牙
        SystemPanel.MobileHotspot => "\uE704",   // 网络共享（热点）
        _ => "\uE708"                            // 月亮（夜间模式）
    };

    /// <summary>当前读数文案（同步、无副作用）：入口类=提示按键，音量/亮度=百分比，WiFi/蓝牙/热点/夜间=开关状态。
    /// 亮度读 WMI 较重且不能在 UI 线程调用，这里只返回后台刷新好的缓存文案</summary>
    public static string Reading(SystemPanel p) => p switch
    {
        SystemPanel.WindowsSettings or SystemPanel.ThisPc => "按 A 打开",
        SystemPanel.Volume => ReadVolumeText(),
        SystemPanel.Brightness => _brightnessText ?? "—",
        SystemPanel.Wifi => WifiText(),
        SystemPanel.Bluetooth => BluetoothText(),
        SystemPanel.MobileHotspot => HotspotText(),
        _ => _nightLightOn ? "已开启" : "已关闭"
    };

    /// <summary>亮度读数文案缓存（后台线程写，UI 线程读）</summary>
    private static volatile string? _brightnessText;

    /// <summary>后台读取亮度并更新缓存（WMI/DDC 都在线程池执行：既避免阻塞界面，也避开与 XAML 争用 COM）</summary>
    public static async Task RefreshBrightnessAsync()
    {
        _brightnessText = await Task.Run(static () =>
        {
            try
            {
                return TryGetBrightness(out var min, out var cur, out var max)
                    ? $"{BrightnessPercent(min, cur, max):F0}%"
                    : "不支持调节";
            }
            catch
            {
                return "不支持调节";
            }
        });
    }

    private static string ReadVolumeText()
    {
        var (mute, scalar) = ReadVolume();
        if (mute is null || scalar is null) return "不可用";
        return mute.Value ? "已静音" : $"{scalar.Value * 100:F0}%";
    }

    private static string WifiText() => _wifiOn.HasValue
        ? (_wifiOn.Value ? "已开启" : "已关闭")
        : (NetworkService.IsWifiConnected() ? "已连接" : "未连接");

    private static string BluetoothText() => _btOn.HasValue
        ? (_btOn.Value ? "已开启" : "已关闭")
        : (NetworkService.IsBluetoothConnected() ? "已连接" : "未连接");

    /// <summary>移动热点状态文案（首次读取前显示"按 A 切换"）</summary>
    private static string HotspotText() => _hotspotOn.HasValue
        ? (_hotspotOn.Value ? "已开启" : "已关闭")
        : "按 A 切换";

    // ---- 交互 ----

    /// <summary>A 键语义：打开入口 / 切换开关。返回给用户看的结果文案（由调用方显示在提示条上）</summary>
    public static async Task<string> ToggleAsync(SystemPanel p)
    {
        switch (p)
        {
            case SystemPanel.WindowsSettings:
                Open("ms-settings:");
                return "已打开 Windows 设置";
            case SystemPanel.ThisPc:
                Open("shell:MyComputerFolder");
                return "已打开此电脑";
            case SystemPanel.Volume:
                return ToggleMute();
            case SystemPanel.Brightness:
                // WMI 写亮度放到后台线程，避免在 UI 线程做 WMI/COM 调用
                var msg = await Task.Run(ToggleScreenOff);
                await RefreshBrightnessAsync();
                return msg;
            case SystemPanel.Wifi:
                return await ToggleRadioAsync(Windows.Devices.Radios.RadioKind.WiFi, "WiFi", "network-wifi");
            case SystemPanel.Bluetooth:
                return await ToggleRadioAsync(Windows.Devices.Radios.RadioKind.Bluetooth, "蓝牙", "bluetooth");
            case SystemPanel.MobileHotspot:
                return await ToggleHotspotAsync();
            default:
                return ToggleNightLight();
        }
    }

    /// <summary>LB/RB 调整音量：±5%。返回 false = 音量控制不可用</summary>
    public static bool Adjust(SystemPanel p, int delta) =>
        p == SystemPanel.Volume && AdjustVolume(delta);

    /// <summary>亮度 LB/RB 调整：±10%（钳到设备量程）。调用方需在线程池执行（WMI 不在 UI 线程跑）</summary>
    public static bool AdjustBrightness(int delta)
    {
        if (!TryGetBrightness(out var min, out var cur, out var max)) return false;
        var span = Math.Max(1, (int)max - (int)min);
        var next = (uint)Math.Clamp((int)cur + delta * (span / 10), (int)min, (int)max);
        return SetBrightness(next);
    }

    /// <summary>刷新 WiFi / 蓝牙射频状态缓存（供磁贴读数用；射频对象只枚举一次，之后直接读 State）</summary>
    public static async Task RefreshRadiosAsync()
    {
        try
        {
            if (!_radiosScanned)
            {
                foreach (var r in await Windows.Devices.Radios.Radio.GetRadiosAsync())
                {
                    if (r.Kind == Windows.Devices.Radios.RadioKind.WiFi) _wifiRadio ??= r;
                    else if (r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth) _btRadio ??= r;
                }
                _radiosScanned = true;
            }
            if (_wifiRadio is not null) _wifiOn = _wifiRadio.State == Windows.Devices.Radios.RadioState.On;
            if (_btRadio is not null) _btOn = _btRadio.State == Windows.Devices.Radios.RadioState.On;
            // 移动热点状态：管理器对象缓存，失败时保持原值（按 A 仍可走打开设置页的降级路径）
            var hotspot = GetHotspotManager();
            if (hotspot is not null)
                _hotspotOn = hotspot.TetheringOperationalState ==
                    Windows.Networking.NetworkOperators.TetheringOperationalState.On;
        }
        catch (Exception ex)
        {
            // 无射频硬件 / 系统拒绝读取：标记已扫描，避免每 1.5s 重复枚举；
            // 读数回退到"是否已连接"判断（WiFi/蓝牙项按 A 也会走打开设置页的降级路径）
            _radiosScanned = true;
            Debug.WriteLine($"[SystemPanel] 读取射频状态失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- 入口 ----

    /// <summary>用 shell 打开一个系统位置（uri 为空 = 打开"资源管理器"本页）</summary>
    private static void Open(string uri)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe", uri) { UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] 打开 {uri} 失败: {ex.Message}");
        }
    }

    // ---- 音量（CoreAudio）----

    private static string ToggleMute()
    {
        var vol = GetVolume();
        if (vol is null) return "音量控制不可用";
        try
        {
            var ctx = Guid.Empty;
            vol.GetMute(out var mute);
            vol.SetMute(!mute, ref ctx);
            return mute ? "已取消静音" : "已静音";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] 静音切换失败: {ex.Message}");
            return "音量控制不可用";
        }
    }

    private static bool AdjustVolume(int delta)
    {
        var vol = GetVolume();
        if (vol is null) return false;
        try
        {
            var ctx = Guid.Empty;
            vol.GetMasterVolumeLevelScalar(out var scalar);
            var next = Math.Clamp(scalar + delta * 0.05f, 0f, 1f);
            vol.SetMasterVolumeLevelScalar(next, ref ctx);
            // 调大音量时自动取消静音（与系统音量键行为一致）
            if (next > 0)
            {
                vol.GetMute(out var mute);
                if (mute) vol.SetMute(false, ref ctx);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] 调整音量失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>当前（是否静音, 音量 0~1）；取不到返回 (null, null)</summary>
    private static (bool? Mute, float? Scalar) ReadVolume()
    {
        var vol = GetVolume();
        if (vol is null) return (null, null);
        try
        {
            vol.GetMute(out var mute);
            vol.GetMasterVolumeLevelScalar(out var scalar);
            return (mute, scalar);
        }
        catch { return (null, null); }
    }

    private static IAudioEndpointVolume? GetVolume()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out var device) != 0) return null;
            var iid = IID_IAudioEndpointVolume;
            if (device.Activate(ref iid, CLSCTXAll, IntPtr.Zero, out var obj) != 0) return null;
            return obj as IAudioEndpointVolume;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] 获取音量接口失败 {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---- 亮度（DDC/CI + ACPI/WMI 双通道）----

    /// <summary>亮度后端：掌机/笔记本内置屏通常不支持 DDC/CI，需走 WMI（ACPI）</summary>
    private enum BrightnessBackend { Unknown, Dxva2, Wmi }

    private static BrightnessBackend _brightnessBackend;
    /// <summary>WMI 也取不到亮度（台式独显屏/虚拟机）：记住后不再反复尝试</summary>
    private static bool _wmiUnavailable;

    /// <summary>A 键：亮度不为最低则熄屏（先记下当前值），已是最低则还原到上次的值</summary>
    private static string ToggleScreenOff()
    {
        if (!TryGetBrightness(out var min, out var cur, out var max)) return "当前设备不支持亮度调节";
        if (cur > min)
        {
            _savedBrightness = cur; // 熄屏前记住当前亮度，再按 A 还原到这里而不是默认值
            return SetBrightness(min) ? "已关闭背光（再按 A 恢复）" : "关闭背光失败";
        }
        if (SetBrightness(_savedBrightness)) return "已恢复亮度";
        return SetBrightness(max) ? "已恢复亮度" : "恢复亮度失败"; // 记忆值越界（显示模式切换过）时用最大值兜底
    }

    /// <summary>写入亮度（钳到量程），成功时记住非最低值供"熄屏后还原"；DDC/CI 失败时自动改走 WMI</summary>
    private static bool SetBrightness(uint value)
    {
        if (!TryGetBrightness(out var min, out _, out var max)) return false;
        var v = (uint)Math.Clamp((int)value, (int)min, (int)max);
        bool ok;
        if (_brightnessBackend == BrightnessBackend.Wmi)
        {
            ok = TryWriteWmiBrightness(v);
        }
        else
        {
            ok = WithMonitor(h => SetMonitorBrightness(h, v));
            if (!ok && TryWriteWmiBrightness(v))
            {
                // 内置屏实际走的是 ACPI：改用 WMI，后续读数也走 WMI（否则读数会停在旧值）
                _brightnessBackend = BrightnessBackend.Wmi;
                ok = true;
            }
        }
        if (ok)
        {
            if (v > min) _savedBrightness = v;
            CacheWmiBrightness(v);
        }
        return ok;
    }

    /// <summary>上次"熄屏"前的亮度（用于 A 再按一次还原）</summary>
    private static uint _savedBrightness = 100;

    private static double BrightnessPercent(uint min, uint cur, uint max) =>
        max <= min ? 0 : (cur - min) * 100.0 / (max - min);

    /// <summary>读亮度：外接屏走 DDC/CI；掌机内置屏 DDC/CI 会返回 ERROR_GEN_FAILURE(31)，改走 WMI（量程 0~100）</summary>
    private static bool TryGetBrightness(out uint min, out uint current, out uint max)
    {
        if (_brightnessBackend != BrightnessBackend.Wmi)
        {
            uint lo = 0, cu = 0, hi = 0;
            if (WithMonitor(h => GetMonitorBrightness(h, out lo, out cu, out hi)))
            {
                _brightnessBackend = BrightnessBackend.Dxva2;
                min = lo;
                current = cu;
                max = hi;
                return true;
            }
            _brightnessBackend = BrightnessBackend.Wmi;
        }

        min = 0;
        max = 100;
        if (!_wmiUnavailable && TryReadWmiBrightness(out current)) return true;
        current = 0;
        return false;
    }

    // ---- WMI（root\WMI：WmiMonitorBrightness / WmiMonitorBrightnessMethods）----
    // 内置屏（掌机、笔记本）的亮度由 ACPI 暴露，DDC/CI 不通；WMI 量程固定 0~100。

    private static bool TryReadWmiBrightness(out uint current)
    {
        current = 0;
        if (_wmiCacheValid && Environment.TickCount64 - _wmiCachedAt < 1000)
        {
            current = _wmiCached;
            return true;
        }
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["CurrentBrightness"] is null) continue;
                    current = Convert.ToUInt32(mo["CurrentBrightness"]);
                    CacheWmiBrightness(current);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] WMI 读取亮度失败 {ex.GetType().Name}: {ex.Message}");
        }
        _wmiUnavailable = true;
        return false;
    }

    /// <summary>WMI 写亮度：Timeout 用 uint.MaxValue（等驱动完成），亮度 0~100</summary>
    private static bool TryWriteWmiBrightness(uint level)
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    mo.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, level });
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] WMI 写入亮度失败 {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    // 读值缓存：磁贴每 1.5s 刷新一次，WMI 查询比直接 API 贵，1s 内复用同一结果
    private static uint _wmiCached;
    private static long _wmiCachedAt;
    private static bool _wmiCacheValid;

    private static void CacheWmiBrightness(uint value)
    {
        _wmiCached = value;
        _wmiCachedAt = Environment.TickCount64;
        _wmiCacheValid = true;
        _wmiUnavailable = false;
    }

    /// <summary>对主显示器逐个物理显示器句柄执行操作（第一个成功即返回），用后立即销毁句柄</summary>
    private static bool WithMonitor(Func<IntPtr, bool> action)
    {
        try
        {
            var hMon = MonitorFromPoint(default, MonitorDefaultToPrimary);
            if (hMon == IntPtr.Zero) return false;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var count) || count == 0) return false;
            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors)) return false;
            try
            {
                foreach (var m in monitors)
                    if (action(m.hPhysicalMonitor)) return true;
                return false;
            }
            finally
            {
                foreach (var m in monitors) DestroyPhysicalMonitor(m.hPhysicalMonitor);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] 亮度接口调用失败 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ---- WiFi / 蓝牙（WinRT Radio）----

    /// <summary>null = 尚未读到（回退用连接状态判断）</summary>
    private static bool? _wifiOn;
    private static bool? _btOn;

    /// <summary>射频对象缓存：只枚举一次，之后直接读 State / SetStateAsync（避免每次读状态都重新枚举设备）</summary>
    private static Windows.Devices.Radios.Radio? _wifiRadio;
    private static Windows.Devices.Radios.Radio? _btRadio;
    private static bool _radiosScanned;

    /// <summary>切换射频开关：被系统拒绝（非打包应用无包标识）或无对应射频时，退化为打开对应设置页</summary>
    private static async Task<string> ToggleRadioAsync(Windows.Devices.Radios.RadioKind kind, string name, string settingsUri)
    {
        try
        {
            var radio = kind == Windows.Devices.Radios.RadioKind.WiFi ? _wifiRadio : _btRadio;
            if (radio is null)
            {
                foreach (var r in await Windows.Devices.Radios.Radio.GetRadiosAsync())
                    if (r.Kind == kind) { radio = r; break; }
                if (kind == Windows.Devices.Radios.RadioKind.WiFi) _wifiRadio = radio;
                else _btRadio = radio;
                _radiosScanned = true;
            }
            if (radio is not null)
            {
                var target = radio.State == Windows.Devices.Radios.RadioState.On
                    ? Windows.Devices.Radios.RadioState.Off
                    : Windows.Devices.Radios.RadioState.On;
                await radio.SetStateAsync(target);
                if (kind == Windows.Devices.Radios.RadioKind.WiFi) _wifiOn = target == Windows.Devices.Radios.RadioState.On;
                else _btOn = target == Windows.Devices.Radios.RadioState.On;
                return target == Windows.Devices.Radios.RadioState.On ? $"{name} 已开启" : $"{name} 已关闭";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] 切换{name}失败 {ex.GetType().Name}: {ex.Message}");
        }

        Open($"ms-settings:{settingsUri}");
        return $"系统不允许直接开关{name}，已打开设置页";
    }

    // ---- 移动热点（WinRT NetworkOperatorTetheringManager）----

    private static Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager? _hotspotManager;
    private static bool? _hotspotOn;
    private static bool _hotspotFailed; // 系统拒绝/无网络配置：记住失败，不再每次按 A 重复尝试

    private static Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager? GetHotspotManager()
    {
        if (_hotspotManager is not null || _hotspotFailed) return _hotspotManager;
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) { _hotspotFailed = true; return null; }
            _hotspotManager = Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager
                .CreateFromConnectionProfile(profile);
        }
        catch (Exception ex)
        {
            // 非打包应用缺少网络操作权限时系统会拒绝：记下失败并退化为打开设置页
            _hotspotFailed = true;
            Debug.WriteLine($"[SystemPanel] 获取热点管理器失败 {ex.GetType().Name}: {ex.Message}");
        }
        return _hotspotManager;
    }

    /// <summary>A 键：开/关移动热点；系统拒绝时退化为打开"移动热点"设置页</summary>
    private static async Task<string> ToggleHotspotAsync()
    {
        var mgr = GetHotspotManager();
        if (mgr is not null)
        {
            try
            {
                var on = mgr.TetheringOperationalState ==
                    Windows.Networking.NetworkOperators.TetheringOperationalState.On;
                await (on ? mgr.StopTetheringAsync() : mgr.StartTetheringAsync());
                _hotspotOn = !on;
                return on ? "移动热点已关闭" : "移动热点已开启";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemPanel] 切换移动热点失败 {ex.GetType().Name}: {ex.Message}");
            }
        }
        Open("ms-settings:network-mobilehotspot");
        return "系统不允许直接开关热点，已打开设置页";
    }

    // ---- 夜间模式（屏幕色温滤镜）----
    // 系统夜间模式没有公开 API，且注册表二进制格式随版本变化、写入有损坏设置的风险；
    // 这里用显示伽马表降低蓝/绿通道实现等效的暖色效果：A 切换，首次开启时备份原始伽马表，关闭时还原。

    private static bool _nightLightOn;
    private static ushort[]? _gammaBackup;

    private static string ToggleNightLight()
    {
        if (!_nightLightOn)
        {
            var ok = WithDisplayGamma((hdc, cur) =>
            {
                _gammaBackup ??= (ushort[])cur.Clone();
                var warm = (ushort[])cur.Clone();
                for (var i = 0; i < 256; i++)
                {
                    warm[256 + i] = (ushort)(cur[256 + i] * 0.92); // 绿：轻微压低
                    warm[512 + i] = (ushort)(cur[512 + i] * 0.70); // 蓝：明显压低 → 暖色
                }
                return SetDeviceGammaRamp(hdc, warm);
            });
            if (!ok) return "当前显示器不支持色温调节";
            _nightLightOn = true;
            return "夜间模式已开启";
        }

        var offOk = _gammaBackup is not null &&
                    WithDisplayGamma((hdc, cur) => SetDeviceGammaRamp(hdc, _gammaBackup));
        if (!offOk) return "恢复色温失败";
        _nightLightOn = false;
        return "夜间模式已关闭";
    }

    /// <summary>在主显示器 DC 上执行伽马表操作（成功后立即释放 DC）</summary>
    private static bool WithDisplayGamma(Func<IntPtr, ushort[], bool> action)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            var ramp = new ushort[3 * 256];
            return GetDeviceGammaRamp(hdc, ramp) && action(hdc, ramp);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPanel] 伽马表操作失败 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    // ---- P/Invoke：CoreAudio ----

    private const int EDataFlowRender = 0;      // eRender（扬声器）
    private const int ERoleMultimedia = 0;      // eMultimedia
    private const int CLSCTXAll = 23;           // CLSCTX_INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER

    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    // 方法顺序必须与 IAudioEndpointVolume 的 vtable 完全一致（否则调用会串到别的方法上）
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(ref Guid eventContext);
        int VolumeStepDown(ref Guid eventContext);
        int QueryHardwareSupport(out uint mask);
        int GetVolumeRange(out float min, out float max, out float increment);
    }

    // ---- P/Invoke：DXVA2 亮度 ----

    private const uint MonitorDefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, uint arraySize, [Out] PHYSICAL_MONITOR[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    // ---- P/Invoke：显示伽马表（夜间模式）----

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);
}
