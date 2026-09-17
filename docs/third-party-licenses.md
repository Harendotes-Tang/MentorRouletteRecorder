# 第三方许可证与本项目许可证结论 / Third-Party Licenses

本文档记录本软件全部第三方依赖的许可证状况，并据此给出本项目自身的许可证结论。
文档同时登记与对外分发相关的决策、生效日期与未决事项，面向维护者，
以及需要评估分发合规性的读者。

> 调查日期：**2026-09-04**。所有版本号来自本机实际还原的 NuGet 包与实际安装的 Qt。
> 面向用户的简版清单见仓库根目录的 [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)。

## 1. 结论摘要

| 项目 | 结论 |
|---|---|
| 本项目许可证 | **GPL-3.0-or-later** |
| 原因 | 链接了 GPL-3.0 的 `Machina.FFXIV`（含 `Machina`）与 GPL-3.0-only 的 Qt Graphs 模块 |
| 闭源 / 商业分发 | **不可能**（在当前依赖组合下） |
| `PUBLIC_DISTRIBUTION_READY` | **true**（2026-09-07 起；11 项前置条件见 §7） |
| Npcap | **不得随本软件分发**，只做检测并给出安装指引 |
| 游戏美术素材 | © SQUARE ENIX；依据 Materials Usage License 随包分发，附来源与版权声明，仅限非商业用途（§9） |
| 副本中文名的上游 | `ffxiv-datamining-cn` **没有 LICENSE**，再分发许可未决（§10） |
| 测试期依赖 | `JsonSchema.Net` 及其依赖链（MIT）**仅用于测试**，不进产物（§5.1） |

## 2. Machina.FFXIV（抓包与协议解码）

| 项 | 值 |
|---|---|
| NuGet 包 id | `Machina.FFXIV` |
| 最新稳定版（调查时） | **2.4.7.7** |
| 作者 | Ravahn |
| 许可证 | **GPL-3.0**（nuspec 为 `<license type="file">LICENSE.md`；该 LICENSE.md 即 GNU GPL v3 全文，673 行） |
| 项目地址 | https://github.com/ravahn/machina |
| 目标框架 | `netstandard2.0` |
| 依赖 | `Machina` `[2.3.1.3, )` |
| 在 net8.0 上是否可用 | **可用。** `net8.0-windows` 项目可还原并编译通过，无 `NU1701` 兼容性警告，输出目录中正确复制 `Machina.dll` 与 `Machina.FFXIV.dll` |

基础库 `Machina` 2.3.1.3 同样采用 GPL-3.0（`<license type="file">LICENSE.md`），
目标框架为 `netstandard2.0`，无外部 NuGet 依赖。

### 2.1 抓包入口（2026-09-07 首包修复）

实时原始报文由 **SharpPcap 6.3.1** 的只读 Npcap API 读取，随后进入本软件自有的有界内存缓冲。
Machina 继续负责已确认连接的 IP/TCP/FFXIV bundle 解码。发送、文件抓包、Raw Socket 与
Deucalion 均未启用。§2.1.1 记录旧 `FFXIVNetworkMonitor` 的模式约束，
新入口不再使用其按连接打开 socket 的路径。

该入口引入的依赖与源码证据如下：

