# 手工补录与更正 / Manual Entry and Correction

本文档说明手工补录与更正的数据模型、写操作、校验规则与界面要求，面向桌面端与采集服务的开发者及维护者。
文中出现的消息名、字段名与错误码均与 IPC 契约一致。

自动记录存在失效的场景：游戏版本更新、Npcap 缺失、用户中途才开始使用本软件、网络掉线。
因此，手工补录与更正是本软件的**一等功能**，而非事后补丁。
核心原则是：**所有变更均为追加式，历史永不消失。**

修正已有记录时，仅凭区域识别得到的副本，以及不在当前副本表中的副本，均保留原有名称；
只修改结果、职业或时间不会清空副本信息。主动匹配新的副本编号时，采集服务同步采用副本表中的名称与种类，
请求中明确填写的名称与种类优先。

结束弹窗在记录缺少职业时提供可选的职业补录，该补录与用户确认的结果写入同一条修订；
记录已有职业时不再询问。用户选择稍后补录时，职业保持未知。

## 1. 四类写操作

| 操作 | 消息 | 效果 |
|---|---|---|
| 手工新建 | `CreateManualRun` | 新建一条 `source = MANUAL`、`manually_created = 1` 的记录 |
| 更正 | `CorrectRun` | 修改已有记录的字段，`revision + 1`，置 `manually_corrected = 1` |
| 软删除 / 恢复 | `SoftDeleteRun` / `RestoreRun` | 翻转 `soft_deleted`，`revision + 1` |
| 撤销 | `UndoRevision` | 追加一条新修订，把上一条修订的 `old_value` 逐字段写回；**被撤销的那条修订依然留在链上** |

`UndoRevision` 是一条正式的 IPC 消息。请求体为 `{run_id, expected_revision, reason}`，其中 `reason` 必填；
应答与其他变更消息一致，包含 `revision`、`audit_event_id` 与幂等标记。

`UndoRevision` 只能撤销**当前最新**的那条修订，且不能撤销 `revision = 1`，即创建修订本身。
撤销创建修订时返回 `ERR_UNDO_NOT_ALLOWED`，其中 `field = "expected_revision"`，
`details.run_id` 给出记录标识。需要移除记录时应使用软删除。
该错误码与 `ERR_BAD_REQUEST` 分开，用于让客户端区分请求格式错误与该修订按定义不可撤销两种情形。

**没有硬删除。** 数据库中不存在任何 `DELETE FROM mentor_runs` 的代码路径，
`run_revisions` 表只允许 `INSERT`。

## 2. 修订链（append-only revisions）

每条记录的历史是一条修订链：

```
mentor_runs.revision = 1  ──►  run_revisions(revision=1, change_kind=CREATE_AUTO|CREATE_MANUAL|IMPORT)
                     = 2  ──►  run_revisions(revision=2, change_kind=CORRECT,     reason="…")
                     = 3  ──►  run_revisions(revision=3, change_kind=SOFT_DELETE, reason="…")
                     = 4  ──►  run_revisions(revision=4, change_kind=RESTORE,     reason="…")
```

以下不变量由测试强制保证：

1. `run_revisions` 中每个 `(run_id, revision)` 唯一。
2. `revision` 从 1 开始，连续递增，无空洞。
3. `revision = 1` 的 `change_kind` 必属于 `CREATE_AUTO` / `CREATE_MANUAL` / `IMPORT`。
4. `mentor_runs.revision` 恒等于该记录 `run_revisions` 中最大的 `revision`。
5. 已写入的修订行永不被 `UPDATE` 或 `DELETE`。
6. `changes_json` 完整记录每个被改字段的 `old_value` 与 `new_value`；
   `CREATE_*` 记录全量初值。

`GetRunRevisions` 按 `revision` 升序分页返回整条修订链，桌面端以时间线形式呈现。

## 3. 乐观并发：`expected_revision`

`CorrectRun`、`SoftDeleteRun` 与 `RestoreRun` 均必须携带 `expected_revision`。

- 若 `expected_revision` 与 `mentor_runs.revision` 不相等，返回 `ERR_REVISION_CONFLICT`，
  并在 `details` 中给出 `current_revision`。
- 客户端应重新拉取该记录，将差异展示给用户，由用户决定是否以新的 `request_id` 重试。
- 后写的请求**绝不覆盖**先写的结果。

## 4. 幂等：`request_id`

- 每个变更请求都携带一个由客户端生成的 UUID `request_id`。
- `run_revisions.request_id` 上有 `UNIQUE` 约束。
- 收到已经成功应用过的 `request_id` 时，采集服务**不再次应用**该请求，
  而是原样返回上一次的结果，并置 `idempotent_replay = true`。
