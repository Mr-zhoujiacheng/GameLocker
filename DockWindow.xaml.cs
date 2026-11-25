// 说明：本文件由 GameLocker.dll 反编译恢复（此前一次批量替换把它按错误编码重写过，
// 原注释无法还原）。行为与修复前构建出来的二进制一致；后续新增改动请照常写注释。
#pragma warning disable CS4014, CS8600, CS8602, CS8622
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using WinRT.GameLockerVtableClasses;
using WinRT.Interop;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace GameLocker;

public sealed partial class DockWindow : Window
{
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private sealed record DockSlot(LaunchItem Item, nint Window, bool Dynamic = false, bool Start = false);

	private struct POINT
	{
		public int X;

		public int Y;
	}

	private const int SidePadding = 14;

	private const int ItemSpacing = 12;
	/// <summary>分隔线两侧额外让出的总宽度（DIP）：左右图标各多 5 DIP。</summary>
	private const int DividerExtra = 10;

	private const int VerticalPadding = 6;

	private int _sidePadding = 14;
	/// <summary>分隔条放在哪个槽位之后（开始=0，所有小组件紧随其后）。</summary>
	private int _dividerAfter = -1;

	private const double WindowCornerRadius = 8.0;

	private int _iconSize = 48;

	private int _barHeight = 72;

	private int _itemCount;

	private readonly List<DockSlot> _slots = new List<DockSlot>();

	private const int PinnedSlots = 12;

	private const int DynamicSlots = 4;

	private const int MaxSlots = 16;

	private bool _windowPreview;

	private bool _folderExpand = true;

	private int _arcAngle = 90;

	private int _hoverIndex = -1;

	/// <summary>预览卡片是否由手柄 Y / 触摸长按固定显示（鼠标移开也不收起，B / 再按 Y / 点击别处收起）。</summary>
	private bool _previewPinned;

	/// <summary>固定预览对应的格子下标（-1=没有固定预览）。</summary>
	private int _previewIndex = -1;

	private CancellationTokenSource? _previewCts;

	private int _magnifyIndex = -1;

	private bool _magnify = true;

	private bool _showRunning = true;

	private int _indicatorStyle = 1;

	private int _indicatorSize = 5;

	private const int IndicatorGap = 1;

	private int _indicatorExtra;

	private Color _indicatorColor = Color.FromArgb(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

	private bool _pressFeedback = true;

	private string _iconPlateColor = "#14FFFFFF";

	private string _startLogoColor = "#4CC2FF";

	private const string StartTarget = "@start";

	private const int VK_LWIN = 91;

	private const uint KEYEVENTF_KEYUP = 2u;

	private double _lastScale = 1.0;

	/// <summary>内容排版/窗口尺寸用的缩放；只在 UI 线程写，钩子线程只读（x64 上 double 读写是原子的）。</summary>
	private double _scale = 1.0;

	private bool _autoHide;

	private DispatcherTimer? _watchTimer;

	private DispatcherTimer? _slideTimer;
	private long _slideStartMs;
	private int _slideFromY, _slideToY, _slideX, _slideW, _slideH;

	private readonly List<(string Target, nint Window, FrameworkElement Dot)> _indicators = new List<(string, nint, FrameworkElement)>();

	private List<AppWindow> _lastWindows = new List<AppWindow>();

	private Dictionary<int, (int Parent, string Name)> _processTree = new Dictionary<int, (int, string)>();

	private long _processTreeAt;

	private readonly Dictionary<string, DateTime> _appMissingSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private readonly List<string> _dynamicOrder = new List<string>();

	private readonly Dictionary<string, DateTime> _indicatorOffSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, DateTime> _indicatorForcedUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private int _staticSlotCount = 1;

	private HashSet<string> _knownExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private HashSet<string> _knownFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private HashSet<string> _trayRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private DateTime _lastTrayCheck;

	private DispatcherTimer? _scanTimer;

	private bool _rebuilding;

	private bool _rebuildPending;

	private bool _scanning;

	private long _fastScanUntil;

	private DateTime? _leaveAt;

	private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250.0);

	private static readonly TimeSpan FastScanInterval = TimeSpan.FromMilliseconds(120.0);

	private static readonly TimeSpan FastScanWindow = TimeSpan.FromSeconds(6.0);

	private static readonly TimeSpan IndicatorHold = TimeSpan.FromSeconds(1.0);

	private static readonly TimeSpan RunningAppHold = TimeSpan.FromSeconds(1.0);

	/// <summary>steamwebhelper 窗口归到 steam.exe 时用；路径只查一次。</summary>
	private static string? _steamExePath;

	/// <summary>
	/// Steam 的界面窗口（SDL_app「Steam」）由 steamwebhelper.exe 创建，
	/// steam.exe 本身没有顶层窗口；如果按窗口 exe 名归类，它会被 <see cref="ShellProcesses"/>
	/// 当成系统宿主过滤掉，底栏就永远看不到 Steam。这里把这类窗口还原成 steam.exe 的身份，
	/// 图标/标题/点击激活（按进程树找窗口）都按 Steam 走；其余 helper 行为不变。
	/// </summary>
	private static AppWindow NormalizeRunningWindow(AppWindow window)
	{
		if (!string.Equals(window.ExeName, "steamwebhelper", StringComparison.OrdinalIgnoreCase)) return window;
		var steam = _steamExePath ??= AppWindows.ExePathOfProcess("steam");
		if (string.IsNullOrEmpty(steam)) return window;
		return window with { ExeName = "steam", ExePath = steam };
	}

	public const int RevealDip = 6;

	private int _x;

	private int _y;

	private int _w;

	private int _h;

