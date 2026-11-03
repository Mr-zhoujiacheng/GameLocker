using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 用 ETW 实时会话监听 Microsoft-Windows-DXGI 的 Present 事件，估算目标进程的提交帧率、帧时间与帧波动。
/// 按事件时间戳差统计，不包含 PresentMon 的完整呈现链路分析，也不代表实际显示帧率。
/// 需要管理员权限（StartTrace 失败时静默降级，调用方显示"—"）。
/// </summary>
internal sealed class PresentEtwListener : IDisposable
{
    /// <summary>DXGI 提供程序 GUID（CA11C036-0102-4A2D-A6AD-F03CFED5D3C9）</summary>
    private static readonly Guid DxgiProviderId = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    // Intel-PresentMon 官方标记需要 SDK/驱动 instrumentation；并非普遍支持 DLSS/FSR。
    // 仅使用目标进程的 PresentFrameType，不处理无法归属目标的驱动 PID 或 FlipFrameType。
    private static readonly Guid PresentMonProviderId = new("ECAA4712-4644-442F-B94C-A32F6CF8A499");
    private const ushort PresentFrameTypeEventId = 1;

    /// <summary>Present 事件 ID。只统计 42：43（PresentMultiplaneOverlay）与 42 成对出现，
    /// 两条都计数会让帧率翻倍；它也不是普通游戏提交流程的帧计数依据</summary>
    private const ushort PresentEventId = 42;

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

