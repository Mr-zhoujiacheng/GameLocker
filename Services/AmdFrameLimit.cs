using System.Runtime.InteropServices;

namespace GameLocker.Services;

internal sealed class AmdFrameLimit : IDisposable
{
    private const ulong InitializeVersion = (2UL << 48) | 125UL;
    private const int Success = 0;
    private const int AlreadyEnabled = 1;
    private const int AlreadyInitialized = 2;

    private IntPtr _library;
    private IntPtr _service;
    private IntPtr _list;
    private IntPtr _gpu;
    private IntPtr _frtc;
    private bool _ownsInitialization;
    private bool _disposed;
    private TerminateDelegate? _terminate;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate(ulong version, out IntPtr service);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TerminateDelegate();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetObjectDelegate(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrtcDelegate(IntPtr self, IntPtr gpu, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ListIndexDelegate(IntPtr self, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ListPositionDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsSupportedDelegate(IntPtr self, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsEnabledDelegate(IntPtr self, out byte value);
    [StructLayout(LayoutKind.Sequential)]
    private struct IntRange
    {
        public int Minimum;
        public int Maximum;
        public int Step;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRangeDelegate(IntPtr self, out IntRange range);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFpsDelegate(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEnabledDelegate(IntPtr self, byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFpsDelegate(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr self);

    public int Minimum { get; }
    public int Maximum { get; }
    public int Step { get; }

    public AmdFrameLimit()
    {
        try
        {
            string path = Path.Combine(Environment.SystemDirectory, "amdadlx64.dll");
            _library = NativeLibrary.Load(path);
            var initialize = Export<InitializeDelegate>("ADLXInitialize");
            _terminate = Export<TerminateDelegate>("ADLXTerminate");
            int status = initialize(InitializeVersion, out _service);
            if (status is not (Success or AlreadyEnabled or AlreadyInitialized))
                throw new InvalidOperationException($"ADLX 初始化失败，状态码 {status}");
            _ownsInitialization = status != AlreadyInitialized;
            if (_service == IntPtr.Zero)
                throw new InvalidOperationException("ADLX 初始化未返回系统服务对象");

            Check(Call<GetObjectDelegate>(_service, 1)(_service, out _list), "获取 GPU 列表");
            if (_list == IntPtr.Zero)
                throw new InvalidOperationException("ADLX 返回了空 GPU 列表");
            Check(Call<GetObjectDelegate>(_service, 7)(_service, out _threeDServices), "获取 3D 设置服务");
            if (_threeDServices == IntPtr.Zero)
                throw new InvalidOperationException("ADLX 返回了空服务对象");

            FindFrtc();
            Check(Call<GetRangeDelegate>(_frtc, 5)(_frtc, out var range), "读取 FRTC 范围");
            if (range.Minimum <= 0 || range.Maximum < range.Minimum || range.Step <= 0)
                throw new InvalidOperationException("ADLX 返回了无效的 FRTC 帧率范围");
            Minimum = range.Minimum;
            Maximum = range.Maximum;
            Step = range.Step;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private IntPtr _threeDServices;

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static T Call<T>(IntPtr instance, int slot) where T : Delegate
    {
        IntPtr table = Marshal.ReadIntPtr(instance);
        IntPtr function = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static bool Succeeded(int status) => status is Success or AlreadyEnabled or AlreadyInitialized;

    private static void Check(int status, string operation)
    {
        if (!Succeeded(status))
            throw new InvalidOperationException($"ADLX {operation}失败，状态码 {status}");
    }

    private void FindFrtc()
    {
        uint begin = Call<ListPositionDelegate>(_list, 5)(_list);
        uint end = Call<ListPositionDelegate>(_list, 6)(_list);
        for (uint index = begin; index < end; index++)
        {
            IntPtr candidate = IntPtr.Zero;
            IntPtr frtc = IntPtr.Zero;
            try
            {
                int status = Call<ListIndexDelegate>(_list, 11)(_list, index, out candidate);
                if (!Succeeded(status) || candidate == IntPtr.Zero) continue;
                status = Call<GetFrtcDelegate>(_threeDServices, 9)(_threeDServices, candidate, out frtc);
                if (!Succeeded(status) || frtc == IntPtr.Zero) continue;
                status = Call<IsSupportedDelegate>(frtc, 3)(frtc, out byte supported);
                if (Succeeded(status) && supported != 0)
                {
                    _gpu = candidate;
                    _frtc = frtc;
                    candidate = IntPtr.Zero;
                    frtc = IntPtr.Zero;
                    return;
                }
            }
            finally
            {
                Release(frtc);
                Release(candidate);
            }
        }
        throw new InvalidOperationException("未找到支持 FRTC 的 AMD GPU");
    }

    public int Read()
    {
        ThrowIfDisposed();
        Check(Call<IsEnabledDelegate>(_frtc, 4)(_frtc, out byte enabled), "读取 FRTC 启用状态");
        if (enabled == 0) return 0;
        Check(Call<GetFpsDelegate>(_frtc, 6)(_frtc, out int fps), "读取 FRTC 帧率");
        return fps;
    }

    public void Write(int fps)
    {
        ThrowIfDisposed();
        if (fps != 0 && (fps < Minimum || fps > Maximum || (fps - Minimum) % Step != 0))
            throw new ArgumentOutOfRangeException(nameof(fps), fps, $"帧率必须在 {Minimum}–{Maximum} FPS 范围内，并按 {Step} FPS 步进");

        Check(Call<IsEnabledDelegate>(_frtc, 4)(_frtc, out byte oldEnabled), "保存 FRTC 启用状态");
        Check(Call<GetFpsDelegate>(_frtc, 6)(_frtc, out int oldFps), "保存 FRTC 帧率");
        try
        {
            if (fps == 0)
                Check(Call<SetEnabledDelegate>(_frtc, 7)(_frtc, 0), "关闭 FRTC");
            else
            {
                Check(Call<SetFpsDelegate>(_frtc, 8)(_frtc, fps), "设置 FRTC 帧率");
                Check(Call<SetEnabledDelegate>(_frtc, 7)(_frtc, 1), "启用 FRTC");
            }
            if (Read() != fps) throw new InvalidOperationException("AMD FRTC 写入后读回值不一致");
        }
        catch (Exception setError)
        {
            var restoreErrors = new List<string>();
            try { Check(Call<SetFpsDelegate>(_frtc, 8)(_frtc, oldFps), "恢复 FRTC 帧率"); }
            catch (Exception ex) { restoreErrors.Add(ex.Message); }
            try { Check(Call<SetEnabledDelegate>(_frtc, 7)(_frtc, oldEnabled), "恢复 FRTC 启用状态"); }
            catch (Exception ex) { restoreErrors.Add(ex.Message); }
            string restore = restoreErrors.Count == 0 ? "已尝试恢复旧帧率和启用状态。" : "恢复时发生错误：" + string.Join("；", restoreErrors);
            throw new InvalidOperationException($"设置 AMD 驱动限帧失败：{setError.Message}；{restore}", setError);
        }
    }

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            try { Call<ReleaseDelegate>(instance, 1)(instance); } catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AmdFrameLimit));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release(_frtc);
        _frtc = IntPtr.Zero;
        Release(_gpu);
        _gpu = IntPtr.Zero;
        Release(_threeDServices);
        _threeDServices = IntPtr.Zero;
        Release(_list);
        _list = IntPtr.Zero;
        if (_ownsInitialization)
        {
            try { _terminate?.Invoke(); } catch { }
            _ownsInitialization = false;
        }
        _service = IntPtr.Zero;
        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}
