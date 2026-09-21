# 抓包诊断 / Capture Diagnostics

> **状态词只有一个规范来源，本文档不复述取值。**
> `LIVE_CAPTURE_STATUS` 即 `src/Collector/Capture/CaptureDiagnostics.cs` 中的
> `CaptureDiagnosticsSnapshot.LiveCaptureStatus`。查询本机构建的状态词，
> 应执行 `MentorRecorder.Collector.exe --capture-doctor --json` 并读取其
> `live_capture_status` 字段，或查看诊断页顶部的同一取值。`scripts/verify.ps1` 与
> `scripts/package.ps1` 断言的同样是该字段，而非任何文档中的字面量。
> 仓库级 `PROTOCOL_PROFILE_STATUS` 记录在
> [`../protocol-profiles/README.md`](../protocol-profiles/README.md) 的顶部标记中，
> 由 `package.ps1` 读入 `BUILD-METADATA.json`。
>
> 状态词描述的是该链路在真实流量上已被验证到的程度。它**不**改变 fail-closed
> 规则：仅与本机区服、版本精确匹配的 `VERIFIED` 档案会被用于自动记录，
> 其余一律不解析、不写入记录。真机验证流程参见
> [live-validation-guide.md](live-validation-guide.md)。

本文件说明采集服务如何自检抓包链路、对外暴露哪些指标，以及常见故障的排查方法，
供需要定位抓包问题的用户与维护者查阅。相关的 IPC 消息为 `GetCaptureStatus`、
`ListCaptureAdapters`、`GetProtocolProfileStatus`、`StartCapture`、`StopCapture`，
以及 `SubscribeLiveEvents` 中的 `CaptureStatusChanged` / `DiagnosticsMessage`。

## 0. 缺少 Npcap 与游戏进程时的行为

在既未安装 Npcap、也未运行游戏的机器上，下表所列的返回值是预期的 fail-closed 表现，
而非功能尚未接通。谎报成功会使用户误以为记录正在自动进行，这是本软件最严重的错误形态。

| 消息 | 该环境下的返回 |
|---|---|
| `GetCaptureStatus` | `state = STOPPED`、`npcap_installed = false`、`ffxiv_running = false`、`profile_status = NONE`、`last_error_code = "UNAVAILABLE"`、`monitor_type = "WinPCap"`、`injected_hook_enabled = false`、`queue_capacity = 4096` |
| `ListCaptureAdapters` | `npcap_installed = false`、`install_hint` 为 §2 的安装指引、`adapters` 为**本机真实网卡列表**（IP 已按 /24 打码），且**没有任何一张被推荐** |
| `GetProtocolProfileStatus` | `status = NONE`，`message` 说明 fail-closed 规则与手工补录途径 |
| `StartCapture` | `ERR_NPCAP_MISSING`（`details.capture = "UNAVAILABLE"`、`details.npcap = "NOT_INSTALLED"`） |
| `StopCapture` | `ERR_CAPTURE_NOT_RUNNING` |
| `GetStatus` | 除 `capture` 外，另有 `game`、`npcap`、`oodle_mode`、`reads_game_executable`，以及可选的 `update`（更新检查的结果，见 [privacy-boundary.md](privacy-boundary.md) §8.4；该环境下取不到版本号，`update_available = false`） |

> **与 Phase 1 的差别**：即使 Npcap 未安装，`ListCaptureAdapters` 也会列出网卡。
> 枚举网卡不依赖驱动，将本机网卡与安装指引一并呈现比返回空列表更有价值。
> 但此时**不推荐任何一张网卡**。缺少游戏连接即无从判断，任意推荐只会造成抓包看似运行、
> 实则未捕获任何流量的假象。

`monitor_type` 与 `injected_hook_enabled` 是硬边界常量，可在运行时随时核对。

## 1. 启动前的检查（按顺序，任一失败即停止）

| # | 检查 | 失败时 |
|---|---|---|
| 1 | Npcap 是否可用（安装 + WinPcap 兼容模式 + 权限） | `ERR_NPCAP_MISSING`（`details.npcap` 给出具体原因，见 §2） |
| 2 | FFXIV 进程是否在运行 | `ERR_FFXIV_NOT_RUNNING`（`retryable = true`） |
| 3 | 能否枚举到至少一个网卡 | `ERR_NPCAP_MISSING`（`details.adapters = 0`） |
| 4 | 能否确定要用哪张网卡 | `ERR_BAD_REQUEST`，`field = "adapter_id"`（**不推测**，要求手动选择） |
| 5 | 协议档案是否 `VERIFIED` | `ERR_PROFILE_UNSUPPORTED` |

> 相对 Phase 1 文档，检查顺序有一处调整：**进程检测排在网卡枚举之前**。
> 判断哪张网卡承载游戏流量需要按系统 TCP 表过滤，
> 而过滤的前提是已知游戏进程的 PID，否则第 3、4 步无法进行。

### 1.0 第 5 步的例外之一：本机校准

自 0.3.0 起，当没有档案匹配当前版本、但随包目录中存在同区服且带 `calibration` 段的
`VERIFIED` 模板时，第 5 步不再拒绝启动，而是让抓包以计数模式启动并进入本机校准。
目录歧义（`AMBIGUOUS`）、档案加载失败、区服未知，以及
`capture.auto_calibration_enabled = false` 时，仍然返回 `ERR_PROFILE_UNSUPPORTED`。

校准期间 `CaptureStatus.profile_status` 保持 `UNSUPPORTED_BUILD`，`calibration.state`
取 `OBSERVING` / `READY` / `BLOCKED`，`warnings` 中给出正在重新校准的提示。
用户确认后，采集服务写出本机档案、重新读取目录，并在当前会话内绑定解析器；
`calibration_bound_at_utc` 记录该时刻，解析计数从零开始。
存在模板但抓包尚未运行时，`calibration.state` 为 `WAITING`。

补丁后的 exe 没有精确匹配的 Oodle 签名档案时，`oodle_signature_source = pattern-fallback`
表示同区服最新 `VERIFIED` 档案的模式在新 exe 上重扫成功
（protocol-profiles/oodle-signatures/README.md）。该字段仍为 `builtin` 则表示模式匹配同样失败，
国服客户端此时通常无法解出任何报文，`messages_decoded` 将持续为 0。

### 1.1 第 5 步的例外之二：`capture.allow_without_profile`

第 5 步是 fail-closed 规则的核心，默认行为与
[../contracts/error-codes.md](../contracts/error-codes.md) 一致：**没有可用档案即不启动抓包**。

该规则带来一处循环依赖。编写第一份档案必须先在真机上启动抓包链路并观察流量，
而在档案存在之前抓包不允许启动。

为解除该依赖，本软件提供默认关闭的设置项 `capture.allow_without_profile`：

- **默认 `false`**：行为与契约完全一致，返回 `ERR_PROFILE_UNSUPPORTED`。
- 置为 `true` 后进入**仅诊断模式**：抓包启动，报文仅被计数，
  **不解析任何字段、不写入任何记录**。此时 live pipeline 退回 `CountingSink`，
  该实现只按 opcode 计数，不读取任何字段。`GetStatus.warnings` 会持续提示
  "抓包正在运行，但协议档案未验证……不会自动写入任何记录（fail-closed）"。

该开关**只放宽观察权限，不放宽记录权限**。
其唯一用途是 [live-validation-guide.md](live-validation-guide.md) 的第 4 层验证。

## 2. Npcap 检测

**只做检测，绝不下载、绝不内置、绝不静默安装。**

检测输入：

1. 文件 `%SystemRoot%\System32\Npcap\wpcap.dll` 与 `%SystemRoot%\System32\Npcap\Packet.dll`
2. 注册表 `HKLM\SOFTWARE\Npcap` 与 `HKLM\SOFTWARE\WOW6432Node\Npcap`
3. 注册表值 `WinPcapCompatible`（缺失时回退到检查 `%SystemRoot%\System32\wpcap.dll` 是否存在）
4. 注册表值 `AdminOnly`
5. 当前进程是否具有管理员权限
6. 版本号：`wpcap.dll` 的 `FileVersionInfo`，回退到注册表 `Version`

检测结果（`GetStatus.npcap.status`）：

