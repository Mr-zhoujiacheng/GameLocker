using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>手柄操作的界面音效种类</summary>
internal enum UiSoundKind
{
    /// <summary>磁贴之间移动（方向键/左摇杆）</summary>
    Move,
    /// <summary>A 键（确认/启动）</summary>
    Confirm,
    /// <summary>鼠标/触摸点中开关、下拉、列表项、磁贴等控件时的选择反馈音（沿用旧版 A 键音）</summary>
    Select,
    /// <summary>B 键（返回/收起）</summary>
    Back,
    /// <summary>报错、未检测到、操作失败等提示</summary>
    Error
}

/// <summary>
/// 界面音效：移动 / A / B / 报错四种。移动音在运行时合成极短的 PCM16；
/// A / B / 报错来自 kopanel 开源项目的振荡器参数（success / warning / error），已转成 WAV 嵌入资源。
/// 四种共用一个常驻 waveOut 设备直接写缓冲，避免 PlaySound 每次开关音频设备的延迟，
/// 连续按方向键也不会有明显滞后。
/// </summary>
internal static class UiSound
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrDone = 0x00000001;
    private const int SampleRate = 44100;

    /// <summary>总体音量系数：合成音与嵌入音效统一按此比例缩放（0.55 约 -5dB，比原始版本轻一档）</summary>
    private const double Volume = 0.55;

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
        var pcm = ScalePcm(CreatePcm(kind), Volume);
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
    /// 生成各键 PCM：移动音在运行时合成（最短、连按不拖尾）；
    /// A / B / 报错读取嵌入的 WAV（kopanel 的 success / warning / error 参数还原，已按软件响度放大）。
    /// </summary>
    private static byte[] CreatePcm(UiSoundKind kind)
    {
        if (kind != UiSoundKind.Move)
        {
            return LoadPcmResource(kind switch
            {
                UiSoundKind.Confirm => "UiSoundConfirm.wav",
                UiSoundKind.Select => "UiSoundSelect.wav",
                UiSoundKind.Back => "UiSoundBack.wav",
                _ => "UiSoundError.wav"
            });
        }

        // 越短越像机械按键的"嗒"，移动音最短，连按方向键不会连成一片
        const int durationMs = 30;
        var count = SampleRate * durationMs / 1000;
        var duration = durationMs / 1000.0;
        var bytes = new byte[count * 2];

        // 轻抬走向 + 二次谐波颗粒感；decay 越短尾音收得越快
        const double startHz = 640.0, endHz = 820.0, decay = 0.0055, gain = 0.62, harmonic = 0.16;

        // 相位累加：每一步按瞬时频率递推，避免"频率 × 时间"的写法在扫频时产生额外的频率抖动
        var phase = 0.0;
        var fadeOut = Math.Min(0.004, duration / 8);
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            var freq = startHz + (endHz - startHz) * (t / duration);
            phase += 2 * Math.PI * freq / SampleRate;
            var v = (Math.Sin(phase) + harmonic * Math.Sin(phase * 2))
                    * SoftAttack(t) * Math.Exp(-t / decay) * gain;

            // 统一在末尾短淡出，保证包络收在 0，避免尾部爆音
            var tail = duration - t;
            if (tail < fadeOut) v *= tail / fadeOut;

            var sample = (short)Math.Clamp(v * 12000, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>按比例缩放 16bit PCM 的振幅（不改变波形与音色，只调响度）</summary>
    private static byte[] ScalePcm(byte[] pcm, double scale)
    {
        if (Math.Abs(scale - 1) < 0.001) return pcm;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (short)Math.Clamp(Math.Round(sample * scale), short.MinValue, short.MaxValue);
            pcm[i] = (byte)(scaled & 0xFF);
            pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>
    /// 从嵌入的 WAV 资源读 PCM 数据。转码时已固定为 44.1kHz / 单声道 / 16bit（与合成音同一格式，
    /// 可直接走同一条 waveOut 缓冲）；这里遍历 RIFF chunk 取 data 段，兼容带 LIST 等附加块的标准 WAV。
    /// </summary>
    private static byte[] LoadPcmResource(string name)
    {
        var asm = typeof(UiSound).Assembly;
        using var stream = asm.GetManifestResourceStream($"GameLocker.Resources.{name}")
            ?? throw new InvalidOperationException($"缺少音效资源 {name}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        // RIFF 头：'RIFF' + size + 'WAVE'，其后是若干 chunk（4 字节 id + 4 字节长度 + 数据）
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = BitConverter.ToInt32(bytes, pos);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == 0x61746164) // 'data'
            {
                var pcm = new byte[size];
                Array.Copy(bytes, pos + 8, pcm, 0, size);
                return pcm;
            }
            pos += 8 + size + (size & 1); // chunk 数据按 2 字节对齐
        }
        throw new InvalidOperationException($"{name} 中找不到 data 块");
    }

    /// <summary>1.5ms 平滑起音：够快显出清脆感，又不会在音头"啪"的一声</summary>
    private static double SoftAttack(double t) => t < 0.0015 ? t / 0.0015 : 1.0;

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
