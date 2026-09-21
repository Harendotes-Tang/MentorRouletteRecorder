# 架构设计 / Architecture

> 本文档描述 **FF14 导随记录器（FF14 Mentor Roulette Recorder）** 的整体架构，
> 面向参与开发、评审与集成的读者。
> 术语与标识符使用英文，说明使用简体中文。

## 1. 目标与范围

本软件在 Windows 10/11 x64 上**被动**观察本机网络流量，自动记录"随机任务：指导者任务"
（mentor roulette）的历史，包括匹配时间、进入的副本、职业、结果与耗时。
记录保存在本地 SQLite 数据库中，由 Qt 6 QML 仪表盘呈现。
本软件另外提供手工补录与更正（append-only 修订）、CSV/JSON 导出、数据库备份，
以及可选的本地 TTS 播报。默认语言为简体中文。**所有数据仅存在本机。**

明确**不做**的事情见 [privacy-boundary.md](privacy-boundary.md)。该文档定义硬边界，
由 `tools/static-boundary-check/` 在每次 `scripts/verify.ps1` 中强制检查。

模块依赖方向由独立的 `tools/architecture-boundary-check/` 检查，并纳入同一验证流程。
该工具检查领域对外部实现的引用，以及历史/统计控制器对界面总控、进程监督器和 TTS 的依赖，
并以带行号的错误阻止边界退化。缺少必需源码或无法可靠检查时，该工具同样判定为失败。

## 2. 双进程结构

```
┌──────────────────────────────────────┐        ┌───────────────────────────────────────────┐
│  MentorRecorder.Desktop.exe          │        │  MentorRecorder.Collector.exe             │
│  C++20 / Qt 6 Quick / QML / CMake    │        │  C# / .NET 8 (net8.0-windows)             │
│                                      │        │                                           │
│  · 仪表盘 / 历史 / 统计 / 设置        │        │  · Machina.FFXIV + Npcap 被动抓包          │
│  · 手工补录与更正对话框               │  Named │  · 协议档案加载与 fail-closed 校验          │
│  · 图表（Qt Graphs）                  │◄─Pipe─►│  · 状态机与判定                            │
│  · 本地 TTS（Qt TextToSpeech）        │ client │  · SQLite 唯一写入者                       │
│  · 启动并监控 Collector 子进程        │ server │  · 导出 / 备份 / 审计                      │
│                                      │        │  · 命名管道服务端                          │
│  ✗ 从不直接访问 SQLite                │        │                                           │
└──────────────────────────────────────┘        └───────────────────────────────────────────┘
                                                                    │
                                                                    ▼
                                                        %LOCALAPPDATA%\MentorRecorder\
                                                              mentor_recorder.db
```

### 2.1 为什么拆成两个进程

| 理由 | 说明 |
|---|---|
| 权限隔离 | 抓包需要 Npcap 驱动访问，UI 不需要。抓包逻辑被限制在一个尽可能小的进程内。 |
| 单一写入者 | SQLite 只有 Collector 打开写句柄，彻底避免多写者锁竞争与半写状态。 |
| 语言分工 | Machina.FFXIV 是 .NET 库，Qt 6 Quick 属于 C++ 生态。两侧各自采用最合适的技术。 |
| 许可证边界清晰 | 两个进程都受 GPLv3 约束（见 [third-party-licenses.md](third-party-licenses.md)），但依赖树互不污染，便于审计。 |
| 崩溃隔离 | 抓包或解析异常不会影响 UI，UI 崩溃不会丢失正在进行的一次记录。 |

### 2.2 进程生命周期

- Desktop 启动时以子进程方式启动 Collector，可执行文件取自同一目录下的固定文件名，
  不接受路径参数注入。命令行固定为 `--serve --parent-pid <Desktop 的 pid>`，
  可另带 `--parent-start-time <UTC ticks|ISO-8601>`。
- 生产 IPC 入口由 `main.cpp` 创建 `CollectorProcess` 并显式传给 `AppController`，
  控制器先于借用的监督器析构。未提供监督器的后端不能启动、停止或接管本地 Collector；
  后端名称本身不赋予进程管理权限。
- Desktop 通过 `GetVersion` 完成握手，校验 `protocol_version == 1`；不匹配则拒绝继续。
- Desktop 定期发送 `GetStatus` 心跳。Collector 崩溃时，Desktop 显示明确的故障态并提供重启按钮。
- Desktop 正常退出时向 Collector 发送停止请求，超时后强制结束子进程。
- Desktop **被强制结束**时（任务管理器、`Stop-Process -Force`、会话结束），
  上述停止请求不会发出，因此 Collector 同时监视 Desktop 的存活状态。
  `--parent-pid` 启用 `Diagnostics/ParentProcessWatchdog.cs`，
  由 `Process.GetProcessById` 与 `WaitForExitAsync` 等待父进程结束。
  这是**进程存在性检查**，不是打开游戏进程句柄，符合
  [privacy-boundary.md](privacy-boundary.md) 第 2 节第 3b 条。
  源码中没有 `OpenProcess`，没有任何 P/Invoke，也不读取父进程的任何内容。
