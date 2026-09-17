# 第三方声明 / Third-Party Notices

**FF14 导随记录器 / FF14 Mentor Roulette Recorder**
本项目以 **GPL-3.0-or-later** 发布。完整许可证文本见 [`LICENSE`](LICENSE)。

本文件列出本软件使用的第三方组件及其许可证。
详细的调查记录、版本证据与结论推导见
[`docs/third-party-licenses.md`](docs/third-party-licenses.md)。

调查日期：2026-09-04。

---

## 决定本项目许可证的两个组件

### Machina / Machina.FFXIV

- 版本：`Machina.FFXIV` 2.4.7.7、`Machina` 2.3.1.3
- 作者：Ravahn
- 许可证：**GNU General Public License v3.0**
- 来源：https://github.com/ravahn/machina

用于重组已确认属于 FF14 的 TCP 流并解码 FFXIV 消息。实时原始报文由
SharpPcap 的只读 Npcap 入口交付。本软件不使用 Raw Socket，也不使用注入式钩子。
包内附带的原生载荷 `deucalion-*.dll` 在构建阶段即从输出中剔除。

### Qt 6.11.2

- 许可证：多数模块 **GNU LGPL v3.0**；本软件使用的 **Qt Graphs** 为 **GNU GPL v3.0 only**
- 来源：https://www.qt.io/ ，许可说明 https://doc.qt.io/qt-6/licensing.html

实际使用的模块：Qt Core、Qt Gui、Qt Qml、Qt Quick、Qt Quick Controls、
**Qt Graphs**（GPLv3-only）、Qt TextToSpeech、Qt Multimedia、Qt Svg。

Qt Multimedia（LGPL-3.0）仅用于播放在线语音取回的 WAV 读音（`QSoundEffect`）。
发行包仅包含其 Windows 多媒体后端（`multimedia\windowsmediaplugin.dll`，使用 Windows
自带的 Media Foundation），**不包含** FFmpeg 后端与 FFmpeg 库（`avcodec` / `avformat` /
`avutil` / `swresample` / `swscale`）。发行包中一旦出现上述文件，`scripts/package.ps1` 即判定失败。

由于 Machina.FFXIV（GPLv3）与 Qt Graphs（GPLv3-only），
本项目**必须**以 GPL-3.0-or-later 发布，**无法**闭源分发。

LGPL-3.0 全文随包提供于 `docs/licenses/LGPL-3.0.txt`。发行包中的 `libgcc_s_seh-1.dll`、
`libstdc++-6.dll`、`libwinpthread-1.dll` 为 MinGW-w64 / GCC 运行时库，按 GPL-3.0 附
GCC Runtime Library Exception 3.1 分发（`docs/licenses/GCC-RUNTIME-LIBRARY-EXCEPTION-3.1.txt`）。

---

## 其他组件

| 组件 | 版本 | 许可证 |
|---|---|---|
| Microsoft.Data.Sqlite / .Core | 8.0.11 | MIT |
| SQLitePCLRaw.core / .bundle_e_sqlite3 / .lib_e_sqlite3 / .provider.e_sqlite3 | 2.1.6 | Apache-2.0 |
| SQLite（`e_sqlite3`，经由 SQLitePCLRaw 附带） | — | Public Domain |
| SharpPcap | 6.3.1 | MIT |
| PacketDotNet（SharpPcap 的传递依赖） | 1.4.8 | MPL-2.0 |
| System.Memory | 4.6.3 | MIT |
| System.Text.Encoding.CodePages | 9.0.5 | MIT |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 | MIT |
| .NET 8 运行时与 SDK | 8.0.30 / 9.0.316 | MIT |
| MinGW-w64 GCC 运行时（`libstdc++`、`libgcc`） | GCC 13.1 | GPL-3.0 **with GCC Runtime Library Exception** |
| xUnit.net（仅测试，不随发布分发） | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio（仅测试） | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk（仅测试） | 18.10.1 | MIT |
| JsonSchema.Net（仅测试） | 7.3.4 | MIT |
| Json.More.Net（仅测试，JsonSchema.Net 的传递依赖） | 2.1.1 | MIT |
| JsonPointer.Net（仅测试，JsonSchema.Net 的传递依赖） | 5.3.1 | MIT |
| Humanizer.Core（仅测试，JsonPointer.Net 的传递依赖） | 2.14.1 | MIT |
| Newtonsoft.Json（仅测试，Microsoft.TestPlatform.ObjectModel 的传递依赖） | 13.0.1 | MIT |

