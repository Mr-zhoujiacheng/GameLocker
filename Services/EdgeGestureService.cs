using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>呼出手势所在屏幕边缘（Bottom：任务栏在屏幕下方时，从底边向上滑呼出）</summary>
public enum SummonEdge { Left, Right, Top, Bottom }

/// <summary>
/// 平板边缘手势呼出：双通道监听"从屏幕边缘向中心"的滑动即触发呼出。
/// ① 全局低级鼠标钩子（WH_MOUSE_LL）：捕获鼠标拖动及触摸合成的鼠标消息（桌面/无触摸处理的前台时有效）；
/// ② 原始输入（Raw Input）触摸通道：RegisterRawInputDevices(RIDEV_INPUTSINK, HID 页 0x0D/用法 0x04 触摸屏)
///    + WM_INPUT 解析 HID 触摸报告（HidP_*）——驱动级数据流，**完全绕过窗口命中测试/Z 序/前台状态**，
///    任意应用（含最大化、独占触摸处理）前台时都能拿到触点坐标。
///    此前透明边缘条方案失败原因：shell 边缘预留地窗压在 TOPMOST 带上吞掉命中测试，WM_POINTER 永不投递。
/// 接收窗口为屏外 1x1 不可交互 sink，不再遮挡/吞掉任何边缘点击。两通道共用 600ms 冷却去重。
/// 钩子+sink 运行在独立 MTA 线程消息循环上；事件在该线程触发，订阅方需 marshal 回 UI 线程。
/// 开启时写注册表禁用 Windows 原生边缘手势，关闭时恢复。
/// 诊断：边缘附近事件与 HID 触摸帧写入 %APPDATA%\GameLocker\edge.log。
/// </summary>
public sealed class EdgeGestureService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_INPUT = 0x00FF;

    /// <summary>按下点距边缘在此范围内（物理像素）才视为边缘手势起点</summary>
    private const int EdgeZone = 70;
    /// <summary>向内拖动触发呼出的最小距离</summary>
    private const int Threshold = 60;
    /// <summary>朝边缘方向快滑触发收起的最小距离（比呼出更大：避免与列表滚动、磁贴拖拽混淆）</summary>
    private const int DismissThreshold = 120;

    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile SummonEdge _edge = SummonEdge.Left;
    private readonly HookProcDelegate _hookProc; // 防 GC 回收
    private IntPtr _hook = 0;
    private int _downX, _downY;
    private volatile bool _tracking;
    private bool _downNearEdge;     // 本次按下起点是否在边缘感应带内（决定抬起时判呼出还是收起）
    private long _lastTrigger;
    private long _lastDismiss;
    private long _lastBack;

    // ---- Raw Input 触摸 sink（屏外 1x1 窗口，仅接收 WM_INPUT）----
    private IntPtr _sink = 0;
    private WndProcDelegate? _sinkProc; // 防 GC 回收
    private IntPtr _rawBuf = 0;         // WM_INPUT 解析缓冲（非托管）
    private uint _rawBufSize;
    private readonly Dictionary<IntPtr, IntPtr> _preparsed = new(); // hDevice → HID preparsed data（-1=非触摸设备跳过）
    // hDevice → 含 TipSwitch 的 Finger 数组集合 LinkCollection 索引（首个 count>0 帧探测一次）
    private readonly Dictionary<IntPtr, ushort[]> _fingerLc = new();
    // 触摸 X/Y HID 逻辑单位 → 屏幕像素 线性映射（_lxMax=0 未初始化，-1 取不到范围保持原始值）
    private int _lxMin, _lxMax, _lyMin, _lyMax;
    // diag 变化门控：仅 (count,tip,x>>5,y>>5) 变化时记录，防 60Hz 刷屏
    private (int, int, int, int) _lastDiag = (-1, -1, -1, -1);
    // HID 触摸手势状态（单指即可判定边缘滑动）
    private bool _tDown;
    private int _tStartX, _tStartY, _tLastX, _tLastY;
    private long _tDownAt;

    /// <summary>边缘向内滑动被识别 → 请求呼出</summary>
    public event Action? SummonTriggered;

    /// <summary>从屏幕边缘起手、朝边缘外快滑被识别 → 请求最小化（中间横滑不再触发，
    /// 主页中间横滑由 MainPage 用于切换副页）。
    /// 走 Raw Input 触摸通道：不受页面命中测试/列表框触摸捕获影响，任何页面都能触发</summary>
    public event Action? DismissTriggered;

    /// <summary>从窗口内部向下快滑被识别 → 请求返回上一级（主页由上层决定改为最小化）。
    /// 同样走 Raw Input 触摸通道</summary>
    public event Action? BackTriggered;

    public EdgeGestureService()
    {
        _hookProc = HookCallback;
    }

    /// <summary>当前是否已启用（钩子运行中）</summary>
    public bool IsRunning => _running;

    /// <summary>启动监听 + 禁用系统边缘手势（幂等）。
    /// 关键：每次新建 Thread——已退出的 Thread 再 Start() 会抛 ThreadStateException，
    /// 在 XAML 事件回调里变成 stowed 异常 0xc000027b 崩 CoreMessagingXP（开关关→开即闪退的根因）。</summary>
    public void Start()
    {
        if (_disposed || _running) return;
        _running = true;
        DisableSystemEdgeGestures(true);
        var t = new Thread(Run) { IsBackground = true, Name = "EdgeGesture" };
        // 钩子线程必须 MTA：STA 与 WinUI3 CoreMessaging/COM 公寓冲突（WH_MOUSE_LL 只需消息泵，不需要 STA）
        t.SetApartmentState(ApartmentState.MTA);
        _thread = t;
        t.Start();
    }

    /// <summary>停止监听 + 恢复系统边缘手势（幂等）</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        DisableSystemEdgeGestures(false);
        // 唤醒阻塞在 GetMessage 的线程立即退出并卸载钩子（否则再 Start 时旧钩子残留双触发）
        var id = _threadId;
        if (id != 0) PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>切换呼出边缘（停靠侧/全屏变化时调用）</summary>
    public void SetEdge(SummonEdge edge)
    {
        _edge = edge;
    }

    /// <summary>兼容旧接口：sink 在屏外不遮挡任何输入，无需显隐控制</summary>
    public void SetStripVisible(bool visible) { }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        Log($"hook installed, handle={_hook}");
        if (_hook == 0)
        {
            Log($"SetWindowsHookEx failed err={Marshal.GetLastWin32Error()}");
            return;
        }
        CreateSink();

        var msg = new MSG();
        // 必须 out/ref 传递：按值传 MSG 时本地结构体永远不被填充，
        // DispatchMessage 收到全零消息 → WM_INPUT 从不投递到 sink（根因）
        while (_running && GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_sink != 0) { DestroyWindow(_sink); _sink = 0; }
        if (_rawBuf != 0) { Marshal.FreeHGlobal(_rawBuf); _rawBuf = 0; }
        if (_capsBuf != 0) { Marshal.FreeHGlobal(_capsBuf); _capsBuf = 0; }
        foreach (var v in _preparsed.Values)
            if (v != new IntPtr(-1)) Marshal.FreeHGlobal(v);
        _preparsed.Clear();
        _fingerLc.Clear();
        UnhookWindowsHookEx(_hook);
        _hook = 0;
        _threadId = 0;
        Log("hook removed, thread exit");
    }

    /// <summary>创建屏外 1x1 sink 窗口并注册全局原始输入（RIDEV_INPUTSINK：非前台也收 WM_INPUT）</summary>
    private void CreateSink()
    {
        _sinkProc = SinkWndProc;
        var hInst = GetModuleHandle(null);
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_sinkProc),
            hInstance = hInst,
            lpszClassName = "GLEdgeSink"
        };
        RegisterClassEx(ref cls); // 类已注册时失败也无妨，直接用同名类建窗

        var h = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "GLEdgeSink", "", WS_POPUP,
            -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        _sink = h;
        // 屏外 1x1 也必须 Show：从未显示的窗口可能收不到 WM_INPUT（隐藏 vs 屏外可见的投递差异）
        ShowWindow(h, SW_SHOWNOACTIVATE);
        _rawBufSize = 16384;
        _rawBuf = Marshal.AllocHGlobal((int)_rawBufSize);
        _capsBuf = Marshal.AllocHGlobal(128); // HIDP_VALUE_CAPS 原生仅 72

        // 注册触摸屏全局原始输入（通配 UsagePage=0/Usage=0 非法：err=87，且会连累整批）
        var cbSize = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
        var touch = new[] { new RAWINPUTDEVICE { UsagePage = 0x0D, Usage = 0x04, Flags = RIDEV_INPUTSINK, Target = h } };
        var okTouch = RegisterRawInputDevices(touch, 1, cbSize);
        Log($"sink created hwnd={h} shown touch reg={okTouch} err={Marshal.GetLastWin32Error()}");
    }

    private IntPtr SinkWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT && _running)
        {
            // 兜底：解析异常绝不崩溃进程（本会话已三次崩溃），记录后继续
            try { ParseRawInput(lParam); }
            catch (Exception ex) { DiagLog($"parse EX: {ex.GetType().Name}: {ex.Message}"); }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>解析 WM_INPUT：HID 触摸屏报告 → 首触点坐标 → 边缘滑动手势状态机</summary>
    private void ParseRawInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0 || size > _rawBufSize) { DiagLog($"rid size={size}"); return; }
        if (GetRawInputData(lParam, RID_INPUT, _rawBuf, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size)
        { DiagLog("rid read fail"); return; }

        // x64 RAWINPUT 布局：header24字节(dwType@0 dwSize@4 hDevice@8 wParam@16)
        // | RAWHID: SizeHid@24 Count@28 数据@32
        var type = Marshal.ReadInt32(_rawBuf, 0);
        if (type != RIM_TYPEHID) { DiagLog($"type={type} (not HID)"); return; }
        var hDevice = Marshal.ReadIntPtr(_rawBuf, 8);
        var sizeHid = Marshal.ReadInt32(_rawBuf, 24);
        var count = Marshal.ReadInt32(_rawBuf, 28);
        var data = _rawBuf + 32;
        if (sizeHid <= 0 || count <= 0 || 32 + sizeHid * count > size)
        { DiagLog($"bad frame sizeHid={sizeHid} count={count} size={size}"); return; }

        // 设备 → 预解析数据缓存；-1 表示已判定非触摸屏设备（无 Contact Count 字段）
        if (!_preparsed.TryGetValue(hDevice, out var pp))
        {
            // 每设备记录一次设备路径（定位触摸屏的实际 HID 接口）
            uint nameLen = 260;
            var nameBuf = new System.Text.StringBuilder((int)nameLen);
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, nameBuf, ref nameLen);
            DiagLog($"new device {hDevice} path={nameBuf}");
            // RIDI_PREPARSEDDATA：先传 NULL 取所需大小，再拷入自分配缓冲（API 不返回内部指针）
            uint ppSize = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppSize);
            if (ppSize == 0) { _preparsed[hDevice] = new IntPtr(-1); DiagLog("preparse size=0"); return; }
            var buf = Marshal.AllocHGlobal((int)ppSize);
            var r = GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, buf, ref ppSize);
            DiagLog($"preparse r={r} size={ppSize} err={Marshal.GetLastWin32Error()}");
            if (r == 0 || r == 0xFFFFFFFF)
            { Marshal.FreeHGlobal(buf); _preparsed[hDevice] = new IntPtr(-1); return; }
            pp = buf;
            _preparsed[hDevice] = pp;
        }
        if (pp == new IntPtr(-1)) return;

        // Contact Count（页 0x0D 用法 0x54）：读不到就不是触摸屏
        var st = HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x54, out var contactCount, pp, data, (uint)sizeHid);
        if (st != HIDP_STATUS_SUCCESS)
        {
            _preparsed[hDevice] = new IntPtr(-1);
            Log($"device {hDevice} not a touchscreen (status 0x{st:X})");
            return;
        }

        // 首触点：TipSwitch(0x0D/0x42) 是数组用法（GetUsageValue 读不到，上轮探测已证实），
        // 用 HidP_GetUsages 枚举 Finger 集合内数组用法判定按下；
        // X/Y(0x09/0x30,0x31) 是变量字段，GetUsageValue + 集合索引读取。
        if (contactCount > 0 && !_fingerLc.ContainsKey(hDevice))
        {
            var lcs = ProbeFingerCollections(pp, data, (uint)sizeHid);
            DiagLog($"finger probe: [{string.Join(",", lcs)}]");
            if (lcs.Length > 0) _fingerLc[hDevice] = lcs;
        }
        bool tip = false; int x = 0, y = 0;
        bool okX = false, okY = false;
        if (contactCount > 0 && _fingerLc.TryGetValue(hDevice, out var fingerLcs))
        {
            ushort tipLc = 0;
            foreach (var lc in fingerLcs)
                if (HasTipSwitch(lc, pp, data, (uint)sizeHid)) { tip = true; tipLc = lc; break; }
            if (tip)
            {
                okX = TryReadXY(0x30, tipLc, pp, data, (uint)sizeHid, out x);
                okY = TryReadXY(0x31, tipLc, pp, data, (uint)sizeHid, out y);
                if (okX && okY) MapTouchXY(tipLc, pp, ref x, ref y);
                if (!_dumped) { _dumped = true; DumpRawBytes(data, (uint)sizeHid); }
            }
        }
        var diagKey = ((int)contactCount, tip ? 1 : 0, x >> 5, y >> 5);
        if (diagKey != _lastDiag)
        {
            _lastDiag = diagKey;
            DiagLog($"count={contactCount} tip={(tip ? 1 : 0)} xOk={(okX ? 1 : 0)} yOk={(okY ? 1 : 0)} ({x},{y})");
        }
        if (contactCount > 0 && tip && okX && okY)
        {
            if (!_tDown)
            {
                _tDown = true;
                _tStartX = x; _tStartY = y;
                _tDownAt = Environment.TickCount64;
                Log($"touch down ({x},{y}) nearEdge={NearEdge(x, y)}");
            }
            _tLastX = x; _tLastY = y;
        }
        else if (_tDown)
        {
            // 手指抬起：判定手势。呼出=从边缘向中心内滑；收起=与呼出反向（从内部朝边缘快滑）
            _tDown = false;
            var inward = _edge switch
            {
                SummonEdge.Left => _tLastX - _tStartX,
                SummonEdge.Right => _tStartX - _tLastX,
                SummonEdge.Bottom => _tStartY - _tLastY,
                _ => _tLastY - _tStartY
            };
            var nearEdge = NearEdge(_tStartX, _tStartY);
            var elapsed = Environment.TickCount64 - _tDownAt;
            Log($"touch up ({_tLastX},{_tLastY}) inward={inward} nearEdge={nearEdge} ms={elapsed}");
            if (nearEdge && inward >= Threshold &&
                (Environment.TickCount64 - _lastTrigger) > 600)
            {
                _lastTrigger = Environment.TickCount64;
                try { SummonTriggered?.Invoke(); Log("touch summon dispatched"); }
                catch (Exception ex) { Log($"summon invoke EX: {ex.GetType().Name}: {ex.Message}"); }
            }
            // 向下滑：起点在窗口内部、竖直向下快滑（横向偏移小，避开横向拖拽）→ 返回上一级
            else if (!nearEdge && (_tLastY - _tStartY) >= DismissThreshold && Math.Abs(_tLastX - _tStartX) < 80 &&
                elapsed < 500 && (Environment.TickCount64 - _lastBack) > 600)
            {
                _lastBack = Environment.TickCount64;
                try { BackTriggered?.Invoke(); Log("touch back dispatched"); }
                catch (Exception ex) { Log($"back invoke EX: {ex.GetType().Name}: {ex.Message}"); }
            }
            // 滑到屏幕左/右边缘（终点在边缘带内、横向快滑且位移够大）→ 最小化；
            // 起点在中间、停在中间的横滑不触发（留给主页切副页）
            else if (!nearEdge && NearSideEdge(_tLastX) && Math.Abs(_tLastX - _tStartX) >= DismissThreshold &&
                Math.Abs(_tLastY - _tStartY) < 80 && elapsed < 500 &&
                (Environment.TickCount64 - _lastDismiss) > 600)
            {
                _lastDismiss = Environment.TickCount64;
                try { DismissTriggered?.Invoke(); Log("touch dismiss dispatched"); }
                catch (Exception ex) { Log($"dismiss invoke EX: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }

    /// <summary>探测含 TipSwitch 数组用法的 LinkCollection 索引（即 Finger 集合）；空结果不缓存，下帧重试</summary>
    private ushort[] ProbeFingerCollections(IntPtr pp, IntPtr data, uint sizeHid)
    {
        var found = new List<ushort>();
        for (ushort lc = 1; lc < 64; lc++) // 0=顶层搜索，Finger 字段必然失败，从 1 起
            if (HasTipSwitch(lc, pp, data, sizeHid)) found.Add(lc);
        return found.ToArray();
    }

    private readonly ushort[] _usageList = new ushort[64];
    // caps 输出用裸 IntPtr 缓冲（128 字节，原生写入上限 72）——
    // 本会话 coreclr 崩溃都在 P/Invoke 结构体/数组编组路径，ABI 必须零编组风险
    private IntPtr _capsBuf = 0;
    private bool _dumped;
    private bool _xyLogged;

    /// <summary>X/Y 读取：0x30/0x31 属于 0x01（通用桌面）页，位于各 Finger 集合内，逐集合读取。
    /// 关键修正——此前误用 0x09（Button）页 → 原生返回 USAGE_NOT_FOUND，触摸通道从未取到坐标，
    /// 手势实际只依赖鼠标钩子（触摸被提升为鼠标消息时才有效，前台应用原生处理触摸时失效）。</summary>
    private bool TryReadXY(ushort usage, ushort tipLc, IntPtr pp, IntPtr data, uint sizeHid, out int value)
    {
        if (HidP_GetUsageValue(HidP_Input, UsagePageDesktop, tipLc, usage, out var v, pp, data, sizeHid) == HIDP_STATUS_SUCCESS)
        { value = (int)v; LogXYOnce(usage, $"Value@lc{tipLc}={v}"); return true; }
        if (HidP_GetUsageValue(HidP_Input, UsagePageDesktop, 0, usage, out v, pp, data, sizeHid) == HIDP_STATUS_SUCCESS)
        { value = (int)v; LogXYOnce(usage, $"Value@top={v}"); return true; }
        if (!_xyLogged)
        {
            _xyLogged = true; // 失败也只记一次，防 60Hz 刷屏
            ushort n = 1;
            var st = HidP_GetSpecificValueCaps(HidP_Input, UsagePageDesktop, tipLc, usage, _capsBuf, ref n, pp);
            DiagLog($"XY {usage:X} read failed, caps st=0x{st:X} n={n}");
        }
        value = 0; return false;
    }

    /// <summary>X/Y 读取成功只记一次（防刷屏，同时保留首个真实值以便核对量纲）</summary>
    private void LogXYOnce(ushort usage, string info)
    {
        if (_xyLogged) return;
        _xyLogged = true;
        DiagLog($"XY {usage:X} via {info}");
    }

    /// <summary>一次性 dump 当前帧原始字节（无需任何 HID API，兜底诊断）</summary>
    private static void DumpRawBytes(IntPtr data, uint sizeHid)
    {
        var n = (int)Math.Min(sizeHid, 64);
        var bytes = new byte[n];
        Marshal.Copy(data, bytes, 0, n);
        DiagLog($"raw[{sizeHid}]: {BitConverter.ToString(bytes).Replace("-", " ")}");
    }

    /// <summary>指定 Finger 集合当前帧的数组用法列表中是否含 TipSwitch(0x42)=触点按下</summary>
    private bool HasTipSwitch(ushort lc, IntPtr pp, IntPtr data, uint sizeHid)
    {
        uint len = (uint)_usageList.Length;
        if (HidP_GetUsages(HidP_Input, 0x0D, lc, _usageList, ref len, pp, data, sizeHid) != HIDP_STATUS_SUCCESS)
            return false;
        for (uint i = 0; i < len && i < (uint)_usageList.Length; i++)
            if (_usageList[i] == 0x42) return true;
        return false;
    }

    /// <summary>一次性 dump caps 原始字节（人工核对字段偏移；裸缓冲无编组）</summary>
    private void DumpCaps(string tag, int n)
    {
        var bytes = new byte[48];
        Marshal.Copy(_capsBuf, bytes, 0, bytes.Length);
        DiagLog($"caps {tag} n={n}: {BitConverter.ToString(bytes).Replace("-", " ")}");
    }

    /// <summary>HID 逻辑单位 → 屏幕像素线性映射（范围取不到则保持原始值并只记一次日志）</summary>
    private void MapTouchXY(ushort lc, IntPtr pp, ref int x, ref int y)
    {
        if (_lxMax == 0) // 未初始化：从 value caps 查询 X/Y 逻辑范围（裸缓冲+手动偏移 LogicalMin@40/LogicalMax@44）
        {
            ushort n = 1;
            var stX = HidP_GetSpecificValueCaps(HidP_Input, UsagePageDesktop, lc, 0x30, _capsBuf, ref n, pp);
            int minX = 0, maxX = 0, minY = 0, maxY = 0;
            if (stX == HIDP_STATUS_SUCCESS)
            {
                DumpCaps("X", n);
                minX = Marshal.ReadInt32(_capsBuf, 40); maxX = Marshal.ReadInt32(_capsBuf, 44);
            }
            n = 1;
            var stY = HidP_GetSpecificValueCaps(HidP_Input, UsagePageDesktop, lc, 0x31, _capsBuf, ref n, pp);
            if (stY == HIDP_STATUS_SUCCESS)
            {
                DumpCaps("Y", n);
                minY = Marshal.ReadInt32(_capsBuf, 40); maxY = Marshal.ReadInt32(_capsBuf, 44);
            }
            if (stX == HIDP_STATUS_SUCCESS && stY == HIDP_STATUS_SUCCESS &&
                maxX > minX && maxY > minY)
            {
                _lxMin = minX; _lxMax = maxX; _lyMin = minY; _lyMax = maxY;
                DiagLog($"logical X[{_lxMin},{_lxMax}] Y[{_lyMin},{_lyMax}]");
            }
            else { _lxMax = -1; DiagLog($"value caps unavailable X=0x{stX:X} Y=0x{stY:X}, raw coords"); }
        }
        if (_lxMax <= 0) return;
        x = (int)Math.Round((x - _lxMin) * (double)(GetSystemMetrics(0) - 1) / (_lxMax - _lxMin));
        y = (int)Math.Round((y - _lyMin) * (double)(GetSystemMetrics(1) - 1) / (_lyMax - _lyMin));
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _running)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msgId = (int)wParam;

            if (msgId == WM_LBUTTONDOWN)
            {
                // 每次都记起点：抬起时按起点是否在边缘带内，判呼出或收起
                _downX = info.ptX; _downY = info.ptY;
                _downNearEdge = NearEdge(info.ptX, info.ptY);
                _tracking = true;
                if (_downNearEdge) Log($"down near edge {_edge} at ({info.ptX},{info.ptY})");
            }
            else if (msgId == WM_LBUTTONUP && _tracking)
            {
                _tracking = false;
                var inward = Inward(info.ptX, info.ptY);
                if (_downNearEdge)
                {
                    Log($"up at ({info.ptX},{info.ptY}) inward={inward}");
                    // 触摸会合成重复的鼠标消息，600ms 冷却去重，防双触发
                    if (inward >= Threshold && (Environment.TickCount64 - _lastTrigger) > 600)
                    {
                        _lastTrigger = Environment.TickCount64;
                        try { SummonTriggered?.Invoke(); Log("summon dispatched"); }
                        catch (Exception ex) { Log($"summon invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                    }
                    // 从屏幕边缘朝外滑 → 最小化（与从边缘内滑呼出相反方向）
                    else if (-inward >= DismissThreshold && (Environment.TickCount64 - _lastDismiss) > 600)
                    {
                        _lastDismiss = Environment.TickCount64;
                        try { DismissTriggered?.Invoke(); Log("dismiss dispatched"); }
                        catch (Exception ex) { Log($"dismiss invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
                else if ((info.ptY - _downY) >= DismissThreshold && Math.Abs(info.ptX - _downX) < 80 &&
                    (Environment.TickCount64 - _lastBack) > 600)
                {
                    _lastBack = Environment.TickCount64;
                    try { BackTriggered?.Invoke(); Log("back dispatched"); }
                    catch (Exception ex) { Log($"back invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                }
                // 滑到屏幕左/右边缘 → 最小化（与触摸通道同一规则）
                else if (NearSideEdge(info.ptX) && Math.Abs(info.ptX - _downX) >= DismissThreshold &&
                    Math.Abs(info.ptY - _downY) < 80 &&
                    (Environment.TickCount64 - _lastDismiss) > 600)
                {
                    _lastDismiss = Environment.TickCount64;
                    try { DismissTriggered?.Invoke(); Log("dismiss dispatched (edge end)"); }
                    catch (Exception ex) { Log($"dismiss invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>抬起点是否落在屏幕左/右边缘带内（与呼出边无关：最小化统一看"滑到左右屏幕边缘"）</summary>
    private bool NearSideEdge(int x)
    {
        var sw = GetSystemMetrics(0);
        return x <= EdgeZone || x >= sw - EdgeZone;
    }

    /// <summary>按下点是否落在当前呼出边缘的感应带内</summary>
    private bool NearEdge(int x, int y)
    {
        var sw = GetSystemMetrics(0);
        var sh = GetSystemMetrics(1);
        return _edge switch
        {
            SummonEdge.Right => x >= sw - EdgeZone,
            SummonEdge.Top => y <= EdgeZone,
            SummonEdge.Bottom => y >= sh - EdgeZone,
            _ => x <= EdgeZone
        };
    }

    /// <summary>抬起点相对按下点的"向屏幕中心"位移</summary>
    private int Inward(int upX, int upY) => _edge switch
    {
        SummonEdge.Left => upX - _downX,   // 向右（向中心）
        SummonEdge.Right => _downX - upX,  // 向左（向中心）
        SummonEdge.Bottom => _downY - upY, // 向上（向中心）
        _ => upY - _downY                  // 向下（向中心）
    };

    /// <summary>
    /// 禁用/恢复 Windows 原生边缘手势（开关开启时写入）：
    /// - Policies\EdgeUI AllowEdgeSwipe=0：官方策略，禁用左右屏幕边缘滑出（小组件/通知中心）。
    /// - Explorer\Advanced TaskbarDa=0：关闭小组件
    /// - Feeds ShellFeedsTaskbarViewMode=2：隐藏小组件按钮（双保险）
    /// 策略生效需重启资源管理器/系统——应用内绝不 Kill explorer（会拖崩本程序），
    /// 写入后随下次注销/重启自然生效。
    /// </summary>
    private static void DisableSystemEdgeGestures(bool disable)
    {
        SetDword(@"Software\Policies\Microsoft\Windows\EdgeUI", "AllowEdgeSwipe", disable ? 0 : null);
        // 小组件：0=关闭 1=开启（恢复默认显示）
        SetDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", disable ? 0 : 1);
        // 小组件按钮：2=隐藏 0=显示
        SetDword(@"Software\Microsoft\Windows\CurrentVersion\Feeds", "ShellFeedsTaskbarViewMode", disable ? 2 : 0);
    }

    private static void SetDword(string subKey, string name, int? value)
    {
        try
        {
            if (value == null)
            {
                using var rk = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                rk?.DeleteValue(name, false);
            }
            else
            {
                using var rk = Registry.CurrentUser.CreateSubKey(subKey);
                rk?.SetValue(name, value.Value, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    /// <summary>诊断日志（便于真机排查触摸是否到达）</summary>
    private static void Log(string text)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            File.AppendAllText(Path.Combine(dir, "GameLocker", "edge.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>窗口层诊断写入：与手势日志同一文件，便于对照"窗口内指针通道"与"原始输入通道"</summary>
    internal static void Trace(string text) => Log(text);

    /// <summary>高频解析诊断：每进程限 40 条，防 HID 帧刷屏</summary>
    private static int _diagCount;
    private static void DiagLog(string text)
    {
        if (_diagCount > 40) return;
        if (_diagCount == 40) { _diagCount++; Log("diag capped"); return; }
        _diagCount++;
        Log($"[diag] {text}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
    }

    // ---- P/Invoke ----
    private delegate IntPtr HookProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // 不进 Alt-Tab
    private const int WS_EX_NOACTIVATE = 0x08000000;   // 永不激活抢焦点

    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEHID = 2;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint HidP_Input = 0;
    /// <summary>通用桌面页：X/Y(0x30/0x31) 在此页（此前误用 0x09 Button 页 → 坐标读不到）</summary>
    private const ushort UsagePageDesktop = 0x01;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int ptX, ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDeviceList,
        uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand,
        System.Text.StringBuilder pData, ref uint pcbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand,
        IntPtr pData, ref uint pcbSize);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(uint reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(uint reportType, ushort usagePage, ushort linkCollection,
        ushort[] usageList, ref uint usageListLength, IntPtr preparsedData, IntPtr report, uint reportLength);

    /// <summary>HIDP_VALUE_CAPS（hidpi.h pack=4，总 72 字节）输出用裸缓冲（调用方保证 ≥72），杜绝结构体编组。
    /// 原生签名有 7 个参数：ValueCapsLength(PUSHORT) 是 in/out 的"结构体个数"——
    /// 漏传该参数（旧签名只有 6 个）会让原生把 preparsed 缓冲当长度指针写入、把栈上垃圾当 preparsedData
    /// → 返回 0xC0110001 INVALID_PREPARSED_DATA，且破坏 preparsed 数据（历史 coreclr AV 的隐患）</summary>
    [DllImport("hid.dll")]
    private static extern int HidP_GetSpecificValueCaps(uint reportType, ushort usagePage, ushort linkCollection,
        ushort usage, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);


}
