using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 通过 PawnIO 的 LpcACPIEC 模块访问 ACPI EC（端口 0x62/0x66），用于探查厂商 EC 里可能存在的 GPU 温度。
/// Intel 核显驱动不提供 GPU 温度/功耗（IGCL 枚举 0 个传感器、0 个功率域），部分整机厂商会把
/// 传感器结果放在 EC RAM 里；本类只做「读取 256 字节并写日志」，用于在掌机上比对空载与游戏时的寄存器差异。
/// 访问协议遵循 ACPI EC 规范：查询状态位 IBF/OBF，尝试持有 Windows 的 EC 全局互斥体 Global\Access_EC，
/// 每一步都有超时，绝不无限等待（防止把 EC 拖死导致风扇失控）；失败时把状态位写进日志便于定位。
/// </summary>
internal static class PawnIoEc
{
    private const uint DeviceType = 41394u << 16;
    private const int FnNameLength = 32;
    private const uint IoctlExecute = 0x841 << 2;
    private const uint IoctlLoad = 0x821 << 2;
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const string MutexName = @"Global\Access_EC";

    private const int PortStatus = 0x66;
    private const int PortData = 0x62;
    private const byte StatusObf = 0x01;
    private const byte StatusIbf = 0x02;
    private const byte CommandRead = 0x80;

    private const uint MutexModifyState = 0x00100001;

    /// <summary>单步等待上限：首次探测给足时间（个别 EC 慢），确认可用后缩短，避免整轮 dump 太慢</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMilliseconds(60);

    private static readonly object Gate = new();
    private static bool _tried;
    private static IntPtr _device = new(-1);
    private static TimeSpan _timeout = ProbeTimeout;

    /// <summary>初始化结果（写入诊断文件）</summary>
    public static string Status { get; private set; } = "未尝试";

    /// <summary>最近一次读取失败的细节（状态位、失败地址），写入日志帮助定位</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>最近一轮快照里读失败、被填 0 的地址（为空表示全部读到）</summary>
    public static string LastBad { get; private set; } = "";

    /// <summary>模块是否可用</summary>
    public static bool Available { get; private set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, byte[] inBuffer, uint inSize,
        byte[]? outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool ReleaseMutex(IntPtr handle);

    /// <summary>打开设备并加载 LpcACPIEC 模块（驱动由 <see cref="PawnIoMsr"/> 负责安装，这里只负责连接）</summary>
    public static bool EnsureReady()
    {
        lock (Gate)
        {
            if (_tried) return Available;
            _tried = true;
            try
            {
                // 两个模块需要各自独立的设备句柄（每个句柄只挂一个已加载模块）
                _device = CreateFileW(DevicePath, 0xC0000000u, 3u, IntPtr.Zero, 3u, 0x80u, IntPtr.Zero);
                if (_device == new IntPtr(-1))
                {
                    Status = "PawnIO 设备打不开（驱动未安装或未提权）";
                    return false;
                }

                var module = ReadModule("LpcACPIEC.bin");
                if (module is null)
                {
                    Status = "安装包缺少 LpcACPIEC 模块";
                    return false;
                }
                if (!DeviceIoControl(_device, DeviceType | IoctlLoad, module, (uint)module.Length,
                        null, 0, out _, IntPtr.Zero))
                {
                    Status = "LpcACPIEC 模块加载失败 err=" + Marshal.GetLastWin32Error();
                    return false;
                }

                // 先用一条寄存器读判断这台机器的 EC 是否允许直接访问，结果写进诊断
                // EC 偶发忙（风扇/充电管理占用），失败重试 3 次再判定不支持
                _timeout = ProbeTimeout;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (ReadEc(0x20, out _))
                    {
                        _timeout = StepTimeout;
                        Available = true;
                        LastError = "";
                        Status = "就绪";
                        return true;
                    }
                    Thread.Sleep(100);
                }
                Status = "EC 端口无响应（" + LastError + "）";
                return false;
            }
            catch (Exception ex)
            {
                Status = "EC 异常：" + ex.Message;
                return false;
            }
        }
    }

    private static byte[]? ReadModule(string suffix)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>执行模块函数：函数名（32 字节）+ ulong 参数；<paramref name="minReturned"/> 为要求的最小返回字节数</summary>
    private static bool Execute(string function, ulong[] args, byte[]? output, uint minReturned)
    {
        if (_device == new IntPtr(-1)) return false;
        var input = new byte[FnNameLength + args.Length * 8];
        var name = Encoding.ASCII.GetBytes(function);
        Buffer.BlockCopy(name, 0, input, 0, Math.Min(FnNameLength - 1, name.Length));
        for (var i = 0; i < args.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(args[i]), 0, input, FnNameLength + i * 8, 8);
        return DeviceIoControl(_device, DeviceType | IoctlExecute, input, (uint)input.Length,
            output, output is null ? 0u : (uint)output.Length, out var returned, IntPtr.Zero)
            && returned >= minReturned;
    }

