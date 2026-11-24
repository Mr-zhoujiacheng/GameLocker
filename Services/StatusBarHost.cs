using Microsoft.UI.Dispatching;

namespace GameLocker.Services;

/// <summary>
/// 顶栏2 的生命周期与占位协调（单实例，主屏；多屏归入 P2-01）。
/// 与 DockHost 相同：所有调用都在 UI 线程；开关关闭时销毁窗口并撤销 AppBar 占位。
/// </summary>
internal static class StatusBarHost
{
    private static StatusBarWindow? _window;
    private static DispatcherQueue? _dispatcher;
    private static bool _registered;
    private static int _registeredHeight;
    private static bool _shellHooked;
    private static bool _suspended;
    private static bool _fullscreenHidden;
    private static bool _force; // --statusbar-preview：忽略设置里的总开关
    private static bool _topTaskbar;
    private static bool _topModeEventHooked;
    private static AppBarHost.RECT _reserved;

    // 游戏内边缘手势：临时唤出（5 秒）或临时隐藏
    private static DispatcherQueueTimer? _peekTimer;
    private static bool _peekActive;
    private static bool _gestureHidden;

    public static bool IsVisible => _window is not null;

    /// <summary>顶栏是否已启用（窗口存在）。</summary>
    public static bool IsEnabled => _window is not null;

    /// <summary>顶栏贴在屏幕顶部（任务栏在底部时贴底）。</summary>
    public static bool IsAtTop => !_topTaskbar || TopTaskbarMode.Edge != TaskbarEdge.Bottom;

