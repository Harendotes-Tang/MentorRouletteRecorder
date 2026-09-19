# 数据模型 / Data Model

本文档描述本软件所用 SQLite 数据库的表结构、约束、索引与数据保留策略，
面向参与开发、评审与数据核对的读者。
表结构以 `migrations/` 下的迁移脚本为准，本文档与其保持一致。

存储引擎：**SQLite**（通过 `Microsoft.Data.Sqlite`）。
文件位置：`%LOCALAPPDATA%\MentorRecorder\mentor_recorder.db`。
数据库文件可由 `--db <path>` 单独指定，也可由环境变量 `MR_DATA_DIR` 连同日志、备份、
默认导出目录一并迁移（见 [architecture.md](architecture.md) §2.3）。
只有 `MentorRecorder.Collector.exe` 打开写连接，Desktop 端**永不**直接访问数据库。

连接参数：`journal_mode = WAL`、`synchronous = FULL`、`foreign_keys = ON`、
`busy_timeout = 5000`（超时后返回 `ERR_DB_BUSY`）。
**例外**：实时抓包写入把**两项**预算一起降到 1 秒，分别是 SQLite 自身的 `busy_timeout`，
以及驱动层的命令超时（`SqliteConnection.DefaultTimeout`，事务的 `BEGIN IMMEDIATE`
同样走它）。只降低 `busy_timeout` 无效：SQLite 的忙等待到期返回 BUSY 之后，
Microsoft.Data.Sqlite 仍会自行重试至命令超时（默认 5 秒）为止，3 次尝试合计接近 15 秒。
两项一并降低之后，3 次重试合计约 3 秒。数据库被外部占用时，`GetCurrentRun` 一类 IPC 请求
不会被解析线程阻塞十余秒。人工变更仍使用 5000 ms。

## 0. 通用约定

| 约定 | 规则 |
|---|---|
| 时间 | 一律 **UTC ISO-8601 带毫秒**，形如 `2026-09-04T11:22:33.456Z`，以 `TEXT` 存储。绝不存本地时间，绝不存不带毫秒的形式。 |
| 时长 | `duration_ms` 为整数毫秒，来自**单调时钟**（`Stopwatch`）。绝不由两个墙钟时间戳相减得出。手工补录时若只给出起止时间，则由它们计算，并要求结果 `>= 0`。 |
| 主键 | 一律 UUID v4 的 `TEXT`（小写带连字符）。 |
| 布尔 | `INTEGER`，取值 0 或 1，并加 `CHECK` 约束。 |
| 删除 | 业务记录**只做软删除**（`soft_deleted = 1`）。修订表 `run_revisions` **只追加**，永不 UPDATE / DELETE。 |
| 未知 | 未知即 `NULL`，绝不填占位数值。展示层把 `job_id IS NULL` 显示为 `未知`（自成一类）。 |

