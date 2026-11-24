using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 底栏的全局触摸输入通道（原始输入 HID 触摸屏）。
///
/// 为什么需要它：底栏是 WS_EX_NOACTIVATE 的浮层，XAML 内容又托管在独立的
/// DesktopChildSiteBridge 子窗口里，鼠标靠 <see cref="DockInputHook"/> 的
/// WH_MOUSE_LL 兜住才有反应。触摸则不同——触摸帧被 WinUI 的指针管道吃掉，
/// 不再提升成鼠标消息，WH_MOUSE_LL 一条也收不到，于是"鼠标能点、手指点不了"。
///
/// 这里用 RegisterRawInputDevices(RIDEV_INPUTSINK) 在屏外 sink 窗口上接收触摸屏
/// 原始 HID 报文，直接解析出首触点坐标（字段偏移、逻辑范围换算与
/// <see cref="EdgeGestureService"/> 的触摸通道同一套，已在该通道验证过），
/// 再按"按下 / 移动 / 抬起"抛给底栏合成为一次左键点击。
/// RIDEV_INPUTSINK 是驱动级数据流，完全不受窗口命中测试 / Z 序 / 前台状态影响。
/// </summary>
internal static class TouchInputHook
{
    /// <summary>首触点按下（屏幕物理像素）。</summary>
    public static event Action<int, int>? Down;

    /// <summary>手指按住期间移动（屏幕物理像素）。</summary>
    public static event Action<int, int>? Move;

    /// <summary>手指抬起（最后一个已知位置的屏幕物理像素）。</summary>
    public static event Action<int, int>? Up;

    private const uint WM_INPUT = 0x00FF;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEHID = 2;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint HidP_Input = 0;

    private const ushort UsagePageDesktop = 0x01;
    private const ushort UsagePageDigitizer = 0x0D;
    private const ushort UsageTouchScreen = 0x04;
    private const ushort UsageContactCount = 0x54;
    private const ushort UsageTipSwitch = 0x42;
    private const ushort UsageX = 0x30;
    private const ushort UsageY = 0x31;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;

    private static Thread? _thread;
    private static volatile bool _running;
    private static volatile uint _threadId;

    private static IntPtr _sink;
    private static WndProcDelegate? _sinkProc; // 窗口类存在期间保持同一个委托
    private static readonly string _sinkClassName = $"GLDockTouchSink_{Guid.NewGuid():N}";

    private static IntPtr _rawBuf;
    private static uint _rawBufSize;
    private static IntPtr _capsBuf;
    private static readonly ushort[] _usageList = new ushort[64];

    /// <summary>hDevice → HID preparsed data（-1 = 已判定非触摸屏，跳过）</summary>
    private static readonly Dictionary<IntPtr, IntPtr> _preparsed = new();
    /// <summary>hDevice → 含 TipSwitch 的 Finger 集合索引</summary>
    private static readonly Dictionary<IntPtr, ushort[]> _fingerLc = new();

    // HID 触摸 X/Y 逻辑单位 → 屏幕像素的线性换算（_lxMax=0 未初始化，-1 取不到范围保持原始值）
    private static int _lxMin, _lxMax, _lyMin, _lyMax;

    /// <summary>当前是否有手指按着（只跟踪首指）。</summary>
    private static bool _down;
    private static int _lastX, _lastY;

    private static int _logCount;

    /// <summary>启动全局触摸监听（幂等）。只在第一块底栏创建时调用。</summary>
    public static void Start()
    {
        if (_running || _thread?.IsAlive == true) return;
        _running = true;
        var thread = new Thread(Run) { IsBackground = true, Name = "DockTouchSink" };
        // 与 EdgeGestureService 一致用 MTA：STA 会和 WinUI3 的 CoreMessaging/COM 公寓冲突
        thread.SetApartmentState(ApartmentState.MTA);
        _thread = thread;
        thread.Start();
    }