| status | 含义 | 处理 |
|---|---|---|
| `READY` | 已安装、兼容模式已装、权限足够 | 可以抓包 |
| `NOT_INSTALLED` | 注册表与两个 DLL 都不存在 | 显示安装指引 |
| `LOAD_FAILED` | 有安装记录但 DLL 不在 | 安装不完整或被部分卸载，建议重装 |
| `NOT_WINPCAP_COMPATIBLE` | 已安装但没有 WinPcap 兼容模式 | 重新安装并勾选兼容模式 |
| `NPCAP_ADMIN_ONLY` | `AdminOnly = 1` 且当前进程未提权 | 以管理员身份运行，或重装时取消该限制 |

未安装时，UI 显示的指引内容为：

> 未检测到 Npcap。本软件需要 Npcap 才能被动读取本机网卡流量。
> 请从 Npcap 官方站点自行下载安装（安装时请勾选 "WinPcap API-compatible Mode"），
> 安装完成后重新启动本软件。
> 本软件不会替您下载或安装任何驱动。

**安装选项要求**：必须勾选 WinPcap 兼容模式。"Support raw 802.11 traffic" 不需要勾选，
本软件只处理以太网与回环上的普通 IP 流量。

## 3. 游戏进程检测

采集服务按进程名查找 `ffxiv_dx11` 与 `ffxiv`（`Process.GetProcessesByName`），并取得以下信息：

- **PID** 与**启动时间**。
- **安装路径**：只从内核进程表读取
  （`NtQuerySystemInformation(SystemProcessIdInformation)`，不打开游戏进程句柄），
  因此由启动器以管理员身份拉起的客户端同样可以读到。本软件不设第二条途径：
  凡是需要打开游戏进程句柄、读取其模块表才能得到路径的做法，都与本节末尾的边界承诺
  相抵触，已由静态边界检查的规则挡在代码之外。内核不返回结果时，安装路径即留空，
  不提权、不重试、不推测。
- **区服判定**：仅依据安装路径中的关键字。`SdoA` / `Shanda` 等判为 `CN`，
  `Square Enix` / `FINAL FANTASY XIV - A Realm Reborn` 判为 `GLOBAL`，
  无法识别时为 `UNKNOWN`。
- **客户端版本**：读取 exe 同目录下启动器的 **`ffxivgame.ver` 文本文件**。
  只接受长度不超过 64、且仅含 `0-9 A-Z a-z . _ -` 的短串，否则视为读取失败。
  游戏未运行时改从记住的安装目录读取同一个文件，见本节末。

**边界**：上述信息全部来自操作系统的进程列表与磁盘上的一个文本文件。本软件
**从不**打开游戏进程句柄，**从不**读取游戏进程内存，**从不**附加到游戏进程。
关于是否读取游戏可执行文件，参见 [privacy-boundary.md](privacy-boundary.md) §4。

**多开**：多开不会导致异常。采集服务选择**最早启动**的实例，并在 `warnings` 中说明实例数量
与所选 PID。用户可在诊断页手动指定 `process_id`。

**路径读取失败**：区服与版本保持 `UNKNOWN` / `null`，档案因此无法匹配，
随即 fail-closed，不产生任何自动记录。这是预期行为。

**游戏未运行时的客户端版本**：采集服务把上次看到的游戏程序路径记在数据库同目录的
`game-install.json`（[privacy-boundary.md](privacy-boundary.md) §9）。没有找到任何游戏进程时，
它按同样的规则读取该目录下的 `ffxivgame.ver` 与路径关键字，因此软件启动后即可得知客户端版本与区服，
档案匹配与共享校准获取（§8.2）不必等到玩家登录游戏。这种情况下 `ffxiv_running` 仍为 `false`，
`ffxiv_process_id` 与 `install_path_readable` 仍为空：已知的是**安装**，不是一个可供监听的客户端，
启动抓包依旧以 `ERR_FFXIV_NOT_RUNNING` 拒绝。记住的路径只在采集服务内部使用，不进入 IPC 应答、
日志、诊断报告或数据库。安装被移走、卸载或所在磁盘未接入时，版本重新变为未知并继续 fail-closed；
该文件不会因此被删除。客户端更新后，`ffxivgame.ver` 在启动器打补丁之前仍是旧版本号，游戏启动后按
真实版本重新匹配，与今天版本变化时的行为一致。

## 4. 适配器选择

`ListCaptureAdapters` 对每个网卡返回 `adapter_id`（网卡标识，视为不透明字符串）、
`description`、`friendly_name`、`ipv4_addresses`、`is_loopback`、`is_up`、`recommended`。

推荐规则（`recommended = true`），按优先级：

1. 用户此前手动选过的网卡（记在 `application_settings` 的 `capture.adapter_id`）；
2. 其 IPv4 地址出现在**游戏进程当前 TCP 连接**的本地地址中，且 `is_up`、非回环。

两条规则均不满足时**不推荐任何网卡**，要求用户手动选择。这同样是 fail-closed 行为，
不作任何推测。用户显式指定的 `adapter_id` 会被记住，下次自动沿用。

系统 TCP 表通过 Machina 的 `ProcessTCPInfo` 读取，该类封装的是 IP Helper 的
`GetExtendedTcpTable`。它与 `netstat -ano` 是同一份信息，与游戏进程本身无关。

Machina 的 WinPCap 监视器按**本地 IP** 选择设备，因此采集服务将所选网卡的地址作为
`LocalIP` 传入。

**隐私**：

- 对外显示的 IPv4 一律按 **/24 打码**（`192.168.1.34` → `192.168.1.x`）。
  未打码的地址仅在进程内部供 Machina 使用，**从不**出现在 IPC 应答、日志或诊断报告中。
- `capture_sessions` 只持久化 `adapter_id`，**不持久化 IP 或 MAC**。
- 脱敏诊断报告中不出现 `adapter_id` 本身，因为网卡 GUID 跨重启稳定，等同于机器标识。
  报告只给出一个 SHA-256 截断指纹，用于判断两份报告是否来自同一张网卡。

## 5. 运行时指标

### 5.1 `CaptureStatus`（IPC 契约字段）

| 字段 | 含义 | 健康值 |
|---|---|---|
| `state` | `STOPPED` / `STARTING` / `RUNNING` / `DEGRADED` / `FAILED` | `RUNNING` |
| `capture_session_id` | 本次抓包会话的 UUID | 运行时非空 |
| `npcap_installed` / `npcap_version` | Npcap 检测结果 | `true` + 版本号 |
| `ffxiv_running` / `ffxiv_process_id` | 游戏进程 | `true` + PID |
| `game_build` / `region` | 从 `ffxivgame.ver` 与安装路径得到；游戏未运行时取自记住的安装目录（§3），因此可以在 `ffxiv_running` 为 `false` 时非空 | 非空 |
| `profile_status` | 与本次检测到的区服、版本精确匹配的协议档案状态 | `VERIFIED`，其余取值一律 fail-closed |
| `adapter_id` | 本次使用的网卡 | 非空 |
| `preference_stale`（`$defs/CaptureAdapter`） | 记住的网卡不再承载游戏流量而另一张网卡承载时为 `true`，此时推荐后者 | `false` |
| `started_at_utc` | 本次抓包开始时间 | 非空 |
| `packets_observed` | 本会话**成功重组并分帧**后交给上层的报文数 | 持续增长 |
| `raw_packets_observed` | 网卡上真正读到的 IPv4/TCP 报文数（重组之前） | 持续增长 |
| `preexisting_connections` | 抓包开始时游戏已存在的 TCP 连接数，无法读取时为 `null` | `0` |
| `silent_reason` | 抓包运行但无任何记录产生时的原因（见 §5.5） | `NONE` |
| `ingress.*` | 重组阶段各类丢弃计数（见 §5.5） | 全部保持 0 |
| `packets_dropped` | 因有界队列已满，或 Npcap 报告丢包而丢弃的报文数 | 保持 0 |
| `queue_depth` / `queue_capacity` | 解析队列的当前深度与容量 | `depth / capacity < 0.5` |
| `monitor_type` | 恒为 `WinPCap` | `WinPCap` |
| `injected_hook_enabled` | 恒为 `false` | `false` |
| `last_error_code` | 最近一次错误码，抓包链路完全不可用时为 `UNAVAILABLE` | `null` |
| `connection_count` | 本会话观察到的不同游戏连接数 | ≥ 1 |
| `messages_decoded` | 分帧读取成功的报文数 | 持续增长 |
| `decode_errors` | 分帧读取失败的报文数 | 保持 0 或增长极慢 |
| `parse_ok_count` / `parse_fail_count` | 解析成功 / 声明消息被拒绝的报文数 | 成功率接近 1 |
| `ignored_count` | 档案未声明的 opcode（与记录无关的普通报文），只计数 | 持续增长 |
| `duplicate_count` | 被判定为重复的报文数 | 较小 |
| `message_rate_per_second` | 平滑后的报文速率 | 与游戏活跃度相关 |
| `uptime_ms` | 本次抓包已运行时长，未抓包时为进程运行时长 | 单调增长 |
| `last_valid_event_at_utc` | 解析器最近一次产出语义事件的时间 | 近期 |
| `last_valid_event_kind` | 同一条事件的语义类型（`CONTENT_FINDER_POP` / `ZONE_INITIALIZATION` / `ZONE_TERRITORY` / `DUTY_RESULT` / `PLAYER_JOB` / `ZONE_LEFT` / `INSTANCE_LEFT` / `MATCH_CANCELLED` / `MATCH_ANNOUNCED`），与时间戳同时设置，界面显示为"21:38:04 副本结算" | 与上一行同时出现 |
| `recent_parser_errors` | 最近的解析拒绝（≤ 20 条，见 §5.3） | 空数组 |

