using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 用 ETW 实时会话监听 Microsoft-Windows-DXGI 的 Present 事件，统计前台进程的真实帧率、帧时间与帧波动。
/// 原理与 PresentMon 一致：Present 事件每帧一条，按事件时间戳差得到帧时间。
/// 需要管理员权限（StartTrace 失败时静默降级，调用方显示"—"）。
/// </summary>
internal sealed class PresentEtwListener : IDisposable
{
    /// <summary>DXGI 提供程序 GUID（CA11C036-0102-4A2D-A6AD-F03CFED5D3C9）</summary>
    private static readonly Guid DxgiProviderId = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    /// <summary>Present / PresentMultiplaneOverlay 事件 ID</summary>
    private const ushort PresentEventId = 42;
    private const ushort PresentOverlayEventId = 43;

    private const string SessionName = "GameLocker-Monitor";
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const byte TRACE_LEVEL_INFORMATION = 4;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const uint EVENT_CONTROL_CODE_DISABLE_PROVIDER = 0;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;

    /// <summary>帧时间环形缓冲：保留最近 600 帧（10 秒@60fps，高帧率下也够 1 秒窗口）</summary>
    private const int FrameWindow = 600;
    /// <summary>帧率统计窗口（毫秒）</summary>
    private const double FpsWindowMs = 1000;
    /// <summary>输出给悬浮条绘制的帧时间样本数</summary>
    private const int HistoryMax = 180;

    private ulong _sessionHandle;
    private ulong _traceHandle;
    private Thread? _traceThread;
    private EventRecordCallback? _callback;
    private volatile bool _running;

    private readonly object _gate = new();
    private readonly double[] _intervals = new double[FrameWindow];
    private readonly long[] _timestamps = new long[FrameWindow];
    private int _count;
    private int _head;
    private long _lastTimestamp;
    private long _lastCallbackTick;

    /// <summary>被统计的目标进程（前台进程；0=无目标）</summary>
    public volatile int TargetPid;

    /// <summary>ETW 会话是否成功启动；false 表示无权/不可用，调用方应显示"—"</summary>
    public bool Available { get; private set; }

    /// <summary>最近一次启动失败的错误码（Win32/HRESULT），用于界面提示与排障</summary>
    public int StartError { get; private set; }

    /// <summary>启动失败的具体阶段说明（排障用）</summary>
    public string StartDiagnostic { get; private set; } = "";

    /// <summary>已收到的 Present 原始事件数（排障用：区分“没有事件”与“全是其它进程”）</summary>
    public long RawEvents { get; private set; }

    /// <summary>ProcessTrace 返回码（0=正常，非 0 表示消费线程异常）</summary>
    public int ProcessTraceError { get; private set; }

