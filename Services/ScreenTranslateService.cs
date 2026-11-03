using System.Runtime.InteropServices;
using System.Text;
using GameLocker.Models;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameLocker.Services;

/// <summary>
/// 屏幕翻译：按 A 隐藏启动器 → 右上角面板提示"点击要翻译的窗口" → 用户点击目标窗口锁定 →
/// 点面板「开始翻译」才截图 OCR → 逐行批量翻译 → 把译文覆盖在原文位置（替换原文）。
/// 「停止翻译」只撤掉译文、恢复原状，不退出；再按一次 A 才结束整个流程。窗口移动跟随，最小化/关闭自动结束。
/// </summary>
public static class ScreenTranslateService
{
    private const int PollMs = 150;
    private const int LockDelayMs = 400;
    private const int SelectTimeoutMs = 2500;
    private const int MinWidth = 320;
    private const int MinHeight = 180;

    private const int StageIdle = 0;
    private const int StageSelecting = 1;
    private const int StageShowing = 2;

    private static volatile bool _running;
    private static volatile int _stage = StageIdle;
    private static LaunchItem? _item;
    private static Timer? _timer;
    private static long _seq;
    private static IntPtr _target;
    private static IntPtr _pending;
    private static long _pendingSince;
    private static bool _mouseDown;
    private static int _watchTicks;
    private static readonly uint _selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    /// <summary>当前是否处于翻译流程（等待选窗、已选中窗口等待开始，或正在显示译文）</summary>
    public static bool IsRunning => _running;

    /// <summary>是否正在翻译并显示译文（决定面板按钮是「停止翻译」还是「开始翻译」）</summary>
    private static bool IsTranslating => _stage == StageShowing;

    /// <summary>A 键开关：运行中则关闭，否则按当前设置开启</summary>
    public static bool Toggle(LaunchItem item)
    {
        if (IsRunning)
        {
            Stop();
            return false;
        }
        Start(item);
        return true;
    }

    public static void Start(LaunchItem item)
    {
        // 已在流程中（编辑页保存了翻译设置）：只更新设置；正在翻译时用新设置原地重译
        if (_running && _target != IntPtr.Zero && IsLiveTarget(_target))
        {
            _item = item;
            if (_stage == StageShowing)
            {
                var again = Interlocked.Increment(ref _seq);
                _ = TranslateTargetAsync(again);
            }
            return;
        }

        Stop();
        _item = item;
        _running = true;
        _stage = StageSelecting;
        _target = IntPtr.Zero;
        _pending = IntPtr.Zero;
        _pendingSince = 0;
        _watchTicks = 0;
        _mouseDown = LeftDown();

        Interlocked.Increment(ref _seq);
        OnUi(() =>
        {
            App.Main?.HideToTray();
            Hud.ShowPrompt("点击要翻译的窗口", "选中后点「开始翻译」· 按 A 关闭");
        });

        var timer = _timer;
        _timer = null;
        timer?.Dispose();
        _timer = new Timer(_ => Tick(), null, PollMs, PollMs);
    }

    public static void Stop()
    {
        _running = false;
        _stage = StageIdle;
        _target = IntPtr.Zero;
        _pending = IntPtr.Zero;
        _item = null;
        Interlocked.Increment(ref _seq);
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
        OnUi(Hud.HideAll);
    }

    private static void Tick()
    {
        if (!_running) return;
        try
        {
            if (_stage == StageSelecting)
            {
                PollSelector();
            }
            else if (_stage == StageShowing && ++_watchTicks % 2 == 0) WatchTarget();
        }
        catch { /* 单次轮询失败不影响后续 */ }
    }

    /// <summary>选窗阶段：盯住前台窗口，用户点击某个窗口使其获得焦点后立刻锁定</summary>
    private static void PollSelector()
    {
        var hwnd = GetForegroundWindow();
        var down = LeftDown();
        var clicked = down && !_mouseDown;
        _mouseDown = down;

        if (!IsCandidate(hwnd))
        {
            _pending = IntPtr.Zero;
            _pendingSince = 0;
            return;
        }

        if (_pending == IntPtr.Zero)
        {
            // 隐藏启动器后系统会把前台交还给原来的窗口：先记为候选，等用户点击或超时后锁定
            _pending = hwnd;
            _pendingSince = Environment.TickCount64;
        }
        else if (_pending != hwnd)
        {
            Lock(hwnd); // 前台换成了另一个窗口 = 用户刚点了它
            return;
        }

        // 点控制面板上的「停止翻译 / 重新选取」不算选中窗口
        if ((clicked && !Hud.PanelContainsCursor()) ||
            Environment.TickCount64 - _pendingSince >= SelectTimeoutMs) Lock(hwnd);
    }

