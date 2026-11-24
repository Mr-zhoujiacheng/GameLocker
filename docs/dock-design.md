# Dock 与顶栏设计文档（借鉴 MiniDock 的自研方案）

版本：v0.5（已实现，含 v0.4 观感打磨与 v0.5 交互补齐）
日期：2026-11-17
状态：S0–F1 全部落地；观感项与"点击切换 / 托盘应用进底栏 / 窗口列表登记"已补齐
变更：v0.2 将**顶栏**从"非目标"改为正式范围；v0.3 记录用户已拍板的 6 项决策。

---

## 1. 背景

前面两次 Dock 尝试（MacDock 的 WinUI 移植、windows-dock 的 C++ 原生后端）都做成了**独立 exe + 宿主托管**的形态，最终都因为"观感不达预期"被整体删除。

这次参考 [MiniDock](https://github.com/wang01116/MiniDock)，但**不搬运其代码**：

- MiniDock 是单个 `minidock.c`（13,666 行 / 703 KB），纯 Win32 + GDI+ 手绘，用 MinGW-w64 构建；
- 仓库**没有 LICENSE 文件**，README 自述"版权归作者所有"。未经授权修改/分发存在法律风险，因此只借鉴做法，不复制实现。

本文档给出：借鉴哪些做法、在 GameLocker 里怎么落地、分几步做、怎么验收。

---

## 2. 一个必须先讲清楚的重名问题

项目里**已经有一个"顶栏"**：`TopbarPage.xaml` + `SettingsPage` 的「顶栏」分类卡片，控制 `ShowClock` / `ShowBattery` / `ShowWifi` / `ShowBluetooth` / `ShowGamepadIcon` / `ShowNotifications` / `ShowNightLight` / `ShowSearch` 这 8 个开关。

它管的是**启动器窗口内部的那条信息栏**（`MainPage` 顶部），只在启动器呼出时可见。

本次要做的是**系统级顶栏**：独立窗口、常驻桌面顶部、启动器收起后仍然显示。两者是完全不同的东西。

**命名约定（本文档沿用）**：

| 名称 | 指代 | 代码位置 |
|---|---|---|
| 启动器内顶栏 | 已有的、`MainPage` 内部那一栏 | `MainPage.xaml`，设置页 `TopbarPage` |
| **系统顶栏** | 本次新增，独立常驻窗口 | `StatusBarWindow`（新） |

> 设置页里新增的是「**系统顶栏**」分类，现有「顶栏」分类**保持不动**，避免破坏已有行为。

---

## 3. 目标与非目标

### 3.1 目标

1. 在 GameLocker 内提供一个**底部 Dock 条**，能把常用应用收进来、一点即启动/切换。
2. 在 GameLocker 内提供一个**顶部系统状态栏**：时间、电量、网络、输入法、通知等常驻信息。
3. 两者都通过 **AppBar 合法占位**（`SHAppBarMessage`），让最大化窗口自动避让，**不隐藏系统任务栏、不和 Explorer 抢窗口层级**。
4. 空闲时**零动画定时器**、零重绘，内存增量控制在可接受范围。
5. 与现有数据（`items.json`）和设置体系复用，不引入第二套配置。

### 3.2 非目标（本期不做）

- 启动台全屏网格、搜索引擎条
- 弧形展开、窗口 DWM 实时缩略图预览
- 多显示器逐屏镜像（先只做主屏）
- 替换或隐藏系统任务栏
- 系统托盘图标区接管、任务栏通知区域镜像
- 控制中心面板（WiFi/蓝牙开关弹层）——顶栏只做**显示**，开关操作仍走系统或启动器

> 这些明确排除，避免范围膨胀重演前两次的失败。「顶栏」的范围仅限**信息显示**，不做系统控制面板。

---

## 4. 关键设计决策

| 编号 | 决策 | 备选 | 理由 |
|---|---|---|---|
| D1 | **同进程内新增顶层窗口** | 独立 exe + IPC 托管 | 前两次失败的共同点就是多进程：数据要同步、生命周期要守护、退出要兜底。同进程直接复用 `ConfigService` / `IconExtractService` / `SettingsService`，没有 IPC、没有第二套配置。 |
| D2 | **AppBar 占位**（`SHAppBarMessage`） | 隐藏系统任务栏 + 自绘（前两次做法） | 已实测：全屏/最大化窗口存在时，Explorer 会把任务栏踢出置顶层，第三方 `SetWindowPos` 无效。AppBar 是系统承认的"预留区域"，最大化窗口会自动避让，不需要抢 Z 序。 |
| D3 | **WinUI XAML 渲染** | 裸 HWND + GDI 手绘（MiniDock 路线） | 观感是前两次失败的主因。XAML 直接拿到抗锯齿、圆角、阴影、字体排版、Composition 动画；手绘 DIB 要达到同等质量代码量会翻几倍。代价是每窗口额外内存（估 10–20 MB），可接受。 |
| D4 | **复用 `items.json`**，给 `LaunchItem` 增加 `DockPinned` 标记 | 新建 `dock.json` | 单一数据源。用户在任意一处增删应用，另一处即时一致。 |
| D5 | **不影响启动器召唤** | 由 Dock 接管召唤 | Dock 是常驻窗口，启动器的 F7 / 手柄 / 边缘手势逻辑保持不变，避免两套 UI 抢焦点。 |
| D6 | **系统顶栏默认"占位"（AppBar）**，可切"浮层不占位" | 只做浮层 | macOS 菜单栏是占位的，占位才有"系统栏"的感觉。但不占位更不打扰，所以做成开关，默认占位。 |
| D7 | **系统顶栏只显示、不控制** | 完整控制中心 | 控制中心需要接管 WiFi/蓝牙/音量等系统状态，与 GameLocker 现有服务重复且风险高。本期只读显示。 |
| D8 | **两个 AppBar 分居上下两边** | 同一侧堆叠 | 系统允许不同边的多个 AppBar 共存；同边会互相抢占工作区。Dock 默认底部、系统顶栏默认顶部，天然错开。 |

### 4.1 为什么不用"隐藏系统任务栏"

实测数据（本机 Win11 26200，2026-11-16）：

| 场景 | 任务栏 topmost |
|---|---|
| 无全屏窗口 | True |
| 出现全屏窗口 | False（被系统压下） |
| 第三方 `SetWindowPos(TOPMOST)` | 无效 |
| `SW_HIDE` + `SW_SHOW` | 约 300 ms 后又被压回 |

结论：**任何"隐藏任务栏再自绘"的方案都要长期和 Explorer 对抗**，这正是前两次体验崩坏的根因。AppBar 走的是系统接口，不参与这场对抗。

---

## 5. 架构

### 5.1 组件

```
AppBarHost (共享, 新)
 └─ 封装 ABM_NEW / SETPOS / REMOVE / ABN_POSCHANGED / TaskbarCreated 重注册
    ├─ DockWindow (WinUI Window, 新)
    │   ├─ DockItemsSource     从 ConfigService.Load() 过滤 DockPinned
    │   ├─ DockRenderer        XAML 图标条 + Composition 弹簧动画
    │   └─ DockInteraction     点击启动 / 右键菜单 / 拖拽排序
    └─ StatusBarWindow (WinUI Window, 新)
        ├─ StatusClock         时间 + 日期（每分钟刷新一次，无秒级定时器）
        ├─ StatusBattery       电量 + 充电状态（复用现有电池服务）
        ├─ StatusNetwork       WiFi / 有线 / 离线
        ├─ StatusIme           中 / EN（读输入法状态）
        └─ StatusNotify        未读通知计数（复用现有通知服务）
```

两个窗口共用 `AppBarHost`，避免重复实现注册、重注册、DPI 重算逻辑。

### 5.2 代码落点

| 文件 | 变更 |
|---|---|
| `Services/AppBarHost.cs`（新） | AppBar 注册/位置/移除/重注册的公共实现 |
| `Services/ShellRestartWatcher.cs`（新） | 监听 `TaskbarCreated`，Explorer 重启后重注册占位 |
| `Services/DockInputHook.cs`（新） | `WH_MOUSE_LL` 接管底栏点击与拖拽；触摸按下 550 ms 不动时抛 `LongPress`（见 §14 架构发现） |
| `Services/ImeStateWatcher.cs`（新） | 输入法转换态读取（IMM 消息）+ 前台变化通知 |
| `Services/AppWindows.cs`（新） | 枚举可见顶层窗口并按 exe 归组；独立窗口模式与预览共用 |
| `Services/DockPreviewHost.cs`（新） | 窗口预览：逐像素透明分层窗口，一个窗口一张抽屉风卡片，在底栏上方摆成叠压手牌；卡内画面用 `PrintWindow(PW_RENDERFULLCONTENT)` 快照 |
| `DockPopupWindow.xaml(.cs)`（新） | 底栏弹出面板：应用组宫格 + 文件夹弧形展开；可激活窗口，布局全用 DIP |
| `Services/Monitors.cs`（新） | 显示器枚举与选屏（主屏 / 跟随鼠标 / 指定序号） |
| `Services/TaskbarControl.cs`（新） | 任务栏自动隐藏/永久隐藏 + Ctrl 下边缘临时唤出 + 退出恢复 |
| `DockNoteWindow.xaml.cs`（新） | 底栏速记：自动保存 + 导出 txt/Markdown |
| `Services/StagingService.cs`（新） | 临时周转组：固定目录，启动清空 |
| `Services/TrayAppService.cs`（新） | 托盘常驻应用：登记 + 按类名/标题/进程追踪窗口 + 唤出 |
| `Services/DockDropTarget.cs`（新） | 文件拖入：OLE 放置目标注册在 WinUI 内容桥窗口上 |
| `Services/DockHost.cs`（新） | Dock 单例生命周期：`Initialize/Apply/Suspend/Shutdown` |
| `Services/StatusBarHost.cs`（新） | 系统顶栏单例生命周期 |
| `DockWindow.xaml` / `.cs`（新） | 无边框、透明、`WS_EX_TOOLWINDOW`、`WS_EX_NOACTIVATE` |
| `StatusBarWindow.xaml` / `.cs`（新） | 同上，高 28–36 px |
| `Controls/StatusBarSegments.cs`（新） | 五个信息段的控件与刷新逻辑 |
| `Models/LaunchItem.cs` | 新增 `bool DockPinned`（默认 false，JSON 向后兼容） |
| `Models/AppSettings.cs` | 新增 `Dock*` 与 `StatusBar*` 两组设置（见 §8） |
| `App.xaml.cs` | `Main = new MainWindow();` 之后调用两个 Host 的 `Initialize()` |
| `SettingsPage.xaml(.cs)` | 新增「Dock」与「系统顶栏」两个分类卡片 |
| `DockSettingsPage` / `StatusBarSettingsPage`（新） | 对应设置子页 |
| `GameLocker.iss` | 无需变更（无额外文件） |

### 5.3 生命周期

```
App.OnLaunched
  → MainWindow 创建
  → DockHost.Initialize()         → 读 DockEnabled → 建窗口 / 注册 AppBar
  → StatusBarHost.Initialize()    → 读 StatusBarEnabled → 建窗口 / 注册 AppBar
  → 退出: 两个 Host Shutdown() → ABM_REMOVE → 释放工作区 → 关窗
```

**顺序要求**：先释放 AppBar 再关窗口；否则工作区残留。

**失败降级**：`ABM_NEW` 返回 FALSE 时，窗口仍显示但**不占位**（浮层），并在设置页明示"当前未获得系统预留区域"。

**全屏游戏**：与 Dock 一致——不抢层级。检测到全屏时两个窗口都暂停刷新与动画，全屏结束恢复。

---

## 6. Dock 交互规格

| 操作 | 行为 |
|---|---|
| 左键单击最左「开始」图标 | 固定槽位（不能拖、不写配置）：**替代开始菜单模式**下显示 / 收起 GameLocker；否则按一下 `Win` 交给系统开始菜单 |
| 左键单击图标 | 启动 / 切换窗口（复用 `LaunchService` 现有逻辑） |
| 悬停 | 图标放大 1.0→1.18，相邻图标 1.08，Composition 弹簧；离开回落 |
| 悬停 600 ms | 显示应用名气泡（纯文本，不做缩略图） |
| 右键图标 | 打开 / 从 Dock 移除 / 打开所在文件夹 |
| 按住拖动 | 交换顺序，松手写回 `items.json` |
| 运行中 | 图标下方小圆点 |
| 新程序出现 / 程序关闭 | 图标弹入（0.5→1.0 缩放 + 淡入，约 300 ms）/ 缩出（→0.35 + 淡出，约 150 ms）后再重建；动态条目按"首次出现"稳定排序，不会互相跳位 |
| 右键空白处 | 设置 / 隐藏 Dock / 退出 GameLocker |
| 拖入文件 | 追加到 Dock 末尾（并标记 `DockPinned`） |

键盘/手柄：Dock 不抢焦点（`WS_EX_NOACTIVATE`），输入仍由启动器处理。

---

## 7. 系统顶栏规格

### 7.1 布局

```
┌──────────────────────────────────────────────────────────────┐
│ 🕐 09:28  周二 11/17        [WiFi] [🔋82%] [中] [🔔3]        │   ← 高 32px
└──────────────────────────────────────────────────────────────┘
  左对齐：时间 + 日期          右对齐：网络 / 电量 / 输入法 / 通知
```

- 高度：默认 32 px（可调 28–40），按 DPI 缩放。
- 左段与右段分区，中间留空（后续可放窗口标题或小组件）。
- 段间距 14 px，右段整体距右边缘 16 px。

### 7.2 信息段

| 段 | 内容 | 刷新时机 | 数据来源 |
|---|---|---|---|
| 时间 | `HH:mm` + 星期 + `M/d` | **每分钟一次**（分钟变化才重绘） | `DateTime.Now` |
| 网络 | WiFi 图标 / 有线 / 离线 | 网络状态变化事件 | 复用现有网络服务 |
| 电量 | 图标 + 百分比 + 充电态 | 5 分钟 + 电源事件 | 复用现有电池服务（WMI） |
| 输入法 | `中` / `EN` | 输入法切换事件 | 现有输入法状态读取 |
| 通知 | 铃铛 + 未读数 | 通知到达时 | 复用现有通知服务 |

**硬性约束**：不使用秒级定时器；只允许每分钟一次的时间检查，且仅在分钟变化时重绘。这是前两次 Dock 被诟病的"电老虎"问题的直接对策。

### 7.3 交互

| 操作 | 行为 |
|---|---|
| 左键单击时间 | 打开系统日期时间设置 |
| 左键单击电量 | 打开系统电源设置 |
| 左键单击通知 | 呼出启动器并定位到通知 |
| 右键空白处 | 设置 / 隐藏顶栏 |
| 悬停 | 段背景轻微高亮（无放大动画，避免干扰） |

顶栏**不抢焦点**（`WS_EX_NOACTIVATE`），点击只触发动作，不激活窗口。

---

## 8. 设置项

### 8.1 Dock

| 项 | 类型 | 默认 | 说明 |
|---|---|---|---|
| 启用 Dock | 开关 | **开** | 默认显示（见 §13.1 首次提示要求） |
| 位置 | 底部 | 底部 | 固定，不允许改到顶部（顶部归系统顶栏） |
| 图标大小 | 36–64 | 48 | **唯一尺寸设置**：条高不再单独配，固定按 `ceil((图标+指示占位)×1.18)+上下内边距 6×2` 推导，调大图标条高自动变高、调小自动变矮；设置页右侧实时显示推导出的条高 |
| 外观主题 | 7 档预设 | 深色玻璃 | 深色玻璃 / 浅色玻璃 / 纯色·炭黑 / 纯色·藏蓝 / 渐变·暮紫 / 渐变·海蓝 / 高透·极简。同时切换底板、顶部高光、图标底盘与窗口明暗（亚克力会跟着切浅色）；只改观感，不改尺寸 |
| 开始图标 | 固定 | 开 | 最左侧常驻的系统「开始」：替代开始菜单模式下点它开 GameLocker，否则开系统开始菜单；不占配置、不能拖动/移除，也没有运行指示 |
| 桌面占位 | 固定浮层 | — | 2026-11-17 起**不再提供占位开关**，始终悬浮、不挤占工作区 |
| 自动隐藏 | 开关 | 关 | 鼠标离开后收起，留 4 px 感应带 |
| 悬停放大 | 开关 | 开 | |
| 显示运行指示 | 开关 | 开 | **语义：该应用此刻在桌面上有窗口（含最小化）**；窗口全关但后台进程还在时不亮。每 1 秒扫描一次，熄灭带 2.5 秒保持（避免抖动闪烁）；样式 0=不显示 / 1=圆点 / 2=横条；画在图标下方 3 DIP 处，条高会按"图标+间隙+指示条"自动抬高 |
| 未固定的运行程序 | 自动 | 开 | 从桌面/开始菜单直接启动、没固定到底栏的程序会自动出现在末尾（最多 4 个），窗口关掉后自动消失；固定过的照旧常驻 |

### 8.2 系统顶栏

| 项 | 类型 | 默认 | 说明 |
|---|---|---|---|
| 启用系统顶栏 | 开关 | **开** | 默认显示（见 §13.1 首次提示要求） |
| 占用屏幕空间（AppBar） | 开关 | 开 | 关掉则浮层，不挤占最大化窗口 |
| 高度 | 28–40 | 32 | |
| 显示时间 | 开关 | 开 | |
| 显示日期 | 开关 | 开 | |
| 显示网络 | 开关 | 开 | |
| 显示电量 | 开关 | 开 | |
| 显示输入法 | 开关 | 开 | |
| 显示通知 | 开关 | 开 | |
| 背景 | 跟随系统 / 半透明 / 纯色 / 渐变 | 跟随系统 | 深色/浅色随系统切换 |
| 底色 | 深黑 / 炭灰 / 藏蓝 / 暖褐 | 深黑 `#161619` | 纯色、半透明直接用；跟随系统时作为亚克力着色 |
| 渐变第二色 | 暮紫 / 深海 / 酒红 / 石墨 | 暮紫 `#3A2B5C` | 仅「渐变」材质生效，对角铺满 |
| 字号 | 小 / 中 / 大 | 中 | |

**与启动器内顶栏的关系**：两组开关**完全独立**。系统顶栏的"显示时间"不影响 `TopbarPage` 的 `ShowClock`（后者控制启动器内那条）。

### 8.3 组合占位提示

两个 AppBar 同时启用且都占位时，屏幕上/下共占用 `顶栏高 + Dock 高`（默认 32 + 72 = 104 px）。设置页需显示实时占位总和，提醒用户。

---

## 9. 异常与边界

| 场景 | 处理 |
|---|---|
| Explorer 重启 | 监听 `TaskbarCreated`，两个 AppBar 都重新 `ABM_NEW` + `ABM_SETPOS` |
| 显示器热插拔 / 分辨率变化 | `WM_DISPLAYCHANGE` → 重算并 `ABM_SETPOS` |
| DPI 变化 | `WM_DPICHANGED` → 重算尺寸与字号 |
| 全屏游戏 | **不做对抗**：被覆盖属正常；检测到全屏时暂停刷新与动画 |
| 下载模式（已有全屏遮罩） | 遮罩期间两个 Host 都 `Suspend()`，结束后恢复 |
| 退出 | 先 `ABM_REMOVE`（两个）再关窗；异常退出由下次启动检测残留并清理 |
| 顶栏与 Dock 同侧 | 不允许：设置里锁定 Dock=底部、顶栏=顶部 |
| 单实例 | 无影响（同进程窗口） |
| 系统任务栏在顶部 | 顶栏占位会让任务栏下移，需在设置页提示"建议任务栏放底部" |

---

## 10. 性能预算

| 指标 | 目标 | 测量方式 |
|---|---|---|
| 空闲 CPU（仅 Dock） | < 0.1% | 任务管理器观察 60 s |
| 空闲 CPU（Dock + 顶栏） | < 0.2% | 同上 |
| 空闲动画 | 0 个运行中的动画/定时器 | 代码审查 + 计时器计数 |
| 内存增量（两个窗口） | < 40 MB（工作集） | 开关前后对比 |
| 顶栏重绘频率 | ≤ 1 次/分钟 | 埋点计数 |

实现约束：不用 `DispatcherTimer` 轮询秒；动画只用 Composition，结束后自动停止；顶栏各段独立失效，只重绘变化的段。

---

## 11. 风险

| 风险 | 等级 | 缓解 |
|---|---|---|
| WinUI 窗口注册 AppBar 需子类化 WndProc | 中 | 先做最小原型验证 `ABM_NEW` 成功、最大化窗口避让 |
| 观感仍不达预期（前两次主因） | **高** | 先做**静态样张**（Dock 一张、顶栏一张），确认风格再写交互 |
| 两个 AppBar 互相干扰工作区 | 中 | 强制分居上下；退出必 `ABM_REMOVE`；原型阶段先只做一个 |
| 顶栏占用顶部空间引起不适 | 中 | 默认占位但提供"浮层不占位"开关；设置页显示占用总和 |
| 与启动器内顶栏语义混淆 | 中 | 命名区分（§2）；设置页文案明确"系统顶栏 vs 启动器内顶栏" |
| 顶栏各段数据源不稳定（电量/网络） | 中 | 数据不可用时显示占位符而非崩溃；复用已验证的现有服务 |
| 透明窗口在部分显卡/Acrylic 关闭时发黑 | 低 | 提供纯色实心回退 |

---

## 12. 分阶段计划

先做**共享的 AppBar 基建**，再分别落地两个窗口——避免 Dock 和顶栏各写一套注册逻辑。

| 阶段 | 内容 | 产出 | 验收 |
|---|---|---|---|
| **S0** | 视觉样张 | Dock + 顶栏各一张静态截图 | 你看过并认可风格 |
| **S1** | `AppBarHost` 基建 | 能占位、最大化窗口避让、退出还原 | 工作区数值前后一致 |
| **D1** | Dock 空壳 | 底部占位、空态提示 | 显示正确、可开关 |
| **D2** | Dock 图标条 | 读 `DockPinned`、点击启动 | 启动 3 个应用正确 |
| **D3** | Dock 动画 + 运行指示 | 弹簧放大、小圆点 | 流畅，空闲 0 动画 |
| **D4** | Dock 设置 + 拖拽排序 | 落盘、重启保持 | 重启后一致 |
| **T1** | 顶栏空壳 | 顶部占位、背景材质 | 显示正确、可开关 |
| **T2** | 顶栏时间 + 电量 | 两个信息段 | 分钟级刷新，不闪 |
| **T3** | 顶栏网络 + 输入法 + 通知 | 三个信息段 | 状态变化及时 |
| **T4** | 顶栏设置页 | 全部开关落盘 | 重启后一致 |
| **F1** | 联合与异常 | 两个同时开、Explorer 重启、DPI、全屏暂停 | 逐项手测 |

每阶段结束都构建并部署到 `.setup\current-build`，你可以随时叫停。

---

## 13. 已定稿的决策（2026-11-17 用户确认）

| # | 决策 | 结果 |
|---|---|---|
| 1 | 先出 S0 样张再写交互 | **同意** |
| 2 | Dock 与系统顶栏的默认状态 | **默认都打开**（与 v0.2 的"默认关闭"相反） |
| 3 | 顶栏占位方式 | **先做 AppBar 占位**（浮动模式后续再加） |
| 4 | 位置分工 | **顶栏固定顶部、Dock 固定底部**，不做可切换 |
| 5 | 顶栏输入法段（中/EN） | **保留** |
| 6 | Dock 图标来源 | **复用现有磁贴固定项**，后续按需求再改 |
| 7 | 底栏是否占桌面空间（2026-11-17 追加） | **不占，固定为浮层**：不再暴露"占用屏幕空间"开关，窗口仍 `HWND_TOPMOST`，距工作区底边 8 DIP |

### 13.1 默认开启带来的额外要求

因为两个功能默认打开，必须补三条：

1. **首次升级不惊吓**：老用户升级后首次启动即出现上下两条栏。要在顶栏首次显示时用一次轻量提示（设置页标记或托盘气泡）说明来源与关闭方式。
2. **退出必须彻底释放**：`ABM_REMOVE` 两个 AppBar，否则工作区永久残留 104 px 空白。
3. **失败必须降级可见**：AppBar 注册失败时不能静默——设置页与诊断日志都要明确记录，否则用户只看到"窗口飘着但不占位"。

### 13.2 样张验收标准（S0）

S0 需产出两张**真实渲染**截图（不是示意图）：

- **Dock 样张**：底部条，含 6–8 个真实应用图标、1 个运行指示点、非悬停态。
- **顶栏样张**：顶部条，含时间/日期、网络、电量、输入法、通知五个段。

两张都要能看出圆角、背景材质、字号层级。你认可后才进入 S1（`AppBarHost` 基建）。

---

## 14. 实现进度（2026-11-17）

> **2026-11-21 勘误**：本节中 `StatusBarWindow` / `StatusBarHost` / `ImeStateWatcher` / `BatteryHealthService` / `StatusBarSettingsPage` 等「系统顶栏」条目与当前代码不符——这些源文件在一次文件回退中丢失，`obj/` 仅剩旧 XBF。当前进度以 `docs/topbar2-development-plan.md` §2 为准；本节保留作设计参考。

| 项 | 状态 | 代码位置 |
|---|---|---|
| 设置页「底栏」入口与子页 | ✅ | `DockSettingsPage.xaml(.cs)`，`SettingsPage` 卡片 index 6 |
| 设置页「顶栏 2」入口与子页 | ✅ | `StatusBarSettingsPage.xaml(.cs)`，卡片 index 7 |
| AppBar 占位基建 | ✅ | `Services/AppBarHost.cs`（ABM_NEW/QUERYPOS/SETPOS/REMOVE） |
| 底栏生命周期与占位 | ✅ | `Services/DockHost.cs` |
| 系统顶栏生命周期与占位 | ✅ | `Services/StatusBarHost.cs` |
| 底栏图标条（真实图标、点击启动、运行点、悬停放大） | ✅ 点击启动已实测（点 notepad 图标成功拉起进程） | `DockWindow.xaml(.cs)` |
| 顶栏信息段（时间/日期/网络/电量/输入法/通知） | ✅ | `StatusBarWindow.xaml(.cs)` |
| 默认开启 | ✅ | `AppSettings.DockEnabled/StatusBarEnabled = true` |
| 退出释放占位 | ✅ 已实测工作区还原 | `MainWindow.Closed` → 两个 Host 的 `Shutdown()` |
| 下载模式挂起 | ✅ | `BeginDownloadOverlay/EndDownloadOverlay` → `Suspend()` |
| Explorer 重启后重注册 | ✅ 已实测 | `Services/ShellRestartWatcher.cs` 监听 `TaskbarCreated` |
| DPI / 分辨率变化重排 | ✅ 用 `XamlRoot.Changed` + 缩放变化判定（避免自身 MoveTo 回环） | 两个窗口的 `LayoutInvalidated` |
| 底栏自动隐藏 | ✅ 已实测：收起留 12px 触发带、悬停展开、离开 450ms 收回；开启时不占位 | `DockWindow` 的 `OnWatchTick`（仅开启时运行一个 150ms 定时器） |
| 底栏右键菜单 | ✅ 底栏与顶栏各有右键菜单（进设置 / 隐藏） | `BuildContextMenu()` |
| 底栏拖拽排序 | ✅ 已实测（真实鼠标事件，Edge 从第 0 位拖到第 2 位并落盘） | `DockWindow.OnHookLeftMove/Up` + `ReorderDock` |
| 顶栏通知计数真实来源 | ✅ 已接：注册表各应用 `LastNotificationAddedTime` 与已读时间比较 | `NotificationService.CountSince`，点击打开通知中心并标记已读 |
| 顶栏输入法精确转换态 | ✅ 已实测：读 `ImmGetDefaultIMEWnd` + `IMC_GETCONVERSIONMODE` 的 `IME_CMODE_NATIVE` 位，中/EN 均与独立探测一致 | `Services/ImeStateWatcher.cs` |
| 顶栏悬停 / 按压反馈 | ✅ 已实测：段高亮 110 ms 淡入、170 ms 淡出；按下缩到 0.96，90 ms 后 150 ms 回弹 | `StatusBarWindow.HoverAt / PressPulse` |
| 顶栏胶囊展开 | ✅ 已实测：悬停胶囊底色提亮并纵向放大 7%（约 26→28 px，仍在 32 px 条高内），落在段间留白也触发 | `StatusBarWindow.HoverAt` + `CapsuleVisual` |
| 顶栏底盘材质 | ✅ 已实测四档：跟随系统（亚克力）/ 半透明 / 纯色 / 渐变，另可选底色与渐变第二色；截图 `s14-samples/topbar-solid.png`、`topbar-gradient.png` | `AppSettings.StatusBarBackdropMode` + `StatusBarWindow.ApplyBackdrop` |
| 顶栏鼠标移动输入 | ✅ 新增非消费式 `Moved` 观察者：钩子线程先矩形粗筛、位移 < 2 px 丢弃，再投递 UI 线程做命中判定 | `DockInputHook.Moved` |
| 底栏/磁贴图标提取（非 exe） | ✅ 修好：`IID_IShellItemImageFactory` 写错成 `BC14D7C4-…`，`SHCreateItemFromParsingName` 恒返回 `E_NOINTERFACE`，导致 .lnk / 文件夹 / .url 全部回落到"?"字形；改成 `bcc18b79-ba16-442f-80c4-8a59c30c463b` 并在取图线程显式 `CoInitializeEx(MTA)` 后正常 | `Services/IconExtractService.cs` |
| 底栏拖入任意文件 | ✅ 除 exe/lnk/文件夹/url 外，文档、图片等也以"默认程序打开"加入（图片直接取自身缩略图做图标） | `DockWindow.AddDroppedFiles` |
| 底栏点击切换（连点=开关） | ✅ 目标已占前台就收起，否则激活/启动；对应 MiniDock 的 `clickToggle` | `LaunchService.ToggleAsync` + `TrayAppService.Toggle` |
| 底栏点击不再模拟托盘图标（2026-11-18） | ✅ 新增"显示隐藏窗口"快路径，并给底栏一个开关控制是否允许托盘唤醒（默认关）。实测隐藏 Notepad 后点击图标，3 ms 命中隐藏窗口、约 0.3 s 恢复到前台；Edge 只剩后台进程时 0.2 s 直接重开入口。此前要等 4–5 s、还会弹出通知区溢出面板并移动鼠标 | `LaunchService.RevealHiddenWindowAsync` + `LaunchOrActivateAsync(…, allowTrayWake)` + `AppSettings.DockTrayWake` + `DockSettingsPage` |
| 托盘应用登记：窗口列表挑选 | ✅ 右键底栏 →「登记托盘应用…」枚举全部可见顶层窗口供选择，不必先切前台；顶栏菜单里也有同样入口 | `TrayAppService.EnumerateAppWindows` / `DockWindow.ShowTrayPicker` |
| 托盘应用作为底栏条目 | ✅ 登记且进程存活的应用自动追加到底栏末尾（`LaunchKind.Tray` 合成条目，不写 items.json、不参与拖拽排序） | `DockWindow.RebuildAsync` |
| 托盘登记从磁盘恢复后不重绑窗口 | ✅ 修好：`Refresh` 原先在进程句柄为空时直接 `continue`，读回的登记项永远找不到窗口；现在改为回落到 `FindWindow` 重新 `OpenProcess` 绑定 | `TrayAppService.Refresh` |
| 底栏改为浮层（默认不占位） | ✅ `DockReserveSpace` 默认 `false`：不注册 AppBar，位置 = 工作区底边上方 8 DIP，仍在 `HWND_TOPMOST`；实测工作区 `0,64–2560,1504`（只被顶栏占 64 px），最大化窗口可延伸到 1517 并被底栏覆盖 | `Models/AppSettings.cs` + `DockHost.Relayout` |
| 移除三个底栏选项（2026-11-17 用户要求） | ✅ 删除「占用屏幕空间」「图标倒影」「每个窗口独立图标」：`AppSettings` 去掉 `DockReserveSpace / DockReflection / DockSeparateWindows`，`DockHost` 不再为底栏注册 AppBar，`DockWindow` 去掉倒影与逐窗槽位（固定"一应用一图标"），设置页只剩 7 个开关 | `AppSettings` / `DockHost` / `DockWindow` / `DockSettingsPage` |
| 运行指示看不出效果 | ✅ 四个原因全修：① `.lnk` 条目在 `ProcessNameOf` 里直接返回空，永远判成未运行；② 指示点建槽时算一次、之后不刷新；③ **设置项下标与渲染语义错位**——设置页写的是「圆点/横条/不显示」，代码里 0=隐藏、2=横条，选"圆点"反而全灭；④ 圆点放到图标框外不会被布局绘制。现在下标统一为 0=不显示 / 1=圆点 / 2=横条，1 秒扫描，圆点/横条画在图标下沿内侧 | `AppWindows` + `DockWindow.ScanTick` + `DockSettingsPage` |
| 运行指示语义改为"有窗口" | ✅ 之前按**进程存活**判定，Edge/Steam 关掉窗口后后台进程还在，指示一直亮着；改成按**可见顶层窗口**（含最小化）判定，窗口全关后约 2.5–3 秒熄灭（保留期，见下一节） | `DockWindow.UpdateIndicators` + `AppWindows.Enumerate` |
| 运行指示更灵敏、不闪烁（2026-11-18） | ✅ 原来每槽位各自 `AppWindows.IsRunning()` → 每个点都重新枚举全部窗口、逐个 `Process.MainModule` 读路径（慢，读不到就闪灭），指示 3 秒 / 动态条目 2 秒两条定时器互相独立。现在改成 **1 秒一次窗口快照**（`QueryFullProcessImageName` 读路径，单进程只查一次），同一份快照同时驱动指示点与动态条目；点亮立即生效、熄灭带 2.5 秒保持，动态图标新增立即出现、消失带 4 秒保持。实测图标 0.4 s 出现、0.6 s 点亮，最小化后 8/8 帧保持点亮，空闲 6 秒 0 次闪烁，快速隐藏/显示 10 次 0 次跳变 | `AppWindows.Enumerate` + `DockWindow.StartScanWatch / ScanTick / UpdateIndicators / CheckRunningAppsAsync` |
| 运行指示位置 | ✅ 从"压在图标下沿"改为**图标下方留 3 DIP 间隙**：图标列高度 = 图标 + 指示条 + 间隙，条高同步按 `ceil((图标+指示条)×1.18)+12` 推导，避免放大时被裁 | `DockWindow.CreateSlotAsync / ApplySettingsAsync` |
| 未固定程序自动进底栏 | ✅ 运行监视每 1 秒枚举一次可见窗口（与运行指示共用同一份快照），按 exe 去重后把"没固定过的程序"追加到末尾（最多 4 个，总槽位上限 16），窗口关闭后保留 4 秒再移除；`.lnk` 固定项先解析真实 exe 做去重，系统外壳/宿主进程（`ApplicationFrameHost`、`steamwebhelper`、`TextInputHost` 等）已列入黑名单。实测 Notepad 约 0.4 秒出现、关闭后约 4 秒消失（`s14-samples/dock-run-open.png`、`dock-run-closed.png`） | `DockWindow.StartScanWatch / CheckRunningAppsAsync` + `AppWindow.ExePath` |
| 底栏圆角与窗口圆角对齐 | ✅ `Bar.CornerRadius` 从 `条高/4`（≈18 DIP）改为常量 8 DIP，与窗口 DWM 圆角一致；原先两层圆角不重合，四角会露出"窗口亚克力边 + Bar 细线"两圈弧（截图对比 `s14-samples/dock-corner2.png` 与 `dock-corner-fixed-zoom.png`） | `DockWindow.ApplySettings` + `DockWindow.xaml` |
| 悬停放大不再被裁切/遮挡 | ✅ 根因是 **`Border` 会把子内容裁到内容区（padding 以内）**：图标条原来放在 `Bar` 里，两端的图标一放大就被 `Bar` 的内容区切掉。改成 `Bar` 只画背景/描边、`ItemsPanel` 做它的兄弟节点并自己带 `Margin`，放大溢出不再被裁。另加两条保险：放大元素 `Canvas.SetZIndex(100)`；水平内边距按 `基础 14 + ceil(图标×(1.18-1)/2)` 留出溢出量 | `DockWindow.xaml` + `DockWindow.OnHookHover / UpdateMagnify` |
| 条高改为由图标推导（2026-11-18） | ✅ 去掉「条高」设置项与 `AppSettings.DockHeight`：条高固定 = `ceil((图标+运行指示占位)×1.18) + 上下内边距 6×2`。上下内边距从 10 缩到 6，默认图标 40 时条高从 152 px 降到 **132 px**（2560×1600 / 200%），图标 56 时自动变 **168 px**。旧配置里的 `DockHeight` 字段读取时直接忽略，不再需要"条高下限"校正 | `Models/AppSettings.cs` + `DockWindow.ApplySettingsAsync` + `DockSettingsPage.AddDockSizeRows` + `SettingsService.Load` |
| 底栏外观主题（2026-11-18） | ✅ 新增 7 档预设：深色玻璃 / 浅色玻璃 / 纯色·炭黑 / 纯色·藏蓝 / 渐变·暮紫 / 渐变·海蓝 / 高透·极简。一个 `DockTheme` 索引同时决定底板（纯色或斜向渐变）、顶部高光强度、图标圆角底盘和窗口明暗；浅色档会切 `ElementTheme` 并让 `AlwaysActiveAcrylicBackdrop` 跟着换亚克力底色。设置页「底栏 → 外观主题」点一下循环切换、立即生效 | `Models/DockTheme.cs` + `AppSettings.DockTheme` + `DockWindow.ApplyTheme` + `DockSettingsPage` + `AlwaysActiveAcrylicBackdrop` |
| 去掉整圈描边，改高光收边（2026-11-18） | ✅ 原来 `Bar` 有一圈 1 DIP 的半透明描边，200% 缩放下是 2 物理像素的硬线，四角还会和 DWM 圆角叠成两圈弧。现在 `BorderThickness=0`，加一层 `Sheen`：顶部高光（按主题给 alpha）向下渐隐到 22%，底部再压一道 8% 黑内阴影，靠明暗而不是线条收边——和 macOS 菜单栏 / 原仓库"描边可选、默认很淡"的思路一致 | `DockWindow.xaml` + `DockWindow.ApplyTheme` + `Models/DockTheme.cs` |
| 最左系统「开始」图标（2026-11-18） | ✅ 固定 0 号槽位（四格 Windows 标志，配色随主题）：替代开始菜单模式下点它显示/收起 GameLocker（`MainWindow.ToggleStartMenu`），否则 `keybd_event(VK_LWIN)` 打开系统开始菜单（同 MiniDock 的 `mdock:start` 做法）。拖拽/右键/预览/运行指示都单独跳过它 | `DockWindow.CreateStartSlot / OpenStartMenu` + `MainWindow.ToggleStartMenu` |
| 增删图标有动画（2026-11-18） | ✅ 新程序进底栏：图标从 0.5 倍弹性放到 1 倍（0.7 处 1.05 过冲）+ 淡入，约 300 ms；程序关闭：先缩小到 0.35 倍 + 淡出约 150 ms，再重建，不再"啪"一下出现/消失。动画用 Composition，播完 `StopAnimation`，不留常驻动画（否则会盖掉悬停放大）。另外：动态条目的顺序改成"首次出现"稳定排序（之前跟窗口枚举顺序走，重建时会互相跳位）、图标加内存缓存（重建时不重新解码，其他图标不闪白） | `DockWindow.AnimateSlotsIn / AnimateSlotsOutAsync / AnimateSlot` + `_dynamicOrder` + `IconCache` |
| 窗口预览改成可交互（2026-11-18） | ✅ 原来只显示缩略图、点不了。现在每张卡片带窗口标题和右上角 ×：**点缩略图激活那扇窗**（最小化先还原）、**点 × 或中键关闭窗口**、悬停有高亮边框；最小化的窗口也纳入预览；悬停 220 ms 才弹，鼠标移到卡片上（含图标到卡片之间的缝隙）不会收起。窗口按 DPI 缩放、圆角裁剪，最多 6 张（3 列） | `DockPreviewHost`（重写：DWM 缩略图 + GDI 标题/关闭/hover 命中）+ `DockWindow.OnHookHover / ShowPreviewFor` |
| 手牌动画提速 + 开始按钮添加卡片 + 底栏小组件（2026-11-20） | ✅ **动画**：卡面（渐变/快照/图标/标题）按 1.3 倍烘焙成位图，动画每帧只做一次 `DrawImage`，不再逐帧重绘文字和窗口图；换卡快照改成后台 `PrintWindow`（版本号丢弃过期任务），不再卡住 UI 线程导致起步一顿；`SetWindowPos` 只在本次显示首帧做一次；插值改为双线性缩小。**开始按钮**：悬停（窗口预览设置关掉也生效）弹出两张添加卡片——**自定义**（程序/文件夹/网址，打开 AddItemPage）和**小组件**（打开 WidgetPage）；点击即进对应页面，不要求双击。从底栏加的东西保存时标 `LauncherHidden`，只进底栏并立刻 `DockHost.ApplyAsync` 重绘图标。**底栏小组件**：`IsDockItem` 只接受 `LauncherHidden` 的 Widget，图标按组件类型画 Segoe MDL2 字形；点击转给 `MainPage.ActivateWidgetItem`，和主页磁贴同一套触发语义（音乐/天气/性能面板/系统等） | `DockPreviewHost`（Face 缓存 + 异步快照 + `ShowAddCards` + 命令卡点击）+ `DockWindow.ShowStartAddCards / OnStartAddCardPicked / WidgetGlyph / IsDockItem` + `AddItemPage.PendingDockAdd` + `WidgetPage.PendingDockAdd` + `MainWindow.ActivateDockWidget` |
| 底栏组件最多 5 个 + 同风格图标（2026-11-20） | ✅ 只有从开始按钮"小组件"卡片添加的组件（`LauncherHidden`）才进底栏，最多 5 个；加到第 6 个时用悬浮提示"底栏最多放 5 个组件，请先长按删除一个"拦住，WidgetPage 保存时也兜底。所有底栏图标统一成开始图标的比例：**深色圆角底盘 + 56% 内容**（开始四格、应用原 logo、组件按类型取亮色的字形；文件夹/网址字形 52%）。图标间距 12 DIP；分隔条为 `2×28` DIP 两端渐隐短竖条，位置在开始+组件之后。分隔线两侧再额外让出 `10` DIP（左右各 5，通过相邻图标 Margin 实现），锚点/命中/窗口宽度同步补上这段偏移。所有窗口预览卡和添加卡固定灰色三段渐变，不再跟随条目强调色。动画帧改用 `CompositionTarget.Rendering` 跟随合成帧（保留 16ms 计时器兜底），悬停切换后延迟 70ms 再抓 `PrintWindow`，动画期间只更新变换、结束后再补卡面，GDI+ 缩放开到双线性/HighSpeed，避免 300ms 里因重烘焙卡面或后台抓图掉帧。**删除**：手柄选中组件按 Y、触摸长按组件、鼠标按住组件 550 ms，都会把该底栏组件从配置移除并立即重建底栏；鼠标长按用 DispatcherQueueTimer，删除后清 `_pressIndex` 防止抬手再触发一次。**右键菜单**整体删除：`OnHookRightDown / ShowDockMenu / ShowTrayPicker / AddDockItemAsync / AddUrlItem / AddItemToConfig / HideDockItem / SetLauncherHidden` 及 `DockInputHook.RightDown` 全部移除，右键不再弹设置窗口。**从开始图标进入添加页**时只显示支持底栏的 11 种组件：修改器、游戏存档、OptiScaler、回收站、性能控制、下载模式、性能监控、文件传输、屏幕键盘、小黄鸭、屏幕翻译；映射/手柄映射及其余卡片 `Visibility.Collapsed` 并从 `_formControls` 移除。底栏触发的提示走 `FloatingToastService` 桌面 toast（`MainPage.BeginDockAction` 180 秒窗口），性能组件点击恢复为呼出启动器窗口，并在主页里直接打开按 Y 的那块完整性能面板（不再拆独立窗口）；提示仍走桌面 toast | `DockWindow.IsDockItem / RebuildCoreAsync / DeleteDockWidget / StartWidgetHold / OpenPadPreview / OnHookLongPress` + `DockInputHook` + `WidgetPage.CommitWidget / CardKinds / DockSupportedKinds` |
| 预览改成抽屉手牌样式 + 三种入口（2026-11-19） | ✅ 弹层不再是一整块深色面板，也告别"一张张并排的独立卡"：改成和主页抽屉**同一套手牌几何**——一个窗口一张卡，卡宽 200 DIP / 高 225 DIP，相邻卡中心间距 98 DIP 叠压，中间高、两侧低 34 DIP，每偏离中心一张倾斜 2.2°；**默认一张都不弹出**：卡片完整绘制、不做窗口裁切；预览窗口排在底栏窗口正后面，底栏底板自然挡住卡片下半截，看起来就是插在底栏背后。鼠标移到哪张才让哪张从底栏后抬起、转正、放大 1.14 倍，最终卡底距底栏上沿 10 DIP，点一下才激活（先选中再确认，和抽屉手牌一致）。**切换/弹出动画与主页抽屉一致**：300 ms；旋转和邻居推挤（42 DIP）走 CubicEaseOut，缩放和抬起走 BackEaseOut（振幅 0.3，会轻微过冲回弹）；刚落下的上一张卡在动画期间仍压在邻居上。**弹出卡有切换粘性**：鼠标在这张卡的实际显示矩形内移动时不换卡——从卡中间移向右上角 × 的途中横坐标会越过邻居中心，原先会中途换卡导致 × 跑掉、永远点不到关闭。卡面复用抽屉卡三段强调色渐变、12 DIP 圆角、半透明白描边、SemiBold 标题行（窗口图标 + 标题 + 关闭 ×）。弹层改成 `WS_EX_LAYERED` 逐像素透明窗，卡片外完全透明；卡内画面用 `PrintWindow(PW_RENDERFULLCONTENT)` 抓快照（切卡时只刷新当前卡），最小化窗口显示"已最小化"占位。入口统一：**鼠标悬停**（220 ms）、**手柄选中图标后按 Y**（再按 Y 收起）、**触摸长按图标 550 ms**（长按后松手不再触发启动）；B / 点击别处收起固定卡片。原 Y 的条目菜单暂不绑定新键，实现保留 | `DockPreviewHost`（重写为分层窗口 + 手牌布局 + PrintWindow 快照）+ `DockWindow.ShowPreviewFor / OpenPadPreview / OnHookLongPress / HidePreview` + `DockInputHook.LongPress` + `System.Drawing.Common` 包 |

### 点击启动为什么不再走托盘（2026-11-18）

问题：底栏点图标时，若目标只有后台进程、没有**可见**窗口，会去模拟点击通知区托盘图标。实测要 4–5 s，期间会弹出"隐藏的图标"面板、指针被挪走，失败率也不低（Edge 后台模式必失败）。

对照 MiniDock（`minidock.c` `LaunchItem` / `RevealHiddenWindow`）：

| 步骤 | MiniDock | 改动前的 GameLocker | 改动后 |
|---|---|---|---|
| 有可见窗口 | `ToggleApp`：枚举前台相关窗口，激活 / 最小化 | `FindProcessWindow` → `ActivateResultAsync` | 同左 |
| 窗口被隐藏（关到托盘只是 `Hide`） | `RevealHiddenWindow`：`EnumWindows` **不筛可见性**，排除 `WS_EX_TOOLWINDOW`/有 owner/外壳类，要求标题 + 至少 160×100，取面积最大者，`ShowWindow` + `SetForegroundWindow` | ❌ 只枚举 `IsWindowVisible`，隐藏窗口永远找不到 | ✅ 同款规则（`RevealHiddenWindowAsync`） |
| 真的没有窗口 | `ShellExecuteW` 重开入口，交给应用自己单实例激活 | `WakeTrayAsync`：UIAutomation 找托盘图标 → `mouse_event` 点击（必要时先展开溢出面板）→ 等 3 s 仍无窗口才重开 | ✅ 底栏默认直接重开入口（`DockTrayWake=false`），不再碰通知区 |

结论：MiniDock 的做法更好——不依赖托盘图标的可访问性、不动鼠标、不弹面板，且"隐藏窗口"这条路径覆盖了大部分关到托盘的应用。因此我们补上同样的隐藏窗口发现逻辑，并把托盘唤醒降级成底栏设置里的可选项（默认关）；启动器磁贴仍保留原行为，`LaunchOrActivateAsync` 的 `allowTrayWake` 默认值不变。

### 运行指示为什么之前会闪（2026-11-18）

问题：底栏的运行指示点（横条/圆点）反应慢，偶尔自己闪灭再亮。

对照 MiniDock（`minidock.c` `ScanRunning` / `ScanProc`）：

| 方面 | MiniDock | 改动前 | 改动后 |
|---|---|---|---|
| 检测方式 | 一次 `EnumWindows` 把每个条目的 `seen[i]` 一起算出来，`IsAppWindow` 只做窗口属性判断 | 每个指示点各自调 `AppWindows.IsRunning()`，**每个点都完整枚举一遍所有窗口**，再用 `Process.GetProcessById + MainModule` 逐个读进程路径 | 一次 `EnumWindows` 快照，同一份结果同时喂给指示点和动态条目 |
| 进程路径 | `WinIdOf` 用 `OpenProcess + QueryFullProcessImageName` | `Process.MainModule`：提权/受保护进程会抛异常 → exe 名为空 → 该窗口被跳过 → 指示点灭一拍 | `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + QueryFullProcessImageNameW`，失败才回退；同一进程只查一次 |
| 刷新节奏 | `T_SCAN` 2 s 扫一次，状态变了才重绘 | 指示 3 s、动态条目 2 s 两条独立定时器，互不同步 | 1 s 一拍；点亮立即生效，熄灭保持 2.5 s；动态图标新增立即出现、消失保持 4 s |
| 闪烁 | 单次快照不会"半拍"；变化才 `Kick()` 重绘 | 重建/抖动/读路径失败都会让点闪灭，且重建后新点默认隐藏，要等下一拍才补 | 快照 + 保持期；重建后立刻用同一份快照补一次可见性 |

结论：同样采用"一次扫描 + 变化才重绘"，但把节奏从 2 s 提到 1 s、并给熄灭/消失加保持期——比 MiniDock 更跟手，也避免它那种"状态刚好在两拍之间"的观感。

### 实测记录

- 运行指示灵敏度（2026-11-18，脚本逐帧截图判定指示条像素）：启动 Notepad 后约 **0.4 s** 动态图标出现、约 **0.6 s** 指示条点亮；最小化后 8/8 帧保持点亮；空闲 6 秒 20/20 帧点亮、**0 次跳变**；快速 `SW_HIDE`/`SW_SHOW` 每 500 ms 交替 10 次，指示条 **10/10 帧保持点亮、0 次闪烁**；关掉 Notepad 后约 **2.7 s** 熄灭、约 **3.6 s** 图标移除（熄灭保持 2.5 s、移除保持 4 s）。验证脚本 `D:\MyZhou\.setup\verify-indicator.ps1`、`verify-flicker.ps1`。
- 隐藏窗口恢复：启动 Notepad 后把它的窗口全部 `ShowWindow(SW_HIDE)`，再点底栏对应图标；日志 `显示隐藏窗口 Notepad: hwnd=0x1F0B3E` → `激活 … success=True`，从点击到回到前台约 0.3 s，全程没有 `[托盘]` 日志、没有面板弹出（本机 2560×1600 / 200%，2026-11-18）。
- 无窗口重开：Edge 关掉所有窗口只剩后台进程（8 个进程、0 个可见窗口）时点底栏图标，日志 `跳过托盘唤醒，直接重开入口` → `启动 Microsoft Edge: pid=28324`，约 0.2 s 出现新窗口；改动前同一场景要等 4–5 s，且 UIA 找不到托盘图标后还是要重开。
- 占位生效：工作区 `0,0–2560,1504` → `0,64–2560,1360`（上 64 px = 顶栏，下 144 px = 底栏），退出后精确还原。
- 底栏宽度随图标数变化；当前 `items.json` 只有 1 个 App 类条目（Edge），因此底栏只显示 1 个图标——这是真实数据，不是渲染缺陷。
- 完整启动时同时创建 `Game Locker Dock` 与 `Game Locker Status Bar` 两个窗口。
- Explorer 重启：杀掉 explorer 后自动重启，占位仍为 `0,64–2560,1360`，未被清掉；退出后仍精确还原。
- 多图标布局：临时把 items.json 加到 3 个 App 条目，底栏宽度 384px（= (28+3×48+2×10)×2），与预期完全一致；测完已还原原文件（12 条、1 个 App）。
- 通知计数：独立脚本按同样规则算出 5，顶栏渲染也是 5（截图 `docs/s2-samples/topbar.png`）。
- 拖入图标：Steam.lnk / DeepSeek Harness 从"?"变为真实图标（`icons` 下 256×256 PNG，截图 `s14-samples/dock-icons-zoom.png`）。修复点是一处写错的 GUID：`BC14D7C4-91B5-4d09-BE40-7934B755F11E` 并非 `IShellItemImageFactory` 的 IID，取图统一失败；exe 因为先走"内嵌图标"路径才掩盖了问题。
- 点击切换：托盘条目点第一下唤出、第二下收起（日志 `[顶栏] 唤出托盘应用` → `[底栏] 收起托盘应用`，同一 hwnd 0xD0CD0）；普通条目点第一下启动（`_测试记事本: pid=33104`），第二次在前台时走 `Minimize`。测试用的 items.json / tray-apps.json 已还原。
- 浮层不占位：工作区从 `0,64–2560,1360`（旧，含底栏占位）变为 `0,64–2560,1504`；最大化窗口实测矩形 `-13,51–2573,1517`，底栏浮在它上面（截图 `s14-samples/dock-over-maximized.png`）。
- 悬停放大：钩子日志确认 `idx=0` → `UpdateMagnify`；截图测量图标彩色区域从 66 px 增至 78 px（1.18×），四角图标完整不再被邻居或窗口边缘切掉（`s14-samples/hover-fixed-zoom.png`）。
- 条高自动推导（2026-11-18）：图标 40 → 底栏窗口 352×132（条高 66 DIP），图标改 56 → 456×168（条高 84 DIP），图标 96×96 完整不裁；截图 `s10-samples/dock-auto-height.png`。旧配置里的 `DockHeight: 76` 被忽略，未再写回。
- 外观主题（2026-11-18）：7 档逐一启动截图核对，底板/顶部高光/图标底盘/亚克力明暗都按预期变化；对照图 `s10-samples/themes-overview.png`，单档截图 `s10-samples/theme-0.png` … `theme-6.png`（生成脚本 `D:\MyZhou\.setup\capture-themes.ps1`）。边缘特写 `D:\MyZhou\.setup\theme-samples\edge-zoom.png`：四角只剩窗口自身的 DWM 圆角，没有第二圈描边。
- 开始图标（2026-11-18）：非替代模式点一下，前台窗口变成 `SearchHost`（Win11 开始菜单宿主），日志 `[底栏] 开始：打开系统开始菜单`；把 `StartMenuMode` 改为 true 重启后，同一点击先把 GameLocker 主窗隐藏、再点恢复显示（UIA 矩形检测），日志 `[底栏] 开始：切换到 GameLocker`。截图 `s10-samples/dock-start.png`，测完已还原 `StartMenuMode=false`。
- 增删动画（2026-11-18）：脚本对底栏区域按 20–35 ms 连拍、算亮度质量。新程序出现时亮度从基线 30 → 60 → 104 → 140 在约 105 ms 内逐帧爬升（不是一帧跳变），说明缩放/淡入生效；日志同步出现 `[底栏] 图标淡入：X.exe` 与 `[底栏] 图标淡出：X.exe`。验证脚本 `D:\MyZhou\.setup\probe-add2.ps1`、`capture-anim.ps1`。
- 可交互预览（2026-11-18）：悬停 QQ 图标 220 ms 后弹出 440×342（200% DPI 下的 1 张卡片），标题「QQ」+ 右上角 ×，缩略图实时；鼠标移到卡片上出现青色高亮边框（`s10-samples/preview-hover.png`，未悬停时 `preview-card.png`）。点缩略图后日志 `[底栏] 预览激活 hwnd=0x1068E`，QQ 主窗被切到前台、预览自动收起；点 × 后日志 `[底栏] 预览关闭 hwnd=0x270BC0`，该窗口被关闭。测完已关掉测试用的 Notepad。
- 手牌式预览（2026-11-19，2026-11-20 更新）：6 窗口时日志 `[底栏] 预览显示 idx=-1 windows=6 hand=1548x719`；屏幕上出现 6 张叠压、倾斜、中间高两侧低的手牌（200% DPI 下每张 400×450）。预览窗口底边对齐底栏下沿、排在底栏窗口后面，卡片完整绘制、下半截被底栏底板自然挡住。鼠标移到某张后该卡在 300 ms 内抬起、转正、放大并显示 ×，弹出后卡底位于底栏上沿上方 10 DIP；移开后全部回缩。单窗口时 `hand=568x719`，同样默认半藏、移上才完整弹出。动画开始约 130 ms 时连续截图可见卡片处于半抬起状态，说明 BackEaseOut 过冲动画生效。最小化窗口显示"已最小化"占位。开始按钮：日志 `[底栏] 添加卡片显示 hand=764x719`，两张添加卡完整绘制并排在底栏后；程序化选中右侧卡后 300 ms 内放大抬起、出现 3px 白边与关闭 ×。临时加入一个 `LauncherHidden` 的 Widget 后底栏从 3 格变 4 格，图标显示音符字形。切换粘性实测：从左卡中心直接移到右上角 ×（`预览点击 468,140 hot=0`）仍保持同一张卡，点击后日志 `预览关闭 hwnd=0x400CA`。
- 裁切根因验证：临时把放大倍率调到 1.6 复现——修复前左端 Edge 图标的可见宽度是 92 px（正好在 `Bar` 内容区边界 680 处被切平），改成兄弟节点后同一位置变成 106 px（= 33×1.6×2，完整）。随后把倍率改回 1.18。
### 托盘应用为什么不做"镜像托盘图标"

Windows 11 的任务栏托盘已经不是经典 `ToolbarWindow32`：实测 UIA `FindAll(ControlType=ToolBar)`
返回 0 个候选，`SysPager → ToolbarWindow32` 那条老路也拿不到按钮。
MiniDock 的源码里同样有这段注释（"the old code clicked the classic Toolbar32 chevron,
which succeeds on Windows 11 without opening anything"），可见是共性限制。

所以采用和它相同的思路：**登记 + 追踪窗口**，而不是列托盘图标。

- 登记：记下 exe 全路径 + 窗口类名 + 窗口标题（用户把目标窗口调到前台点一下即可）
- 追踪：按类名 `FindWindowEx` 枚举，再用标题和 exe 路径确认；`OpenProcess` 句柄守住 PID 复用
- 唤出：`ShowWindow(SW_RESTORE)` + `SetForegroundWindow`
- 另外给一个"打开系统托盘"按钮，发 Win+B 再 Enter，让 Windows 自己弹浮出窗口

注意：菜单弹层自己会 `Activate()` 抢前台，所以"登记前台应用"必须用**点击顶栏那一刻**
记下的窗口（`StatusBarHit.ForeignWindow`），不能用菜单里的 `GetForegroundWindow()`。

### 文件拖入为什么能成

WinUI 的内容托管在 `Microsoft.UI.Content.DesktopChildSiteBridge` 子窗口里，鼠标命中的是它，
所以 `RegisterDragDrop` 必须注册在**那个窗口**上（注册顶层窗口无效）。

另一个必要条件是**自己调用 `OleInitialize`**：不调用时 `RegisterDragDrop` 返回 `0x8007000E`。
补上之后顶层窗口与桥窗口都注册成功（HRESULT 0），`HDROP` 解析也用同一套内存布局做了往返验证。

### 右键菜单为什么不用 MenuFlyout

`MenuFlyout` 在底栏（`WS_EX_NOACTIVATE`）里弹不出来：`ShowAt` 直接抛异常（消息为空）。
而且钩子回调跑在钩子线程上，WinUI 窗口不能跨线程创建。
所以右键菜单改为：钩子里判定命中 → `DispatcherQueue` 派发到 UI 线程 →
用自有的 `DockPopupWindow`（可激活的普通窗口）做竖排菜单。

### 一个必须先说明的架构发现（输入路径）

实测结论：**WinUI 的指针事件在 `WS_EX_NOACTIVATE`、且从未 `Activate()` 的窗口里不会投递**，
顶层窗口过程的 `WM_LBUTTONDOWN` 也收不到——鼠标命中目标是 WinUI 自建的
`Microsoft.UI.Content.DesktopChildSiteBridge` 窗口。XAML 拖放（`DragStarting/Drop`）同样失效。

因此底栏的点击与拖拽改由 `Services/DockInputHook.cs`（`WH_MOUSE_LL` 全局钩子）接管：
按屏幕坐标判断是否落在底栏矩形内，命中就消费消息并自行处理。
钩子内部**自行记录左键状态**——一旦我们吞掉 `LBUTTONDOWN`，系统不再更新按键状态表，
`GetAsyncKeyState` 会一直返回"未按下"（实测）。自动隐藏的悬停检测仍用光标轮询，
因为窗口大半移出屏外时 XAML 的 `PointerEntered/Exited` 同样不可靠。

另一个实测结论：`EVENT_OBJECT_IME_CHANGE` 在 TSF 输入法按 Shift 切换中/英时**不触发**，
而 `IMC_SETCONVERSIONMODE` 也改不动 Win11 拼音的状态。所以输入法段用 700ms 轮询兜底
（仅在显示输入法段时运行，值不变不重绘），这是本文档"禁止秒级定时器"约束的唯一例外。
- 自动隐藏：工作区保持 `0,0–2560,1504`（不占位）；底栏收起为 `y=1492`（露出 12px），光标进入后展开到 `y=1344`，移开 450ms 后回到 `y=1492`。
- 底栏点击启动：点 notepad 图标后 `notepad.exe` 成功启动（PID 29576），说明钩子路径的"未拖动即点击"分支正确。
- 底栏拖拽排序：用 `mouse_event` 绝对移动做真实拖拽（`SetCursorPos` 不产生低级钩子事件，测不出来），Edge 从第 0 位拖到第 2 位，`items.json` 同步变为 `AAA-Notepad | BBB-Calc | Microsoft Edge`；测完已还原原文件。
- 顶栏输入法：按一次 Shift 后转换态由 `0x401`(中) 变为 `0x0`(EN)，顶栏同步显示 `EN`，还原后回到 `中`（截图 `docs/s3-samples/`）。

---

## 15. 附：不借鉴的部分

MiniDock 里这些**不打算做**：

- 启动台 + 搜索引擎（启动器本身已是全屏面板）
- 控制中心面板（本期顶栏只显示不控制，见 D7）
- 系统托盘镜像、任务栏通知区接管
- 弧形展开、DWM 窗口缩略图
- 多显示器逐屏镜像
- 单文件 GDI+ 手绘渲染（改用 WinUI，见 D3）

- 卡片选卡模式（2026-11-21）：手柄 Y / 触摸长按弹出卡片后进入选卡模式，←/→ 或 LB/RB 在卡片间移动（第一次选中会把卡片弹出来），A 打开当前卡；鼠标/触摸同样两步，第一次点只选中弹出，点同一张第二次才打开。底栏图标上的 A 等同鼠标单击：没选中时直接触发第一格，不再需要按两次。B/Y 收起卡片后选中框回到弹出卡片的那一格，不会连图标选中一起消失。实现：`DockPreviewHost.Show(selectionMode)` + `SelectRelative/ActivateSelected` + `DockWindow.HandleGamepad/ActivatePadSelection`；选卡模式下预览窗不再用悬停自动换卡。

- 底栏固定行为（2026-11-21）：设置页删除「悬停显示窗口预览 / 无窗口时用托盘唤醒 / 点击文件夹展开内容 / 展开弧形角度 / 底栏所在屏幕 / 指定屏序号 / 显示运行指示 / 运行指示样式 / 运行指示大小 / 运行指示颜色 / 手柄操作底栏」全部可调项，`AppSettings` 对应属性改为固定 getter：预览开、托盘唤醒关、文件夹展开开、弧形 90、主屏、横条指示、大小 3、绿色 #7BD88F、手柄开。