    /// <summary>启动实时会话；失败返回 false（不抛异常）</summary>
    public bool Start()
    {
        // 结构布局与原生不一致时直接放弃（避免封送错位导致进程崩溃），界面显示"—"
        if (!LayoutOk())
        {
            StartDiagnostic = $"布局不一致 {Marshal.SizeOf<EVENT_TRACE_LOGFILE>()}/{Marshal.SizeOf<TRACE_LOGFILE_HEADER>()}"
                + $"/{Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()}/{Marshal.SizeOf<EVENT_RECORD>()}";
            return false;
        }
        StartError = 0;
        StartDiagnostic = "";
        try
        {
            var propsPtr = AllocProperties(out _);
            if (propsPtr == IntPtr.Zero) return false;

            var start = StartTraceW(out _sessionHandle, SessionName, propsPtr);
            Marshal.FreeHGlobal(propsPtr);
            if (start == ERROR_ALREADY_EXISTS)
            {
                // 同名会话可能因上次异常退出残留，先停止再重试
                var stopProps = AllocProperties(out _);
                if (stopProps != IntPtr.Zero)
                {
                    ControlTraceW(0, SessionName, stopProps, EVENT_TRACE_CONTROL_STOP);
                    Marshal.FreeHGlobal(stopProps);
                }
                propsPtr = AllocProperties(out _);
                if (propsPtr == IntPtr.Zero) return false;
                start = StartTraceW(out _sessionHandle, SessionName, propsPtr);
                Marshal.FreeHGlobal(propsPtr);
            }
            if (start != 0)
            {
                _sessionHandle = 0;
                StartError = (int)start;
                StartDiagnostic = $"StartTrace={start}";
                return false;
            }

            var provider = DxgiProviderId;
            if (EnableTraceEx2(_sessionHandle, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                    TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero) != 0)
            {
                StartError = Marshal.GetLastWin32Error();
                StartDiagnostic = $"EnableTraceEx2={StartError}";
                StopSession();
                return false;
            }

            _callback = OnEvent;
            var logfile = new EVENT_TRACE_LOGFILE
            {
                LoggerName = SessionName,
                ProcessTraceMode = EVENT_TRACE_REAL_TIME_MODE | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
            };
            _traceHandle = OpenTraceW(ref logfile);
            if (_traceHandle == ulong.MaxValue || _traceHandle == 0)
            {
                StartError = Marshal.GetLastWin32Error();
                StartDiagnostic = $"OpenTrace={StartError}";
                _traceHandle = 0;
                StopSession();
                return false;
            }

            _running = true;
            _traceThread = new Thread(TraceThread)
            {
                IsBackground = true,
                Name = "GameLocker.Monitor.ETW"
            };
            _traceThread.Start();
            Available = true;
            return true;
        }
        catch (Exception ex)
        {
            StartDiagnostic = "异常：" + ex.Message;
            StopSession();
            Available = false;
            return false;
        }
    }

    /// <summary>结构布局自检：与原生 evntrace.h 在 x64 下的字节数一致才允许调用</summary>
    private static bool LayoutOk() =>
        Marshal.SizeOf<EVENT_TRACE_LOGFILE>() == 448
        && Marshal.SizeOf<TRACE_LOGFILE_HEADER>() == 280
        && Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() == 120
        && Marshal.SizeOf<EVENT_RECORD>() == 112;

    private void TraceThread()
    {
        try
        {
            ProcessTraceError = (int)ProcessTrace([_traceHandle], 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            ProcessTraceError = ex.HResult;
        }
    }

    /// <summary>ETW 回调（非 UI 线程）：只统计目标进程的 Present 事件</summary>
    private void OnEvent(IntPtr recordPtr)
    {
        if (!_running) return;
        try
        {
            var record = Marshal.PtrToStructure<EVENT_RECORD>(recordPtr);
            if (record.EventHeader.ProviderId != DxgiProviderId) return;
            var id = record.EventHeader.EventDescriptor.Id;
            if (id != PresentEventId && id != PresentOverlayEventId) return;
            RawEvents++;
            var pid = TargetPid;
            if (pid <= 0 || record.EventHeader.ProcessId != (uint)pid) return;
            RecordFrame(record.EventHeader.TimeStamp);
        }
        catch { }
    }

    /// <summary>
    /// 记录一帧。时间取事件自带的时间戳（100ns），不能用回调到达时刻——
    /// ProcessTrace 是批量投递的，同一批事件会在几乎同一微秒内连续回调，
    /// 用回调时刻算间隔会全部变成 0.001ms 而被过滤掉。
    /// </summary>
    private void RecordFrame(long eventTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _lastCallbackTick = now;
            if (eventTimestamp <= 0) return;
            if (_lastTimestamp > 0)
            {
                var ms = (eventTimestamp - _lastTimestamp) / 10000.0;
                // 过滤异常间隔（暂停/切换窗口导致的秒级空档）
                if (ms is > 0.2 and < 500)
                {
                    _intervals[_head] = ms;
                    _timestamps[_head] = eventTimestamp;
                    _head = (_head + 1) % FrameWindow;
                    if (_count < FrameWindow) _count++;
                }
            }
            _lastTimestamp = eventTimestamp;
        }
    }