- Windows 的进程号一经释放即被重新分配，因此仅凭 pid 有可能指向一个无关进程。
  `--parent-start-time` 将 pid 与该进程的启动时刻配成一对（容差 1 秒）。
  两者不一致时，看门狗判定为**无法确认父进程**并停止后续动作，
  而**不是**判定父进程已退出。这两种判定导向相反的动作，其中只有一种是安全的：
  依据一个关于无关进程的猜测执行停止，会终止用户正在进行的记录；
  不执行任何动作，至多留下一个用户随时可以关闭的 Collector。
  启动时刻同样只来自进程列表，不需要任何句柄。不带该参数时的行为与此前一致。
  父进程结束后，看门狗触发与 Ctrl+C **完全相同**的停止路径
  （停抓包 → 按既有 lifecycle 把在途记录置为 `INTERRUPTED` → 停管道 → 释放 host），
  并以 10 秒硬退出为上限，防止某一步骤阻塞。
- 由此保证**不产生孤儿 Collector**。孤儿进程的实际危害不在于多出一个进程，
  而在于它占用按用户划分的 serve 租约，使下一次启动的 Desktop 复用一个
  用户既看不到、也无法停止的实例。
- Collector **不会**在没有 Desktop 的情况下自行长期后台驻留（无自启动、无服务注册）。

### 2.3 Collector 的文件落点

| 参数 / 变量 | 作用 |
|---|---|
| `--db <path>` | 数据库文件。省略时用受管位置。 |
| `--log-dir <path>` | 轮转诊断日志的目录。省略时按下面的规则推导。 |
| `MR_DATA_DIR` | 环境变量，一次性把整个根目录（数据库、日志、备份、默认导出目录）搬走。 |