## 1. `mentor_runs` —— 一次导随尝试

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `run_id` | TEXT | PK | UUID |
| `revision` | INTEGER | NOT NULL, `>= 1` | 乐观并发版本号；每次成功变更 +1 |
| `capture_session_id` | TEXT | NULL, FK → `capture_sessions.capture_session_id` | 手工补录为 NULL |
| `region` | TEXT | NOT NULL, IN (`CN`,`GLOBAL`,`UNKNOWN`) | 区服 |
| `game_build` | TEXT | NULL | 识别到的客户端版本标识 |
| `protocol_profile_id` | TEXT | NULL | 产生该记录所用的协议档案 id；手工补录为 NULL |
| `mentor_roulette_id` | INTEGER | NULL | 判定为导随所依据的 roulette id |
| `content_id` | INTEGER | NULL | 副本 content id；**仅在报文中确实携带该字段时才写入**，绝不由 `territory_id` 反推 |
| `territory_id` | INTEGER | NULL | 地图/区域 id；进本标记不带区域时由 `ZONE_TERRITORY` 播报补齐（[state-machine.md](state-machine.md) §3.11） |
| `duty_source` | TEXT | NULL, IN (`CONTENT_ID`,`TERRITORY`,`MANUAL`) | 副本身份的来源（schema v8 新增，见 §1.4） |
| `duty_name` | TEXT | NULL | 副本名称（本地化，来自 `data/duties/`）；可由 `content_id` 或 `territory_id` 反查，两者均只填补空值，不覆盖已有值 |
| `duty_category` | TEXT | NULL | 副本分类（如 迷宫挑战 / 讨伐战 / 大型任务） |
| `job_id` | INTEGER | NULL | 职业 id |
| `job_name` | TEXT | NULL | 职业名称；`job_id IS NULL` 时为 `未知` |
| `role` | TEXT | NOT NULL, IN (`TANK`,`HEALER`,`DPS`,`UNKNOWN`) | 由 `job_id` 推导 |
| `matched_at_utc` | TEXT | NULL | 匹配弹出时间 |
| `entered_at_utc` | TEXT | NULL | 进入副本时间。**是否计入 attempt 的判据** |
| `ended_at_utc` | TEXT | NULL | 结束时间 |
| `duration_ms` | INTEGER | NULL, `>= 0` | 单调时钟测得的时长 |
| `result` | TEXT | NOT NULL, IN (`COMPLETED`,`LEFT_OR_ABANDONED`,`CANCELLED_BEFORE_ENTRY`,`DISCONNECTED`,`INTERRUPTED`,`UNKNOWN`) | 结果 |
| `detection_confidence` | TEXT | NOT NULL, IN (`HIGH`,`MEDIUM`,`LOW`,`NONE`) | 自动判定置信度；手工记录为 `NONE` |
| `source` | TEXT | NOT NULL, IN (`AUTO_NETWORK`,`MANUAL`,`IMPORT`) | 数据来源 |
| `contributes_to_goal` | INTEGER | NOT NULL, 0/1, default 1 | 是否计入成就进度 |
| `manually_created` | INTEGER | NOT NULL, 0/1 | 是否为手工创建 |
| `manually_corrected` | INTEGER | NOT NULL, 0/1 | 是否被人工**更正**过：某次 `CorrectRun` 改动了软件已记下的内容后置 1，且不再回退。仅回答待复核记录的结局（`result` / `pending_review` / `contributes_to_goal`）、补上原本为空的职业或副本、或编辑备注，不算更正（`RunMutationRules.OverrulesTheRecord`）。1.3.1 之前每次 `CorrectRun` 都置 1；旧数据在采集服务启动时按各自的修订链重新判定（`CorrectedFlagMaintenance`，不产生修订、不改 `updated_at_utc`） |
| `soft_deleted` | INTEGER | NOT NULL, 0/1, default 0 | 软删除标记 |
| `pending_review` | INTEGER | NOT NULL, 0/1, default 0 | 崩溃恢复标记「待复核」（schema v2 新增） |
| `note` | TEXT | NULL | 用户手工填写的备注（schema v2 新增） |
| `created_at_utc` | TEXT | NOT NULL | 创建时间 |
| `updated_at_utc` | TEXT | NOT NULL | 最近变更时间 |

约束与索引：

```
CHECK (matched_at_utc IS NULL OR entered_at_utc IS NULL OR matched_at_utc <= entered_at_utc)
CHECK (entered_at_utc IS NULL OR ended_at_utc IS NULL OR entered_at_utc <= ended_at_utc)
CHECK (duration_ms IS NULL OR duration_ms >= 0)

INDEX ix_runs_entered      ON mentor_runs(entered_at_utc)
INDEX ix_runs_result       ON mentor_runs(result)
INDEX ix_runs_content      ON mentor_runs(content_id)
INDEX ix_runs_job          ON mentor_runs(job_id)
INDEX ix_runs_live         ON mentor_runs(soft_deleted, entered_at_utc)
INDEX ix_runs_session      ON mentor_runs(capture_session_id)
INDEX ix_runs_pending_review ON mentor_runs(pending_review) WHERE pending_review = 1
```