    /// <summary>停止监听并清理 sink 窗口。</summary>
    public static void Stop()
    {
        _running = false;
        var id = _threadId;
        if (id != 0) PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(2000); } catch { }
        }
        _thread = null;
    }

    private static void Run()
    {
        try
        {
            // 先取一次消息：让线程拥有消息队列，PostThreadMessage(WM_QUIT) 才能生效
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _threadId = GetCurrentThreadId();
            CreateSink();

            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log("触摸 sink 失败：" + ex.Message);
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>创建屏外 1x1 sink 窗口并注册全局触摸屏原始输入（RIDEV_INPUTSINK：非前台也收 WM_INPUT）。</summary>
    private static void CreateSink()
    {
        _sinkProc ??= SinkWndProc;
        var hInst = GetModuleHandle(null);
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_sinkProc),
            hInstance = hInst,
            lpszClassName = _sinkClassName
        };
        if (RegisterClassEx(ref cls) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            _sinkClassName, "", WS_POPUP,
            -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _sink = hwnd;

        // 屏外 1x1 也必须 Show：从未显示的窗口可能收不到 WM_INPUT
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        _rawBufSize = 16384;
        _rawBuf = Marshal.AllocHGlobal((int)_rawBufSize);
        _capsBuf = Marshal.AllocHGlobal(128); // HIDP_VALUE_CAPS 原生仅 72 字节

        // 只注册触摸屏（页 0x0D / 用法 0x04）；通配 UsagePage=0/Usage=0 非法（err=87）
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                UsagePage = UsagePageDigitizer,
                Usage = UsageTouchScreen,
                Flags = RIDEV_INPUTSINK,
                Target = hwnd
            }
        };
        var ok = RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Log($"touch sink created hwnd=0x{hwnd.ToInt64():X} reg={ok} err={Marshal.GetLastWin32Error()}");
    }

    private static void Cleanup()
    {
        try
        {
            if (_sink != IntPtr.Zero)
            {
                DestroyWindow(_sink);
                _sink = IntPtr.Zero;
            }
            if (_rawBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_rawBuf); _rawBuf = IntPtr.Zero; }
            if (_capsBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_capsBuf); _capsBuf = IntPtr.Zero; }
            UnregisterClass(_sinkClassName, GetModuleHandle(null));
        }
        catch { }
    }

    private static IntPtr SinkWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                if (_running)
                {
                    // 解析异常绝不往上传：钩子/窗口过程里抛异常会直接打断输入线程
                    try { ParseRawInput(lParam); }
                    catch (Exception ex) { Log("触摸解析异常：" + ex.GetType().Name + ": " + ex.Message); }
                }
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>解析 WM_INPUT：HID 触摸屏报告 → 首触点坐标 → 按下/移动/抬起。</summary>
    private static void ParseRawInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size < 32 || size > _rawBufSize) return;
        if (GetRawInputData(lParam, RID_INPUT, _rawBuf, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size) return;

        // x64 RAWINPUT 布局：header 24 字节（dwType@0 dwSize@4 hDevice@8 wParam@16）
        // | RAWHID: SizeHid@24 Count@28 数据@32
        if (Marshal.ReadInt32(_rawBuf, 0) != RIM_TYPEHID) return;
        var hDevice = Marshal.ReadIntPtr(_rawBuf, 8);
        var sizeHid = Marshal.ReadInt32(_rawBuf, 24);
        var count = Marshal.ReadInt32(_rawBuf, 28);
        var data = _rawBuf + 32;
        if (sizeHid <= 0 || count <= 0 || 32L + (long)sizeHid * count > size) return;

        // 设备 → 预解析数据缓存；-1 表示已判定非触摸屏（没有 Contact Count 字段）
        if (!_preparsed.TryGetValue(hDevice, out var pp))
        {
            pp = LoadPreparsed(hDevice);
            _preparsed[hDevice] = pp;
        }
        if (pp == new IntPtr(-1)) return;

        // Contact Count（页 0x0D 用法 0x54）：读不到就不是触摸屏
        var st = HidP_GetUsageValue(HidP_Input, UsagePageDigitizer, 0, UsageContactCount,
            out var contactCount, pp, data, (uint)sizeHid);
        if (st != HIDP_STATUS_SUCCESS)
        {
            _preparsed[hDevice] = new IntPtr(-1);
            Log($"device 0x{hDevice.ToInt64():X} 不是触摸屏 (status 0x{st:X})");
            return;
        }

        // 首触点：TipSwitch(0x0D/0x42) 是数组用法，按 Finger 集合枚举；X/Y(0x01/0x30,0x31) 是变量字段。
        if (contactCount > 0 && !_fingerLc.ContainsKey(hDevice))
        {
            var lcs = ProbeFingerCollections(pp, data, (uint)sizeHid);
            if (lcs.Length > 0) _fingerLc[hDevice] = lcs;
        }

        var tip = false;
        int x = 0, y = 0;
        var okX = false;
        var okY = false;
        if (contactCount > 0 && _fingerLc.TryGetValue(hDevice, out var fingerLcs))
        {
            ushort tipLc = 0;
            foreach (var lc in fingerLcs)
            {
                if (!HasTipSwitch(lc, pp, data, (uint)sizeHid)) continue;
                tip = true;
                tipLc = lc;
                break;
            }
            if (tip)
            {
                okX = TryReadXY(UsageX, tipLc, pp, data, (uint)sizeHid, out x);
                okY = TryReadXY(UsageY, tipLc, pp, data, (uint)sizeHid, out y);
                if (okX && okY) MapTouchXY(tipLc, pp, ref x, ref y);
            }
        }

        if (tip && okX && okY)
        {
            _lastX = x;
            _lastY = y;
            if (!_down)
            {
                _down = true;
                Invoke(Down, x, y);
                LogFirst($"[底栏] 触摸按下 ({x},{y})");
            }
            else
            {
                Invoke(Move, x, y);
            }
        }
        else if (_down)
        {
            // 手指抬起（或本帧已无触点）：用最后已知位置收尾
            _down = false;
            Invoke(Up, _lastX, _lastY);
            LogFirst($"[底栏] 触摸抬起 ({_lastX},{_lastY})");
        }
    }

    /// <summary>读取设备的 HID preparsed data；失败/非触摸设备返回 -1。</summary>
    private static IntPtr LoadPreparsed(IntPtr hDevice)
    {
        uint ppSize = 0;
        // 先传 NULL 取所需大小，再拷入自分配缓冲（API 不返回内部指针）
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppSize);
        if (ppSize == 0) { Log("preparse size=0"); return new IntPtr(-1); }

        var buf = Marshal.AllocHGlobal((int)ppSize);
        var r = GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, buf, ref ppSize);
        if (r == 0 || r == 0xFFFFFFFF)
        {
            Marshal.FreeHGlobal(buf);
            Log($"preparse 读取失败 r={r} err={Marshal.GetLastWin32Error()}");
            return new IntPtr(-1);
        }
        return buf;
    }

    /// <summary>探测含 TipSwitch 数组用法的 LinkCollection 索引（即 Finger 集合）；空结果不缓存，下帧重试。</summary>
    private static ushort[] ProbeFingerCollections(IntPtr pp, IntPtr data, uint sizeHid)
    {
        var found = new List<ushort>();
        // 0 = 顶层搜索，Finger 字段必然失败，从 1 起
        for (ushort lc = 1; lc < 64; lc++)
        {
            if (HasTipSwitch(lc, pp, data, sizeHid)) found.Add(lc);
        }
        return found.ToArray();
    }

    /// <summary>指定 Finger 集合当前帧的数组用法列表中是否含 TipSwitch(0x42)=触点按下。</summary>
    private static bool HasTipSwitch(ushort lc, IntPtr pp, IntPtr data, uint sizeHid)
    {
        uint len = (uint)_usageList.Length;
        if (HidP_GetUsages(HidP_Input, UsagePageDigitizer, lc, _usageList, ref len, pp, data, sizeHid) != HIDP_STATUS_SUCCESS)
            return false;
        for (uint i = 0; i < len && i < (uint)_usageList.Length; i++)
        {
            if (_usageList[i] == UsageTipSwitch) return true;
        }
        return false;
    }

    /// <summary>
    /// X/Y 读取：0x30/0x31 属于 0x01（通用桌面）页，位于各 Finger 集合内，逐集合读取；
    /// 读不到再退回顶层集合。与 EdgeGestureService 的修正一致（早期误用 0x09 Button 页 → 永远读不到坐标）。
    /// </summary>
    private static bool TryReadXY(ushort usage, ushort tipLc, IntPtr pp, IntPtr data, uint sizeHid, out int value)
    {
        if (HidP_GetUsageValue(HidP_Input, UsagePageDesktop, tipLc, usage, out var v, pp, data, sizeHid) == HIDP_STATUS_SUCCESS)
        {
            value = (int)v;
            return true;
        }
        if (HidP_GetUsageValue(HidP_Input, UsagePageDesktop, 0, usage, out v, pp, data, sizeHid) == HIDP_STATUS_SUCCESS)
        {
            value = (int)v;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>HID 逻辑单位 → 屏幕像素线性映射（主屏；范围取不到则保持原始值并记一次日志）。</summary>
    private static void MapTouchXY(ushort lc, IntPtr pp, ref int x, ref int y)
    {
        if (_lxMax == 0)
        {
            ushort n = 1;
            var stX = HidP_GetSpecificValueCaps(HidP_Input, UsagePageDesktop, lc, UsageX, _capsBuf, ref n, pp);
            if (stX == HIDP_STATUS_SUCCESS)
            {
                _lxMin = Marshal.ReadInt32(_capsBuf, 40);
                _lxMax = Marshal.ReadInt32(_capsBuf, 44);
            }
            n = 1;
            var stY = HidP_GetSpecificValueCaps(HidP_Input, UsagePageDesktop, lc, UsageY, _capsBuf, ref n, pp);
            if (stY == HIDP_STATUS_SUCCESS)
            {
                _lyMin = Marshal.ReadInt32(_capsBuf, 40);
                _lyMax = Marshal.ReadInt32(_capsBuf, 44);
            }
            if (!(stX == HIDP_STATUS_SUCCESS && stY == HIDP_STATUS_SUCCESS && _lxMax > _lxMin && _lyMax > _lyMin))
            {
                _lxMax = -1;
                Log($"触摸逻辑范围读取失败 X=0x{stX:X} Y=0x{stY:X}，坐标按原始值使用");
            }
        }
        // 逻辑范围缺失时不做换算（退化为原始值），至少不会把坐标全压到屏幕左上角
        if (_lxMax <= 0 || _lyMax <= 0) return;

        x = (int)Math.Round((x - _lxMin) * (double)(GetSystemMetrics(0) - 1) / (_lxMax - _lxMin));
        y = (int)Math.Round((y - _lyMin) * (double)(GetSystemMetrics(1) - 1) / (_lyMax - _lyMin));
    }

    private static void Invoke(Action<int, int>? handler, int x, int y)
    {
        if (handler is null) return;
        try
        {
            handler(x, y);
        }
        catch (Exception ex)
        {
            Log("触摸回调异常：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 诊断日志：写 DebugLog（设置里开启"记录运行日志"时才落盘），并另行写一份
    /// %APPDATA%\GameLocker\touch.log（与 EdgeGestureService 的 edge.log 同理，默认就写，
    /// 便于在没有开启运行日志的机器上确认"触摸帧到底有没有进来"）。高频帧限流 40 条。
    /// </summary>
    private static void Log(string text)
    {
        try { DebugLog.Write("[触摸] " + text); } catch { }
        if (_logCount > 40) return;
        _logCount++;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "touch.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>高频帧日志限流（最多 20 条）。</summary>
    private static void LogFirst(string text)
    {
        if (_logCount >= 20) return;
        Log(text);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDeviceList,
        uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand,
        IntPtr pData, ref uint pcbSize);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(uint reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(uint reportType, ushort usagePage, ushort linkCollection,
        ushort[] usageList, ref uint usageListLength, IntPtr preparsedData, IntPtr report, uint reportLength);

    /// <summary>
    /// HIDP_VALUE_CAPS（hidpi.h pack=4，共 72 字节）用裸缓冲输出，杜绝结构体编组。
    /// 原生签名 7 个参数：ValueCapsLength 是 in/out 的结构体个数，漏传会让原生把
    /// preprased 缓冲当长度指针写、返回 INVALID_PREPARSED_DATA 并破坏缓存数据。
    /// </summary>
    [DllImport("hid.dll")]
    private static extern int HidP_GetSpecificValueCaps(uint reportType, ushort usagePage, ushort linkCollection,
        ushort usage, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);
}