    /// <summary>
    /// 游戏内从边缘滑出顶栏：即使正在全屏隐藏也先显示，<paramref name="durationMs"/> 毫秒后。
    /// 再次自动隐藏（游戏里退出全屏仍由 OnFullscreenChanged 恢复）。
    /// </summary>
    public static void Peek(int durationMs = 5000)
    {
        if (_window is null) return;
        _gestureHidden = false;
        _peekActive = true;
        Relayout();
        _window.SetVisible(true);

        if (_dispatcher is null) return;
        _peekTimer ??= _dispatcher.CreateTimer();
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, durationMs));
        _peekTimer.IsRepeating = false;
        _peekTimer.Tick -= OnPeekTick;
        _peekTimer.Tick += OnPeekTick;
        _peekTimer.Start();
        DebugLog.Write($"[顶栏2] 手势滑出顶栏，{durationMs}ms 后恢复隐藏");
    }

    private static void OnPeekTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_peekActive) return;
        _peekActive = false;
        _gestureHidden = true;
        _window?.SetVisible(false);
        DebugLog.Write("[顶栏2] 手势唤出结束，重新隐藏");
    }

    /// <summary>游戏内反向滑动：立刻收起顶栏（直到退出全屏或改设置才恢复）。</summary>
    public static void HideByGesture()
    {
        if (_window is null) return;
        _peekTimer?.Stop();
        _peekActive = false;
        _gestureHidden = true;
        _window.SetVisible(false);
        DebugLog.Write("[顶栏2] 手势反滑，收起顶栏");
    }

    /// <summary>恢复正常显示（退出全屏/改设置时调用）。</summary>
    private static void ResetGestureVisibility()
    {
        _peekActive = false;
        _gestureHidden = false;
        _peekTimer?.Stop();
    }

    private static bool _gameMode;

    /// <summary>当前是否处在"游戏里"（顶栏默认隐藏，靠手势临时滑出）。</summary>
    public static bool GameMode => _gameMode;

    /// <summary>
    /// 进出游戏：进游戏把顶栏藏起来（顶部任务栏模式下也一样，避免盖住游戏画面），
    /// 离开游戏恢复正常显示。必须在 UI 线程调用。
    /// </summary>
    public static void SetGameMode(bool inGame)
    {
        if (_gameMode == inGame) return;
        _gameMode = inGame;
        if (_window is null) return;

        if (inGame)
        {
            _peekTimer?.Stop();
            _peekActive = false;
            _gestureHidden = false;
            _window.SetVisible(false);
            DebugLog.Write("[顶栏2] 进入游戏：顶栏让路（滑出边缘可临时唤出）");
        }
        else
        {
            ResetGestureVisibility();
            _window.SetVisible(!_suspended && !_fullscreenHidden);
            DebugLog.Write("[顶栏2] 离开游戏：恢复显示");
        }
    }

    public static async Task InitializeAsync(bool force = false)
    {
        _force = force;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        if (!_topModeEventHooked)
        {
            TopTaskbarMode.StateChanged += () =>
                _dispatcher?.TryEnqueue(() => { _ = ApplyAsync(); });
            _topModeEventHooked = true;
        }

        await ApplyAsync();
    }

    /// <summary>按设置创建/更新/销毁顶栏。设置页拨开关后调用。</summary>
    public static async Task ApplyAsync()
    {
        var settings = SettingsService.Current;
        ResetGestureVisibility();

        if (!settings.StatusBarEnabled && !_force)
        {
            TopTaskbarMode.Disable();
            _topTaskbar = false;
            CloseWindow();
            return;
        }

        // 系统项：始终接管任务栏所在的上/下边缘，隐藏任务栏并用顶栏填满预留条
        if (TopTaskbarMode.TryEnable(out var message))
        {
            _topTaskbar = true;
        }
        else
        {
            _topTaskbar = false;
            if (!string.IsNullOrWhiteSpace(message))
                FloatingToastService.Show(message, warning: true);
        }

        // 关闭「全屏自动隐藏」后，即使之前因全屏被藏起来也要立即恢复
        if (!settings.StatusBarFullscreenHide) _fullscreenHidden = false;

        if (_window is null)
        {
            var window = new StatusBarWindow();
            window.LayoutInvalidated = () =>
            {
                ReRegister();
                Relayout();
            };
            window.FullscreenChanged = OnFullscreenChanged;
            _window = window;

            // 只有真正创建顶栏时才启动 Explorer 重启监听，未启用时不占线程
            if (!_shellHooked)
            {
                ShellRestartWatcher.TaskbarRecreated += OnTaskbarRecreated;
                ShellRestartWatcher.Start();
                _shellHooked = true;
            }
        }

        await _window.ApplySettingsAsync();
        if (_topTaskbar) _window.SetTopTaskbarHeight(TopTaskbarMode.DisplayHeightDip);
        EnsureRegistration();
        Relayout();
        _window.SetVisible(!_suspended && !_fullscreenHidden && !_gestureHidden && !_gameMode);

        // 顶栏跟随任务栏边缘后，底栏的折叠/展开位置也要重新计算
        DockHost.Relayout();
    }

    /// <summary>重新计算并摆放窗口；占位模式下使用系统回填的矩形。</summary>
    public static void Relayout()
    {
        if (_window is null) return;

        var height = _window.PreferredHeight;
        var monitor = Monitors.Primary();

        if (_topTaskbar)
        {
            // 跟着任务栏：任务栏在顶部贴顶边，在底部贴底边，并填满它隐藏后留下的预留条
            var top = TopTaskbarMode.Edge == TaskbarEdge.Bottom
                ? monitor.Bounds.Bottom - height
                : monitor.Bounds.Top;
            _window.MoveTo(monitor.Bounds.Left, top,
                monitor.Bounds.Right - monitor.Bounds.Left, height);
            return;
        }

        var rect = _registered
            ? _reserved
            : AppBarHost.EdgeRect(_window.Hwnd, AppBarHost.EdgeTop, height, monitor.Handle);

        _window.MoveTo(rect.Left, rect.Top, rect.Right - rect.Left, height);
    }

    /// <summary>下载模式等全屏遮罩期间挂起：隐藏窗口并撤销占位，恢复时重新注册。</summary>
    public static void Suspend(bool suspended)
    {
        _suspended = suspended;
        if (_window is null) return;

        if (suspended)
        {
            RemoveAppBar();
            _window.SetVisible(false);
        }
        else if (!_fullscreenHidden)
        {
            ReRegister();
            Relayout();
            _window.SetVisible(true);
        }
    }

    public static void Shutdown()
    {
        if (_shellHooked)
        {
            ShellRestartWatcher.TaskbarRecreated -= OnTaskbarRecreated;
            _shellHooked = false;
        }
        TopTaskbarMode.Disable();
        _topTaskbar = false;
        CloseWindow();
        _force = false;
    }

    private static void CloseWindow()
    {
        if (_window is null) return;
        try
        {
            RemoveAppBar();
            _window.Close();
        }
        catch (Exception ex)
        {
            DebugLog.Write("[顶栏2] 关闭窗口失败：" + ex.Message);
        }
        finally
        {
            _window = null;
            _fullscreenHidden = false;
            _suspended = false;
        }
    }

    private static void ReRegister()
    {
        if (_window is null) return;

        RemoveAppBar();

        if (_topTaskbar) return;

        var settings = SettingsService.Current;
        if (!settings.StatusBarReserveSpace || _suspended || _fullscreenHidden) return;

        try
        {
            var monitor = Monitors.Primary();
            _registered = AppBarHost.Register(
                _window.Hwnd, AppBarHost.EdgeTop, _window.PreferredHeight, monitor.Handle, out _reserved);
            if (_registered) _registeredHeight = _window.PreferredHeight;
            if (!_registered)
                DebugLog.Write("[顶栏2] AppBar 占位失败，回退为浮层");
            else
                DebugLog.Write($"[顶栏2] AppBar 占位成功 h={_window.PreferredHeight} " +
                               $"rect={_reserved.Left},{_reserved.Top},{_reserved.Right},{_reserved.Bottom}");
        }
        catch (Exception ex)
        {
            _registered = false;
            DebugLog.Write("[顶栏2] AppBar 占位异常：" + ex.Message);
        }
    }

    /// <summary>只在占位开关或高度真的变化时才重注册，避免改个信息段就闪一下工作区。</summary>
    private static void EnsureRegistration()
    {
        if (_window is null) return;

        var settings = SettingsService.Current;
        if (_topTaskbar)
        {
            RemoveAppBar();
            return;
        }

        if (!settings.StatusBarReserveSpace || _suspended || _fullscreenHidden)
        {
            RemoveAppBar();
            return;
        }

        var height = _window.PreferredHeight;
        if (_registered && _registeredHeight == height) return;
        ReRegister();
    }

    private static void RemoveAppBar()
    {
        if (!_registered || _window is null) return;
        try { AppBarHost.Remove(_window.Hwnd); }
        catch (Exception ex) { DebugLog.Write("[顶栏2] 撤销 AppBar 失败：" + ex.Message); }
        finally { _registered = false; _registeredHeight = 0; }
    }

    private static void OnFullscreenChanged(bool hidden)
    {
        // 顶部任务栏模式下顶栏就是那条预留区，全屏时也保持显示（任务栏占位仍在）
        if (_topTaskbar)
        {
            _fullscreenHidden = false;
            // 退出全屏时把手势造成的临时隐藏收回，避免顶栏一直不回来
            if (!hidden && _window is not null)
            {
                ResetGestureVisibility();
                if (!_suspended && !_gameMode) _window.SetVisible(true);
            }
            return;
        }

        _fullscreenHidden = hidden;
        if (_window is null) return;

        if (hidden)
        {
            RemoveAppBar();
            _window.SetVisible(false);
            DebugLog.Write("[顶栏2] 前台全屏，暂时隐藏");
            return;
        }

        if (_suspended) return;
        ResetGestureVisibility();   // 退出全屏：手势的临时显隐一并复位
        ReRegister();
        Relayout();
        _window.SetVisible(!_gameMode);
        DebugLog.Write("[顶栏2] 退出全屏，恢复显示");
    }

    private static void OnTaskbarRecreated()
    {
        _dispatcher?.TryEnqueue(() =>
        {
            if (_window is null) return;
            ReRegister();
            Relayout();
            _window.SetVisible(!_suspended && !_fullscreenHidden && !_gestureHidden && !_gameMode);
        });
    }
}