违反时间顺序 → `ERR_TIME_ORDER`；`duration_ms < 0` → `ERR_NEGATIVE_DURATION`。

### 1.1 `pending_review` 与 `note`（schema v2）

两列由 `migrations/0002_pending_review_and_note.sql` 追加，`0001_initial.sql` 永不修改
（其校验和是识别篡改的依据，见 [migrations/README.md](../migrations/README.md)）。

- `pending_review = 1` 表示这条记录的结果尚未经人工确认：由**崩溃恢复**置为 `INTERRUPTED`
  （见 [state-machine.md](state-machine.md) §3.9），或由没有 `DUTY_RESULT` 的档案在离开副本时
  置为 `UNKNOWN`（§3.10）。它是
  `GetDashboardStats.unfinished_pending_review` 的唯一依据。此前该口径由
  `result = INTERRUPTED AND detection_confidence = LOW AND manually_corrected = 0` 推断，
  现已改为直接读取该列，语义不再依赖置信度的巧合。
- Collector **永远不会**自行清除该标记。用户通过 `CorrectRun` 提交结果或显式设置
  `pending_review=false`，以及 `SoftDeleteRun`（软删除该记录），会将其置 0。
  仅更正备注等其他字段时，待复核标记保留；人工字段保护读取 `run_revisions`，
  不以历史标记冻结整行。
- `note` 是用户在更正对话框中手工填写的本地备注文本，永远不来自抓包内容，也不离开本机。

迁移 0002 会把历史上符合旧口径的记录回填为 `pending_review = 1`，因此升级前后
"待复核"的条数一致。

### 1.2 契约中的对应字段

`contracts/ipc-v1.schema.json` 的 `$defs/Run` 是 `additionalProperties: false`，
而本实现在 `Run` 载荷中输出 `pending_review` 与 `note`。
契约**已经**声明了这两个属性（连同 `CreateManualRunRequest` 与
`CorrectRunRequest.changes` 里的 `note`），逐条记录见
[`../contracts/CHANGELOG.md`](../contracts/CHANGELOG.md)。

### 1.3 `run_reflections` —— 导随心得（schema v3）

由 `migrations/0003_run_reflections.sql` 追加，与 `mentor_runs` 是 **1 : 0..1**。

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `run_id` | TEXT | PK，FK → `mentor_runs(run_id)` ON DELETE CASCADE | 一条记录最多一条心得 |
| `mood` | TEXT | NOT NULL，取 `good` / `ok` / `bad` | 心情；界面文案 顺利 / 一般 / 糟心 |
| `text` | TEXT | NOT NULL，`length BETWEEN 1 AND 2000` | 用户手写的正文，已 trim |
| `created_at_utc` | TEXT | NOT NULL | UTC ISO-8601（毫秒） |
| `updated_at_utc` | TEXT | NOT NULL | UTC ISO-8601（毫秒） |

`INDEX ix_run_reflections_updated ON run_reflections(updated_at_utc DESC)`。

不将其实现为 `mentor_runs` 上的两列，原因是**心得由用户自行撰写，不是抓包得到的事实**。
因此写入心得

- **不**递增 `mentor_runs.revision`，**不**追加 `run_revisions`，也**不需要** `reason`，
  因为没有任何"被更正的观测值"需要留痕；
- 允许写在**软删除**的记录上，因为用户记述的是当时发生过的事，而不是在编辑一条已丢弃的记录；
- `text` 经 `Trim()` 后为空即表示**删除**这条心得（`SetRunReflection` 的一条消息同时承担写入与清空）。

按 `request_id` 幂等，与其它变更消息共用 `ipc_idempotency` 表与同一套指纹规则
（见 §7.1）。写入成功后 Collector 发布 `stats_invalidated`（`message = "reflection_changed"`）
与 `run_updated` 两个实时事件。