日志目录的优先级：**显式 `--log-dir` > 显式 `--db` 旁边的 `logs\` > 受管位置**
（`MR_DATA_DIR`，否则 `%LOCALAPPDATA%\MentorRecorder\logs`）。

第二条规则解决的是下述问题。此前无论 `--db` 指向何处，日志目录一律从 `%LOCALAPPDATA%` 解析，
因此每一次 `dotnet test` 与每一次 `scripts/verify.ps1` 都会向用户的日志目录写入并轮转，
并**按保留期删除**其中的旧文件，覆盖用户用于排查问题的记录。
将 Collector 指向一个用完即弃的数据库，表明本次运行不属于用户的日常使用；
其诊断日志应当与该数据库相邻存放，其保留期清理也不应触及用户的日志目录
（review finding C1）。

### 2.4 Collector 的退出码

| 码 | 含义 | 调用方应有的反应 |
|---|---|---|
| 0 | 正常结束，或按请求停止 | — |
| 1 | `--validate-profile` 判定档案不合格 | 展示校验结果 |
| 2 | 命令行无法解析，或档案检查无法执行 | 修正调用方式 |
| 3 | 无法履行职责：数据库完整性、路径不可写、I/O 失败 | 提示故障，参见本机日志 |
| 4 | 本机已有 Collector 在服务同一条管道（`ERR_ALREADY_RUNNING`） | 连接正在运行的那一个实例 |
| 5 | 看门狗的优雅停止超时，进程被硬性切断 | 视为异常终止并记录 |
| 6 | 管道**存在且仍可接受连接**，但占用者连续 6 次、每次 500 ms（合计 ≥ 3 秒）都不应答 `GetVersion` 探活 | 请求其停止并等待其退出；仍不退出时结束该进程（进程号见日志目录下的 `serve.pid`）后重试 |

退出码 4 与 3 分开，是因为"本机已有实例在运行"是一个带有明确后续动作的**正常结果**，
而不是故障。调用方不应为此弹出错误对话框，也不应依靠匹配错误文本来区分两者。
退出码 6 与 4 分开，是因为两者的后续动作**相反**：4 要求连接该实例，6 要求结束该实例。

退出码 5 必须非零。该路径下数据库由运行时关闭，而非由本软件关闭；
若报告为 0，启动器、脚本乃至操作系统的作业统计都会认为那是一次干净的收尾。

**判定为 6 的情形只有一种。** 两道闸中的任何一道拒绝时，先向对象管理器查询该管道的当前状态：

| 管道状态 | 含义 | 探活 | 退出码 |
|---|---|---|---|
| 不存在 | 占用者已取得租约但尚未建立管道（正在启动），或已拆除管道但尚未释放租约（正在停止） | 不探活 | **4** |
| 实例已被占满 | 8 个实例全部在服务其他客户端，占用者处于存活状态 | 不探活 | **4** |
| 存在且可连 | 仅此一种情形可能是僵死实例 | 连接后发送 `GetVersion`，最多 6 次、每次 500 ms | 应答 → **4**；始终不应答 → **6** |

三秒的预算按"大型数据库执行 `integrity_check`、迁移与崩溃恢复"所需的时间估算。
早先的实现只探活一次、只等待 1 秒，于是一个正在升级数据库的健康实例被判为僵死，
桌面端据此对其执行 `taskkill /F`，反而制造出优雅停止本应消除的孤儿进程（评审 R-3）。

成功取得租约的进程会将自身进程号写入日志目录下的 `serve.pid`，并在干净退出时删除该文件。
**写入时机是约定的一部分**：只有在数据库迁移、崩溃恢复与管道服务全部就绪之后才写入，
而在停止时它是最先被释放的资源。因此该文件的语义是"当前确实在提供服务"，
而不是"该进程存在"。正在执行迁移的 Collector 没有 `serve.pid`，
桌面端的接管流程因而不会指向它（评审 2 H-B）。
用 `--pipe` 指定了其他管道名时，写入的文件名为 `serve.<管道名>.pid`，
其中非字母数字且不属于 `.-_` 的字符替换为 `_`；默认的每用户管道仍使用 `serve.pid`。
开发用的第二个实例因此不会覆盖并删除用户实例的进程号（评审 R-10）。

该进程同时创建一个手动重置事件 `Local\<管道名>.stop`。本机任意进程置位该事件的效果
等同于 Ctrl+C，走完全相同的停止路径。没有控制台的桌面端因此可以让 Collector 干净退出，
而不必结束其进程。

**桌面端的接管流程因此也是渐进的**，退出码 6 本身不再结束任何进程：

1. `serve.pid` 已不存在，或其指向本进程、本进程的子进程：直接启动自己的实例，不提示；
2. `serve.pid` 写入不足 15 秒：不做任何处理，该状态正对应实例在执行迁移，仅提示"刚刚启动"；
3. 置位其停止事件并等待 3 秒，期间每 250 ms 检查 `serve.pid` 是否消失、管道是否恢复，
   任一条件成立即结束等待；
4. 只有在等待期满、**并且**桌面端一侧的"连续四次连接失败"计数也已计满时，才依次执行
   `tasklist` → `taskkill`（不带 `/F`）→ 等待 2 秒 → `tasklist` → `taskkill /F`。
   成败一律依据命令的**退出码**判断（0 才是成功），不依据本地化的输出文本。

单实例由两道闸保证：一是 `Local\` 命名空间中的命名事件（按登录会话划分），
二是命名管道本身是否已存在。后者用 `WaitNamedPipe(name, NMPWAIT_NOWAIT)` 询问，
而不是 `File.Exists`：后者会真的连上正在服务的实例。
早先传入的 `0` 实际是 `NMPWAIT_USE_DEFAULT_WAIT`，并非零等待。
同一个用户登录两次时（切换用户，或 RDP 会话与控制台会话并存），第一道闸会各发放一份租约，
两个 Collector 将竞争同一个数据库；第二道闸恰好能回答第一道闸无法回答的问题。

## 3. 进程间通信（IPC）

- 传输：Windows 命名管道，名称 `MentorRecorder.<UserSidHash>.v1`。
  `<UserSidHash>` 是当前用户 SID 的稳定哈希（截断的十六进制），用于同机多用户隔离。
- ACL：仅当前用户（`PipeSecurity`：当前用户 SID 完全控制；不授予 `Everyone`、
  `Authenticated Users`、`NETWORK`）。
  **服务端**使用显式 `PipeSecurity`，**不**附加 `PipeOptions.CurrentUserOnly`：
  .NET 明确拒绝二者同时出现（`NamedPipeServerStreamAcl.Create` 会抛 `ArgumentException`）。
  显式 ACL 是二者中更强的一项，并且可以从进程外审计。
  **客户端**保留 `PipeOptions.CurrentUserOnly`，用于完成 ACL 无法完成的检查：
  校验刚连上的服务端确实属于同一个用户。
- 管道名：`"MentorRecorder." + SHA-256(UTF-8(当前用户 SID 字符串)) 前 16 字节的小写十六进制 + ".v1"`。
  两端各自实现（C# `src/Collector/Ipc/PipeNaming.cs`，C++ `src/Desktop/cpp/PipeName.cpp`），
  必须逐字节一致；`--pipe-name-only` 可打印本机的名字用于核对。
- 分帧：`4 字节小端无符号长度` + `该长度的 UTF-8 JSON`。单帧上限 **4 MiB**
  （`FrameCodec.MaxFrameBytes` == `src/Desktop/cpp/IpcFraming.h` 的 `kMaxFrameBytes`
  == 契约描述文字里的 4194304，三者已对齐）。
- 信封：`request_id`（UUID）、`protocol_version`（= 1）、`message_type`、`payload`。
- 契约：[../contracts/ipc-v1.schema.json](../contracts/ipc-v1.schema.json)（JSON Schema draft 2020-12）
  与 [../contracts/error-codes.md](../contracts/error-codes.md)；
  变更记录见 [../contracts/CHANGELOG.md](../contracts/CHANGELOG.md)。
  契约与实现的一致性由 `tests/Collector.IntegrationTests/Contract*.cs`
  （校验 Collector 发出的每一条应答与事件）和 `tests/Desktop.Tests/IpcRequestTests.cpp`
  （固定 Desktop 发出的每一条请求样本）强制，任何一方发生漂移都会导致测试失败。
- **不存在任何 HTTP / TCP / WebSocket / gRPC 监听端口。** 由静态检查强制。

### 3.1 消息一览（48 个业务消息 + `Event` + `Error`）

规范来源是契约的 `$defs/MessageType` 枚举，本表是该枚举的分组视图。
消息数量同样不在其他位置重复声明：`tests/Collector.IntegrationTests/ContractSchema.cs`
的 `BusinessMessageTypes` 从枚举中读出这 48 个名称（枚举减去 `Event` 与 `Error`），
覆盖断言据此逐条核对。

| 分组 | 消息 |
|---|---|
| 版本与状态 | `GetVersion` `GetStatus` `GetCaptureStatus` `GetProtocolProfileStatus` |
| 抓包控制 | `ListCaptureAdapters` `StartCapture` `StopCapture` `GetCaptureSettings` `UpdateCaptureSettings` |
| 抓包验证 | `StartCaptureValidation` `GetCaptureValidationStatus` `AddCaptureValidationMarker` `StopCaptureValidation` |
| 实时 | `GetCurrentRun` `SubscribeLiveEvents` |
| 查询 | `QueryRuns` `GetRunRevisions` `GetRunEvents` |
| 统计 | `GetDashboardStats` `GetDungeonStats` `GetJobStats` `GetResultStats` |
| 变更 | `CreateManualRun` `CorrectRun` `SoftDeleteRun` `RestoreRun` `UndoRevision` `UpdateAchievementBaseline` |
| 心得 | `SetRunReflection` `GetReflectionSummary` |
| 候选取证 | `QueryCandidateObservations` `ReviewCandidateObservation` `ExportCandidateEvidence` |
| 本机校准 | `ConfirmCalibration` `DiscardCalibration` `GetCalibrationShareCode` |
| 共享校准 | `CheckSharedCalibration` `ImportCalibrationCode` `AcceptSharedQueueInference` `RejectSharedCalibration` |
| 语音播报 | `GetSpeechSettings` `UpdateSpeechSettings` `SynthesizeSpeech` |
| 导出与备份 | `ExportCsv` `ExportJson` `BackupDatabase` `ExportDiagnosticsReport` `CheckDatabaseIntegrity` |

`UndoRevision` 撤销一条记录**最新**的那条修订：追加一条新修订，将 `old_value` 逐字段写回；
被撤销的修订仍保留在修订链上。`revision = 1` 是创建记录本身，
撤销它返回 `ERR_UNDO_NOT_ALLOWED`（[manual-correction.md](manual-correction.md) §1）。

所有变更类消息：
- 返回 `revision` 与 `audit_event_id`；
- 按 `request_id` **幂等**（重放不重复应用，返回 `idempotent_replay = true`）；
- 非法状态返回显式错误码，不静默成功。

### 3.2 应答与事件的实际线格式

成功：`{protocol_version, request_id, message_type, ok: true, payload}`。
失败：`{protocol_version, request_id, message_type, ok: false, error: {code, message, field,
retryable, details?}, payload: <同 error>}`。
同时填写 `error` 与 `payload`，是为了兼容 Desktop 客户端
（`src/Desktop/cpp/IpcClient.cpp`）已实现的两条分支：`ok` 分支读取 `error`，
`message_type == "Error"` 分支读取 `payload`。无法解析出 `message_type` 时，
`message_type` 填 `"Error"`。
契约的 `ResponseEnvelope` / `EventEnvelope` / `ErrorEnvelope` 均已声明 `ok` 与 `error`
（`additionalProperties: false` 保持不变），根节点因此从 `oneOf` 改为 `anyOf`：
`message_type = "Error"` 的信封本就同时满足两种形状。

事件：`{protocol_version, request_id: <订阅请求的 id>, message_type: "Event", ok: true,
payload: <LiveEvent>}`。
`SubscribeLiveEvents` 之后连接保持打开，事件帧与普通请求应答在同一条连接上并行；
一个连接可以既订阅又继续发普通请求，多个客户端可以同时订阅。
每个订阅者拥有独立的有界缓冲，容量 256 条。缓冲满时丢弃最旧的一条，
`sequence` 随之出现空洞；契约已规定空洞意味着客户端应重新查询。
响应缓慢的 UI 因此**永远不会**阻塞写库线程。

**补发缓冲（replay buffer）。** 事件总线另外保留最近 **64** 条已发布事件，
在每个新订阅建立时先按原顺序补发给它，然后才转入实时事件。
这样做的原因是：本进程最重要的几条事件在客户端能够连接之前就已发出。
`CollectorHost.Open` 中的崩溃恢复（把未完结记录置为 `INTERRUPTED` 并标记待复核）
运行在 `PipeServer` 构造之前。在此之前 `LiveEventBus.Publish` 在无订阅者时直接返回，
相应的 `run_updated` 与 `stats_invalidated` 不会送达任何客户端，
桌面端只能通过重新查询获知这些变化。
补发与发布共用同一把锁，因此"补发在前、实时在后"是原子的，既不会遗漏，也不会乱序。

补发的事件**不带任何标记**。`$defs/LiveEvent` 声明了 `additionalProperties: false`，
而 LiveEvent 本身就是事件信封的 `payload`，v1 契约里没有任何合法的位置可以放置
`replayed` 字段，修改契约不在本次范围内。标记也并非必需：`event_id` 在补发前后保持一致，
客户端可以据此识别自己已经见过的事件；`sequence` 跨越补发与实时之间的接缝仍然连续，
不会被误读成空洞。

本实现发出的事件（`kind` 字段）与契约 `event_type` 的对应关系：

| `kind` | `event_type` | 载荷 |
|---|---|---|
| `run_state_changed` | `StateChanged` | `state`；**存在进行中的记录时**另带 `run`（变化之后的那一份） |
| `run_created` | `RunStarted` | `run` |
| `run_updated` | `RunUpdated` | `run` |
| `run_finished` | `RunFinished` | `run`、`state`（终态） |
| `stats_invalidated` | `DiagnosticsMessage` | `severity`、`message` |
| `collector_status` | `CaptureStatusChanged` | `capture`、`severity`、`message` |
| `heartbeat` | `Heartbeat` | 无 |
| `candidate_observed` | `CandidateObserved` | `name`、`group`、`t_ms`、`observation_id`、`capture_session_id` |

`kind` 已是契约字段（`$defs/LiveEvent.kind`，必填），其粒度比 `event_type` 更细。
`stats_invalidated` 在 `event_type` 枚举里没有独立取值，归入 `DiagnosticsMessage`，
由 `kind` 区分。`sequence` 同样为必填。

以下三点需要单独说明：

- `run_state_changed` 附带 `run`，是因为消费该事件的客户端需要播报"进入 {副本名}"，
  并按 `run_id` 去重。仅携带 `state` 时，客户端只能回退到自身缓存的那一份快照；
  国服弹窗发生的时刻尚未取得副本名，播报结果因此始终是"进入 未知副本"。
  `$defs/LiveEvent` 一直允许任何事件携带 `run`。
- `run_finished` 依据**存储行**的 `ended_at_utc` 判定，而不是依据状态机的当前状态。
  "收尾旧记录并在同一事件中开出新记录"在可观察的状态上无法区分，但它是两件事。
- `heartbeat` **不受** `SubscribeLiveEventsRequest.event_types` 过滤器影响，
  其全部意义在于证明该订阅仍然存活。间隔由 `heartbeat_interval_ms`
  （1000–60000，默认 5000）决定，并在订阅应答里原样回显。

上表之外，契约中其余与实现不符的地方也已改为描述现状：
`$defs/Run` 增加 `pending_review` / `note`，`CreateManualRunRequest` 与
`CorrectRunRequest.changes` 增加 `note`，`BackupDatabaseRequest.target_path` 变为可选，
`Responses/BackupDatabase` 增加 `pruned_count`，错误码表增加
`ERR_IDEMPOTENCY_CONFLICT`。逐条见 [../contracts/CHANGELOG.md](../contracts/CHANGELOG.md)。

### 3.3 错误隔离

错误隔离分按连接与按消息两级。分帧错误（超长帧）不可恢复，答复一次后关闭该连接。
其余错误，包括非法 JSON、未知消息类型、业务拒绝与未预期的异常，都只产生一个错误信封，
连接继续服务。未预期的异常返回 `ERR_INTERNAL`，细节只写入本机日志，**不回传给客户端**。

## 4. Collector 内部线程模型

```
  [Npcap capture thread]                    [parser + persistence thread]
  Machina 回调，尽量短                       单线程消费、判定、写库、发事件
        │                                             │
        │  DecodedMessage                             │
        ▼                                             ▼
   ┌──────────────────┐  bounded               ┌───────────────────────┐
   │ capture queue    │──────────────────────► │ parser / state machine│
   │ cap = N, drop-old│                        │ SQLite / live events  │
   └──────────────────┘                        └───────────────────────┘
                                                          │
                                                          ▼
                                                   ┌──────────────┐
                                                   │ IPC server   │  每连接一个读写任务
                                                   │ (named pipe) │  广播 Event 给订阅者
                                                   └──────────────┘