- 因此，请求发出后连接断开、客户端重发是安全的，不会产生重复记录或重复修订。
- 幂等表随 `run_revisions` 一起持久化，跨进程重启依然有效。
- 幂等行同时保存请求内容的指纹（SHA-256）。若同一个 `request_id` 被用于**内容不同**的请求，
  采集服务拒绝执行，返回 `ERR_IDEMPOTENCY_CONFLICT` 并保留 `details.conflict = "idempotency"`；
  若此时返回上一次的结果，客户端会误认为新的改动已经生效。

**可重放的窗口是 24 小时。** 幂等表按 `created_at_utc` 清理，清理规则见
[data-model.md](data-model.md) §7.1。因此，原样重放的保证跨进程重启成立，跨天不成立。
超过 24 小时之后重发同一个 `request_id` 时，采集服务仍可凭 `run_revisions.request_id`
与 `candidate_reviews.request_id` 的 `UNIQUE` 约束识别出该请求已经执行过，但无法再取出当时的应答。
此时采集服务既不重复应用变更，也不返回笼统的内部错误，而是返回 `ERR_IDEMPOTENCY_CONFLICT`，
并附 `details.reason = "RESPONSE_EXPIRED"` 与 `field = "request_id"`。
客户端应换用新的 `request_id` 重发。

## 5. 理由 `reason`

以下操作必须携带非空 `reason`，长度为 1–500 字符；缺失时返回 `ERR_REASON_REQUIRED`：

- `CreateManualRun`（说明补录的原因，例如“7 月 3 日掉线未记录”）
- `CorrectRun`
- `SoftDeleteRun`
- `RestoreRun`
- `UpdateAchievementBaseline`

理由写入修订链，并在桌面端的历史时间线中展示，使用户在很久之后仍能了解某条记录当初被更正的原因。

## 6. 校验规则

`CreateManualRun` 与 `CorrectRun` 的字段校验以**变更后的最终值**为对象，
而不仅校验请求中出现的字段：

| 规则 | 违反时的错误码 |
|---|---|
| `matched_at_utc <= entered_at_utc`（两者均非空时） | `ERR_TIME_ORDER` |
| `entered_at_utc <= ended_at_utc`（两者均非空时） | `ERR_TIME_ORDER` |
| `duration_ms >= 0` | `ERR_NEGATIVE_DURATION` |
| 时间戳为带毫秒的 UTC ISO-8601 | `ERR_BAD_REQUEST` |
| `result` 属于六个合法取值 | `ERR_BAD_REQUEST` |
| `changes` 非空且至少有一个字段的新值不同于当前值 | `ERR_NO_CHANGES` |
| 记录存在 | `ERR_NOT_FOUND` |
| 同一 `request_id` 只用于内容相同的请求 | `ERR_IDEMPOTENCY_CONFLICT` |
| 软删除操作的目标当前未被删除 | `ERR_ALREADY_DELETED` |
| 恢复操作的目标当前处于删除态 | `ERR_NOT_DELETED` |

补充规则：

- 新建时若提供了 `entered_at_utc` 与 `ended_at_utc` 而未提供 `duration_ms`，
  或更正时实际改变了任一端点，采集服务按 `ended - entered` 计算时长，并要求结果 `>= 0`。
  手工记录没有单调时钟可用，这是推算时长的唯一场景。计算得出的时长在 `changes_json` 中如实记录。
- 若三者同时提供且互相矛盾，以显式的 `duration_ms` 为准，不做静默调整。
- 新建或更正时显式传入 `duration_ms: null` 表示耗时未知，该记录不参与推算，也不计入平均耗时。
  桌面端再次编辑耗时未知的记录时，只修改结束时间仍保留空耗时；
  只有将原进本时间替换为实际时间之后，才重新计算时长。
- 手工创建的记录取 `detection_confidence = NONE`、`source = MANUAL`、
  `capture_session_id = NULL`、`protocol_profile_id = NULL`。

## 7. 可更正的字段

`content_id`、`duty_name`、`duty_category`、`job_id`、
`matched_at_utc`、`entered_at_utc`、`ended_at_utc`、`duration_ms`、
`result`、`contributes_to_goal`、`note`、`pending_review`。

`pending_review` 只接受 `false`，表示确认复核。仅包含该字段的更正是合法的：
确认复核本身即是一次人工决定，与其他更正一样写入修订记录。
记录不处于待复核状态时返回 `ERR_NO_CHANGES`；传入 `true` 返回 `ERR_BAD_REQUEST`，
因为待复核状态只由系统在观察不到结局时设置。
提交 `result` 同样表示确认结果，为待复核记录再次提交当前结果亦属确认。
仅修改备注、职业或时间不清除待复核状态。`manually_corrected` 只记录人工更正历史，不代表结果已确认。