`$defs/Run` 的 `reflection` 属性由该表填充。**每一处**序列化 Run 的位置都会带上它，
没有心得时取值为 `null`。填充发生在 `RunRepository` 读取行的位置，而不是五处序列化点上。

### 1.4 `duty_source` —— 副本身份的来源（schema v8）

由 `migrations/0008_run_duty_source.sql` 追加一列，不改动 0001–0007 及其校验和。

| 取值 | 含义 |
|---|---|
| `CONTENT_ID` | 报文里带了明确的 `content_id`（弹窗或进本标记） |
| `TERRITORY` | 由观察到的 `territory_id` 经本地副本表反查而来 |
| `MANUAL` | 人工新增或人工更正填的 |
| `NULL` | 这条记录还没有任何副本身份，或者是 0008 之前写入的历史行 |

引入该列的原因是：`ZONE_TERRITORY` 的字段偏移尚未在副本内负载上核实。此前
`SetDuty` 会把由 `territory_id` 反查出的 `content_id` 写入 `content_id` 列，
一个错误的偏移足以污染副本统计。现在**自动写入只落 `territory_id` 与显示用的
名称、分类**，`content_id` 保持 `NULL`，该列用于说明这条记录的副本身份具有何种可信程度。

副本统计因此改为按 `content_id ?? territory_id` 聚合。两者位于各自的键空间，
一个 territory id 不会与一个 content id 冲突。只按区域识别出来的那一组在
IPC 上仍然回报 `content_id: null`，名称取自本地副本表。见
[statistics-definitions.md](statistics-definitions.md)。

**该列不进入 IPC。** `$defs/Run` 未变，`MentorRun.DutySource` 带 `[JsonIgnore]`，
界面、实时事件与导出中均不包含该列。它只是本机数据库中供维护者排查偏移的来源标注，
也不单独产生一条 revision。

## 2. `run_events` —— 一次尝试内的事件轨迹

该表用于解释判定结果的成因，同时用于诊断。**只存元数据，不存报文正文。**

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `event_id` | TEXT | PK | UUID |
| `run_id` | TEXT | NOT NULL, FK → `mentor_runs` ON DELETE RESTRICT | 所属记录 |
| `sequence` | INTEGER | NOT NULL, `>= 0` | 记录内单调递增 |
| `occurred_at_utc` | TEXT | NOT NULL | 事件时间 |
| `monotonic_offset_ms` | INTEGER | NOT NULL, `>= 0` | 相对该记录起点的单调时钟偏移 |
| `event_type` | TEXT | NOT NULL | 如 `CONTENT_FINDER_POP` / `ZONE_INITIALIZATION` / `DUTY_RESULT` / `PROCESS_RESTART` |
| `from_state` | TEXT | NULL | 迁移前状态 |
| `to_state` | TEXT | NULL | 迁移后状态 |
| `confidence` | TEXT | NOT NULL | `HIGH` / `MEDIUM` / `LOW` / `NONE` |
| `detail_json` | TEXT | NULL | 结构化元数据（**不含负载**，见下） |
| `event_key` | TEXT | NULL | 去重键；非空时在表内唯一 |

`detail_json` 允许的内容：opcode 编号、方向、报文长度、被解析出的 id 型字段
（`content_id`、`territory_id`、`roulette_id`、`job_id`）、档案 id。
**禁止**：原始字节、十六进制转储、字符串聊天内容、角色名、任何其他玩家的信息。

```
UNIQUE (run_id, sequence)
INDEX ix_events_run ON run_events(run_id, sequence)
UNIQUE INDEX ux_events_key ON run_events(event_key) WHERE event_key IS NOT NULL
```

## 3. `run_revisions` —— 只追加的审计表