    /// <summary>读一个端口（LpcACPIEC 只允许 0x62/0x66）</summary>
    private static bool ReadPort(int port, out byte value)
    {
        value = 0;
        var output = new byte[8];
        if (!Execute("ioctl_pio_read", [(ulong)port], output, 8)) return false;
        value = output[0];
        return true;
    }

    /// <summary>写一个端口（该函数没有输出数据）</summary>
    private static bool WritePort(int port, byte value) =>
        Execute("ioctl_pio_write", [(ulong)port, value], null, 0);

    /// <summary>等待 IBF 清零 / OBF 置位；超时返回 false，最后一个状态位记进 <see cref="LastError"/></summary>
    private static bool WaitStatus(bool waitForObf)
    {
        var watch = Stopwatch.StartNew();
        byte last = 0;
        var saw = false;
        while (watch.Elapsed < _timeout)
        {
            if (ReadPort(PortStatus, out var status))
            {
                last = status;
                saw = true;
                var ok = waitForObf ? (status & StatusObf) != 0 : (status & StatusIbf) == 0;
                if (ok) return true;
            }
            Thread.Sleep(0);
        }
        LastError = saw ? $"等待 {(waitForObf ? "OBF" : "IBF 清零")} 超时，status=0x{last:X2}" : "状态口读不到";
        return false;
    }

    /// <summary>读状态口（诊断用，同时清掉残留的 OBF：读一次数据口）</summary>
    private static byte ReadStatusAndDrain()
    {
        if (!ReadPort(PortStatus, out var status)) return 0xFF;
        if ((status & StatusObf) != 0) ReadPort(PortData, out _);
        return status;
    }

    /// <summary>按 ACPI EC 协议读一个寄存器：命令(0x80)写 0x66，地址写 0x62，最后从 0x62 取数据</summary>
    private static bool ReadEc(byte address, out byte value)
    {
        value = 0;
        var before = ReadStatusAndDrain();
        if (!WaitStatus(false))
        {
            LastError = $"地址 0x{address:X2}：初始 IBF 未清零，status=0x{before:X2}";
            return false;
        }
        if (!WritePort(PortStatus, CommandRead)) { LastError = "写命令 0x80 失败"; return false; }
        if (!WaitStatus(false))
        {
            LastError = $"地址 0x{address:X2}：命令后 IBF 未清零（EC 未接受命令）";
            return false;
        }
        if (!WritePort(PortData, address)) { LastError = "写地址失败"; return false; }
        if (!WaitStatus(true))
        {
            ReadPort(PortStatus, out var after);
            LastError = $"地址 0x{address:X2}：无数据返回，status 0x{before:X2}→0x{after:X2}";
            return false;
        }
        return ReadPort(PortData, out value);
    }

    /// <summary>一次性读出 EC RAM 0x00-0xFF；失败返回 null（细节见 <see cref="LastError"/>）</summary>
    public static byte[]? DumpRam()
    {
        if (!Available) return null;
        lock (Gate)
        {
            var mutex = OpenMutexW(MutexModifyState, false, MutexName);
            var held = mutex != IntPtr.Zero && WaitForSingleObject(mutex, 200) == 0;
            try
            {
                var data = new byte[256];
                var bad = new List<byte>();
                for (var i = 0; i < data.Length; i++)
                {
                    // 个别地址偶发超时（造型 / 充电管理并发访问 EC）：最多试 3 次，
                    // 仍失败就把该字节记为 0 继续读，避免一个坏点毁掉整行快照
                    var ok = false;
                    for (var attempt = 0; attempt < 3 && !ok; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(5);
                        if (ReadEc((byte)i, out var v))
                        {
                            data[i] = v;
                            ok = true;
                        }
                    }
                    if (!ok) bad.Add((byte)i);
                }
                LastBad = bad.Count == 0
                    ? ""
                    : string.Join(",", bad.Take(16).Select(b => $"0x{b:X2}"))
                        + (bad.Count > 16 ? $" 等 {bad.Count} 个" : "");
                return data;
            }
            catch (Exception ex)
            {
                LastError = "EC 读取异常：" + ex.Message;
                return null;
            }
            finally
            {
                if (held) ReleaseMutex(mutex);
                if (mutex != IntPtr.Zero) CloseHandle(mutex);
            }
        }
    }

    /// <summary>释放设备句柄（助手退出时调用）</summary>
    public static void Close()
    {
        lock (Gate)
        {
            if (_device != new IntPtr(-1))
            {
                try { CloseHandle(_device); } catch { }
                _device = new IntPtr(-1);
            }
            Available = false;
            _tried = false;
        }
    }
}