`job_name` 与 `role` **不由客户端直接给出**。更正 `job_id` 时，采集服务按
`data/jobs/jobs.json` 重新推导这两列，并在 `changes_json` 中如实记录其变化，
以保证职业名称与职能始终与 `job_id` 一致。

`note` 是 schema v2 新增的列，定义见 [data-model.md](data-model.md) §1.1。
契约中的 `CorrectRunRequest.changes` 与 `CreateManualRunRequest` 均已声明该字段，
见 [../contracts/CHANGELOG.md](../contracts/CHANGELOG.md) 第 8、9 条。

更正的校验对象是**变更后的整行**，而不是请求中出现的字段。因此，仅修改 `ended_at_utc`
也可能因数据库中已有的 `entered_at_utc` 而返回 `ERR_TIME_ORDER`。此外：

- `result != CANCELLED_BEFORE_ENTRY` 的记录必须有 `entered_at_utc`；
- `result = COMPLETED` 的记录必须有 `ended_at_utc`。

违反上述两条时返回 `ERR_BAD_REQUEST`，并在 `payload.field` 中指出具体字段。
若进本时间或结束时间实际发生变化，而请求未显式给出 `duration_ms`，
采集服务按 `ended - entered` 重算时长。端点未发生变化时保留原时长，
包括自动记录的单调时长与用户此前明确填写的时长。

以下字段**不可更正**，由系统维护：`run_id`、`revision`、`source`、`capture_session_id`、
`protocol_profile_id`、`game_build`、`region`、`detection_confidence`、
`manually_created`、`created_at_utc`、`updated_at_utc`。
`manually_corrected` 由系统在首次 `CorrectRun` 时置 1，且不会因后续操作回退。

活动记录的人工更正按字段保护：后续的自动观察与重启恢复从已有人工修订中读取实际被变更的字段，
保留当前人工值，其余字段继续自动补齐。职业标识与职业名称、职能一并保留；
副本标识与其关联的展示字段一并保留。旧版本曾回退过历史标记的行同样以修订证据为准。
自动观察不新增人工修订，也不修改既有修订号；重启恢复仍追加原有的系统修订与事件。

**撤销会解除该字段的保护。** 保护集合由 `actor = USER`、`change_kind = CORRECT` 的修订
按顺序重放得出：字段被改为其他取值即进入保护集合；字段被改回该用户首次修改之前的取值时，
从保护集合中移除，`UndoRevision` 产生的正是后一种形态。
因此，用户误改后执行撤销，后续的自动观察可以继续补齐该字段，不会使其永久停留在空值上。

比较的参照物是该字段第一条 `USER` / `CORRECT` 修订的 `old_value`，而**不是**创建修订中的初值。
`UndoRevision` 写回的正是被撤销修订的 `old_value`。字段在被用户修改之前往往已由抓包补写过一次，
例如进本后写入进入时刻、补上职业，此时其取值与创建初值并不相同。
若以创建初值为参照，撤销之后该字段仍被判为受保护，后续真实的换职业事件将被永久阻挡，
相关分析见评审 R-8。`manually_corrected` 与整条修订链不受撤销影响：
历史不会被抹除，撤销本身也是一次新的修订。

时间字段的合并分为两种情形：

- 记录**存在**受保护的人工字段时：人工时间与后续自动端点冲突，
  或保护空端点会产生不完整的 `COMPLETED` 时，保留当前合法的时间与时长，
  未确认的结果维持 `UNKNOWN` 并标记待复核。采集服务不伪造时间，也不因合并而导致采集写入失败。
- 记录**不存在**任何受保护字段，即完全由自动写入时：越界的墙钟端点被贴到相邻端点上，
  `matched` 拉到 `entered`，`ended` 抬到 `entered`，而不是整组退回上一版本。
  `duration_ms` 取自单调时钟，不受影响。因此，本机时钟与服务器时间存在偏差时，
  一次正常的收尾仍然产生已关闭的记录，不会出现 `ended_at_utc` 为 NULL、
  每次启动都被重新发现的“未知 · 永远未完结”记录。

## 8. 软删除的语义

- 软删除将 `pending_review` 置 0：删除一条记录同样构成一次复核。恢复操作不改变该字段。
- `soft_deleted = 1` 的记录**永远不参与任何统计**，
  定义见 [statistics-definitions.md](statistics-definitions.md) §0。
- 软删除的记录默认不出现在列表中，只有 `RunFilter.include_deleted = true` 时才显示，
  并带有明显的“已删除”标记。