	private static readonly HashSet<string> ShellProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"ApplicationFrameHost", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "Widgets", "WidgetService", "LockApp", "steamwebhelper",
		"GameBar", "GameBarFTServer", "XboxGameBarWidgets", "PhoneExperienceHost"
	};

	private static readonly HashSet<string> NonIdentityAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"explorer", "svchost", "services", "wininit", "winlogon", "userinit", "sihost", "taskhostw", "RuntimeBroker", "dllhost",
		"conhost", "ApplicationFrameHost", "cmd", "powershell", "pwsh", "WindowsTerminal", "wscript", "cscript", "OpenConsole"
	};

	private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private int _pressIndex = -1;

	private int _pressScreenX;

	/// <summary>按下时的手指/鼠标 Y：纯竖直拖动（往底栏外甩）也要算"拖拽"，否则拖不出去。</summary>
	private int _pressScreenY;

	private bool _dragging;

	private int _dragTarget = -1;

	private FrameworkElement? _pressVisual;

	private DockSlot? _pressSlot;

	/// <summary>鼠标按住组件图标 550 ms = 删除组件（和触摸长按同一语义）。</summary>
	private DispatcherQueueTimer? _widgetHoldTimer;
	private int _widgetHoldIndex = -1;

	/// <summary>手柄当前选中的格子（-1=没有选中）；鼠标/触摸一动就清掉，避免两种光标同时存在。</summary>
	private int _padIndex = -1;

	/// <summary>正在画选中框的图标底盘（Grid；开始格是根节点，其余格是外层 Grid 的第一个子节点）。</summary>
	private Grid? _padRing;

	/// <summary>手柄选中的外发光圈（浮在图标底盘上，重置时一并移除）。</summary>
	private Border? _padHalo;

	/// <summary>强调色（跟随深/浅主题），用于手柄选中圈。</summary>
	private Color _accentColor = Color.FromArgb(byte.MaxValue, 76, 194, 255);

	private const int DragThreshold = 8;

	private DispatcherQueue? _dispatcher;

	private const int GWL_STYLE = -16;

	private const int GWL_EXSTYLE = -20;

	private const long WS_CAPTION = 12582912L;

	private const long WS_THICKFRAME = 262144L;

	private const long WS_MINIMIZEBOX = 131072L;

	private const long WS_MAXIMIZEBOX = 65536L;

	private const long WS_SYSMENU = 524288L;

	private const long WS_POPUP = 2147483648L;

	private const long WS_EX_TOOLWINDOW = 128L;

	private const long WS_EX_NOACTIVATE = 134217728L;

	private const uint SWP_NOSIZE = 1u;

	private const uint SWP_NOMOVE = 2u;

	private const uint SWP_NOZORDER = 4u;

	private const uint SWP_NOACTIVATE = 16u;

	private const uint SWP_FRAMECHANGED = 32u;

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

	private const int DWMWCP_ROUND = 2;

	private const int SW_SHOWNOACTIVATE = 4;

	private const int SW_HIDE = 0;

	private static readonly nint HWND_TOPMOST = new IntPtr(-1);

	public Action? LayoutInvalidated { get; set; }

	public bool AutoHide
	{
		get
		{
			return _autoHide;
		}
		set
		{
			_autoHide = value;
			if (value)
			{
				SetCollapsed(collapsed: true);
				StartWatch();
			}
			else
			{
				StopWatch();
				SetCollapsed(collapsed: false);
			}
		}
	}

	public bool IsCollapsed { get; private set; }

	public Action? CollapsedChanged { get; set; }

	/// <summary>把自动隐藏收起的底栏亮出来（手柄呼出组合键用）。已经展开时无副作用。</summary>
	public void Reveal()
	{
		if (IsCollapsed)
		{
			SetCollapsed(collapsed: false);
		}
		// 底栏是 NOACTIVATE 置顶浮层，重申一次置顶把它提到最上面
		SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0,
			SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
	}

	/// <summary>收起底栏（游戏内手势下滑隐藏用）。</summary>
	public void Collapse() => SetCollapsed(collapsed: true);

	public nint Hwnd => WindowNative.GetWindowHandle(this);

	/// <summary>当前格子数（含最左侧「开始」格）；底栏宽度就按它推出来。</summary>
	public int ItemCount => _itemCount;

	public double Scale
	{
		get
		{
			// 只读缓存值：这个属性会在全局鼠标钩子线程里被调用（命中计算），
			// 绝不能直接碰 Root/XamlRoot —— WinUI 对象有线程亲和性，跨线程访问会抛异常，
			// 异常会从钩子回调里冒出去把钩子线程打死，表现为底栏从此悬停、点击全都没反应。
			// 缓存由 UI 线程在 <see cref="RefreshScale"/> 里刷新。
			double num = _scale;
			if (!(num > 0.0))
			{
				return 1.0;
			}
			return num;
		}
	}

	/// <summary>
	/// 在 UI 线程刷新缩放：优先取 XAML 实际栅格化比例（窗口尺寸必须和内容排版比例一致），
	/// 拿不到再退回窗口 DPI。只允许 UI 线程调用。
	/// </summary>
	private void RefreshScale()
	{
		double num = (object)Root != null && Root.XamlRoot != null ? Root.XamlRoot.RasterizationScale : 0.0;
		if (!(num > 0.0))
		{
			num = (double)GetDpiForWindow(Hwnd) / 96.0;
		}
		if (!(num > 0.0))
		{
			num = 1.0;
		}
		_scale = num;
	}

	public int PreferredWidth
	{
		get
		{
			int num = _sidePadding * 2 + _itemCount * _iconSize + Math.Max(0, _itemCount - 1) * ItemSpacing
				+ (_dividerAfter >= 0 && _itemCount > _dividerAfter + 1 ? DividerExtra : 0);
			if (_itemCount == 0)
			{
				num = 200;
			}
			return (int)Math.Round((double)num * Scale);
		}
	}

	public int PreferredHeight => (int)Math.Round((double)_barHeight * Scale);

	public DockWindow()
	{
		InitializeComponent();
		base.Title = "Game Locker Dock";
		ApplyChrome();
		RefreshScale();
		_dispatcher = DispatcherQueue.GetForCurrentThread();
		HookMouse();
		HookDrop();
		Root.PointerEntered += delegate
		{
			_leaveAt = null;
			if (IsCollapsed)
			{
				SetCollapsed(collapsed: false);
			}
		};
		UIElement content = base.Content;
		FrameworkElement root = content as FrameworkElement;
		if ((object)root == null)
		{
			return;
		}
		root.Loaded += delegate
		{
			RefreshScale();
			if ((object)root.XamlRoot != null)
			{
				root.XamlRoot.Changed += delegate
				{
					RefreshScale();
					double scale = Scale;
					if (!(Math.Abs(scale - _lastScale) < 0.01))
					{
						_lastScale = scale;
						LayoutInvalidated?.Invoke();
					}
				};
			}
		};
	}

	public void MoveTo(int x, int y, int width, int height)
	{
		StopSlide();
		ApplyMove(x, y, width, height);
	}

	/// <summary>带轻快滑入/滑出动画的移动，仅用于自动隐藏的收起/呼出。</summary>
	public void MoveToAnimated(int x, int y, int width, int height)
	{
		RefreshScale();
		if (!_autoHide || Math.Abs(y - _y) < 2)
		{
			MoveTo(x, y, width, height);
			return;
		}

		_slideX = x;
		_slideW = width;
		_slideH = height;
		_slideFromY = _y;
		_slideToY = y;
		_slideStartMs = Environment.TickCount64;

		_slideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
		_slideTimer.Tick -= OnSlideTick;
		_slideTimer.Tick += OnSlideTick;
		_slideTimer.Start();
	}

	private void OnSlideTick(object? sender, object e)
	{
		const double duration = 170.0;
		var t = Math.Min(1.0, (Environment.TickCount64 - _slideStartMs) / duration);
		var eased = 1 - Math.Pow(1 - t, 3);
		var y = _slideFromY + (int)Math.Round((_slideToY - _slideFromY) * eased);
		ApplyMove(_slideX, y, _slideW, _slideH);
		if (t >= 1.0) StopSlide();
	}

	private void StopSlide()
	{
		_slideTimer?.Stop();
	}

	private void ApplyMove(int x, int y, int width, int height)
	{
		bool flag = width != _w || height != _h;
		_x = x;
		_y = y;
		_w = width;
		_h = height;
		SetWindowPos(Hwnd, HWND_TOPMOST, x, y, width, height, 16u);
		if (flag)
		{
			DebugLog.Write($"[底栏] 尺寸变化：{width}x{height}（图标 {_itemCount} 个，缩放 {Scale:0.##}）");
		}
	}

	public void SetVisible(bool visible)
	{
		if (!visible)
		{
			_hoverIndex = -1;
			HidePreview();
			if (_magnifyIndex != -1)
			{
				UpdateMagnify(-1);
			}
		}
		if (visible)
		{
			StartScanWatch();
		}
		else
		{
			_scanTimer?.Stop();
		}
		ShowWindow(Hwnd, visible ? 4 : 0);
	}

	public async Task ApplySettingsAsync()
	{
		AppSettings current = SettingsService.Current;
		RefreshScale();
		ApplyTheme(DockThemes.Get(current.DockTheme));
		_iconSize = Math.Clamp(current.DockIconSize, 36, 64);
		_magnify = current.DockMagnify;
		_showRunning = current.DockShowRunning;
		_indicatorStyle = (current.DockShowRunning ? Math.Clamp(current.DockIndicatorStyle, 0, 2) : 0);
		_indicatorSize = Math.Clamp(current.DockIndicatorSize, 3, 16);
		_indicatorExtra = AppSettings.DockIndicatorExtra(_showRunning, _indicatorStyle, _indicatorSize);
		_barHeight = AppSettings.DockBarHeight(_iconSize, _indicatorExtra);
		_sidePadding = 14 + (int)Math.Ceiling((double)_iconSize * 0.17999999999999994 / 2.0);
		_indicatorColor = ParseColor(current.DockIndicatorColor);
		_pressFeedback = current.DockPressFeedback;
		_windowPreview = current.DockWindowPreview;
		_folderExpand = current.DockFolderExpand;
		_arcAngle = Math.Clamp(current.DockArcAngle, 0, 180);
		Bar.CornerRadius = new CornerRadius(8.0);
		// 上边多留 3 DIP、下边收 1 DIP：条顶抬高一点，图标略下沉，底部不再显厚
		ItemsPanel.Margin = new Thickness(_sidePadding, 6.0, _sidePadding, 2.0);
		await RebuildAsync();
		StartScanWatch();
	}

	private void ApplyTheme(DockTheme theme)
	{
		Root.RequestedTheme = (theme.Light ? ElementTheme.Light : ElementTheme.Dark);
		_iconPlateColor = theme.IconPlate;
		_startLogoColor = (theme.Light ? "#0078D4" : "#4CC2FF");
		_accentColor = ParseColor(_startLogoColor);
		byte lineR = (byte)(theme.Light ? 0 : 255);
		byte lineG = (byte)(theme.Light ? 0 : 255);
		byte lineB = (byte)(theme.Light ? 0 : 255);
		GroupDivider.Background = new LinearGradientBrush
		{
			StartPoint = new Point(0.5, 0),
			EndPoint = new Point(0.5, 1),
			GradientStops = new GradientStopCollection
			{
				new GradientStop { Color = Color.FromArgb(0, lineR, lineG, lineB), Offset = 0 },
				new GradientStop { Color = Color.FromArgb(0x66, lineR, lineG, lineB), Offset = 0.5 },
				new GradientStop { Color = Color.FromArgb(0, lineR, lineG, lineB), Offset = 1 }
			}
		};
		if (string.IsNullOrEmpty(theme.Bar2))
		{
			Bar.Background = new SolidColorBrush(ParseColor(theme.Bar));
		}
		else
		{
			Bar.Background = new LinearGradientBrush
			{
				StartPoint = new Point(0f, 0f),
				EndPoint = new Point(1f, 1f),
				GradientStops = 
				{
					new GradientStop
					{
						Color = ParseColor(theme.Bar),
						Offset = 0.0
					},
					new GradientStop
					{
						Color = ParseColor(theme.Bar2),
						Offset = 1.0
					}
				}
			};
		}
		Bar.BorderBrush = null;
		Bar.BorderThickness = new Thickness(0.0);
		Color color = ParseColor(theme.Highlight);
		Sheen.Background = new LinearGradientBrush
		{
			StartPoint = new Point(0.5, 0.0),
			EndPoint = new Point(0.5, 1.0),
			GradientStops = 
			{
				new GradientStop
				{
					Color = color,
					Offset = 0.0
				},
				new GradientStop
				{
					Color = Color.FromArgb(0, color.R, color.G, color.B),
					Offset = 0.22
				},
				new GradientStop
				{
					Color = Color.FromArgb(0, 0, 0, 0),
					Offset = 0.78
				},
				new GradientStop
				{
					Color = Color.FromArgb(20, 0, 0, 0),
					Offset = 1.0
				}
			}
		};
	}

	private static Color ParseColor(string hex)
	{
		try
		{
			string text = hex.TrimStart('#');
			return text.Length switch
			{
				6 => Color.FromArgb(byte.MaxValue, Convert.ToByte(text.Substring(0, 2), 16), Convert.ToByte(text.Substring(2, 2), 16), Convert.ToByte(text.Substring(4, 2), 16)), 
				8 => Color.FromArgb(Convert.ToByte(text.Substring(0, 2), 16), Convert.ToByte(text.Substring(2, 2), 16), Convert.ToByte(text.Substring(4, 2), 16), Convert.ToByte(text.Substring(6, 2), 16)), 
				_ => Color.FromArgb(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue), 
			};
		}
		catch
		{
		}
		return Color.FromArgb(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);
	}

	private static bool IsDockItem(LaunchItem item)
	{
		if (item.DockHidden)
		{
			return false;
		}
		if (item.Kind == LaunchKind.Group)
		{
			return item.Members.Count > 0;
		}
		// 只有从底栏"小组件"入口加入的组件才进底栏（LauncherHidden），最多 5 个
		if (item.Kind == LaunchKind.Widget)
		{
			return item.LauncherHidden && !string.IsNullOrWhiteSpace(item.Target);
		}
		LaunchKind kind = item.Kind;
		if ((uint)kind > 2u)
		{
			return false;
		}
		return !string.IsNullOrWhiteSpace(item.Target);
	}

	private async Task RebuildAsync()
	{
		if (_rebuilding)
		{
			_rebuildPending = true;
			return;
		}
		_rebuilding = true;
		try
		{
			await RebuildCoreAsync();
		}
		finally
		{
			_rebuilding = false;
			if (_rebuildPending)
			{
				_rebuildPending = false;
				await RebuildAsync();
			}
		}
		// 格子数变了窗口宽度也要跟着变：重建完成后再通知宿主摆位。
		// 不能在重建开始/中途通知——那时 _itemCount 还是 0，宿主会按最小宽度摆，
		// 图标就会被挤在窄条里（新增条目走 ReapplyLayoutAsync 的 30ms 延时也常常早于重建结束）。
		LayoutInvalidated?.Invoke();
	}

	private async Task RebuildCoreAsync()
	{
		_hoverIndex = -1;
		HidePreview();
		ItemsPanel.Children.Clear();
		_itemCount = 0;
		_slots.Clear();
		_magnifyIndex = -1;
		_padRing = null;
		_padIndex = -1;
		_indicators.Clear();
		_indicatorOffSince.Clear();
		_appMissingSince.Clear();
		List<LaunchItem> source;
		try
		{
			source = ConfigService.Load();
		}
		catch
		{
			source = new List<LaunchItem>();
		}
		// 开始 + 底栏小组件 是第一组；其它应用/网址/文件夹/分组 是第二组，中间画一条分组线。
		// 小组件始终紧跟在开始后面，不跟随 items.json 里的原始顺序。
		List<LaunchItem> dockItems = source.Where(IsDockItem).ToList();
		List<LaunchItem> widgetItems = dockItems.Where(x => x.Kind == LaunchKind.Widget).Take(5).ToList();
		List<LaunchItem> otherItems = dockItems.Where(x => x.Kind != LaunchKind.Widget)
			.Take(Math.Max(0, 16 - widgetItems.Count)).ToList();
		List<LaunchItem> list = widgetItems.Concat(otherItems).ToList();
		_dividerAfter = widgetItems.Count;   // 开始 + 全部小组件为第一组
		List<TrayAppEntry> trayEntries = (from x in TrayAppService.Snapshot()
			where x.Running
			select x.Entry).ToList();
		HashSet<string> knownExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> knownFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (LaunchItem item6 in list)
		{
			string text = AppWindows.ProcessNameOf(item6.Target);
			if (!string.IsNullOrEmpty(text))
			{
				knownExes.Add(text);
			}
			string text2 = AppWindows.PackageFamilyOfAppId(AppWindows.AppIdOfTarget(item6.Target));
			if (text2.Length > 0)
			{
				knownFamilies.Add(text2);
			}
		}
		foreach (TrayAppEntry item7 in trayEntries)
		{
			knownExes.Add(Path.GetFileNameWithoutExtension(item7.ExePath));
		}
		_knownExes = knownExes;
		_knownFamilies = knownFamilies;
		_trayRunning = new HashSet<string>(trayEntries.Select((TrayAppEntry e) => e.ExePath), StringComparer.OrdinalIgnoreCase);
		List<AppWindow> runningApps = (from g in (from w in AppWindows.Enumerate(allowUntitled: true).Select(NormalizeRunningWindow)
				where !string.IsNullOrEmpty(w.ExeName) && !ShellProcesses.Contains(w.ExeName) && !knownExes.Contains(w.ExeName) && (w.AppId.Length <= 0 || !knownFamilies.Contains(AppWindows.PackageFamilyOfAppId(w.AppId)))
				select w).GroupBy<AppWindow, string>((AppWindow w) => w.ExeName, StringComparer.OrdinalIgnoreCase)
			select g.First()).OrderBy(delegate(AppWindow w)
		{
			int num = _dynamicOrder.FindIndex((string x) => string.Equals(x, KeyOf(w), StringComparison.OrdinalIgnoreCase));
			return (num >= 0) ? num : int.MaxValue;
		}).Take(4).ToList();
		_dynamicOrder.RemoveAll((string key) => !runningApps.Any((AppWindow w) => string.Equals(KeyOf(w), key, StringComparison.OrdinalIgnoreCase)));
		foreach (AppWindow item8 in runningApps)
		{
			string text3 = KeyOf(item8);
			if (!_dynamicOrder.Contains<string>(text3, StringComparer.OrdinalIgnoreCase))
			{
				_dynamicOrder.Add(text3);
			}
		}
		LaunchItem item = new LaunchItem
		{
			Name = "开始",
			Target = "@start",
			Kind = LaunchKind.App
		};
		_slots.Add(new DockSlot(item, IntPtr.Zero, Dynamic: false, Start: true));
		ItemsPanel.Children.Add(CreateStartSlot());
		int index = 1;
		foreach (LaunchItem item2 in list)
		{
			if (index >= 18)
			{
				break;
			}
			var (item3, frameworkElement) = await CreateSlotAsync(item2, index, IntPtr.Zero);
			_slots.Add(new DockSlot(item2, IntPtr.Zero));
			if ((object)frameworkElement != null)
			{
				_indicators.Add((item2.Target, IntPtr.Zero, frameworkElement));
			}
			ItemsPanel.Children.Add(item3);
			index++;
		}
		foreach (TrayAppEntry item9 in trayEntries)
		{
			if (index < 18)
			{
				LaunchItem item2 = new LaunchItem
				{
					Name = (string.IsNullOrWhiteSpace(item9.DisplayName) ? Path.GetFileNameWithoutExtension(item9.ExePath) : item9.DisplayName),
					Target = item9.ExePath,
					Kind = LaunchKind.Tray
				};
				nint window = TrayAppService.WindowOf(item9);
				var (item4, frameworkElement2) = await CreateSlotAsync(item2, index, window);
				_slots.Add(new DockSlot(item2, window));
				if ((object)frameworkElement2 != null)
				{
					_indicators.Add((item2.Target, window, frameworkElement2));
				}
				ItemsPanel.Children.Add(item4);
				index++;
				continue;
			}
			break;
		}
		_staticSlotCount = _slots.Count;
		foreach (AppWindow item10 in runningApps)
		{
			if (index < 18)
			{
				string target = KeyOf(item10);
				LaunchItem item2 = new LaunchItem
				{
					Name = ((item10.Hosted && !string.IsNullOrWhiteSpace(item10.Title)) ? item10.Title : Path.GetFileNameWithoutExtension(item10.ExeName)),
					Target = target,
					Kind = LaunchKind.App
				};
				nint window = ((item10.Hosted || string.IsNullOrEmpty(item10.ExePath)) ? item10.Handle : IntPtr.Zero);
				string iconKey = (item10.Hosted ? IconExtractService.ShellIconKeyOf(item10.AppId) : "");
				var (item5, frameworkElement3) = await CreateSlotAsync(item2, index, window, iconKey);
				_slots.Add(new DockSlot(item2, window, Dynamic: true));
				if ((object)frameworkElement3 != null)
				{
					_indicators.Add((item2.Target, window, frameworkElement3));
				}
				ItemsPanel.Children.Add(item5);
				index++;
				continue;
			}
			break;
		}
		_itemCount = _slots.Count;
		UpdateGroupDivider();
	}

	/// <summary>分隔条摆到"开始 + 小组件"最后一格与其它应用第一格之间的缝隙中。</summary>
	private void UpdateGroupDivider()
	{
		// 先清掉上一次加在相邻图标上的额外间距
		foreach (var child in ItemsPanel.Children)
			if (child is FrameworkElement element) element.Margin = new Thickness(0);

		if (_dividerAfter < 0 || _dividerAfter + 1 >= _slots.Count || _itemCount <= 1)
		{
			GroupDivider.Visibility = Visibility.Collapsed;
			return;
		}

		int last = _dividerAfter;
		int first = last + 1;
		if (last < ItemsPanel.Children.Count && ItemsPanel.Children[last] is FrameworkElement left)
			left.Margin = new Thickness(0, 0, DividerExtra / 2.0, 0);
		if (first < ItemsPanel.Children.Count && ItemsPanel.Children[first] is FrameworkElement right)
			right.Margin = new Thickness(DividerExtra / 2.0, 0, 0, 0);

		double x = _sidePadding + last * (_iconSize + ItemSpacing) + _iconSize + (ItemSpacing + DividerExtra) / 2.0;
		GroupDivider.Margin = new Thickness(x - 1, 0, 0, 0);
		GroupDivider.Visibility = Visibility.Visible;
	}

	private double DividerExtraFor(int index) =>
		_dividerAfter >= 0 && index > _dividerAfter && _dividerAfter + 1 < _slots.Count ? DividerExtra : 0;

	private int AnchorXFor(RECT rect, int index) =>
		rect.Left + (int)((_sidePadding + index * (_iconSize + ItemSpacing) + DividerExtraFor(index) + _iconSize / 2.0) * Scale);

	private FrameworkElement CreateStartSlot()
	{
		Grid grid = new Grid
		{
			Width = _iconSize,
			Height = _iconSize,
			Background = new SolidColorBrush(ParseColor(_iconPlateColor)),
			CornerRadius = new CornerRadius(10.0),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top
		};
		Grid grid2 = new Grid
		{
			Width = (double)_iconSize * 0.56,
			Height = (double)_iconSize * 0.56,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			RowSpacing = Math.Max(1.0, (double)_iconSize * 0.045),
			ColumnSpacing = Math.Max(1.0, (double)_iconSize * 0.045)
		};
		grid2.RowDefinitions.Add(new RowDefinition());
		grid2.RowDefinitions.Add(new RowDefinition());
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		SolidColorBrush background = new SolidColorBrush(ParseColor(_startLogoColor));
		for (int i = 0; i < 2; i++)
		{
			for (int j = 0; j < 2; j++)
			{
				Border border = new Border
				{
					Background = background,
					CornerRadius = new CornerRadius(1.5)
				};
				Grid.SetRow(border, i);
				Grid.SetColumn(border, j);
				grid2.Children.Add(border);
			}
		}
		grid.Children.Add(grid2);
		Grid obj = new Grid
		{
			Width = _iconSize,
			Height = _iconSize + _indicatorExtra,
			Background = new SolidColorBrush(Colors.Transparent),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Children = { (UIElement)grid }
		};
		ToolTipService.SetToolTip(obj, new ToolTip
		{
			Content = "开始"
		});
		return obj;
	}

	private void OpenStartMenu()
	{
		try
		{
			if (SettingsService.Current.StartMenuMode)
			{
				App.Main?.ToggleStartMenu();
				DebugLog.Write("[底栏] 开始：切换到 GameLocker");
			}
			else
			{
				keybd_event(91, 0, 0u, UIntPtr.Zero);
				keybd_event(91, 0, 2u, UIntPtr.Zero);
				DebugLog.Write("[底栏] 开始：打开系统开始菜单");
			}
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 打开开始菜单失败：" + ex.Message);
		}
	}

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

	/// <summary>小组件彩绘底盘的主色：每种组件一个鲜明颜色，避免设置/抽屉这类灰色强调色发灰。</summary>
	private static Color WidgetAccent(WidgetKind kind) => kind switch
	{
		WidgetKind.Music => Color.FromArgb(255, 0xFF, 0x6B, 0x9D),
		WidgetKind.Photos => Color.FromArgb(255, 0xFF, 0xB7, 0x4D),
		WidgetKind.Weather => Color.FromArgb(255, 0x4F, 0xC3, 0xF7),
		WidgetKind.Mapping => Color.FromArgb(255, 0xB3, 0x88, 0xFF),
		WidgetKind.Trainer => Color.FromArgb(255, 0xFF, 0x8A, 0x65),
		WidgetKind.Backup => Color.FromArgb(255, 0x7B, 0xE4, 0x95),
		WidgetKind.GamepadMap => Color.FromArgb(255, 0x4D, 0xD0, 0xE1),
		WidgetKind.OptiScaler => Color.FromArgb(255, 0xCE, 0x93, 0xD8),
		WidgetKind.Drawer => Color.FromArgb(255, 0x9F, 0xA8, 0xDA),
		WidgetKind.RecycleBin => Color.FromArgb(255, 0x64, 0xB5, 0xF6),
		WidgetKind.Foreground => Color.FromArgb(255, 0x5C, 0xE1, 0xE6),
		WidgetKind.System => Color.FromArgb(255, 0x82, 0xB1, 0xFF),
		WidgetKind.Performance => Color.FromArgb(255, 0x9C, 0xCC, 0x65),
		WidgetKind.Power => Color.FromArgb(255, 0xFF, 0x6E, 0x6E),
		WidgetKind.Monitor => Color.FromArgb(255, 0x4D, 0xD0, 0xE1),
		WidgetKind.DownloadMode => Color.FromArgb(255, 0xFF, 0xB7, 0x4D),
		WidgetKind.FileTransfer => Color.FromArgb(255, 0x7B, 0xE4, 0x95),
		WidgetKind.LosslessScaling => Color.FromArgb(255, 0xFF, 0xD5, 0x4F),
		WidgetKind.Keyboard => Color.FromArgb(255, 0xB0, 0xBE, 0xC5),
		WidgetKind.Clash => Color.FromArgb(255, 0xFF, 0x8A, 0x65),
		WidgetKind.Wallpaper => Color.FromArgb(255, 0xF4, 0x8F, 0xB1),
		WidgetKind.ScreenTranslate => Color.FromArgb(255, 0x64, 0xB5, 0xF6),
		WidgetKind.Settings => Color.FromArgb(255, 0x4C, 0xC2, 0xFF),
		_ => Color.FromArgb(255, 0x90, 0xCA, 0xF9),
	};

	/// <summary>底栏小组件图标字形（Segoe MDL2 Assets），按组件类型区分。</summary>
	private static string WidgetGlyph(WidgetKind kind) => kind switch
	{
		WidgetKind.Music => "\uE8D6",
		WidgetKind.Photos => "\uE91B",
		WidgetKind.Weather => "\uE706",
		WidgetKind.Mapping => "\uE765",
		WidgetKind.Trainer => "\uE90F",
		WidgetKind.Backup => "\uE74E",
		WidgetKind.GamepadMap => "\uE7FC",
		WidgetKind.OptiScaler => "\uE8C8",
		WidgetKind.Drawer => "\uE8F1",
		WidgetKind.RecycleBin => "\uE74D",
		WidgetKind.Foreground => "\uE7C4",
		WidgetKind.System => "\uE770",
		WidgetKind.Performance => "\uE945",
		WidgetKind.Power => "\uE7E8",
		WidgetKind.Monitor => "\uE7F4",
		WidgetKind.DownloadMode => "\uE896",
		WidgetKind.FileTransfer => "\uE8B7",
		WidgetKind.LosslessScaling => "\uE768",
		WidgetKind.Keyboard => "\uE765",
		WidgetKind.Clash => "\uE774",
		WidgetKind.Wallpaper => "\uE91B",
		WidgetKind.ScreenTranslate => "\uE8C1",
		WidgetKind.Settings => "\uE713",
		_ => "\uE9CE",
	};

	private async Task<(FrameworkElement Element, FrameworkElement? Dot)> CreateSlotAsync(LaunchItem item, int index, nint window, string iconKey = "")
	{
		Grid iconGrid = new Grid
		{
			Width = _iconSize,
			Height = _iconSize,
			Background = new SolidColorBrush(ParseColor(_iconPlateColor)),
			CornerRadius = new CornerRadius(10.0)
		};
		if (item.Kind == LaunchKind.Widget && item.WidgetKind == WidgetKind.LosslessScaling
			&& LosslessScalingService.UsesOptiScaler(item))
		{
			iconGrid.Children.Add(TileStyle.OptiScalerIcon(_iconSize * 0.56));
		}
		else if (item.Kind == LaunchKind.Widget)
		{
			// 和开始图标同一风格：同样的深色圆角底盘，中间只放一个纯色图形。
			// 不用整块渐变彩盘，避免比软件图标更抢眼。
			iconGrid.Children.Add(new FontIcon
			{
				Glyph = WidgetGlyph(item.WidgetKind),
				FontSize = (double)_iconSize * 0.56,
				Foreground = new SolidColorBrush(WidgetAccent(item.WidgetKind)),
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			});
		}
		else
		{
			ImageSource imageSource = await LoadIconAsync(item, iconKey);
			if ((object)imageSource != null)
			{
				iconGrid.Children.Add(new Image
				{
					// 和开始图标的四格一样留出同样的底盘边距：统一 56% 大小
					Width = (double)_iconSize * 0.56,
					Height = (double)_iconSize * 0.56,
					Stretch = Stretch.Uniform,
					Source = imageSource
				});
			}
			else
			{
				string glyph = ((item.Kind == LaunchKind.Url) ? "\ue774" : ((item.Kind == LaunchKind.Folder) ? "\ue8b7" : ((item.Kind == LaunchKind.Group) ? "\ue71d" : "\ue9ce")));
				iconGrid.Children.Add(new FontIcon
				{
					Glyph = glyph,
					FontSize = (double)_iconSize * 0.52,
					Foreground = new SolidColorBrush(Color.FromArgb(230, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				});
			}
		}
		Grid grid = new Grid
		{
			Width = _iconSize,
			Height = _iconSize + _indicatorExtra,
			Background = new SolidColorBrush(Colors.Transparent),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top
		};
		iconGrid.HorizontalAlignment = HorizontalAlignment.Center;
		iconGrid.VerticalAlignment = VerticalAlignment.Top;
		grid.Children.Add(iconGrid);
		FrameworkElement item2 = null;
		if (_indicatorStyle > 0)
		{
			FrameworkElement frameworkElement = BuildIndicator();
			frameworkElement.HorizontalAlignment = HorizontalAlignment.Center;
			frameworkElement.VerticalAlignment = VerticalAlignment.Top;
			frameworkElement.Margin = new Thickness(0.0, _iconSize + IndicatorGap, 0.0, 0.0);
			frameworkElement.Visibility = Visibility.Collapsed;
			grid.Children.Add(frameworkElement);
			item2 = frameworkElement;
		}
		if (!_windowPreview)
		{
			string text = ((window != IntPtr.Zero) ? WindowTitleOf(window) : item.Name);
			if (string.IsNullOrWhiteSpace(text))
			{
				text = item.Name;
			}
			ToolTipService.SetToolTip(grid, new ToolTip
			{
				Content = text
			});
		}
		return (Element: grid, Dot: item2);
	}

	private async Task<ImageSource?> LoadIconAsync(LaunchItem item, string iconKey = "")
	{
		string key = ((iconKey.Length > 0) ? iconKey : ((item.Target.Length == 0) ? item.Name : item.Target));
		if (IconCache.TryGetValue(key, out ImageSource value))
		{
			return value;
		}
		try
		{
			bool isShellItem = key.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);
			string path = (isShellItem
				? IconExtractService.CachedShellIconPath(key)
				: IconExtractService.CachedIconPath(key));
			if (path == null)
			{
				// 原生/商店应用：包内 exe 不带真实图标，只有 Shell 的 AppsFolder 项才是设置/计算器那张图
				string text = (isShellItem
					? await IconExtractService.ExtractShellItemAsync(key)
					: await IconExtractService.ExtractAsync(key));
				if (text != null)
				{
					path = Path.Combine(LaunchItem.IconsDir, text);
				}
			}
			if (path == null)
			{
				return null;
			}
			BitmapImage bitmapImage = new BitmapImage(new Uri(path));
			IconCache[key] = bitmapImage;
			return bitmapImage;
		}
		catch
		{
			return null;
		}
	}

	private void StartScanWatch()
	{
		if ((object)_scanTimer == null)
		{
			_scanTimer = new DispatcherTimer
			{
				Interval = ScanInterval
			};
		}
		if (!_scanTimer.IsEnabled)
		{
			_scanTimer.Tick += delegate
			{
				ScanTick();
			};
			_scanTimer.Start();
		}
		ScanTick();
	}

	private async void ScanTick()
	{
		if (_rebuilding || _scanning)
		{
			return;
		}
		_scanning = true;
		try
		{
			List<AppWindow> list;
			try
			{
				list = AppWindows.Enumerate(allowUntitled: true);
			}
			catch (Exception ex)
			{
				DebugLog.Write("[底栏] 运行状态扫描失败：" + ex.Message);
				return;
			}
			_lastWindows = list;
			long tickCount = Environment.TickCount64;
			if (tickCount - _processTreeAt >= 1000)
			{
				_processTree = AppWindows.ProcessTree();
				_processTreeAt = tickCount;
			}
			UpdateIndicators(list);
			await SyncDynamicSlotsAsync(list);
			await CheckTrayAppsAsync();
		}
		catch (Exception ex2)
		{
			DebugLog.Write("[底栏] 运行状态刷新失败：" + ex2.Message);
		}
		finally
		{
			_scanning = false;
			ApplyScanInterval();
		}
	}

	private void KickScan()
	{
		_fastScanUntil = Environment.TickCount64 + (long)FastScanWindow.TotalMilliseconds;
		ApplyScanInterval();
		if (!_scanning && !_rebuilding)
		{
			ScanTick();
		}
	}

	private void ApplyScanInterval()
	{
		if ((object)_scanTimer == null)
		{
			return;
		}
		TimeSpan timeSpan = ((Environment.TickCount64 < _fastScanUntil) ? FastScanInterval : ScanInterval);
		if (!(_scanTimer.Interval == timeSpan))
		{
			bool isEnabled = _scanTimer.IsEnabled;
			_scanTimer.Stop();
			_scanTimer.Interval = timeSpan;
			if (isEnabled)
			{
				_scanTimer.Start();
			}
		}
	}

	private void UpdateIndicators(List<AppWindow> windows)
	{
		if (_indicators.Count == 0 || _indicatorStyle <= 0)
		{
			return;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AppWindow window in windows)
		{
			if (!string.IsNullOrEmpty(window.ExeName))
			{
				hashSet.Add(window.ExeName);
			}
			if (window.AppId.Length > 0)
			{
				hashSet2.Add(AppWindows.PackageFamilyOfAppId(window.AppId));
			}
		}
		HashSet<string> hashSet3 = null;
		if (_processTree.Count > 0)
		{
			foreach (AppWindow window2 in windows)
			{
				if (string.IsNullOrEmpty(window2.ExeName))
				{
					continue;
				}
				int num = window2.Pid;
				for (int i = 0; i < 6; i++)
				{
					if (!_processTree.TryGetValue(num, out (int, string) value))
					{
						break;
					}
					(num, _) = value;
					if (num <= 0 || !_processTree.TryGetValue(num, out (int, string) value2) || string.IsNullOrEmpty(value2.Item2))
					{
						break;
					}
					if (!NonIdentityAncestors.Contains(value2.Item2))
					{
						(hashSet3 ?? (hashSet3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase))).Add(value2.Item2);
					}
				}
			}
		}
		DateTime utcNow = DateTime.UtcNow;
		foreach (var indicator in _indicators)
		{
			string item = indicator.Target;
			FrameworkElement item2 = indicator.Dot;
			string text = AppWindows.ProcessNameOf(item);
			string text2 = AppWindows.AppIdOfTarget(item);
			bool flag = ((text2.Length > 0) ? hashSet2.Contains(AppWindows.PackageFamilyOfAppId(text2)) : (!string.IsNullOrEmpty(text) && (hashSet.Contains(text) || (hashSet3?.Contains(text) ?? false))));
			if (!flag && _indicatorForcedUntil.TryGetValue(item, out var value3))
			{
				if (value3 > utcNow)
				{
					flag = true;
				}
				else
				{
					_indicatorForcedUntil.Remove(item);
				}
			}
			DateTime value4;
			if (flag)
			{
				_indicatorOffSince.Remove(item);
				if (item2.Visibility != 0)
				{
					item2.Visibility = Visibility.Visible;
				}
			}
			else if (!_indicatorOffSince.TryGetValue(item, out value4))
			{
				_indicatorOffSince[item] = utcNow;
			}
			else if (!(utcNow - value4 < IndicatorHold) && item2.Visibility != Visibility.Collapsed)
			{
				item2.Visibility = Visibility.Collapsed;
			}
		}
	}

	private void LightIndicator(string target)
	{
		if (_indicators.Count == 0 || _indicatorStyle <= 0)
		{
			return;
		}
		foreach (var (text, _, frameworkElement) in _indicators)
		{
			if (string.Equals(text, target, StringComparison.OrdinalIgnoreCase))
			{
				_indicatorForcedUntil[text] = DateTime.UtcNow.AddSeconds(8.0);
				_indicatorOffSince.Remove(text);
				if (frameworkElement.Visibility != 0)
				{
					frameworkElement.Visibility = Visibility.Visible;
				}
			}
		}
	}

	private async Task SyncDynamicSlotsAsync(List<AppWindow> windows)
	{
		Dictionary<string, AppWindow> byKey = new Dictionary<string, AppWindow>(StringComparer.OrdinalIgnoreCase);
		foreach (AppWindow raw in windows)
		{
			AppWindow window = NormalizeRunningWindow(raw);
			if (string.IsNullOrEmpty(window.ExeName) || ShellProcesses.Contains(window.ExeName) || _knownExes.Contains(window.ExeName) || (window.AppId.Length > 0 && _knownFamilies.Contains(AppWindows.PackageFamilyOfAppId(window.AppId))))
			{
				continue;
			}
			string text = KeyOf(window);
			if (text.Length != 0)
			{
				if (!_dynamicOrder.Contains<string>(text, StringComparer.OrdinalIgnoreCase))
				{
					_dynamicOrder.Add(text);
				}
				byKey.TryAdd(text, window);
			}
		}
		List<string> ordered = byKey.Keys.OrderBy((string key) => _dynamicOrder.FindIndex((string x) => string.Equals(x, key, StringComparison.OrdinalIgnoreCase))).ToList();
		int count = Math.Clamp(16 - _staticSlotCount, 0, 4);
		HashSet<string> desired = new HashSet<string>(ordered.Take(count), StringComparer.OrdinalIgnoreCase);
		DateTime now = DateTime.UtcNow;
		foreach (DockSlot item in _slots.Where((DockSlot s) => s.Dynamic).ToList())
		{
			string key2 = KeyOf(item.Item);
			if (key2.Length == 0)
			{
				continue;
			}
			DateTime value;
			if (desired.Contains(key2))
			{
				_appMissingSince.Remove(key2);
			}
			else if (!_appMissingSince.TryGetValue(key2, out value))
			{
				_appMissingSince[key2] = now;
			}
			else if (!(now - value < RunningAppHold))
			{
				_appMissingSince.Remove(key2);
				_dynamicOrder.RemoveAll((string x) => string.Equals(x, key2, StringComparison.OrdinalIgnoreCase));
				await RemoveDynamicSlotAsync(key2);
			}
		}
		foreach (string item2 in ordered)
		{
			if (desired.Contains(item2) && IndexOfDynamicSlot(item2) < 0)
			{
				if (_slots.Count >= 18)
				{
					break;
				}
				await AppendDynamicSlotAsync(byKey[item2]);
			}
		}
	}

	private async Task AppendDynamicSlotAsync(AppWindow app)
	{
		string target = KeyOf(app);
		LaunchItem item = new LaunchItem
		{
			Name = ((app.Hosted && !string.IsNullOrWhiteSpace(app.Title)) ? app.Title : Path.GetFileNameWithoutExtension(app.ExeName)),
			Target = target,
			Kind = LaunchKind.App
		};
		nint window = ((app.Hosted || string.IsNullOrEmpty(app.ExePath)) ? app.Handle : IntPtr.Zero);
		string iconKey = (app.Hosted ? IconExtractService.ShellIconKeyOf(app.AppId) : "");
		var (item2, frameworkElement) = await CreateSlotAsync(item, _slots.Count, window, iconKey);
		if (!_rebuilding && _slots.Count < 16 && IndexOfDynamicSlot(target) < 0)
		{
			_slots.Add(new DockSlot(item, window, Dynamic: true));
			if ((object)frameworkElement != null)
			{
				_indicators.Add((item.Target, window, frameworkElement));
			}
			ItemsPanel.Children.Add(item2);
			_itemCount = _slots.Count;
			AnimateSlotsIn(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target });
			UpdateIndicators(_lastWindows);
			LayoutInvalidated?.Invoke();
			DebugLog.Write("[底栏] 新增运行图标：" + item.Name);
		}
	}

	private async Task RemoveDynamicSlotAsync(string key)
	{
		int num = IndexOfDynamicSlot(key);
		if (num < 0)
		{
			return;
		}
		if (num < ItemsPanel.Children.Count && ItemsPanel.Children[num] is FrameworkElement element)
		{
			DebugLog.Write("[底栏] 移除运行图标：" + Path.GetFileName(key));
			AnimateSlot(element, appearing: false);
			await Task.Delay(150);
		}
		num = IndexOfDynamicSlot(key);
		if (num >= 0)
		{
			string target = _slots[num].Item.Target;
			_slots.RemoveAt(num);
			if (num < ItemsPanel.Children.Count)
			{
				ItemsPanel.Children.RemoveAt(num);
			}
			_indicators.RemoveAll(((string Target, nint Window, FrameworkElement Dot) i) => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase));
			_indicatorOffSince.Remove(target);
			_indicatorForcedUntil.Remove(target);
			_itemCount = _slots.Count;
			LayoutInvalidated?.Invoke();
		}
	}

	private int IndexOfDynamicSlot(string key)
	{
		return _slots.FindIndex((DockSlot s) => s.Dynamic && string.Equals(KeyOf(s.Item), key, StringComparison.OrdinalIgnoreCase));
	}

	private static string KeyOf(AppWindow window)
	{
		if (!string.IsNullOrEmpty(window.ExePath))
		{
			return window.ExePath;
		}
		return window.ExeName;
	}

	private static string KeyOf(LaunchItem item)
	{
		if (item.Target.Length != 0)
		{
			return item.Target;
		}
		return item.Name;
	}

	private async Task CheckTrayAppsAsync()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow - _lastTrayCheck < TimeSpan.FromSeconds(1.5))
		{
			return;
		}
		_lastTrayCheck = utcNow;
		HashSet<string> hashSet;
		try
		{
			hashSet = (from x in TrayAppService.Snapshot()
				where x.Running
				select x.Entry.ExePath).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		catch
		{
			return;
		}
		if (!hashSet.SetEquals(_trayRunning))
		{
			_trayRunning = hashSet;
			await RebuildAsync();
			UpdateIndicators(_lastWindows);
			LayoutInvalidated?.Invoke();
		}
	}

	private FrameworkElement? SlotElementOf(string key)
	{
		for (int i = 0; i < _slots.Count && i < ItemsPanel.Children.Count; i++)
		{
			DockSlot dockSlot = _slots[i];
			if (!dockSlot.Start && string.Equals((dockSlot.Item.Target.Length == 0) ? dockSlot.Item.Name : dockSlot.Item.Target, key, StringComparison.OrdinalIgnoreCase))
			{
				return ItemsPanel.Children[i] as FrameworkElement;
			}
		}
		return null;
	}

	private void AnimateSlotsIn(HashSet<string> keys)
	{
		foreach (string key in keys)
		{
			FrameworkElement element = SlotElementOf(key);
			if ((object)element != null)
			{
				DebugLog.Write("[底栏] 图标淡入：" + Path.GetFileName(key));
				if (element.IsLoaded)
				{
					AnimateSlot(element, appearing: true);
				}
				else
				{
					element.Loaded += OnLoaded;
				}
			}
			void OnLoaded(object sender, RoutedEventArgs args)
			{
				element.Loaded -= OnLoaded;
				AnimateSlot(element, appearing: true);
			}
		}
	}

	private static void AnimateSlot(FrameworkElement element, bool appearing)
	{
		try
		{
			Visual visual = ElementCompositionPreview.GetElementVisual(element);
			Compositor compositor = visual.Compositor;
			float num = ((element.Width > 0.0) ? ((float)element.Width) : ((float)element.ActualWidth));
			float num2 = ((element.Height > 0.0) ? ((float)element.Height) : ((float)element.ActualHeight));
			if (num > 0f && num2 > 0f)
			{
				visual.CenterPoint = new Vector3(num / 2f, num2 / 2f, 0f);
			}
			Vector3KeyFrameAnimation vector3KeyFrameAnimation = compositor.CreateVector3KeyFrameAnimation();
			ScalarKeyFrameAnimation scalarKeyFrameAnimation = compositor.CreateScalarKeyFrameAnimation();
			if (appearing)
			{
				vector3KeyFrameAnimation.InsertKeyFrame(0f, new Vector3(0.5f, 0.5f, 1f));
				vector3KeyFrameAnimation.InsertKeyFrame(0.7f, new Vector3(1.05f, 1.05f, 1f));
				vector3KeyFrameAnimation.InsertKeyFrame(1f, Vector3.One);
				scalarKeyFrameAnimation.InsertKeyFrame(0f, 0f);
				scalarKeyFrameAnimation.InsertKeyFrame(1f, 1f);
				vector3KeyFrameAnimation.Duration = TimeSpan.FromMilliseconds(300.0);
				scalarKeyFrameAnimation.Duration = TimeSpan.FromMilliseconds(180.0);
			}
			else
			{
				vector3KeyFrameAnimation.InsertKeyFrame(0f, Vector3.One);
				vector3KeyFrameAnimation.InsertKeyFrame(1f, new Vector3(0.35f, 0.35f, 1f));
				scalarKeyFrameAnimation.InsertKeyFrame(0f, 1f);
				scalarKeyFrameAnimation.InsertKeyFrame(1f, 0f);
				vector3KeyFrameAnimation.Duration = TimeSpan.FromMilliseconds(160.0);
				scalarKeyFrameAnimation.Duration = TimeSpan.FromMilliseconds(150.0);
			}
			visual.StartAnimation("Scale", vector3KeyFrameAnimation);
			visual.StartAnimation("Opacity", scalarKeyFrameAnimation);
			DispatcherQueue dispatcher = element.DispatcherQueue;
			Task.Delay(appearing ? 340 : 190).ContinueWith((Task _) => dispatcher.TryEnqueue(delegate
			{
				try
				{
					visual.StopAnimation("Scale");
					visual.StopAnimation("Opacity");
				}
				catch
				{
				}
			}));
		}
		catch
		{
		}
	}

	private FrameworkElement BuildIndicator()
	{
		SolidColorBrush background = new SolidColorBrush(_indicatorColor);
		if (_indicatorStyle == 2)
		{
			return new Border
			{
				Width = Math.Max(6.0, (double)_iconSize * 0.34),
				Height = Math.Max(2.0, (double)_indicatorSize / 2.0),
				CornerRadius = new CornerRadius(1.0),
				Background = background
			};
		}
		return new Border
		{
			Width = _indicatorSize,
			Height = _indicatorSize,
			CornerRadius = new CornerRadius((double)_indicatorSize / 2.0),
			Background = background
		};
	}

	private void HookMouse()
	{
		DockInputHook.LeftDown = OnHookLeftDown;
		DockInputHook.LeftMove = OnHookLeftMove;
		DockInputHook.LeftUp = OnHookLeftUp;
		DockInputHook.Hover = OnHookHover;
		DockInputHook.LongPress = OnHookLongPress;
		// 触摸滑走时取消按下态：清掉按压缩放，别让图标一直停在"被按住"的样子
		DockInputHook.PressCancelled = OnHookPressCancelled;
		DockInputHook.Start();
	}

	/// <summary>本次按下作废（触摸滑出底栏/滑动超过抖动范围）：复位按下态与按压反馈。</summary>
	private void OnHookPressCancelled()
	{
		int pressed = _pressIndex;
		_pressIndex = -1;
		_dragging = false;
		_dragTarget = -1;
		_pressSlot = null;
		_dispatcher?.TryEnqueue(StopWidgetHold);
		_dispatcher?.TryEnqueue(delegate
		{
			SetPressVisual(pressed, pressed: false);
		});
	}

	/// <summary>收起预览卡片，并清掉"手柄/触摸固定显示"的状态。</summary>
	private void HidePreview()
	{
		_previewPinned = false;
		_previewIndex = -1;
		_hoverIndex = -1;
		_previewCts?.Cancel();
		_previewCts = null;
		DockPreviewHost.Hide();
	}

	/// <summary>
	/// 触摸长按图标（原始输入通道，钩子线程调用）：弹出该格子对应的窗口卡片，
	/// 与手柄 Y 一样固定显示；松手不会再触发一次点击启动。
	/// </summary>
	private void OnHookLongPress(int screenX, int screenY)
	{
		_dispatcher?.TryEnqueue(delegate
		{
			try
			{
				if (IsCollapsed || !GetWindowRect(Hwnd, out var rect)) return;
				if (screenX < rect.Left || screenX > rect.Right || screenY < rect.Top || screenY > rect.Bottom) return;
				var index = IndexFromClientX(screenX - rect.Left);
				if (index < 0 || index >= _slots.Count) return;
				if (_slots[index].Item.Kind == LaunchKind.Widget)
				{
					_pressIndex = -1;
					StopWidgetHold();
					DeleteDockWidget(_slots[index]);   // 长按组件 = 删除
					return;
				}
				ClearGamepadSelection();
				ShowPreviewFor(index, pinned: true);
			}
			catch (Exception ex) { DebugLog.Write("[底栏] 长按预览异常：" + ex.Message); }
		});
	}

	// ---------------- 手柄操作 ----------------
	// 底栏窗口是 NOACTIVATE 的，拿不到焦点，所以不能用常规的控件焦点导航；
	// 这里维护自己的选中格（_padIndex + 图标底盘描边），并按与鼠标相同的入口执行动作。

	/// <summary>
	/// 手柄按键（轮询线程调用，内部切到 UI 线程执行）：←/→ 或 LB/RB 选择，A 打开，Y 弹出窗口卡片，B 取消选择/收起卡片。
	/// 自动隐藏收起时，任意一个被接受的按键先把它唤出（等价鼠标悬停），本次不执行动作。
	/// </summary>
	public bool HandleGamepad(GamepadButtons buttons)
	{
		if (buttons == GamepadButtons.None || !IsWindowVisible(Hwnd)) return false;

		bool left = buttons.HasFlag(GamepadButtons.DPadLeft) || buttons.HasFlag(GamepadButtons.LeftShoulder);
		bool right = buttons.HasFlag(GamepadButtons.DPadRight) || buttons.HasFlag(GamepadButtons.RightShoulder);
		bool accept = buttons.HasFlag(GamepadButtons.A) || buttons.HasFlag(GamepadButtons.B)
			|| buttons.HasFlag(GamepadButtons.X) || buttons.HasFlag(GamepadButtons.Y) || left || right;
		if (!accept) return false;

		if (IsCollapsed)
		{
			_dispatcher?.TryEnqueue(delegate
			{
				if (CanApplyGamepadInput()) SetCollapsed(collapsed: false);
			});
			return true;
		}

		// Y / 长按弹出的卡片：手柄此时改为选卡模式，左右选卡、A 弹开/打开、B 或 Y 收起
		if (DockPreviewHost.IsVisible && _previewPinned)
		{
			_dispatcher?.TryEnqueue(delegate
			{
				if (!CanApplyGamepadInput()) return;
				if (left) DockPreviewHost.SelectRelative(-1);
				else if (right) DockPreviewHost.SelectRelative(1);
				else if (buttons.HasFlag(GamepadButtons.A)) DockPreviewHost.ActivateSelected();
				else if (buttons.HasFlag(GamepadButtons.B) || buttons.HasFlag(GamepadButtons.Y))
				{
					// 收起卡片，但选中框回到弹出卡片的那个图标上，不要连图标选中一起消失
					var returnIndex = _previewIndex;
					HidePreview();
					if (returnIndex >= 0 && returnIndex < _slots.Count)
					{
						_padIndex = returnIndex;
						RefreshPadVisual();
					}
				}
			});
			return true;
		}

		_dispatcher?.TryEnqueue(delegate
		{
			if (!CanApplyGamepadInput()) return;
			if (left) MovePadSelection(-1);
			else if (right) MovePadSelection(1);
			else if (buttons.HasFlag(GamepadButtons.A)) ActivatePadSelection();
			else if (buttons.HasFlag(GamepadButtons.X)) DeletePadSelection();
			else if (buttons.HasFlag(GamepadButtons.Y)) OpenPadPreview();
			else if (buttons.HasFlag(GamepadButtons.B))
			{
				HidePreview();
				ClearGamepadSelection();
				SetCollapsed(collapsed: true);
			}
		});
		return true;
	}

	private bool CanApplyGamepadInput() => SettingsService.Current.SystemEnabled
		&& App.Main?.OwnsPageGamepadInput != true && IsWindowVisible(Hwnd);

	/// <summary>清掉手柄选中框（鼠标/触摸接管、重建格子、收起时调用）。</summary>
	public void ClearGamepadSelection()
	{
		if (_padIndex < 0 && _padRing is null) return;
		ResetPlateRingVisual();
		_padIndex = -1;
		UpdateMagnify(-1);
	}

	private static void ResetPlateRing(Grid plate)
	{
		plate.BorderThickness = new Thickness(0);
		plate.BorderBrush = null;
	}

	/// <summary>取某个格子的图标底盘：外层格子（含指示点高度）的第一个子节点，开始格同样如此。</summary>
	private Grid? IconPlateOf(int index)
	{
		if (index < 0 || index >= ItemsPanel.Children.Count) return null;
		if (ItemsPanel.Children[index] is not FrameworkElement element) return null;
		if (element is Panel panel && panel.Children.Count > 0 && panel.Children[0] is Grid plate) return plate;
		return null;
	}

	private void MovePadSelection(int delta)
	{
		var count = _slots.Count;
		if (count == 0) return;
		HidePreview(); // 换选中的图标时，上一张卡片先收起
		_padIndex = _padIndex < 0
			? (delta < 0 ? count - 1 : 0)
			: (_padIndex + delta + count) % count;
		RefreshPadVisual();
	}

	private void RefreshPadVisual()
	{
		ResetPlateRingVisual();
		if (_padIndex < 0) return;

		var plate = IconPlateOf(_padIndex);
		if (plate is null) return;

		// 新样式：贴住图标的一圈细描边 + 外面一圈略放大的强调色光环。
		// 描边负责"贴合"，光环负责"醒目"，不加动画也不挡图标。
		var accent = _accentColor;
		plate.BorderBrush = new SolidColorBrush(Color.FromArgb(0x8C, accent.R, accent.G, accent.B));
		plate.BorderThickness = new Thickness(1);

		var halo = new Border
		{
			Width = _iconSize + 6,
			Height = _iconSize + 6,
			CornerRadius = new CornerRadius(13),
			BorderThickness = new Thickness(3),
			BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x6F, 0xD8, 0xFF)),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0, -3, 0, 0),
			IsHitTestVisible = false
		};
		plate.Children.Add(halo);
		_padHalo = halo;

		_padRing = plate;
		// 与鼠标悬停共用同一个放大通道，选中项保持"抬起"的观感
		UpdateMagnify(_padIndex);
	}

	private void ResetPlateRingVisual()
	{
		if (_padHalo is not null)
		{
			if (_padHalo.Parent is Panel haloParent) haloParent.Children.Remove(_padHalo);
			_padHalo = null;
		}
		if (_padRing is not null)
		{
			ResetPlateRing(_padRing);
			_padRing = null;
		}
	}

	/// <summary>A：没选中时先选中第一格（防误触），已选中则执行与鼠标左键相同的动作。</summary>
	private void ActivatePadSelection()
	{
		if (_slots.Count == 0) return;
		HidePreview();
		if (_padIndex < 0)
		{
			// 没选中时 A 也和鼠标点第一格一样：先选中，再立即触发
			_padIndex = 0;
			RefreshPadVisual();
		}
		if (_padIndex >= _slots.Count)
		{
			ClearGamepadSelection();
			return;
		}
		// 和鼠标按下一样：图标先缩一下再回弹，然后再执行动作
		PlayPadPressFeedback(_padIndex);
		// 与鼠标单击完全同一个入口：A 就是“点一下”
		ActivateSlot(_slots[_padIndex], _padIndex);
	}

	/// <summary>Y：弹出当前选中格的窗口卡片（一个窗口一张卡）；再次按 Y 收起。没有选中时先选中第一格。</summary>
	private void OpenPadPreview()
	{
		if (_slots.Count == 0) return;
		if (_padIndex < 0)
		{
			_padIndex = 0;
			RefreshPadVisual();
		}
		if (_padIndex >= _slots.Count) return;
		// Y 在底栏小组件上 = 删除组件（先长按删除的键位版本）
		if (_slots[_padIndex].Item.Kind == LaunchKind.Widget)
		{
			DeleteDockWidget(_slots[_padIndex]);
			return;
		}
		// 卡片还在：再按 Y 收起。卡片已被点击/× 关掉但固定标记还在：重新弹出来
		if (_previewPinned && _previewIndex == _padIndex && DockPreviewHost.IsVisible)
		{
			HidePreview();
			return;
		}
		ShowPreviewFor(_padIndex, pinned: true);
	}

	private void OnHookHover(int screenX, int screenY)
	{
		// 手柄 Y / 触摸长按弹出的卡片是"固定显示"的：鼠标移动不接管，避免刚冒出来就被移开收掉。
		// 卡片自己因点击/关闭而隐藏后，下一次鼠标移动再恢复普通悬停逻辑。
		if (_previewPinned)
		{
			if (DockPreviewHost.IsVisible) return;
			_previewPinned = false;
			_previewIndex = -1;
		}
		// 鼠标一动就交还控制权：手柄选中框不能和悬停高亮同时存在
		if (_padIndex >= 0) _dispatcher?.TryEnqueue(ClearGamepadSelection);
		RECT rect = default(RECT);
		bool flag = false;
		int num = -1;
		try
		{
			GetWindowRect(Hwnd, out rect);
			flag = !IsCollapsed && screenX >= rect.Left && screenX <= rect.Right && screenY >= rect.Top && screenY <= rect.Bottom;
			if (flag)
			{
				num = IndexFromClientX(screenX - rect.Left);
			}
		}
		catch
		{
		}
		if (num != _magnifyIndex)
		{
			int target = num;
			_dispatcher?.TryEnqueue(delegate
			{
				UpdateMagnify(target);
			});
		}
		// 开始按钮的"添加卡片"不属于窗口预览设置：即使关掉悬停预览也要能加东西。
		// 注意只在鼠标位于底栏其它图标上时跳过；离开底栏（flag=false）仍要执行收起逻辑。
		if (!_windowPreview && flag && num != 0)
		{
			return;
		}
		if (!flag)
		{
			if (_hoverIndex != -1 && (!DockPreviewHost.IsVisible || !DockPreviewHost.Contains(screenX, screenY)))
			{
				_hoverIndex = -1;
				_dispatcher?.TryEnqueue(HidePreview);
			}
		}
		else
		{
			if (num == _hoverIndex)
			{
				return;
			}
			_hoverIndex = num;
			_previewCts?.Cancel();
			_previewCts = null;
			if (num < 0 || num >= _slots.Count)
			{
				_dispatcher?.TryEnqueue(HidePreview);
				return;
			}
			CancellationToken token = (_previewCts = new CancellationTokenSource()).Token;
			int previewIndex = num;
			Task.Delay(220, token).ContinueWith(delegate(Task t)
			{
				if (!t.IsCanceled)
				{
					_dispatcher?.TryEnqueue(delegate
					{
						if (!token.IsCancellationRequested && _hoverIndex == previewIndex)
						{
							ShowPreviewFor(previewIndex);
						}
					});
				}
			}, TaskScheduler.Default);
		}
	}

	/// <summary>
	/// 弹出某个格子的窗口预览卡片：pinned=true 时由手柄 Y / 触摸长按触发，固定显示；
	/// 否则是鼠标悬停触发，鼠标移开就收起。每个窗口排成一张独立卡片。
	/// </summary>
	private void ShowPreviewFor(int index, bool pinned = false)
	{
		try
		{
			if (index < 0 || index >= _slots.Count)
			{
				if (pinned) HidePreview(); else DockPreviewHost.Hide();
				return;
			}
			DockSlot dockSlot = _slots[index];
			if (dockSlot.Start)
			{
				ShowStartAddCards(index, pinned);
				return;
			}
			List<nint> list;
			if (dockSlot.Window == IntPtr.Zero)
			{
				list = ((dockSlot.Item.Kind != 0) ? new List<nint>() : (from w in AppWindows.ForTarget(dockSlot.Item.Target)
					select w.Handle).Take(6).ToList());
			}
			else
			{
				int num = 1;
				List<nint> list2 = new List<nint>(num);
				CollectionsMarshal.SetCount(list2, num);
				Span<nint> span = CollectionsMarshal.AsSpan(list2);
				int num2 = 0;
				span[num2] = dockSlot.Window;
				num2++;
				list = list2;
			}
			if (list.Count == 0)
			{
				if (pinned) HidePreview(); else DockPreviewHost.Hide();
				return;
			}
			_previewPinned = pinned;
			_previewIndex = index;
			DebugLog.Write($"[底栏] 预览显示 idx={index} windows={list.Count} pinned={pinned}");
			GetWindowRect(Hwnd, out var rect);
			int anchorX = AnchorXFor(rect, index);
			// 卡片强调色跟抽屉卡一致：取该条目的磁贴强调色
			DockPreviewHost.Show(list, anchorX, rect.Top, rect.Top, rect.Bottom, Hwnd, Scale, TileStyle.AccentFor(dockSlot.Item), selectionMode: pinned);
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 悬停预览异常：" + ex.Message);
		}
	}

	/// <summary>开始按钮的添加手牌：自定义 / 小组件两张卡片，点哪张就走哪条添加流程。</summary>
	private void ShowStartAddCards(int index, bool pinned)
	{
		try
		{
			_previewPinned = pinned;
			_previewIndex = index;
			GetWindowRect(Hwnd, out var rect);
			int anchorX = AnchorXFor(rect, index);
			DockPreviewHost.ShowAddCards(anchorX, rect.Top, rect.Top, rect.Bottom, Hwnd, Scale, _accentColor, OnStartAddCardPicked, selectionMode: pinned);
			DebugLog.Write($"[底栏] 开始添加卡片 idx={index} pinned={pinned}");
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 开始添加卡片异常：" + ex.Message);
		}
	}

	/// <summary>点击开始按钮手牌：0=自定义（程序/文件夹/网址），1=小组件。</summary>
	private void OnStartAddCardPicked(int command)
	{
		try
		{
			HidePreview();   // 归零悬停/固定标记，下次移回开始图标还能再弹出
			if (command == 0)
			{
				AddItemPage.PendingDockAdd = true;
				App.Main?.ShowFromTray();
				App.Main?.RootNavigateAddItem();
			}
			else
			{
				var items = ConfigService.Load();
				if (items.Count(x => x.Kind == LaunchKind.Widget && x.LauncherHidden && !x.DockHidden) >= 5)
				{
					FloatingToastService.Show("底栏最多放 5 个组件，请先长按删除一个", warning: true);
					return;
				}
				WidgetPage.PendingDockAdd = true;
				App.Main?.ShowFromTray();
				App.Main?.RootNavigateWidget();
			}
			DebugLog.Write($"[底栏] 开始添加卡片选择 {command}");
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 开始添加卡片处理失败：" + ex.Message);
		}
	}

	/// <summary>删除底栏上的组件（Y / 长按）：只移除 LauncherHidden 的底栏副本，不影响主页同名组件。</summary>
	private void DeleteDockWidget(DockSlot? slot)
	{
		if (slot is null || slot.Item.Kind != LaunchKind.Widget) return;
		try
		{
			string name = slot.Item.Name;
			var items = ConfigService.Load();
			int index = items.FindIndex(x => x.Kind == LaunchKind.Widget && x.LauncherHidden
				&& string.Equals(x.Target, slot.Item.Target, StringComparison.OrdinalIgnoreCase));
			if (index >= 0)
			{
				items.RemoveAt(index);
				ConfigService.Save(items);
			}
			HidePreview();
			ClearGamepadSelection();
			RebuildAsync();
			ReapplyLayoutAsync();
			FloatingToastService.Show($"已从底栏删除组件「{name}」");
			DebugLog.Write($"[底栏] 删除组件：{name} (removed={index >= 0})");
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 删除组件失败：" + ex.Message);
		}
	}

	/// <summary>手柄 X：删除当前选中的常驻图标（「开始」和运行中的图标不参与）。</summary>
	private void DeletePadSelection()
	{
		if (_slots.Count == 0) return;
		if (_padIndex < 0)
		{
			// 还没选图标时，先把手柄光标放到第一格，避免一按 X 就删掉东西
			_padIndex = 0;
			RefreshPadVisual();
			return;
		}
		if (_padIndex >= _slots.Count) return;

		var slot = _slots[_padIndex];
		if (slot.Start)
		{
			FloatingToastService.Show("「开始」按钮不能删除", warning: true);
			return;
		}
		if (slot.Dynamic)
		{
			FloatingToastService.Show("运行中的图标会随程序自动出现/消失，不需要删除", warning: true);
			return;
		}
		PlayPadPressFeedback(_padIndex);
		RemoveResidentSlot(slot);
	}

	/// <summary>
	/// 从底栏移除一个常驻图标（手柄 X / 触屏拖出底栏）。
	/// 只影响底栏、不删除启动器里的条目：应用/网址/分组置 DockHidden（可以再添加回来），
	/// 组件移除底栏副本，托盘应用取消登记。「开始」与运行中的动态图标不参与。
	/// </summary>
	private void RemoveResidentSlot(DockSlot? slot)
	{
		if (slot is null || slot.Start || slot.Dynamic) return;
		try
		{
			var name = slot.Item.Name;
			if (slot.Item.Kind == LaunchKind.Widget)
			{
				DeleteDockWidget(slot);   // 复用组件自己的删除与提示
				return;
			}

			if (slot.Item.Kind == LaunchKind.Tray)
			{
				var entry = TrayAppService.FindEntry(slot.Item.Target);
				if (entry is null) return;
				TrayAppService.Remove(entry);
			}
			else
			{
				var items = ConfigService.Load();
				var target = slot.Item.Target ?? "";
				var source = items.FirstOrDefault(x => IsDockItem(x)
					&& x.Kind == slot.Item.Kind
					&& string.Equals(x.Target ?? "", target, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(x.Name, name, StringComparison.Ordinal));
				if (source is null) return;
				source.DockHidden = true;     // 只从底栏隐藏，启动器里仍保留
				ConfigService.Save(items);
			}

			HidePreview();
			ClearGamepadSelection();
			RebuildAsync();
			ReapplyLayoutAsync();
			FloatingToastService.Show($"已从底栏移除「{name}」");
			DebugLog.Write($"[底栏] 移除常驻图标：{name} kind={slot.Item.Kind}");
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 移除常驻图标失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 拖拽松手的位置是否已经"离开底栏"（触屏/鼠标把常驻图标拖出去 = 删除）。
	/// 往上拖出门槛最低；左右方向要求甩得更远，避免拖动排序时手滑误删。
	/// </summary>
	private bool IsOutsideDockForDelete(int screenX, int screenY)
	{
		if (!GetWindowRect(Hwnd, out var rect)) return false;
		var scale = Math.Max(1.0, Scale);
		var marginY = (int)(36 * scale);
		var marginX = (int)(80 * scale);
		return screenY < rect.Top - marginY
			|| screenX < rect.Left - marginX
			|| screenX > rect.Right + marginX;
	}

	private bool IsMinimized(nint hwnd)
	{
		return IsIconic(hwnd);
	}

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint hwnd);

	private bool HitDock(int screenX, int screenY)
	{
		if (IsCollapsed)
		{
			return false;
		}
		nint hwnd = Hwnd;
		if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
		{
			return false;
		}
		if (!GetWindowRect(hwnd, out var rect))
		{
			return false;
		}
		if (screenX >= rect.Left && screenX <= rect.Right && screenY >= rect.Top)
		{
			return screenY <= rect.Bottom;
		}
		return false;
	}

	private void ShowGroupPopup(LaunchItem group, int index)
	{
		try
		{
			List<DockPopupEntry> list = (from m in @group.Members
				where !string.IsNullOrWhiteSpace(m.Target)
				select new DockPopupEntry(m.Name, m, m.Target, IconPathOf(m.Target))).ToList();
			if (list.Count == 0)
			{
				LaunchAsync(group);
				return;
			}
			GetWindowRect(Hwnd, out var rect);
			int anchorX = AnchorXFor(rect, index);
			DockPopupWindow.ShowGrid(group.Name, list, anchorX, rect.Top);
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 打开组失败：" + ex.Message);
		}
	}

	private void ShowFolderPopup(LaunchItem folder, int index)
	{
		try
		{
			if (!Directory.Exists(folder.Target))
			{
				LaunchAsync(folder);
				return;
			}
			List<DockPopupEntry> list = new List<DockPopupEntry>();
			foreach (string item in Directory.GetDirectories(folder.Target).Take(8))
			{
				list.Add(new DockPopupEntry(Path.GetFileName(item), null, item, null, IsFolder: true));
			}
			foreach (string item2 in Directory.GetFiles(folder.Target).Take(12))
			{
				list.Add(new DockPopupEntry(Path.GetFileName(item2), null, item2, IconPathOf(item2)));
			}
			if (list.Count == 0)
			{
				LaunchAsync(folder);
				return;
			}
			if (list.Count > 12)
			{
				list = list.Take(12).ToList();
			}
			GetWindowRect(Hwnd, out var rect);
			int anchorX = AnchorXFor(rect, index);
			DockPopupWindow.ShowArc(folder.Name, list, anchorX, rect.Top, _arcAngle);
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 展开文件夹失败：" + ex.Message);
		}
	}

	private static string? IconPathOf(string target)
	{
		try
		{
			string text = IconExtractService.CachedIconPath(target);
			return (text != null && File.Exists(text)) ? text : null;
		}
		catch
		{
			return null;
		}
	}

	private bool OnHookLeftDown(int screenX, int screenY)
	{
		try
		{
			// 自动隐藏开启时，点击底栏以外（桌面/其它窗口/页面）就收起；
			// 关掉自动隐藏 = 一直显示，点别处不再收起（只有手柄 B / 下滑手势才收）
			if (_autoHide && !IsCollapsed && GetWindowRect(Hwnd, out var rect)
				&& (screenX < rect.Left || screenX > rect.Right || screenY < rect.Top || screenY > rect.Bottom))
			{
				_dispatcher?.TryEnqueue(delegate { SetCollapsed(collapsed: true); });
			}
			return HandleHookLeftDown(screenX, screenY);
		}
		catch (Exception ex)
		{
			// 钩子线程上抛异常会被上层吞掉，表现为"点了没反应"且没有任何线索；这里至少留下记录。
			DebugLog.Write("[底栏] 左键处理异常：" + ex);
			_pressIndex = -1;
			return false;
		}
	}

	private void StartWidgetHold(int index)
	{
		StopWidgetHold();
		if (_dispatcher is null || index < 0) return;
		_widgetHoldIndex = index;
		_widgetHoldTimer ??= _dispatcher.CreateTimer();
		_widgetHoldTimer.Interval = TimeSpan.FromMilliseconds(550);
		_widgetHoldTimer.IsRepeating = false;
		_widgetHoldTimer.Tick -= OnWidgetHoldTick;
		_widgetHoldTimer.Tick += OnWidgetHoldTick;
		_widgetHoldTimer.Start();
	}

	private void StopWidgetHold()
	{
		_widgetHoldIndex = -1;
		_widgetHoldTimer?.Stop();
	}

	private void OnWidgetHoldTick(DispatcherQueueTimer sender, object args)
	{
		if (_widgetHoldIndex < 0 || _pressIndex != _widgetHoldIndex || _dragging)
		{
			StopWidgetHold();
			return;
		}
		int index = _widgetHoldIndex;
		DockSlot? slot = _pressSlot;
		StopWidgetHold();
		_pressIndex = -1;       // 抬起时不要再触发一次组件的正常点击
		SetPressVisual(index, pressed: false);
		DeleteDockWidget(slot);
	}

	private bool HandleHookLeftDown(int screenX, int screenY)
	{
		// 鼠标/触摸开始操作：先撤掉手柄选中框，避免两种光标并存
		if (_padIndex >= 0) _dispatcher?.TryEnqueue(ClearGamepadSelection);
		if (DockPopupWindow.IsOpen && !DockPopupWindow.Contains(screenX, screenY))
		{
			DockPopupWindow.HideCurrent();
		}
		// 固定显示的卡片：点击卡片本身交给预览窗处理（切换/关闭窗口），点其它地方先收起
		if (_previewPinned && !DockPreviewHost.Contains(screenX, screenY))
		{
			HidePreview();
		}
		if (!HitDock(screenX, screenY))
		{
			return false;
		}
		_hoverIndex = -1;
		HidePreview();
		GetWindowRect(Hwnd, out var rect);
		_pressIndex = IndexFromClientX(screenX - rect.Left);
		DebugLog.Write($"[底栏] 左键 {screenX},{screenY} idx={_pressIndex} 底栏={rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
		_pressScreenX = screenX;
		_pressScreenY = screenY;
		_dragging = false;
		_dragTarget = -1;
		_pressSlot = ((_pressIndex >= 0 && _pressIndex < _slots.Count) ? _slots[_pressIndex] : null);
		if (_pressSlot?.Item.Kind == LaunchKind.Widget)
		{
			int held = _pressIndex;
			_dispatcher?.TryEnqueue(() => StartWidgetHold(held));
		}
		else
		{
			_dispatcher?.TryEnqueue(StopWidgetHold);
		}
		int pressed = _pressIndex;
		_dispatcher?.TryEnqueue(delegate
		{
			SetPressVisual(pressed, pressed: true);
		});
		return true;
	}

	private bool OnHookLeftMove(int screenX, int screenY)
	{
		if (_pressIndex < 0)
		{
			return false;
		}
		DockSlot pressSlot = _pressSlot;
		if ((object)pressSlot != null && pressSlot.Start)
		{
			return true;
		}
		if (!_dragging && Math.Abs(screenX - _pressScreenX) < 8 && Math.Abs(screenY - _pressScreenY) < 8)
		{
			return true;
		}
		_dragging = true;
		_dispatcher?.TryEnqueue(StopWidgetHold);
		GetWindowRect(Hwnd, out var rect);
		_dragTarget = IndexFromClientX(screenX - rect.Left);
		int offset = screenX - _pressScreenX;
		// 拖出底栏时把图标压暗，给"松手即删除"一个视觉预告（开始/运行图标本来就不能删）
		bool outside = _pressSlot is { Start: false, Dynamic: false } && IsOutsideDockForDelete(screenX, screenY);
		_dispatcher?.TryEnqueue(delegate
		{
			if (_pressIndex >= 0 && _pressIndex < ItemsPanel.Children.Count && ItemsPanel.Children[_pressIndex] is FrameworkElement frameworkElement)
			{
				frameworkElement.Translation = new Vector3(offset, 0f, 0f);
				frameworkElement.Opacity = outside ? 0.35 : 0.7;
			}
		});
		return true;
	}

	private bool OnHookLeftUp(int screenX, int screenY)
	{
		if (_pressIndex < 0)
		{
			return false;
		}
		_dispatcher?.TryEnqueue(StopWidgetHold);
		int from = _pressIndex;
		int to = _dragTarget;
		bool wasDragging = _dragging;
		DockSlot? releasedSlot = _pressSlot;
		// 触屏/鼠标：把常驻图标拖出底栏再松手 = 删除（开始与运行中的图标不参与）
		bool droppedOutside = wasDragging && releasedSlot is { Start: false, Dynamic: false }
			&& IsOutsideDockForDelete(screenX, screenY);
		_pressIndex = -1;
		_dragging = false;
		_dragTarget = -1;
		_dispatcher?.TryEnqueue(delegate
		{
			SetPressVisual(from, pressed: false);
			if (droppedOutside)
			{
				ResetDragVisual(from);
				RemoveResidentSlot(releasedSlot);
				return;
			}
			if (wasDragging)
			{
				if (to >= 0 && to != from)
				{
					ReorderDock(from, to);
				}
				else
				{
					ResetDragVisual(from);
				}
			}
			else if (from < _slots.Count)
			{
				ActivateSlot(releasedSlot, from);
			}
		});
		return true;
	}

	/// <summary>
	/// 打开一个格子对应的目标：开始菜单 / 托盘应用 / 已运行的窗口 / 应用组 / 文件夹 / 启动。
	/// 鼠标左键抬起与手柄 A 共用，保证两条输入通道行为完全一致。
	/// </summary>
	private void ActivateSlot(DockSlot? slot, int index)
	{
		if (slot is null) return;
		if (slot.Start)
		{
			OpenStartMenu();
			return;
		}
		if (slot.Item.Kind == LaunchKind.Tray)
		{
			_ = ToggleLaunchAsync(slot.Item);
			return;
		}
		if (slot.Item.Kind == LaunchKind.Widget)
		{
			// 性能组件：在桌面弹一个性能面板，而不是启动器内部的面板
			if (slot.Item.WidgetKind == WidgetKind.Performance)
			{
				ShowPerformanceDockPanel(index);
				return;
			}
			// 修改器/游戏存档依赖“当前正在玩的游戏”，先给桌面提示，避免点了像没反应
			if (slot.Item.WidgetKind is WidgetKind.Trainer or WidgetKind.Backup)
			{
				if (string.IsNullOrEmpty(TrainerService.LastExeName) || !TrainerService.IsGameRunning())
				{
					FloatingToastService.Show("未检测到游戏进程，请先运行游戏再点这个组件", warning: true);
					return;
				}
				if (!TrainerService.IsLastGameActuallyForeground())
				{
					FloatingToastService.Show("请先把游戏切到前台，再点这个组件", warning: true);
					return;
				}
			}
			// 其它组件：与主页磁贴同一套触发语义
			App.Main?.ActivateDockWidget(slot.Item);
			return;
		}
		if (slot.Window != IntPtr.Zero)
		{
			AppWindows.Activate(slot.Window);
			return;
		}
		if (slot.Item.Kind == LaunchKind.Group)
		{
			ShowGroupPopup(slot.Item, index);
			return;
		}
		if (slot.Item.Kind == LaunchKind.Folder && _folderExpand)
		{
			ShowFolderPopup(slot.Item, index);
			return;
		}
		ToggleLaunchAsync(slot.Item);
	}

	/// <summary>性能组件在底栏点击：桌面弹层里直接切电源模式，不打开启动器。</summary>
	private void ShowPerformanceDockPanel(int index)
	{
		try
		{
			MainPage.BeginDockAction();
			if (App.Main is { } main)
			{
				// 复用主页按 Y 打开的那块完整性能面板，原样搬进独立桌面窗口
				main.OpenPerformanceFromDock();
				return;
			}
			GetWindowRect(Hwnd, out var rect);
			int anchorX = AnchorXFor(rect, index);
			var state = PerformanceService.GetState();
			int current = (int)state.Level;
			var list = new List<DockPopupEntry>();
			string[] names = ["节能", "均衡", "性能"];
			for (var i = 0; i < names.Length; i++)
			{
				int level = i;
				string label = $"{names[i]}{(current == i ? "（当前）" : "")}";
				list.Add(new DockPopupEntry(label, null, "", null, IsFolder: false, delegate
				{
					ApplyDockPerformanceLevel((PerformanceLevel)level);
				}));
			}
			if (!state.Available)
				list.Add(new DockPopupEntry("当前系统不支持电源模式", null, "", null, IsFolder: false, null));
			DockPopupWindow.ShowMenu("性能控制", list, anchorX, rect.Top);
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 性能面板失败：" + ex.Message);
		}
	}

	private async void ApplyDockPerformanceLevel(PerformanceLevel level)
	{
		try
		{
			var (ok, message) = await Task.Run(() => PerformanceService.ApplyLevel(level));
			FloatingToastService.Show(message, warning: !ok);
		}
		catch (Exception ex)
		{
			FloatingToastService.Show("切换性能模式失败：" + ex.Message, warning: true);
		}
	}

	private void ResetDragVisual(int index)
	{
		if (index >= 0 && index < ItemsPanel.Children.Count && ItemsPanel.Children[index] is FrameworkElement frameworkElement)
		{
			frameworkElement.Translation = new Vector3(0f, 0f, 0f);
			frameworkElement.Opacity = 1.0;
		}
	}

	private int IndexFromClientX(int clientX)
	{
		if (_itemCount <= 0)
		{
			return -1;
		}
		double scale = Scale;
		if (scale <= 0) return -1;
		double x = clientX / scale;
		double step = _iconSize + ItemSpacing;
		int best = 0;
		double bestDistance = double.MaxValue;
		for (var i = 0; i < _itemCount; i++)
		{
			double center = _sidePadding + i * step + DividerExtraFor(i) + _iconSize / 2.0;
			double distance = Math.Abs(x - center);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = i;
			}
		}
		return best;
	}

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hwnd, out RECT rect);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hwnd);

	private void ReorderDock(int from, int to)
	{
		if (from < 0 || to < 0 || from >= _slots.Count || to >= _slots.Count || _slots[from].Start || _slots[to].Start || _slots[from].Item.Kind == LaunchKind.Tray || _slots[to].Item.Kind == LaunchKind.Tray || _slots[from].Dynamic || _slots[to].Dynamic)
		{
			return;
		}
		DockSlot item = _slots[from];
		_slots.RemoveAt(from);
		_slots.Insert(to, item);
		List<LaunchItem> list = new List<LaunchItem>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (DockSlot slot in _slots)
		{
			if (!slot.Start && slot.Item.Kind != LaunchKind.Tray && !slot.Dynamic && hashSet.Add(slot.Item.Target))
			{
				list.Add(slot.Item);
			}
		}
		try
		{
			List<LaunchItem> list2 = ConfigService.Load();
			List<int> list3 = new List<int>();
			for (int i = 0; i < list2.Count; i++)
			{
				if (IsDockItem(list2[i]))
				{
					list3.Add(i);
				}
			}
			for (int j = 0; j < list3.Count && j < list.Count; j++)
			{
				list2[list3[j]] = list[j];
			}
			ConfigService.Save(list2);
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 保存排序失败：" + ex.Message);
		}
		RebuildAsync();
		ReapplyLayoutAsync();
	}

	private async Task ReapplyLayoutAsync()
	{
		await Task.Delay(30);
		LayoutInvalidated?.Invoke();
	}

	private void UpdateMagnify(int index)
	{
		if (index != _magnifyIndex)
		{
			int magnifyIndex = _magnifyIndex;
			_magnifyIndex = index;
			if (magnifyIndex >= 0 && magnifyIndex < ItemsPanel.Children.Count && ItemsPanel.Children[magnifyIndex] is FrameworkElement frameworkElement)
			{
				Canvas.SetZIndex(frameworkElement, 0);
				ApplyMagnify(frameworkElement, inside: false);
			}
			if (index >= 0 && index < ItemsPanel.Children.Count && ItemsPanel.Children[index] is FrameworkElement frameworkElement2)
			{
				Canvas.SetZIndex(frameworkElement2, 100);
				ApplyMagnify(frameworkElement2, inside: true);
			}
		}
	}

	private void ApplyMagnify(FrameworkElement slot, bool inside)
	{
		float target = ((inside && _magnify) ? 1.18f : 1f);
		if (!inside && _pressVisual == slot && _pressFeedback)
		{
			target = 0.92f;
		}
		SetScaleSafe(slot, target);
	}

	private static void SetScaleSafe(FrameworkElement slot, float target)
	{
		Vector3 centerPoint = new Vector3((float)(slot.ActualWidth / 2.0), (float)(slot.ActualHeight / 2.0), 0f);
		try
		{
			Visual elementVisual = ElementCompositionPreview.GetElementVisual(slot);
			elementVisual.StopAnimation("Scale");
			elementVisual.StopAnimation("Opacity");
			elementVisual.CenterPoint = centerPoint;
			elementVisual.Scale = new Vector3(target, target, 1f);
			return;
		}
		catch
		{
		}
		try
		{
			slot.Scale = new Vector3(target, target, 1f);
			slot.CenterPoint = centerPoint;
		}
		catch
		{
		}
	}

	private DispatcherQueueTimer? _padPressTimer;
	private int _padPressIndex = -1;

	/// <summary>手柄 A 的按下反馈：复用鼠标按下的 0.92 缩放，约 110ms 后弹回。</summary>
	private void PlayPadPressFeedback(int index)
	{
		if (!_pressFeedback || index < 0 || index >= ItemsPanel.Children.Count) return;
		if (_padPressTimer is null)
		{
			_padPressTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
			if (_padPressTimer is null) return;
			_padPressTimer.Interval = TimeSpan.FromMilliseconds(110);
			_padPressTimer.IsRepeating = false;
			_padPressTimer.Tick += delegate
			{
				_padPressTimer?.Stop();
				SetPressVisual(_padPressIndex, pressed: false);
				if (_padIndex >= 0) UpdateMagnify(_padIndex);
			};
		}
		_padPressIndex = index;
		SetPressVisual(index, pressed: true);
		_padPressTimer.Stop();
		_padPressTimer.Start();
	}

	private void SetPressVisual(int index, bool pressed)
	{
		if (_pressFeedback && index >= 0 && index < ItemsPanel.Children.Count && ItemsPanel.Children[index] is FrameworkElement frameworkElement)
		{
			_pressVisual = (pressed ? frameworkElement : null);
			SetScaleSafe(frameworkElement, pressed ? 0.92f : 1f);
		}
	}

	/// <summary>
	/// 点击启动/切换，按 MiniDock 的做法：目标已在前台就收起，否则还原（被隐藏的窗口 Show 出来）
	/// 或启动；不模拟点击托盘图标（allowTrayWake 用设置里的开关，默认关）。
	/// </summary>
	private async Task LaunchAsync(LaunchItem item)
	{
		LightIndicator(item.Target);
		KickScan();
		try
		{
			await LaunchService.LaunchOrActivateAsync(item, CancellationToken.None, SettingsService.Current.DockTrayWake);
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 启动失败：" + ex.Message);
		}
		KickScan();
	}

	private async Task ToggleLaunchAsync(LaunchItem item)
	{
		LightIndicator(item.Target);
		KickScan();
		try
		{
			var result = await LaunchService.ToggleAsync(item, CancellationToken.None, SettingsService.Current.DockTrayWake);
			if (item.Kind == LaunchKind.Tray && result.Outcome == LaunchOutcome.Failed && result.Message.Length > 0)
			{
				DebugLog.Write("[底栏] 托盘应用：" + result.Message);
				FloatingToastService.Show(result.Message, warning: true);
			}
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 切换失败：" + ex.Message);
		}
		KickScan();
	}

	private static string WindowTitleOf(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return "";
		}
		try
		{
			StringBuilder stringBuilder = new StringBuilder(256);
			return (GetWindowTextRaw(hwnd, stringBuilder, stringBuilder.Capacity) > 0) ? stringBuilder.ToString() : "";
		}
		catch
		{
			return "";
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextRaw(nint hwnd, StringBuilder text, int count);

	private void SetCollapsed(bool collapsed)
	{
		if (IsCollapsed != collapsed)
		{
			IsCollapsed = collapsed;
			if (collapsed)
			{
				_hoverIndex = -1;
				HidePreview();
				ClearGamepadSelection();
			}
			CollapsedChanged?.Invoke();
		}
	}

	private void StartWatch()
	{
		if ((object)_watchTimer == null)
		{
			_watchTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(150.0)
			};
		}
		_watchTimer.Tick -= OnWatchTick;
		_watchTimer.Tick += OnWatchTick;
		_watchTimer.Start();
	}

	private void StopWatch()
	{
		_watchTimer?.Stop();
		_leaveAt = null;
	}

	private void OnWatchTick(object? sender, object e)
	{
		if (!_autoHide)
		{
			StopWatch();
		}
		else
		{
			if (!GetCursorPos(out var point))
			{
				return;
			}
			if (IsCollapsed)
			{
				int num = (int)Math.Round(6.0 * Scale) + 2;
				if (point.X >= _x && point.X <= _x + _w && point.Y >= _y && point.Y <= _y + num)
				{
					_leaveAt = null;
					SetCollapsed(collapsed: false);
				}
				return;
			}
			// 呼出后保持显示：不再因为鼠标离开而自动收起，只有手柄 B 或失焦时才隐藏
			return;
		}
	}

	private void HookDrop()
	{
		DockDropTarget.DockDropTargetHandler = delegate(List<string> files)
		{
			_dispatcher?.TryEnqueue(delegate
			{
				AddDroppedFiles(files);
			});
		};
		Task.Delay(1500).ContinueWith((Task _) => _dispatcher?.TryEnqueue(delegate
		{
			try
			{
				DockDropTarget.Attach(Hwnd);
			}
			catch (Exception ex)
			{
				DebugLog.Write("[底栏] 注册拖放失败：" + ex.Message);
			}
		}));
	}

	private void AddDroppedFiles(List<string> files)
	{
		try
		{
			List<LaunchItem> list = ConfigService.Load();
			int num = 0;
			foreach (string file in files)
			{
				if (string.IsNullOrWhiteSpace(file))
				{
					continue;
				}
				LaunchKind kind;
				if (Directory.Exists(file))
				{
					kind = LaunchKind.Folder;
				}
				else if (file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
				{
					kind = LaunchKind.Url;
				}
				else
				{
					if (!File.Exists(file))
					{
						continue;
					}
					kind = LaunchKind.App;
				}
				list.Add(new LaunchItem
				{
					Name = Path.GetFileNameWithoutExtension(file),
					Target = file,
					Kind = kind,
					// 拖进底栏的条目只属于底栏，不再出现在启动器主页
					LauncherHidden = true
				});
				num++;
			}
			if (num != 0)
			{
				ConfigService.Save(list);
				RebuildAsync();
				ReapplyLayoutAsync();
				DebugLog.Write($"[底栏] 拖入已添加 {num} 个条目");
			}
		}
		catch (Exception ex)
		{
			DebugLog.Write("[底栏] 处理拖入失败：" + ex.Message);
		}
	}

	private void ApplyChrome()
	{
		nint hwnd = Hwnd;
		if (hwnd != IntPtr.Zero)
		{
			long num = ((IntPtr)GetWindowLongPtr(hwnd, -16)).ToInt64();
			num &= -13565953;
			num |= 0x80000000u;
			SetWindowLongPtr(hwnd, -16, (nint)num);
			long num2 = ((IntPtr)GetWindowLongPtr(hwnd, -20)).ToInt64();
			SetWindowLongPtr(hwnd, -20, (nint)(num2 | 0x80 | 0x8000000));
			SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
			int value = 2;
			DwmSetWindowAttribute(hwnd, 33, ref value, 4);
			base.SystemBackdrop = new AlwaysActiveAcrylicBackdrop();
		}
	}

	private static nint GetWindowLongPtr(nint hwnd, int index)
	{
		if (IntPtr.Size != 8)
		{
			return GetWindowLong32(hwnd, index);
		}
		return GetWindowLongPtr64(hwnd, index);
	}

	private static nint SetWindowLongPtr(nint hwnd, int index, nint value)
	{
		if (IntPtr.Size != 8)
		{
			return SetWindowLong32(hwnd, index, ((IntPtr)value).ToInt32());
		}
		return SetWindowLongPtr64(hwnd, index, value);
	}

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong32(nint hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern nint GetWindowLongPtr64(nint hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
	private static extern int SetWindowLong32(nint hwnd, int index, int value);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hwnd, int cmd);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint hwnd);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT point);
}


