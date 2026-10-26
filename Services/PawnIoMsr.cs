using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 通过 PawnIO 内核驱动读 Intel MSR，拿到真实的 CPU 封装温度。
/// 掌机固件常只向 ACPI 暴露固定的环境温度（例如恒 27.85℃），真实封装温度只能读 MSR：
///   IA32_TEMPERATURE_TARGET(0x1A2) bits16-23 = TjMax；
///   IA32_PACKAGE_THERM_STATUS(0x1B1) bits16-22 = 距 TjMax 的度数，bit31 为有效位；
///   IA32_THERM_STATUS(0x19C) 每核同布局（读之前要切到对应核心）。
/// PawnIO 设备默认只允许管理员打开，所以未提权的主进程要通过 --monitor-helper 提权助手代读，
/// 温度经共享内存回传。驱动未安装时助手会用随安装包附带的 PawnIO_setup.exe 静默安装一次。
/// </summary>
internal static class PawnIoMsr
{
    private const uint DeviceType = 41394u << 16;
    private const int FnNameLength = 32;
    private const uint IoctlExecute = 0x841 << 2;
    private const uint IoctlLoad = 0x821 << 2;
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    private const uint MsrTemperatureTarget = 0x1A2;
    private const uint MsrPackageThermStatus = 0x1B1;
    private const uint MsrThermStatus = 0x19C;

    private static readonly object Gate = new();
    private static bool _tried;
    private static IntPtr _device = new(-1);

    /// <summary>初始化结果（写入诊断文件）</summary>
    public static string Status { get; private set; } = "未尝试";

    /// <summary>驱动与模块是否可用</summary>
    public static bool Available { get; private set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, byte[] inBuffer, uint inSize,
        byte[]? outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

    /// <summary>
    /// 打开 PawnIO 设备并加载 IntelMSR 模块（只尝试一次）。
    /// <paramref name="installIfMissing"/> 只在提权助手里传 true：驱动缺失时静默安装。
    /// </summary>
    public static bool EnsureReady(bool installIfMissing = false)
    {
        lock (Gate)
        {
            if (_tried) return Available;
            _tried = true;
            try
            {
                _device = OpenDevice();
                if (_device == new IntPtr(-1) && installIfMissing)
                {
                    Status = "驱动未安装，正在静默安装…";
                    var installResult = InstallDriver();
                    if (installResult.Length > 0)
                    {
                        Status = installResult;
                        return false;
                    }
                    // 安装器返回成功后设备节点可能还要一小会儿才就绪，短暂重试几次
                    for (var attempt = 0; attempt < 5 && _device == new IntPtr(-1); attempt++)
                    {
                        Thread.Sleep(400);
                        _device = OpenDevice();
                    }
                }
                if (_device == new IntPtr(-1))
                {
                    Status = "PawnIO 设备打不开（需要管理员权限或驱动未安装）";
                    return false;
                }

                var module = ReadModule();
                if (module is null)
                {
                    Status = "安装包缺少 IntelMSR 模块";
                    return false;
                }
                if (!DeviceIoControl(_device, DeviceType | IoctlLoad, module, (uint)module.Length,
                        null, 0, out _, IntPtr.Zero))
                {
                    Status = "IntelMSR 模块加载失败 err=" + Marshal.GetLastWin32Error();
                    return false;
                }

                Available = true;
                Status = "就绪";
                return true;
            }
            catch (Exception ex)
            {
                Status = "PawnIO 异常：" + ex.Message;
                return false;
            }
        }
    }

    private static IntPtr OpenDevice() =>
        CreateFileW(DevicePath, 0xC0000000u, 3u, IntPtr.Zero, 3u, 0x80u, IntPtr.Zero);

    /// <summary>用随包附带的安装器静默安装驱动；成功返回空串，失败返回原因</summary>
    private static string InstallDriver()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "PawnIO_setup.exe");
        if (!File.Exists(exe)) return "未找到 PawnIO_setup.exe，无法安装驱动";
        try
        {
            var psi = new ProcessStartInfo(exe, "-install -silent")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return "PawnIO 安装器启动失败";
            p.WaitForExit(90_000);
            // 0=成功；3010=需要重启（此时驱动一般已可用）
            return p.HasExited && p.ExitCode is 0 or 3010
                ? ""
                : "PawnIO 安装失败 exit=" + (p.HasExited ? p.ExitCode : -1);
        }
        catch (Exception ex)
        {
            return "PawnIO 安装异常：" + ex.Message;
        }
    }

    /// <summary>读取嵌入的 IntelMSR 模块（签名校验由驱动完成）</summary>
    private static byte[]? ReadModule()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("IntelMSR.bin", StringComparison.OrdinalIgnoreCase));
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

    /// <summary>读一个 MSR（EAX 在低 32 位、EDX 在高 32 位）</summary>
    private static bool ReadMsr(uint index, out ulong value)
    {
        value = 0;
        if (!Available || _device == new IntPtr(-1)) return false;
        byte[] input = new byte[FnNameLength + 8];
        var fnName = Encoding.ASCII.GetBytes("ioctl_read_msr");
        Buffer.BlockCopy(fnName, 0, input, 0, fnName.Length);
        Buffer.BlockCopy(BitConverter.GetBytes((ulong)index), 0, input, FnNameLength, 8);
        var output = new byte[8];
        lock (Gate)
        {
            if (!DeviceIoControl(_device, DeviceType | IoctlExecute, input, (uint)input.Length,
                    output, (uint)output.Length, out var returned, IntPtr.Zero) || returned < 8)
                return false;
        }
        value = BitConverter.ToUInt64(output, 0);
        return true;
    }

    /// <summary>读取 CPU 封装温度（℃）；不可用返回 NaN。调用前需 EnsureReady 完成（初始化在后台跑，避免阻塞采样）</summary>
    public static double ReadTemperature()
    {
        if (!Available) return double.NaN;

        // TjMax（异常值按 100℃ 处理）
        var tjMax = 100u;
        if (ReadMsr(MsrTemperatureTarget, out var target))
        {
            var value = (uint)(target >> 16) & 0xFF;
            if (value is > 0 and < 130) tjMax = value;
        }

        // 封装温度（首选）
        if (ReadMsr(MsrPackageThermStatus, out var package) && (package & 0x8000_0000) != 0)
            return tjMax - ((package >> 16) & 0x7F);

        // 回退：逐核读 0x19C 取最高（需要把当前线程临时绑到对应核心）
        var best = double.NaN;
        var cores = Math.Min(Environment.ProcessorCount, 64);
        for (var core = 0; core < cores; core++)
        {
            var previous = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(1UL << core));
            if (previous == UIntPtr.Zero) continue;
            try
            {
                if (!ReadMsr(MsrThermStatus, out var status) || (status & 0x8000_0000) == 0) continue;
                var value = tjMax - ((status >> 16) & 0x7F);
                if (double.IsNaN(best) || value > best) best = value;
            }
            finally
            {
                SetThreadAffinityMask(GetCurrentThread(), previous);
            }
        }
        return best;
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