- 软删除的记录仍占据 `run_id`，仍保留完整的修订链，可随时通过 `RestoreRun` 恢复。
- 导出默认不含软删除记录；如需包含，须由用户在导出对话框中显式勾选。

## 9. 与“重启后待复核”的关系

进程重启时，未完结记录中未受保护的结果被置为 `result = INTERRUPTED`、
`detection_confidence = LOW`，详见 [state-machine.md](state-machine.md) §3.9。
人工保护在此过程中仍然适用，时间冲突时保持未知结果。
桌面端按 `pending_review` 将需要确认的记录列为“待复核”。用户可采取两种处理方式：

- 通过 `CorrectRun` 提交实际结果（含 `COMPLETED`），或显式提交 `pending_review=false`，
  并附上理由。该操作清除待复核状态，记录随之移出列表；仅修改备注不会将其移出。
- 通过 `SoftDeleteRun` 删除该记录，适用于该次实际上并未进入副本的情形。

**系统自身永远不会把待复核记录自动改为 `COMPLETED`。**

## 10. UI 要求

- 更正对话框必须显示“当前值 → 新值”的对照，以及必填的理由输入框。
- 记录详情页必须设有“修改历史”区域，按时间倒序展示修订链，包括操作者、时间、变更内容与理由。
- 由人工创建或更正过的记录在列表中带有可见标记（`manually_created` / `manually_corrected`），
  使用户能够区分自动记录与人工数据。
- 导出结果必须自带可信度信息。

### 10.1 导出格式（已实现）

**CSV**：UTF-8 编码并**带 BOM**，这是 Excel 正确打开中文副本名的前提；
转义遵循 RFC 4180，含 `,`、`"` 或换行的字段加引号，字段内部的引号写成 `""`；行尾为 `CRLF`。
表头固定为 16 列，顺序不可变，新列只能追加在末尾，既有列的位置永不变动：

```
date, matched_at, entered_at, ended_at, duty_name, duty_category, job_name,
result, duration, source, manually_corrected, run_id, content_id, job_id,
reflection_mood, reflection_text
```

- `date` 取 `entered_at` 的 `yyyy-MM-dd`；`entered_at` 缺失时依次回退到 `matched_at`、`ended_at`。
  该列是 **UTC 日历日**，不是本地日。同一行的 `matched_at`、`entered_at`、`ended_at`
  是完整的 UTC ISO-8601 时间戳，本身没有歧义；但 UTC+8 的用户在当地 22:00 进行的一场，
  其 `date` 会落在前一天。需要按本地日分组时，应依据这三列自行换算，不要使用 `date`。
- `duration` 为**整数毫秒**，与 `duration_ms` 同值，留空表示未知。
- `manually_corrected` 取 `0` 或 `1`。
- `reflection_mood` 取 `good` / `ok` / `bad`，`reflection_text` 为心得正文；
  没有心得时两列均为空，字段定义见 [data-model.md](data-model.md) §1.3。

**防公式注入只作用于自由文本列。** 电子表格会把以 `=`、`+`、`-`、`@`、制表符或回车开头的
单元格当作公式执行。因此，`duty_name`、`duty_category`、`job_name`、`reflection_text`
这四列的内容若以上述字符开头，导出时会在引号**内部**前置一个撇号（`'`）。
Excel 与 LibreOffice 均会吸收该撇号并显示原文；以脚本解析时，需去掉首字符还原原文。

其余 12 列（`date`、`matched_at`、`entered_at`、`ended_at`、`result`、`duration`、
`source`、`manually_corrected`、`run_id`、`content_id`、`job_id`、`reflection_mood`）
**原样导出**，不加撇号。这些列的取值来自枚举、UUID 与数字，不可能构成公式。
早期实现对全部 16 列无差别处理，会改写心得中形如 `+1，很顺`、`-记得先清小怪` 的正文，
导致导出结果与 `QueryRuns` 读到的不再是同一份文本。

**JSON**：由契约 `$defs/Run` 对象组成的数组，`revision`、`manually_created`、`soft_deleted`、
`pending_review`、`note`、`reflection` 等全部字段均包含在内。
需要完整可信度信息时应导出 JSON；CSV 的 16 列是面向电子表格的精简视图。

两种导出遵循与 `QueryRuns` **完全相同**的 `RunFilter`，导出范围与列表中可见的范围一致。
导出目录可以选择个人文件夹之外的本地目录，例如其他盘符下的文件夹，该目录须具备写入权限。
UNC 共享、网络映射盘、设备路径及备用数据流仍返回 `ERR_EXPORT_FAILED`；
目录或文件链接的最终位置同样必须是本地路径。已有文件的覆盖规则不变。
