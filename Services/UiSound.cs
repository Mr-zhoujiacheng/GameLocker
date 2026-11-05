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
/// 界面音效：移动 / A / B / 报错 / 选择五种。移动音在运行时合成极短的 PCM16；
/// A / B / 报错 / 选择来自 kopanel 开源项目的振荡器参数（success / warning / error），已转成 WAV 嵌入资源。
/// 共用常驻 waveOut 设备直接写缓冲；播放前打断仍在队列中的音效，避免跨种类排队。
/// 播放时检查默认输出设备变化，写入失败时重新打开设备并重试一次。
/// </summary>
internal static class UiSound
{
    private const int SampleRate = 44100;

    /// <summary>总体音量系数：合成音与嵌入音效统一按此比例缩放（0.55 约 -5dB，比原始版本轻一档）</summary>
    private const double Volume = 0.55;

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrInQueue = 0x00000010;

    private static readonly object Gate = new();
    private static readonly Dictionary<UiSoundKind, Buffer> Buffers = [];
    private static IntPtr _hwo;
    private static string? _defaultDeviceId;

    /// <summary>静音开关：true 后不再发声</summary>
    public static bool Muted { get; set; }

    /// <summary>提前合成并打开音频设备：应用启动时调用一次</summary>
    public static void Open()
    {
        lock (Gate)
        {
            if (_hwo != IntPtr.Zero) return;
            try
            {
                var deviceId = GetDefaultDeviceId();
                var format = new WAVEFORMATEX
                {
                    wFormatTag = 1,
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
                _defaultDeviceId = deviceId;

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

    private static string? GetDefaultDeviceId()
    {
        try
        {
            return Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(
                Windows.Media.Devices.AudioDeviceRole.Default);
        }
        catch
        {
            // 查询失败仍沿用当前设备，不能阻止播放。
            return null;
        }
    }

    private static Feedback? _feedback;
    public static long PlayVersion { get; private set; }

    public static IDisposable BeginFeedback(UiSoundKind? fallback, bool immediate = false, bool alreadyPlayed = false)
        => new Feedback(fallback, immediate, alreadyPlayed);

    private sealed class Feedback : IDisposable
    {
        private readonly Feedback? _previous = _feedback;
        private UiSoundKind? _kind;
        private bool _explicit;
        private bool _played;

        public Feedback(UiSoundKind? fallback, bool immediate, bool alreadyPlayed)
        {
            _kind = fallback;
            _played = alreadyPlayed;
            if (immediate && !alreadyPlayed && fallback is { } kind && SettingsService.Current.UiSoundEnabled)
            {
                Play(kind);
                _played = true;
            }
            _feedback = this;
        }

        public void Set(UiSoundKind kind)
        {
            if (_explicit && _kind == UiSoundKind.Error) return;
            _explicit = true;
            _kind = kind;
            if ((!_played || kind == UiSoundKind.Error) && SettingsService.Current.UiSoundEnabled)
            {
                _feedback = _previous;
                try { Play(kind); }
                finally { _feedback = this; }
                _played = true;
            }
        }

        public void Dispose()
        {
            _feedback = _previous;
            if (!_played && _kind is { } kind && SettingsService.Current.UiSoundEnabled) Play(kind);
        }
    }

    /// <summary>播放一次音效；缓存不存在时先补建</summary>
    public static void Play(UiSoundKind kind)
    {
        if (_feedback is { } feedback)
        {
            feedback.Set(kind);
            return;
        }
        if (Muted) return;
        lock (Gate)
        {
            PlayVersion++;
            try
            {
                var deviceId = GetDefaultDeviceId();
                if (_hwo != IntPtr.Zero && deviceId != null && deviceId != _defaultDeviceId)
                    Close();
                if (_hwo == IntPtr.Zero) Open();
                if (_hwo == IntPtr.Zero || !Buffers.TryGetValue(kind, out var buffer)) return;

                // 任意种类仍在播都先打断，不能只检查本次目标缓冲。
                foreach (var queued in Buffers.Values)
                {
                    if ((Marshal.PtrToStructure<WAVEHDR>(queued.Header).dwFlags & WhdrInQueue) == 0)
                        continue;
                    waveOutReset(_hwo);
                    break;
                }
                if (waveOutWrite(_hwo, buffer.Header, (uint)Marshal.SizeOf<WAVEHDR>()) == 0) return;

                Close();
                Open();
                if (_hwo != IntPtr.Zero && Buffers.TryGetValue(kind, out buffer)
                    && waveOutWrite(_hwo, buffer.Header, (uint)Marshal.SizeOf<WAVEHDR>()) != 0)
                    Close();
            }
            catch
            {
                // 失败时释放设备和缓冲，下一次播放可重新打开。
                Close();
            }
        }
    }

    /// <summary>关闭音频设备并释放全部缓冲（应用退出时调用）</summary>
    public static void Close()
    {
        lock (Gate)
        {
            if (_hwo != IntPtr.Zero)
            {
                try { waveOutReset(_hwo); } catch { }
                foreach (var buffer in Buffers.Values)
                {
                    try { waveOutUnprepareHeader(_hwo, buffer.Header, (uint)Marshal.SizeOf<WAVEHDR>()); }
                    catch { }
                }
                try { waveOutClose(_hwo); } catch { }
                _hwo = IntPtr.Zero;
            }
            _defaultDeviceId = null;

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
        var buffer = new Buffer();
        try
        {
            buffer.Pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            buffer.Header = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            var header = new WAVEHDR
            {
                lpData = buffer.Pin.AddrOfPinnedObject(),
                dwBufferLength = (uint)pcm.Length
            };
            Marshal.StructureToPtr(header, buffer.Header, false);
            if (waveOutPrepareHeader(_hwo, buffer.Header, (uint)Marshal.SizeOf<WAVEHDR>()) != 0)
                throw new InvalidOperationException("waveOutPrepareHeader 失败");
            return buffer;
        }
        catch
        {
            if (buffer.Header != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Header);
            if (buffer.Pin.IsAllocated) buffer.Pin.Free();
            throw;
        }
    }

    /// <summary>
    /// 生成各键 PCM：移动音在运行时合成（最短、连按不拖尾）；
    /// A / B / 报错 / 选择读取嵌入的 WAV（kopanel 的 success / warning / error 参数还原，已按软件响度放大）。
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
    /// 从嵌入的 WAV 资源读 PCM 数据。转码时已固定为 44.1kHz / 单声道 / 16bit（与合成音同一格式）；
    /// 这里遍历 RIFF chunk 取 data 段，兼容带 LIST 等附加块的标准 WAV。
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