    /// <summary>取当前统计：帧率、平均帧时间、最近帧时间序列（毫秒，旧→新）</summary>
    public (double Fps, double FrameMs, double[] History) GetStats()
    {
        lock (_gate)
        {
            if (_count == 0) return (0, 0, []);
            // 最近 1.5 秒没有新的 Present（进程切走/空闲）→ 视为无数据
            var freshTicks = (long)(1.5 * Stopwatch.Frequency);
            var fresh = Stopwatch.GetTimestamp() - _lastCallbackTick < freshTicks;

            // 帧率窗口：以最新一帧的时间戳为基准往前 1 秒
            var windowTicks = FpsWindowMs * 10000.0;
            var newest = _timestamps[(_head - 1 + FrameWindow) % FrameWindow];
            var framesInWindow = 0;
            var sum = 0.0;
            for (var i = 0; i < _count; i++)
            {
                var idx = (_head - _count + i + FrameWindow) % FrameWindow;
                if (newest - _timestamps[idx] <= windowTicks) framesInWindow++;
                if (i >= _count - 30) sum += _intervals[idx];
            }

            // 只把最近 HistoryMax 帧交给悬浮条画波动线
            var outCount = Math.Min(_count, HistoryMax);
            var history = new double[outCount];
            for (var i = 0; i < outCount; i++)
            {
                var idx = (_head - outCount + i + FrameWindow) % FrameWindow;
                history[i] = _intervals[idx];
            }

            var avg = sum / Math.Min(30, _count);
            var fps = fresh && framesInWindow > 1 ? framesInWindow * (1000.0 / FpsWindowMs) : 0;
            return (fps, avg, history);
        }
    }

    /// <summary>停止会话并释放（可在任意线程调用）</summary>
    public void Dispose()
    {
        _running = false;
        Available = false;
        if (_traceHandle != 0)
        {
            try { CloseTrace(_traceHandle); } catch { }
            _traceHandle = 0;
        }
        StopSession();
        if (_traceThread is { IsAlive: true })
        {
            try { _traceThread.Join(800); } catch { }
        }
        _traceThread = null;
        _callback = null;
    }

    private void StopSession()
    {
        if (_sessionHandle == 0) return;
        var props = AllocProperties(out _);
        if (props != IntPtr.Zero)
        {
            try { ControlTraceW(_sessionHandle, null, props, EVENT_TRACE_CONTROL_STOP); } catch { }
            Marshal.FreeHGlobal(props);
        }
        _sessionHandle = 0;
    }

    // ---- 属性块（EVENT_TRACE_PROPERTIES + 会话名）----

    private static IntPtr AllocProperties(out int size)
    {
        var nameBytes = (SessionName.Length + 1) * 2;
        size = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + nameBytes;
        var ptr = Marshal.AllocHGlobal(size);
        // 清零，避免脏值
        for (var i = 0; i < size; i++) Marshal.WriteByte(ptr, i, 0);

        var props = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER
            {
                BufferSize = (uint)size,
                Flags = WNODE_FLAG_TRACED_GUID,
                ClientContext = 1 // QPC
            },
            LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
            LogFileNameOffset = 0,
            LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()
        };
        Marshal.StructureToPtr(props, ptr, false);
        // StartTrace 要求属性块在 LoggerNameOffset 处已放好会话名
        Marshal.Copy(SessionName.ToCharArray(), 0, ptr + (int)props.LoggerNameOffset, SessionName.Length);
        return ptr;
    }

    // ---- P/Invoke ----

    private delegate void EventRecordCallback(IntPtr record);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public ushort[] StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public ushort[] DaylightName;
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public long TimeStamp;
        public Guid Guid;
        public long ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_LOGFILE
    {
        // 原生顺序：LogFileName 在前、LoggerName 在后（顺序颠倒会把会话名当文件名，OpenTrace 返回 123）
        [MarshalAs(UnmanagedType.LPWStr)] public string? LogFileName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public long ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    private const int ERROR_ALREADY_EXISTS = 183;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILE logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint CloseTrace(ulong traceHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode,
        byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);
}
