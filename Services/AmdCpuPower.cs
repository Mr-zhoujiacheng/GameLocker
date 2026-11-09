using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

internal sealed class AmdCpuPower : IDisposable
{
    private const double Tolerance = 0.25;
    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const string RangeMessage = "保守范围：1 W 至首次读取的 OEM 限制向下取整；不是硬件安全上限，仅允许降低或恢复初始限制";
    private const string DependencyMessage = "官方 RyzenAdj x64 DLL 与 inpoutx64.dll、WinRing0x64.dll 需位于 RyzenAdj 目录，WinRing0x64.sys 需位于程序 EXE 目录；Windows 可能阻止这些驱动或依赖，请检查系统安全提示，不要关闭安全策略";
    private static readonly string[] Names = ["STAPM", "slow", "fast"];
    private readonly object _gate = new();
    private readonly bool[] _possiblyChanged = new bool[3];
    private IntPtr _library;
    private IntPtr _inpout;
    private IntPtr _context;
    private bool _attempted;
    private bool _tableInitialized;
    private bool _disposed;
    private string _error = "";
    private double[]? _original;
    private Init? _init;
    private Cleanup? _cleanup;
    private Table? _refresh;
    private GetLimit[] _get = [];
    private SetLimit[] _set = [];

    public CpuPowerService.State Read()
    {
        lock (_gate)
        {
            if (!EnsureReady()) return new(false, true, _error, []);
            if (!TryRead(out var values, out string error)) return new(false, true, error, []);
            _original ??= (double[])values.Clone();
            var limits = new CpuPowerService.Limit[3];
            for (int i = 0; i < limits.Length; i++)
            {
                double maximum = Math.Floor(_original[i]);
                limits[i] = new(Names[i], values[i], 1, maximum, maximum >= 1);
            }
            string message = "AMD RyzenAdj：" + RangeMessage;
            if (!Ordered(_original)) message += "；OEM 初始限制未满足 STAPM ≤ slow ≤ fast，设置时只校验目标与相关实际项，不自动调整其他限制";
            return new(true, true, message, limits);
        }
    }

    public CpuPowerService.Result Apply(int index, double watts)
    {
        lock (_gate)
        {
            if (index < 0 || index >= 3 || !double.IsFinite(watts)) return new(false, "功耗索引或瓦数无效");
            if (!EnsureReady()) return new(false, _error);
            if (!TryRead(out var before, out string error)) return new(false, error);
            _original ??= (double[])before.Clone();
            double maximum = Math.Floor(_original[index]);
            bool restoreOriginal = watts == _original[index];
            if (maximum < 1 || watts < 1 || (!restoreOriginal && watts > maximum))
                return new(false, RangeMessage + $"；{Names[index]} 初始值 {_original[index]:0.###} W，当前目标不允许");
            uint milliwatts = ToMilliwatts(watts);
            double target = milliwatts / 1000.0;
            if (target > _original[index]) return new(false, "目标超过首次读取的 OEM 初始限制");
            if (!TargetOrdered(index, target, before))
                return new(false, "拒绝设置：目标与当前实际限制不满足 STAPM ≤ slow ≤ fast；不会自动改写其他项" +
                    (Ordered(_original) ? "" : "（OEM 初始限制本身未有序）"));

            // A firmware command may affect more than its named limit, even when it reports failure.
            Array.Fill(_possiblyChanged, true);
            int status;
            try { status = _set[index](_context, milliwatts); }
            catch (Exception ex) { return RollBack(index, before, "写入异常：" + ex.Message); }
            if (!TryRead(out var after, out error))
                return RollBack(index, before, $"写入返回 {status}；" + error);
            bool otherLimitsUnchanged = Enumerable.Range(0, 3).All(i => i == index || Math.Abs(after[i] - before[i]) <= Tolerance);
            if (status != 0 || Math.Abs(after[index] - target) > Tolerance || !otherLimitsUnchanged
                || !TargetOrdered(index, after[index], after)
                || Enumerable.Range(0, 3).Any(i => after[i] > _original[i] + Tolerance))
                return RollBack(index, before, $"写入未通过回读校验（返回 {status}，目标 {target:0.###} W，实际 {after[index]:0.###} W）");
            return new(true, $"{Names[index]} 已设置并回读确认：{after[index]:0.###} W；" + RangeMessage);
        }
    }