```

规则：

1. **抓包回调线程只负责转存**：把需要的字段复制进队列元素后立即返回，不做解析、不做 I/O、
   不取锁等待。Machina 的回调阻塞会导致丢包。
2. **有界队列（bounded queue）**：容量固定（默认 4096 条）。队列满时按
   *丢弃最旧* 策略丢包，并在 `CaptureStatus.packets_dropped` 中累计，
   通过 `DiagnosticsMessage` 事件上报。**绝不无界增长**，绝不因为背压而阻塞抓包线程。
3. **解析线程单线程**：同一个消费者线程完成协议解析、状态机推进、自动记录写库与
   实时事件发布。抓包顺序因此不会在自动记录路径里被重排，也便于开展确定性重放测试
   （`tools/fixture-replay`）。
4. **自动抓包路径单写入者**：自动记录由上面的消费者线程顺序写入 SQLite。
   `journal_mode = WAL`，`synchronous = FULL`，`busy_timeout` 有限重试后返回 `ERR_DB_BUSY`。
5. **原始负载不落盘**：队列元素在解析后立即释放。诊断日志只记录长度、方向、时间戳、
   opcode 编号等元数据，绝不记录报文正文。详见 [privacy-boundary.md](privacy-boundary.md)。
6. **时间**：所有落库时间为 UTC ISO-8601（毫秒精度）；所有**时长**由单调时钟
   （`Stopwatch` / `QueryPerformanceCounter`）测得，绝不用两个墙钟时间相减，
   以免系统时间调整或夏令时导致负时长。

## 5. 判定流程（概览）

完整规则见 [state-machine.md](state-machine.md)，要点如下：

- 只有在协议档案状态为 `VERIFIED` 时才解析任何字段；否则 **fail-closed**。
- `CONTENT_FINDER_POP` 且 `roulette_id == mentor_roulette_id` 才进入 `MENTOR_MATCHED`。
- 进程重启时未完结的记录一律标记 `INTERRUPTED_PENDING_REVIEW`，**永远不会自动判为 COMPLETED**。

## 6. Fail-closed 策略

本软件的默认行为是"不确定就不记录"。

| 情形 | 行为 |
|---|---|
| 未安装 Npcap | `ERR_NPCAP_MISSING`，显示安装指引，不下载、不内置 |
| 游戏未运行 | `ERR_FFXIV_NOT_RUNNING`，不启动抓包 |
| 客户端版本未知 / 无对应协议档案 | `ProfileStatus = UNSUPPORTED_BUILD`，`ERR_PROFILE_UNSUPPORTED`，**不解析任何报文，不写入任何记录** |
| 档案存在但未经证据验证 | `ProfileStatus = UNVERIFIED`，同样不用于自动记录 |
| 关键字段缺失（如无法确定 `content_id`） | 记录 `detection_confidence` 降级，字段留 `NULL`，不猜测 |
| 队列溢出导致事件丢失 | 该次记录标记为低置信度或 `UNKNOWN`，不补全 |
| 数据库完整性校验失败 | `ERR_DB_INTEGRITY`，采集服务拒绝启动（退出码 3，IPC 管道不会打开），错误信息给出数据库文件位置并提示先复制一份留底 |

**绝不**在没有证据的情况下猜测 opcode 或结构偏移。协议档案的证据要求见
[protocol-profile-format.md](protocol-profile-format.md)。

## 7. 组件与目录

### 7.1 Collector 内部组件

| 组件 | 路径 | 职责 | 关键类型 |
|---|---|---|---|
| Capture | `src/Collector/Capture/` | Npcap 检测（**每次开始监听都重新枚举设备列表**）、适配器枚举、游戏进程定位、Machina 封装（**仅 WinPCap 模式**）、FFXIV 分帧、有界队列、抓包诊断、Oodle 临时副本清单与回收 | `NpcapDetector` `AdapterEnumerator` `GameProcessLocator` `MachinaCaptureSource` `FfxivFraming` `DecodedMessageQueue` `CaptureController` `CaptureDiagnostics` `CaptureCli` `OodleTempCopyCleaner` |
| Protocol / Decoded | `src/Collector/Protocol/Decoded/` | 抓包与解析之间**唯一**的交接类型 | `DecodedMessage` `IDecodedMessageSink` |
| Protocol / Profiles | `src/Collector/Protocol/Profiles/` | 档案加载、Schema 校验、规范化哈希、目录扫描与 `AMBIGUOUS`、按 `region`+`game_build` 选档 | `ProfileLoader` `JsonSchemaValidator` `CanonicalJson` `ProfileValidationReport` `ProfileCatalog` `ProfileSelector` `ProtocolProfile` |
| Protocol / Parsing | `src/Collector/Protocol/Parsing/` | 字节 → 语义事件，**零硬编码常量**；拒绝分类与有界错误环 | `ProfileMessageParser` `ParserError` |
| Domain / Events | `src/Collector/Domain/Events/` | 实时解析和离线重放共用的语义事件、方向和去重键 | `SemanticEvent` 及其子类、`EventKey`、`PacketDirection` |
| Protocol / Pipeline | `src/Collector/Protocol/Pipeline/` | 抓包 → 解析 → 状态机 → 存储 → 实时事件的接线；语义事件到数据库行的**唯一**路径 | `LiveProtocolPipeline` `SemanticEventProcessor` `ICaptureLifecycleListener` |
| Domain | `src/Collector/Domain/` | 状态机与判定规则、领域模型、变更规则、统计模型、单调时钟；变更拒绝只携带类型化业务原因及上下文 | `MentorRunStateMachine` `StateMachineCommands` `ProfileBinding` `BoundedDedupSet` `MentorRun` `RunEvent` `RunRevision` `RunMutationRules` `RunRuleViolationException` |
| Application / Mutations | `src/Collector/Application/Mutations/` | 调用领域规则，将业务拒绝转换成既有错误契约；保留校验优先级及服务的事务位置 | `RunMutationValidation` |
| Contracts / Errors | `src/Collector/Contracts/Errors/` | 错误码和契约异常的唯一实现，由应用服务与适配器使用，IPC 负责序列化 | `ErrorCodes` `CollectorException` |
| Storage | `src/Collector/Storage/` | SQLite 访问、迁移、完整性校验、仓储、append-only 修订写入 | `SqliteDatabase` `MigrationRunner` `Run*Repository` `RunMutationService` |
| Reference | `src/Collector/Reference/` | 副本 / 职业名称映射的加载、版本选择与回退（内嵌资源） | `DutyCatalog` `JobCatalog` |
| Replay | `src/Collector/Replay/` | 两种离线重放：语义事件固件、解码报文固件 | `ReplayFixture` `FixtureReplayRunner` `DecodedFixture` `DecodedReplayRunner` |
| Recovery | `src/Collector/Recovery/` | 启动时扫描未完结记录 → `INTERRUPTED_PENDING_REVIEW` | `CrashRecoveryService` |
| Ipc | `src/Collector/Ipc/` | 命名管道服务端、分帧、信封编解码、消息分发、幂等、实时事件总线 | `PipeServer` `PipeNaming` `FrameCodec` `MessageDispatcher` `LiveEventBus` |
| Capture / Validation | `src/Collector/Capture/` | 显式开启的被动验证会话：脱敏 opcode 级 trace、标记、保留策略（最近 10 次会话 / 7 天）、界面会话 2 小时上限 | `CaptureValidationController` `CaptureTraceRunner` `CaptureTraceSink` |
| Export | `src/Collector/Export/` | CSV / JSON 导出、数据库备份与保留 | `RunExporter` `BackupService` `ExportPaths` |
| Diagnostics | `src/Collector/Diagnostics/` | 结构化轮转日志（**无报文正文**）、速率估计 | `RotatingFileLogger` `ExponentialRateEstimator` |

### 7.2 自动记录的数据流

```
Machina.FFXIV（抓包回调线程，只做搬运）
   │  DecodedMessage
   ▼