每次变更写一行；**永不 UPDATE，永不 DELETE**（含软删除与恢复本身）。

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `revision_id` | TEXT | PK | UUID，即 IPC 返回的 `audit_event_id` |
| `run_id` | TEXT | NOT NULL, FK → `mentor_runs` | |
| `revision` | INTEGER | NOT NULL, `>= 1` | 本次变更后的 `mentor_runs.revision` |
| `changed_at_utc` | TEXT | NOT NULL | |
| `change_kind` | TEXT | NOT NULL, IN (`CREATE_AUTO`,`CREATE_MANUAL`,`CORRECT`,`SOFT_DELETE`,`RESTORE`,`IMPORT`) | |
| `actor` | TEXT | NOT NULL, IN (`USER`,`SYSTEM`) | |
| `reason` | TEXT | NULL | `CREATE_MANUAL` / `CORRECT` / `SOFT_DELETE` / `RESTORE` 必填，去除首尾空白后长度为 1–500，否则 `ERR_REASON_REQUIRED` |
| `request_id` | TEXT | NULL | 触发本次变更的 IPC `request_id`，用于幂等 |
| `changes_json` | TEXT | NOT NULL | `[{"field":…,"old_value":…,"new_value":…}]`；`CREATE_*` 为全量初值 |

```
UNIQUE (run_id, revision)
UNIQUE (request_id)                 -- 幂等：同一 request_id 只能产生一次变更
INDEX ix_revisions_run ON run_revisions(run_id, revision)
```

`revision = 1` 一定是 `CREATE_AUTO` / `CREATE_MANUAL` / `IMPORT`。

## 4. `achievement_settings` —— 成就目标与基线

单行表（`id = 1`），用于用户从中途开始使用本软件的场景。

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `id` | INTEGER | PK, CHECK (`id = 1`) | 单行 |
| `goal_count` | INTEGER | NOT NULL, `>= 1`, **default 2000** | 目标次数 |
| `baseline_completed_count` | INTEGER | NOT NULL, `>= 0`, default 0 | 开始记录前已完成的次数（用户自报） |
| `baseline_effective_at` | TEXT | NOT NULL | 基线生效时间（UTC）。早于该时刻的记录不重复计入进度 |
| `updated_at_utc` | TEXT | NOT NULL | |

基线的每次修改都必须带 `reason`，并写入 `application_settings` 的审计或独立审计行；
`UpdateAchievementBaseline` 返回 `audit_event_id`。

## 5. `schema_migrations` —— 迁移记录

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `version` | INTEGER | PK | 迁移版本号，单调递增 |
| `name` | TEXT | NOT NULL | 迁移文件名 |
| `checksum` | TEXT | NOT NULL | 迁移脚本内容的 SHA-256，检测被篡改的历史迁移 |
| `applied_at_utc` | TEXT | NOT NULL | |

启动时向前迁移到本程序支持的最高版本（当前为 **8**，`0008_run_duty_source.sql`；
逐版说明见 [migrations/README.md](../migrations/README.md)）。若库中版本**高于**本程序支持的版本，
或历史迁移 checksum 不匹配，则返回 `ERR_DB_INTEGRITY` 并进入只读模式。

## 6. `application_settings` —— 键值设置

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `key` | TEXT | PK | 如 `ui.language`、`tts.enabled`、`capture.adapter_id` |
| `value_json` | TEXT | NOT NULL | JSON 编码的值 |
| `updated_at_utc` | TEXT | NOT NULL | |

默认值：`ui.language = "zh-Hans"`、`tts.enabled = false`、`capture.follow_game = true`（未写入时视为开启；
旧键 `capture.autostart` 仍被种子为 `false`，只有显式取值 `true` 才被视为用户意图）。
**此表不存放任何凭据、令牌或个人身份信息。**