| 组件 | 版本 / 许可 | 对应源码 |
|---|---|---|
| SharpPcap | 6.3.1 / MIT | [bfedf297](https://github.com/dotpcap/sharppcap/tree/bfedf297e7410ffcf44e05d14f7fbef304f20895) |
| PacketDotNet | 1.4.8 / MPL-2.0 | [690707ce](https://github.com/dotpcap/packetnet/tree/690707ce56d6e9c266daf6236c4f76ac5035334c) |
| System.Memory | 4.6.3 / MIT | [f62ca000](https://github.com/dotnet/maintenance-packages/tree/f62ca0009b038cab4725a720f386623a969d73ad) |
| System.Text.Encoding.CodePages | 9.0.5 / MIT | [e36e4d1a](https://github.com/dotnet/runtime/tree/e36e4d1a8f8dfb08d7e3a6041459c9791d732c01) |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 / MIT | NuGet 包内许可声明 |

SharpPcap 与 PacketDotNet 均不修改上游源码，表中前两个仓库链接指向所分发版本的源码。
许可证文本以及 CodePages 附带的第三方声明存放于 `docs/licenses/`，打包脚本递归随附该目录。
版本、许可证与源码提交号取自本机 NuGet 包的 `.nuspec`，实际依赖版本取自 Collector 的
`obj/project.assets.json`。本节仅登记抓包包装层引入的依赖，不重新评定既有的分发前置条件。

### 2.1.1 原 Machina 抓包模式（MonitorType）

`FFXIVNetworkMonitor` 的配置属性（来自上游源码 `Machina.FFXIV/FFXIVNetworkMonitor.cs`）：

| 属性 | 类型 | 上游默认值 | 本项目的设定 |
|---|---|---|---|
| `MonitorType` | `NetworkMonitorType` | `RawSocket` | **必须显式设为 WinPCap（Npcap）** |
| `ProcessID` / `ProcessIDList` | `uint` / `ICollection<uint>` | — | 指向 FFXIV 进程 |
| `WindowName` | `string` | `"FINAL FANTASY XIV"` | 保持默认 |
| `UseDeucalion` | `bool` | `false` | **必须保持 `false`** |
| `OodleImplementation` | `Oodle.OodleImplementation` | `FfxivTcp` | 见 §2.3，决策项 `DEC-OODLE-01` |
| `OodlePath` | `string` | 指向游戏安装目录 | 见 §2.3 |

本项目**不使用** `RawSocket` 回退：Npcap 缺失时直接返回 `ERR_NPCAP_MISSING`。
静态规则 `CAP-003` 禁止源码中出现 `NetworkMonitorType.RawSocket`。

### 2.2 Deucalion（注入式钩子）：本项目禁用

较新版本的 Machina.FFXIV 支持 **Deucalion**，即一个注入到游戏进程的 DLL 钩子。
`Machina.FFXIV 2.4.7.7` 包的 `lib/netstandard2.0/` 目录中附带原生载荷
**`deucalion-1.5.0.dll`**，该文件在还原后的包目录中可以确认，
且默认构建会将其复制到输出目录。

上游的 `Machina.FFXIV/Deucalion/DeucalionClient.cs` 仅通过命名管道 `deucalion-<pid>`
与已注入的钩子通信，实际的注入逻辑位于 `DeucalionInjector`。

本项目的处理方式如下：

1. `UseDeucalion` 恒为 `false`（并有断言与静态规则 `DEU-001`）；
2. 不引用任何 `Deucalion*` 类型（`DEU-002`）；
3. 仓库根 `Directory.Build.targets` 的 `StripInjectionPayloadFromOutput`
   把 `deucalion-*.dll` 从**所有**构建输出中删除（`DEU-003` 禁止再次引用）；
4. IPC 的 `CaptureStatus.injected_hook_enabled` 固定为 `false` 并展示在诊断页。

详见 [privacy-boundary.md](privacy-boundary.md) §3。

### 2.3 Oodle：已定稿决策（`DEC-OODLE-01`）

FFXIV 流量经 Oodle 压缩。Machina 的 `OodleImplementation` 枚举取自上游
`Machina.FFXIV/Oodle/OodleFactory.cs`，各取值如下：

| 取值 | 实现类 | 做法 |
|---|---|---|
| `LibraryTcp` / `LibraryUdp` | `OodleNative_Library` | 加载独立的 `oo2net_9_win64.dll` |
| `FfxivTcp`（默认） | `OodleNative_Ffxiv` + `SigScan` | 将游戏 exe **复制到临时目录**，由 `LoadLibraryW` 把该副本加载进**本软件自身的进程**，再执行特征扫描 |
| `KoreanFfxivUdp` | `OodleNative_Ffxiv` + `KoreanSigScan` | 同上，韩服特征 |

上游 `OodleNative_Ffxiv` 的关键调用序列为：`File.Copy(path, _libraryTempPath, true);`，
随后 `NativeMethods.LoadLibraryW(_libraryTempPath)`，再 `_sigscan.Read(_libraryHandle)`。

**边界含义**：该做法**不是**进程注入。它不打开游戏进程，不写入游戏进程的内存，
也不创建远程线程；加载的是磁盘上游戏二进制的一份**副本**，加载目标是本软件自身的进程。
但它确实意味着本软件会**读取并加载游戏的可执行文件**，该行为必须向用户明示。

决策项 `DEC-OODLE-01` 已定稿：**默认取 `FfxivTcp`，用户自备库时优先取 `LibraryTcp`**。
三个选项及其取舍见 [privacy-boundary.md](privacy-boundary.md) §4.1。无论采用哪一项，
本软件都不分发 `oo2net_9_win64.dll`（RAD Game Tools 专有），不分发游戏文件，
临时副本使用后立即删除，并在首次运行页面明确告知用户。

## 3. Qt 6.11.2

在开源许可下，Qt 的**多数**模块采用 **LGPL-3.0**，另有一批模块**只提供 GPL-3.0**。
Qt 官方文档 `https://doc.qt.io/qt-6/licensing.html` 列出的 GPLv3-only 模块包括：

> Qt Canvas Painter、Qt CoAP、**Qt Graphs**、Qt GRPC、Qt HTTP Server、
> Qt Lottie Animation、Qt MQTT、Qt Network Authorization、Qt Qml Compiler、
> **Qt Quick 3D**、Qt Quick 3D Physics、Qt Quick Timeline、Qt Virtual Keyboard、
> Qt Wayland Compositor

本项目使用的模块如下：

| 模块 | 开源许可 | 用途 |
|---|---|---|
| Qt Core / Gui / Qml / **Qt Quick** / **Qt Quick Controls** | LGPL-3.0（亦可 GPL-3.0） | UI 框架 |
| **Qt Graphs** | **GPL-3.0-only** | 图表（仪表盘统计） |
| Qt Charts | GPL-3.0（自 Qt 6.10 起弃用，本项目**不使用**） | — |
| **Qt TextToSpeech** | LGPL-3.0 | 可选的本地语音播报 |
| **Qt Multimedia** | LGPL-3.0 | 播放在线语音取回的 WAV（`QSoundEffect`）；Qt TextToSpeech 本身也依赖它。只随包 Windows 后端 `windowsmediaplugin`，**不随包 FFmpeg**（见下） |
| Qt Svg | LGPL-3.0 | 图标 |
| Qt Quick 3D / Qt ShaderTools | GPL-3.0-only / LGPL-3.0 | **本项目不使用**（避免不必要的 GPL 依赖） |

Qt 的整体许可说明：`https://www.qt.io/licensing/`、`https://doc.qt.io/qt-6/licensing.html`。

**FFmpeg 不随包分发**（2026-09-16 起，界面改版 P4b）。`windeployqt` 默认会为 Qt Multimedia
部署 FFmpeg 后端 `ffmpegmediaplugin.dll`，以及 FFmpeg 的 `avcodec`、`avformat`、`avutil`、
`swresample`、`swscale` 动态库。0.8.0 之前的发行包因 Qt TextToSpeech 依赖 Qt Multimedia，
实际已附带这些文件，本机 `artifacts\` 目录下的 0.2.4 与 0.3.0 可以查证。
本软件只播放 16 位 PCM 的 WAV，不需要任何解码器。因此，`scripts/package.ps1` 以
`--no-ffmpeg --exclude-plugins ffmpegmediaplugin` 调用 `windeployqt`；`main.cpp` 在该变量
未设置时将 `QT_MEDIA_BACKEND` 设为 `windows`；打包输出中另有断言，要求 `multimedia\`
目录下只有 `windowsmediaplugin.dll`，且任何位置都不存在 FFmpeg 文件。
据此，本项目无须为 FFmpeg（LGPL-2.1+，视构建选项可能为 GPL）另附许可与源码说明。
本机 Qt 安装目录内附有 `D:\APPS\Qt\6.11.2\mingw_64\LICENSE` 与 `COPYING.txt`。

> **Qt Graphs 是 GPL-3.0-only**，仅此一项即要求本项目必须与 GPLv3 兼容。
> 若将来需要解除这一约束，唯一的途径是改用非 Qt 的图表方案。
> 购买 Qt 商业许可并不能解除该约束，因为它与 Machina.FFXIV 的 GPLv3 仍然冲突。

### 3.1 MinGW / GCC 运行时

C++ 侧使用 MinGW-w64 GCC 13.1 构建。GCC 运行时库 `libstdc++` 与 `libgcc` 采用
**GPL-3.0 + GCC Runtime Library Exception**。该例外允许将运行时库随非 GPL 程序分发，
对本项目不构成额外限制，因为本项目本身即以 GPLv3 发布。
发布时需随附这些 DLL 及其许可证声明。

### 3.2 Cinzel（内置字体，可随本软件分发）

「艾欧泽亚」界面风格的拉丁文标题与数字使用衬线字体 **Cinzel**。

| 项 | 值 |
|---|---|
| 文件 | `src/Desktop/resources/fonts/Cinzel-Regular.ttf`、`Cinzel-Bold.ttf` |
| 家族 | `Cinzel`（`Theme.headingFamily`） |
| 版权 | Copyright 2020 The Cinzel Project Authors |
| 许可证 | **SIL Open Font License 1.1**（全文随附于 `src/Desktop/resources/fonts/OFL.txt`） |
| 上游 | <https://github.com/NDISCOVER/Cinzel>，即 google/fonts `ofl/cinzel/` 的上游项目 |
| 取用的构建 | 上游 `fonts/ttf/` 的两个静态字重。google/fonts 现在只发布可变字体 `Cinzel[wght].ttf`，静态字重可保证 Qt 在离屏（freetype）后端上稳定获得真正的 Bold 字形 |
| 是否进入构建产物 | **是**。由 `src/Desktop/CMakeLists.txt` 的 `qt_add_resources(... "mr_fonts" ...)` 编入，并由 `main.cpp` 的 `QFontDatabase::addApplicationFont()` 注册 |

OFL 1.1 明确允许再分发与内嵌，条件为：随附许可证全文、不单独售卖字体本身、
衍生版本不得使用保留名称。本项目只做原样内嵌，不修改字体，**满足上述条件**。
与 §9 的游戏美术素材不同，这两个字体文件**可以**保留在公开发布的产物中。

### 3.3 IBM Plex Mono（内置字体，可随本软件分发）

「经典」界面风格（workbench，默认风格）的数字与等宽文字使用 **IBM Plex Mono**。

| 项 | 值 |
|---|---|
| 文件 | `src/Desktop/resources/fonts/IBMPlexMono-Medium.ttf`、`IBMPlexMono-SemiBold.ttf` |
| 家族 | `IBM Plex Mono`（`Theme.plexMonoFamily`，经典风格的 `Theme.numFamily` / `Theme.figureFamily` / `Theme.monoFamily`） |
| 版权 | Copyright © 2017 IBM Corp. with Reserved Font Name "Plex" |
| 许可证 | **SIL Open Font License 1.1**（全文随附于 `src/Desktop/resources/fonts/IBMPlexMono-OFL.txt`，即上游 `LICENSE.txt`） |
| 上游 | <https://github.com/IBM/plex>，发布 `@ibm/plex-mono@2.5.0` 的 `ibm-plex-mono.zip`（字体版本 2.005） |
| 取用的构建 | `fonts/complete/ttf/` 的 Medium（500）与 SemiBold（600）两个静态字重，原样未改；SHA-256 见 `src/Desktop/resources/fonts/README.md` |
| 是否进入构建产物 | **是**。与 Cinzel 同在 `qt_add_resources(... "mr_fonts" ...)` 中，并在同一处由 `QFontDatabase::addApplicationFont()` 注册；只在开发期下载一次，运行时不联网 |

OFL 条件同 §3.2。IBM Plex 的保留字体名为 “Plex”；本项目只做原样内嵌，不修改字体，**满足条件**。

中文标题使用 `Noto Serif SC`，该字体不内置。`main.cpp` 通过
`QFont::insertSubstitutions("Noto Serif SC", {...})` 登记
`Noto Serif CJK SC`、`Songti SC`、`SimSun`、`NSimSun` 作为兜底字体。
这些均为系统字体，**不随本软件分发**。

### 3.4 Lucide（内置图标，可随本软件分发）

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**。

| 项 | 值 |
|---|---|
| 上游 | <https://lucide.dev>，<https://github.com/lucide-icons/lucide>；取自 npm 包 `lucide-static@1.46.0` 的 `icons/*.svg` |
| 许可证 | **ISC**（全文随附于 `src/Desktop/resources/icons/lucide/LICENSE`）。同一文件的后半段说明：Lucide 从 **Feather** 派生的那些图标同时受 **The MIT License (MIT)**，Copyright (c) 2013-present Cole Bemis 约束。本项目带的 24 个里有 9 个在这份名单上：`calendar`、`chevron-left`、`chevron-right`、`clock`、`moon`、`plus`、`radio`、`server`、`x` |
| 版权 | Copyright (c) 2026 Lucide Icons and Contributors；Feather 派生部分另有 Copyright (c) 2013-present Cole Bemis |
| 文件 | `src/Desktop/resources/icons/lucide/*.svg`（原样未改；SHA-256 见同目录 `README.md`） |
| 是否进入构建产物 | **是**，但进入产物的不是 SVG 本身：`tools/lucide-icons/generate.py` 将每个图标的绘制元素生成到 `src/Desktop/qml/components/Lucide.js`。该文件头部带有 ISC 的版权与许可声明，并在包含 Feather 派生图标时一并带上 MIT 的版权与许可声明，随 QML 模块编入 `MentorRecorder.Desktop.exe`。生成器从 LICENSE 文件读取 Feather 名单，`tools/lucide-icons/test_generate.py` 保证生成文件与 SVG、名单一致 |
| 运行时 | 不联网。`AppButton` 将按钮当前的文字颜色写入 SVG 的 `stroke`，并以 `data:` 地址交给 `Image` 绘制 |

ISC 与 MIT 的条件均只有一条：在所有副本中保留版权声明与许可声明。
`Lucide.js` 的文件头同时保留了两份声明，本文件与 `THIRD_PARTY_NOTICES.md` 亦已保留，**满足条件**。

## 4. Npcap：不可随本软件分发

| 项 | 结论 |
|---|---|
| 许可证 | Npcap 专有许可（**不是** BSD，也**不是** WinPcap 的旧许可） |
| 免费版条款 | 个人/内部使用，**最多 5 台机器**，且**不得对外再分发** |
| 例外 | 仅 Nmap、Wireshark、Microsoft Defender for Identity 等被点名的软件可无限制随附 |
| 商业分发 | 需要向 Nmap Software LLC 购买 **Redistribution License**（OEM） |
| 来源 | `https://npcap.com/#download`（含 "The free version of Npcap may be used (but not externally redistributed) on up to 5 systems"） |

**基于上述条款，本项目：**

- **不内置** Npcap 安装包；
- **不自动下载** Npcap；
- **不静默安装** Npcap；
- 只做**检测**，检测对象为注册表 `HKLM:\SOFTWARE\WOW6432Node\Npcap`、`HKLM:\SOFTWARE\Npcap`
  与 `C:\Windows\System32\Npcap\wpcap.dll`。未安装时显示官方站点的安装指引，
  由用户自行安装并自行遵守 Npcap 的许可条款。

`ERR_NPCAP_MISSING` 的用户提示文案见 [capture-diagnostics.md](capture-diagnostics.md) §2。

## 5. .NET 侧依赖清单（本机实际还原的结果）

| 包 | 版本 | 许可证 | 说明 |
|---|---|---|---|
| `Machina.FFXIV` | 2.4.7.7 | **GPL-3.0** | 直接依赖 |
| `Machina` | 2.3.1.3 | **GPL-3.0** | 传递依赖 |
| `Microsoft.Data.Sqlite` | 8.0.11 | MIT | 直接依赖 |
| `Microsoft.Data.Sqlite.Core` | 8.0.11 | MIT | 传递依赖 |
| `SQLitePCLRaw.core` | 2.1.6 | Apache-2.0 | 传递依赖 |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.6 | Apache-2.0 | 传递依赖 |
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.6 | Apache-2.0 | 传递依赖（内含 SQLite 本体，**SQLite 本身是 public domain**） |
| `SQLitePCLRaw.provider.e_sqlite3` | 2.1.6 | Apache-2.0 | 传递依赖 |
| `SharpPcap` | 6.3.1 | MIT | 直接依赖 |
| `PacketDotNet` | 1.4.8 | MPL-2.0 | 传递依赖（SharpPcap） |
| `System.Memory` | 4.6.3 | MIT | 传递依赖 |
| `System.Text.Encoding.CodePages` | 9.0.5 | MIT | 传递依赖（PacketDotNet） |
| `System.Runtime.CompilerServices.Unsafe` | 6.0.0 | MIT | 传递依赖 |
| `xunit` | 2.9.3 | Apache-2.0 | 仅测试 |
| `xunit.runner.visualstudio` | 2.8.2 | Apache-2.0 | 仅测试 |
| `Microsoft.NET.Test.Sdk` | 18.10.1 | MIT | 仅测试 |
| `JsonSchema.Net` | 7.3.4 | MIT | 仅测试（集成测试直接依赖） |
| `Json.More.Net` | 2.1.1 | MIT | 仅测试（`JsonSchema.Net` 的传递依赖） |
| `JsonPointer.Net` | 5.3.1 | MIT | 仅测试（`JsonSchema.Net` 的传递依赖） |
| `Humanizer.Core` | 2.14.1 | MIT | 仅测试（`JsonPointer.Net` 的传递依赖） |
| `Newtonsoft.Json` | 13.0.1 | MIT | 仅测试（`Microsoft.TestPlatform.ObjectModel` 的传递依赖） |

许可证取自各包 `.nuspec` 中的 `<license type="expression">`（MIT 与 Apache-2.0），
或 `<license type="file">LICENSE.md`（Machina 系列，内容为 GPL v3 全文）。
.NET 运行时与 SDK 本身为 MIT。

MIT 与 Apache-2.0 均与 GPL-3.0 兼容，因此不构成障碍。
Apache-2.0 与 **GPLv2** 不兼容，但与 GPLv3 兼容。

### 5.1 `JsonSchema.Net` 及其依赖链

`tests/Collector.IntegrationTests/MentorRecorder.Collector.IntegrationTests.csproj` 是
**唯一**引用该包的工程。`src/Collector/MentorRecorder.Collector.csproj` 只有
`Machina.FFXIV`、`Microsoft.Data.Sqlite` 与 `SharpPcap` 三个 `PackageReference`，
因此这条依赖链**不进入任何发布产物**。

| 包 | 版本 | 作者 | 项目主页 | `.nuspec` 声明 |
|---|---|---|---|---|
| `JsonSchema.Net` | 7.3.4 | Greg Dennis | <https://github.com/json-everything/json-everything> | `<license type="expression">MIT</license>` |
| `Json.More.Net` | 2.1.1 | Greg Dennis | 同上 | 同上 |
| `JsonPointer.Net` | 5.3.1 | Greg Dennis | 同上 | 同上 |
| `Humanizer.Core` | 2.14.1 | Mehdi Khalili, Claire Novotny | <https://github.com/Humanizr/Humanizer> | 同上 |

前三个包内均附有 `LICENSE` 文件，内容为 MIT 全文，版权行为
`Copyright (c) .NET Foundation and Contributors`。

**用途**：对采集服务发出的每一条应答与事件，以及
`tests/Fixtures/ipc-requests/*.json` 中的每一条请求样本，
按 `contracts/ipc-v1.schema.json` 做完整的 draft 2020-12 校验，
对应测试为 `ContractSchemaTests` 与 `ContractRequestSampleTests`。

**产品代码不使用该库的原因**：协议档案的 schema 是一条硬安全边界，
解析器从字节缓冲中读出的一切内容都必须先通过它。这段校验代码有意保留在本仓库内，
也保留在本仓库的许可证之内，实现位于 `src/Collector/Protocol/Profiles/JsonSchemaValidator.cs`。
该实现是 draft 2020-12 的一个受限子集，并有测试断言 `profile.schema.json` 只使用其支持的关键字。
IPC 契约不存在这一层顾虑，因此测试侧直接使用成熟的库，两者互为交叉验证。

## 6. 本项目许可证结论

**`GPL-3.0-or-later`。**

推导过程如下：

1. `Machina.FFXIV` 与 `Machina` 采用 **GPL-3.0**，本项目与之链接并一同分发，
   因此整体作品必须以 GPL-3.0 兼容条款发布。
2. **Qt Graphs** 在开源许可下为 **GPL-3.0-only**，同样将整体作品约束到 GPLv3。
3. 其余依赖采用 MIT、Apache-2.0 或 public domain，均与 GPLv3 兼容。
4. 结论为 GPL-3.0-or-later。GPL-3.0 全文位于仓库根目录 `LICENSE`。

由此得出：

- **不可能**以闭源方式分发本软件的二进制。
- **不可能**在保留这些依赖的前提下做商业闭源产品。
- 任何分发都必须随附**完整对应源码**（GPLv3 第 6 条）与 `LICENSE`。
- 任何贡献都以 GPL-3.0-or-later 提交。

> 本文件是工程记录，不构成法律意见。正式对外发布前应自行寻求专业意见。

## 7. `PUBLIC_DISTRIBUTION_READY = true`（2026-09-07 落实）

对外发布二进制之前，以下每一项都必须落实：

1. **`DEC-OODLE-01` 用户告知**：方案已定稿，仍须在首次运行页面明确告知用户，
   本软件会读取并加载游戏可执行文件的副本。
2. **完整对应源码的分发渠道**：公开仓库或随包提供源码归档，满足 GPLv3 第 6 条。
3. **`THIRD_PARTY_NOTICES.md` 校对完毕**，并随二进制一同分发。
4. **确认发布包中不含**：Npcap、`oo2net_9_win64.dll`、`deucalion-*.dll`、
   任何游戏文件或游戏数据表。打包脚本需对此做硬断言。
5. **`data/duties/`、`data/jobs/` 的来源合规**：其中的 id ↔ 名称映射必须是
   自行整理、来源可说明的公开信息，并在文件中注明来源；不得复制游戏数据文件，
   也不得从 AGPL 项目移植。
6. **确认未从 AGPL 项目复制任何代码**，并在 `THIRD_PARTY_NOTICES.md` 中
   记录所有参考过的项目及参考方式。
7. **`LIVE_CAPTURE_STATUS`** 的当前值必须如实标注在 README 与发布说明中
   （现为 `VERIFIED_POP_TO_EXIT`：弹窗与换区已验证，通关报文未识别）。
8. **游戏服务条款与当地法律的自查**，并在 README 中给出清晰的风险提示与免责声明。
9. **Qt LGPL/GPL 合规检查**：**已落实**。模块清单见 `THIRD_PARTY_NOTICES.md`；
   `docs/licenses/LGPL-3.0.txt` 与 `docs/licenses/GCC-RUNTIME-LIBRARY-EXCEPTION-3.1.txt` 随包分发。
10. **游戏美术素材**：**已落实**（维护者决定，2026-09-07）。图标随包分发，依据
    FINAL FANTASY XIV Materials Usage License 作非商业用途使用；版权声明显示在
    “设置 → 版权与来源”，并写入 `THIRD_PARTY_NOTICES.md`。`-DMR_BUNDLE_GAME_ICONS=OFF`
    可构建无图标版本，玩家也可自备图标到 `%LOCALAPPDATA%\MentorRecorder\icons\`。见
    [`../src/Desktop/resources/icons/LICENSE-NOTE.md`](../src/Desktop/resources/icons/LICENSE-NOTE.md)。
11. **`ffxiv-datamining-cn` 的许可空白**：**已落实**（维护者决定，2026-09-07）。
    `data/duties/cn.<version>.json` 随包分发；文件 `source` 字段、`THIRD_PARTY_NOTICES.md`
    与“设置 → 版权与来源”注明该社区导出为来源、名称为 SQUARE ENIX 游戏文本，仅限非商业展示。

上述 11 项已全部落实。其中第 10、11 项以随包分发并附来源与版权声明的方式落实，
相应风险由维护者承担。

## 8. 本文档引用的来源

调查过程中实际访问的地址如下：

- `https://api.nuget.org/v3/registration5-gz-semver2/machina.ffxiv/index.json` —— Machina.FFXIV 版本与依赖
- `https://github.com/ravahn/machina` —— 项目主页与 GPL3 声明
- `https://raw.githubusercontent.com/ravahn/machina/master/Machina.FFXIV/Machina.FFXIV.csproj` —— 目标框架、版本、许可证文件
- `https://raw.githubusercontent.com/ravahn/machina/master/Machina.FFXIV/FFXIVNetworkMonitor.cs` —— 配置属性与默认值
- `https://raw.githubusercontent.com/ravahn/machina/master/Machina.FFXIV/Oodle/OodleFactory.cs` —— `OodleImplementation` 枚举
- `https://raw.githubusercontent.com/ravahn/machina/master/Machina.FFXIV/Oodle/OodleNative_Ffxiv.cs` —— 加载游戏 exe 副本的实现
- `https://raw.githubusercontent.com/ravahn/machina/master/Machina.FFXIV/Deucalion/DeucalionClient.cs` —— Deucalion 客户端
- `https://doc.qt.io/qt-6/licensing.html` —— Qt 6 GPLv3-only 模块清单
- `https://doc.qt.io/qt-6/qtgraphs-index.html`、`https://doc.qt.io/qt-6/qtcharts-index.html` —— Qt Graphs / Qt Charts 的许可与弃用说明
- `https://npcap.com/#download` —— Npcap 许可与再分发限制

在本机核对的材料：还原后的 `.nuspec` 与 `LICENSE.md`（位于 `%USERPROFILE%\.nuget\packages\`）、
Qt 安装目录中的 `LICENSE` 与 `COPYING.txt`、MinGW 的 `licenses/gcc/COPYING3`。

## 9. 游戏美术素材：分发条件

`src/Desktop/resources/icons/` 下的所有 PNG，包括 `jobs/`、`jobs_framed/`、
`content_types/`、`roles/` 四个目录，均为**从 FINAL FANTASY XIV 客户端提取的美术素材**。

| 项 | 值 |
|---|---|
| 版权 | © SQUARE ENIX CO., LTD. All rights reserved. |
| 适用条款 | **FINAL FANTASY XIV Materials Usage License** |
| 条款 URL | <https://support.na.square-enix.com/rule.php?id=5382&tag=authc> |
| 查阅的生效日期 | 2026-05-07（Effective May 7, 2026） |
| 获取途径 | XIVAPI v2 的 `api/asset`；`roles/allrounder.png` 另叠加了 Gamer Escape 的成品图 |
| 是否进入构建产物 | **是**。由 `src/Desktop/CMakeLists.txt` 以 `qt_add_resources` 编入 `MentorRecorderDesktopLib` |

关键约束：

- **不得用于任何销售或商业用途**，不得收取授权费或广告收入。
  仅对 YouTube 与 Twitch 合作伙伴计划及主播直播存在有限例外。
- 不得修改、移除或遮蔽任何商标或版权声明。版权与商标声明必须随素材展示，
  本项目将其置于「关于」页。
- 该条款针对截图、影片、直播一类的「Materials」编写，
  **没有**专门覆盖将素材内嵌进可分发软件的场景。
  因此，将游戏图标打包进发布产物属于**灰色地带**，SQUARE ENIX 并未明示许可。
- XIVAPI v2 与 Gamer Escape 均为社区服务或维基，
  **不拥有、也无法转授**这些美术素材的版权；SQUARE ENIX 的条款始终优先适用。

逐文件的 `icon_id`、SHA-256 与字节数，`allrounder.png` 的通道合成过程与决策记录，
以及分发前的逐项检查清单，见
[`../src/Desktop/resources/icons/LICENSE-NOTE.md`](../src/Desktop/resources/icons/LICENSE-NOTE.md)。

> 对应 §7 第 10 项：2026-09-07 起按“随包分发 + 来源与版权声明 + 仅限非商业用途”落实。

## 10. 副本名称数据的上游

`data/duties/*.json` 由 `tools/duty-data-generator/generate.py` 生成，
只保存 `content_id → 名称 / 分类 / 领地 / 等级` 这一层**展示映射**，
**不复制、也不再分发**游戏的任何数据文件。逐页 URL、抓取时间、行数与原始下载的 SHA-256
记录在每个文件的 `provenance` 字段中；**原始下载不进入仓库**。

| 上游 | 用途 | 许可证状况 |
|---|---|---|
| **XIVAPI v2**（<https://v2.xivapi.com>） | 英文名、`TerritoryType`、`ContentType`、等级需求；分页请求，上限 10 次 | 公开的只读 API；社区服务，本身不拥有游戏数据的版权 |
| **thewakingsands/ffxiv-datamining-cn**（<https://github.com/thewakingsands/ffxiv-datamining-cn>） | 简体中文名，只请求 `ContentFinderCondition.csv` 一次 | ⚠️ **没有 LICENSE 文件** |

> **未决问题（2026-09-04 核对）**：
> `GET /repos/thewakingsands/ffxiv-datamining-cn` 返回 `license: null`，
> `https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master/LICENSE`
> 返回 404，即该仓库**没有给出任何明示的再分发许可**。
> 该仓库是国服客户端的 SaintCoinach 导出，其原始内容本身是 © SQUARE ENIX 的游戏文本。
>
> 在该问题解决之前，`data/duties/cn.<version>.json` 按随时可能移出仓库、
> 改为安装时在本地生成的数据对待。
> 相同说明同时写在 [`../data/duties/README.md`](../data/duties/README.md) 与
> [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)。

> 对应 §7 第 11 项：2026-09-07 起按“随包分发 + 来源声明”落实，数据可随时改为安装时本地生成。
