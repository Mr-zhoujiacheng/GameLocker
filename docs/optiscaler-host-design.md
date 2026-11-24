# OptiScaler 插帧宿主接入设计（替换 FGHost）

| 项 | 内容 |
|---|---|
| 版本 | v0.1（草案） |
| 日期 | 2026-11-23 |
| 状态 | **仅设计，不开发**；待拍板项见 §15 |
| 目标 | 移除 FGHost，把 OptiCaptureHost 整体搬进 GameLocker；插帧引擎收敛为「小黄鸭（LSFG）」与「OptiScaler」两条；选 OptiScaler 时暴露对应参数；实现一键插帧 |
| 参考仓库 | [tegraultra/OptiCaptureHost](https://github.com/tegraultra/OptiCaptureHost)（GPL-3.0，v2.10.70） |
| 关联代码 | `Services/LosslessScalingService.cs`、`Models/LaunchItem.cs`、`AddItemPage.xaml(.cs)`、`WidgetPage.xaml.cs`、`MainPage.xaml.cs`、`Services/TileStyle.cs`、`Services/ForegroundWatcherService.cs`、`Services/MonitorService.cs`、`Services/ConfigService.cs`、`GameLocker.csproj`、`GameLocker.iss` |

---

## 0. 摘要（TL;DR）

1. **搬运对象**：OptiCaptureHost 的宿主程序（捕获 + D3D12 覆盖层 + 输出超分 + 呈现节拍）与其运行配置；它的插帧能力来自同目录的 OptiScaler（`dxgi.dll`），不来自宿主自身。
2. **引擎模型**：`LSFG` 保持不变；删除 `XESS` / `FSR3`，新增 `OPTISCALER`。旧配置迁移到 `OPTISCALER` 的默认参数。
3. **参数模型**：UI 参数分三层落地——`LaunchItem` 字段 → `OptiCaptureHost.ini`（宿主节拍/超分/限帧）→ `OptiScaler.ini`（插帧模式/倍率/上采样器/VSync/热键）。
4. **一键插帧**：A 键 = 启动宿主（插帧开）；再按 A = `WM_CLOSE` 优雅停止宿主（插帧关）。状态由 `OptiCaptureHost.status` + 进程存活 + 日志判定。
5. **合规边界（硬约束）**：OptiCaptureHost 与 OptiScaler 均为 **GPL-3.0**。GameLocker 保持 MIT 的前提是——GPL 组件**独立目录、独立进程、独立许可与源码提供**，MIT 主体不吸收其代码。三种分发方式见 §4。
6. **一期不做**：HDR 处理、超分自动改游戏窗口大小、RTSS 限帧接管、OptiScaler 菜单内嵌 UI。
7. **游戏内面板（追加需求）**：需要在不退出游戏的情况下打开 OptiScaler 面板改参数。宿主已有「聚焦覆盖层 + 注入菜单键」的机制，但**菜单开关状态不外露**；要做成可靠的一键进出，建议给宿主加一个很小的 IPC 补丁（§17）。

---

## 1. 背景与目标

### 1.1 现状

当前「小黄鸭」组件的插帧引擎有三条路：

| 引擎 | 实现 | 入口 |
|---|---|---|
| LSFG | 外部商业软件 Lossless Scaling：检测/启动/最小化 → 写 `Settings.xml` → 切回游戏 → 发 `Ctrl+Alt+S` → PresentMon 校验 | `LosslessScalingService.ActivateAsync`（L440） |
| XeSS FG | 自带 `FG/FGHost.exe`（Magpie 改造版，XeFG） | `ActivateMagpieAsync`（L545） |
| FSR3 FG | 同上（FSR3 FG + AMD/NVIDIA 硬件光流） | 同上 |

FGHost 的接入面包括：`[state]` 状态管道、错误码 6/7、Presenter PID、初始化取消、前台窗口排除、FPS 监控、Toast 文案。

### 1.2 目标

- 用 OptiCaptureHost 取代 FGHost 作为「内置插帧」后端；
- 引擎只剩两条：**小黄鸭（LSFG）** / **OptiScaler**；
- 选 OptiScaler 时提供完整参数（插帧模式、倍率、输出超分、呈现、VSync、延迟等）；
- 保持「A 一键插帧」的交互心智不变；
- 游戏运行中可打开 OptiScaler 面板改参数并返回游戏（§17）；
- 合规可发布。

### 1.3 非目标（一期）

- 不做 HDR 管线（参考宿主未处理 HDR）；
- 不自动修改游戏窗口尺寸做输出超分（参考 launcher 会 `ResizeGameWindow`，风险高，见 §7.5）；
- 不接管 RTSS 限帧（GameLocker 已有 `FrameLimitService`）；
- 不把 OptiScaler 菜单嵌进 GameLocker UI；
- 不做多后端并存（FGHost 二期删除，过渡期可保留开关）。

---

## 2. 现状盘点（接入点清单）

| 文件 / 位置 | 现状职责 | 本次动作 |
|---|---|---|
| `Services/LosslessScalingService.cs` L440 `ActivateAsync` | 一键入口；分派 LSFG / Magpie | 改造：分派 LSFG / OptiScalerHost |
| 同文件 L545 `ActivateMagpieAsync` | 拉起 FGHost、解析 `[state]`、错误码、取消 | **删除**（逻辑迁到新服务） |
| 同文件 L727 `StopMagpie` | 杀 FGHost | 删除 |
| 同文件 L64 `ActiveMagpieGamePid` / L103 `ActiveMagpiePresenterPid` | 供监控/前台判断 | 改为宿主版实现 |
| 同文件 L152 `IsMagpieScaling` / L156 `IsLsfgScaling` | 磁贴状态 | 改为 `IsHostScaling` |
| 同文件 L179 `MinimizeBlocked` | 小黄鸭管理员权限提示 | 保留（仅 LSFG） |
| `Models/LaunchItem.cs` L414–431 | `FrameGenEngine` + `Fg*` 六个参数 | 改：枚举值收敛；新增 `OptiHost*` 参数；旧字段废弃 |
| `AddItemPage.xaml` L1041–1200 | 引擎下拉 + Magpie 参数面板 | 改造：OptiScaler 参数分组 |
| `AddItemPage.xaml.cs` L536–563 / L907–932 / L2101–2128 / L2592–2612 | 表单导航、载入、保存、面板切换 | 改造 |
| `WidgetPage.xaml.cs` L1047–1075 | 添加组件时默认参数 | 改造 |
| `MainPage.xaml.cs` L2368–2377 / L4812–4908 / L4933–4944 / L5172 / L5281 | 磁贴构建、A 键、结果文案、Y 键、选中判断 | 改造 |
| `Services/TileStyle.cs` L1951 / L2187 | 小黄鸭磁贴样式与状态 | 改造：新增宿主状态 |
| `Services/ForegroundWatcherService.cs` L57–85 | 排除 FGHost 输出窗 | 改造：排除 `OptiCaptureHostOverlay`/`OptiCaptureHostLabel` |
| `Services/MonitorService.cs` L523 | 用 Magpie 绑定目标/Presenter | 改造：宿主 PID + 游戏 HWND |
| `Services/FloatingToastService.cs` L41 | 每 400ms 重置顶 | 需评估与宿主 TOPMOST 的冲突 |
| `GameLocker.csproj` L59 | 声明 FG 后端约 157MB | 改造：新目录与新文件清单 |
| `GameLocker.iss` L16 | 缺 FGHost 直接报错 | 改造：校验新宿主 |

---

## 3. 目标架构

### 3.1 进程与数据流

```
┌──────────────────────── GameLocker（C# / MIT）────────────────────────┐
│  AddItemPage ─ WidgetPage ─ MainPage ─ TileStyle                      │
│                                                                       │
│  LosslessScalingService ──(LSFG)──► Lossless Scaling（外部）          │
│                                                                       │
│  OptiScalerHostService ──(CLI / ini / status / WM_CLOSE)──┐           │
└───────────────────────────────────────────────────────────┼───────────┘
                                                            ▼
              %LOCALAPPDATA%\GameLocker\fg-host\   （GPL 组件，独立目录）
              ├─ OptiCaptureHost.exe            ← WGC 捕获 + D3D12 覆盖层
              ├─ dxgi.dll                       ← OptiScaler（GPL）
              ├─ OptiScaler.ini
              ├─ OptiCaptureHost.ini
              ├─ OptiCaptureHost.status         ← 每秒状态（只读）
              ├─ OptiCaptureHost.log            ← 排障
              └─ 运行库（FSR / DLSS / XeSS / DLSS Enabler，按需）
                                                            │
                                            捕获（WGC，只读）
                                                            ▼
                                                  游戏窗口（原样，不注入）
```

要点：

- **游戏始终不被注入/修改**，OptiScaler 只注入宿主进程；
- 宿主自己**不含插帧实现**，插帧由 OptiScaler 在宿主进程内完成（源码注释与 README 均明确）；
- GameLocker 与宿主只通过 **命令行 + 两份 ini + status/log 文件 + WM_CLOSE** 通信，无 SDK、无链接。

### 3.2 目录布局（关键）

| 位置 | 内容 | 原因 |
|---|---|---|
| 安装目录（Program Files，只读） | `GameLocker.exe`、`FG\`（二期清空） | 安装包资产 |
| `%LOCALAPPDATA%\GameLocker\fg-host\` | 宿主 + OptiScaler + 运行库 + 两份 ini + status/log | **宿主把 ini/status/log 写在 exe 同目录**；Program Files 下普通权限写不进去，必须放可写目录 |
| `%LOCALAPPDATA%\GameLocker\fg-host\Game Profiles\` | 每游戏参数快照 | 复用参考 launcher 的 profile 形态（可选） |

开发期保留环境变量 `GAMELOCKER_FG_HOST_DIR` 覆盖目录（替代现有 `GAMELOCKER_FG_DIR`）。

### 3.3 组件边界

| 边界 | 规则 |
|---|---|
| 进程 | 宿主独立进程，GameLocker 不加载其任何 DLL |
| 目录 | GPL 组件集中在 `fg-host\`，与 MIT 代码物理隔离 |
| 许可 | `fg-host\LICENSE`（GPL-3.0）+ `NOTICE`，GameLocker 根 `LICENSE` 仍为 MIT |
| 源码 | 发布物附带宿主对应源码（或书面提供方式），修改需公开 |
| 交互 | 仅 CLI / ini / status / log / WM_CLOSE，不共享内存结构体 |

---

## 4. 许可与合规（前置决策）

> 这一节不是建议，是「完全搬运」能否发布的前提。

### 4.1 各组件许可

| 组件 | 许可 | 义务 |
|---|---|---|
| OptiCaptureHost | **GPL-3.0** | 分发二进制须提供对应源码；修改须以 GPL 公开；保留版权与许可 |
| OptiScaler（janblade fork） | **GPL-3.0** | 同上；`dxgi.dll` 改名不改变许可 |
| FSR 1 / NIS shader、ffx 头文件 | MIT | 保留声明 |
| AMD FidelityFX 运行库 | AMD 许可 | 按 SDK 条款分发，不修改 |
| NVIDIA DLSS | NVIDIA RTX SDK 许可 | 只能随应用整体分发、不得单独分发、保留许可文本 |
| Intel XeSS | Intel Simplified Software License | 二进制、不修改 |
| DLSS Enabler（headless） | 作者授权页 | **禁止修改、禁止售卖**、需署名 |

### 4.2 三种搬运方式

| 方案 | 做法 | GameLocker 许可 | 用户体验 | 结论 |
|---|---|---|---|---|
| **A. 用户自备** | 不随包分发；用户指定宿主目录（类似 DittoSR） | MIT 干净 | 差：要自己找文件 | 合规最省，产品最差 |
| **B. 独立 GPL 组件随包**（推荐） | `fg-host\` 整目录 GPL，附源码/许可/修改记录；独立进程调用 | MIT 主体干净（聚合，非衍生） | 开箱即用 | **推荐** |
| **C. 并入主程序** | 把 C++ 源码编进/链进 GameLocker | 必须整体 GPL-3.0，MIT 不再成立 | 开箱即用 | 仅当你决定全项目转 GPL |

### 4.3 若选 B 的落地要求

1. `fg-host\` 内放 `LICENSE`（GPL-3.0 全文）、`NOTICE.md`（上游版权 + 你的修改说明）、`SOURCE.txt`（源码地址/版本/commit）。
2. 仓库内保留 `third_party\opticapturehost\` 源码目录（或 submodule 固定 commit），构建脚本可复现。
3. 对宿主做任何补丁（见 §15 决策 D2）都要在 `SOURCE.txt` 标注并公开补丁源码。
4. DLSS Enabler 若随包，删除「售卖」路径；如 GameLocker 未来收费，必须移除该文件并改为用户自备。
5. 发布页/关于页写明「本产品包含 GPL-3.0 组件，源码见 …」。

### 4.4 结论

**「完全搬运」可行，但只能按 B 做**：MIT 主体 + 独立 GPL 宿主组件。若坚持 C，则 GameLocker 需整体改为 GPL-3.0，且所有分发物都要附源码——这会改变项目性质，需你明确确认。

---

## 5. 搬运方案

### 5.1 源码与版本

- fork `tegraultra/OptiCaptureHost` 到你自己的仓库（或固定 upstream commit），记录 `version.h` 版本（当前参考为 v2.10.70）。
- **不要跟随 upstream 自动升级**：它的 `OptiScaler.ini` 键位、FG 模式编号、present mode 行为都可能变；升级走「回归测试 + 改映射表」。
- 只搬 **OptiCaptureHost.exe**（宿主）。`OptiCaptureLauncher.exe` 作为排障工具保留在 `fg-host\tools\`，不进入用户流程（它的职责由 GameLocker 接管，见 §5.4）。

### 5.2 构建

- 环境：Windows + VS2022（Desktop C++）+ CMake 3.20+，x64。
- 命令：`cmake -S . -B build -A x64 && cmake --build build --config Release`。
- DLSS 头文件不随仓库：本地构建可无 DLSS（宿主日志会注明）；CI 可下载（参考仓库的 `build.yml` 做法）。
- 产物：`OptiCaptureHost.exe`（+ pdb 可选）。
- 建议在 GameLocker 仓库加一个 `build-fg-host.ps1` 与 CI job，产物落到 `fg-host\`。

### 5.3 目录与文件清单（随包）

| 文件 | 必需 | 说明 |
|---|---|---|
| `OptiCaptureHost.exe` | ✅ | 宿主 |
| `OptiScaler.ini` | ✅ | OptiScaler 配置（**必须用与该版本匹配的模板**） |
| `dxgi.dll` | ✅ | OptiScaler 本体改名 |
| `amd_fidelityfx_dx12.dll` | 条件 | FSR3/4 输出超分；缺失回退 FSR1 |
| `nvngx_dlss.dll` | 条件 | DLSS 输出超分 |
| `dlss-enabler-headless.dll` | 条件 | DLSSG / Combo / Nukem / Nvidia MFG 路径 |
| Intel XeSS / XeFG 运行库 | 条件 | Intel MFG / Intel FG 模式 |
| `DittoSR\` | 可选 | DittoSR 上采样器 |
| `nvngx_dlssnr.dll` | 可选 | DLSS 5（版本 310.9.16，自行获取） |

> 与现有 FGHost 目录（FSR/XeSS 运行库约 157MB）**不是同一套文件**，不能简单复用；体积不会更小（见 §11）。

> 版本注意：宿主配套的是 **janblade 的 OptiScaler fork**（含 DLSS 5 / NR / FG 相关特性），与现有「OptiScaler 小组件」下载的官方包不是同一套；混用会出现 `OptiScaler.ini` 键不匹配。建议宿主目录内固定一份 fork 版本，独立于游戏注入用的包。

### 5.4 参考 launcher 的职责迁移

GameLocker 不走 `OptiCaptureLauncher.exe`，需要自己实现它做过的事：

| 优先级 | launcher 职责 | 迁移到 |
|---|---|---|
| P0 | 写 `OptiCaptureHost.ini`（FG 模式/倍率/超分/呈现/VSync/延迟） | `OptiScalerHostService.Prepare()` |
| P0 | 写 `OptiScaler.ini`：`[FrameGen] FGOutput/FGNvngxReplacement`、`[XeFG]/[DLSSG] InterpolationCount`、`[Upscalers] Dx12Upscaler`、`[V-Sync]`、`[Menu]` 热键、`[Framerate] FramerateLimit` | 同上 |
| P0 | `--hwnd/--parent/--gpu` 启动、`WM_CLOSE` 停止、status/log 读取 | `OptiScalerHostService` |
| P1 | 首次准备：检查/下载/复制 OptiScaler 与运行库 | `OptiHostProvisioner`（可复用现有 OptiScaler 下载逻辑） |
| P2 | RTSS 限帧、OSD 隐藏 | 交给 `FrameLimitService`（不与宿主 [Limiter] 同时开） |
| P2 | 输出超分时改游戏窗口大小（`ResizeGameWindow`） | 一期不做 |
| P2 | 每游戏 profile 四槽、实时面板、DLSS 5 面板 | 二期/不做 |

### 5.5 安装包改动

- `GameLocker.iss`：删除 `FG\FGHost.exe` 校验；新增 `fg-host\` 文件清单与许可文件；安装后首次运行复制到 `%LOCALAPPDATA%`（或直接安装到 LocalAppData）。
- `GameLocker.csproj`：L59 的 FG 注释与 `Content` 项改为新目录。
- 卸载：清理 `%LOCALAPPDATA%\GameLocker\fg-host\`（保留用户参数可选）。

### 5.6 「完全搬运」的范围界定

| 搬 | 不搬 | 原因 |
|---|---|---|
| 宿主全部源码能力：WGC 捕获、D3D12 覆盖层、输出超分、呈现节拍、焦点伪造、热键、status/log | — | 这些是宿主价值本体 |
| launcher 的参数映射逻辑（→ C# 重写） | launcher 的 GDI+ 界面 | 与 GameLocker UI 重复；用户只应看到一个界面 |
| profile 机制（二期） | 实时面板、DLSS 5 面板 | 优先级低；面板依赖 nvngx_dlssnr 等受限文件 |
| 首次准备/版本固定策略 | RTSS 限帧、OSD 隐藏 | GameLocker 已有 `FrameLimitService`，避免双份 |
| 依赖检查与降级策略 | 第三方运行库的「无条件再分发」 | 许可限制（DLSS Enabler / NVIDIA / Intel） |

结论：用户视角是「GameLocker 里多了一个 OptiScaler 引擎」，不是「多了一个 launcher」。宿主对用户不可见（只有覆盖层），所有配置留在 GameLocker 编辑页。

---

## 6. 引擎与数据模型

### 6.1 引擎枚举

| 值 | 显示名 | 实现 |
|---|---|---|
| `LSFG` | 小黄鸭（Lossless Scaling） | 现有 `LosslessScalingService` |
| `OPTISCALER` | OptiScaler（内置宿主） | 新 `OptiScalerHostService` |
| ~~`XESS`~~ | — | 迁移到 `OPTISCALER` |
| ~~`FSR3`~~ | — | 迁移到 `OPTISCALER` |

### 6.2 LaunchItem 字段变更

保留（LSFG 用）：

- `LosslessPath`、`LosslessProfile`、`LosslessOptimize`、`LosslessOpt*`

废弃（Magpie 专用）：

- `FgCaptureMode`、`FgOpticalFlow`、`FgFrameSync`、`FgXeSSMultiplier`（迁移后清理；JSON 里残留字段反序列化会被忽略）

复用：

- `FgGpuIndex/VendorId/DeviceId` → 宿主的 `--gpu`

新增（建议用一个嵌套对象，减少字段膨胀）：

```json
"OptiHost": {
  "FgMode": 5,            // FrameGenMode：见 §7.2
  "MfgCount": 0,          // 0=auto，1..5=2x..6x
  "Upscale": 0,           // 0 关 / 1 Bilinear / 2 FSR1 / 3 NIS / 4 FSR3-4 / 5 DLSS
  "UpscaleLevel": 0,      // 0 Quality / 1 Balanced / 2 Performance / 3 Native
  "UpscalerMode": 4,      // 0 xess / 1 ffx / 2 dlss / 3 dlss3.7 / 4 fsr4 / 5 DittoSR
  "PresentMode": 6,       // 0..6，见 §7.3
  "PresentDelayMs": 3,    // 0..12，实时生效
  "VSyncMode": "auto",    // auto / on0 / on1 / off
  "Boost": 2,             // 0..3
  "Sharpness": 25,        // FSR1/NIS 共用旋钮（0..200 / 0..100）
  "MenuKey": 45           // Insert；仅高级模式暴露，写入时转十六进制
}
```

> 序列化说明：`ConfigService` 用 `System.Text.Json`，新增属性对旧 `items.json` 缺省即默认值；迁移在 `Load()` 里加一次性函数（参照现有 `MigrateTileSets` / `MigrateClashName`）。上面代码块为**带注释的示意**，实际存储时去掉 `//` 注释。

### 6.3 迁移规则

| 旧值 | 新值 |
|---|---|
| `FrameGenEngine = XESS` | `OPTISCALER` + `FgMode=0`（Intel MFG）+ `MfgCount = FgXeSSMultiplier - 1` |
| `FrameGenEngine = FSR3` | `OPTISCALER` + `FgMode=7`（FSR3-4 FG）+ `MfgCount=0` |
| `FrameGenEngine = LSFG` | 不变 |
| 其他/空 | `LSFG` |

迁移后写回 `items.json`，并把 `FgCaptureMode/FgOpticalFlow/FgFrameSync/FgXeSSMultiplier` 清空/忽略。

### 6.4 能力矩阵

| 能力 | LSFG | OPTISCALER |
|---|---|---|
| 插帧倍率 | 小黄鸭内配置 | 2×–6×（按模式） |
| 输出超分 | 无（小黄鸭自带缩放，不可控） | DLSS / FSR3-4 / FSR1 / NIS / Bilinear |
| 呈现/延迟调节 | 写 LSFG 队列参数 | PresentMode + PresentDelay + VSync |
| 一键 | ✅ | ✅（启停宿主） |
| 依赖 | Lossless Scaling（付费、Steam） | 宿主 + OptiScaler + 运行库（随包/自备） |
| HDR | 由小黄鸭处理 | 无（一期） |

---

## 7. 参数体系（核心）

### 7.1 三层结构

```
GameLocker 编辑页
   └─ LaunchItem.OptiHost.*（持久化）
        ├─ OptiCaptureHost.ini（宿主节拍/超分/限帧/热键）
        └─ OptiScaler.ini（插帧模式/倍率/上采样器/VSync/热键/限帧）
```

写入时机：**每次一键启动前重写**（宿主只在启动时读大部分键，运行中仅少数键实时生效）。

### 7.2 基础参数（必须暴露）

| UI 名称 | 取值 | 写入位置 | 生效 | 默认 | 备注 |
|---|---|---|---|---|---|
| 插帧模式 | 见 §7.4 表 | `[Settings] FrameGenMode` + `[FrameGen] FGOutput/FGNvngxReplacement` | 启动时 | 5（Combo） | 决定 GPU 兼容性与倍率上限 |
| 插帧倍率 | 自动 / 2×–6× | `[Settings] MfgCount` + `[XeFG]` 或 `[DLSSG] InterpolationCount` | 启动时 | 自动 | 仅模式 0/1/5/8 有效 |
| 输出超分 | 关 / Bilinear / FSR1 / NIS / FSR3-4 / DLSS | `[Upscale] Method` | 启动时 | 关 | 超分不改游戏窗口时是「画面放大」，见 §7.5 |
| 画质档 | Quality / Balanced / Performance / Native | `[Upscale] Level` | 启动时 | Quality | Native 只做 AA/锐化 |
| 锐化 | 0–100（NIS）/ 0–200（FSR1） | `[Upscale] NisSharpness` / `[Upscale] Sharpness` | 启动时 | 50 / 25 | 按超分方式切换 |
| 垂直同步 | 自动 / 开·0 / 开·1 / 关 | `[General] VSyncMode` + `[General] VSync` + `[V-Sync] OverrideVsync/ForceVsync/SyncInterval` | 混合 | 自动 | 与 PresentMode 联动，逻辑见 §7.5 |
| 呈现模式 | 0–6 | `[Advanced] PresentMode` | **需重启捕获** | 6 | 详见 §7.3 |
| 呈现延迟 | 0–12ms | `[Advanced] PresentDelayMs` | **实时** | 3 | 越低越跟手，过低易抖 |
| 性能优先级 | 关 / CPU / CPU+GPU / 最大 | `[Advanced] Boost` | 启动时 | 2 | 最大=REALTIME，谨慎 |
| 插帧 GPU | 自动 / 枚举 | CLI `--gpu <index>` | 启动时 | 自动 | 索引是 **DXGI `EnumAdapters1` 顺序**；GameLocker 的枚举若不同源会错位，一期可只给「自动（高性能）」 |

### 7.3 高级参数（折叠）

| UI 名称 | 写入 | 说明 |
|---|---|---|
| 上采样器 | `[Settings] UpscalerMode` + `[Upscalers] Dx12Upscaler` + `[FSR] UpscalerIndex` | xess / ffx / dlss / DLSS3.7 / FSR4 / DittoSR；FSR4 另写锐化/CAS/NativePass |
| 宿主缓冲 | `[Advanced] Buffers` 2–4 | 实验项 |
| 覆盖层不透明度 | `[Advanced] OverlayAlpha` | 254 默认；呈现模式 3/4/5/6 会强制 255 |
| 启动延迟 | `[Advanced] StartDelayMs` | 默认 1500；GameLocker 建议 300（已在游戏中） |
| 探测/无层/测试 | `[Advanced] Probe/NoLayered/TestMode` | 仅诊断 |
| 菜单键 | `[General] MenuKey` + `[Menu] ShortcutKey` | 实时 |
| FPS 键 / 切换键 / 插帧键 | `[General] FpsKey/FpsCycleKey/FgKey` + `[Menu] Fps*/FGShortcutKey` | 实时 |
| 停止/焦点/菜单快捷键 | `[Shortcuts] Stop/Focus/Menu/Launcher`（格式 `mods,vk`） | 实时 |
| 限帧 | `[Limiter] Target/Fps/Mode/HideOsd/OptiFps/Auto` | **一期保持 Target=0 关闭**，交给 GameLocker |
| 每游戏参数 | 参考 `Game Profiles\` 四槽 | 二期 |

### 7.4 插帧模式映射（关键表）

参考 launcher 的 11 个 `frameGenMode`（3 已废弃）：

| 模式 | UI 文案（建议） | `FGOutput` | `FGNvngxReplacement` | 倍率适用 | 依赖 |
|---|---|---|---|---|---|
| 0 | Intel MFG（新 GPU / XeLL） | `XeFG` | `auto` | ✅ | Intel XeSS FG 运行库 |
| 1 | Artur MFG（新老 GPU） | `DLSSG` | `Arturs` | ✅ | DLSS Enabler |
| 2 | 关闭插帧（只捕获/超分） | `nofg` | `auto` | ❌ | — |
| 4 | Intel FG（xell / 新 GPU） | `XeFG` | `auto` | ❌ | Intel FG 目录 |
| 5 | Combo MFG（新老 GPU） | `DLSSG` | `Combo` | ✅ | DLSS Enabler |
| 6 | Nukem FG（新老 GPU） | `DLSSG` | `Nukems` | ❌ | DLSS Enabler |
| 7 | FSR3-4 FG（新老 GPU） | `DLSSG` | `FFX` | ❌ | DLSS Enabler + FSR FG 运行库 |
| 8 | Nvidia MFG（仅 RTX） | `DLSSG` | `None` | ✅ | NVIDIA DLSSG |
| 9 | FSR FG（AntiLag2 / RDNA2-4） | `FSRFG` | `auto` | ❌ | FSR FG 运行库（AL2 目录） |
| 10 | FSR FG（LatencyFlex / 新老 GPU） | `FSRFG` | `auto` | ❌ | FSR FG 运行库（LF 目录） |

曝光策略建议：

- **简单模式只列 5 个**：自动推荐 / Combo MFG / Nukem FG / FSR3-4 FG / 关闭；
- **高级模式列全 10 个**；
- 「自动推荐」按 GPU 厂商预选（NVIDIA RTX→Nvidia MFG 或 Combo、Intel→Intel MFG、AMD→FSR3-4 或 FSR FG），**具体映射待实测确定**（见 §15 D6）。

### 7.5 依赖校验与冲突

**依赖矩阵（启动前检查，缺文件直接拦或降级）：**

| 模式/能力 | 必需文件 | 缺失表现 | 处理 |
|---|---|---|---|
| 任意插帧 | `dxgi.dll` + `OptiScaler.ini` | 只有捕获，无插帧 | 拦：提示准备 OptiScaler |
| DLSSG 系（1/5/6/7/8） | `dlss-enabler-headless.dll` | 模式无效/黑屏 | 拦或降级 Combo |
| FSRFG（9/10） | FSR FG 运行库 | 模式无效 | 拦 |
| Intel MFG/FG（0/4） | Intel XeSS/XeFG 运行库 | 模式无效 | 拦/推荐换模式 |
| FSR3-4 输出超分 | `amd_fidelityfx_dx12.dll` | 回退 FSR1（日志写明） | 提示 |
| DLSS 输出超分 | `nvngx_dlss.dll` | 回退 FSR1 | 提示 |

**冲突规则：**

1. **限帧双份**：宿主 `[Limiter]` 与 GameLocker `FrameLimitService` 不能同时开。一期宿主任意 `Target=0`，由 GameLocker 统一。
2. **VSync 双份**：宿主 `[General] VSync` 与 `OptiScaler [V-Sync]` 必须由同一套逻辑推导（参考 `ApplyVsyncToOptiScaler` 的分支），不能各自写死。
3. **PresentMode 与 VSync**：模式 3/5/6 用宿主自己的 VSync；模式 4（Lossless 风格）默认撕裂、只在显式「开」时等垂直同步；模式 0–2 交给 OptiScaler。UI 上把 VSync 置灰或提示。
4. **超分与窗口尺寸**：参考 launcher 会把游戏窗口缩小再放大；GameLocker 一期不缩放，只做「同尺寸放大/锐化」，否则会改变游戏 UI 与输入映射。
5. **FPS 覆盖层**：默认 `HideFps=true`，避免与 GameLocker 的性能监控叠加。

### 7.6 参数持久化

- 一期：参数存在 `LaunchItem.OptiHost`（每磁贴一份），启动时写 ini。
- 二期：按游戏 exe 存 `Game Profiles\<exe>\OptiScaler.ini` 快照（参考仓库的 4 槽），实现「每个游戏一套参数」。
- ini 写入用「键级覆盖」而不是整文件重写（保留 OptiScaler 模板里的其它键与注释），参考 `SetIniValue` 的做法。

---

## 8. 一键插帧

### 8.1 交互定义

| 输入 | LSFG（现状） | OPTISCALER（新） |
|---|---|---|
| A / 点击磁贴 | 开/关小黄鸭插帧 | 未运行→启动宿主（插帧开）；运行中→停止宿主（插帧关） |
| Y | 启动/唤醒小黄鸭（最小化） | 进入/退出 OptiScaler 面板（游戏内改参数，详见 §17） |
| R3 / 长按 | 指定小黄鸭程序 | 打开宿主诊断（日志/状态/重新准备） |
| 编辑页 | 路径/配置/优化 | OptiScaler 参数分组 |

> 语义说明：一期「关插帧」= 停宿主，因为 OptiScaler 的插帧开关依赖覆盖层焦点（见 §8.8），热键切换不可靠；二期可做「保活 + 切 FG」。

### 8.2 状态机

```
Idle ──A──► Preparing ──► Starting ──► Active ──A──► Stopping ──► Idle
              │              │            │
              └──失败────────┴──失败──────┴──宿主崩溃──► Error ──(3s/重试)──► Idle
```

| 状态 | 进入条件 | UI | 允许的操作 |
|---|---|---|---|
| Idle | 宿主未运行、无残留 status | 「就绪」 | A 启动 |
| Preparing | 开始预检/写 ini | 「准备中…」 | 再按 A = 取消 |
| Starting | 进程已创建，等覆盖层/status | 「初始化…」 | 再按 A = 取消（杀进程） |
| Active | status 新鲜 + 进程存活 | 「插帧中」/「运行中」 | A 停止、Y 菜单、R3 诊断 |
| Stopping | 已发 `WM_CLOSE` | 「正在停止…」 | 无 |
| Error | 预检失败/进程早退/超时 | 具体错误 | 重试/诊断 |

防重入：全局 `_hostBusy` 标志 + 1s 冷却；`Starting` 阶段的取消只在「用户再次按 A」时触发。

> `Active` 下还有一个「面板中」子状态（`menuMode`），见 §17.11。

### 8.3 启动时序

1. 取目标：`ForegroundWatcherService` 记录的游戏前台（排除 GameLocker/宿主自身）；无则取最近游戏。
2. 预检：
   - 目标 HWND 可见、非最小化、非独占全屏（用 `FullscreenDetector`）；
   - `OptiCaptureHost.exe`、`dxgi.dll`、`OptiScaler.ini` 存在；
   - 所选模式所需运行库存在；
   - **权限一致**：游戏以管理员运行时，GameLocker/宿主也需同权限，否则捕获黑屏（现有 Magpie 的 code=7 同类问题）；
   - 游戏未在「反作弊高风险名单」（可选，见 §13）。
3. 隐藏 dock/浮层（`App.Main?.HideToTray()`），把游戏切前台并等待 `GetForegroundWindow()==target`（最多 ~1s）。
4. 停止残留宿主（若上次异常退出）。
5. 写 `OptiCaptureHost.ini`（§7）与 `OptiScaler.ini`（键级写入）。
6. `CreateProcess`：
   - `OptiCaptureHost.exe --hwnd <十进制 HWND> --parent <GameLocker PID> --gpu <index> --delay 0`
   - 工作目录 = `fg-host\`；不重定向 stdout（它写日志文件）。
7. 轮询（每 300–500ms，最多 12s）：
   - 找到类名 `OptiCaptureHostOverlay` 且属于宿主 PID 的窗口；
   - `OptiCaptureHost.status` 的 `stamp` 在 2.5s 内且 `pid` 匹配。
8. 判定插帧（§8.4）。成功 → Toast「已开启 OptiScaler 插帧（模式/倍率）」。
9. 失败分支：进程早退 → 读日志尾部 40 行 → 映射文案（§8.6）；超时 → 杀进程 + 报错。
10. 恢复 dock（若启动失败）；启动成功则保持隐藏（依产品策略）。

### 8.4 运行中状态判定

输入：`status`（每秒 1 次）+ 进程存活 + 覆盖层窗口。

| 判据 | 含义 |
|---|---|
| `stamp` 新鲜 + PID 匹配 | 宿主在跑 |
| `focus` 0/1/2 | 前台是游戏/覆盖层/其他 |
| `capture` | 捕获帧率（≈游戏输出） |
| `dxgi` | 呈现帧率（**含生成的帧**） |
| `present` | 宿主 Present 次数/秒 |
| `interval` | 实际同步间隔 |

插帧判定（建议，去抖 3 次）：

```
fgOn = dxgi >= capture * 1.25 && capture >= 15
     || (refreshHz > 0 && dxgi >= refreshHz * 0.95 && capture < refreshHz * 0.70)
```

- 高刷显示器被刷新率封顶时，用第二条兜底；
- `PresentMode 3`（Classic）捕获被 Windows 限到 60，第一条可能失效，允许「只显示运行中」；
- `PresentMode 5/6` 下捕获节拍特殊，比率仅作参考。

**结论分级**：`插帧中`（判定成立）/ `运行中`（宿主在跑但判定不成立）/ `初始化` / `未运行`。宁可显示「运行中」也不要误报「插帧中」。

### 8.5 停止时序

1. 找到覆盖层窗口（`OptiCaptureHostOverlay`，属于宿主 PID）。
2. `PostMessage(hwnd, WM_CLOSE, 0, 0)` —— 宿主 `OverlayProc` 收到后 `PostQuitMessage(0)`，走正常清理（含 FG DLL 卸载）。
3. 等待退出最多 5s（参考 launcher 给 4s，另加兜底）。
4. 未退出 → `Kill(entireProcessTree: true)`（参考仓库注释：Nukem/FSR3-4 卸载可能卡死/崩溃）。
5. 删除/忽略旧 `status`；恢复 dock；刷新磁贴；Toast「已关闭插帧」。
6. 若宿主因游戏退出自动结束（`targetClosed_`），走同一收尾但文案不同。

### 8.6 失败分类与文案

| 现象 | 判定 | Toast 文案 | 后续 |
|---|---|---|---|
| 缺宿主 | 文件检查 | 「未安装 OptiScaler 宿主：点此准备」 | 打开准备向导 |
| 缺 `dxgi.dll` | 文件检查 | 「缺少 OptiScaler（dxgi.dll）」 | 准备向导 |
| 缺运行库 | 依赖矩阵 | 「当前插帧模式缺少 xxx.dll」 | 换模式/准备 |
| 游戏独占全屏 | 预检 | 「请把游戏改为无边框窗口化」 | 中止 |
| 游戏最小化 | 预检 | 「游戏已最小化」 | 中止 |
| 游戏权限高于宿主 | 预检/黑屏 | 「游戏以管理员运行，请以相同权限重启 GameLocker」 | 中止 |
| 受保护内容（DRM/桌面合成限制） | 黑屏 | 「该窗口受保护，无法捕获」 | 中止/记录 |
| 进程 12s 内早退 | 进程退出码 | 「插帧宿主启动失败」 | 打开日志 |
| 日志含 WGC 不支持 | 日志 | 「系统不支持窗口捕获（需 Win10 2004+）」 | 中止 |
| 日志含初始化失败 | 日志 | 「初始化失败：请更新显卡驱动/检查权限」 | 日志 |
| 有 status 但 8s 无法判定插帧 | 状态 | 「无法确认插帧是否生效，请查看诊断」 | 诊断 |
| 宿主崩溃 | 进程退出 | 「插帧宿主已退出」 | 重试 |
| 游戏退出 | 宿主自退 | 静默收尾 | — |

### 8.7 并发与竞态

- **连按 A**：状态机拒绝重复进入；`Starting` 中按 A = 取消并杀进程。
- **切换游戏**：先停旧宿主，再对新目标启动；不允许两个宿主同时跑（全局单例）。
- **切前台/Alt-Tab**：宿主覆盖层始终置顶并跟随游戏窗；`focus=2` 时只提示，不重置。
- **锁屏/休眠**：监听 `SystemEvents`，恢复后若宿主已死则回 Idle，不自动重启（避免黑屏时启动）。
- **GameLocker 退出**：`--parent` 让宿主自动退出；另在 `App` 退出路径显式停止。
- **宿主崩溃循环**：连续 2 次失败后进入冷却 60s，提示诊断。

### 8.8 与 LSFG 一键的差异

| 点 | LSFG | OPTISCALER |
|---|---|---|
| 启动耗时 | 3–5s（小黄鸭冷启动） | 3–8s（捕获 + OptiScaler 初始化） |
| 状态来源 | GPU 占用/覆盖层 + PresentMon | status 文件 + 日志 |
| 关闭方式 | 发热键（可逆） | 停宿主（重开需重新初始化） |
| 焦点要求 | 热键必须落在游戏 | 覆盖层点击穿透，游戏保持前台 |
| 失败可见性 | Toast 文案 | Toast + 日志 + 可选诊断窗 |
| 参数归属 | 小黄鸭 Settings.xml | 两份 ini（GameLocker 全权） |

---

## 9. UI 设计

### 9.1 编辑页（替换现有 Magpie 面板）

```
插帧引擎
  ( ) 小黄鸭（Lossless Scaling）      ← 现有参数：路径/配置/优化
  (•) OptiScaler（内置宿主）          ← 新面板

── OptiScaler 参数 ─────────────────────────────
[基础]
  插帧模式   [ 自动推荐 ▾ ]   提示：当前 GPU 建议 Combo MFG
  插帧倍率   [ 自动 ▾ ]      2× 3× 4× 5× 6×（按模式启用）
  输出超分   [ 关 ▾ ]        Bilinear / FSR1 / NIS / FSR3-4 / DLSS
  画质档     [ Quality ▾ ]   （超分≠关时启用）
  垂直同步   [ 自动 ▾ ]
[高级 ▾]
  呈现模式 / 呈现延迟 / 性能优先级 / 插帧 GPU / 上采样器 / 锐化
[状态]
  ○ 就绪 · 宿主 v2.10.70 · OptiScaler 已就绪
  [ 准备/修复 ] [ 打开日志 ] [ 诊断 ]
```

交互细节：

- 模式与倍率联动：不支持倍率的模式禁用倍率控件并说明原因。
- 缺文件时在对应控件下方显示「缺少 xxx.dll（影响：…）」+ 一键准备。
- 「自动推荐」旁显示探测到的 GPU 与建议模式（不确定时显示「建议手动选择」）。
- 保存时校验：`MfgCount>1` 但模式不支持 → 自动归零并提示。

### 9.2 磁贴状态

| 状态 | 文案 | 圆点/颜色 |
|---|---|---|
| 未准备 | 「未准备」 | 灰 |
| 就绪 | 「就绪」 | 蓝 |
| 初始化 | 「初始化…」 | 黄（呼吸） |
| 运行中（FG 未证实） | 「运行中」 | 绿 |
| 插帧中 | 「插帧中 · Combo ×3」 | 绿（亮） |
| 错误 | 「启动失败」 | 红 |

复用现有 `TileStyle.BuildLosslessTile/FillLosslessState` 结构，新增 `LosslessTileRefs` 状态字段或新建 `HostTileRefs`。

### 9.3 首次使用引导

引导页只做三件事：

1. 检测 `fg-host\` 是否完整（宿主/dxgi/OptiScaler.ini/运行库）；
2. 缺失则「一键准备」：从固定版本源下载并解压（复用 `OptiScalerService` 的下载/校验/渠道能力，但**固定 janblade fork 版本**，不跟随官方 latest）；
3. 完成后跑一次自检：启动宿主到「就绪」即停，不进入游戏。

### 9.4 诊断入口

- 打开 `OptiCaptureHost.log` / `OptiCaptureHost.status`；
- 「复制诊断信息」：宿主版本、OptiScaler 版本、GPU、模式、依赖文件校验和、最近 50 行日志；
- 一键「重新准备」（重写 ini、校验文件，不重装）。

### 9.5 文案规范

- 不用「Magpie」字样（用户无感知）；
- 说「OptiScaler 插帧」，不暴露 GPL/上游实现细节到主 UI；
- 错误文案必须带「下一步动作」（换模式/准备/看日志）。

---

## 10. 代码改造清单（按文件）

### 10.1 新增

| 文件 | 职责 |
|---|---|
| `Services/OptiScalerHostService.cs` | 启停、ini 写入、status/log 解析、状态机、结果枚举 |
| `Models/OptiHostOptions.cs` | 参数对象 + 默认值 + 校验 |
| `Services/OptiHostProvisioner.cs` | 目录准备、文件校验、固定版本下载/复制 |
| `Services/OptiHostStatus.cs` | status 文件解析模型 |
| `docs/optiscaler-host-design.md` | 本文档 |

### 10.2 改造

| 文件 | 改动 |
|---|---|
| `LaunchItem.cs` | `FrameGenEngine` 新值；新增 `OptiHost`；标记旧 `Fg*` 废弃 |
| `ConfigService.cs` | 一次性迁移：XESS/FSR3 → OPTISCALER；清理旧字段 |
| `AddItemPage.xaml` / `.cs` | 引擎两项；参数分组；依赖提示；表单导航与保存 |
| `WidgetPage.xaml.cs` | 新组件默认参数；预览磁贴 |
| `MainPage.xaml.cs` | A/Y/R3 语义；结果文案映射；停止路径 |
| `TileStyle.cs` | 状态渲染 |
| `ForegroundWatcherService.cs` | 排除 `OptiCaptureHostOverlay` / `OptiCaptureHostLabel` |
| `MonitorService.cs` | Presenter PID = status.pid；目标 = 游戏 HWND |
| `FloatingToastService.cs` | FG 期间置顶策略（评估） |
| `GameLocker.csproj` / `.iss` | 新目录、许可文件、安装/卸载 |

### 10.3 删除/保留

- 删除：`ActivateMagpieAsync`、`StopMagpie`、Magpie 专用参数与 UI 分支、FGHost 日志解析。
- 保留（过渡期）：`FG\FGHost.exe` 可分阶段删除；建议先「隐藏入口、保留文件」，确认回归后再删。
- 保留：`LosslessOutputListener`（仍用于 LSFG；宿主状态已由 status 提供，可不再依赖 PresentMon）。

---

## 11. 资源与分发

| 项 | 现状（FGHost） | 方案（宿主） |
|---|---|---|
| 宿主 | 2.6MB | 预计 < 5MB（两个 exe，launcher 可选） |
| FSR 运行库 | `amd_fidelityfx_framegeneration_dx12.dll` 40MB + loader | 类似，另需 `amd_fidelityfx_dx12.dll`（超分） |
| XeSS | libxess 78MB + libxess_fg 23MB | 视模式，可能仍需 |
| OptiScaler | — | 约 10–30MB（包，含多套运行库时更大） |
| DLSS Enabler | — | 数 MB（许可受限） |
| DLSS | — | 视是否随包（许可受限，建议用户自备） |
| 合计 | ~157MB | **可能 150–300MB** |

结论：**体积不会更小**。若在意体积，可按「用户所选模式」按需下载（FSR/DLSS/XeSS 分渠道），与 `OptiScalerService` 的渠道缓存思路一致。

---

## 12. 测试计划

### 12.1 环境矩阵

| 维度 | 取值 |
|---|---|
| OS | Win10 22H2 / Win11 23H2 / 24H2（WGC 行为差异） |
| GPU | NVIDIA RTX / NVIDIA GTX / AMD RDNA2/3 / Intel Arc / 混合显卡笔记本 |
| 显示 | 1080p60 / 1440p165 / 4K120 + HDR / 竖屏 / 多屏 / DPI 125–200% |
| 游戏 | DX11 / DX12 / Vulkan / OpenGL / 模拟器 / 视频播放器 / 独占全屏 / 无边框 |
| 权限 | 普通 / 游戏管理员 / GameLocker 管理员 |
| 干扰 | 反作弊（EAC/BattlEye）、录屏、RTSS、其他覆盖层 |

### 12.2 用例（关键项）

1. 首次准备 → 引导完整 → 一键启动成功。
2. 每种插帧模式各启动一次，确认 FG 判定与文案。
3. 倍率 2×–6× 与实际呈现帧率一致（status.dxgi）。
4. 输出超分 5 种方式；缺 DLL 时回退/拦截。
5. HDR 游戏：记录画面是否发灰/过曝（预期：一期不支持）。
6. Alt-Tab / 最小化 / 切换显示器 / 移动窗口：覆盖层跟随、焦点回游戏。
7. 停止：正常 4s 内退出；人为卡死时 5s 后被杀；无残留进程。
8. 游戏退出：宿主自退，磁贴回 Idle。
9. GameLocker 退出：宿主随父进程退出。
10. 连按 A / 快速切换游戏：无重入、无双宿主。
11. 无 OptiScaler / 错版本 ini：文案正确，不崩溃。
12. 长时间（≥2h）稳定性：句柄/内存/帧率不劣化。
13. 锁屏/休眠/唤醒：不自动重启、不黑屏。
14. 与 dock/Toast 的 Z 序：dock 可见、Toast 可见（重点）。
15. 中文路径/用户名含空格/非管理员安装。
16. 反作弊游戏：至少记录是否被拦截（不作为支持承诺）。

### 12.3 验收标准

- 一键启动成功率 ≥ 95%（受支持环境）；
- 停止无残留进程；
- 状态误报率（把「运行中」显示成「插帧中」）< 2%；
- 无新增崩溃/卡死；日志可定位 ≥ 90% 的失败。

---

## 13. 风险登记册

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| GPL 传染/发布违规 | 高（若处理不当） | 法律 | §4 方案 B：独立目录/进程/源码 |
| HDR 画面异常 | 高 | 体验 | 一期明确不支持；检测 HDR 并提示关闭 |
| 覆盖层与 dock/Toast 抢置顶 | 高 | 体验 | 实机验证；必要时给宿主打补丁（须开源） |
| 插帧画质不如 FGHost（无硬件光流） | 中 | 口碑 | 保留 LSFG；用模式/超分弥补；灰度对比 |
| 延迟增加（多一跳） | 中 | 体验 | PresentMode 4/6 + 延迟 0–1；实测 |
| OptiScaler 版本漂移 | 高 | 维护 | 固定版本 + 校验 ini 关键键 + 回归清单 |
| 宿主初始化失败弹 MessageBox | 中 | 无人值守体验 | 一期接受；二期打补丁加 `--quiet` |
| 卸载 FG DLL 卡死/崩溃 | 中 | 稳定性 | `WM_CLOSE` 优先 + 5s 超时杀树 |
| 反作弊误判 | 中 | 封号投诉 | 免责声明 + 高风险游戏名单提示 |
| 全局热键冲突（F10/F11/F12、Insert、End） | 高 | 误触 | 统一改为 GameLocker 不用的组合；或改走 UI/Toast 按钮 |
| 游戏内面板焦点抢不回/菜单残留 | 中 | 体验 | §17：IPC 跟踪菜单状态 + `ForceForeground` 兜底 + 文案引导 |
| 面板内改参与磁贴参数冲突 | 高（未同步时） | 设置被覆盖 | §17.6 退出回读 + 启动前「用哪套」提示 |
| 混合显卡跨 GPU 拷贝开销 | 中 | 帧率/延迟 | 用 `--gpu` 固定独显；实测笔记本内屏/外接屏 |
| 体积增大 | 高 | 分发 | 按需下载运行库 |
| 上游停止维护 | 低 | 维护 | fork 自持；代码可读；固定 tag |

---

## 14. 里程碑（不含开发，仅规划）

| 阶段 | 交付物 | 粗估 |
|---|---|---|
| M0 决策与合规 | 分发方式定稿、许可文件模板、`fg-host` 目录规范 | 1–2 天 |
| M1 手工验证 | 用参考宿主 + 你的游戏跑通：覆盖层/焦点/HDR/延迟/置顶冲突记录 | 0.5–1 天 |
| M2 服务层闭环 | `OptiScalerHostService` + 启停 + status 判定 + 文案 | 3–5 天 |
| M3 参数与 UI | 引擎收敛、参数面板、迁移、依赖校验 | 3–5 天 |
| M4 状态与集成 | 磁贴/监控/前台/Toast/dock、诊断入口 | 3–4 天 |
| M5 打包与测试 | 安装包、许可、CI、回归矩阵 | 3–5 天 |

合计约 **2–3 周**（不含宿主补丁与 HDR）。

---

## 15. 待拍板（开工前必须确认）

| # | 问题 | 选项 | 建议 |
|---|---|---|---|
| D1 | 分发方式 | A 用户自备 / B 独立 GPL 组件 / C 整体 GPL | **B** |
| D2 | 是否 fork 修改宿主 | 不改 / 打小补丁（菜单 IPC、静默失败、置顶让位、FG 状态输出、取消启动） | **建议打最小补丁**：游戏内面板需要状态与原子进出（§17） |
| D3 | 一键模型 | 启停宿主 / 保活 + 切 FG | **启停宿主**（一期） |
| D4 | FGHost 去留 | 立即删 / 过渡共存 | 过渡共存，回归后删 |
| D5 | 输出超分 | 一期做 / 只做画面放大 / 延后 | 一期只做「不缩窗」的放大与锐化 |
| D6 | 「自动推荐」映射 | 按 GPU 厂商 / 全部手选 | M1 实测后定 |
| D7 | 限帧归属 | 宿主 / GameLocker | **GameLocker**（宿主 Target=0） |
| D8 | HDR | 一期不支持 / 强制 SDR | 一期不支持 + 提示 |
| D9 | GPL 源码公开位置 | 仓库内 `third_party` / 独立 repo | 独立 repo 或 `third_party` + 发布附源码 |

> D10–D12（面板触发方式、是否加 IPC 补丁、参数回读策略）见 §17.12。

---

## 16. 附录

### 16.1 `OptiCaptureHost.ini` 参考模板（GameLocker 写入）

```ini
[General]
VSync=1
VSyncMode=auto
MenuKey=45
FpsKey=33
FpsCycleKey=34
FgKey=35

[Settings]
FrameGenMode=5
MfgCount=0
UpscalerMode=4

[Advanced]
PresentMode=6
PresentDelayMs=3
Boost=2
StartDelayMs=300
Buffers=3
OverlayAlpha=254

[Upscale]
Method=0
Level=0
DlssPreset=0
NisSharpness=50
Sharpness=25
LabelSeconds=8

[Limiter]
Target=0
Fps=0
HideOsd=1
OptiFps=1
Auto=0
Mode=1

[Shortcuts]
Stop=6,123
Focus=6,122
Menu=6,121
Launcher=6,120
```

### 16.2 `OptiScaler.ini` 关键键（按模式写入）

```ini
[FrameGen]
FGOutput=DLSSG
FGNvngxReplacement=Combo

[DLSSG]
InterpolationCount=2

[Upscalers]
Dx12Upscaler=ffx

[FSR]
UpscalerIndex=auto

[V-Sync]
OverrideVsync=auto
ForceVsync=auto
SyncInterval=auto

[Menu]
ShortcutKey=0x2D
FpsShortcutKey=0x21
FpsCycleShortcutKey=0x22
FGShortcutKey=0x23

[Framerate]
FramerateLimit=144.000000
```

> 说明：`ShortcutKey` 用十六进制字符串；`InterpolationCount` 只在支持倍率的模式写；`[V-Sync]` 三键必须**一起**写——`auto` 表示不强制，显式开/关时写 `OverrideVsync=true` + `ForceVsync=true/false` + `SyncInterval=1/0`。

### 16.3 术语

| 术语 | 含义 |
|---|---|
| 宿主 | OptiCaptureHost.exe，负责捕获/覆盖层/呈现/超分 |
| 模式 | `FrameGenMode`，决定走哪套 FG 运行时 |
| 倍率 | 基础帧 : 输出帧 = 1 : N（2×–6×） |
| 超分 | 输出端放大（DLSS/FSR/NIS/FSR1/Bilinear） |
| 呈现模式 | 宿主的排队与 VSync 策略（0–6） |
| 状态文件 | `OptiCaptureHost.status`，每秒覆盖写 |
| 一键 | A 键启动/停止宿主 |

### 16.4 手工最小验证（M1，开发前做）

目的：在写任何 C# 代码前，先确认这台机器上「捕获 + 覆盖层 + 插帧」能跑、以及与本产品形态的冲突点。

1. 准备一个可写目录 `fg-host\`：放入 `OptiCaptureHost.exe`、`dxgi.dll`（OptiScaler）、`OptiScaler.ini`、所需运行库。
2. 启动一个无边框窗口游戏，记下窗口句柄（可用 Spy++/AutoHotkey 或任务管理器 + Win32 工具）。
3. 命令行启动：`OptiCaptureHost.exe --hwnd <HWND> --parent <当前 shell PID> --delay 0`。
4. 观察：覆盖层是否精确贴合、鼠标是否穿透、键盘是否进游戏、`Ctrl+Shift+F11` 焦点切换是否正常、`F10` 能否开 OptiScaler 菜单。
5. 看 `OptiCaptureHost.status`：`capture/dxgi/present/focus` 是否符合预期；开/关 FG 后 `dxgi` 是否变化。
6. 记录冲突：GameLocker dock 在屏幕上时是否被覆盖层盖住、Toast 是否可见、任务栏/ALT-TAB 是否正常。
7. 记录 HDR：开 HDR 的游戏画面是否发灰/过曝。
8. 记录停止：`WM_CLOSE` 到退出耗时；再试 `Ctrl+Shift+F12`；最后试强杀，确认无残留。
9. 记录延迟主观感受（PresentMode 3/4/6 各试一次）与画质（对比 LSFG）。
10. 把结论回填到本文档 §12/§13，再决定 D2（是否打补丁）与 D6（默认模式）。
11. 面板流程：`F10` 打开菜单 → 鼠标/键盘改 1–2 项 → `Insert` 关菜单 → `F11` 回游戏；记录：菜单是否残留、焦点是否回来、改动是否写进 `OptiScaler.ini`、游戏内是否即时生效。
12. 若决定打补丁：先用 §17.5 的 `MENU_ENTER/MENU_EXIT/GET_STATE` 三个命令做最小验证，再进 C# 集成。

---

## 17. 增补：游戏内 OptiScaler 面板（2026-11-23 追加）

> 需求：**不退出游戏**，在游戏画面里打开 OptiScaler 面板，用鼠标/键盘改参数，改完回游戏继续玩。

### 17.1 为什么这件事不简单

| 约束 | 说明 |
|---|---|
| 覆盖层默认不吃输入 | 宿主覆盖层是 `WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`，键盘/鼠标全部穿透给游戏 |
| OptiScaler 只认前台窗口 | 它只在「自己呈现的窗口是前台」时接收菜单键与鼠标；覆盖层不聚焦，按 `Insert` 没用 |
| 覆盖层样式会被还原 | 宿主每 250ms 检查 `EnforceOverlayStyle`，外部程序改 `WS_EX_*` 会被改回去 |
| 菜单状态不可见 | OptiScaler 的菜单是自绘的，没有独立窗口；宿主也没有把「菜单开/关」写进 status |
| 焦点必须还回去 | README 明确：关掉菜单后不把焦点还给游戏，手柄/鼠标/键盘在游戏里就失效 |

宿主已经提供了「半套」答案：`Ctrl+Shift+F10` 会先把覆盖层切成可交互并抢焦点，300ms 后注入 OptiScaler 的菜单键；`Ctrl+Shift+F11` 把焦点还给游戏。缺的是**状态**与**原子化的进出**。

### 17.2 宿主现有机制（事实）

| 能力 | 实现 | 可用性 |
|---|---|---|
| 打开菜单 | `OpenMenu()`：未交互则 `ToggleInteractive()`（去掉 TRANSPARENT/NOACTIVATE + `ForceForeground(overlay)`），300ms 后注入 `menuKey` 的 down/up | ✅ 默认 `Ctrl+Shift+F10` |
| 交还焦点 | `ToggleInteractive()` 关闭时 `ForceForeground(target)` | ✅ 默认 `Ctrl+Shift+F11` |
| 热键注册 | `RegisterHotKey(nullptr, id, mods\|MOD_NOREPEAT, vk)`，全局生效；冲突写日志 | ⚠️ 被占用会静默失效（仅日志） |
| 游戏最小化兜底 | `KeepGameVisible()`：焦点切换导致游戏最小化时自动 `ShowWindow(SW_SHOWNOACTIVATE)` | ✅ |
| 菜单开关状态 | 无 | ❌ 需要补丁 |

### 17.3 交互设计（GameLocker 视角）

- **触发方式**（三选一，建议手柄优先）：
  1. 手柄组合键（如 `LB+RB+Start`，需避开游戏与现有映射）；
  2. 底栏/悬浮 toast 上的「调参」按钮（鼠标可达）；
  3. 键盘热键（用户可配，默认不占用 `Insert` / `F10` / `F11` / `F12`）。
- **单键 toggle**：按一次进入面板，再按一次返回游戏。
- **进入时**：Toast「已打开 OptiScaler 面板 · 鼠标/键盘可调参 · 按 {键} 返回游戏」；磁贴状态变为「面板中」。
- **退出时**：Toast「已返回游戏」；焦点回游戏；恢复 dock/手柄映射。
- **面板中禁止**：A/Y 的插帧启停语义、dock 边缘手势、手柄→游戏映射、其他会抢焦点的 toast。
- **宿主未运行时**按触发键：提示「请先一键插帧」并可选自动启动。

### 17.4 方案 A：不改宿主（按键序列，过渡）

| 动作 | 发送 | 结果 |
|---|---|---|
| 进入面板 | `SendInput` `Ctrl+Shift+F10` | 宿主切交互 + 注入菜单键 → 面板打开 |
| 退出面板 | `SendInput` `Ctrl+Shift+F11` | 焦点回游戏；**菜单是否关闭未知** |

局限：

1. 用户若已用 `Insert` 关掉菜单，`F11` 只还焦点，行为正确；
2. 若菜单仍开着，`F11` 后菜单留在画面上但没有输入（需用户再开关一次）；
3. 无法判断当前是否在面板中，无法做「关闭菜单 + 还焦点」的原子操作；
4. 依赖宿主热键注册成功（被其他软件占用时完全失效）；
5. 合成的 `Ctrl+Shift+F10` 可能被游戏/反作弊看到（热键注册通常会截获，但不保证）。

**适用**：v1 过渡，文案必须写清「先按 Insert 关闭面板，再按 {键} 回游戏」。

### 17.5 方案 B：最小 IPC 补丁（推荐）

给宿主加一个很小的命令通道，GameLocker 不再靠模拟按键猜状态。

**通信方式**（二选一，推荐前者）：

- `WM_COPYDATA` 发到覆盖层窗口（`OptiCaptureHostOverlay`），载荷 UTF-8 JSON；
- 或命名管道 `\\.\pipe\GameLockerFG-<pid>`（可双向，但要管生命周期）。

**安全**：宿主只接受 `--parent` 传入的 GameLocker PID 发来的命令（`GetWindowThreadProcessId` 校验），其它进程一律忽略。

**命令集**：

| 命令 | 作用 | 返回 |
|---|---|---|
| `PING` | 探活/取版本 | `{version, ok}` |
| `MENU_ENTER` | 交互开 + 注入菜单键 + `menuOpen=true` | `{ok, menuOpen}` |
| `MENU_EXIT` | `menuOpen` 时注入菜单键关闭 → 焦点回游戏 → 交互关 | `{ok}` |
| `MENU_TOGGLE` | 按当前状态进出 | `{menuOpen}` |
| `GET_STATE` | 取状态 | `{menuOpen, interactive, focus, capture, dxgi, present}` |
| `RELOAD_INI` | 让宿主立刻重读 `OptiCaptureHost.ini` | `{ok}` |
| `FG_TOGGLE`（可选） | 临时聚焦 → 注入 FG 热键 → 还焦点 | `{ok, fg}` |

**菜单状态跟踪（补丁核心）**：

1. `MENU_ENTER` 注入菜单键时置 `injectingMenuKey_`，忽略这次按键；
2. `OverlayProc` 增加 `WM_KEYDOWN/WM_SYSKEYDOWN`：若 `vk == menuKey && interactive_`，翻转 `menuOpen`（用户自己按 `Insert` 也能被观察到）；
3. `MENU_EXIT`：仅当 `menuOpen` 注入一次菜单键关闭；随后强制 `ForceForeground(target)` + 关闭交互；
4. `GET_STATE` 把 `menuOpen` 暴露出来，GameLocker 不再猜。

**注意**：OptiScaler 若用 `GetAsyncKeyState` 轮询或在子类 WndProc 里先消费按键，宿主的观察可能滞后甚至看不到；此时以 `MENU_ENTER/EXIT` 自身状态为准，用户手动按 `Insert` 可能不同步。兜底：实测 `Esc` 是否能关菜单——若可以，`MENU_EXIT` 优先发 `Esc`（已关闭时对游戏无害），否则退化为「发菜单键 + 还焦点」，并在文案里写明（D13 待实测）。

**补丁代价**：C++ 约 200–400 行（消息处理 + 状态 + 两个命令），C# 侧约 150 行客户端；GPL 义务：补丁源码必须公开（`SOURCE.txt` + diff）。

**兼容策略**：无 IPC 时（旧宿主/用户自备版本）自动退回方案 A，并在设置里标注「面板功能降级」。

### 17.6 参数修改的读写与同步

游戏里面板改的是 `OptiScaler.ini`（或 OptiScaler 运行时状态），与 GameLocker 磁贴参数是两套存储，必须定一个事实源。

**建议规则**：

1. **启动前**：GameLocker 全量写 ini（磁贴参数优先），并记录写入后的 `LastWriteTime` + 关键键值快照。
2. **面板中**：不写 ini（避免和 OptiScaler 抢写）。
3. **退出面板后**：等 500ms（OptiScaler 可能延迟落盘），比较 mtime/键值：
   - 有变化 → 回读并映射回 `LaunchItem.OptiHost`，Toast「游戏内参数已同步」；
   - 无变化 → 不动。
4. **下次启动前**：若检测到「ini 比磁贴新」且未同步 → 提示二选一：「用磁贴参数启动 / 用上次游戏内设置」。

**回读映射**（只回收 GameLocker 建模的键，其余保留在 ini 里不动）：

| ini 键 | 映射到 |
|---|---|
| `[FrameGen] FGOutput` + `FGNvngxReplacement` | `FgMode`（反向查 §7.4 表） |
| `[DLSSG]/[XeFG] InterpolationCount` | `MfgCount` |
| `[Upscalers] Dx12Upscaler` + `[FSR] UpscalerIndex` | `UpscalerMode` |
| `[Upscale] Method / Level` | `Upscale / UpscaleLevel` |
| `[Sharpness] Sharpness`、`[Upscale] NisSharpness` | `Sharpness` |
| `[Menu] ShortcutKey/FGShortcutKey` | `MenuKey` 等 |
| 其他（DLSS preset、OptiFG HUDFix、FSRFG AllowAsync…） | 不入模，保留为「高级自定义」 |

> 特别地：若面板内改了菜单键（`[Menu] ShortcutKey`），回读后必须同步写回 `OptiCaptureHost.ini` 的 `[General] MenuKey` 与 `[Shortcuts] Menu`，否则宿主下一次注入/跟踪的就不是同一个键。

**可热改 vs 需重开**（待实测，先按此表做 UI 提示）：

| 参数 | 面板内即时生效 | 备注 |
|---|---|---|
| FG 开/关 | 大概率 | OptiScaler 自己有 `FGShortcutKey` |
| 上采样器 / 超分方式 | 部分 | 多数需重开宿主 |
| DLSS/FSR preset、锐化 | 部分 | 视 OptiScaler 版本 |
| 插帧模式（FGOutput） | 否 | 需重开宿主 |
| 呈现模式 / 倍率 | 否 | 需重开宿主 |

**落盘风险**：OptiScaler 是否在面板改参时立刻写 ini，取决于它的版本（有的只在 Apply / 关闭菜单 / 进程退出时写）。若宿主被强杀，只在内存里的改动会丢失。对策：

1. 退出面板后检查 `OptiScaler.ini` 的 mtime，没变就提示「本次修改可能未保存」；
2. 停止宿主坚持 `WM_CLOSE` 优雅退出，给 OptiScaler 保存/卸载的机会；
3. 产品定位上，把面板改参定义为「临时调试」，长期参数仍建议在 GameLocker 编辑页设置（可回读同步过去）。

### 17.7 输入、焦点与并发边界

| 场景 | 处理 |
|---|---|
| 面板中按 A/Y | 不触发插帧启停（语义让位给「返回游戏」） |
| 面板中切 Alt-Tab | 允许；回来仍保持面板模式 |
| 面板中游戏最小化 | 宿主 `KeepGameVisible` 会还原；GameLocker 不额外抢焦点 |
| 面板中游戏退出/宿主崩溃 | 状态机回 Idle，清 `menuMode`，恢复 dock |
| 面板中按停止（一键插帧的停止路径） | 先 `MENU_EXIT` 再停宿主，避免焦点留在覆盖层 |
| 锁屏/休眠 | 清 `menuMode`；恢复后不自动重开面板 |
| 手柄 | 暂停 GameLocker 手柄→游戏映射；手柄组合键仍可退出 |

### 17.8 文案

| 时机 | 文案 |
|---|---|
| 进入 | 已打开 OptiScaler 面板 · 鼠标/键盘可调参 · 按 {键} 返回游戏 |
| 退出 | 已返回游戏 |
| 热键被占用 | 面板快捷键被系统/游戏占用，请到设置里换一个 |
| 无法聚焦 | 系统拒绝切换焦点：请点一下游戏画面，或以管理员运行 |
| 同步完成 | 游戏内参数已同步到本组件 |
| 旧版本无 IPC | 当前宿主不支持面板状态回传，已使用兼容模式 |

### 17.9 测试用例

1. 进入面板：键盘/鼠标可用、游戏键鼠暂停、覆盖层不被还原成穿透。
2. 面板内改 FG/超分/锐化 → 退出 → 是否生效（区分热改/需重开）。
3. 面板内用 `Insert` 关闭 → 再按退出键：不重复打开（B）/ 行为符合文案（A）。
4. 热键冲突（游戏或其他软件占用 F10/F11）→ 提示与降级。
5. 面板中 Alt-Tab、锁屏、游戏最小化。
6. 面板中游戏退出、宿主崩溃、GameLocker 退出。
7. 面板中 dock/手柄映射不误触、不抢焦点。
8. 连续进出 20 次：焦点不错乱、无残留菜单、无句柄泄漏。
9. 回读同步：面板改 3 项 → 退出 → 磁贴参数与 ini 一致；再启动不覆盖。
10. 双屏/DPI 150%：面板鼠标坐标与画面一致。
11. 落盘：面板改参后不关菜单直接停宿主 → 重启后参数是否保留；强杀 vs `WM_CLOSE` 对比。

### 17.10 风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| 焦点抢不回（Windows 前台锁） | 键鼠失效 | `ForceForeground` 兜底 + 文案「点一下画面」 |
| 菜单残留（状态未知） | 画面被挡 | 方案 B 跟踪；方案 A 文案引导 |
| 热键被占用 | 功能失效 | 启动时读日志检测注册失败 + 换键 |
| 参数双写冲突 | 设置被覆盖 | 启动前提示 + 退出回读同步 |
| 反作弊对焦点切换/合成输入敏感 | 封号投诉 | 风险提示；合成输入仅用于宿主热键 |
| 补丁维护成本 | 升级回归 | 补丁尽量小、单独文件、写测试 |

### 17.11 与一键插帧/状态机的关系

- `Active` 下新增子状态 `面板中`（`menuMode=true`），磁贴显示「面板中」。
- 停止宿主：`Active(面板中)` → `MENU_EXIT` → `Stopping`。
- 宿主自退/崩溃：清 `menuMode`，Toast 区分「插帧已停止」与「面板已关闭」。

### 17.12 决策补充

| # | 问题 | 建议 |
|---|---|---|
| D10 | 面板触发方式 | 手柄组合优先 + 底栏按钮兜底；键盘热键可配 |
| D11 | 是否加 IPC 补丁 | **建议加**；否则面板体验只能做到「能用但不可靠」 |
| D12 | 面板内参数是否回读 | **回读并同步**；未建模键保留 |
| D13 | 菜单关闭键 | 实测 `Insert` / `Esc` 哪个能关；决定 `MENU_EXIT` 的按键序列 |

---

*本文档仅为设计，未修改任何代码。*