## 7. `capture_sessions` —— 一次抓包会话

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `capture_session_id` | TEXT | PK | UUID |
| `started_at_utc` | TEXT | NOT NULL | |
| `ended_at_utc` | TEXT | NULL | 非空表示正常结束；进程崩溃后重启时若仍为 NULL，则其未完结记录转 `INTERRUPTED_PENDING_REVIEW` |
| `collector_version` | TEXT | NOT NULL | |
| `region` | TEXT | NOT NULL | |
| `game_build` | TEXT | NULL | |
| `protocol_profile_id` | TEXT | NULL | |
| `profile_status` | TEXT | NOT NULL | `NONE` / `UNVERIFIED` / `VERIFIED` / `UNSUPPORTED_BUILD` |
| `adapter_id` | TEXT | NULL | Npcap 设备名 |
| `packets_observed` | INTEGER | NOT NULL, default 0 | |
| `packets_dropped` | INTEGER | NOT NULL, default 0 | 丢失的消息数：有界队列溢出 + 收尾超时放弃的积压（`LostCount`）。两者在诊断日志中分开记录（`dropped` / `abandoned`），只有前者会把进行中的记录降级为"中断" |
| `end_reason` | TEXT | NULL | `USER_STOP` / `PROCESS_EXIT` / `ERROR` / `UNKNOWN` |

**不存储**适配器的 MAC 地址、公网 IP、其他主机地址等可用于识别网络环境的信息；
`adapter_id` 视为不透明标识。

## 7.1 `ipc_idempotency` —— 变更消息的幂等表

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `request_id` | TEXT | PK | 客户端信封里的 `request_id` |
| `message_type` | TEXT | NOT NULL | 首次应用时的消息类型 |
| `response_json` | TEXT | NOT NULL | 首次成功时的变更快照（含请求内容的**指纹**），用于原样重放 |
| `created_at_utc` | TEXT | NOT NULL | UTC ISO-8601（毫秒） |

`INDEX ix_idempotency_created ON ipc_idempotency(created_at_utc)`。

同一 `request_id` 再次到达且请求指纹**相同**时，不重新应用变更，直接回放已保存的结果，
并带上 `idempotent_replay = true`。
指纹**不同**时（同一个 `request_id` 被复用于另一份内容）返回
`ERR_IDEMPOTENCY_CONFLICT`。回放旧结果会使客户端误认为其**新**改动已经生效，
拒绝是唯一安全的处理方式。
该表只存**结果快照**与指纹，不存请求原文。

**保留期是 24 小时。** 每次写入后按 `created_at_utc` 裁去更早的行
（`IdempotencyRepository.RetentionWindow`）。因此"同一 `request_id` 原样重放"的保证
只在 24 小时内成立：跨进程重启仍然有效，跨天则不再成立。

超过保留期之后再重放同一个 `request_id` 时，幂等行已不存在，但
`run_revisions.request_id` / `candidate_reviews.request_id` 的 UNIQUE 约束仍记录该请求已执行。
此时**不会**再次应用变更，也不会笼统地报告内部错误，而是返回
`ERR_IDEMPOTENCY_CONFLICT`，`details = {conflict: "idempotency", request_id, reason: "RESPONSE_EXPIRED"}`、
`field = "request_id"`，提示调用方换用新的 `request_id` 重发。

## 7.2 `parser_errors` —— 有界的解析拒绝诊断

| 列 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `error_id` | TEXT | PK | UUID |
| `occurred_at_utc` | TEXT | NOT NULL | UTC ISO-8601（毫秒） |
| `capture_session_id` | TEXT | NULL | 所属抓包会话；离线重放时是固件的合成会话 |
| `kind` | TEXT | NOT NULL | 拒绝分类：`E_UNKNOWN_OPCODE` / `E_LEN_MISMATCH` / `E_OFFSET_OOB` / `E_FIELD_CONSTRAINT` / `E_PROFILE_UNSUPPORTED` / `E_INTERNAL`（见 [protocol-profile-format.md](protocol-profile-format.md) §9） |
| `detail` | TEXT | NULL | **非敏感**说明，例如 `ZONE_INITIALIZATION: payload length 12 violates the declared length rule` |

