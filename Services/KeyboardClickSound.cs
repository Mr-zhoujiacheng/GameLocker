using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 屏幕键盘的点击音效：运行时合成一小段机械键盘声（PCM16），
/// 用常驻的 waveOut 设备直接写入播放。
/// 不用 PlaySound 的原因：它每次发声都要开关一次音频设备，实测有几十毫秒延迟，
/// 常驻设备后按键声基本和按下同时出现。
/// </summary>
internal static class KeyboardClickSound
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrDone = 0x00000001;

    private static readonly object Gate = new();
    private static IntPtr _hwo;
    private static IntPtr _headerPtr;
    private static GCHandle _wavePin;
    private static byte[]? _pcm;

    /// <summary>关掉音效（设 true 后按键不再发声）</summary>
    public static bool Muted { get; set; }

    /// <summary>提前合成并打开音频设备：在键盘显示时调用，避免第一次按键才初始化</summary>
    public static void Open()
    {
        lock (Gate)
        {
            if (_hwo != IntPtr.Zero) return;
            try
            {
                _pcm = CreatePcm();
                _wavePin = GCHandle.Alloc(_pcm, GCHandleType.Pinned);

                var fmt = new WAVEFORMATEX
                {
                    wFormatTag = 1,          // PCM
                    nChannels = 1,
                    nSamplesPerSec = SampleRate,
                    nAvgBytesPerSec = SampleRate * 2,
                    nBlockAlign = 2,
                    wBitsPerSample = 16,
                    cbSize = 0
                };
                if (waveOutOpen(out var hwo, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                {
                    _wavePin.Free();
                    _pcm = null;
                    return;
                }
                _hwo = hwo;

                _headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
                var header = new WAVEHDR
                {
                    lpData = _wavePin.AddrOfPinnedObject(),
                    dwBufferLength = (uint)_pcm.Length
                };
                Marshal.StructureToPtr(header, _headerPtr, false);
                if (waveOutPrepareHeader(_hwo, _headerPtr, (uint)Marshal.SizeOf<WAVEHDR>()) != 0)
                {
                    Marshal.FreeHGlobal(_headerPtr);
                    _headerPtr = IntPtr.Zero;
                    waveOutClose(_hwo);
                    _hwo = IntPtr.Zero;
                    _wavePin.Free();
                    _pcm = null;
                }
            }
            catch
            {
                // 音效失败不影响键盘输入
            }
        }
    }

    /// <summary>播放一次按键声；设备没打开时先补开</summary>
    public static void Play()
    {
        if (Muted) return;
        if (_hwo == IntPtr.Zero) Open();
        if (_hwo == IntPtr.Zero) return;
        try
        {
            // 还在播上一声就先掐掉，保证这次按键立刻发声，而不是排队等 60ms
            var header = Marshal.PtrToStructure<WAVEHDR>(_headerPtr);
            if ((header.dwFlags & WhdrDone) == 0) waveOutReset(_hwo);
            waveOutWrite(_hwo, _headerPtr, (uint)Marshal.SizeOf<WAVEHDR>());
        }
        catch
        {
            // 忽略：音效只是附加效果
        }
    }

    /// <summary>关闭音频设备并释放缓冲（键盘窗口销毁时调用）</summary>
    public static void Close()
    {
        lock (Gate)
        {
            if (_hwo != IntPtr.Zero)
            {
                try
                {
                    waveOutReset(_hwo);
                    if (_headerPtr != IntPtr.Zero)
                        waveOutUnprepareHeader(_hwo, _headerPtr, (uint)Marshal.SizeOf<WAVEHDR>());
                    waveOutClose(_hwo);
                }
                catch { }
                _hwo = IntPtr.Zero;
            }
            if (_headerPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_headerPtr);
                _headerPtr = IntPtr.Zero;
            }
            if (_wavePin.IsAllocated) _wavePin.Free();
            _pcm = null;
        }
    }

    private const int SampleRate = 44100;

    /// <summary>
    /// 合成机械键盘的“嗒”：极短的噪声爆音（键帽撞击）+ 低频 thock + 高频 tick，
    /// 各分量指数衰减，尾部淡出，避免结束时爆音。只要 PCM 采样，不含 WAV 头。
    /// </summary>
    private static byte[] CreatePcm()
    {
        const int durationMs = 60;
        const int count = SampleRate * durationMs / 1000;
        const double fadeOutStart = 0.048;
        const double duration = durationMs / 1000.0;

        var bytes = new byte[count * 2];
        var rng = 0x2545F491;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;

            // 噪声爆音：模拟键帽与轴体撞击的“啪”，衰减极快
            rng = unchecked(rng * 1103515245 + 12345);
            var noise = ((rng >> 16) & 0xFFFF) / 32768.0 - 1.0;
            var click = noise * Math.Exp(-t / 0.005) * 0.5;

            // 低频 thock：机械键盘厚实的“咚”
            var thock = Math.Sin(2 * Math.PI * 168 * t) * Math.Exp(-t / 0.022) * 0.46;

            // 高频 tick：让声音清脆一点
            var tick = Math.Sin(2 * Math.PI * 1750 * t) * Math.Exp(-t / 0.006) * 0.18;

            var v = click + thock + tick;
            if (t < 0.0008) v *= t / 0.0008;                       // 0.8ms 起音，避免起头爆音
            if (t > fadeOutStart) v *= (duration - t) / (duration - fadeOutStart);

            var sample = (short)Math.Clamp(v * 26000, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return bytes;
    }

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
