using System.Runtime.InteropServices;

namespace GameLocker.Services;

internal static class FrameLimitService
{
    internal sealed record State(bool Supported, string Backend, int Fps, string Message, int Minimum = 30, int Maximum = 144, int Step = 1);
    private static readonly object Gate = new();
    private static readonly object CacheGate = new();
    private static State _cached = new(false, "", 0, "正在检测锁帧后端…");

    /// <summary>上一次读取并缓存的锁帧状态（线程安全；UI 线程只读，避免在磁贴绘制时调用驱动）</summary>
    public static State Cached
    {
        get { lock (CacheGate) return _cached; }
    }

    private static void SetCached(State state)
    {
        lock (CacheGate) _cached = state;
    }

    /// <summary>最近一次应用结果（诊断用，随日志导出）</summary>
    public static string LastApply { get; private set; } = "未尝试";

    /// <summary>后台刷新缓存（内部 Task.Run，不阻塞 UI 线程）</summary>
    public static async Task<State> RefreshCacheAsync()
    {
        var state = await Task.Run(Read).ConfigureAwait(false);
        SetCached(state);
        return state;
    }

    public static State Read()
    {
        lock (Gate)
        {
            try
            {
                var state = ReadNative(out var reason) ?? ReadFallback(reason);
                // 驱动默认上限通常是 144；按当前显示器刷新率放宽，避免 165/240Hz 屏幕设不到更高
                var hz = ReadDisplayHz();
                if (hz > state.Maximum) state = state with { Maximum = Math.Clamp(hz, state.Minimum + 1, 480) };
                DebugLog.Write($"[锁帧] 读取：backend={state.Backend} supported={state.Supported} fps={state.Fps} range={state.Minimum}-{state.Maximum}/{state.Step} hz={hz} msg={state.Message}");
                SetCached(state); // 顺带更新缓存，磁贴可直接读 Cached 展示
                return state;
            }
            catch (Exception ex)
            {
                // 后端检测异常不能让整个缓存刷新链中断（否则后续 CPU 设置缓存不会更新）
                DebugLog.Write($"[锁帧] 读取异常：{ex}");
                var failed = new State(false, "", 0, "锁帧后端检测失败：" + ex.Message);
                SetCached(failed);
                return failed;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY;
        public int DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    /// <summary>当前主显示器刷新率（Hz）；读取失败返回 0</summary>
    private static int ReadDisplayHz()
    {
        try
        {
            var mode = new DevMode
            {
                DeviceName = "", FormName = "",
                Size = (short)Marshal.SizeOf<DevMode>()
            };
            return EnumDisplaySettings(null, -1, ref mode) && mode.DisplayFrequency > 1
                ? mode.DisplayFrequency
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>当前主显示器纵向分辨率（像素）；读取失败返回 0</summary>
    internal static int ReadDisplayHeight()
    {
        try
        {
            var mode = new DevMode
            {
                DeviceName = "", FormName = "",
                Size = (short)Marshal.SizeOf<DevMode>()
            };
            return EnumDisplaySettings(null, -1, ref mode) && mode.PelsHeight > 0
                ? mode.PelsHeight
                : 0;
        }
        catch { return 0; }
    }

    public static (bool Ok, string Message) Apply(State selected, int fps)
    {
        var result = ApplyCore(selected, fps);
        LastApply = $"backend={selected.Backend} supported={selected.Supported} fps={fps} → ok={result.Ok} msg={result.Message}";
        DebugLog.Write("[锁帧] 应用：" + LastApply);
        return result;
    }

    private static (bool Ok, string Message) ApplyCore(State selected, int fps)
    {
        if (!selected.Supported) return (false, "没有可用的限帧后端");
        if (fps != 0 && (fps < selected.Minimum || fps > selected.Maximum || (fps - selected.Minimum) % selected.Step != 0))
            return (false, $"锁帧范围为 {selected.Minimum}–{selected.Maximum} FPS，步长 {selected.Step}，或关闭");
        lock (Gate)
        {
            try
            {
                if (selected.Backend == "AMD ADLX")
                {
                    using var amd = new AmdFrameLimit();
                    amd.Write(fps);
                    return (true, fps == 0 ? "已关闭 AMD FRTC 全局锁帧" : $"AMD FRTC 已保存：{fps} FPS；仅支持该功能的全屏游戏生效");
                }
                if (selected.Backend == "Intel IGCL")
                {
                    IntelGpuTemp.WriteFrameLimit(fps);
                    var check = IntelGpuTemp.ReadFrameLimit();
                    DebugLog.Write($"[锁帧] Intel 写入 {fps} → 读回 supported={check.Supported} fps={check.Fps} msg={check.Message}");
                    if (!check.Supported)
                        return (false, "Intel 驱动写入后无法读回限帧状态，可能被驱动拒绝");
                    if (check.Fps != fps)
                        return (false, $"Intel 驱动未接受 {fps} FPS（读回 {check.Fps}），请确认没有游戏内限帧覆盖");
                    return (true, fps == 0 ? "已关闭 Intel 全局锁帧" : $"Intel 全局锁帧已保存：{fps} FPS；部分游戏需重启");
                }
                if (selected.Backend != "NVIDIA 驱动") return ApplyFallback(selected, fps);
                using var api = new NvidiaApi();
                int old = api.Read();
                api.Write(fps);
                if (api.Read() != fps)
                {
                    api.Write(old);
                    return (false, "驱动读回值不一致，已尝试恢复原值；未启用第三方限帧");
                }
                return (true, fps == 0 ? "已关闭 NVIDIA 全局锁帧" : $"NVIDIA 全局锁帧已保存：{fps} FPS；部分游戏需重启");
            }
            catch (Exception ex)
            {
                return (false, "锁帧未完成：" + ex.Message + "；未切换其他后端，请刷新确认当前值");
            }
        }
    }

    private static State? ReadNative(out string reason)
    {
        var errors = new List<string>();
        try
        {
            using var api = new NvidiaApi();
            reason = "";
            return new(true, "NVIDIA 驱动", api.Read(), "NVIDIA 全局配置 · 仅 NVIDIA 渲染的程序生效，独立游戏配置可覆盖；部分游戏需重启");
        }
        catch (Exception ex) { errors.Add("NVIDIA：" + ex.Message); }
        try
        {
            using var api = new AmdFrameLimit();
            reason = "";
            return new(true, "AMD ADLX", api.Read(), "AMD FRTC · 仅所选 AMD GPU 支持的全屏程序生效；部分游戏需重启", api.Minimum, api.Maximum, api.Step);
        }
        catch (Exception ex) { errors.Add("AMD：" + ex.Message); }
        try
        {
            var state = IntelGpuTemp.ReadFrameLimit();
            if (state.Supported) { reason = ""; return state; }
            errors.Add("Intel：" + state.Message);
        }
        catch (Exception ex) { errors.Add("Intel：" + ex.Message); }
        reason = string.Join("；", errors);
        return null;
    }

    private static State ReadFallback(string reason) => RtssFrameLimit.Read(reason);
    private static (bool Ok, string Message) ApplyFallback(State state, int fps) => RtssFrameLimit.Apply(state, fps);

    private sealed class RtssFrameLimit : IDisposable
    {
        private IntPtr _library;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate void Profile(string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int Property(string name, ref uint value, uint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Update();
        private T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        private RtssFrameLimit()
        {
            // RTSS must already be running: never install or silently start injection software.
            string? directory = null;
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("RTSS"))
            {
                using (process)
                {
                    try { directory = System.IO.Path.GetDirectoryName(process.MainModule?.FileName); } catch { }
                }
                if (directory != null) break;
            }
            if (directory == null) throw new InvalidOperationException("请先安装并启动 RTSS（当前驱动未提供可用的原生限帧）");
            try
            {
                _library = NativeLibrary.Load(System.IO.Path.Combine(directory, "RTSSHooks64.dll"));
                Function<Profile>("LoadProfile")("");
            }
            catch { Dispose(); throw; }
        }
        private uint Get()
        {
            uint value = 0;
            if (Function<Property>("GetProfileProperty")("FramerateLimit", ref value, 4) == 0)
                throw new InvalidOperationException("RTSS 无法读取全局 FramerateLimit");
            return value;
        }
        private void Set(uint value)
        {
            if (Function<Property>("SetProfileProperty")("FramerateLimit", ref value, 4) == 0)
                throw new InvalidOperationException("RTSS 拒绝写入限帧属性");
            Function<Profile>("SaveProfile")("");
            Function<Update>("UpdateProfiles")();
            Function<Profile>("LoadProfile")("");
        }
        public static State Read(string reason)
        {
            try
            {
                using var api = new RtssFrameLimit();
                uint value = api.Get();
                if (value > int.MaxValue) throw new InvalidOperationException("RTSS 使用特殊限帧格式，请在 RTSS 中调整");
                return new(true, "RTSS", (int)value, "厂商后端不可用：" + reason + "；使用 RTSS 全局默认配置，专属配置可覆盖；需保持 RTSS 运行");
            }
            catch (Exception ex) { return new(false, "", 0, "厂商后端不可用：" + reason + "；" + ex.Message); }
        }
        public static (bool Ok, string Message) Apply(State state, int fps)
        {
            if (state.Backend != "RTSS") return (false, "没有可用的限帧后端");
            // Do not fall back if a vendor backend has become available since the last read.
            var native = ReadNative(out _);
            if (native != null) return (false, $"{native.Backend} 后端已可用，请刷新后使用驱动限帧");
            using var api = new RtssFrameLimit();
            uint old = api.Get();
            api.Set((uint)fps);
            if (api.Get() != fps)
            {
                api.Set(old);
                return (false, "RTSS 读回不一致，已尝试恢复原值");
            }
            return (true, fps == 0 ? "已关闭 RTSS 全局锁帧" : $"RTSS 全局限帧已保存：{fps} FPS；需保持 RTSS 运行，检测和限帧开关须已开启");
        }
        public void Dispose()
        {
            if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        }
    }

    // Layout and interface IDs: NVIDIA nvapi.h, nvapi_interface.h and NvApiDriverSettings.h.
    private sealed class NvidiaApi : IDisposable
    {
        private const uint SettingId = 0x10835002;
        private IntPtr _library, _session, _profile;
        private bool _initialized;
        private Query _query = null!;
        private SessionCall? _destroy;
        private NoArgs? _unload;
        [StructLayout(LayoutKind.Explicit, Size = 12320, Pack = 4)]
        private struct Setting
        {
            [FieldOffset(0)] public uint Version;
            [FieldOffset(4100)] public uint Id;
            [FieldOffset(4104)] public uint Type;
            [FieldOffset(8220)] public uint Value;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Query(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionCall(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetProfile(IntPtr session, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting(IntPtr session, IntPtr profile, uint id, ref Setting setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSetting(IntPtr session, IntPtr profile, ref Setting setting);
        private T Function<T>(uint id) where T : Delegate
        {
            var ptr = _query(id);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("驱动未提供所需 NVAPI 接口");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }
        private static void Check(int status)
        {
            if (status != 0) throw new InvalidOperationException($"NVAPI 状态 {status}（权限不足时请以管理员身份运行）");
        }
        public NvidiaApi()
        {
            try
            {
                _library = NativeLibrary.Load(System.IO.Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
                _query = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));
                _unload = Function<NoArgs>(0xd22bdd7e);
                _destroy = Function<SessionCall>(0xdad9cff8);
                Check(Function<NoArgs>(0x0150e828)());
                _initialized = true;
                Check(Function<Create>(0x0694d52e)(out _session));
                Reload();
            }
            catch { Dispose(); throw; }
        }
        private void Reload()
        {
            Check(Function<SessionCall>(0x375dbd6b)(_session));
            Check(Function<GetProfile>(0xda8466a0)(_session, out _profile));
        }
        public int Read()
        {
            Reload();
            var setting = new Setting { Version = 12320 | (1u << 16), Id = SettingId };
            int status = Function<GetSetting>(0x73bf8338)(_session, _profile, SettingId, ref setting);
            // An unset global value uses FRL_FPS_DEFAULT (disabled).
            if (status == -160) return 0; // NVAPI_SETTING_NOT_FOUND
            Check(status);
            if (setting.Type != 0 || setting.Value > 1023) throw new InvalidOperationException("驱动返回了不支持的限帧格式");
            return (int)setting.Value;
        }
        public void Write(int fps)
        {
            var setting = new Setting { Version = 12320 | (1u << 16), Id = SettingId, Value = (uint)fps };
            Check(Function<SetSetting>(0x577dd202)(_session, _profile, ref setting));
            Check(Function<SessionCall>(0xfcbc7e14)(_session));
        }
        public void Dispose()
        {
            if (_session != IntPtr.Zero) { _destroy?.Invoke(_session); _session = IntPtr.Zero; }
            if (_initialized) { _unload?.Invoke(); _initialized = false; }
            if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        }
    }
}