    // ---- 帧率排障日志：%APPDATA%\GameLocker\fps-diag.txt，按秒记录目标进程的
    //      Present(42)/MultiplaneOverlay(43) 事件数与相邻间隔，用于定位帧率翻倍来源 ----
    private static readonly string DiagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "fps-diag.txt");
    private readonly object _diagGate = new();
    private readonly List<long> _diagStamps = new();
    private readonly List<string> _diagPayload = new();
    private long _diagSecond;
    private int _diagPid, _diag42, _diag43, _diagOther, _diagPayloadCount, _diagLines;

    private void DiagInit()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagPath)!);
            File.WriteAllText(DiagPath, $"[GameLocker 帧率诊断] {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        }
        catch { }
    }

    private void DiagCount(in EVENT_RECORD record, ushort id)
    {
        lock (_diagGate)
        {
            var stamp = record.EventHeader.TimeStamp;
            var sec = stamp / 10_000_000;
            if (_diagSecond != 0 && sec != _diagSecond) DiagFlushLocked();
            _diagSecond = sec;
            _diagPid = (int)record.EventHeader.ProcessId;
            if (id == PresentEventId)
            {
                _diag42++;
                if (_diagStamps.Count < 48) _diagStamps.Add(stamp);
            }
            else if (id == 43) _diag43++;
            else _diagOther++;
            // 记录前几条事件的载荷头（第一个字段通常是交换链指针），看是否两条事件对应同一交换链
            if (_diagPayloadCount < 4 && record.UserData != IntPtr.Zero && record.UserDataLength >= 8)
            {
                var b = new byte[8];
                Marshal.Copy(record.UserData, b, 0, 8);
                _diagPayload.Add($"id{id}:{BitConverter.ToString(b)}");
                _diagPayloadCount++;
            }
        }
    }

    private void DiagFlushLocked()
    {
        // 最多保留约 5 分钟样本，避免长期运行把日志写大
        if (_diagLines >= 300) return;
        try
        {
            var deltas = new List<string>();
            for (var i = 1; i < _diagStamps.Count && i <= 12; i++)
                deltas.Add(((_diagStamps[i] - _diagStamps[i - 1]) / 10000.0).ToString("0.00"));
            var line = $"{DateTime.Now:HH:mm:ss} pid={_diagPid} 42={_diag42} 43={_diag43} 其他={_diagOther}"
                + $" 间隔(ms)={string.Join(",", deltas)}";
            if (_diagPayload.Count > 0) line += " 载荷=" + string.Join(" ", _diagPayload);
            File.AppendAllText(DiagPath, line + "\n");
            _diagLines++;
            _diag42 = _diag43 = _diagOther = 0;
            _diagStamps.Clear();
            _diagPayload.Clear();
            _diagPayloadCount = 0;
        }
        catch { }
    }

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
    private int _targetPid;
    private long _targetVersion;
    private readonly HashSet<uint> _originalFrameIds = new();
    private readonly Queue<uint> _originalFrameOrder = new();
    private readonly Queue<long> _originalTimestamps = new();
    private long _lastOriginalTimestamp;
    private long _lastOriginalCallbackTick;
    private long _lastGeneratedCallbackTick;

    public bool FrameGenerationDetected
    {
        get
        {
            lock (_gate)
                return _lastGeneratedCallbackTick != 0
                    && Stopwatch.GetTimestamp() - _lastGeneratedCallbackTick < 3 * Stopwatch.Frequency;
        }
    }

    /// <summary>被统计的目标进程（前台进程；0=无目标）</summary>
    public int TargetPid
    {
        get { lock (_gate) return _targetPid; }
        set
        {
            lock (_gate)
            {
                if (_targetPid == value) return;
                _targetPid = value;
                _targetVersion++;
                ResetStatsLocked();
            }
        }
    }

    private void ResetStatsLocked()
    {
        ResetDxgiStatsLocked();
        ResetOriginalStatsLocked();
        _lastGeneratedCallbackTick = 0;
    }

    private void ResetOriginalStatsLocked()
    {
        _originalFrameIds.Clear();
        _originalFrameOrder.Clear();
        _originalTimestamps.Clear();
        _lastOriginalTimestamp = _lastOriginalCallbackTick = 0;
    }

    private void ResetDxgiStatsLocked()
    {
        Array.Clear(_intervals, 0, _intervals.Length);
        Array.Clear(_timestamps, 0, _timestamps.Length);
        _count = _head = 0;
        _lastTimestamp = _lastCallbackTick = 0;
    }

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

            // 可选标记源：启用失败不影响 DXGI 提交帧率监听。
            var presentMonProvider = PresentMonProviderId;
            EnableTraceEx2(_sessionHandle, ref presentMonProvider, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                TRACE_LEVEL_INFORMATION, 1, 0, 0, IntPtr.Zero);

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
            DiagInit();
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
            var isDxgi = record.EventHeader.ProviderId == DxgiProviderId;
            var isPresentMon = record.EventHeader.ProviderId == PresentMonProviderId;
            if (!isDxgi && !isPresentMon) return;
            var id = record.EventHeader.EventDescriptor.Id;
            if (isDxgi) RawEvents++;
            int pid;
            long targetVersion;
            lock (_gate)
            {
                pid = _targetPid;
                targetVersion = _targetVersion;
            }
            if (pid <= 0 || record.EventHeader.ProcessId != (uint)pid) return;
            if (isPresentMon)
            {
                var version = record.EventHeader.EventDescriptor.Version;
                // 官方 manifest：v0 为 UInt32 FrameId + byte FrameType，v1 尾部另有 UInt32。
                if (id != PresentFrameTypeEventId || record.UserData == IntPtr.Zero
                    || !((version == 0 && record.UserDataLength == 5)
                        || (version == 1 && record.UserDataLength == 9))) return;
                var frameType = Marshal.ReadByte(record.UserData, 4);
                // 官方枚举：XeFG=50、AFMF=100、DLSS=150；Repeated(2) 不是生成帧。
                if (frameType is 50 or 100 or 150)
                {
                    lock (_gate)
                    {
                        if (_targetPid == pid && _targetVersion == targetVersion)
                            _lastGeneratedCallbackTick = Stopwatch.GetTimestamp();
                    }
                    return;
                }
                if (frameType != 1) return;
                var frameId = unchecked((uint)Marshal.ReadInt32(record.UserData, 0));
                RecordOriginalFrame(pid, targetVersion, frameId, record.EventHeader.TimeStamp);
                return;
            }
            DiagCount(record, id);
            if (id != PresentEventId) return;
            RecordFrame(pid, targetVersion, record.EventHeader.TimeStamp);
        }
        catch { }
    }

    /// <summary>
    /// 记录一帧。时间取事件自带的时间戳（100ns），不能用回调到达时刻——
    /// ProcessTrace 是批量投递的，同一批事件会在几乎同一微秒内连续回调，
    /// 用回调时刻算间隔会全部变成 0.001ms 而被过滤掉。
    /// </summary>
    private void RecordFrame(int pid, long targetVersion, long eventTimestamp)
    {
        lock (_gate)
        {
            // 目标可能在回调筛选后切换，版本同时防止 A→B→A 的旧回调混入。
            if (_targetPid != pid || _targetVersion != targetVersion || eventTimestamp <= 0) return;
            if (_lastTimestamp > 0)
            {
                var delta = eventTimestamp - _lastTimestamp;
                if (delta == 0) return;
                var ms = delta / 10000.0;
                // 回退或长空档后重新采样，避免旧曲线与新时间轴混用。
                if (delta < 0 || ms >= 500)
                {
                    ResetDxgiStatsLocked();
                }
                else
                {
                    // 仅保留原有的极短间隔下限；不按历史均值推断重复，避免吞掉真实加速。
                    if (ms <= 0.2) return;
                    _intervals[_head] = ms;
                    _timestamps[_head] = eventTimestamp;
                    _head = (_head + 1) % FrameWindow;
                    if (_count < FrameWindow) _count++;
                }
            }
            _lastTimestamp = eventTimestamp;
            _lastCallbackTick = Stopwatch.GetTimestamp();
        }
    }

    private void RecordOriginalFrame(int pid, long targetVersion, uint frameId, long eventTimestamp)
    {
        lock (_gate)
        {
            if (_targetPid != pid || _targetVersion != targetVersion || eventTimestamp <= 0) return;
            if (_originalFrameIds.Contains(frameId)) return;
            if (_lastOriginalTimestamp > 0
                && (eventTimestamp < _lastOriginalTimestamp
                    || eventTimestamp - _lastOriginalTimestamp >= 5_000_000))
                ResetOriginalStatsLocked();

            _originalFrameIds.Add(frameId);
            _originalFrameOrder.Enqueue(frameId);
            if (_originalFrameOrder.Count > FrameWindow)
                _originalFrameIds.Remove(_originalFrameOrder.Dequeue());

            _originalTimestamps.Enqueue(eventTimestamp);
            while (_originalTimestamps.Count > FrameWindow
                || eventTimestamp - _originalTimestamps.Peek() > 10_000_000)
                _originalTimestamps.Dequeue();
            _lastOriginalTimestamp = eventTimestamp;
            _lastOriginalCallbackTick = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>原始帧标记的最近一秒帧率；无标记、样本不足或到达过期时返回 NaN。</summary>
    public double GetOriginalFps()
    {
        lock (_gate)
        {
            if (_originalTimestamps.Count < 2
                || Stopwatch.GetTimestamp() - _lastOriginalCallbackTick >= 1.5 * Stopwatch.Frequency)
                return double.NaN;
            var duration = _lastOriginalTimestamp - _originalTimestamps.Peek();
            return duration > 0
                ? (_originalTimestamps.Count - 1) * 10_000_000.0 / duration
                : double.NaN;
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
            if (Stopwatch.GetTimestamp() - _lastCallbackTick >= freshTicks) return (0, 0, []);

            // 帧率窗口：以最新一帧的时间戳为基准往前 1 秒
            var windowTicks = FpsWindowMs * 10000.0;
            var newest = _timestamps[(_head - 1 + FrameWindow) % FrameWindow];
            var intervalsInWindow = 0;
            var coveredMs = 0.0;
            var sum = 0.0;
            for (var i = 0; i < _count; i++)
            {
                var idx = (_head - _count + i + FrameWindow) % FrameWindow;
                if (newest - _timestamps[idx] < windowTicks)
                {
                    intervalsInWindow++;
                    coveredMs += _intervals[idx];
                }
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
            // 每个样本代表一个完整间隔，而非一个端点；以实际覆盖时长归一化。
            // 启动不足一秒或环形缓冲不足一秒时也适用，边界不额外计入一帧。
            var fps = coveredMs > 0 ? intervalsInWindow * 1000.0 / coveredMs : 0;
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