> **`packets_observed` 与 `raw_packets_observed` 的含义不同。**
> 前者只统计通过了流重组、握手校验与分帧的报文。中途接入（本软件在游戏登录之后才开始监听）
> 会使每一个报文都在重组阶段被丢弃，于是前者恒为 0，`decode_errors` 同样恒为 0，
> 界面上呈现出完全健康的假象。后者是网卡层的原始计数，因此可以区分"网卡上没有流量"
> 与"流量全部被丢弃"两种情况。2026-09-08 整晚无记录的故障即属后者
> （contracts/CHANGELOG.md 第 19 条）。

上述十个计数字段在契约中**全部是可选的**，属于 2026-09-04 的附加性变更
（contracts/CHANGELOG.md 第 14 条）。这些数值一直在采集，此前仅出现在导出的脱敏报告中。

> **客户端必须区分 `0` 与字段缺失。** 字段存在且为 `0` 表示已测量且结果为零；
> 字段缺失表示该端未测量，例如采集服务未连接，或对端为更早的版本。
> 后者在界面上一律渲染为 `—`，**绝不**替对端补一个并未收到的 `0`。

自 0.3.0 起，`CaptureStatus` 另带三个校准字段（contracts/CHANGELOG.md 2026-09-10 条）：

| 字段 | 含义 |
|---|---|
| `profile_origin` | `SHIPPED` / `LOCAL_CALIBRATION` / `SHARED_CALIBRATION` / null：生效档案分别来自随包目录、本机校准，或其他用户分享并已在本机核实的校准 |
| `calibration_bound_at_utc` | 本会话内热绑定本机档案的时刻，未发生过热绑定时为 null |
| `calibration` | `state`（`IDLE` / `OBSERVING` / `READY` / `BLOCKED` / `DONE`）、`game_build`、`template_profile_id`、`local_profile_id`、`bound_at_utc`、`blockers[]`（面向用户的完整语句）、`progress`（排本 / 弹窗 / 换区簇数 / 进本 / 出本）、`events[]`（待核对的时间线，仅含中文标签、轮盘编号、区域编号与副本名，不含 opcode）、`shared`（共享校准，见 §9.6） |

### 5.2 诊断快照（`CaptureDiagnosticsSnapshot`，诊断页与脱敏报告的数据源）

在 §5.1 所列字段之外，诊断快照还包含：

| 字段 | 含义 |
|---|---|
| `npcap.status` / `version` / `winpcap_compatible` / `admin_only` / `process_elevated` | §2 的完整检测结果 |
| `game.instance_count` / `install_path_readable` | 多开数量；安装路径是否可读 |
| `connection_count` | 本会话观察到的不同游戏连接数（按连接键去重，上限 64） |
| `messages_decoded` | 分帧读取成功的报文数 |
| `decode_errors` | 分帧读取失败的报文数（短包、截断、乱码；**只计数，绝不抛异常**） |
| `parse_ok` / `parse_fail` / `ignored` / `duplicates` | 来自 `IParserStats`。无可用档案时由 `CountingSink` 暴露为 0，存在 `VERIFIED` 档案时来自 live parser。`ignored` 表示档案未声明的 opcode，不计为失败（contracts/CHANGELOG.md 第 18 条） |
| `dropped` | 有界队列溢出丢弃数 |
| `queue_depth` / `queue_capacity` | 同上 |
| `profile.status` / `profile_id` | 来自针对当前游戏区服与版本的档案选择，无精确匹配时 fail-closed |
| `last_valid_event_at_utc` / `last_valid_event_kind` | 解析器最近一次产出语义事件的时间与该事件的语义类型（报告里在 `run` 下） |
| `run.state` | 当前进行中记录的状态 |
| `message_rate_per_second` | 报文速率的指数移动平均（EMA，平滑系数 0.3，最小采样间隔 250 ms） |
| `uptime_ms` | 本次抓包已运行时长，取自单调时钟，不以两个墙钟时间相减得出 |
| `oodle_mode` / `reads_game_executable` | 见 [privacy-boundary.md](privacy-boundary.md) §4.1 |

`DEGRADED` 的定义为：抓包仍在进行（`state = RUNNING`），但 `packets_dropped > 0`
（有界队列溢出**或** Npcap 报告的驱动、网卡丢包），或队列深度超过容量的 80%。

自 2026-09-04 起，§5.2 中除 `npcap.*` / `game.*` / `oodle_*` 之外的每一项都同时出现在
§5.1 的 `CaptureStatus` 中。因此诊断页显示的数值与脱敏报告中的数值来自**同一次快照**，
不会相互矛盾。

### 5.3 解析错误列表 `recent_parser_errors`

解析器（`ProfileMessageParser`）在内存中以环形缓冲保留最近 **100** 条拒绝记录。
`CaptureStatus` 只输出其中**最后 20 条**（`CaptureDiagnosticsSnapshot.MaxRecentParserErrors`），
按时间升序排列。每条记录包含以下字段：

| 字段 | 含义 |
|---|---|
| `at_utc` | 该次拒绝发生的时间。它是区分档案长期错误与游戏刚刚更新的唯一依据 |
| `code` | `E_LEN_MISMATCH` / `E_OFFSET_OOB` / `E_FIELD_CONSTRAINT` / `E_PROFILE_UNSUPPORTED` / `E_INTERNAL`；`E_UNKNOWN_OPCODE` 仅见于回放工具与历史数据（[protocol-profile-format.md](protocol-profile-format.md) §9） |
| `opcode` | 定宽十六进制字符串，如 `0x01A3` |
| `direction` | `S2C` / `C2S` / `NONE` |
| `message` | 解析器生成的简短说明，例如 `CONTENT_FINDER_POP.roulette_id: value is outside the declared constraints` |

**`message` 中不会出现任何报文字节。** 自 contracts/CHANGELOG.md 第 18 条起，未声明的
opcode 只计入 `ignored_count`，不再出现在该表中。客户端版本变化由 `game_build` 门禁以
fail-closed 方式发现，不依赖对未知 opcode 的计数；对未知 opcode 计数已足以察觉游戏更新，
无需抄录报文内容（docs/privacy-boundary.md §5）。
该约束由 `tests/Collector.UnitTests/CaptureDiagnosticsTests.cs` 以正则逐条断言，
而非依赖编码时的人工注意。

### 5.4 连接键

每条 `DecodedMessage` 携带一个 `ConnectionKey`，取
`SHA-256(会话 id | 本地 IP | 本地端口 | 远端 IP | 远端端口)` 的前 16 个十六进制字符。
该值可用于区分连接，但**不含任何地址**；由于会话 id 参与哈希，它跨会话不可关联。

### 5.5 抓包运行但无记录产生：`silent_reason` 与 `ingress.*`

抓包状态为 `RUNNING`、档案已匹配、解析失败计数为 0 且没有有效事件，是本软件最具误导性的
一种显示，因为它与健康状态的表现完全一致。造成该状态的原因有三类，对应的处理方式各不相同。
因此本软件不作推断，而是直接报出判定结果。

`silent_reason` 取值（`$defs/CaptureStatus.silent_reason`，UPPER_SNAKE 令牌）：