`INDEX ix_parser_errors_time ON parser_errors(occurred_at_utc)`。

**该表中绝不会出现报文正文、角色名或地址。** `detail` 只由解析器自行拼出的
"哪条消息的哪个字段违反了哪条声明的规则"构成，不含从报文中读出的字节。

该表是**有界**的：`ParserErrorRepository` 每次写入后把它裁到最新的 1000 行，
因此一份错误的档案不会使数据库无限增长。
写入失败不会打断解析循环；丢失的诊断行不再补写，计数仍保留在 `IParserStats` 中。

## 8. 数据保留

- 原始报文：默认不落盘，解析后立即释放；仅显式研究模式允许下述候选负载例外（见 [privacy-boundary.md](privacy-boundary.md) §5.1）。
- `run_events`：随记录长期保留（仅元数据）。
- 诊断日志：按大小滚动，单文件上限 **2 MiB**、最多 **5** 个文件，另按 **7 天**清理，不含报文正文。
- 幂等结果快照：**24 小时**（§7.1）。
- 数据库备份：桌面端的"每日自动备份"**默认开启**，在每天第一次连上 Collector 之后执行一次，
  写入 `<数据库目录>\backups\`（默认 `%LOCALAPPDATA%\MentorRecorder\backups\`），
  保留最近 **14** 份，超出部分按时间删除；同时清理该目录中遗留超过 1 小时的
  `.mentor-export-*.tmp` 暂存文件。用户显式请求的 `BackupDatabase` 可写入自选路径，
  **本软件一律不清理该目录**。备份是数据库的完整副本，因此可能包含 §8.1 的研究负载，
  不是脱敏产物（见 [privacy-boundary.md](privacy-boundary.md) §8.1）。

### 8.1 候选研究负载

`candidate_observations.payload_hex` 是独立的可空研究列。schema 7 允许最多 512 字节
（1024 个小写 ASCII 十六进制字符），字符数必须与 `payload_length` 一致；超限不截断，
只保留观测元数据。默认关闭，写入前必须同时满足候选验证已开启和明确的合法白名单。
`ZONE_LOAD` 锚点及窗口聚合样本不带负载。普通 IPC 查询、实时事件、日志和诊断不返回该列；
专用证据导出保留正文并如实标记 `contains_raw_payload`，关闭研究后旧证据仍然保留。

0007 在一个事务内升级既有 256 字节约束，不修改旧迁移 checksum，不重建观测父表，保留
核对外键与只追加历史。观测保留上限为 20000 条 / 30 天；删除到期观测时清理关联核对。
数据库备份可能包含这些原始负载，不能视为脱敏诊断文件。

## 9. 与 IPC 的映射

`contracts/ipc-v1.schema.json` 中的 `$defs/Run` 与本表一一对应，
差别仅在于：布尔以 JSON `true/false` 表示；`NULL` 以 JSON `null` 表示；
IPC 不暴露 `run_events.detail_json` 的原始内容，只暴露归纳后的字段；
以及 `$defs/Run.reflection` 来自 `run_reflections`（§1.3）而不是 `mentor_runs` 的列。

导出同样包含心得：JSON 导出的每个 run 对象含 `reflection`（对象或 `null`），
CSV 在原有 14 列**之后**追加 `reflection_mood`、`reflection_text` 两列（无心得时为空），
共 16 列。列顺序只增不改，见 [manual-correction.md](manual-correction.md) §10.1。

CSV 的 `date` 列是**UTC 日历日**（取 `entered_at` / `matched_at` / `ended_at` 里第一个非空的
那一个的 UTC 日期）。同一行的 `matched_at` / `entered_at` / `ended_at` 是完整的 UTC ISO-8601
时间戳，本身没有歧义。但对 UTC+8 的用户而言，当地 22:00 进行的一场记录，
其 `date` 会落在**前一天**。
`duty_source`（§1.4）不在导出里。