    /// <summary>可翻译的候选窗口：排除本进程、桌面/任务栏、最小化与过小的窗口</summary>
    private static bool IsCandidate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == Hud.OverlayHandle || hwnd == Hud.PanelHandle) return false;
        if (IsIconic(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _selfPid) return false;
        var cls = ClassNameOf(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return false;
        return rect.Right - rect.Left >= MinWidth && rect.Bottom - rect.Top >= MinHeight;
    }

    /// <summary>锁定目标窗口（只选中、不翻译，等用户点面板的「开始翻译」）</summary>
    private static void Lock(IntPtr hwnd)
    {
        if (!_running) return;
        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return;
        if (rect.Right - rect.Left < MinWidth || rect.Bottom - rect.Top < MinHeight) return;

        var changed = _target != hwnd;
        if (changed && _stage == StageShowing)
        {
            // 翻译中换了目标窗口：取消进行中的识别/翻译，撤掉旧译文，等用户再点开始
            Interlocked.Increment(ref _seq);
            _stage = StageSelecting;
            OnUi(Hud.HideOverlay);
        }
        _target = hwnd;
        _pending = IntPtr.Zero;
        _pendingSince = 0;
        _watchTicks = 0;
        if (!changed) return; // 同一个窗口：保留当前状态文案（例如"翻译失败"提示）
        OnUi(() =>
        {
            Hud.ShowPanel();
            Hud.SetStatus("已选中窗口", "点「开始翻译」开始翻译");
        });
    }

    /// <summary>截图 → OCR → 批量翻译 → 在 UI 线程重建覆盖层</summary>
    private static async Task TranslateTargetAsync(long seq)
    {
        await Task.Delay(LockDelayMs);
        if (seq != _seq || !_running) return;

        var item = _item;
        var hwnd = _target;
        if (item is null || hwnd == IntPtr.Zero) return;
        if (!IsLiveTarget(hwnd))
        {
            OnUi(Stop);
            return;
        }

        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w < MinWidth || h < MinHeight) return;

        var cap = Capture(rect, w, h);
        if (cap is null)
        {
            DebugLog.Write($"屏幕翻译: 截图失败 {w}x{h}");
            Retry("截图失败", "可再点「开始翻译」重试");
            return;
        }

        var lines = await OcrAsync(cap, w, h, item.TranslateSource);
        if (seq != _seq || !_running) return;
        if (lines.Count == 0)
        {
            DebugLog.Write($"屏幕翻译: OCR 未识别到文字 {w}x{h}");
            Retry("未识别到文字", "可换一个窗口再试");
            return;
        }

        // 目标为中文时，纯中文/数字行没必要再翻：省免费额度，也避免把已经是中文的内容再盖一遍
        var candidates = new List<TextLine>(lines.Count);
        var payload = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (item.TranslateTarget == "zh" && !NeedsTranslation(l.Text)) continue;
            candidates.Add(l);
            payload.Add(l.Text);
        }
        if (payload.Count == 0)
        {
            Retry("没有可翻译的文字", "该窗口的文字已经是目标语言");
            return;
        }

        OnUi(() => Hud.SetStatus($"正在翻译 {payload.Count} 行…", "在线翻译中，请稍候"));

        var translated = await TranslateService.TranslateLinesAsync(
            payload, item.TranslateEngine, item.TranslateApiKey, item.TranslateRegion,
            item.TranslateSource, item.TranslateTarget);
        if (seq != _seq || !_running) return;

        var ok = translated.Count(t => !string.IsNullOrWhiteSpace(t));
        DebugLog.Write($"屏幕翻译: OCR {lines.Count} 行, 待译 {payload.Count} 行, 成功 {ok} 行, 引擎={item.TranslateEngine}");
        if (ok == 0)
        {
            Retry("翻译失败", "在线翻译不可用，可在编辑页填入 Azure 密钥");
            return;
        }

        var drawn = new List<OverlayLine>(candidates.Count);
        for (var i = 0; i < candidates.Count && i < translated.Count; i++)
        {
            var text = translated[i]?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var line = candidates[i];
            var x = Math.Max(0, line.X - 5);
            var y = Math.Max(0, line.Y - 2);
            var right = Math.Min(w, line.X + line.W + 5);
            var bottom = Math.Min(h, line.Y + line.H + 2);
            var bw = right - x;
            var bh = bottom - y;
            if (bw <= 2 || bh <= 2) continue;

            var bg = SampleBackground(cap.Pixels, w, h, x, y, bw, bh);
            var fg = ContrastText(bg);
            drawn.Add(new OverlayLine(text, x, y, bw, bh, bg, fg));
        }

        if (drawn.Count == 0)
        {
            Retry("翻译失败", "检查翻译引擎设置后重试");
            return;
        }

        var rectCopy = rect;
        OnUi(() =>
        {
            Hud.ShowOverlay(drawn, rectCopy);
            Hud.SetStatus($"已翻译 {drawn.Count} 行", "点「停止翻译」恢复原状");
        });
    }

    /// <summary>识别/翻译失败：撤掉译文、停留在原窗口，用户可再点「开始翻译」重试</summary>
    private static void Retry(string title, string sub)
    {
        if (!_running) return;
        Interlocked.Increment(ref _seq);
        _stage = StageSelecting;
        _pending = _target;
        _pendingSince = Environment.TickCount64;
        _mouseDown = LeftDown();
        OnUi(() =>
        {
            Hud.HideOverlay();
            Hud.ShowPanel();
            Hud.SetStatus(title, sub);
        });
    }

    /// <summary>面板「开始翻译」：截图 → OCR → 翻译，并把译文覆盖在原文上</summary>
    private static void StartTranslating()
    {
        if (!_running) return;
        var hwnd = _target;
        if (hwnd == IntPtr.Zero || !IsLiveTarget(hwnd))
        {
            _target = IntPtr.Zero;
            OnUi(() =>
            {
                Hud.ShowPanel();
                Hud.SetStatus("请先点击要翻译的窗口", "选中窗口后再点「开始翻译」");
            });
            return;
        }

        _stage = StageShowing;
        _watchTicks = 0;
        var seq = Interlocked.Increment(ref _seq);
        OnUi(() => Hud.SetStatus("正在识别文字…", "请稍候"));
        _ = TranslateTargetAsync(seq);
    }

    /// <summary>面板「停止翻译」：只撤掉译文恢复原状，流程与面板保持，可再次开始</summary>
    private static void StopTranslating()
    {
        if (!_running) return;
        Interlocked.Increment(ref _seq);
        _stage = StageSelecting;
        _pending = _target;
        _pendingSince = Environment.TickCount64;
        _mouseDown = LeftDown();
        OnUi(() =>
        {
            Hud.HideOverlay();
            Hud.ShowPanel();
            Hud.SetStatus("已停止翻译", "点「开始翻译」可再次翻译");
        });
    }

    /// <summary>目标窗口看门狗：关闭/最小化则停止；被启动器遮住时临时隐藏；移动时跟随</summary>
    private static void WatchTarget()
    {
        if (!_running || _stage != StageShowing) return;
        var hwnd = _target;
        if (!IsLiveTarget(hwnd))
        {
            OnUi(Stop);
            return;
        }

        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return;
        if (rect.Right - rect.Left < MinWidth || rect.Bottom - rect.Top < MinHeight)
        {
            OnUi(Stop);
            return;
        }

        var suppressed = App.Main?.IsShown ?? false;
        var copy = rect;
        OnUi(() => Hud.Follow(copy, suppressed));
    }

    private static bool IsLiveTarget(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && !IsIconic(hwnd);

    /// <summary>悬浮窗的创建与绘制必须回到有消息循环的 UI 线程，否则点击/重绘消息不会被处理</summary>
    private static void OnUi(Action action)
    {
        var queue = App.Main?.DispatcherQueue;
        if (queue is null) return;
        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool LeftDown()
    {
        try { return (GetAsyncKeyState(0x01) & 0x8000) != 0; }
        catch { return false; }
    }

    private sealed class Captured
    {
        public required byte[] Bmp;      // 预处理后的 32 位 BMP（供 OCR）
        public required byte[] Pixels;   // 原始 BGRA 像素（供按行采样背景色，自下而上）
    }

    private sealed record TextLine(string Text, int X, int Y, int W, int H);

    private sealed record OverlayLine(string Text, int X, int Y, int W, int H, uint Bg, uint Fg);

    private static Captured? Capture(RECT rect, int w, int h)
    {
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hbmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);
            hbmp = CreateCompatibleBitmap(screenDc, w, h);
            old = SelectObject(memDc, hbmp);
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, SRCCOPY)) return null;

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = h, // 正数 = 自下而上，正好是 BMP 的存储顺序
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };
            var pixels = new byte[w * h * 4];
            if (GetDIBits(memDc, hbmp, 0, (uint)h, pixels, ref info, 0) == 0) return null;

            // OCR 用的副本做暗底反相/灰度拉伸，保留原始像素用于覆盖层取色
            var processed = (byte[])pixels.Clone();
            Preprocess(processed);

            // 包成 32 位 BMP，交给 BitmapDecoder 解码
            var bmp = new byte[54 + processed.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            WriteInt(bmp, 2, bmp.Length);
            WriteInt(bmp, 10, 54);
            WriteInt(bmp, 14, 40);
            WriteInt(bmp, 18, w);
            WriteInt(bmp, 22, h);
            bmp[26] = 1;
            bmp[28] = 32;
            WriteInt(bmp, 34, processed.Length);
            System.Buffer.BlockCopy(processed, 0, bmp, 54, processed.Length);
            return new Captured { Bmp = bmp, Pixels = pixels };
        }
        catch { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// OCR 预处理（就地修改 BGRA 像素）：系统 OCR 对"深底浅字"识别率很低，先取出现次数最多的亮度作为背景色，
    /// 背景偏暗时反相成"浅底深字"；再把背景固定到白端、文字压到黑端，并转灰度去掉彩色干扰。
    /// </summary>
    private static void Preprocess(byte[] pixels)
    {
        var total = pixels.Length / 4;
        if (total <= 0) return;

        var hist = new int[256];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
            hist[luma]++;
        }

        var bg = 0;
        for (var v = 1; v < 256; v++)
            if (hist[v] > hist[bg]) bg = v;

        var lo = 0;
        var hi = 255;
        for (var v = 0; v < 256; v++) if (hist[v] > 0) { lo = v; break; }
        for (var v = 255; v >= 0; v--) if (hist[v] > 0) { hi = v; break; }

        var dark = bg < 110;                                  // 深底浅字
        var span = dark ? hi - bg : bg - lo;                   // 背景到文字的亮度距离
        if (span < 30) return;                                 // 接近纯色：拉伸只会放大噪点

        var lut = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var mapped = dark ? 255 - (v - bg) * 255 / span : (v - lo) * 255 / span;
            lut[v] = (byte)(mapped < 0 ? 0 : mapped > 255 ? 255 : mapped);
        }

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
            var g = lut[luma];
            pixels[i] = g;
            pixels[i + 1] = g;
            pixels[i + 2] = g;
        }
    }

    private static void WriteInt(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static async Task<List<TextLine>> OcrAsync(Captured cap, int w, int h, string source)
    {
        var engines = GetEngines(source);
        if (engines.Count == 0) return [];

        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(cap.Bmp);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // 小窗口按 2 倍放大再识别：系统 OCR 对 12px 以下的文字几乎识别不出来；
        // 4K 这类大窗口本身字够大，放大反而拖慢
        var scale = w * h <= 3_000_000 ? 2u : 1u;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)w * scale,
            ScaledHeight = (uint)h * scale,
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        // 屏幕截图的 alpha 通道无意义，必须用 Ignore：按预乘 alpha 解码会把像素当成全透明
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        var best = new List<TextLine>();
        var bestScore = -1;
        var bestText = "";
        foreach (var (engine, chinese) in engines)
        {
            var result = await engine.RecognizeAsync(bitmap);
            // 按"该引擎对应文字的数量"打分：中文引擎读英文界面会夹带大量空格和误识别汉字，
            // 直接比长度会让它永远胜出，必须按中文字数 / 英文字母数分别计分
            var score = Score(result.Text, chinese);
            if (score > bestScore || (score == bestScore && result.Text.Length > bestText.Length))
            {
                bestScore = score;
                bestText = result.Text;
                best = BuildLines(result, scale, w, h);
            }
        }
        return best;
    }

    /// <summary>把 OCR 的每行文字与其包围盒换算回截图原始像素坐标（放大过的要除以 scale）</summary>
    private static List<TextLine> BuildLines(OcrResult result, uint scale, int w, int h)
    {
        var list = new List<TextLine>();
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0) continue;

            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var word in line.Words)
            {
                var br = word.BoundingRect;
                if (br.Width <= 0 || br.Height <= 0) continue;
                l = Math.Min(l, (float)br.X);
                t = Math.Min(t, (float)br.Y);
                r = Math.Max(r, (float)(br.X + br.Width));
                b = Math.Max(b, (float)(br.Y + br.Height));
            }
            if (l == float.MaxValue) continue;

            var x = (int)Math.Floor(l / scale);
            var y = (int)Math.Floor(t / scale);
            var right = (int)Math.Ceiling(r / scale);
            var bottom = (int)Math.Ceiling(b / scale);
            x = Math.Max(0, x); y = Math.Max(0, y);
            right = Math.Min(w, right); bottom = Math.Min(h, bottom);
            if (right - x <= 4 || bottom - y <= 4) continue;

            list.Add(new TextLine(text, x, y, right - x, bottom - y));
        }
        return list;
    }

    /// <summary>目标为中文时粗判一行是否含英文：至少两个拉丁字母才值得送去翻译，纯中文/数字行直接跳过</summary>
    private static bool NeedsTranslation(string text)
    {
        var letters = 0;
        foreach (var c in text)
            if (char.IsAsciiLetter(c) && ++letters >= 2) return true;
        return false;
    }

    /// <summary>统计识别结果里属于该引擎语言的字符数（中文数汉字，英文数拉丁字母）</summary>
    private static int Score(string text, bool chinese)
    {
        var n = 0;
        foreach (var c in text)
        {
            if (chinese ? c is >= '\u4E00' and <= '\u9FFF' : char.IsAsciiLetter(c)) n++;
        }
        return n;
    }

    private static OcrEngine? _zhEngine, _enEngine, _jaEngine;

    /// <summary>
    /// 按源语言取 OCR 引擎（第二个值表示是否中文引擎）。"自动"时中英文引擎各识别一遍、取分数更高的一份：
    /// 只选一个时，中文引擎读英文界面、或英文引擎读中文界面都会大量漏字。
    /// </summary>
    private static List<(OcrEngine Engine, bool Chinese)> GetEngines(string source)
    {
        var list = new List<(OcrEngine, bool)>(2);
        try
        {
            switch (source)
            {
                case "zh":
                    if (Engine("zh-Hans-CN") is { } zh) list.Add((zh, true));
                    break;
                case "en":
                    if (Engine("en-US") is { } en) list.Add((en, false));
                    break;
                case "ja":
                    if (Engine("ja") is { } ja) list.Add((ja, false));
                    break;
                default:
                    if (Engine("zh-Hans-CN") is { } z) list.Add((z, true));
                    if (Engine("en-US") is { } e) list.Add((e, false));
                    break;
            }
            if (list.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback)
                list.Add((fallback, false));
        }
        catch { }
        return list;
    }

    private static OcrEngine? Engine(string tag)
    {
        try
        {
            return tag switch
            {
                "en-US" => _enEngine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")),
                "ja" => _jaEngine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja")),
                _ => _zhEngine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"))
            };
        }
        catch { return null; }
    }

    /// <summary>在原文行框（略微外扩）里取出现次数最多的颜色作为背景色，字形像素只占少数不会夺魁</summary>
    private static uint SampleBackground(byte[] pixels, int w, int h, int x, int y, int bw, int bh)
    {
        const int pad = 6;
        var x0 = Math.Max(0, x - pad);
        var y0 = Math.Max(0, y - pad);
        var x1 = Math.Min(w, x + bw + pad);
        var y1 = Math.Min(h, y + bh + pad);
        if (x1 <= x0 || y1 <= y0) return 0x00202020;

        var counts = new int[64];
        var sumR = new int[64];
        var sumG = new int[64];
        var sumB = new int[64];
        for (var yy = y0; yy < y1; yy++)
        {
            var row = (h - 1 - yy) * w; // 像素自下而上
            for (var xx = x0; xx < x1; xx++)
            {
                var i = (row + xx) * 4;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var idx = ((r >> 6) << 4) | ((g >> 6) << 2) | (b >> 6);
                counts[idx]++;
                sumR[idx] += r; sumG[idx] += g; sumB[idx] += b;
            }
        }

        var best = 0;
        for (var i = 1; i < 64; i++) if (counts[i] > counts[best]) best = i;
        if (counts[best] == 0) return 0x00202020;

        var rr = sumR[best] / counts[best];
        var gg = sumG[best] / counts[best];
        var bb = sumB[best] / counts[best];
        return (uint)(rr | (gg << 8) | (bb << 16)); // COLORREF: 0x00BBGGRR
    }

    /// <summary>按背景亮度选黑/白文字，保证覆盖层文字始终清晰</summary>
    private static uint ContrastText(uint colorref)
    {
        var r = (int)(colorref & 0xFF);
        var g = (int)((colorref >> 8) & 0xFF);
        var b = (int)((colorref >> 16) & 0xFF);
        var luma = (r * 299 + g * 587 + b * 114) / 1000;
        return luma < 140 ? 0x00FFFFFFu : 0x00000000u;
    }

    /// <summary>译文覆盖层与右上角控制面板（同一窗口类，按 hwnd 区分绘制内容）</summary>
    private static class Hud
    {
        private const string ClassName = "GameLockerTranslateHud";
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_HIDE = 0;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int LWA_COLORKEY = 0x1;
        private const int LWA_ALPHA = 0x2;
        private const uint ColorKey = 0x00FF00FF; // 品红做透明色键
        private const int DT_SINGLELINE = 0x00000020;
        private const int DT_CENTER = 0x00000001;
        private const int DT_VCENTER = 0x00000004;
        private const int DT_NOPREFIX = 0x00000800;
        private const int DT_END_ELLIPSIS = 0x00008000;
        private const int TRANSPARENT_BK = 1;

        // 右上角常驻控制面板
        private const int PanelW = 272;
        private const int PanelH = 132;
        private const int PanelMargin = 16;

        private static IntPtr _overlay, _panel;
        private static bool _registered;
        private static WndProcDelegate? _wndProc;
        private static string _status = "", _statusSub = "";
        private static List<OverlayLine> _lines = [];
        private static readonly object LineLock = new();
        private static bool _overlayVisible;
        private static bool _panelVisible;
        private static RECT _rect;
        private static int _pressed; // 1=底部按钮被按下 0=无
        private static RECT _actionRect;
        private static double _scale = 1;

        /// <summary>按面板 DPI 缩放像素值（掌机 125%/150% 缩放下字体与按钮才不会被裁切）</summary>
        private static int Scaled(int value) => (int)Math.Round(value * _scale);

        public static IntPtr OverlayHandle => _overlay;
        public static IntPtr PanelHandle => _panel;

        private static void Create()
        {
            if (_overlay != IntPtr.Zero) return;
            var instance = GetModuleHandle(null);
            if (!_registered)
            {
                _wndProc = WndProc;
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = instance,
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = ClassName
                };
                RegisterClassExW(ref wc);
                _registered = true;
            }

            var ex = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            _overlay = CreateWindowExW(ex, ClassName, "GameLocker Translate Overlay", WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            // 控制面板要能接鼠标点击：去掉 WS_EX_TRANSPARENT，改用整窗透明度
            _panel = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
                ClassName, "GameLocker Translate Panel", WS_POPUP,
                0, 0, PanelW, PanelH, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

            if (_overlay != IntPtr.Zero)
                SetLayeredWindowAttributes(_overlay, ColorKey, 255, LWA_COLORKEY);
            if (_panel != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(_panel);
                _scale = dpi > 0 ? dpi / 96.0 : 1;
                SetLayeredWindowAttributes(_panel, 0, 232, LWA_ALPHA);
            }
        }

        public static void ShowPrompt(string title, string sub)
        {
            Create();
            ShowPanel();
            SetStatus(title, sub);
        }

        /// <summary>覆盖层也是置顶窗口，重新显示会盖住面板：把它再提到最前，保证按钮始终可点</summary>
        public static void RaisePanel()
        {
            if (_panel != IntPtr.Zero && _panelVisible)
                SetWindowPos(_panel, TopMost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>右上角常驻面板：显示当前阶段提示与「停止 / 重新选取」两个按钮</summary>
        public static void ShowPanel()
        {
            Create();
            if (_panel == IntPtr.Zero) return;
            _panelVisible = true;
            SetTimer(_panel, (UIntPtr)1, 250, IntPtr.Zero);
            var pw = Scaled(PanelW);
            var ph = Scaled(PanelH);
            var margin = Scaled(PanelMargin);
            var x = Math.Max(margin, GetSystemMetrics(0) - pw - margin);
            SetWindowPos(_panel, TopMost, x, margin, pw, ph, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(_panel, IntPtr.Zero, true);
            UpdateWindow(_panel);
        }

        public static void HidePanel()
        {
            _panelVisible = false;
            _pressed = 0;
            if (_panel != IntPtr.Zero)
            {
                KillTimer(_panel, (UIntPtr)1);
                ShowWindow(_panel, SW_HIDE);
            }
        }

        /// <summary>更新面板上的状态文字（主状态 + 次说明）</summary>
        public static void SetStatus(string main, string sub = "")
        {
            _status = main;
            _statusSub = sub;
            if (_panel != IntPtr.Zero && _panelVisible)
            {
                InvalidateRect(_panel, IntPtr.Zero, true);
                UpdateWindow(_panel);
            }
        }

        /// <summary>鼠标是否落在控制面板上：选窗阶段用它排除点面板造成的误锁定</summary>
        public static bool PanelContainsCursor()
        {
            if (_panel == IntPtr.Zero || !_panelVisible) return false;
            POINT pt;
            if (!GetCursorPos(out pt)) return false;
            RECT r;
            if (!GetWindowRect(_panel, out r)) return false;
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }

        public static void ShowOverlay(List<OverlayLine> lines, RECT rect)
        {
            Create();
            if (_overlay == IntPtr.Zero) return;
            lock (LineLock) _lines = lines;
            _rect = rect;
            // 启动器正显示时先不弹（编辑页改设置会走到这里），等它收起后由看门狗补显
            if (App.Main?.IsShown == true)
            {
                _overlayVisible = false;
                return;
            }
            _overlayVisible = true;
            SetWindowPos(_overlay, TopMost, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(_overlay, IntPtr.Zero, true);
            UpdateWindow(_overlay);
            RaisePanel();
        }

        public static void HideOverlay()
        {
            lock (LineLock) _lines = [];
            _overlayVisible = false;
            if (_overlay != IntPtr.Zero) ShowWindow(_overlay, SW_HIDE);
        }

        public static void HideAll()
        {
            HideOverlay();
            HidePanel();
        }

        /// <summary>看门狗：跟随窗口移动；启动器呼出时把覆盖层藏起来（否则会盖在启动器上）</summary>
        public static void Follow(RECT rect, bool suppressed)
        {
            if (_overlay == IntPtr.Zero) return;
            if (suppressed)
            {
                if (_overlayVisible)
                {
                    ShowWindow(_overlay, SW_HIDE);
                    _overlayVisible = false;
                }
                RaisePanel();
                return;
            }
            if (!_panelVisible) ShowPanel();
            if (!_overlayVisible)
            {
                _rect = rect;
                _overlayVisible = true;
                SetWindowPos(_overlay, TopMost, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                InvalidateRect(_overlay, IntPtr.Zero, true);
                UpdateWindow(_overlay);
                RaisePanel();
                return;
            }
            if (rect.Left == _rect.Left && rect.Top == _rect.Top &&
                rect.Right == _rect.Right && rect.Bottom == _rect.Bottom) return;
            _rect = rect;
            SetWindowPos(_overlay, TopMost, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                SWP_NOACTIVATE);
            RaisePanel();
        }

        public static void Paint(IntPtr hwnd)
        {
            PAINTSTRUCT ps;
            var dc = BeginPaint(hwnd, out ps);
            RECT client;
            GetClientRect(hwnd, out client);

            if (hwnd == _panel)
            {
                PaintPanel(dc, client);
                EndPaint(hwnd, ref ps);
                return;
            }

            // 整窗先铺品红（色键透明），再在需要的区域画内容
            var brush = CreateSolidBrush(ColorKey);
            FillRect(dc, ref client, brush);
            DeleteObject(brush);

            if (hwnd == _overlay) PaintOverlay(dc, client);
            EndPaint(hwnd, ref ps);
        }

        /// <summary>右上角控制面板：标题 + 当前状态/进度 + 「开始翻译 / 停止翻译」按钮</summary>
        private static void PaintPanel(IntPtr dc, RECT client)
        {
            var w = client.Right;
            var h = client.Bottom;

            var outer = CreateSolidBrush(0x00423F4B);
            FillRect(dc, ref client, outer);
            DeleteObject(outer);
            var inner = new RECT { Left = 1, Top = 1, Right = w - 1, Bottom = h - 1 };
            var bg = CreateSolidBrush(0x001B1A20);
            FillRect(dc, ref inner, bg);
            DeleteObject(bg);

            SetBkMode(dc, TRANSPARENT_BK);

            var titleRect = new RECT { Left = Scaled(14), Top = Scaled(8), Right = w - Scaled(14), Bottom = Scaled(32) };
            SelectObject(dc, GetFont(Scaled(16)));
            SetTextColor(dc, 0x00FFFFFF);
            DrawTextW(dc, "屏幕翻译", -1, ref titleRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

            var mainRect = new RECT { Left = Scaled(14), Top = Scaled(34), Right = w - Scaled(14), Bottom = Scaled(58) };
            SelectObject(dc, GetFont(Scaled(13)));
            SetTextColor(dc, 0x00DEDEE4);
            DrawTextW(dc, _status, -1, ref mainRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);

            if (_statusSub.Length > 0)
            {
                var subRect = new RECT { Left = Scaled(14), Top = Scaled(58), Right = w - Scaled(14), Bottom = Scaled(80) };
                SelectObject(dc, GetFont(Scaled(11)));
                SetTextColor(dc, 0x009898A2);
                DrawTextW(dc, _statusSub, -1, ref subRect, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            }

            LayoutAction(w, h, out _actionRect);
            PaintButton(dc, _actionRect, IsTranslating ? "停止翻译" : "开始翻译", _pressed == 1);
        }

        private static void PaintButton(IntPtr dc, RECT rect, string text, bool pressed)
        {
            var border = CreateSolidBrush(0x00484454);
            FillRect(dc, ref rect, border);
            DeleteObject(border);
            var inner = new RECT { Left = rect.Left + 1, Top = rect.Top + 1, Right = rect.Right - 1, Bottom = rect.Bottom - 1 };
            var fill = CreateSolidBrush(pressed ? 0x00545060u : 0x00302E38u);
            FillRect(dc, ref inner, fill);
            DeleteObject(fill);

            SetTextColor(dc, pressed ? 0x00FFFFFFu : 0x00D6D6DEu);
            SelectObject(dc, GetFont(Scaled(13)));
            DrawTextW(dc, text, -1, ref inner, DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
        }

        /// <summary>按钮命中区域：整条底部按钮，按窗口实际像素（含 DPI 缩放）计算</summary>
        private static void LayoutAction(int w, int h, out RECT action)
        {
            var y = h - Scaled(44);
            var bh = Scaled(32);
            action = new RECT { Left = Scaled(12), Top = y, Right = w - Scaled(12), Bottom = y + bh };
        }

        /// <summary>面板鼠标按下：记录按下的按钮并重绘出按压效果</summary>
        public static void OnPanelMouseDown(IntPtr lParam)
        {
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            var hit = Inside(_actionRect, x, y) ? 1 : 0;
            if (hit == _pressed) return;
            _pressed = hit;
            // 捕获鼠标：按下后移到面板外再松开也能收到抬起消息，避免按钮卡在按下的样子
            if (hit != 0) SetCapture(_panel);
            InvalidateRect(_panel, IntPtr.Zero, true);
        }

        /// <summary>面板鼠标抬起：在按钮内抬起才触发动作</summary>
        public static void OnPanelMouseUp(IntPtr lParam)
        {
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            var hit = Inside(_actionRect, x, y) ? 1 : 0;
            var armed = _pressed;
            _pressed = 0;
            if (armed != 0) ReleaseCapture();
            if (_panel != IntPtr.Zero) InvalidateRect(_panel, IntPtr.Zero, true);
            if (hit == 0 || hit != armed) return;

            // 按钮在两种状态间切换：未翻译→开始翻译；翻译中→停止并恢复原状
            if (IsTranslating) StopTranslating();
            else StartTranslating();
        }

        private static bool Inside(RECT r, int x, int y) =>
            x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

        private static void PaintOverlay(IntPtr dc, RECT client)
        {
            List<OverlayLine> lines;
            lock (LineLock) lines = _lines;
            if (lines.Count == 0) return;

            SetBkMode(dc, TRANSPARENT_BK);
            foreach (var line in lines)
            {
                // 先用采样到的背景色盖住原文，再把译文写在原位
                var box = new RECT
                {
                    Left = line.X,
                    Top = line.Y,
                    Right = line.X + line.W,
                    Bottom = line.Y + line.H
                };
                var brush = CreateSolidBrush(line.Bg);
                FillRect(dc, ref box, brush);
                DeleteObject(brush);

                var size = Math.Max(10, Math.Min(72, (int)(line.H * 0.82)));
                var font = GetFont(size);
                var old = SelectObject(dc, font);
                var avail = Math.Max(8, line.W - 4);
                while (size > 10)
                {
                    SIZE extent;
                    GetTextExtentPoint32W(dc, line.Text, line.Text.Length, out extent);
                    if (extent.cx <= avail) break;
                    size -= 2;
                    font = GetFont(size);
                    SelectObject(dc, font);
                }

                SetTextColor(dc, line.Fg);
                var textRect = box;
                textRect.Left += 2;
                textRect.Right -= 2;
                DrawTextW(dc, line.Text, -1, ref textRect,
                    DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
                SelectObject(dc, old);
            }
        }
    }

    private static readonly Dictionary<int, IntPtr> FontCache = [];

    /// <summary>按像素高度缓存字体；charset 用 DEFAULT_CHARSET(1)，中/日/英都由系统字体回退处理</summary>
    private static IntPtr GetFont(int height)
    {
        height = Math.Max(8, Math.Min(96, height));
        if (FontCache.TryGetValue(height, out var cached)) return cached;
        var font = CreateFontW(-height, 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei UI");
        FontCache[height] = font;
        return font;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0113: // WM_TIMER
                if (hwnd == Hud.PanelHandle) Hud.RaisePanel();
                return IntPtr.Zero;
            case 0x000F: // WM_PAINT
                Hud.Paint(hwnd);
                return IntPtr.Zero;
            case 0x0014: // WM_ERASEBKGND
                return (IntPtr)1;
            case 0x0084: // WM_NCHITTEST
                // 控制面板必须返回 HTCLIENT(1) 才会收到鼠标消息；返回 HTNOWHERE(0) 会被当成点在空白处
                return hwnd == Hud.PanelHandle ? (IntPtr)1 : (IntPtr)(-1);
            case 0x0201: // WM_LBUTTONDOWN
                if (hwnd == Hud.PanelHandle) Hud.OnPanelMouseDown(lParam);
                return IntPtr.Zero;
            case 0x0202: // WM_LBUTTONUP
                if (hwnd == Hud.PanelHandle) Hud.OnPanelMouseUp(lParam);
                return IntPtr.Zero;
            case 0x0021: // WM_MOUSEACTIVATE
                return (IntPtr)3; // MA_NOACTIVATE：永不抢前台
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    private static IntPtr TopMost => new(-1); // HWND_TOPMOST

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint;
        public int fRestore; public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    // 注意：SetBkMode / SetTextColor / CreateSolidBrush / CreateFontW / GetTextExtentPoint32W 都由 gdi32.dll 导出，
    // 声明成 user32.dll 会在调用时抛 EntryPointNotFoundException（0x80131523），在窗口过程里直接崩进程。
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rect, int format);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int esc, int orient, int weight,
        int italic, int underline, int strike, int charset, int outPrecision, int clipPrecision,
        int quality, int pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out SIZE size);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines,
        [Out] byte[] bits, ref BITMAPINFO info, uint usage);
    private const uint SRCCOPY = 0x00CC0020;
}
