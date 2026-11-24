# OptiScaler 游戏内菜单：简体中文

此构建针对 OptiCaptureHost **v2.10.80** 搭配的 Native OptiScaler。它汉化的是运行时的 Dear ImGui 菜单，而不是 GameLocker 本身的设置页面，也不影响单独的游戏注入组件。

## 支持范围

- [上游 OptiScaler F5 fork v0.1.39](https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass/tree/956bb7198e819da857710f7f9ace223790a7ba55)，源码提交 `956bb7198e819da857710f7f9ace223790a7ba55`。受支持的原版代理 DLL SHA-256：`8570433BA0A5293298EBF43EEE494E64DAA283E9A2FD5A20B789CE6CFB27520D`。
- 中文模块：[dxgi.zh-CN.dll](../Resources/OptiScalerZhCN/dxgi.zh-CN.dll)。它与原版分别保存，[配套清单](../Resources/OptiScalerZhCN/OptiScaler.zh-CN.json)记录两份 DLL 的 SHA-256。不得将不匹配的原版 DLL 替换为此模块；未知版本应继续使用其原版英文菜单。
- [简体中文词表](../tools/OptiScalerZhCN/zh-CN.tsv)包括导航、超分辨率、帧生成、帧率、画质、叠加层、输入、其他选项及部分帮助、提示和插件状态。未覆盖的高级诊断、插件自有文本、产品名和文件名可能保留英文。
- 翻译在 ImGui 测量和绘制文字时执行，控件仍使用原来的英文标签及 `##`/`###` ID 后缀。保存的页面键、INI 键、内部取值和运行时属性不变。可变参数格式化保留原参数顺序、宽度和类型。
- CJK 字体是 [Noto Sans SC](https://github.com/google/fonts/tree/2894aab31764f10f29c421bdfd2340d3b382d384/ofl/notosanssc) 的重命名子集，嵌入 DLL 的 Windows 资源，并合并至菜单的经典、现代、默认及自定义字体。保留原字体的拉丁字符；机器上不必预装中文字库。见[许可证与修改告知](../Resources/OptiScalerZhCN/NOTICE.md)。

## 从源码重建

需要 Windows、Git、Visual Studio 2022 Build Tools（x64 C++、Windows SDK）、Python 3.10+ 及 `fonttools==4.59.2`，并能访问上游 Git 和字库。工作目录应使用短路径、不要含空格（上游 MSBuild 的打包命令未完全引用路径）。输出与工作目录必须是尚不存在的目录，不要指向正在使用的运行库。

```powershell
python -m pip install fonttools==4.59.2
$original = "$env:LOCALAPPDATA\GameLocker\OptiCaptureHost\v2.10.80\dxgi.dll"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-OptiScalerZhCN.ps1 `
  -OriginalDxgi $original -WorkDirectory C:\opti-zh-work -OutputDirectory C:\opti-zh-out
```

[构建脚本](../tools/Build-OptiScalerZhCN.ps1)校验原版 DLL、源码提交、子模块版本、字体和字体许可哈希。[源码生成器](../tools/OptiScalerZhCN/generate.py)只修改临时 checkout：在固定锚点增加渲染翻译及字体合并，生成 UTF-8 字符串表和字体，并检查所有非 ASCII 译文字符是否都有字形。随后运行上游 Release x64 MSBuild，只发布本地化模块、清单和许可证。MSBuild 的版本资源包含构建时间，工具链和路径也可能影响成品字节；每次发布都要更新并核对新成品的 SHA-256。构建不下载或改写已安装的运行库。

## 隔离验证

[验证脚本](../tools/Test-OptiScalerZhCN.ps1)比较二进制导出，以数据文件模式读取实际 DLL 的嵌入式字体，并编译原生 ImGui 验证程序，检查两种字号的字形、可变参数、英文回退、绘制输出以及原控件 ID。它不启动宿主，也不写入运行库。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-OptiScalerZhCN.ps1 `
  -SourceDirectory C:\opti-zh-work\source -OriginalDxgi $original `
  -LocalizedDxgi C:\opti-zh-out\dxgi.zh-CN.dll -OutputDirectory C:\opti-zh-test
```

交互式确认使用单独的运行库副本和测试窗口。启动 `OptiCaptureHost.exe` 之后，按 `Ctrl+Shift+F10` 或 `Insert` 打开 OptiScaler 菜单；检查帧生成页、保存设置按钮，以及是否能在 100% 和 150% 缩放下看到清晰中文。不在游戏目录或已安装的运行库中替换模块。
