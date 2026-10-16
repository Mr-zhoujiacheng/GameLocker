using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>
/// 手柄映射服务：把「已开启」的手柄映射磁贴登记在内存（会话级，不写盘），
/// 订阅 GamepadService.ButtonDown（不经 @RoutingEnabled 门控），命中源手柄键即按目标类型派发——
/// 键盘/鼠标经 SendInput 注入前台程序（启动器隐藏到托盘、游戏在前台时同样生效）；
/// 手柄仅在本启动器前台时内部派发（Windows 无法向其它进程注入 XInput）。
/// 交互：A/点击=开关（Toggle），长按=关闭（Disable）。
/// </summary>
public static class GamepadRemapService
{
    private static readonly object Gate = new();

    /// <summary>已开启映射的磁贴（键=Target；会话级，退出启动器即失效）</summary>
    private static readonly Dictionary<string, LaunchItem> Enabled = [];

    private static bool _subscribed;

    /// <summary>开关状态变化（供磁贴刷新"映射开/关"显示；在 UI 线程触发）</summary>
    public static event Action? StateChanged;

    /// <summary>订阅手柄按下事件（进程内仅需一次；由 MainWindow 启动时调用）</summary>
    public static void Init()
    {
        if (_subscribed) return;
        _subscribed = true;
        GamepadService.ButtonDown += OnButtonDown;
    }

    public static bool IsEnabled(LaunchItem item)
    {
        lock (Gate) return Enabled.ContainsKey(item.Target);
    }

    /// <summary>A/点击：开/关切换</summary>
    public static void Toggle(LaunchItem item)
    {
        lock (Gate)
        {
            if (!Enabled.Remove(item.Target)) Enabled[item.Target] = item;
        }
        StateChanged?.Invoke();
    }

    /// <summary>开启映射（已开启则刷新登记的磁贴引用）</summary>
    public static void Enable(LaunchItem item)
    {
        lock (Gate) Enabled[item.Target] = item;
        StateChanged?.Invoke();
    }

    /// <summary>关闭映射（长按）</summary>
    public static void Disable(LaunchItem item)
    {
        bool removed;
        lock (Gate) removed = Enabled.Remove(item.Target);
        if (removed) StateChanged?.Invoke();
    }

    /// <summary>磁贴重建/编辑保存后刷新登记的磁贴引用（编辑映射后立即生效，且保持开关状态）</summary>
    public static void Refresh(LaunchItem item)
    {
        lock (Gate)
        {
            if (Enabled.ContainsKey(item.Target)) Enabled[item.Target] = item;
        }
    }

    /// <summary>当前已开启映射的磁贴数量</summary>
    public static int EnabledCount
    {
        get { lock (Gate) return Enabled.Count; }
    }

    /// <summary>手柄按下（轮询线程）→ 命中已开启映射的源按键即派发目标</summary>
    private static void OnButtonDown(GamepadButtons pressed)
    {
        List<GamepadMapping> hits = [];
        lock (Gate)
        {
            foreach (var item in Enabled.Values)
                foreach (var m in item.GamepadMappings)
                    if (m.Source != GamepadButtons.None && (pressed & m.Source) == m.Source)
                        hits.Add(m);
        }
        if (hits.Count == 0) return;

        // 启动器在前台时：注入键盘/鼠标会打到启动器自己，跳过；手柄目标仅此时才有意义
        var launcherForeground = App.Main?.IsLauncherForeground == true;
        foreach (var m in hits)
        {
            switch (m.TargetKind)
            {
                case MappingTargetKind.Key:
                    if (!launcherForeground && m.TargetKey != 0)
                        KeySendService.Send(m.TargetKey, m.TargetMods);
                    break;
                case MappingTargetKind.Mouse:
                    if (!launcherForeground && m.TargetMouse != 0)
                        KeySendService.SendMouse(m.TargetMouse);
                    break;
                case MappingTargetKind.Pad:
                    if (launcherForeground && m.TargetPad != GamepadButtons.None)
                        App.Main?.DispatchGamepad(m.TargetPad);
                    break;
            }
        }
    }
}
