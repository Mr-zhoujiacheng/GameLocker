using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace GameLocker.Controls;

/// <summary>
/// Mascot sprite playback; interaction and idle sleep are handled by the other partial.
/// </summary>
public sealed partial class PixelMascot : UserControl
{
    private const int BlinkRow = 1, GripRow = 2, SwishRow = 3, HappyRow = 4, SleepRow = 5, WakeRow = 6, DragRow = 7, AngryRow = 8;
    private const int HeadphonesRow = 9, ListenRow = 10, ListenDragRow = 11, ListenHappyRow = 12, ListenAngryRow = 13;
    private const int PeekRow = 14, WinkRow = 15, BlushRow = 16;
    private static readonly string[] Actions =
        { "idle", "blink", "grip", "swish", "happy", "sleep", "wake", "scared", "angry",
          "headphones", "listen", "listen-scared", "listen-happy", "listen-angry", "peek", "wink", "blush" };
    private static readonly int[] FrameMs = { 150, 70, 130, 130, 160, 230, 85, 100, 150, 95, 135, 100, 160, 150, 135, 105, 135 };
    private static readonly int[] FrameCount = { 8, 4, 8, 8, 8, 8, 8, 8, 8, 6, 8, 8, 8, 8, 8, 8, 8 };
    private static readonly int[] Loops = { 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
    private const int BareHeadphonesFrame = 0;
    private static readonly int WornHeadphonesFrame = FrameCount[HeadphonesRow] - 1;

    private const int TailMovingMs = 10_000;
    private const int TailCycleMs = TailMovingMs + 5_000;
    private const int FrameWidth = 96, FrameHeight = 60;
    private readonly ReadOnlyMemory<byte>[,,] _frames = new ReadOnlyMemory<byte>[Actions.Length, 8, 2];
    private readonly WriteableBitmap _surface = new(FrameWidth, FrameHeight);
    private readonly DispatcherTimer _timer = new();
    private readonly Random _rng = new();

    private int _row;
    private int _col;
    private ReadOnlyMemory<byte> _shownFrame;
    private int _loopsLeft;
    private long _frameAt;
    private long _nextSpecialAt;
    private long _tailStartedAt;
    private bool _musicPlaying;
    private Task? _preload;

    public PixelMascot()
    {
        InitializeComponent();
        FrameImage.Source = _surface;

        _timer.Interval = TimeSpan.FromMilliseconds(45);
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += (_, _) => SuspendPlayback();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await (_preload ??= PreloadAsync());
        TryStartPlayback();
    }

    internal void PrepareIdleFrame() => PresentFrame(_frames[0, 0, 0]);

    internal void SetMusicPlaying(bool playing)
    {
        if (_musicPlaying == playing) return;
        _musicPlaying = playing;
        if (!_timer.IsEnabled)
        {
            // 编辑态保留静态猫娘，也要同步耳机状态。
            PresentFrame(_frames[playing ? ListenRow : 0, 0, 0]);
            return;
        }

        long now = Environment.TickCount64;
        _lastActivityAt = now;
        _deepSleeping = false;
        if (_row == HeadphonesRow) return; // 下一帧从当前位置反向播放

        _row = HeadphonesRow;
        _col = playing ? BareHeadphonesFrame : WornHeadphonesFrame;
        ShowFrame(now);
        _frameAt = now + FrameMs[HeadphonesRow];
    }

    private void TryStartPlayback()
    {
        if (!_pageActive || !IsLoaded || _timer.IsEnabled || _preload?.IsCompletedSuccessfully != true) return;

        _lastActivityAt = _tailStartedAt = Environment.TickCount64;
        StartIdle(_tailStartedAt);
        _timer.Start();
    }

    private async Task PreloadAsync()
    {
        for (int row = 0; row < Actions.Length; row++)
            await LoadRowAsync(row);
    }

    private async Task LoadRowAsync(int row)
    {
        for (int col = 0; col < FrameCount[row]; col++)
        for (int tail = 0; tail < 2; tail++)
        {
            var variant = tail == 0 ? "" : "rest-";
            var name = $"catmaid-{variant}{Actions[row]}-{col}.png";
            try
            {
                using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Mascot", name));
                using var stream = file.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                if (decoder.PixelWidth != FrameWidth || decoder.PixelHeight != FrameHeight) continue;
                var decoded = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                var pixels = decoded.DetachPixelData();
                if (pixels.Length != FrameWidth * FrameHeight * 4) continue;
                _frames[row, col, tail] = pixels;
                if (_shownFrame.IsEmpty) PresentFrame(pixels);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Mascot frame '{name}' could not be decoded: {ex.Message}");
            }
        }
    }

    private void OnTick(object? sender, object e)
    {
        long now = Environment.TickCount64;
        UpdateInteraction(now);
        if (now < _frameAt)
        {
            ShowFrame(now);
            return;
        }

        if (_row == HeadphonesRow)
        {
            AdvanceHeadphones(now);
            return;
        }

        _col++;
        if (_col < FrameCount[_row])
        {
            ShowFrame(now);
            _frameAt = now + FrameMs[_row];
            return;
        }

        _col = 0;
        if (_deepSleeping || _row is DragRow or ListenRow)
        {
            ShowFrame(now);
            _frameAt = now + FrameMs[_row];
            return;
        }

        if (_row == 0)
        {
            if (now >= _nextSpecialAt)
            {
                StartSpecial(now);
            }
            else
            {
                ShowFrame(now);
                _frameAt = now + FrameMs[0];
            }
            return;
        }

        _loopsLeft--;
        if (_loopsLeft > 0)
        {
            ShowFrame(now);
            _frameAt = now + FrameMs[_row];
            return;
        }

        StartIdle(now);
    }

    private void AdvanceHeadphones(long now)
    {
        var target = _musicPlaying ? WornHeadphonesFrame : BareHeadphonesFrame;
        if (_col == target)
        {
            if (_dragging || _returnAt.HasValue) StartPose(DragRow, now);
            else StartIdle(now);
            return;
        }

        _col += _musicPlaying ? 1 : -1;
        ShowFrame(now);
        _frameAt = now + FrameMs[HeadphonesRow];
    }

    /// <summary>回到待机或继续听歌。</summary>
    private void StartIdle(long now)
    {
        if (_musicPlaying)
        {
            StartPose(ListenRow, now);
            return;
        }
        _row = 0;
        _col = 0;
        _loopsLeft = 1;
        _nextSpecialAt = now + 3200 + _rng.Next(3200);
        ShowFrame(now);
        _frameAt = now + FrameMs[0];
    }

    /// <summary>随机挑一个待机动作；睡觉只由无操作计时触发。</summary>
    private void StartSpecial(long now)
    {
        int row = _rng.NextDouble() switch
        {
            < 0.22 => BlinkRow,
            < 0.36 => GripRow,
            < 0.50 => SwishRow,
            < 0.62 => HappyRow,
            < 0.78 => PeekRow,
            < 0.90 => WinkRow,
            _ => BlushRow
        };
        StartPose(row, now);
    }

    private void StartPose(int row, long now)
    {
        _row = row;
        _col = 0;
        _loopsLeft = Loops[_row];
        ShowFrame(now);
        _frameAt = now + FrameMs[_row];
    }

    private static bool IsTailResting(long elapsedMs) => elapsedMs % TailCycleMs >= TailMovingMs;

    private void ShowFrame(long now)
    {
        bool tailResting = _row == SleepRow || IsTailResting(now - _tailStartedAt);
        int spriteRow = _musicPlaying ? _row switch
        {
            DragRow => ListenDragRow,
            HappyRow => ListenHappyRow,
            AngryRow => ListenAngryRow,
            _ => _row
        } : _row;
        PresentFrame(_frames[spriteRow, _col, tailResting ? 1 : 0]);
    }

    private void PresentFrame(ReadOnlyMemory<byte> frame)
    {
        // Never unload the current image if the next frame is unavailable.
        if (frame.IsEmpty || frame.Equals(_shownFrame)) return;
        using var buffer = _surface.PixelBuffer.AsStream();
        buffer.Position = 0;
        buffer.Write(frame.Span);
        _surface.Invalidate();
        _shownFrame = frame;
    }
}