DecodedMessageQueue（有界，默认 4096，满则丢最旧并计数；恰好一个消费者线程）
   ▼
LiveProtocolPipeline（会话守卫；档案选择在一次会话内冻结）
   ▼
ProfileMessageParser（档案驱动）──► SemanticEvent
   ▼
SemanticEventProcessor
   ├─► MentorRunStateMachine（state-machine.md 的规则）
   └─► SQLite（mentor_runs / run_events / run_revisions / parser_errors）
   ▼
LiveEventBus（每订阅者 256 条有界缓冲）
   ▼
命名管道 ──► MentorRecorder.Desktop.exe
```

选不到可用档案时，`LiveProtocolPipeline` 退化为**只计数的沉降端**：
不创建状态机、不创建解析器、不写入任何记录。详见
[state-machine.md](state-machine.md) §7。

### 7.3 仓库目录

```
src/Collector/          C# Collector（见 7.1）
src/Desktop/            C++20 / Qt 6 Quick 桌面端（cpp/ + qml/ + resources/）
contracts/              IPC 契约（JSON Schema + 错误码 + CHANGELOG）
protocol-profiles/      协议档案（国服 cn.2026.08.05 为 VERIFIED；global 仍是 unsupported 占位；synthetic 仅供离线测试）
data/duties/            副本名称与分类映射（含生成器产出与合成样例）
data/jobs/              职业名称映射
migrations/             SQLite 迁移脚本
tests/Collector.UnitTests/         C# 单元测试
tests/Collector.IntegrationTests/  真管道 / 真数据库的集成测试
tests/Desktop.Tests/               Qt Test 的桌面端测试
tests/Fixtures/         语义事件固件 / decoded 解码报文固件 / ipc-requests 请求样本
tools/duty-data-generator/         副本数据生成器
tools/fixture-replay/              固件重放说明（重放本身是 Collector 的模式）
tools/protocol-profile-validator/  档案校验器（validate.py）
tools/static-boundary-check/       硬边界静态检查
tools/architecture-boundary-check/ 模块依赖方向检查及正反例自测
docs/                   本目录
scripts/                PowerShell 环境自检、构建、测试、验证、打包
```

> 公开仓库包含测试、工具、合成档案与全部脚本，克隆后可完整构建与验证。
> 不纳入仓库的内容只有设计原型（`DOC/`）和各类日志（见 `.gitignore`）。
> 合成档案与测试材料同样不会进入发行包，`scripts/package.ps1` 对此设有断言。

### 7.4 Desktop 工作流归属

| 组件 | 状态及职责 | 协作边界 |
|---|---|---|
| `HistoryController` | 历史列表/筛选、选中与待复核记录、修订和事件加载、变更请求及一次修订冲突重试 | 通过 `IBackend` 查询和变更；拥有模型与快照，以自身作为异步回调上下文 |
| `StatisticsController` | 仪表盘、趋势窗口与桶、近 7/30 天计数、副本选项、副本/职业统计模型、成就基线请求 | 先采纳统计并发送通知，再通知总控更新相关状态，最后完成回调 |
| `AppController` | 保留 QML 属性/槽/信号投影，协调导航、导出筛选、心得、结果提示和 TTS | 不再保存历史/统计的第二份状态；TTS 上下文在记录结束回调之前更新；监督器由入口提供 |

两个工作流控制器借用后端及其回复对象，可以在没有 QML 引擎、AppController 或本地
Collector 的情况下单独测试。后端销毁时清除模型中的借用引用，回复回调依附控制器的
QObject 生命周期。生产部署仍为 Desktop 与 Collector 两个进程。

`scripts/build.ps1` 与 `scripts/test.ps1` 共用可选环境变量 `MR_BUILD_DIR`，
默认目录为 `build/`，相对路径相对仓库根目录解析。`scripts/verify.ps1` 因而可以在
同一个新目录中完成构建、Collector 部署及 CTest 验证。
架构门禁的反向自测由现有 Python 工具发现流程执行。

## 8. 阶段计划 / Phase plan

| Phase | 名称 | 交付内容 | 完成判据 | 状态 |
|---|---|---|---|---|
| **0** | 仓库与依赖基线 | 目录骨架、`Directory.Build.props`、解决方案、最小 Collector、xunit 冒烟测试、IPC 契约（Schema + 错误码）、全部文档、静态边界检查、PowerShell 脚本、许可证结论 | `dotnet build` / `dotnet test` / 边界检查 / Qt 工具链冒烟编译全部通过 | **已完成** |
| 1 | 存储与桌面壳 | SQLite schema + 迁移、`src/Desktop/CMakeLists.txt` 与 QML 壳、Collector 侧数据访问层、**手工补录与更正、统计、导出与备份、命名管道服务端与全部消息、离线固件重放、崩溃恢复** | 建库、迁移、往返读写有测试；Qt 应用可启动并显示空仪表盘；契约中每个 `message_type` 至少一条正例与一条错误例；固件重放可复现全部终态且重复执行不产生重复数据 | **已完成** |
| 2 | IPC 打通 | 命名管道服务端与 Qt 客户端、信封编解码、幂等表、全部只读消息 | 契约用例（每个 `message_type` 至少一条正例 + 一条错误例）通过 | **已完成** |
| 3 | 手工补录与统计 | `CreateManualRun` / `CorrectRun` / `SoftDeleteRun` / `RestoreRun` / `GetRunRevisions`、统计四件套、导出与备份 | 统计定义的黄金用例通过；修订链 append-only 有测试 | **已完成** |
| 4 | 抓包与判定 | Npcap 检测、Machina 封装（**仅 WinPCap 模式**）、协议档案加载与校验、`--replay-decoded`、live parser/state machine/SQLite/IPC 接线 | 离线固件回放与合成 live 集成测试可复现状态迁移与入库；无真实 opcode 时保持 fail-closed | **已完成**（离线判据；真机部分见 Phase 5） |
| 5 | 打包与联调 | `package.ps1`、发布布局、TTS、真机验证 | 弹窗到离开的流程已在真机上验证。通关报文尚未识别，离开一律以"未知 · 待复核"收尾 | **进行中** |

> 阶段编号沿用本表：**抓包与协议解析是 Phase 4**。
> Phase 4 的"完成"指离线判据（固件重放 + 合成 live 集成测试）全部通过。
>
> **两个状态词只有一个规范来源，本文档不复述取值**：
> `LIVE_CAPTURE_STATUS` 是 `CaptureDiagnosticsSnapshot.LiveCaptureStatus`，
> 由 `MentorRecorder.Collector.exe --capture-doctor --json` 读取；
> `PROTOCOL_PROFILE_STATUS` 写在
> [`../protocol-profiles/README.md`](../protocol-profiles/README.md) 的顶部标记里，
> 由 `scripts/package.ps1` 从该处读入 `BUILD-METADATA.json`。
> 在正文中复述取值曾造成三份互相矛盾的文档，因此不再采用该做法。

## 9. 与本文档一起阅读

- [data-model.md](data-model.md) —— 数据库表结构与字段语义
- [state-machine.md](state-machine.md) —— 状态机与结果判定
- [statistics-definitions.md](statistics-definitions.md) —— 统计口径（固定不可改）
- [manual-correction.md](manual-correction.md) —— 手工补录与更正规则
- [privacy-boundary.md](privacy-boundary.md) —— 硬边界与合规
- [protocol-profile-format.md](protocol-profile-format.md) —— 协议档案格式与证据要求
- [capture-diagnostics.md](capture-diagnostics.md) —— 抓包诊断与故障排查
- [build-and-package.md](build-and-package.md) —— 工具链与构建
- [live-validation-guide.md](live-validation-guide.md) —— 真机验证流程
- [third-party-licenses.md](third-party-licenses.md) —— 第三方许可证与本项目许可证结论