SharpPcap 与 PacketDotNet 使用未经修改的 NuGet 二进制。许可证全文分别见
`docs/licenses/SharpPcap-MIT.txt`、`docs/licenses/PacketDotNet-MPL-2.0.txt`。
对应源码：[SharpPcap 6.3.1](https://github.com/dotpcap/sharppcap/tree/bfedf297e7410ffcf44e05d14f7fbef304f20895)、
[PacketDotNet 1.4.8](https://github.com/dotpcap/packetnet/tree/690707ce56d6e9c266daf6236c4f76ac5035334c)。
新增 .NET 组件的 MIT 文本与 CodePages 附带声明见 `docs/licenses/dotnet-MIT.txt`、
`docs/licenses/System.Text.Encoding.CodePages-NOTICES.txt`。

> **`JsonSchema.Net` 仅出现在 `tests/Collector.IntegrationTests/`**，
> 用于按 `contracts/ipc-v1.schema.json` 校验 Collector 的每一条应答、事件与请求样本。
> 产品代码中的 JSON Schema 校验由本仓库自有的
> `src/Collector/Protocol/Profiles/JsonSchemaValidator.cs` 承担。协议档案 schema 是硬安全边界，
> 该实现有意保留在本仓库及本仓库的许可证之内。因此这四个包**不进入发布产物**。

### 内置字体（SIL Open Font License 1.1）

两款字体原样编入 `MentorRecorder.Desktop.exe` 的 Qt 资源，各自的许可证全文一并编入
（`:/resources/fonts/OFL.txt`、`:/resources/fonts/IBMPlexMono-OFL.txt`），源文件位于
`src/Desktop/resources/fonts/`。逐项说明见 [`docs/third-party-licenses.md`](docs/third-party-licenses.md) §3.2–3.3。

| 字体 | 字重 | 版权 | 许可证全文 | 来源 |
|---|---|---|---|---|
| Cinzel | Regular、Bold | Copyright 2020 The Cinzel Project Authors | `src/Desktop/resources/fonts/OFL.txt` | https://github.com/NDISCOVER/Cinzel |
| IBM Plex Mono 2.005 | Medium、SemiBold | Copyright © 2017 IBM Corp. with Reserved Font Name "Plex" | `src/Desktop/resources/fonts/IBMPlexMono-OFL.txt` | https://github.com/IBM/plex（`@ibm/plex-mono@2.5.0`） |

---

### 内置图标（ISC License）

按钮上的线条图标来自 **Lucide**（<https://lucide.dev>），版本 1.46.0，许可证 ISC，
版权 Copyright (c) 2026 Lucide Icons and Contributors。其中 `calendar`、`chevron-left`、
`chevron-right`、`clock`、`moon`、`plus`、`radio`、`server`、`x` 九个由 Lucide 从 **Feather** 项目派生，同时受
**The MIT License (MIT)**，Copyright (c) 2013-present Cole Bemis 约束；该名单列于 Lucide 的
许可证文件中。SVG 原件与许可证全文（ISC 与 MIT 两段）位于
`src/Desktop/resources/icons/lucide/`。进入构建产物的是由其生成的
`src/Desktop/qml/components/Lucide.js`，该文件头部载有 ISC 与 MIT 两份版权与许可声明，随
`MentorRecorder.Desktop.exe` 的 QML 模块一并编入。逐项说明见
[`docs/third-party-licenses.md`](docs/third-party-licenses.md) §3.4。

## 游戏美术素材与游戏文本：随发行版分发，附来源与版权声明

职业、职能、副本类型的 PNG 图标（`src/Desktop/resources/icons/`）为**从 FINAL FANTASY XIV
客户端提取的美术素材**；`data/duties/*.json` 与 `data/jobs/jobs.json` 中的副本、职业名称为游戏文本。
两者版权均归 SQUARE ENIX CO., LTD.，**不适用**本项目的 GPL 许可证，而是依据
**FINAL FANTASY XIV Materials Usage License** 作**非商业**用途使用，并按其要求保留版权声明。
该声明显示在软件的“设置 → 版权与来源”，同时写入本文件、数据文件的 `source` 字段与
`src/Desktop/resources/icons/LICENSE-NOTE.md`。本项目不得用于任何销售或商业用途。
使用 `-DMR_BUNDLE_GAME_ICONS=OFF` 可构建不含图标的版本，界面改用文字徽章。

- 适用条款：**FINAL FANTASY XIV Materials Usage License**
  （<https://support.na.square-enix.com/rule.php?id=5382&tag=authc>，2026-05-07 生效版本），
  **禁止任何销售或商业用途**。该条款面向截图、影片与直播制定，
  **未**专门覆盖将图标打包进可分发软件的情形，属于灰色地带。
- 获取途径 XIVAPI v2 与 Gamer Escape 均为社区服务或维基，
  **不拥有亦无法转授**这些素材的版权。
- 结论：随本项目分发，附来源与版权声明，仅限非商业用途。该决定由维护者于 2026-09-07 做出，
  相应风险由维护者承担。

逐目录说明见
[`src/Desktop/resources/icons/LICENSE-NOTE.md`](src/Desktop/resources/icons/LICENSE-NOTE.md)。

---

## 未分发但为运行所必需的组件

### Npcap

- 许可证：**Npcap 专有许可**（不是 BSD）
- 来源：https://npcap.com/

免费版允许个人或内部使用，**最多 5 台机器，且不得对外再分发**。
商业再分发须向 Nmap Software LLC 购买 OEM 许可。

**因此本软件不内置、不分发 Npcap。**
本软件仅**检测** Npcap 是否已安装，未安装时显示官方站点的安装指引。
安装器（`installer/MentorRecorder.iss`）在检测不到 Npcap 时，从官方站点
`https://npcap.com/dist/` 下载**未经修改的官方安装程序**（固定版本，校验 SHA-256）并启动。
用户在 Npcap 自身的向导中完成安装并接受其许可条款。安装器不包含、不缓存、不改写 Npcap。

### Oodle / 游戏文件

- 本软件**不分发** `oo2net_9_win64.dll`（RAD Game Tools 专有组件）。
- 本软件**不分发**任何 FINAL FANTASY XIV 的文件、资源或数据表。
- 关于运行时是否会读取/加载游戏可执行文件（Oodle 解压所需），
  见 [`docs/privacy-boundary.md`](docs/privacy-boundary.md) §4，
  决策项 `DEC-OODLE-01`（已定稿）。

FINAL FANTASY XIV 是 SQUARE ENIX CO., LTD. 的商标。
本项目与 SQUARE ENIX 无任何关联，未获其授权、认可或赞助。

---

## 代码来源声明

- 本项目**未从任何 AGPL-3.0 项目复制代码或资源**。
- 本项目未逐行搬运任何第三方实现。对上游行为的了解来自其公开源码与文档，
  引用地址已记录在 [`docs/third-party-licenses.md`](docs/third-party-licenses.md) §8。
- `data/duties/`、`data/jobs/` 中的 id ↔ 名称映射为本项目自行整理的公开信息，
  每个文件需注明来源；**不复制游戏数据文件**。

### `data/duties/*.json` 的两个上游

由 `tools/duty-data-generator/generate.py` 生成。逐页 URL、抓取时间、行数与原始下载的
SHA-256 均记录在每个文件的 `provenance` 中。**原始下载不纳入仓库。**

| 上游 | 用途 | 许可证状况 |
|---|---|---|
| **XIVAPI v2**（<https://v2.xivapi.com>） | 英文名、`TerritoryType`、`ContentType`、等级需求 | 公开的只读 API；社区服务，本身不拥有游戏数据的版权 |
| **thewakingsands/ffxiv-datamining-cn**（<https://github.com/thewakingsands/ffxiv-datamining-cn>） | 简体中文名（`ContentFinderCondition.csv`） | ⚠️ **没有 LICENSE 文件** |

> **未决的许可问题**：2026-09-04 核对，
> `GET /repos/thewakingsands/ffxiv-datamining-cn` 返回 `license: null`，
> `raw.../master/LICENSE` 返回 404，即**没有任何明示的再分发许可**。
> 本项目仅保存由其派生的 `content_id → 中文名` 映射，
> 且映射的原始内容为 © SQUARE ENIX 的游戏文本。
>
> 在该问题解决之前，`data/duties/cn.<version>.json` 应视为可随时移出仓库、
> 改为安装时在本地生成的数据。详见
> [`data/duties/README.md`](data/duties/README.md) 与
> [`docs/third-party-licenses.md`](docs/third-party-licenses.md)。

---

## 分发状态

> **`PUBLIC_DISTRIBUTION_READY = true`**（2026-09-07）
>
> [`docs/third-party-licenses.md`](docs/third-party-licenses.md) §7 的 11 项前置条件已全部落实。
> 其中第 10 项（游戏图标）与第 11 项（`ffxiv-datamining-cn` 无许可证文件的副本名称数据）
> 由维护者决定以“随包分发 + 来源与版权声明 + 仅限非商业用途”的方式落实。
>
> **`LIVE_CAPTURE_STATUS = VERIFIED_POP_TO_EXIT`**：国服 `2026.08.05` 的弹窗与换区已在真实流量上验证，
> 通关报文尚未识别（[`docs/live-validation-guide.md`](docs/live-validation-guide.md) §0）。