| 值 | 判定依据 | `hint` 给出的处理建议 |
|---|---|---|
| `NONE` | 健康，或证据尚不足以判定。启动后 60 秒内不作判定，证据确凿时除外 | 无 |
| `MIDSTREAM` | 开始抓包时游戏已有连接且始终未解出 IPC；或网卡上读到了足够多的报文（≥ 20 条），而**每一条**都因未观察到握手而被丢弃在重组阶段 | 返回标题画面重新登录，无需关闭游戏；或先启动本软件再启动游戏 |
| `NO_PACKETS_ON_ADAPTER` | 运行超过 60 秒，`raw_packets_observed` 仍为 0 | 可能正在使用加速器或 VPN，请在捕获诊断页重新选择网卡 |
| `NO_STREAM_OWNERSHIP` | 存在原始报文，但没有任何连接被系统确认属于游戏 | 关闭加速器或代理，必要时以管理员身份运行 |

`midstream_suspected` 的含义保持不变，等价于 `silent_reason == "MIDSTREAM"`。

`ingress` 对象是上述判定的原始依据，也是重组阶段每一条丢弃路径的计数。这些路径此前
**既无计数也不写日志**，故障因此无法被发现。

| 字段 | 含义 |
|---|---|
| `dropped_no_stream` | 报文所属连接没有被跟踪的流，这是中途接入的典型形态；或该方向已因空洞被放弃 |
| `dropped_no_syn` | 该方向从未观察到自己的 SYN，无法安全重放 |
| `expired_streams` | 等待系统确认连接归属超时（30 秒）而释放的流 |
| `unconfirmed_tuples` | 从未被系统 TCP 表确认属于游戏的连接数（按连接去重） |
| `stream_resets` | 因丢包空洞在容忍时间（5 秒）内无法补齐而放弃的方向数 |
| `adapter_dropped` | Npcap 报告的驱动或网卡丢包数。少量丢包仅降级为 `DEGRADED`，持续丢包才判定为故障 |
| `handshakes` | 因观察到 TCP 握手而建立的流数，不区分程序。长时间为 0 表示该网卡上没有握手报文 |
| `game_connections` | 开始抓包以来系统归属于游戏的连接数，已去重 |
| `game_connections_now` | 最近一次读数时系统归属于游戏的连接数 |

**`game_connections` 大于 `preexisting_connections` 时，不能再判定为本软件启动过晚。**
这两个数值将一份 `messages_decoded: 0` 的报告区分为两种情况。两者相等时，客户端始终沿用
抓包开始之前已建立的连接，返回标题画面重新登录可以解决问题。前者大于后者时，客户端已在
抓包期间重新建立连接而本软件仍无法解出报文，说明握手报文在入口处即已丢失，再次登录没有
意义（2026-09-13）。提示语句与诊断页的警告均依据该区分选择措辞。

同一组数值每 30 秒以 `ingress_stats` 事件写入本机诊断日志，因此一次无记录的会话在事后
仅凭日志便可定位。日志中不含任何地址、路径或报文内容。

**两个超时时长含义不同，不可混用。** 等待连接归属确认的时长为 30 秒。默认 Oodle 模式需要
复制并扫描 `ffxiv_dx11.exe`，在冷盘上可能超过 5 秒；以 5 秒淘汰该连接等于取消它唯一的机会。
空洞容忍时长为 5 秒，且**只丢弃该方向的滞留报文**，解码器与另一方向继续工作。
此前的实现会连同解码器一并删除整条流，一次丢包便会使一条正常连接的全部计数永久冻结且不报错。

**首包缓冲预算耗尽不再中止抓包。** pcap 过滤器按本机地址过滤而非按进程过滤，本机所有程序的
TCP 流量共用同一份预算；归属确认又需等待 30 秒，一次普通下载便会将预算填满。预算耗尽时
释放的是**最旧且尚未被认领的连接**，整条释放而非截断，并计入 `ingress.expired_streams`。
仅当每一条被跟踪的流都已进入解码、无可释放时才 fail-closed。
"绝不将残缺的前缀送入解码器"这一约束保持不变。

## 6. 有界队列与背压

- 容量默认 4096 条，可通过 `application_settings` 的 `capture.queue_capacity` 调整
  （范围 512–65536，越界自动收敛到边界）。
- 队列满时的策略为**丢弃最旧的报文**，并累加 `packets_dropped`。
- **绝不**阻塞抓包回调线程，**绝不**允许队列无界增长。
  抓包回调一旦阻塞会造成驱动层丢包，其诊断难度高于本软件自身丢包。
- 恒等式为 `offered == delivered + dropped`。单元测试以 10 万条报文配合一个阻塞的 sink
  验证该恒等式，并同时验证队列占用不超过容量。
  `tests/Collector.IntegrationTests/SoakTests.cs` 在完整抓包链路上再次验证同一恒等式：
  使用真实的 `CaptureController`、有界队列、档案解析器、状态机与 SQLite，
  以不低于 5000 msg/s 持续推送混合流量，包括合法记录、未知 opcode、畸形长度、重复报文
  与 2 万条突发，最后断言 `messages_decoded == parse_ok + parse_fail + ignored + dropped`
  严格相等。实测数值见 [release-checklist.md](release-checklist.md) 第 4 节。
- 解析线程为**单线程**，按观察顺序调用 `IDecodedMessageSink.Accept`。
  队列自身会捕获 sink 异常并计入 `SinkErrorCount`，worker 不会因此崩溃。生产环境下的
  `CaptureController` 会将该会话置为 `FAILED`，避免界面仍显示运行中而后续记录持续丢失。
- 发生丢弃时，当前进行中的记录会被标记为可能不完整。若关键事件因此缺失，
  该记录最终判定为 `INTERRUPTED`（见 [state-machine.md](state-machine.md) §3.7）。

## 7. 监视器故障

Machina 在自身线程内将失败写入 `Trace`，而不向调用方抛出异常。因此
`MachinaCaptureSource` 在抓包期间挂载一个 `TraceListener`，其行为如下：

- Machina 的每一行 trace 都会被脱敏并**分类**为 `fatal` / `decode_error` / `diagnostic`，
  但落盘的**只有该分类**，形式为 `capture/monitor_trace {kind}`。上游 trace 字符串可能含有
  报文十六进制、地址与路径，因此文本本身不跨越日志边界。
- 写盘同时采样。每一类在每个抓包源生命周期内最多写 **8** 行，此后按至多 **30 秒**一次
  汇总为 `capture/monitor_trace_summary {fatal, decode_error, diagnostic, interval_ms}`，
  并在停止时补写一次。缺少该采样层时，一次丢包会导致该方向后续每个压缩 bundle 各写一行，
  40–70 分钟便会将当天日志滚掉。**被采样略去的仅是磁盘写入**，
  下述计数与故障通知不会有任何遗漏。
- 命中致命标记（`Cannot load`、`Unable to retrieve network data`、`PcapException`、
  `Error opening`、`Cannot find one or more signatures`）时，抓包进入 `FAULTED`
  （契约中 `state = FAILED`），会话以 `end_reason = ERROR` 关闭，
  并通过 `ICaptureLifecycleListener` 通知状态机按 `INTERRUPTED` 处理。

缺少这一层时，启动后即失效的监视器只会表现为报文数持续为 0，与选错网卡的表象相同。

## 8. 诊断日志

- 位置：`%LOCALAPPDATA%\MentorRecorder\logs\collector-YYYYMMDD.log`
- 格式：结构化日志，每行一条 JSON，字段为 `ts` / `level` / `component` / `event`
  以及有限的元数据。
- **绝不写入**：原始字节、十六进制转储、聊天内容、角色名、IP、MAC、SID、
  其他用户的路径、其他玩家信息。允许写入：opcode 编号、方向、长度、解析出的 id 型字段、
  档案 id、计数与耗时。
- 兜底机制：`RotatingFileLogger.Sanitize` 在写盘前逐条替换下列五类内容。
  Machina 的 trace 行同样经过该步骤后才落盘。

  | 类别 | 匹配 | 替换为 |
  |---|---|---|
  | 任意用户的 profile 路径 | `[A-Za-z]:\Users\<name>` | `%USERPROFILE%` |
  | Windows SID | `S-1-<n>-<n>…` | `[sid]` |
  | IPv6 字面量 | 完整八段式与各种 `::` 压缩式 | `[ip6]` |
  | IPv4 字面量 | 点分四段 | `[ip]` |
  | 十六进制串 | 连续 **16 位及以上** | `[hex]` |

