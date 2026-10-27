using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>手柄操作的界面音效种类</summary>
internal enum UiSoundKind
{
    /// <summary>磁贴之间移动（方向键/左摇杆）</summary>
    Move,
    /// <summary>A 键（确认/启动）</summary>
    Confirm,
    /// <summary>B 键（返回/收起）</summary>
    Back
}

/// <summary>
/// 手柄操作的界面音效：移动 / A / B 三种，运行时合成极短的 PCM16，
/// 共用一个常驻 waveOut 设备直接写缓冲，避免 PlaySound 每次开关音频设备的延迟，
/// 连续按方向键也不会有明显滞后。
/// </summary>
internal static class UiSound
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrDone = 0x00000001;
    private const int SampleRate = 44100;

    private static readonly object Gate = new();
    private static readonly Dictionary<UiSoundKind, Buffer> Buffers = [];
    private static IntPtr _hwo;

    /// <summary>静音开关：true 后不再发声</summary>
    public static bool Muted { get; set; }

    /// <summary>提前合成并打开音频设备：应用启动时调用一次，第一次按键就能立刻出声</summary>
    public static void Open()
    {
        lock (Gate)
        {
            if (_hwo != IntPtr.Zero) return;
            try
            {
                var format = new WAVEFORMATEX
                {
                    wFormatTag = 1,          // PCM
                    nChannels = 1,
                    nSamplesPerSec = SampleRate,
                    nAvgBytesPerSec = SampleRate * 2,
                    nBlockAlign = 2,
                    wBitsPerSample = 16,
                    cbSize = 0
                };
                if (waveOutOpen(out var hwo, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                    return;
                _hwo = hwo;

                foreach (var kind in Enum.GetValues<UiSoundKind>())
                    Buffers[kind] = CreateBuffer(kind);
            }
            catch
            {
                // 音效失败不影响手柄操作
                Close();
            }
        }
    }

    /// <summary>播放一次音效；设备没打开时先补开</summary>
    public static void Play(UiSoundKind kind)
    {
        if (Muted) return;
        if (_hwo == IntPtr.Zero) Open();
        if (_hwo == IntPtr.Zero || !Buffers.TryGetValue(kind, out var buffer)) return;
        try
        {
            // 上一声还没播完就先掐掉，让这次操作立刻有反馈，而不是排队等几十毫秒
            var header = Marshal.PtrToStructure<WAVEHDR>(buffer.Header);
            if ((header.dwFlags & WhdrDone) == 0) waveOutReset(_hwo);
            waveOutWrite(_hwo, buffer.Header, (uint)Marshal.SizeOf<WAVEHDR>());
        }
        catch
        {
            // 忽略：音效只是附加反馈
        }
    }

    /// <summary>关闭音频设备并释放全部缓冲（应用退出时调用）</summary>
    public static void Close()
    {
        lock (Gate)
        {
            if (_hwo != IntPtr.Zero)
            {
                try
                {
                    waveOutReset(_hwo);
                    foreach (var buffer in Buffers.Values)
                        waveOutUnprepareHeader(_hwo, buffer.Header, (uint)Marshal.SizeOf<WAVEHDR>());
                    waveOutClose(_hwo);
                }
                catch { }
                _hwo = IntPtr.Zero;
            }

            foreach (var buffer in Buffers.Values)
            {
                if (buffer.Header != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Header);
                if (buffer.Pin.IsAllocated) buffer.Pin.Free();
            }
            Buffers.Clear();
        }
    }

    private sealed class Buffer
    {
        public GCHandle Pin;
        public IntPtr Header;
    }

    private static Buffer CreateBuffer(UiSoundKind kind)
    {
        var pcm = CreatePcm(kind);
        var pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        var headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
        var header = new WAVEHDR
        {
            lpData = pin.AddrOfPinnedObject(),
            dwBufferLength = (uint)pcm.Length
        };
        Marshal.StructureToPtr(header, headerPtr, false);
        if (waveOutPrepareHeader(_hwo, headerPtr, (uint)Marshal.SizeOf<WAVEHDR>()) != 0)
        {
            Marshal.FreeHGlobal(headerPtr);
            pin.Free();
            throw new InvalidOperationException("waveOutPrepareHeader 失败");
        }
        return new Buffer { Pin = pin, Header = headerPtr };
    }

    /// <summary>
    /// 合成一段短音（气泡 pop 风格）：音高在几十毫秒内快速下坠，听感软糯，
    /// 移动 / A / B 用同一种音色，只靠起止音高区分；2.5ms 起音 + 指数衰减 + 尾巴淡出，圆润不刺耳。
    /// </summary>
    private static byte[] CreatePcm(UiSoundKind kind)
    {
        var durationMs = kind == UiSoundKind.Confirm ? 70 : 50;
        var count = SampleRate * durationMs / 1000;
        var duration = durationMs / 1000.0;
        var bytes = new byte[count * 2];

        var (startHz, endHz) = kind switch
        {
            UiSoundKind.Move => (520.0, 150.0),    // 下滑幅度小，轻点
            UiSoundKind.Confirm => (660.0, 220.0), // A：起音更高，落点更实
            _ => (440.0, 110.0)                     // B：整体更低，回落感
        };

        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            var freq = startHz + (endHz - startHz) * (t / duration);
            var v = Math.Sin(2 * Math.PI * freq * t) * SoftAttack(t) * Math.Exp(-t / 0.016) * 0.62;

            // 统一在最后 6ms 淡出，保证包络收在 0，避免尾部爆音
            var tail = duration - t;
            if (tail < 0.006) v *= tail / 0.006;

            var sample = (short)Math.Clamp(v * 12000, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>2.5ms 平滑起音，避免音头“啪”的一声</summary>
    private static double SoftAttack(double t) => t < 0.0025 ? t / 0.0025 : 1.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hwo, uint deviceId, ref WAVEFORMATEX format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);
}