    public CpuPowerService.Result Restore()
    {
        lock (_gate)
        {
            if (!_possiblyChanged.Any(x => x)) return new(true, "没有需要恢复的 AMD 功耗限制");
            if (!EnsureReady() || _original is null) return new(false, "无法恢复 AMD 功耗：" + _error);
            var failures = new List<string>();
            // Restore every possibly affected item, then check again for cross-limit side effects.
            for (int i = 0; i < 3; i++)
            {
                if (!_possiblyChanged[i]) continue;
                try
                {
                    if (!TryRead(out _, out string preflightError))
                    {
                        failures.Add(Names[i] + " 恢复前 PM 校验失败，未写入：" + preflightError);
                        continue;
                    }
                    int status = _set[i](_context, ToMilliwatts(_original[i]));
                    if (!TryRead(out var values, out string error)) failures.Add(Names[i] + "：" + error);
                    else if (status != 0 || Math.Abs(values[i] - _original[i]) > Tolerance)
                        failures.Add($"{Names[i]} 恢复失败（返回 {status}，实际 {values[i]:0.###} W）");
                }
                catch (Exception ex) { failures.Add(Names[i] + "：" + ex.Message); }
            }
            if (!TryRead(out var final, out string finalError)) failures.Add(finalError);
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    if (!_possiblyChanged[i]) continue;
                    if (Math.Abs(final[i] - _original[i]) <= Tolerance) _possiblyChanged[i] = false;
                    else failures.Add($"{Names[i]} 最终回读不等于初始值（{final[i]:0.###} / {_original[i]:0.###} W）");
                }
            }
            return failures.Count == 0 ? new(true, "AMD 初始功耗限制已逐项恢复并回读确认")
                : new(false, "AMD 恢复未全部成功：" + string.Join("；", failures));
        }
    }

    private CpuPowerService.Result RollBack(int index, double[] before, string reason)
    {
        try
        {
            if (!TryRead(out _, out string preflightError))
                return new(false, reason + "；回滚前 PM 校验失败，禁止继续写入，限制可能已改变：" + preflightError);
            int status = _set[index](_context, ToMilliwatts(Math.Min(before[index], _original![index])));
            if (!TryRead(out var values, out string error)) return new(false, reason + "；回滚无法确认：" + error);
            bool restored = status == 0 && Enumerable.Range(0, 3).All(i => Math.Abs(values[i] - before[i]) <= Tolerance);
            return new(false, reason + (restored ? "；已回滚并回读确认，设置失败" : "；回滚未完全确认，限制可能已改变，需恢复初始值"));
        }
        catch (Exception ex) { return new(false, reason + "；回滚失败，限制可能已改变：" + ex.Message); }
    }

    private bool EnsureReady()
    {
        if (_disposed) { _error = "AMD 功耗后端已释放"; return false; }
        if (_attempted) return _tableInitialized;
        _attempted = true;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        { _error = "unsupported：RyzenAdj 仅支持 Windows x64 助手"; return false; }
        try
        {
            string root = Path.GetFullPath(AppContext.BaseDirectory);
            string[] directories = [Path.Combine(root, "Resources", "RyzenAdj"), Path.Combine(root, "RyzenAdj")];
            string? path = directories.Select(d => Path.Combine(d, "libryzenadj.dll")).FirstOrDefault(File.Exists);
            if (path is null)
            {
                _error = "unsupported：缺少 libryzenadj.dll，请安装官方 RyzenAdj x64 到 Resources/RyzenAdj 或程序目录下 RyzenAdj。" + DependencyMessage;
                return false;
            }
            // RyzenAdj later loads inpout by bare name; retain an absolute-path preload.
            string inpout = Path.Combine(Path.GetDirectoryName(path)!, "inpoutx64.dll");
            _inpout = LoadLibraryEx(inpout, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            if (_inpout == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!File.Exists(Path.Combine(root, "WinRing0x64.sys")))
                throw new FileNotFoundException("程序 EXE 目录缺少 WinRing0x64.sys");
            _library = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            if (_library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _init = Export<Init>("init_ryzenadj");
            _cleanup = Export<Cleanup>("cleanup_ryzenadj");
            var initTable = Export<Table>("init_table");
            _refresh = Export<Table>("refresh_table");
            _get = [Export<GetLimit>("get_stapm_limit"), Export<GetLimit>("get_slow_limit"), Export<GetLimit>("get_fast_limit")];
            _set = [Export<SetLimit>("set_stapm_limit"), Export<SetLimit>("set_slow_limit"), Export<SetLimit>("set_fast_limit")];
            _context = _init();
            if (_context == IntPtr.Zero) { _error = "unsupported：RyzenAdj 初始化失败，CPU/SMU 不支持或驱动不可用。" + DependencyMessage; return false; }
            int status = initTable(_context);
            if (status != 0)
            {
                // Official cleanup may dereference a null table here. The helper's exit reclaims it.
                _error = $"unsupported：RyzenAdj PM init_table 失败（{status}），禁止写入。" + DependencyMessage;
                return false;
            }
            _tableInitialized = true;
            return true;
        }
        catch (Exception ex) { _error = "unsupported：无法加载/初始化官方 RyzenAdj x64：" + ex.Message + "。" + DependencyMessage; return false; }
    }

    private bool TryRead(out double[] values, out string error)
    {
        values = new double[3];
        error = "";
        try
        {
            int status = _refresh!(_context);
            if (status != 0) { error = $"unsupported：RyzenAdj PM refresh_table 失败（{status}），禁止写入"; return false; }
            for (int i = 0; i < 3; i++)
            {
                values[i] = _get[i](_context);
                if (!double.IsFinite(values[i]) || values[i] <= 0 || values[i] >= 1000)
                { error = $"unsupported：{Names[i]} PM 限制读数无效，禁止写入"; return false; }
            }
            return true;
        }
        catch (Exception ex) { error = "unsupported：RyzenAdj PM 读取失败，禁止写入：" + ex.Message; return false; }
    }

    private T Export<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static uint ToMilliwatts(double watts) => checked((uint)Math.Floor(watts * 1000));
    private static bool Ordered(double[] values) => values[0] <= values[1] && values[1] <= values[2];
    private static bool TargetOrdered(int index, double target, double[] values) => index switch
    {
        0 => target <= values[1] && target <= values[2],
        1 => values[0] <= target && target <= values[2],
        2 => values[0] <= target && values[1] <= target,
        _ => false
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_tableInitialized && _context != IntPtr.Zero) _cleanup!(_context);
            }
            finally
            {
                _context = IntPtr.Zero;
                if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
                if (_inpout != IntPtr.Zero) NativeLibrary.Free(_inpout);
                _inpout = IntPtr.Zero;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr Init();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Cleanup(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Table(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate float GetLimit(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetLimit(IntPtr context, uint milliwatts);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
}