- 十六进制串的门槛取 16 位而非 8 位，目的是**保留短哈希 id**。适配器指纹为 SHA-256 的
  前 12 位（`SanitizedDiagnosticsReport.AdapterFingerprintLength`），UUID 的分段更短。
  这两类值不泄漏任何信息，却是日志中区分两张网卡、两个会话的唯一依据。
  脱敏只应移除秘密，不应一并移除可诊断性。
- 替换顺序固定，匹配范围宽的规则先执行，且**宁可多删**。损失少量上下文是可接受的代价，
  遗留一个地址则是事故。
- 五类规则全部由 `tests/Collector.UnitTests/DiagnosticsLogHygieneTests.cs` 强制，
  **不存在 Skip 用例**。其中一个用例专门反向断言 12 位指纹与 UUID 不会被误删。
- 滚动策略：**按大小**滚动，单文件上限 2 MiB，最多保留 5 个文件，总量上限约 10 MiB，
  另按 7 天清理。
- 日志写入失败（磁盘已满、权限不足）**绝不**导致进程崩溃。诊断信息可以放弃，用户数据不可以。

## 9. 脱敏诊断报告

诊断页的「导出脱敏诊断报告」生成一份 JSON，供用户附在 issue 中提交。
该报告采用**白名单**机制而非过滤机制，只复制下列字段，因此后续向快照中新增字段不会造成意外泄漏。

包含：`report_version`（当前为 2，见下）、`generated_at_utc`、`app_version` / `collector_version`
（两个二进制同一个版本戳）、`ipc_protocol_version`、`live_capture_status`
（本次构建的 `CaptureDiagnosticsSnapshot.LiveCaptureStatus`）、`public_distribution_ready`、
`boundary`（`monitor_type` / `injected_hook_enabled` / 监听端口数（恒为 0） /
`outbound` / `reads_game_process_memory` / `reads_game_executable` / `oodle_mode`）、
`npcap`（状态、版本、兼容模式、AdminOnly、是否提权）、
`game`（是否运行、`process_id`、区服、`game_build`、实例数、安装路径是否可读）、
`adapter`（是否已选、指纹、地址数量）、
`capture`（状态、时长、连接数、速率、`last_error_code`）、
`counters`（全部计数器）、`profile`（含 `origin`，取值为随包、本机校准或共享校准）、
`calibration`（状态、模板档案 id、本机档案 id、阻塞原因、进度、`evidence`、`shared`（§9.6）；
**不含**时间线，因为时间线记录了用户完成过的副本与轮盘）、`run`、
`recent_parser_errors`（与 §5.3 为同一批行，由同一个渲染函数产出，
因此提交的报告与诊断页的截图不会出现差异）。

`--capture-doctor --json` 在该白名单之外另加两项，因为它运行于**尚无数据库、尚无 IPC**
的场合：`adapter_count`，以及 §9.3 的 `oodle_temp_copies`。

报告**不包含**下列内容，该约束由 `tests/Collector.UnitTests/ExportDiagnosticsReportTests.cs`
以正则逐条断言：任何 IPv4 / IPv6 字面量，包括已打码的地址；任何文件系统路径，以及
`%USERPROFILE%` 等环境变量占位符；任何 Windows SID；`adapter_id` 本身；任何十六进制报文
转储；任何 `warnings` 自由文本。

### 9.1 导出方式

导出由 IPC 消息 **`ExportDiagnosticsReport`** 完成：

```
→ { "target_path": "C:/Users/<me>/Desktop/diag.json" }   // 可省略
← { "target_path": ..., "byte_count": ..., "completed_at_utc": ... }
```

- 省略 `target_path`，或指向一个已存在的目录时，写入
  `<数据库目录>/diagnostics/diag_<yyyyMMdd_HHmm>.json`。
- 目标可以是个人文件夹之外的任意可写本地目录，包括其他盘符。
  该消息与 `ExportCsv` / `ExportJson` / `BackupDatabase` / `ExportCandidateEvidence` 共用
  `ExportPaths`，拒绝 UNC、网络映射盘、设备路径及备用数据流；目录链接或文件链接的最终目标
  也必须位于本机。
- **只有本软件自行生成的文件名** `diag_<yyyyMMdd_HHmm>.json` 会被就地替换。报告本身即一次
  新的观测，同一分钟内导出两次会落到同一个文件名上。其余目标文件已存在时返回
  `ERR_EXPORT_FAILED`，除非请求中显式带有 `overwrite: true`。文件对话框中的一次误操作
  不应破坏用户的文档。

命令行 `--capture-doctor --json` 仍然保留，用于**尚无数据库、尚无 IPC** 的场合。
例如 docs/live-validation-guide.md 要求测试者在任何抓包之前先执行一次。
桌面端不再使用该子进程通路。

### 9.2 抓包取证 trace（`--capture-trace`）的内容范围

`--capture-trace <out.jsonl>` 是第二种脱敏产物，用途与诊断报告不同。诊断报告说明本机当前
处于什么状态，trace 则记录一段时间内出现过哪些 opcode。它属于
[privacy-boundary.md](privacy-boundary.md) §5 所定义的诊断模式产物：由用户显式开启、
容量有界、并按保留策略清理。

**保留策略**（`CaptureValidationController.SweepTraces`，在启动时与每次会话结束后各执行一次）：
界面发起的验证将每次会话写入 `<数据库目录>\traces\<时间戳>-<GUID>\`，只保留**最近 10 次**
会话，且不保留超过 **7 天**的会话。清理只删除该服务自身按上述命名规则写入的完整会话目录，
用户自行放入的内容一律不动。命令行 `--capture-trace <out.jsonl>` 写入的是用户指定的文件，
不受该策略约束，也不会覆盖已有输出。

**时长上限**：界面发起的验证最长运行 **2 小时**
（`CaptureValidationServices.MaxSessionDuration`），到时自动停止。命令行
`--duration-seconds <n>` 最大取 86400；**省略该参数或取 0 表示不限时长**，
此时只有行数上限（`--max-lines`，默认 20 万）、Ctrl+C 与游戏退出可以结束它。

trace 同样采用**白名单**机制，每条消息只写入下列字段。

| 字段 | 含义 |
|---|---|
| `seq` | 本次取证内的序号 |
| `t_ms` | 相对取证开始的**单调**毫秒 |
| `at_utc` | UTC 时间戳，取自**本机时钟**。该行为自 0.2.4 起生效，更早版本的取证在此处记录的是服务器 epoch |
| `epoch` | 该报文所属压缩包的**服务器 epoch**（毫秒）。`at_utc` 改用本机时钟之后，两代取证依靠该字段对齐 |
| `conn` | 连接键（不含任何地址，跨会话不可关联；见 §5.4） |
| `dir` | `S2C` / `C2S` |
| `seg` | 段类型 |
| `op` | opcode，写作 `0xNNNN` |
| `len` | **负载长度**（字节数） |
| `h12` | 负载 SHA-256 的**前 12 位**十六进制 |

`h12` 仅用于在同一份取证内比对重复负载。它不是加密手段，也不构成匿名化保证，
不能作为公开分享整份 trace 的依据。

文件首行为文件头，包含 `trace_version` / `synthetic` / `started_at_utc` / `npcap_version` /
`game_build` / `region` / `adapter_fingerprint` / `collector_version` / `oodle_mode` /
`live_capture_status`，其中最后一项与 `--capture-doctor` 报告的是同一取值。
末行为 summary，包含消息数、解码错误、丢弃数、时长、是否因行数上限被截断，以及
Top 40 opcode。两者之间可能夹有用户输入的标记行（`marker` / `t_ms` / `at_utc`）。

真机 trace 还有一项前置条件：必须从**新建立的游戏 TCP 连接**开始。
若采集服务发现游戏在所选适配器的本地地址上已存在活动连接，将拒绝启动 `--capture-trace`，
而不是写出一份几乎全为 `decode_errors` 的中途接入文件。对 Oodle 流而言，
这类文件不属于低质量证据，而属于不可信证据。
所选适配器没有可绑定的 IPv4 地址时同样拒绝启动，不会退回到全进程连接计数，
也不会交由 Machina 自行选择网卡。
抓包源同时关闭 Machina 的远端 IP 过滤，以免在监听器安装期间遗漏有状态解码所需的连接初始
数据。无关候选包仍会被目标连接的 IP/TCP 元组解码器丢弃，且不会落盘。

**不包含**（由 `tests/Collector.UnitTests/CaptureTraceTests.cs` 对产出的字节直接反向断言）：

- 任何负载字节，无论以十六进制、Base64 还是其他形式书写；
- 除 `h12` 之外**任何长于 12 位的十六进制串**。断言方式是先抹除 `h12` 字段，
  再断言全文不含 13 位以上的十六进制串；
- 任何 IPv4 / IPv6 字面量。**已经哈希过的连接键同样不写入**，
  因为区分连接对识别 opcode 没有帮助；
- 角色名、聊天内容、队友或任何其他玩家的信息；
- 网卡 GUID，只写入其 12 位指纹。

标记同样不是自由文本，只接受 `queued`、`pop`、`entered`、`victory`、`left`
五个固定词，大小写不敏感，落盘时统一为小写，其他输入一律忽略。因此地址、路径、
报文片段与角色名无法经标记入口进入 trace。

有界性是**强制约束**而非约定。消息行默认上限 20 万行（`--max-lines`），
标记行上限 1 万行；达到上限后只计数、不再写入，并在 summary 中标记 `truncated`。
时长由 `--duration-seconds`、游戏进程退出或 Ctrl+C 三者之一封顶。
输出路径已存在时命令拒绝覆盖，以免破坏既有证据。
结束时写出 `<out>.sha256` 边车文件，未附哈希的证据不予采信
（[protocol-profile-format.md](protocol-profile-format.md) §5）。

该模式**不打开数据库、不建立命名管道、不加载协议档案、不解析任何字段**，
因此既不可能写出记录，也不可能受未验证的 opcode 影响判定。
离线分析使用 `--trace-report`，其用法与候选表的解读方式见
[live-validation-guide.md](live-validation-guide.md) §3.5。

### 9.3 游戏程序临时副本的核对（`--capture-doctor`）

为解压流量，本软件会将游戏可执行文件复制到临时目录，并在自身进程内加载该副本
（[privacy-boundary.md](privacy-boundary.md) §4）。`--capture-doctor` 如实报告这些副本的
当前数量：

```
游戏程序临时副本（只看本软件登记过的文件，不扫描临时目录）
  尚未删除:            0
  占用字节:            0
  登记但已不存在:      0
