using System.IO.MemoryMappedFiles;

namespace GameLocker.Services;

/// <summary>
/// 主进程 ↔ 提权监控助手的共享内存通道。
/// 帧率依赖 ETW 实时会话（必须管理员权限），主进程未提权时由助手采集，
/// 通过这里把 FPS / 帧时间 / 帧波动线写给主进程绘制悬浮条。
/// 布局：写入前把 Seq 置奇数、写完置偶数，读侧序号一致才采用（无锁双缓冲）。
/// </summary>
internal sealed class MonitorShared : IDisposable
{
    public const string MapName = @"Local\GameLockerMonitorShared";
    public const string StopEventName = @"Local\GameLockerMonitorStop";
    public const int HistoryCapacity = 180;

    private const int Size = 4096;
    private const int OffSeq = 0;       // int  写入序列（奇数=正在写）
    private const int OffTargetPid = 4; // int  主进程写入要监控的前台进程
    private const int OffTick = 8;      // long 助手心跳（Environment.TickCount64）
    private const int OffFps = 16;      // double
    private const int OffFrameMs = 24;  // double
    private const int OffCount = 32;    // int  帧时间样本数
    private const int OffHistory = 40;  // double[180]，40..1479
    private const int OffMagic = 1484;  // int
    private const int OffCpuTemp = 1492; // double 助手读到的 CPU 封装温度（PawnIO/MSR，NaN=不可用）
    private const int OffGpuTemp = 1500; // double 助手读到的 GPU 温度（厂商 WMI，NaN=不可用）
    private const int Magic = 0x4D4F4E31; // "MON1"

    private const long FreshMs = 3000;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;

    private MonitorShared(MemoryMappedFile mmf, MemoryMappedViewAccessor view)
    {
        _mmf = mmf;
        _view = view;
    }

    /// <summary>主进程：创建（或复用）共享内存并写入协议标记</summary>
    public static MonitorShared? CreateWriter()
    {
        try
        {
            var mmf = MemoryMappedFile.CreateOrOpen(MapName, Size);
            var view = mmf.CreateViewAccessor();
            var shared = new MonitorShared(mmf, view);
            view.Write(OffMagic, Magic);
            view.Write(OffSeq, 0);
            return shared;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>助手进程：打开主进程创建好的共享内存</summary>
    public static MonitorShared? Open()
    {
        try
        {
            var mmf = MemoryMappedFile.OpenExisting(MapName);
            var view = mmf.CreateViewAccessor();
            if (view.ReadInt32(OffMagic) != Magic)
            {
                view.Dispose();
                mmf.Dispose();
                return null;
            }
            return new MonitorShared(mmf, view);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>主进程：每秒告诉助手当前要统计哪个进程（0=无目标）</summary>
    public void SetTargetPid(int pid)
    {
        try { _view.Write(OffTargetPid, pid); } catch { }
    }

    /// <summary>助手：读取目标进程号</summary>
    public int ReadTargetPid()
    {
        try { return _view.ReadInt32(OffTargetPid); } catch { return 0; }
    }

    /// <summary>助手：发布一帧统计数据（含心跳时间，主进程据此判断助手是否存活）</summary>
    public void Publish(double fps, double frameMs, double[] history, double cpuTemp, double gpuTemp)
    {
        try
        {
            var seq = _view.ReadInt32(OffSeq);
            _view.Write(OffSeq, seq + 1); // 奇数：写入中
            _view.Write(OffFps, fps);
            _view.Write(OffFrameMs, frameMs);
            _view.Write(OffCpuTemp, cpuTemp);
            _view.Write(OffGpuTemp, gpuTemp);
            var count = Math.Min(history.Length, HistoryCapacity);
            _view.Write(OffCount, count);
            for (var i = 0; i < count; i++)
                _view.Write(OffHistory + i * 8L, history[i]);
            _view.Write(OffTick, Environment.TickCount64);
            _view.Write(OffSeq, seq + 2); // 偶数：写完
        }
        catch { }
    }

    /// <summary>主进程：读一帧统计数据；助手未运行/无数据/正在写入时返回 false</summary>
    public bool TryRead(out double fps, out double frameMs, out double[] history, out double cpuTemp, out double gpuTemp)
    {
        fps = 0;
        frameMs = 0;
        cpuTemp = double.NaN;
        gpuTemp = double.NaN;
        history = [];
        try
        {
            var seq1 = _view.ReadInt32(OffSeq);
            if (seq1 == 0 || (seq1 & 1) != 0) return false;
            if (Environment.TickCount64 - _view.ReadInt64(OffTick) > FreshMs) return false;
            var count = Math.Clamp(_view.ReadInt32(OffCount), 0, HistoryCapacity);
            var values = new double[count];
            for (var i = 0; i < count; i++)
                values[i] = _view.ReadDouble(OffHistory + i * 8L);
            var f = _view.ReadDouble(OffFps);
            var ms = _view.ReadDouble(OffFrameMs);
            var temp = _view.ReadDouble(OffCpuTemp);
            var gtemp = _view.ReadDouble(OffGpuTemp);
            if (_view.ReadInt32(OffSeq) != seq1) return false; // 读取期间被写入，丢弃
            fps = f;
            frameMs = ms;
            cpuTemp = temp;
            gpuTemp = gtemp;
            history = values;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _view.Dispose(); } catch { }
        try { _mmf.Dispose(); } catch { }
    }
}
