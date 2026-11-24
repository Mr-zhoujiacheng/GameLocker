# 小黄鸭：用 OptiCaptureHost 替换 FGHost

## 目标

保留「小黄鸭」磁贴、A 键开关和原有 LSFG 路线。将原先由 FGHost 提供的「XeSS FG（Magpie）」和「FSR3 FG（Magpie）」入口合并为「OptiScaler」，验收后从正式版本移除 FGHost 及其专用运行库。

## 接入方案

1. **独立运行。** GameLocker 直接启动 `OptiCaptureHost.exe`，不集成上游启动器。宿主用 Windows Graphics Capture 捕获游戏窗口，在自己的 D3D12 覆盖层里运行 OptiScaler（`dxgi.dll`），不向游戏进程注入。首版支持 Windows 10 2004+ 或 Windows 11（x64）、DX12 显卡，游戏须为窗口化或无边框模式。
2. **先跑通一键开关。** 固定一套验证过的宿主、OptiScaler 和运行库版本，在用户可写目录准备两份 INI。按 A 时确定游戏窗口并传 `--hwnd` 启动宿主；再按 A 时关闭宿主。用当前进程的状态文件判断宿主就绪，结合帧率监控验证插帧；未确认时只显示「宿主运行中」。缺依赖、启动失败或超时给出明确提示；游戏退出后清理覆盖层和状态。
3. **迁移和清理。** 在 [LaunchItem.cs](../Models/LaunchItem.cs#L414) 中将旧 `XESS` / `FSR3` 配置迁移到 `OPTISCALER` 的已验证默认配置，不照搬旧光流和同步参数；`LSFG` 不变。在 [LosslessScalingService.cs](../Services/LosslessScalingService.cs#L440) 替换 Magpie 分支，同步更新设置、磁贴状态、前台窗口排除和帧率监控。通过验收后，移除旧进程管理、旧设置入口，以及 [构建](../GameLocker.csproj#L59) 和 [安装包](../GameLocker.iss#L16) 中的 FGHost 文件与校验。

## 验收与发布

- 同一场景对比关闭和开启插帧：输出帧率确实提高，且无需手动打开 OptiScaler 菜单。再验证反复开关、切回游戏、游戏退出和缺运行库时的表现；旧配置可迁移，LSFG 不受影响。
- 设置页保留 `LSFG` 和 `OptiScaler` 两种引擎。OptiScaler 提供帧生成和 DX12 超分两项配置；不接管 RTSS，也不改变游戏窗口尺寸。随包分发 GPL-3.0 组件时提供对应源码和许可声明；第三方运行库须逐项核对再分发条款。

## 实现步骤清单

最新范围：已按要求移除旧 FGHost；LSFG 本轮不改动、不实测。下面清单保留发布和回归项目，不代表已发布包含 OptiScaler 的新安装包。

- [x] **1. 验证并固定默认配置（不改 GameLocker 代码）。** 选定宿主、OptiScaler 和所需运行库的版本，记录来源、文件清单和两份 INI。用上游启动器生成一套配置作为参考，再退出启动器，直接用 `--hwnd` 启动宿主。**验收：**在同一窗口化游戏、同一场景和相同限帧条件下对比插帧开关，记录捕获和输出帧率；不打开菜单也能确认插帧生效。
- [ ] **2. 准备运行目录和依赖。** 新增 `OptiCaptureHostService` 的准备逻辑：将宿主和依赖放在独立的用户可写目录，按第 1 步的清单检查文件，启动前写入 `OptiCaptureHost.ini`、`OptiScaler.ini`，保留日志用于排障。**验收：**普通用户可启动；移除任一必需文件时，提示指出具体缺项，不启动宿主，也不影响 LSFG。
- [ ] **3. 实现启动、停止和清理。** 服务接收已校验的游戏 HWND，绑定目标和本次子进程，防止并发重复启动。启动中支持取消；再次切换时优先请求宿主正常退出，超时只终止本次启动的宿主。游戏关闭、宿主异常退出或 GameLocker 退出时清理会话。**验收：**连续启停、启动中取消、超时和退出后，都没有残留覆盖层或错误的运行状态。
- [ ] **4. 接入前台与帧率监控。** 在 [ForegroundWatcherService.cs](../Services/ForegroundWatcherService.cs#L52) 排除宿主覆盖层，保留原游戏目标；在 [MonitorService.cs](../Services/MonitorService.cs#L520) 分开跟踪游戏和宿主的帧率。结合属于本次进程的最新状态和帧率区分「启动中」「宿主运行中」「插帧中」。**验收：**过期状态、覆盖层获得焦点、无有效帧率数据或仅有宿主运行时，都不会误报插帧。
- [ ] **5. 迁移旧配置并更新设置页。** 在 [LaunchItem.cs](../Models/LaunchItem.cs#L414) 定义 `OPTISCALER`，在 [ConfigService.cs](../Services/ConfigService.cs#L20) 将 `XESS` / `FSR3` 一次性迁移到该引擎和第 1 步的默认配置。在 [设置页](../AddItemPage.xaml#L1050) 及[保存逻辑](../AddItemPage.xaml.cs#L2600) 只保留 LSFG 和 OptiScaler，不显示 Magpie 参数。**验收：**两类旧配置升级后都能正常打开和保存；重启不重复迁移，现有 LSFG 和新条目的默认值不变。
- [ ] **6. 切换 A 键与磁贴。** 在 [LosslessScalingService.cs](../Services/LosslessScalingService.cs#L440) 显式按引擎分派，保留 LSFG 原路径；在 [MainPage.xaml.cs](../MainPage.xaml.cs#L4860) 接入启动、取消、停止和错误提示，在 [TileStyle.cs](../Services/TileStyle.cs#L2219) 展示对应状态。**验收：**OptiScaler 路线按 A 能开、再按能关；启动失败时有具体原因，只有确认插帧才提示「已开启插帧」。
- [ ] **7. 做回归，决定是否移除旧后端。** 用旧配置升级和新配置安装分别验证一键开关、窗口化和无边框、切回游戏、游戏退出、独占全屏提示、缺依赖及宿主异常退出；同场景对比插帧前后，并实测 LSFG 开关。**验收：**不用手动打开 OptiScaler 菜单即可插帧，LSFG 行为不变，失败能恢复并再次启动。
- [ ] **8. 删除 FGHost 并验收发布包。** 去掉旧 Magpie 进程管理和专用字段（保留旧引擎值的迁移识别）；移除 [GameLocker.csproj](../GameLocker.csproj#L85) 的旧复制任务和 [GameLocker.iss](../GameLocker.iss#L16) 的 FGHost 校验，升级安装时只清理旧安装目录中的 `FG`。按已核准的清单打包新宿主和依赖，或提供独立准备方式，并附对应源码获取方式和许可声明。**验收：**干净构建和覆盖安装均不含 FGHost；安装后重复验证两条引擎路线。

## 本轮 UI 与旧后端清理

- [设置页](../AddItemPage.xaml#L1044) 只显示 `LSFG` 和 `OptiScaler` 两个引擎名称。OptiScaler 的帧生成可选 XeFG 2×、FSR FG、DLSS FG 或关闭；DX12 超分可选 AMD FSR 自动版本、FSR 3.1、Intel XeSS 或 NVIDIA DLSS。
- [OptiCaptureProfile.cs](../Services/OptiCaptureProfile.cs) 在下次启动宿主时应用保存的选项，并在选中的模式缺少额外运行库时拒绝启动。选项写的是 OptiScaler 后端设置，不改变游戏窗口分辨率。
- 已移除 FGHost 的后端实现、专用字段和构建复制流程，保留旧引擎值的迁移兼容。[安装脚本](../GameLocker.iss#L46) 排除旧构建残留，并在覆盖安装时清理安装目录里的 `FG`。隔离安装测试证实旧 `FG` 被移除，其他文件保留。
- x64 构建、配置映射、缺依赖和宿主启停测试通过。未重新验证所有可选 GPU 模式；正式包仍未包含 OptiScaler 运行库，本轮不改 LSFG。

## 第 1 步：A/B 实测（历史）

- 候选：[OptiCaptureHost v2.10.80 完整包](https://github.com/tegraultra/OptiCaptureHost/releases/tag/v2.10.80)，归档 SHA-256 `e61853c6e9b577f87dfd505ae669656d241562ffce93f37caa4235203e878120`。包内 OptiScaler `0.1.39.1`、XeFG `1.3.1.78`、XeLL `1.3.2.10`。仅用于本地验证，不进入构建或安装包。
- 把同一发行包放进两个独立目录 `off` 和 `on`。保留包内 XeFG 2× 和相同的宿主节拍；两边都关闭宿主配置中的限帧选项。只有 `off` 将 `OptiScaler.ini` 的 `[FrameGen] Enabled` 改为 `false`，`on` 保持 `true`。
- 在窗口化或无边框的离线游戏里，用游戏自身限帧把帧率设为约 30 FPS，停在同一段持续运动的画面。用任务管理器确定游戏 PID；启动脚本后立即切回游戏，不开 OptiScaler 菜单，直到两轮结束。

从项目根目录用 [准备脚本](../tools/Prepare-OptiCaptureHostProbe.ps1) 为原版发行包生成两组配置。它会校验 SHA-256，只写入一个新目录，不会覆盖现有测试结果：

```powershell
$root = Join-Path $env:LOCALAPPDATA 'GameLocker\OptiCaptureHost-validation\v2.10.80'
$probe = Join-Path $root ('ab-' + [guid]::NewGuid().ToString('N'))
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Prepare-OptiCaptureHostProbe.ps1 `
    -ArchivePath (Join-Path $root 'OptiCaptureHost-2.10.80_Full.Pack.rar') -OutputDirectory $probe
```

用 [独立采样脚本](../tools/Test-OptiCaptureHost.ps1) 实测，把 `1234` 换成游戏 PID：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-OptiCaptureHost.ps1 `
    -GameProcessId 1234 -OffDirectory (Join-Path $probe 'off') -OnDirectory (Join-Path $probe 'on') `
    -ReportPath (Join-Path $probe ('result-' + [guid]::NewGuid().ToString('N') + '.json'))
```

脚本只接受当前宿主的新鲜、游戏在前台的样本。两轮须使用相同刷新率；关闭组输出应接近捕获和宿主自身的 `Present` 帧率，开启组输出中位数须比关闭组输出、开启组捕获和 `Present` 帧率都至少高 25%。JSON 保留原始样本、判定或失败原因，不覆盖旧报告；退出码 `0` 表示通过、`2` 表示有两组数据但未能确认、`1` 表示运行出错。已验证准备脚本生成每组 86 个文件，唯一差异是 `[FrameGen] Enabled`；采样自测及模拟报告测试均通过，真实游戏的验收仍待验证。

### Valheim 实测结果

在 165 Hz 屏幕上，每组采集 8 个有效前台样本。OptiCaptureHost v2.10.80 直启，使用 XeFG 2×，无需打开 OptiScaler 菜单：

| 组别 | 捕获 FPS | 宿主 Present FPS | DXGI 输出 FPS |
|---|---:|---:|---:|
| 关 | 29.96 | 29.96 | 29.96 |
| 开 | 29.98 | 29.98 | 59.96 |

采样中位数通过第 1 步验收。已在开发机准备相同配置至 `%LOCALAPPDATA%\GameLocker\OptiCaptureHost\v2.10.80`；其他开发机可对已校验的完整包运行准备脚本，加 `-RuntimeOnly` 并将该目录作为 `-OutputDirectory`。该模式只用于本地开发，不代表完整包获准再分发。

### 早期连通性记录

本机连通性实测：v2.10.80 直接捕获了独立动画窗口，选中 RTX 4060 Laptop GPU，宿主正常呈现约 24 FPS，收到关闭请求后退出码为 0。两次试跑时测试窗口都未获得系统前台焦点，`OFF` 没有合格样本，因此未执行 `ON`，不能据此确认插帧。采样脚本现会在超时时报告样本数，并区分失焦、无当前宿主状态和有效帧率样本不足；诊断自测已通过。

已先实现第 4 步中可独立验证的 [C# 状态读取和插帧迹象判断](../Services/OptiCaptureHostStatus.cs)。读取使用上游相同的 `GetTickCount64` 时钟；只接受当前宿主启动后 2.5 秒内的状态。连续 3 个前台样本的 DXGI 帧率比捕获帧率和宿主自身的 Present 帧率都高至少 25% 时，才报告插帧迹象。已通过独立测试（错 PID、旧状态、失焦、重复样本和真实文件读取）；未接入正式后端，不能代替真实游戏验收。

已实现第 2、3 步的 [宿主预检和启停模块](../Services/OptiCaptureHostService.cs)。模块使用明确提供的测试目录，不自动下载、复制或改写运行文件。用 v2.10.80 和独立动画窗口实测：缺依赖及无效窗口会在启动前报错，取消启动会回收子进程，收到新鲜状态才能判定就绪，重复启动会被拒绝，重复停止和目标窗口关闭后均无宿主残留。正式运行库准备、游戏验收及磁贴接入仍未完成。

### 小黄鸭接入进度

开发构建已接入 OptiScaler（固定 XeFG 2×）：设置页保留 LSFG，旧 XESS/FSR3 配置迁移到 OptiScaler；A 键管理独立宿主，磁贴和监控区分启动中、宿主运行中和已检测到插帧。x64 构建、迁移测试和独立宿主启停回归已通过。

Valheim 磁贴回归通过：调试版启动后旧 XESS 磁贴实际迁成 `OPTISCALER`；按 A 启动的宿主确由调试版持有，前台状态实测捕获 29.96 FPS、DXGI 输出 58.92 FPS。再次按 A 提示关闭，宿主日志确认正常退出（退出码 0）。剩余：实测 LSFG 开关、确定发行依赖和许可清单，再清理旧 FGHost 与安装包。

### 发布候选：精简运行库

[准备脚本](../tools/Prepare-OptiCaptureHostProbe.ps1) 增加 `-CoreFilesOnly`。生成两组各 7 个文件、约 54.6 MiB：宿主、OptiScaler DLL、两份 INI、Intel XeFG、Intel XeLL 和 Intel 许可。关闭 ASI 插件和 MFG 解锁，不提取上游启动器、额外超分库或可选插件。两组仅在插帧开关上不同；关闭组已通过实际宿主启停。Valheim 精简候选 A/B 每组 8 个前台样本，捕获中位数 30.04 / 29.98 FPS，DXGI 输出 30.04 / 59.96 FPS，无需开菜单，通过验收；未覆盖开发目录。

两份 Intel DLL 的 Authenticode 签名有效。随包前仍要保留 [Intel 条款](https://github.com/intel/xess/blob/main/LICENSE.txt) 和相关告知，使用未修改的二进制，禁用解锁。宿主源码对应 [v2.10.80 标签](https://github.com/tegraultra/OptiCaptureHost/tree/v2.10.80)。完整包里的 OptiScaler DLL 文件版本虽为 `0.1.39.1`，但 SHA-256 `8570433ba0a5293298ebf43eee494e64daa283e9a2fd5a20b789ce6cfb27520d` 与 [v0.1.39 公开发行件](https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/releases/tag/v0.1.39) 内的 DLL 完全一致。`-CoreFilesOnly -OfficialScalerArchive <公开 ZIP>` 支持从该发行件直接生成候选；已逐项核对，二进制和配置值与通过 A/B 的开启组相同。正式分发仍须提供对应源码、构建说明和许可告知，不默认随包。

### 本地运行库安装预览

[Inno 预览脚本](../tools/OptiCaptureHostRuntimePreview.iss) 只打包上面的 7 个文件和 [预览告知](../tools/OptiCaptureHostRuntimePreview.NOTICE.md)，不安装或启动 GameLocker，不删除旧 FGHost。用 Inno Setup 6.7.3 编译并静默安装到 `%LOCALAPPDATA%\GameLocker-OptiCaptureHost-Preview` 的新子目录，8 个文件的哈希全部一致；预览范围外的测试目录被拒绝（退出码 7，没有创建目录）。安装包约 22.1 MiB，仅用于本地验证。

从项目根目录执行；`$coreRuntime` 指向用 `-CoreFilesOnly -OfficialScalerArchive -RuntimeOnly` 生成的开启组，`$output` 指向单独的预览输出目录：

```powershell
$inno = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
$notice = (Resolve-Path .\tools\OptiCaptureHostRuntimePreview.NOTICE.md).ProviderPath
& $inno "/DRuntimeSource=$coreRuntime" "/DPreviewNotice=$notice" "/O$output" .\tools\OptiCaptureHostRuntimePreview.iss
```

从预览目录直接启动安装出来的宿主，与同配置关闭组在 Valheim 对照：每组 8 个前台样本，捕获中位数 30.00 / 29.99 FPS，DXGI 输出 30.00 / 59.97 FPS（165 Hz），无需打开上游菜单。安装后的运行文件通过插帧验收。

公开发行的源码引用已固定：OptiCaptureHost [f2ee50b](https://github.com/tegraultra/OptiCaptureHost/tree/f2ee50b48364ca4a27e94d30534e207deb545b68)，OptiScaler [956bb71](https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/tree/956bb7198e819da857710f7f9ace223790a7ba55)。固定引用不等于已备齐公开分发用的源码、构建说明和告知。

正式安装脚本仍保留原有流程。本机 Steam 库未检测到 Lossless Scaling，磁贴上也没有手动指定有效程序。启用正式发布前，需在可运行 `LosslessScaling.exe` 的环境实测 LSFG 开关，备齐对应源码和告知，再验证正式覆盖安装仅清理安装目录里的旧 FGHost。

### 启停回归（不验证插帧效果）

从项目根目录运行 [宿主回归脚本](../tools/Invoke-OptiCaptureHostSmoke.ps1)。默认只测试状态和缺依赖；传入准备好的 `off` 目录后，还会用独立动画窗口测试实际宿主启停，不需要运行游戏。两种模式均已通过。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Invoke-OptiCaptureHostSmoke.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Invoke-OptiCaptureHostSmoke.ps1 `
    -RuntimeDirectory (Join-Path $probe 'off')
```

参考：[上游 README](https://github.com/tegraultra/OptiCaptureHost/blob/main/README.md) · [第三方许可说明](https://github.com/tegraultra/OptiCaptureHost/blob/main/NOTICE.md) · [项目原详细草案](optiscaler-host-design.md)