```

`--json` 输出同样的三项，位于 `oodle_temp_copies` 对象中：
`registered_present` / `registered_bytes` / `registered_missing`。

该自检读取的是**清单文件** `<数据库目录>\oodle-temp.json`，而不是枚举 `%TEMP%`。
清单中只有本软件自行创建过的路径，其所有权是确定的；而枚举临时目录的自检会读取与本软件
无关的文件。`尚未删除` 大于 0 时，输出附带说明"这些副本会在下次启动采集服务时自动删除"。
回收发生在 `CollectorHost.Open`，只删除清单中仍然存在、且确实位于 Machina 专属临时子目录
下的条目。

### 9.4 校准受阻时 `calibration.evidence` 的解读

处于校准中的报告最常见的疑问是：用户已完成一次副本，为何四行进度均显示未观察到。
下列字段用于区分各种"未观察到"的成因，应按表中顺序解读。

| 字段 | 受阻时说明什么 |
|---|---|
| `pairs` | 格式为 `0x申请->0x回执=次数`。存在一条即表示申请与回执均已识别。仅有一条时草稿尚不能锁定弹窗，系统会要求再排一次不同的随机任务。同一条回执下出现两个不同的申请 opcode 未必构成歧义：该版本一次申请返回两条回执，用户按下按钮时客户端还会发出其他 24 字节报文，其中一条若恰好以相同的小数字开头，即会与多出的那条回执配对。以次数占多数者为准，次数接近时才判定为歧义 |
| `pops` | 格式为 `0x回执=总数/回执窗口内的条数`。两者相等表示该 opcode 上只出现过紧随申请的回执，**匹配成功的那一条从未到达**，或未被捕获 |
| `finder_lengths` | 已完成配对的 opcode 此后又出现过多少条、长度各为多少。它是区分"报文长度发生变化"与"报文不再出现"的唯一依据 |
| `finder_states` | 同一 opcode 上状态字段出现过的取值。国服旧版中回执为 0 与 7，匹配成功为 3 |
| `pop_refusals` | 形状匹配但被模板的哪一个字段拒绝。某个 opcode 上大量 `roulette_id` 被拒，通常表示它只是长度恰好相同的普通报文 |
| `roulette_echoes` | 格式为 `0x报文:长度=命中/该报文总数`。**不限长度**，统计在模板的随机任务编号位置带有用户实际申请过的编号、且已越过回执窗口的报文。当上述各项均表明弹窗报文不再出现时，线索在此。该项仅为线索，不参与任何判定 |
| `match_echoes` | 格式为 `0x报文:长度=进本前出现过几次/总共进本几次+独立出现几次@距加载开始多少秒`。上一行按数值筛选，本行按时间筛选。判据有三项：**每次进本前都出现**；**时间距离**为距加载开始数十秒，宣布匹配的报文需等待用户决定与读条，伴随报文则紧贴 0 秒；**独立性**即 `+N`，表示报文出现过但其后没有进本。匹配报文在接受与拒绝两种情形下都会发出，伴随报文在没有加载时则不存在。拒绝一次匹配便可区分两者 |
| `markers` | 格式为 `0x报文:长度@第几个字节=命中/该形状被扫过几条x跟过几个不同的随机任务`。上述两行只检查模板声明的那一个字节位置，本行**不预设位置**，将每条服务器报文的每个字节与用户当前正在排的随机任务比对一次。判据只有一条：该位置每次都带有**当时所排的编号**，且跟随过**至少两个不同的随机任务**（`x2` 及以上）。普通报文无法跟随一个持续变化的数值。`x1` 不能说明任何问题，列出它只是为了让受阻的报告能指出差距所在 |
| `marker_overflow` | 上述表格溢出过多少次。溢出只削弱该项扫描的效果，不影响任何判定 |
| `carried` | 本轮观察从上一次运行继承的报文条数。该值为 0 而磁盘上本应存在证据时，说明证据未被读入，属于软件缺陷，而非用户未进行游戏 |
| `watched_seconds` / `quiet_seconds` | 观测器实际监听的时长，以及距其最后一次收到报文的时长。**应优先查看这两项**。`watched_seconds` 远小于用户实际在线时长，或 `quiet_seconds` 达到数十乃至上百秒时，说明缺少的是报文而非游戏行为，继续游戏无法补足 |
| `clusters_at` / `pairs_at` | 每一次换区与每一次排本配对发生在导出时刻之前多少秒。该值为相对时间，不含任何绝对时间。若用户完成了两次副本而只有一个 `clusters_at`，便可直接判断缺失的是哪一段 |
| `duty_zones` | 有多少次换区识别出了副本表中的区域。该值为 0 而 `clusters` 不为 0 时，说明本版本同时调整了区域报文，继续游戏无助于校准 |
| `zone_candidates` / `territory_candidates` | 模板所声明的两个长度上剩余的候选数量 |
| `zone_shapes` | 换区报文所在长度上每个形状的表现，格式为 `0x报文:长度=恰好出现一次的簇数/总簇数+簇外出现次数`。真正的换区标记表现为前一项数值大、后一项数值小。`zone_candidates` 为 0 时，本行是唯一能说明原因的字段 |
| `job_shapes` | 职业报文形状（模板声明的方向与长度）上每个 opcode 的表现，格式为 `0x报文=佐证的换区簇数/总簇数!与之矛盾的簇数 v读到的职业编号`（最多列 4 个取值）。判据要求进本簇与出本簇都佐证、至少半数簇佐证、没有任何簇矛盾，且满足条件的 opcode **恰好一个**。`progress.job_seen` 为 false 时，本行说明原因：没有一行满足条件、两行并列，或唯一的一行被某个簇否决（取值越界，或同一簇内取值不一致）。取值为 ClassJob 编号，与记录中保存的职业编号相同 |
| `timed_candidates` | 按**出现时机**判断的候选，每个未出局的形状一行，格式为 `0x报文:长度=排本期间出现次数+进本之前出现次数/总次数!离群次数 e先于几次进本/进本总数 lead最小提前量秒`。国服 2026.09.15 客户端的匹配通知在任何字节位置都不带轮盘编号，`markers` 与 `match_echoes` 都指不出它，只能看它何时出现：真正的匹配通知只在排本期间出现（离群为 0），先于每一次自己排本后的进本，提前量是玩家确认与读条的那几秒到十几秒；伴随加载的报文提前量接近 0。判据要求至少 2 次进本、至少 2 个不同轮盘，并列时取最小提前量最大者（见 [protocol-profile-format.md](protocol-profile-format.md) §11.5） |
| `timing_overflow` | 上述表格溢出过多少次。与 `marker_overflow` 不同，该值不为 0 时**不会给出任何按时机认定的候选**：判据的说法是「每次进本之前它都出现过」，而没能入表的形状既不能佐证也不能否定这句话 |
| `zone_outside` | 换区报文所在长度上，**既出现在换区簇内、又在簇外出现过**的形状数量。`zone_candidates` 为 0 时应先查看本项。该值不为 0 表示形状仍然存在，只是本次抓包遗漏了报文：某次换区未被识别为簇，簇内的标记即被记为在簇外出现，而一次簇外出现即永久取消候选资格。此时应重新登录游戏并点击「重新观察」，无需等待新版本软件。两项同时为 0 才表示长度确实发生了变化 |
| `outside_keys` / `overflow` | 前者为在换区簇之外出现过的形状数；后者非零表示有界表曾经溢出，证据已不完整，只能重新观察 |

`progress` 中的 `duty_entry_seen` 与 `duty_exit_seen` 判断的是本次换区能否佐证匹配弹窗，
而非用户是否进入过副本。后者由 `duty_zone_seen` 表示，卡片上的「进本」一行即按该字段显示。

### 9.4.1 校准证据的持久化

校准累积的证据写入 `%LOCALAPPDATA%\MentorRecorder\calibration\<区服>.<客户端版本>.json`。
关闭软件、升级版本或重启计算机都不会导致证据丢失，下次启动后继续累积。

- 文件按**区服与客户端版本**命名，并附带当时所用**模板档案的哈希**。游戏版本或模板发生变化时，
  旧文件被直接忽略。
- 文件内容与诊断报告中已输出的内容一致：opcode、报文长度、字节偏移、id 类数值（轮盘、区域、
  职业），以及脱敏后的连接标签与时间。**不含任何负载字节**（docs/privacy-boundary.md §5.2）。
- 点击「重新观察」时一并删除该文件，否则下次启动会重新载入已被放弃的证据。
- 校准完成并写出本机档案后同样删除该文件。仅「按排本申请推断」生成的临时档案予以保留，
  因为仍需继续查找真正的匹配报文。
- 文件无法读取、版本不匹配或内容损坏时，一律按无证据处理，不会因此导致启动失败。

### 9.5 认不出匹配报文时：按排本申请推断

上述全部扫描都可能落空。某一版本完全可能将匹配成功发送为一条不带随机任务编号的报文，
此时无论进行多少次游戏都无法识别。在这种情况下本软件不放弃判定，而是
**以用户自身发出的排本申请替代匹配报文**，再由随后一小时内进入的、副本表可识别的副本
确认该次确实匹配成功。

该路径属于推断而非观察，因此在每一处都标明其推断性质：

- 档案中 `CONTENT_FINDER_POP` 的方向为 `CLIENT_TO_SERVER`，因为服务器宣布匹配的报文不可能
  由客户端发出。`match_window_seconds` 取 3600 而非模板的 120，它度量的是排队时长而非确认
  倒计时。
- 状态机对该类档案附加一条规则：只有**副本表可识别的区域**才计为进本，普通传送不计，
  否则排队期间的一次城内传送会被误判为进本。
- 卡片首行提示"已经可以正常记录导随了，软件还在后台找更准的判定依据"。校准**不会停止**，
  找到真正的匹配报文后会再次请用户核对并替换该档案。

已知且唯一的代价是：用户申请随机任务后取消，并在随后一小时内手动进入了一个副本，
该次会被记入已取消的那个随机任务名下。协议本身不提供区分这两种情况的依据。

### 9.6 共享校准：`calibration.shared` 与 `boundary.outbound` 的解读

共享校准是本软件默认会主动发起的网络请求之一
（[privacy-boundary.md](privacy-boundary.md) §8.2）。另两类是默认关闭的在线语音与默认开启的更新检查，
均见本节后半部分。
报告中与共享校准相关的只有下列两部分，二者均为白名单，不含地址、主机名、IP、校准码正文、
opcode 与负载字节。

**`boundary.outbound`** 自报告版本 2 起取代恒为 0 的 `outbound_connections`。引入共享校准之后，
该恒定的 0 已不再准确。

| 字段 | 含义 |
|---|---|
| `shared_calibration_enabled` | 设置「获取共享校准」是否开启。读不到设置时（例如 `--capture-doctor`）取默认值 `true` |
| `kill_switch` | 进程环境变量 `MR_DISABLE_SHARED_FETCH` 是否关闭了整个获取流程。除空值、`0`、`false` 之外的取值均视为关闭 |
| `last_fetch_utc` | 本进程**实际发出**请求的最近一次时间，从未发出时为 null。开关关闭、已有可用档案，或用户已选择不使用共享校准时都不会发出任何请求，该字段保持 null |
| `last_fetch_status` | 该次请求的总体结果：`OK` / `CANCELLED` / `INDEX_UNAVAILABLE` / `NONE_FOR_BUILD` / `CODES_UNAVAILABLE` |

`boundary.outbound.online_speech` 自 2026-09-16 起提供，属于只增不删的变更，报告版本仍为 2。
它说明第二类出站请求，即由用户自行开启的在线语音
（[privacy-boundary.md](privacy-boundary.md) §8.3）：

| 字段 | 含义 |
|---|---|
| `provider` | `none` / `azure` / `openai_compatible`。取 `none` 时不发出任何请求 |
| `kill_switch` | 进程环境变量 `MR_DISABLE_ONLINE_SPEECH` 是否关闭了整个在线语音功能 |
| `last_request_utc` | 本进程**实际发出**语音请求的最近一次时间。缓存命中、未配置，以及被总开关拦截的情形均不计入 |
| `last_outcome` | 该次请求的结果：`OK` 或一个 `ERR_SPEECH_*` 错误码 |

该对象不含区域、地址、主机名、模型、音色与播报文字，也不含密钥。密钥仅保存在
`speech-key.bin` 中，并以 DPAPI 加密。

`boundary.outbound.update_check` 自 2026-09-17 起提供，同样属于只增不删的变更，报告版本仍为 2。
它说明第三类出站请求，即默认开启、只读取发布页版本号的更新检查
（[privacy-boundary.md](privacy-boundary.md) §8.4）：

| 字段 | 含义 |
|---|---|
| `enabled` | 采集设置 `update_check_enabled` 是否开启。读不到设置时取默认值 `true` |
| `kill_switch` | 进程环境变量 `MR_DISABLE_UPDATE_CHECK` 是否关闭了整个更新检查。除空值、`0`、`false` 之外的取值均视为关闭 |
| `last_checked_utc` | 本进程**实际发出**请求的最近一次时间，从未发出时为 null。检查只在桌面端连接并轮询时顺带发起，且每 24 小时最多一次 |
| `last_outcome` | 该次检查的结果：成功，或一个表示失败原因的短令牌。失败一律静默，界面上不出现任何提示 |
| `latest_version` | 上次检查取到的版本号，未取到时为 null |

该对象不含主机名，也不含地址：检查读取的是一个固定地址，没有可变部分可供记录。

监听端口仍恒为 0。除共享校准、用户开启的在线语音与更新检查之外，不存在任何出站连接。

**`calibration.shared`** 与 `CaptureStatus.calibration.shared` 为同一个对象，由同一个函数产出。

| 字段 | 受阻时说明什么 |
|---|---|
| `phase` | 总体阶段。`UNAVAILABLE` 表示上次获取时所有源均不可达或未取到校准码，本机校准照常进行；`VERIFYING` 表示已有校准码正等待本机流量核实；`AWAITING_CONSENT` 表示按排本推断的校准码已通过，等待用户确认一次；`VERIFIED` 表示共享档案正在使用；`REJECTED` 表示全部校准码均不匹配、已被撤销、在用档案被撤下，或用户选择了不使用共享校准，此时应先查看 `user_rejected` |
| `last_fetch_status` / `last_index_attempts` | 该版本最近一次获取的总体结果，以及每个源（`GITHUB_RAW` / `CDN_PRIMARY` / `CDN_FALLBACK`）索引请求的结果码。三个源全部为 `DNS_OR_CONNECT` 或 `TIMEOUT` 表示用户网络无法到达，此时应引导用户使用「导入校准码」 |
| `candidates[]` | 每份校准码一行，包含 `sha12`（码身份的前 12 位，可与公开仓库中的文件名对照）、`source`（`DOWNLOADED` 为下载，`MANUAL` 为手动导入）、`match_source`、`status`、`verdict`。正在使用的共享档案排在第一位 |
| `candidates[].criteria[]` | 每条声明报文对应一项判定，包含 `message`（语义名，非 opcode）、`verdict`（`PASS` / `WAIT` / `CONTRADICTED`）、`reason`（中文原因）、`contradicting_sessions`（已有多少个**健康**抓包会话与之矛盾，达到两个才判定为拒绝）。长期为 `WAIT` 且原因为未观察到登录时的换区，通常说明本软件在游戏登录之后才开始抓包（§5.5），而非校准码存在问题 |
| `candidates[].staging_overflowed` | 暂存事件超过上限，该校准码在本会话内无法绑定，需在下一个抓包会话重试 |
| `candidates[].provenance` | 1.1.0 起。`PUBLISHED` 表示本机最近一次读到的索引列出了这份码（下载来的，或导入后在索引里找到的），登录时换区判据通过即绑定，排本与进本判据在记录中继续核对；`IMPORTED` 表示导入后任何索引都不认识，三条判据全部通过才绑定。`IMPORTED` 的码长期停在 `VERIFYING`，通常是还没排过本，不是码有问题 |
| `candidates[].audit_pending` / `audit_pending` | 1.1.0 起。正在使用（或可绑定）的共享档案仍有绑定后核对的判据在等待。为 true 时校准保持布防，记录照常生成；两个健康会话判矛盾会撤下档案并把它自绑定起生成的记录标记待复核 |
| `profile_id` / `bound_at_utc` | 正在使用的共享档案，以及它在抓包会话内开始记录的时刻 |
| `last_refusal` | 上一次绑定或撤下失败的原因令牌：`NOT_SELECTED`（写出后目录未选中它）、`STAGING_NOT_FOR_THIS_SESSION`、`WRITE_FAILED`、`BUILD_*`、`STALE`（写出期间状态发生变化）、`CONTRADICTED`、`REVOKED`、`REJECTED`、`USER_REJECTED`、`INTERNAL`。令牌之外的细节（异常类型、路径）只保留在本机，不写入报告 |
| `rejected_candidates` | 因矛盾或撤销而被拒绝的校准码数量。矛盾记录跨重启保留，执行「重新观察」时清空 |
| `user_rejected` | 用户已选择「不用共享的，我自己校准」。在执行「重新观察」之前，该区服与版本不再获取、导入或绑定任何共享校准，本机校准不受影响。该标志与矛盾记录分开存储，不计入 `rejected_candidates` |
| `recheck` | 1.4.0 起，缺省为 null。已有档案在记录时仍读取索引的那一次：`last_utc` 为读取时刻，`status` 与 `last_fetch_status` 同一套取值，`reason` 说明为何允许读取——`SHARED_IN_USE`（在用的是其他玩家分享的校准，仓库可能已撤回它）或 `QUEUE_INFERRED_IN_USE`（在用的档案按排本推断匹配，认服务器报文的码比它更准）。随包档案或认服务器报文的本机档案在用时不读索引，该字段保持 null；已完整记录过一次且判据全部通过的非排本共享档案结束看护后校准解除布防，同样不再读取 |

## 10. 常见故障排查表

| 现象 | 可能原因 | 处理 |
|---|---|---|
| `ERR_NPCAP_MISSING`，`npcap.status = NOT_INSTALLED` | 未安装 Npcap | 按 §2 安装 |
| `ERR_NPCAP_MISSING`，`npcap.status = NOT_WINPCAP_COMPATIBLE` | 安装时未勾选兼容模式 | 重新安装并勾选 |
| `ERR_NPCAP_MISSING`，`npcap.status = NPCAP_ADMIN_ONLY` | Npcap 限制为管理员专用 | 以管理员身份运行本软件 |
| `ERR_NPCAP_MISSING`，`npcap.status = LOAD_FAILED` | 安装不完整 | 重装 Npcap |
| `ERR_BAD_REQUEST`，`field = adapter_id` | 无法确定游戏流量所在网卡 | 在诊断页手动选择一张网卡 |
| 适配器列表为空 | Npcap 服务未启动，或权限不足 | 检查 `npcap` 服务，并以管理员身份运行一次 |
| `packets_observed` 持续为 0 | 适配器选择有误，或游戏流量经由另一张网卡（例如 VPN 虚拟网卡） | 在适配器列表中改选，并确认游戏连接所在的网卡 |
| `profile.status = NONE`，`game.install_path_readable = false`，`region = UNKNOWN` | 无法读取游戏安装路径。0.2.1 及更早版本在客户端由管理员身份的启动器拉起时必然出现 | 升级至 0.2.2 及以上；仍无法读取时，请确认游戏仍在运行、且游戏所在磁盘已分配盘符。提权运行对此没有帮助：路径取自内核进程表，本就不需要额外权限 |
| `packets_observed` 增长但无记录产生 | 协议档案未达到 `VERIFIED`，触发 fail-closed | 查看诊断页的 `profile_status`，此时只能手工补录 |
| `packets_dropped` 持续增长 | CPU 占用过高，或队列容量过小 | 提高 `capture.queue_capacity`，并关闭其他抓包工具 |
| `state = FAILED` 且 `last_error_code` 非空 | 监视器致命错误（见 §7），或适配器被拔出、禁用 | 查看日志中的 `monitor_trace`，随后重新调用 `StartCapture` |
| 记录全部为 `INTERRUPTED` | 采集服务频繁重启，或抓包被反复中断 | 查看日志中的 `PROCESS_RESTART` 事件 |
| 游戏在运行但 `ffxiv_running = false` | 游戏以不同的进程名运行 | 在诊断页手动指定 `process_id` |

## 11. 用户可自行做的边界核对

核对项目参见 [privacy-boundary.md](privacy-boundary.md) §9。诊断页直接展示其中的关键项：
`monitor_type`、`injected_hook_enabled`、监听端口数（恒为 0）、`oodle_mode` 与
`reads_game_executable`。出站连接不再标注为恒为 0。本软件默认会发出的请求是获取共享校准与更新检查，
脱敏报告的 `boundary.outbound` 如实给出二者的开关状态（设置项与 `MR_DISABLE_SHARED_FETCH` /
`MR_DISABLE_UPDATE_CHECK`），以及本进程最近一次实际发出请求的时间与结果（§9.6）。用户自行启用在线语音后，另有
`boundary.outbound.online_speech` 记录的语音合成请求（[privacy-boundary.md](privacy-boundary.md) §8.3）。
除这三类之外没有任何出站连接。

## 12. 无游戏环境下的自检

在没有 FFXIV、没有 Npcap 的开发机或 CI 环境中，以下项目仍然可以验证：

- `bootstrap.ps1` 正确报告 Npcap **未安装**；
- `ListCaptureAdapters` 返回 `npcap_installed = false`、安装指引与真实网卡列表
  （IP 已打码且无一被推荐），而不是崩溃；
- `StartCapture` 返回 `ERR_NPCAP_MISSING`；
- `MentorRecorder.Collector.exe --serve --db <临时路径>` 能够建库、迁移、完整性校验、
  崩溃恢复并开启命名管道；
- 注入 `FakeCaptureSource` 后，完整抓包链路在没有驱动、没有游戏的条件下可以跑通，
  依次覆盖启动、解析、状态机、存储与 IPC、丢包统计、故障、停止，直至写出
  `capture_sessions` 行；
- `--replay` 与 `--replay-decoded` 能够分别以离线语义事件与解码报文驱动整个状态机。

上述项目全部由 `tests/Collector.UnitTests/Capture*.cs` 与
`tests/Collector.IntegrationTests/Capture*.cs` 自动覆盖，
不依赖游戏、Npcap 与网络。

真机验证流程参见 [live-validation-guide.md](live-validation-guide.md)。
本机构建的 `LIVE_CAPTURE_STATUS` 通过 `--capture-doctor --json` 读取，本文档不予复制。
