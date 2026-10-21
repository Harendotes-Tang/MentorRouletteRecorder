# IPC 契约变更记录 / IPC contract changelog

## 2026-10-21 · `elapsed_ms` 的口径、更新地址的来源、导随进行中拒绝更换校准与其他澄清

不新增消息类型；除 `CheckSharedCalibration` 应答新增两个可选字段外，不改变任何结构；以下为口径修正、新的拒绝情形与此前未写明的约定。

- **`GetCurrentRun.elapsed_ms`** 改为副本内用时：从这一场进入副本（`ENTERED_DUTY`）起按单调时钟计时；进本由事后补交的报文得出时（确认校准或共享绑定交还此前保留的报文），从进本那条报文的时刻算起。没有进行中的记录、或记录尚未进入副本（`MENTOR_MATCHED`）时为 `null`。此前它从匹配弹窗、匹配宣布或绑定时刻起算，进本前也有数值；桌面端据此显示的进本后用时（总览「已进行」、统计浮窗的副本用时）因此不再包含匹配到进本之间的等待，进本之前总览显示「—」，浮窗另按 `matched_at_utc` 显示匹配后已过的时间。契约中该字段的描述已写明这一定义。
- **延迟应答上限。**`SynthesizeSpeech`、`CheckDatabaseIntegrity`、`CheckUpdateNow`、`PreviewRunImport` 与 `CommitRunImport` 在完成后另行写回应答。同一连接上已受理而尚未应答的这类请求达到 8 条时（尚未应答的 `CheckUpdateNow` 无论几条只计一条，它们加入同一次进行中的检查，自身另以 8 条为限），再到达的一条不被处理，采集服务直接关闭连接，不发送错误信封，尚未写出的应答一并丢弃（[docs/architecture.md](../docs/architecture.md) §3.3、[error-codes.md](error-codes.md)）。这是分帧错误之外唯一不作应答即关闭连接的情形；客户端应把连接断开视为全部未完成请求的失败，变更类请求以原 `request_id` 重发，以得到确定结果。
- **`UpdateStatus.release_url` / `installer_url` 跟随版本号的来源。**`latest_version` 由 GitCode 镜像得知时，两个地址分别是镜像上该版本的发布页与同名附件；提供版本号的来源随版本号一同保存（设置 `update.latest_source`），重启后照常给出镜像地址。此前来源只在内存中，重启后一律给出 GitHub 地址；2026-09-17 一节「`release_url` 固定为公开发布页」的说法自镜像回退起已不成立。
- **`CheckUpdateNow.outcome = DISABLED`** 另包括检查进行中关闭「检查新版本并提示」的情形：该次检查不再发出任何请求（包括镜像），也不记录检查时间与结果。同一连接上重复的 `CheckUpdateNow` 加入同一次进行中的检查。
- **`ImportCalibrationCode.reason`** 新增 `NOT_APPLICABLE` 原因 `STATE_UNREADABLE`：本机的共享校准记录暂时无法读取（文件被占用，或读取时出错），因而不知道玩家是否拒绝过，或不知道这份码是否已被本机流量否定，稍后重试可能成功；说明为 `sharing.import.state_unreadable`（「暂时读不到本机保存的共享校准记录（可能被其他程序占用），请稍后再导入。」）。`REVOKED` 的含义改为本机读到过的任一份索引撤回过这份码：保存的撤回名单只增不减（每个版本最多 512 个），之后读到的索引不再列出撤回时名单不变。
- **`CheckSharedCalibration` 应答新增可选的 `message` 与 `message_ref`。**本机的共享校准记录暂时无法读取、因而不知道玩家是否已对当前游戏版本选择只用本机校准时，「立即检查」与此前一样不发出任何请求，`outcome` 仍为 `NOT_NEEDED`，另带说明 `sharing.check.state_unreadable`（「暂时读不到本机保存的共享校准记录（可能被其他程序占用），请稍后再查找。」）。桌面端收到这两个字段时应显示该说明，而不是 `NOT_NEEDED` 的固定说明；此前这种情形显示「已有可用校准，或你已选择当前游戏版本只用本机校准」，与实情不符。其余情形不带这两个字段；旧桌面端忽略它们，照旧显示原说明。
- **`CorrectRun.changes`** 中的 `result`、`contributes_to_goal` 与 `pending_review` 不再接受 JSON `null`，以 `ERR_BAD_REQUEST` 拒绝（`field` 分别为 `payload.changes.result`、`payload.changes.contributes_to_goal` 与 `changes.pending_review`）。此前 `"result": null` 被当作确认原结果，记录随之退出待复核；桌面端从不发送这样的请求。
- **导随进行中拒绝更换校准。**状态机处于 `MENTOR_MATCHED` 或 `ENTERED_DUTY`，或按排本推断匹配的档案保留着一条仍在匹配窗口内的排本申请（玩家已在排队、还没有记录）时，`DiscardCalibration` 带 `retire_local_profile` 或 `restore_local_profile`、以及共享档案生效时的 `RejectSharedCalibration`，以 `ERR_CALIBRATION_NOT_READY` 拒绝（目录键 `calibration.change_during_run`；只在排队时为 `calibration.change_while_queued`），这一场结束、或那条排本申请失效之后可再次请求；此前它们会把进行中的记录按停止捕获收尾，或丢掉已在排队的申请，使接下来的这一把不被记录。不带这两个字段的 `DiscardCalibration` 不受此限（[error-codes.md](error-codes.md)、[docs/protocol-profile-format.md](../docs/protocol-profile-format.md) §11.6）。
- **导入：表格中只有日期的开始时间。**`PreviewRunImport` 的表格来源（XLS、XLSX、CSV、PASTE）中，`entered_at_utc` 只有日期（`yyyy-MM-dd`、`yyyy/M/d` 或 `yyyy.M.d`）时不作为进本时间，而以 `yyyy-MM-dd` 写入 `import_metadata.source_recorded_at`，`entered_at_utc` 为 `null`。同一行已有与之同一天的原站时间（包括带时刻的）时保留原值；日期不同，或原站时间只以 UTC 给出时，该行以 `import.row.start_date_source_conflict` 判为无效（[docs/data-model.md](../docs/data-model.md) §9）。
- **导入：本地时间原样保留在候选中。**`rows[].candidate` 中不带时区或偏移的 `matched_at_utc`、`entered_at_utc`、`ended_at_utc`、`created_at_utc` 保持来源原文，不再改写为以 `Z` 结尾的规范文本；`rows[].run` 照常给出按 `time_zone` 换算的 UTC。以新的 `time_zone` 重新校验时，这些时间按新时区重新换算。
- **导入：行的默认身份与「可能重复」。**没有 `run_id` 与 `source_key` 的行，默认 `source_key` 改为该行自身取值的哈希加上它在相同行中的序号，不再取整份文件的哈希加行号；旧键仍一并与已保存的来源指纹核对，来源指纹的组成不变；职业只能靠中文别名识别的行另按早先版本的职业解析结果再算一份指纹核对。`possible_duplicate` 另包括：与本地记录副本相同、原站日期相同、双方至少一方没有具体时刻（双方都有职业时职业也相同）的行，以及同一批次中带有具体时刻（进本时间或原站时间）、且与前面某一行副本、进本时间、原站时间与心得都相同的行（`matching_run_id` 指向前面那一行；只有日期的行不按此判定）。规则见 [docs/data-model.md](../docs/data-model.md) §9。
- **导入：来源文件路径的拒绝。**`PreviewRunImport` 的 `file_path` 指向网络共享、设备路径，或无法确认是否位于本机磁盘时，以 `ERR_BAD_REQUEST`（`field = "file_path"`，`import.preview.path_not_local` / `import.preview.path_uncheckable`）拒绝；此前沿用导出的错误码与说明。
- **`BackupDatabase` 核对副本。**备份副本在发布前另行打开并做完整性校验；未通过时以 `ERR_EXPORT_FAILED`（`export.backup_integrity_failed`）拒绝，且不清理既有备份。应答中的 `integrity_check_passed` 因此只要有应答即为 `true`。
- **`GetDungeonStats`**：`territory_id` 在本地副本表中查不到的记录并入 `content_id` 与名称都未知的那一行「未知副本」，不再各自成行（[docs/statistics-definitions.md](../docs/statistics-definitions.md) §10）。
- **文字目录**（[collector-texts.json](collector-texts.json)）新增 `calibration.change_during_run`、`calibration.change_while_queued`、`sharing.import.state_unreadable`、`sharing.check.state_unreadable`（出现在 `CheckSharedCalibration.message_ref`）、`import.preview.path_not_local`、`import.preview.path_uncheckable`、`capture.warning.validation_releasing`（出现在 `CollectorStatus.warnings`）与 `calibration.provisional.searching_job`、`calibration.provisional.searching_more`、`calibration.provisional.blocked_more`、`calibration.provisional.recording_popup`（出现在 `CalibrationStatus.blockers`；最后一个是按排本推断匹配、已认得匹配弹窗的档案正在记录而后台校准无法继续时，卡片的第一句）；`capture.game.region_ambiguous`、`capture.game.region_unmarked`、`capture.game.path_denied`、`capture.game.path_unknown` 去掉句末的「（fail-closed）」，`capture.oodle.profile_load_failed` 改写为面向玩家的说明；`import.row.start_date_source_conflict` 改写为说明哪一项对不上。旧桌面端照常显示中文原句。

## 2026-10-20 · 本人历史导入可分页核对整批记录

- `PreviewRunImport` 首次来源请求可带整数 `page_size`（1–100），应答新增可选 `page_info = { page, page_size, total }`。服务端先分类冻结全批，再按完整行切页；`page` 从 1 开始，实际 `page_size` 可降低以满足原 3 MiB 预览预算，随后固定。`total`、`summary`、来源和 `preview_id` 在各页保持全批快照，不因翻页重新读取文件或数据库，跨页重复照常识别。
- 续页请求只含 `preview_id` 与 `page`，禁止混入来源、编辑或 `page_size` 字段；空批次仅有第 1 页。过期、越界、混合请求以关联的 `ERR_BAD_REQUEST` 拒绝。冻结缓存保留 30 分钟、最多 8 个会话，并按冻结 JSON 的 UTF-8 字节数限制总计 64 MiB；先回收最旧会话，单批超预算明确拒绝，取消的预览不进入缓存。全批候选（`rows[].candidate` 的 JSON）超过 3 MiB 时，分页预览在冻结之前即以 `ERR_BAD_REQUEST`（`import.preview.too_large`，`field = "rows"`）拒绝：其后的每次编辑或时区更改都要把全批候选放进一条请求重新校验，超过这一预算的批次将无法提交。
- 未带 `page_size` 的旧请求仍返回完整预览并保留原容量拒绝；4 MiB 帧上限不变。分页不改变 `CommitRunImport`：全部选中行仍在同一事务内重查去重与冲突、扣除已包含的成就基数并保存幂等回执，失败整批回滚。

## 2026-10-20 · 中途加入进行中的副本：`Run.joined_in_progress` 与三处统计计数

附加式变更，不新增消息类型。采集服务按内容识别国服副本导演报文中的两种内容：开始宣布与剩余时间（[docs/protocol-profile-format.md](../docs/protocol-profile-format.md) §13），据此在进本后记下这一场是否中途加入进行中的副本（[docs/state-machine.md](../docs/state-machine.md) §3.14）。

- **`$defs/Run.joined_in_progress`**（布尔或 `null`，每条记录都带）：`true` 中途加入了进行中的副本；`false` 副本在玩家在场时才开始；`null` 软件没有看到（schema 11 之前写入的记录、没有填写的手工记录、不比较这两种报文的档案、两种报文到达之前就离开的副本）。
- **`CorrectRunRequest.changes.joined_in_progress`** 与 **`CreateManualRunRequest.joined_in_progress`**（布尔或 `null`，可选）：人工填写或更正进入方式。填入原本为 `null` 的值不算更正（不置 `manually_corrected`，[docs/manual-correction.md](../docs/manual-correction.md) §7），改掉软件记下的值算更正；撤销照常恢复旧值。
- **`DashboardStats.joined_in_progress_count`**（整数）与 **`joined_in_progress_rate`**（0–1 或 `null`）、**`DungeonStatsRow.joined_in_progress_count`**、**`JobStatsRow.joined_in_progress_count`**：口径见 [docs/statistics-definitions.md](../docs/statistics-definitions.md) §12.2。占比的分母是软件看到过进入方式的尝试（`joined_in_progress` 非空），没有这样的尝试时为 `null`。既有的次数、通关率、离开率、平均时长与成就进度不变。
- **`RunFilter.joined_in_progress`**（布尔，可选）：`true` 只列中途加入的记录，`false` 只列开局进入的记录；软件没有看到进入方式的记录两种取值都不命中。
- **`CaptureStatus.last_valid_event_kind`** 新增两个取值 `DUTY_COMMENCED` 与 `DUTY_TIME_REMAINING`（副本开始宣布、副本剩余时间，[docs/protocol-profile-format.md](../docs/protocol-profile-format.md) §13）；`GetRunEvents` 的 `event_type` 同样可能出现这两个值。旧桌面端按「不认识的取值视为某个事件」处理。
- 兼容性：旧采集服务不发送这些字段，对请求中的新字段答 `ERR_BAD_REQUEST`；旧桌面端忽略新字段。CSV 导出在末尾追加一列 `joined_in_progress`（`1` / `0` / 空）；此前 16 列的导出文件导入时仍按本软件的导出处理；备份与 JSON 导入接受同名字段。诊断日志新增 `protocol/duty_start_signal`（[docs/capture-diagnostics.md](../docs/capture-diagnostics.md) §8）。

## 2026-10-15 · 采集服务的提示按桌面端的界面语言显示

附加式变更，不新增消息类型，原有字段的取值逐字不变，只有两处例外：`StartCaptureValidation` 在验证进行中被拒绝时，`message` 去掉了句末多余的空格；文字参数超过 4000 个字符时（例如回显的超长字段名）截断并以「…」结尾，中文与引用一致。设计见 [collector-texts.md](collector-texts.md)。

- 新增 **`$defs/TextRef`** `{ key, args? }` 与 **`$defs/TextRefList`**（与中文列表等长同序，无目录键的项为 `null`）。`key` 是 [collector-texts.json](collector-texts.json) 中的键；`args` 按名称给出占位符的取值，种类为整数、原样文字、副本 / 职业 / 随机任务的国服名称、`UtcTimestamp`（显示为本机 `HH:mm`）或嵌套的 `TextRef`。
- 采集服务在原有的中文字段旁写出配套字段：`ErrorPayload.message_ref`、`CaptureStatus.hint_ref`、`CollectorStatus.warning_refs`、`CollectorStatus.npcap.install_hint_ref`、`ListCaptureAdapters.install_hint_ref`、`ProtocolProfileStatus.message_ref`、`CaptureValidationStatus.message_ref`、`CalibrationStatus.blocker_refs`、`CalibrationEvent.label_ref`、`UpdateDownload.message_ref`、`ImportCalibrationCode.message_ref`、`RunImportPreview.rows[].error_refs` / `warning_refs`、`SpeechVoiceOption.label_ref`（collector-texts.md §2.1）。全部为可选字段；脱敏诊断报告不含配套字段。
- 拒绝启动时，标准错误在最后一行 `ERR_XXX: <中文说明>` 之前另写一行 `MR-TEXT <TextRef JSON>`（collector-texts.md §2.2）。
- 兼容性：旧版桌面端忽略配套字段与 `MR-TEXT` 行，照旧显示中文；新版桌面端遇到旧版采集服务（没有配套字段）、不认识的键或读不出的参数时同样显示中文。简体中文界面显示的正是采集服务的中文原句。

## 2026-10-15 · 公开证据预览与新区服候选采证

- 新增只读 `GetCalibrationEvidence { code_sha256?: string | null }` 与 `GetTemplateObservation {}`，统一返回 `{ available, kind, evidence_json, issue_url, evidence_in_url, reason }`。不可用时 JSON 与地址为 `null`，原因是固定 token；读取不上传、不写剪贴板、不打开浏览器。旧服务拒绝新消息时，桌面端说明需更新服务。
- `evidence_json` 是 [calibration-evidence-v1.schema.json](calibration-evidence-v1.schema.json) 的严格白名单 JSON，最多 48 KiB UTF-8。真实完整健康会话与随机别名独立留存；无模板观察只给元数据候选，不发布原始报文、报文哈希、角色、地址、设备标识或游戏时间线。
- `issue_url` 固定为共享仓库的证据或模板观察表单，只含 `template` 与可选完整 `evidence` 参数；超过 7500 字符省略预填，仍提供完整 JSON 供玩家显式复制。提交后摘要随公开 Issue 保留。
- 公开索引可选 `quarantined` 与非负 `quarantine_epoch`，客户端将隔离候选改为完整本机核实，仍可下载观察；恢复保留 epoch。不使用永久撤回列表，也不因此撤下本机已证明的档案。
- `StartCaptureValidation` 新增可选布尔 `switch_from_unsupported`，仅由玩家明确点击“切换为采证”发送。在采集生命周期锁内完成停止无正式档案的监听和启动采证，避免自动跟随在两次请求之间重占租约；已有 VERIFIED 档案时拒绝，不停止正在记录的会话。
- `SharedCalibrationCandidate` 新增可选 `code_sha256`（完整哈希或 `null`），供多候选证据选择；旧服务只给 `sha12` 时不补造完整身份。

## 2026-10-15 · 共享校准的字段级共识与「这份校准好用，告诉其他玩家」

附加式变更，不新增消息类型。设计见 plans/shared-calibration.md §20。

- **`SharedCalibrationCandidate.disputed`**（可选字符串数组）、**`preferred`**（可选布尔）、**`shape_prior`**（可选，布尔或 `null`）。采集服务把同一 (区服, 版本, 模板) 下正在核实的候选逐字段比对：换区、区域、职业跨匹配方式比对，匹配报文（匹配方式与 `pop`）只在同一匹配方式内比对，未声明的字段不投票也不构成分歧。`disputed` 列出与其他候选取值不同的报文名（`ZONE_INITIALIZATION`、`ZONE_TERRITORY`、`PLAYER_JOB`、`CONTENT_FINDER_POP`，按此顺序，没有分歧时为空数组）；`preferred` 表示在每个有分歧的字段上，本候选的取值得到的提交账号数严格多于其他取值，或匹配报文票数相同而只有本候选 `shape_prior == true`，没有分歧时为 `true`；`shape_prior` 为这台机器自己算出的结构先验，不是公开索引里的同名字段：与这台机器上一版本核实过的档案的匹配报文形状一致时为 `true`（只看同一区服、同一模板、日期严格更早的最近一个版本；本机校准的档案，以及完整记录过一次且审计全部通过的共享档案；同一天的版本不比较），匹配方式相同而形状不同时为 `false`，没有可比的档案、按排本推断或从磁盘采用的档案为 `null`。索引里的 `shape_prior` 任何账号都可以借一个虚构的更早版本写进去，因此只用于下载与候选排序，不参与门槛。在用档案的这三项取自绑定时的候选；从磁盘采用的为空数组、`true`、`null`。同一对象也写入脱敏诊断报告。旧采集服务不发送，桌面端应把缺省的 `preferred` 视为 `true`。
- **登录即启用的条件改变（取值规则，字段不变）。**已发布的校准码只有在 `preferred == true` 时才按 `PUBLISHED` 门槛核实（同时仍须本机没有可用档案、`submitters > 0`、没有提交人数更多的候选），此时 `criteria[].gate` 中匹配与进本两项为 `AUDIT`；否则两项为 `REQUIRED`。索引的 `conflicting` 标记不再单独让匹配与进本两项变为 `REQUIRED`，只影响候选顺序。通过核实的候选按 读服务器报文优先 → `preferred` → 提交人数 → 本机的 `shape_prior == true` → 索引的 `shape_prior == true` → 下载顺序 绑定。
- **`GetCalibrationShareCode` 应答新增可选 `basis`**：`CALIBRATED`（本机校准的档案，与此前相同）或 `VERIFIED`（其他玩家分享、在本机完整记录过一次随机任务且审计判据全部通过的共享档案；码文本取自本机下载保存的校准码）。`issue_url` 在标题之后预填表单的 `basis` 文本框（GitHub 不能预填下拉框），取值逐字等于公开仓库 `issue.py` 接受的两段文字之一，其他非空文字会被 Action 以 `BASIS_INVALID` 拒绝；校准码仍放在最后，地址超长时只去掉校准码。旧采集服务不发送该字段，桌面端应视为 `CALIBRATED`。
- **重启后仍列出已核实的共享档案（取值规则，字段不变）。**此前在上一次运行中已完整核实（`PROVEN`）、且读服务器匹配报文的共享档案，重启后校准不再布防，`SharedCalibrationStatus.candidates` 为空。现在它作为第一个候选列出：`status = PROVEN`、`audit_pending = false`、`disputed` 为空、`preferred = true`；`provenance` 在本机保存着它的校准码时为 `PUBLISHED`，否则为 `IMPORTED`（例如粘贴导入、从未下载的码）；`shape_prior` 与 `source` 为 `null`。这些全部来自本机已有的文件，不发出任何网络请求。本机记录显示它的校准码已被撤回时，同样在重启后撤下并把它生成的记录标为待复核，此前这一检查只在校准布防时进行。
- **在用档案的 `provenance` 含义（取值规则，字段不变）。**状态为 `IN_USE` 或 `PROVEN` 的在用档案，`provenance = PUBLISHED` 表示本机保存着这份已发布的校准码（`GetCalibrationShareCode` 能交回的正是它），否则为 `IMPORTED`，与它按哪一套门槛被核对无关。索引只把它列为下载失败的条目时也是 `IMPORTED`。桌面端据此决定是否显示「这份校准好用，告诉其他玩家」。
- **`ERR_SHARE_CODE_UNAVAILABLE` 的 `details.reason`**：新增 `SHARED_NOT_PROVEN`（在用的共享档案尚未完整记录过一次随机任务或仍有审计判据在等待）；`SHARED` 的含义改为「在用的共享档案已核实通过，但本机没有保存对应的校准码」（例如玩家粘贴导入、从未下载的码），不再表示「共享来的不再转手」。

## 2026-10-11 · 日历可选日期查询

- 新增只读 `QueryRunCalendarDays { month: "YYYY-MM", filter?: RunFilter }`，应答为 `{ month, days: ["YYYY-MM-DD"] }`。日期升序去重，覆盖完整匹配集合，不受分页截断。
- 查询忽略 `from_utc`、`to_utc`、`history_from_day`、`history_to_day`，保留其他筛选（含删除筛选）；已知历史时刻按本机时区投影，原站仅日期保留原日，未知时间不生成日期。
- 月份与字段严格校验；旧服务明确拒绝新消息，桌面端不得将失败当作无记录。

## 2026-10-11 · 已删除记录独立筛选

- `RunFilter.deleted_only` 为可选布尔，默认 false。为 true 时，历史列表、分页总数和筛选导出只匹配软删除记录，优先于 `include_deleted`；其他筛选继续取交集。
- `include_deleted` 单独使用仍包含正常与已删除记录。统计忽略两种删除筛选，始终排除删除记录。旧服务不支持新字段时继续明确报错。

## 2026-10-10 · 历史多选、回收站保留期与导入时间豁免

- 新增 `BatchMutateRuns { action, runs: [{ run_id, expected_revision }], reason }`，支持 `soft_delete`、`restore` 与仅限已软删除目标的 `purge`；整批校验与提交，冲突不部分执行。结果带 `changed_count`、`run_ids` 与 `action`。
- 新增 `GetHistoryRetentionSettings {}` / `UpdateHistoryRetentionSettings { retention_days }`。默认 30 天，0 表示永不自动清理，允许 1–36500 天；应答报告实际保存值。`Run.deleted_at_utc` 为可空的最近软删除时间。
- 新增 `GetPendingImageCleanup {}` / `AcknowledgeImageCleanup { run_ids }`。获取应答带 `run_ids`，按最近尝试顺序轮转，每页最多 2000 条；成功清理对应本地附件后再确认。
- `RunFilter.run_id` 支持最多 2000 个精确记录编号；选择导出不重用其他页面筛选，目标缺失时拒绝写出文件。`date_field` 与排序可采用 `history_date`；`history_from_day` / `history_to_day` 仅随对应 UTC 边界使用，保持仅有原站日历日的筛选语义。
- 已确认且结果已知的本人导随历史 IMPORT 不因未知游戏时间待复核或被排除统计；`import_metadata.incomplete` 仅表示事实待补充。总览待复核计数排除历史导入，实际时间/耗时仍保持未知。成就基数扣除使用同一统计资格，兼容旧请求与已有回执。
- 永久删除清除对应正文和旧回执，保留无正文墓碑防止重放。旧采集服务不识别新消息或筛选字段时明确拒绝，新桌面端不将拒绝报告成成功。

## 2026-10-09 · `CommitRunImport` 可以从成就基数中扣除本批已包含的通关（一个可选请求字段、两个可选应答字段）

附加式变更，不新增消息类型。

- **`CommitRunImportRequest.deduct_from_baseline`**（可选布尔）。为 `true` 时，采集服务在同一事务内统计本次新写入的记录中会计入成就进度的通关（确认为导随、`result = COMPLETED`、`contributes_to_goal = true`、非待补充，与 `GetDashboardStats.achievement_progress` 同一口径），从 `baseline_completed_count` 中扣除同样多的次数，扣除后基数不低于 0，并以该请求的 `request_id` 追加一条基数修改记录。重复与冲突的行没有写入，不参与扣除。缺省或 `false` 时基数不变。该选项参与提交的幂等判定：同一 `preview_id` 或 `request_id` 以不同的取值重发时，返回 `ERR_IDEMPOTENCY_CONFLICT`，与改变 `row_numbers` 相同。
- **`RunImportCommit.baseline_deducted_count`** 与 **`baseline_completed_count`**（可选整数）：本次扣除的次数（未要求扣除时为 0）与提交后保存的基数。此前写下的回执没有这两个字段，重放时照原样返回。
- 导入写入了记录时照常发布 `stats_invalidated`（`records_imported`），基数的改变随同一次重新读取到达桌面端。旧采集服务对未知字段答 `ERR_BAD_REQUEST`，旧桌面端不发送该字段。

## 2026-10-05 · 国服的通关结算按内容识别；`Run.pending_review` 的说明改写（数据结构不变）

没有新增或改动任何消息、字段与取值，`$defs/MessageType` 仍为 **52** 个业务消息 + `Event` + `Error`。

- **国服的记录现在可以由采集服务自行记为 `COMPLETED`。**副本内收到通关结算时（按报文内容识别，docs/protocol-profile-format.md §12），`run_finished` 的 `state` 为 `COMPLETED`，`Run.result = COMPLETED`、`pending_review = false`；这些取值此前已在契约中，只是国服档案从未产生。离开副本前没有收到结算时与此前相同：`UNKNOWN_FINAL_STATE`、`result = UNKNOWN`、`pending_review = true`。
- **`Run.pending_review` 的说明改写。**此前只写「崩溃恢复关闭的记录」；实际上副本以未知结果结束、匹配后始终未见进入，以及生成记录的校准被撤下时也会置位，其中最后一种不改动结果，因此待复核的记录可以已经是 `COMPLETED`。桌面端不得把「待复核」等同于「结果未知」。
- 通关结算的计数（`duty_clear_signals`、`duty_clear_completions`）只出现在诊断日志与脱敏诊断报告中，不进入 `CaptureStatus`。

## 2026-10-04 · 软件内下载更新：`StartUpdateDownload` / `CancelUpdateDownload`（两条新消息）

附加式变更：两条新消息、`UpdateStatus` 的一个新对象。`$defs/MessageType` 现共 **52** 个业务消息 + `Event` + `Error`。此前的约定「更新只提示，不下载」改为「只在用户要求时下载，采集服务从不执行所下载的文件」（docs/privacy-boundary.md §8.6）。

- **新消息 `StartUpdateDownload { reinstall? }`** → `{ update: UpdateStatus }`。用户要求下载 `latest_version` 的安装程序。立即应答，下载在后台进行：`update.download.state` 为 `DOWNLOADING`（已开始或正在进行）、`VERIFYING`（本机已有该版本的安装程序，正在重新校验；仍一致则转为 `READY`，否则重新下载）或 `FAILED`（`NO_UPDATE`：没有获知更新的版本；`DISABLED`：更新检查已关闭或被环境变量停用，此时不发出任何请求）。`reinstall = true` 时不要求 `latest_version` 比本程序新，只要已获知即可，供维护者重新取得已发布的安装程序。该版本的安装程序刚刚校验过时直接答 `READY`；被取消的下载尚未收尾，或距上一次开始不足数秒时，不开始新的下载，原样报告当前状态。
- **新消息 `CancelUpdateDownload {}`** → `{ update: UpdateStatus }`。停止正在进行的下载并删除已写入的内容，之后 `state` 为 `IDLE`；其他状态下不做任何事。
- **`UpdateStatus.download`**（`$defs/UpdateDownload`）：`state`（`IDLE` / `DOWNLOADING` / `VERIFYING` / `READY` / `FAILED`）、`version`、`received_bytes`、`total_bytes`、`file_path` 与 `sha256`（仅 `READY`）、`failure` 与 `message`（仅 `FAILED`）。凡报告 `UpdateStatus` 之处（`GetStatus`、`CheckUpdateNow` 与上述两条消息的应答）都带有它，进度通过 `GetStatus` 读取。采集服务从本项目发布页取得安装程序及其公布的 SHA-256，两者一致才保留文件；它从不运行该文件，由桌面端在用户再次点击时启动。下载进行中或安装程序已就绪时把 `update_check_enabled` 设为 `false`，下载随即停止、安装程序随之删除，`state` 为 `FAILED`（`DISABLED`）。下载的两个请求只接受本项目该版本的发布目录与 GitHub 的发布资产服务器。旧桌面端忽略该对象即可；旧采集服务没有该对象，对两条新消息答 `ERR_BAD_REQUEST`，桌面端退回到由浏览器下载。

## 2026-10-04 · 成就进度恢复为基数加全部已记录完成；更新状态给出安装包地址

一项取值规则恢复为 1.5.0 的口径，一个新的可选字段。不新增消息类型。

- `GetDashboardStats.achievement_progress` / `remaining`：进度等于 `baseline_completed_count` 加上全部计入进度的已记录完成（`result = COMPLETED` 且 `contributes_to_goal = true`），不论记录何时结束、基数何时填写或修改。2026-10-03 一节中「结束时间早于 `baseline_effective_at` 的完成不再叠加」的口径只存在于 1.5.1-beta.1 与 beta.2，现已撤回；`baseline_effective_at` 仍随请求与应答传递，只记录基数最近一次改变的时间，不参与任何统计。基数不变时保留已保存的 `baseline_effective_at`、目标与基数都不变的保存不算修改，这两条约定不变。
- `UpdateStatus` 新增可选字段 `installer_url`：`latest_version` 对应安装包在本项目发布页上的地址（`releases/download/v<版本>/MentorRecorder-<版本>-setup.exe`），与 `latest_version` 同时出现、同时缺席。采集服务只报告这个地址，从不请求它；桌面端在用户点击下载时把它交给系统浏览器。重启后读回的 `latest_version` 现在同样必须是三段纯数字，否则视为尚未获知。旧桌面端忽略该字段即可。

## 2026-10-04 · 原样保存成就设置不再算作修改

行为说明，传输格式不变，不新增消息类型。

- `UpdateAchievementBaseline`：`goal_count` 与 `baseline_completed_count` 都与已保存的值相同时，不写入设置与修改记录，也不发布 `stats_invalidated`；应答给出已保存的值——`updated_at_utc` 与 `baseline_effective_at` 为已保存的时间，`audit_event_id` 为写下当前值的那条修改记录的编号（修改记录中没有这样一条时为新编号），`idempotent_replay` 为 `false`。同一 `request_id` 重发仍按幂等约定返回首次的结果。

## 2026-10-03 · 请求字段与契约对齐、存储与启动拒绝的约定修正

删除一个从未生效的可选请求字段，`GetResultStats` 改用自己的请求定义，其余是既有字段的取值规则、错误约定与说明文字。不新增消息类型，消息数目不变。

- **`ExportCsv` / `ExportJson` 删除可选字段 `include_revisions`**：它在契约中声明，却从未起作用，导出从不包含修订记录。现在它与其他未声明字段一样答 `ERR_BAD_REQUEST`。桌面端从未发送过它。
- **`GetResultStats` 的载荷改为 `$defs/ResultStatsRequest`**，只有 `filter`。此前它与 `GetDashboardStats` 共用 `$defs/StatsRequest`，契约允许 `trend_granularity`，而采集服务一直拒绝；现在两边一致。
- 信封或载荷任意一层的对象内字段名重复，答 `ERR_BAD_REQUEST`，按可读出的 `request_id` 应答，连接保持可用。此前信封内重复会让该请求得不到应答，载荷内重复答 `ERR_INTERNAL`。
- `UndoRevision` 新增 `ERR_UNDO_NOT_ALLOWED` 情形：撤销程序写下的修订（`actor = SYSTEM`）会让记录回到“进行中”（自动记录、`UNKNOWN`、没有结束时间、不处于待复核状态），或没有进入时间却不是进本前取消时，拒绝且不写入任何内容。此前撤销重启收尾的修订会让记录永久从统计与待复核中消失。判断有误时用 `CorrectRun` 更正。
- `CorrectRun` 把 `content_id` 显式设为 `null`（未知副本）时，记录中观测到的区域 `territory_id` 一并清空，统计、`RunFilter.content_ids` 与副本名称不再把它归回原副本；`UndoRevision` 照旧恢复两者。请求与响应结构不变。
- `GetDashboardStats.achievement_progress` / `remaining`：成就基数大于 0 时，结束时间早于 `baseline_effective_at` 的完成记录不再叠加在基数上（基数已包含它们）；缺结束时间的按进入时间、再按匹配时间判断，都没有的不叠加。基数为 0 时全部计入。`UpdateAchievementBaseline` 的上限校验采用同一口径。字段与类型不变。
- `UpdateAchievementBaseline`：`baseline_completed_count` 与已保存的基数相同时（只改目标，或重新填入同一个数），保留已保存的 `baseline_effective_at`，不采用请求中的时间；只有基数改变时才以请求中的时间为生效时间。响应中的 `baseline_effective_at` 与审计记录都是实际保存的值。此前每次保存都换成请求中的时间，而桌面端每次发送的都是保存时刻，加上前一条的口径，只修改目标就会让基数生效之后记录的完成从进度中消失。请求与响应结构不变。
- `CaptureStatus.npcap_version`（以及诊断报告与取证文件头里的 Npcap 版本）改为 Npcap 自身的版本，取自 `Packet.dll`；此前报告的是其中 libpcap 的版本（例如 Npcap 1.88 报为 1.10.6）。只读得到 libpcap 版本时写作 `libpcap 1.10.6`。
- `CheckDatabaseIntegrity` 的说明更正：它在自己的只读连接上进行，不经过数据库锁，不会让采集写入等待；请求所在连接断开或采集服务停止时中途停止，不给应答。`BackupDatabase` 同样改在独立的只读连接上复制，备份期间采集写入不再等待。
- 进程启动（不经由 IPC）：SQLite 在打开数据库时报告的错误（不是数据库文件、文件损坏、无法读写、磁盘已满）映射为 `ERR_DB_INTEGRITY`，文件被其他程序锁定映射为 `ERR_DB_BUSY`，退出码均为 3，不再以未处理异常结束进程。所有拒绝打开数据库的原因同时写入本机日志 `startup/open_failed`。管道被另一个 Windows 账户（或以管理员身份运行的进程）占用时退出码为 3，不再报为“已有实例无响应”（6）。
- 补记今日其他修正中对契约可见的变化：`UpdateStatus.update_available` 在 `enabled = false` 时恒为 `false`，此前检查到的版本仍在 `latest_version` 中；`SynthesizeSpeech` 的请求所在连接断开时，正在发送与仍在排队的句子都答 `ERR_SPEECH_TIMEOUT` + `details.reason = CANCELLED`；共享校准中，其他玩家发布的校准码若会顶替本机已可用的校准、带冲突标记，或提交人数少于同一版本的另一个校准码，`SharedCalibrationCandidate.criteria[].gate` 中匹配与进本两项核对为 `REQUIRED`（此前为 `AUDIT`），需在本机核对通过才启用；候选按提交人数优先排序，冲突标记只在人数相同时靠后。

## 2026-10-03 · 游戏客户端身份读取与选择状态修正

附加式变更：一个新的可选计数字段，其余是既有字段的取值规则与错误约定。不新增消息类型。

- `CaptureStatus.game_selection_required` 只在 `game_processes` 至少列出一个客户端时为 `true`。没有任何客户端在运行时恒为 `false`，`game_selection_reason` 仍保留原因（例如 `EXITED`）供诊断；此时 `StartCapture` 返回普通的 `ERR_FFXIV_NOT_RUNNING`（`retryable = true`），不再要求选择窗口。
- `game_selection_reason = MULTIPLE` 在所有客户端都关闭后回到 `NONE`，此后单独启动的客户端自动锁定。`EXITED` 在没有客户端时保留，同一安装目录中单独重启的客户端仍自动接续。
- 进程列表读取失败不再等同于“游戏已退出”：锁定保持，状态沿用上一次成功读取的结果，正在进行的采集不因此停止，同时到来的连接中断仍按掉线处理（docs/state-machine.md §3.6）。同一进程（编号与启动时间都相同）在一次漏读后重新出现时重新锁定；已锁定进程的启动时间暂时不可读时不视为退出。
- 启动时间改为从内核进程表读取（`NtQuerySystemInformation(SystemProcessInformation)`），不再打开游戏进程句柄。`game_processes[].started_at_utc` 的取值与格式不变，schema 改为引用 `NullableUtcTimestamp`；`game_selection_reason` 补上 `"type": "string"`。
- `selection_token` 比较不区分大小写。`SelectGameProcess` 缺少 `process_id` 时 `field` 改为 `payload.process_id`，与其他缺失字段一致。
- `SelectGameProcess` 的所有拒绝都发生在停止旧采集之前；旧采集停止之后选择一定提交。所选客户端若在切换过程中退出，应答为 `ERR_FFXIV_NOT_RUNNING`，状态显示 `EXITED` 并等待重新选择，旧客户端不会被悄悄重新接上；同一瞬间开始的采集验证跟随新的选择。各错误码与 `retryable` 见 contracts/error-codes.md。
- `StartCapture` 在有客户端等待选择时返回的 `ERR_FFXIV_NOT_RUNNING` 由 `retryable = false` 改为 `true`：选择之后原样重发即可成功，与该错误码的约定一致。
- `CaptureStatus.ingress` 新增可选计数 `damaged_game_directions`：因序列缺口而放弃的游戏连接方向数。旧桌面端忽略即可。
- 补记 2026-10-02 起的含义变化：`ffxiv_running` / `ffxiv_process_id` 只描述已锁定的客户端，等待选择时为 `false` / `null`，候选客户端只出现在 `game_processes` 中；`StartCapture.process_id` 只能等于已锁定的客户端。

## 2026-10-03 · 备注搜索与单开重启接续

- `RunFilter.text` 同时匹配 `duty_name`、`job_name` 和 `note`，与历史页搜索提示一致；通配符字符仍按字面匹配。请求与响应结构不变。
- 已选择的客户端退出后，同路径中新启动的唯一客户端可自动接续。原本已打开的其他客户端不会接替；多开或身份不明确时仍等待选择。选择仍只保存在内存。

## 2026-10-02 · 多开客户端选择与锁定

- 新增 `SelectGameProcess`：请求带 `process_id` 和 `selection_token`，响应为 `CaptureStatus`。
- `CaptureStatus` 增加可选字段 `game_selection_required`、`game_selection_reason` 和 `game_processes`。候选项只含进程编号、启动时间与本次服务运行中的随机选择令牌，不包含窗口标题、角色名或安装路径。
- 令牌绑定进程编号和完整启动时间。客户端退出、编号被复用或服务重启后，旧令牌失效；选择失效客户端返回 `ERR_FFXIV_NOT_RUNNING`。启动时间不可读时暂停，不能只凭编号继续记录。
- 切换先停止旧采集、排空队列并结束会话；仍未释放资源时拒绝切换。采集验证占用监听资源时也拒绝切换。
- `StartCapture.process_id` 保留兼容，但必须与当前锁定的客户端一致；改变目标必须通过 `SelectGameProcess`，避免抓包目标与区服、版本、协议档案来自不同客户端。
- 单个客户端自动锁定；首次发现多个客户端时等待选择。被选客户端退出后保持暂停，其他客户端不会自动接替。选择只保存在内存，不跨采集服务重启保存。

## 2026-09-21 · 「立即检查」的最短间隔：`CheckSharedCalibration.outcome` 新增 `RECENTLY_CHECKED`（附加）

附加式变更：一个既有枚举新增一个取值，字段与消息数目均不变。**消息数目不变**：`$defs/MessageType` 仍为 49 个业务消息 + `Event` + `Error`。

- **`CheckSharedCalibration` 响应的 `outcome` 新增 `RECENTLY_CHECKED`**：此前手动检查只在已有下载正在进行时受限，上一轮一结束就能立刻再发起一整轮获取（索引最多 3 个源，加上每个校准码最多 3 个源乘最多 8 个候选）。现在两次手动检查之间有一个只存在于内存、不落盘的最短间隔（不影响「游戏更新后自动获取」原有的六小时节流）。间隔之内的请求答 `RECENTLY_CHECKED`。
- 为什么不复用 `ALREADY_FETCHING`：那个取值意味着确实有一次下载正在进行，桌面端据此提示「结果会显示在校准卡片上」；而被间隔拒绝时并没有下载正在进行，上一轮的结果早已出来，沿用那句话会让人等一个不会到来的结果。
- 1.4.0 之前的桌面端不认识这个取值，会落到它的兜底分支（提示「现在不需要获取」），不会报错；采集服务与桌面端随同一版本一起发布。

## 2026-09-20 · 共享校准在用时也读索引：`CalibrationStatus.shared` 新增 `recheck`（附加）

docs/plans/shared-calibration-rollback.md §3。全部为附加式：一个新的可选对象字段与两个新的 `$defs`，缺席或为 `null` 即“未发生过”，旧桌面端忽略即可。**消息数目不变**：`$defs/MessageType` 仍为 49 个业务消息 + `Event` + `Error`。

- **`$defs/SharedCalibrationStatus` 新增可选 `recheck`**（对象或 `null`）：已有档案在记录时仍读取索引的那一次。`last_utc` 为读取时刻，`status` 与 `last_fetch_status` 同一套取值（`$defs/SharedFetchStatus`），`reason` 为新枚举 `$defs/SharedRecheckReason`：`SHARED_IN_USE`（在用的是其他玩家分享的校准）与 `QUEUE_INFERRED_IN_USE`（在用的档案按排本推断匹配）。1.4.0 之前的采集服务不发送该字段。同一对象同时出现在脱敏诊断报告的 `calibration.shared` 中（docs/capture-diagnostics.md §9.6），不含地址、主机名与校准码。
- 行为变化（不在字段上）：`CheckSharedCalibration` 在已有可用档案时不再一律答 `NOT_NEEDED`——在用的档案来自共享校准、或按排本推断匹配时答 `STARTED`。取值集合未变。

## 2026-09-20 — 重新校准与回退：`DiscardCalibration` 新增 `retire_local_profile` / `restore_local_profile`，`CalibrationStatus` 新增 `retired_local_profile_available`（附加）

附加式变更：一个新的可选请求字段，缺省 `false` 即旧行为，旧桌面端不发它，旧采集服务按契约未声明字段拒绝。**不新增消息类型**，`$defs/MessageType` 仍为 49 个业务消息 + `Event` + `Error`。

- **`DiscardCalibration` 请求载荷**（原为 `$defs/EmptyPayload`，现为 `$defs/DiscardCalibrationRequest`）新增可选布尔字段 `retire_local_profile`，缺省 `false`。
  置 `true` 时，若当前生效的是本机校准档案（`profile_origin = LOCAL_CALIBRATION`），则先停用它：文件改名保留（后缀 `.json.retired`，不删除），
  正在进行的记录按停止捕获收尾，校准在当前会话内重新开始观察，其后可以再次导入校准码。
- 与流量证伪后的自动撤下不同：它生成的记录**不**标为待复核，它所认的匹配报文也**不**被拒绝——是玩家要求重新校准，而不是流量证伪了它。
- 当前生效的不是本机校准档案（随包档案、共享校准，或未选中任何档案）时，`true` 与 `false` 等价，即仅重新观察。应答仍为 `{state}`。
- **`DiscardCalibration` 请求载荷**再新增可选布尔字段 `restore_local_profile`，缺省 `false`：把 `retire_local_profile` 停用的那份档案
  （`<profile_id>.json.retired`）改回 `.json` 并立即用它记录，同时放开当前生效的档案。正在使用的共享校准只是被让位：
  不计作被证伪，其校准码不被拒绝，`user_rejected` 不变，已生成的记录也不标待复核。不丢弃校准证据。
  流量证伪后自动撤下的档案（`.json.contradicted`）永远不可恢复。
- `retire_local_profile` 与 `restore_local_profile` 互斥：同时为真答 `ERR_BAD_REQUEST`。
  无可恢复、同名档案已存在、或恢复后无法通过档案校验时，答 `ERR_CALIBRATION_NOT_READY` 并带上可直接展示的中文说明。
- **`$defs/CalibrationStatus` 新增可选 `retired_local_profile_available`**（布尔）：被停用的本机校准还在磁盘上、
  且当前没有本机档案生效时为 `true`，桌面端据此决定是否展示「恢复上一份本机校准」。
  旧采集服务不发该字段，缺席等同于 `false`。同一字段一并出现在脱敏诊断报告的 `calibration` 对象里
  （只是一个文件名事实，不涉及玩家打过什么、也不涉及协议）。

## 2026-09-20 — 再次匹配也播报：`run_state_changed` 新增可选 `match_offer`（附加）

附加式变更：一个新的可选字段，旧桌面端忽略它，旧采集服务不发它。**不新增消息类型**，`$defs/MessageType` 仍为 49 个业务消息 + `Event` + `Error`。

- **事件载荷新增可选 `match_offer`**（整数，自 1 起）：仅出现在 `state = MENTOR_MATCHED` 的 `run_state_changed` 上，表示这是该条记录的第几次匹配。
  有人取消后任务搜索器重新组队，匹配再次弹出时仍是同一条记录、同一个状态，此前因此不发任何事件，桌面端也就无从播报。
  现在同一条记录内与上一次匹配相隔 10 秒及以上的再次匹配（同一次匹配连发的 3–4 条相隔不到 1 秒，不算）会再发一次 `MENTOR_MATCHED`，`match_offer` 加一；
  桌面端的重复播报保护按「记录 + 状态 + `match_offer`」去重，因此会再播报一次，而断线重连后的重放仍然静默。
- 行为变化（不在字段上）：匹配窗口过后再次匹配时，状态机在同一步内收尾旧记录并开启新记录，前后状态同为 `MENTOR_MATCHED`；
  此前同样不发事件，现在记录变化也会发出 `run_state_changed`（`match_offer = 1`）。

## 2026-09-19 — 「匹配成功」报文：`CaptureStatus.last_valid_event_kind` 新增 `MATCH_ANNOUNCED`（附加）

docs/plans/timed-announcement.md。全部为附加式变更：只向一个已有的枚举里加了一个取值，旧桌面端按该字段原本的约定将不认识的令牌当作「某个事件」处理，只显示时间。**不新增消息类型**，`$defs/MessageType` 仍为 49 个业务消息 + `Event` + `Error`。

- **`CaptureStatus.last_valid_event_kind`** 新增取值 `MATCH_ANNOUNCED`：国服 2026.09.15 客户端的匹配通知在任何字节位置都不带轮盘编号，只能按出现时机认出；档案里记为可选消息 `MATCH_ANNOUNCED`，解析器产生同名语义事件。该报文只提供「匹配成功」的时刻，所排的轮盘仍由玩家自己发出的排本申请确定。
- 行为变化（不在字段上）：按排本申请推断匹配的档案，若另带有 `MATCH_ANNOUNCED`，则由它开启的那一次 `MENTOR_MATCHED` 状态事件发 `match_from_queue = false`（字段早已存在），桌面端现有逻辑据此播报「匹配成功」；其余状态仍为 `true`。
- 脱敏诊断报告（不属于 IPC 契约，同步记在这里）：`calibration.evidence` 新增 `timed_candidates[]` 与 `timing_overflow`，只含 opcode、长度、计数与秒数。报告版本仍为 2（只增不删）。

## 2026-09-18 — 更新检查：`CheckUpdateNow`（一条新消息）与启动时检查

docs/privacy-boundary.md §8.4 的两处放宽：用户可以手动检查一次；每次进程启动检查一次。

- **新消息 `CheckUpdateNow {}`**（`$defs/MessageType` 现共 **49** 个业务消息 + `Event` + `Error`）→
  `{ outcome, update }`：`outcome` 为 `CHECKED`（发出了一次检查，或等到了正在进行的那次）、`DISABLED`（`update_check_enabled` 关闭，不发请求）、
  `BLOCKED`（`MR_DISABLE_UPDATE_CHECK` 生效，不发请求）；`update` 与 `CollectorStatus.update` 同一对象（`$defs/UpdateStatus`），
  `last_outcome` 说明这次检查怎么结束。**异步应答**：与 `SynthesizeSpeech` 同类，请求发出后同一连接上的其他请求照常应答，
  应答按 `request_id` 配对；不受每日节流限制。空载荷。
- 行为变化（不在字段上）：采集服务每个进程启动后第一次应答 `GetStatus` 时检查一次（上次检查距今不足 1 小时则不检查，防止反复重启变成反复请求），
  之后仍是每 24 小时最多一次。

## 2026-09-17 — 共享校准：核实门槛按来源分级（附加）

docs/plans/shared-calibration.md §18。全部为附加式变更：新字段在 schema 中都是可选的，旧桌面端收到的应答照常通过校验，
旧采集服务不发这些字段，桌面端按"未报告"处理。**不新增消息类型**，`$defs/MessageType` 仍为 48 个业务消息 + `Event` + `Error`。

- **`$defs/SharedCandidateProvenance`**（新枚举）：`PUBLISHED`（本机最近一次读到的索引列出了这份码——下载来的，或粘贴导入后在索引里找到的；
  登录簇核实通过即绑定，排本与进本在记录中继续核对）与 `IMPORTED`（粘贴导入、任何索引都不认识；排本与进本都核实通过才记录）。
- **`SharedCalibrationCandidate`** 新增可选 `provenance`（可为 null：从磁盘恢复、码未能还原的档案）与 `audit_pending`
  （已绑定或可绑定，但仍有绑定后核对的判据在等待）。
- **`SharedCalibrationCriterion`** 新增可选 `gate`（`REQUIRED` / `AUDIT` / `OPTIONAL`）：该判据是绑定前必过、绑定后核对，还是可选（职业报文）。
- **`SharedCalibrationStatus`** 新增可选 `audit_pending`：正在使用的共享档案仍有绑定后核对的判据在等待。
- **`ImportCalibrationCode` 应答**新增可选 `provenance`（`APPLIED` 时为 `PUBLISHED` 或 `IMPORTED`，其他情况为 null）；
  `reason` 新增取值 `REVOKED`（本机最近一次读到的索引已撤回这份码，`NOT_APPLICABLE`）。
- 行为变化（不在字段上）：共享档案被撤下（对不上或被撤回）时，它自绑定起生成的记录以系统修订标记 `pending_review`，
  桌面端会收到对应的 `RunUpdated` 与统计失效事件；玩家自己选择「不用共享的」不标记。

## 2026-09-17 — 更新检查：`CollectorStatus.update`、`update_check_enabled`（附加）

只提示、不下载的更新检查（docs/privacy-boundary.md §8.4）。全部为附加式变更：新字段在 schema 中都是可选的，
旧桌面端收到的应答照常通过校验。**不新增消息类型**，`$defs/MessageType` 仍为 48 个业务消息 + `Event` + `Error`。
任何字段都不含主机名、URL、IP 或负载字节，唯一的例外是 `release_url`，它与
`GetCalibrationShareCode.issue_url` 同类，是一个交给浏览器打开的固定公开页面地址。

- **`CollectorStatus.update`**（可选，`GetStatus` 的应答）：
  `enabled`（采集设置 `update_check_enabled` 的当前值）、`update_available`、`latest_version`（未取到时为 null）、
  `release_url`（固定为公开发布页 `https://github.com/Harendotes-Tang/MentorRouletteRecorder/releases/latest`，
  不随检查结果变化）、`last_checked_at_utc`（本进程实际发出过请求的最近一次时间，从未发出时为 null）、
  `last_outcome`（该次检查的结果令牌）。检查只在采集服务应答 `GetStatus` 时顺带发起，因此只有桌面端连接并轮询时才会发生；
  同一进程内每 24 小时最多一次，失败一律静默，桌面端据此不显示任何内容。旧采集服务不发这一项，桌面端按"未报告"处理。
- **`CaptureSettings` / `UpdateCaptureSettingsRequest`** 新增 `update_check_enabled`（默认 true）：
  关闭后不再发出任何请求，`CollectorStatus.update.enabled` 随之为 false。采集服务持久化为 `update.check_enabled`。
- 脱敏诊断报告（不属于 IPC 契约，同步记在这里）：`boundary.outbound.update_check =
  {enabled, kill_switch, last_checked_utc, last_outcome, latest_version}`，不含主机名、地址与请求头。
  报告版本仍为 2（只增不删）。

## 2026-09-16 — 在线语音（三条新消息）、`CheckDatabaseIntegrity`、`CaptureStatus.last_valid_event_kind`（附加）

界面改版 P4b 与决策 4、5。全部为附加式变更，旧桌面端收到的应答照常通过校验。
隐私边界已先行修订：在线语音是第二类出站请求，默认关闭（docs/privacy-boundary.md §8.3）。

- **新消息**（`$defs/MessageType` 现共 48 个业务消息 + `Event` + `Error`）：
  - `GetSpeechSettings {}` → `$defs/SpeechSettings`：`provider`（`none` / `azure` / `openai_compatible`）、`azure_region`、
    `openai_base_url`、`openai_model`、`voice`、`has_key`、`configured`、`target_host`（播报文字会发往的主机名，未配置时为 null）、
    `azure_voices[]` 与 `openai_voices[]`（`{name, label}`，设置页的内置音色清单）。**没有密钥字段。**
  - `UpdateSpeechSettings`（`$defs/UpdateSpeechSettingsRequest`，全部可选）→ 同上。四个字符串字段给 null 或 `""` 表示清除；
    `api_key` 只写：省略 = 不变，`""` = 删除，其他值用 DPAPI（当前用户）加密存进 `speech-key.bin`，并与这次更新之后的目标
    （Azure 主机，或完整的 OpenAI 兼容地址）绑定。换服务、换区域或换地址而不带新密钥时，旧密钥被删除。
    所有字段先校验再写入，不合法是 `ERR_BAD_REQUEST`，`field` 指出哪一项。
  - `SynthesizeSpeech {text, rate_percent?, test?}` → `{audio_path, from_cache, provider}`。`text` 1–200 字，`rate_percent`
    50–200（默认 100），`test = true` 跳过缓存读取，但仍会写缓存。**异步应答**：在同一连接上，这条消息等待期间其他请求照常应答，
    应答按 `request_id` 配对，客户端应至少等待 20 秒。`audio_path` 在数据目录的 `tts-cache\` 下，是 44 字节头的 16 位 PCM WAV。
    同一时间只发一个请求，最多三句排队。第五句立即 `ERR_SPEECH_TIMEOUT`（`details.reason = QUEUE_FULL`），排队超过 8 秒为
    `QUEUE_WAIT`。
  - `CheckDatabaseIntegrity {}` → `{passed, detail, checked_at_utc}`：采集服务在自己的一条只读连接上执行 `PRAGMA integrity_check`
    （不占用实时写入的数据库闸门，抓包不受影响），`detail` 为 `ok` 或第一条问题（经日志脱敏、≤ 200 字）。未通过时
    `passed = false`，不是错误；数据库被占用时返回 `ERR_DB_BUSY`。**异步应答**（2026-09-17 起，与 `SynthesizeSpeech` 同一机制）：
    大型数据库的全表扫描可能耗时数秒，期间同一连接上的其他请求照常应答，应答按 `request_id` 配对。
- **新错误码**（contracts/error-codes.md）：`ERR_SPEECH_DISABLED`、`ERR_SPEECH_NOT_CONFIGURED`、`ERR_SPEECH_AUTH`、
  `ERR_SPEECH_QUOTA`、`ERR_SPEECH_NETWORK`、`ERR_SPEECH_TIMEOUT`、`ERR_SPEECH_FORMAT`。`details` 最多带 `http_status` 与大写
  `reason`，从不带语音服务的响应正文。
- **`CaptureStatus.last_valid_event_kind`**（可选，可为 null）：`last_valid_event_at_utc` 那一条事件的语义类型，同时设置，取值
  `CONTENT_FINDER_POP` / `ZONE_INITIALIZATION` / `ZONE_TERRITORY` / `DUTY_RESULT` / `PLAYER_JOB` / `ZONE_LEFT` /
  `INSTANCE_LEFT` / `MATCH_CANCELLED`。界面据此显示"21:38:04 副本结算"。0.8 之前的采集服务不发这一项，桌面端按"未报告"处理。
  无法识别的取值按"某个事件"处理。
- 脱敏诊断报告（不属于 IPC 契约，同步记在这里）：`run.last_valid_event_kind`；`boundary.outbound.online_speech =
  {provider, kill_switch, last_request_utc, last_outcome}`，不含主机、地址、音色、文字或密钥。报告版本仍为 2（只增不删）。
- 四条新消息的请求样本（`tests/Fixtures/ipc-requests/`）先由采集服务侧写出，桌面端包装器实现时必须生成逐字相同的载荷。

## 2026-09-15 — 共享校准：`CalibrationStatus.shared`、五条新消息、`SHARED_CALIBRATION`、`shared_calibration_enabled`（附加）

其他玩家分享、并在本机流量里核实过的校准。全部为附加式变更：
新字段在 schema 里都是可选的，旧桌面端收到的应答照常通过校验。任何字段都不含 URL、主机名、IP 或负载字节。
唯一的例外是 `GetCalibrationShareCode.issue_url`，它是交给浏览器打开的公开 Issue 表单地址。

- **`CaptureStatus.profile_origin`** 新增 `SHARED_CALIBRATION`：生效档案来自其他玩家分享的校准。
  此前 Collector 把这种来源映射成 null；诊断报告的 `profile.origin` 同步。
- **`CalibrationStatus.shared`**（可选，`$defs/SharedCalibrationStatus`）：
  `phase`（`NONE` / `FETCHING` / `UNAVAILABLE` / `VERIFYING` / `AWAITING_CONSENT` / `VERIFIED` / `REJECTED`）、
  `candidates[]`（`sha12`、`source` = `DOWNLOADED` | `MANUAL` | null、`match_source`、`status`、`verdict`、
  `criteria[]` = `{message, verdict, reason, contradicting_sessions}`、`staging_overflowed`；生效中的共享档案排第一、
  核实中的其次、被拒绝的最后）、`last_fetch_status`、`last_index_attempts[]` = `{source, outcome}`
  （`source` 为 `GITHUB_RAW` / `CDN_PRIMARY` / `CDN_FALLBACK`）、`profile_id`、`bound_at_utc`、`last_refusal`（大写短令牌）、
  `rejected_candidates`、`user_rejected`。`user_rejected = true` 表示用户选择了「不用共享的，我自己校准」，此时 `phase = REJECTED`。
  卡片应当显示"已按你的选择改为本机校准"，而不是"对不上"。
- **`CaptureSettings` / `UpdateCaptureSettingsRequest`** 新增 `shared_calibration_enabled`（默认 true）：
  关闭该项会取消进行中的获取并丢弃已下载的候选。已核实生效的共享档案不受影响，手动导入也不受它限制。
- **新消息**（`$defs/MessageType` 现共 44 个业务消息 + `Event` + `Error`）：
  - `GetCalibrationShareCode {}` → `{code, code_sha256, issue_url, code_in_url}`：只对生效中的**本机校准**档案有效，
    校准码由磁盘上的档案文件反推得出。`issue_url` 预填标题与校准码；地址超过 7500 字符时不带码、`code_in_url = false`，
    桌面端改为只复制校准码。无法给出校准码时返回新错误码 `ERR_SHARE_CODE_UNAVAILABLE`，`details.reason` 为
    `NO_PROFILE` / `NOT_LOCAL` / `SHARED` / `NOT_SHAREABLE`。
  - `CheckSharedCalibration {}`（立即检查）→ `{outcome}`：`STARTED` / `ALREADY_FETCHING` / `DISABLED` / `NOT_NEEDED`。
    已有可用档案或用户拒绝过共享时返回 `NOT_NEEDED`，不发出任何请求。
  - `ImportCalibrationCode {code}`（导入校准码）→ `{outcome, reason, message, code_sha256}`：
    `outcome` 为 `APPLIED` / `NOT_APPLICABLE` / `MALFORMED`；`reason` 是大写令牌（码本身的 `E_SHARE_CODE_*` 拒绝码，
    或 `NOT_CALIBRATING` / `OTHER_REGION` / `OTHER_BUILD` / `OTHER_TEMPLATE` / `USER_REJECTED` / `REJECTED` /
    `TOO_MANY_CANDIDATES` / `CHANGED` / `UNBUILDABLE`），应用成功时为 null；`message` 是面向用户的中文整句，不含十六进制；
    `code_sha256` 在无法解码时为 null。`code` 请求上限 65536 字符，超过校准码长度上限（4096）时不解码，直接返回 `MALFORMED`。
  - `AcceptSharedQueueInference {}` → `{outcome}`：`ACCEPTED` / `NOTHING_TO_ACCEPT`。
  - `RejectSharedCalibration {}`（不用共享的，我自己校准）→ `{withdrawn_profile_id, dropped_candidates}`：
    撤下生效中的共享档案（删除文件、重新选择、重新准备校准），丢弃正在核实的候选，并把"用户拒绝"与矛盾记录分开存放。
    在「重新观察」（`DiscardCalibration`）之前，该区服与版本不再获取、导入或绑定任何共享校准。本机校准不受影响。
    游戏版本未知时不执行任何动作，应答 `{null, 0}`。
- **新错误码** `ERR_SHARE_CODE_UNAVAILABLE`（contracts/error-codes.md）。
- **`calibration_changed`** 在共享校准阶段、候选状态或 `user_rejected` 变化时同样发出，载荷不变；桌面端照旧重新读 `GetCaptureStatus`。
- 五条新消息的请求样本（`tests/Fixtures/ipc-requests/`）先由 Collector 侧写出，桌面端包装器在阶段 C 实现时必须生成逐字相同的载荷。

## 2026-09-15 — `CaptureStatus.calibration.progress.job_seen`（附加，可选）

新增可选布尔字段，表示“职业报文已经认出来”。职业报文对档案而言是可选的，校准可以在没有它的情况下完成。
但在它为 true 之前，每条记录的职业都会写成未知，只能在结束时的结果弹窗里补录。校准卡片为此新增“职业”
一行，说明当前的职业识别状态。`required` 未变，0.7.11 之前的 Collector 不发这一项，桌面端按缺席处理。

## 2026-09-14 — `LiveEvent.match_from_queue`（附加，可选）

`run_state_changed` 携带当前状态机绑定档案的匹配来源：`true` 表示以客户端排队请求为起点，
`false` 表示服务器匹配通知。来源随事件保留，重连重放、校准状态刷新或档案更新均不改变它。
桌面端只有收到明确的 `false` 才播报“匹配成功”。旧事件缺少此字段时不播报匹配，进本与结束播报照常。

## 2026-09-13 — `CreateManualRun.duration_ms` 区分省略与显式空值

新建记录时，省略 `duration_ms` 仍按进本、结束时间推算；显式传入 `null` 表示耗时未知，
保持空值并排除在平均耗时之外，与 `CorrectRun.changes.duration_ms` 的空值语义一致。
幂等指纹区分这两种请求；非空数值及省略字段的已有行为不变。未增加字段或数据库列。

## 2026-09-10 — `CaptureStatus.calibration.progress.duty_zone_seen`（附加，可选）

新增可选布尔字段，表示“看到过进入已知副本的换区”，与匹配弹窗是否对得上无关。
`duty_entry_seen` 仍是更严格的一项：只有当某次换区能佐证匹配弹窗时才为 true。
有了这一项，即使尚未与排本对应，“进本”一行也可以如实显示“已看到，还没能和排本对上”，
而不是四行全部显示“还没见到”。`required` 未变，0.3.5 之前的 Collector 不发这一项，桌面端按缺席处理。

## 2026-09-10 — `CaptureStatus.calibration.progress.pop_shape_seen`（附加，可选）

新增可选布尔字段，表示曾见到模板弹窗的方向与长度，仅用于诊断，不代表匹配已经确认。
当它为 true 且 `pop_seen` 为 false 时，卡片显示“尚未确认，等待进本核对”。`required` 未变，
0.3.4 之前的 Collector 不发这一项，桌面端按缺席处理；`pop_seen` 仅在副本证据支持匹配后为 true。

## 2026-09-10 — 本机校准：`ConfirmCalibration` / `DiscardCalibration`、`CaptureStatus.calibration`、`calibration_changed`

游戏补丁会重排 opcode，随包档案随之失效。本条将"换版本后重新认出报文"改为在用户机器上完成。
契约面的变更如下，全部为附加式变更。

- **新消息** `ConfirmCalibration`：请求 `{verdicts: [{event_id, verdict: CORRECT|WRONG}]}`，
  必须覆盖 `calibration.events` 里每一条 `requires_confirmation = true` 的事件。全部 CORRECT →
  Collector 写出本机档案（`<数据目录>\protocol-profiles\<region>\<region>.<build>.local.json`，
  状态 VERIFIED，证据为 OBSERVED_LOCAL_TRAFFIC + USER_CONFIRMED），重新从磁盘读目录，并在当前
  抓包会话尚未绑定解析器时就地绑定；应答 `{profile_id, profile_path, bound_in_session}`。
  任一 WRONG → `ERR_CALIBRATION_REJECTED`，草稿作废、被否决的候选本会话内不再提出。
  没有可核对草稿 → `ERR_CALIBRATION_NOT_READY`。
- **新消息** `DiscardCalibration`：空请求，丢弃本次会话的校准证据重新观察；应答 `{state}`。
- **`CaptureStatus` 新增** `profile_origin`（`SHIPPED` | `LOCAL_CALIBRATION` | null）、
  `calibration_bound_at_utc`、`calibration`（`$defs/CalibrationStatus`：`state`、`game_build`、
  `template_profile_id`、`local_profile_id`、`bound_at_utc`、`blockers[]`、`progress`、`events[]`）。
  事件只含中文标签、轮盘编号、区域编号与副本名，不含 opcode。
- **`CaptureSettings` / `UpdateCaptureSettingsRequest` 新增** `auto_calibration_enabled`（默认 true）。
- **新事件 kind** `calibration_changed`（`event_type = CalibrationChanged`），载荷
  `calibration_state` 与 `ready`；不推送时间线，桌面端收到后重新 `GetCaptureStatus`。
- **新错误码** `ERR_CALIBRATION_NOT_READY`、`ERR_CALIBRATION_REJECTED`（contracts/error-codes.md）。
- **0.3.1 追加**：`CalibrationState` 新增 `WAITING`（有模板、无抓包会话）；
  `CaptureStatus.oodle_signature_source` 新增 `pattern-fallback`（同区服最新 VERIFIED 签名档案的
  模式在新 exe 上重扫成功）。两者均为附加式变更。
- 校准期间 `StartCapture` 不再因 `UNSUPPORTED_BUILD` 返回 `ERR_PROFILE_UNSUPPORTED`。
  前提是随包目录里有同区服、带 `calibration` 段的 VERIFIED 模板，且选择结果确实是"没有档案匹配"。
  目录歧义与加载失败仍然 fail-closed。

## 2026-09-08 — 补记既有消息与事件；研究负载上限 256 → 512；`run_state_changed` 带 `run`（第 22 条）

本条**补记**四组早已存在于 schema 与实现、却漏记在本文件的契约面，另有一处真实的语义变化。
契约与实现的一致性仍由 `tests/Collector.IntegrationTests/ContractSchemaTests.cs`
与 `ContractRequestSampleTests.cs`（每个 `message_type` 至少一条请求样本 + 一条应答/错误例）
以及 `tests/Desktop.Tests/IpcRequestTests.cpp` 强制。

**补记的消息**（`$defs/MessageType` 现共 **37** 个业务消息 + `Event` + `Error`）：

- `UndoRevision`：撤销一条记录**最新**的那条修订。实现方式是追加一条新修订，把被撤销修订的
  `old_value` 逐字段写回，被撤销的那条依然留在链上。请求为
  `{run_id, expected_revision, reason}`（`reason` 必填），应答与其他变更消息一致
  （`revision` / `audit_event_id` / 幂等）。`revision = 1` 是创建记录本身，不可撤销，
  返回新的 `ERR_UNDO_NOT_ALLOWED`（见下）。
- `GetRunEvents`：只读地取回一条记录的事件轨迹（原 release-checklist §5 的缺口 G1）。
- `GetCaptureSettings`：只读地取回当前抓包设置（请求为空对象，应答是 `$defs/CaptureSettings`）。
  它与 2026-09-05 已记过的 `UpdateCaptureSettings` 共用同一个设置对象。

**补记的事件 kind**（`$defs/LiveEvent.kind` 现共 8 个）：

- `run_finished`：一条记录已经收尾。它从**存储行**（`ended_at_utc`）判定，而不是从
  状态机的当前状态，因此"收尾旧记录 + 同一事件里开出新记录"也能各发一条。
- `heartbeat`：空闲订阅上的心跳帧，用于表明订阅仍然有效。它**不受** `event_types`
  过滤器影响。

**补记的订阅参数**：`$defs/SubscribeLiveEventsRequest` 的 `event_types`（字符串数组，
为空或省略表示全部；无法识别的取值会把一切都过滤掉，而不是被忽略）与
`heartbeat_interval_ms`（1000–60000，默认 5000，在订阅应答里原样回显）。

**研究负载上限 256 → 512 字节。** `UpdateCaptureSettings / CaptureSettings` 的
`research_payload_opcodes` 白名单准入条件由"长度上界不超过 256 字节"改为
**不超过 512 字节**，实际负载超过 512 字节时整段不保存、不截取前缀。数据库侧由
`migrations/0007_candidate_research_payload_512.sql` 同步（schema 7）。本条取代
2026-09-05「MRR-PASSIVE-VALIDATION 4.3」里的 256。

**真实的语义变化：`run_state_changed` 现在带 `run`。** `$defs/LiveEvent` 一直允许任何事件
携带 `run`，但 Collector 发布状态变化时只写 `state`。桌面端的进本播报读取的正是
`event.run`，因此每次都播报为"进入 未知副本"。现在存在进行中的记录时，该事件带上**变化之后**
的那条记录。schema 未变，旧客户端不受影响。

由 `tests/Collector.IntegrationTests/{ContractSchemaTests,ContractRequestSampleTests}.cs`
（`ContractSchema.BusinessMessageTypes` 从 `$defs/MessageType` 读出这 37 个名字，
覆盖断言逐条核对）与 `tests/Desktop.Tests/IpcRequestTests.cpp` 持续强制；
`ContractSchemaTests.cs` 的类注释不再写死数字，改为指向 `BusinessMessageTypes`。

## 2026-09-08 — 新增 `ERR_UNDO_NOT_ALLOWED`；退出码 6；幂等结果过期（第 21.1 条）

`$defs/ErrorPayload/properties/code` 新增 **`ERR_UNDO_NOT_ALLOWED`**：`UndoRevision`
指向的是 `revision = 1`（创建记录本身）。此前这里返回 `ERR_BAD_REQUEST`，无法与请求格式错误
区分。`field = "expected_revision"`，`details.run_id` 给出记录标识。

**新的进程退出码 6**（与 `ERR_ALREADY_RUNNING` 同源，同样不经由 IPC 返回）：本机已有一个
Collector 占着同一条管道，**且那条管道确实存在、还能接连接**，但它连续 6 次、每次 500 ms
（合计 ≥ 3 秒）都**不响应** `GetVersion` 探活。4 表示应当连接正在运行的实例，6 表示该实例
已无法服务。收到 6 时，调用方应当先通过 `Local\<管道名>.stop` 请求它停止并等待数秒，
仍未退出再将其结束。**租约被占着却没有管道**（对方正在启动或正在停止）与**管道实例已被占满**
（对方正在服务别的客户端）都仍然返回 4。若这两种情形返回 6，调用方会去结束一个健康的、
或正在优雅收尾的实例。占用者的进程号写在日志目录下的 `serve.pid`；`--pipe` 指定了自定义管道名时写的是
`serve.<管道名>.pid`，而 `serve.pid` 只在迁移、崩溃恢复与管道服务全部就绪之后才写下、
停止时第一个删除。详见 contracts/error-codes.md。

**`ERR_IDEMPOTENCY_CONFLICT` 多了一种 `details.reason`。** 幂等表按 24 小时清理，此后同一个
`request_id` 的迟到重放既查不到幂等行、又会撞上 `run_revisions.request_id` /
`candidate_reviews.request_id` 的 UNIQUE 约束，原本兜底成 `ERR_INTERNAL`。现在返回
`ERR_IDEMPOTENCY_CONFLICT`，`details = {conflict: "idempotency", request_id, reason:
"RESPONSE_EXPIRED"}`，`field = "request_id"`，客户端应换用新的 `request_id` 重发。
**没有新增错误码**，只多了 `details.reason` 这一个可选取值。

**`ERR_BAD_REQUEST` 多了一种来源。** 所选网卡在开始抓包时已经不在设备列表里（用户中途打开
加速器 / VPN、换了 Wi-Fi、重新拿到 IP）时，`StartCapture` 返回
`ERR_BAD_REQUEST`、`field = "adapter_id"`、`retryable = true`、`details.adapter_id` 为原来
那张网卡。此前它被翻译成 `ERR_NPCAP_MISSING`（"请重装 Npcap"），既误导用户，又让控制器按
30 秒退避重试。现在按就绪等待处理。

四项分别由以下用例保证：
`tests/Collector.UnitTests/MutationTests.cs`
（`UndoRevision_CannotUndoTheCreateRevision` 断言 `ErrorCodes.UndoNotAllowed`）、
`tests/Collector.IntegrationTests/LifecycleProcessTests.cs`
（`APipeHeldBySomethingThatNeverAnswersExitsWithItsOwnCode`）、
`tests/Collector.UnitTests/IdempotencyPruneTests.cs`
（`AReplayAfterThePruneIsAContractConflictRatherThanAnInternalError`）、
`tests/Collector.UnitTests/NpcapPacketTests.cs`
（`AVanishedAdapterIsABadRequestAboutTheAdapter_NotAMissingNpcap` 与
`TranslateKeepsTheVanishedAdapterRefusalIntact`）。

## 2026-09-08 — `CorrectRun.changes` 新增 `pending_review`（第 21 条）

`$defs/CorrectRunRequest.changes` 新增可选 `pending_review`，只接受 `false`：确认一条待复核记录。
此前桌面端"确认复核"按钮发送 `{pending_review: false}`，Collector 因字段未声明返回
`ERR_BAD_REQUEST`；即使声明，纯确认也会落入 `ERR_NO_CHANGES`。现在只带 `pending_review: false`
的更正是合法的（确认本身就是改动，会写入一条 `CORRECT` 修订并在 `changes_json` 里记录
`pending_review: true -> false`）；记录不处于待复核状态时仍返回 `ERR_NO_CHANGES`；
`pending_review: true` 返回 `ERR_BAD_REQUEST`。

## 2026-09-08 — 诊断报告不再覆盖不是自己生成的文件；新增 `ERR_ALREADY_RUNNING`（第 20 条）

`$defs/ExportDiagnosticsReportRequest` 新增可选 `overwrite`（布尔，默认 `false`）。
此前该导出无条件以 `overwrite: true` 落盘，因此 `ExportDiagnosticsReport
{target_path: "D:\论文.docx"}` 会直接覆盖该文件，而这只需文件对话框中的一次误操作。
现在只有**本程序自己生成的文件名** `diag_<yyyyMMdd_HHmm>.json` 会被就地替换：一份报告
本身就是一次新的观测，同一分钟内导出两次会落到同一个文件名上，拒绝第二次没有意义。
其余目标文件已存在时返回 `ERR_EXPORT_FAILED`，除非请求里显式带上 `overwrite: true`。
这与 `ExportCsv` / `ExportJson` / `BackupDatabase` 的 `overwrite` 语义一致。
旧客户端不受影响，不传该字段即为默认的"拒绝覆盖"。

`target_path` 的说明同步更正。2026-09-07 已经放开"必须位于用户配置目录之内"的限制，
该行文字此前漏改。

`$defs/ErrorPayload/properties/code` 新增 `ERR_ALREADY_RUNNING`：本机已有一个
Collector 在服务同一条管道时，新进程拒绝启动。它**不经由 IPC 返回**，只出现在
`--serve` 的 stderr 与本机日志里，但错误码枚举是规范来源，因此一并声明。
进程退出码为 4，与表示本机数据异常的 3 区分开。4 是正常结果，
调用方应当连接正在运行的那一个实例。详见 contracts/error-codes.md。

## 2026-09-08 — 抓包"在跑却什么都没记"的可诊断字段（第 19 条）

`$defs/CaptureStatus` 新增可选字段 `raw_packets_observed`、`preexisting_connections`、
`silent_reason` 与 `ingress` 对象（`dropped_no_stream`、`dropped_no_syn`、
`expired_streams`、`unconfirmed_tuples`、`stream_resets`、`adapter_dropped`）；
`$defs/CaptureAdapter` 新增可选 `preference_stale`。全部为附加，旧客户端不受影响。

起因：一次整晚的会话显示 `RUNNING`、档案已匹配、`0 次解析失败、没有有效事件`，
却没有产生任何记录。中途接入（本软件在游戏登录之后才开始监听）会让每一个报文在流重组阶段被
丢弃，`packets_observed` 与 `decode_errors` 因此恒为 0，界面上看不出异常。
`raw_packets_observed` 是网卡这一层的原始计数，用于区分"网卡上没有流量"与
"流量都被丢在重组阶段"。`ingress.*` 给出每一条丢弃路径的计数。

`silent_reason` 取 `NONE` / `MIDSTREAM` / `NO_PACKETS_ON_ADAPTER` / `NO_STREAM_OWNERSHIP`，
`hint` 同时给出对应的中文操作建议。`midstream_suspected` 语义不变，等价于
`silent_reason == "MIDSTREAM"`，保留给既有客户端。

`packets_dropped` 的含义扩展为"有界队列溢出 **或** Npcap 报告的驱动/网卡丢包"，
少量驱动丢包不再中止抓包，只把本次运行标记为 `DEGRADED`。

`preference_stale` 用于加速器/VPN 场景：记住的网卡已经不承载游戏流量而另一张承载时，
推荐会切换到承载流量的那一张，并把记住的那张标记为 `preference_stale`，供界面说明原因。

## 2026-09-08 — 未声明的 opcode 不再计为解析失败（第 18 条）

`$defs/CaptureStatus` 新增可选 `ignored_count`：档案没有声明的 `(direction, opcode)`
现在只计数，**不再**进入 `parse_fail_count`、`recent_parser_errors` 与 `parser_errors` 表。
国服档案只声明弹窗与换区两条消息，客户端每分钟发来的几百种其他报文原本全部被记成
`E_UNKNOWN_OPCODE`。诊断页因此在链路完全健康时显示"解析成功率 0.0%、4298 次失败"，
真正的拒绝（声明消息的长度、偏移、约束错误）被淹没。`parse_fail_count` 的含义收窄为
"声明消息被拒绝的次数"；`parse_ok_count`、`duplicate_count` 不变。桌面端把缺失的
`ignored_count` 渲染成 `—`。`E_UNKNOWN_OPCODE` 保留在 `ParserErrorEntry.code` 枚举里，
供已持久化的旧行与样本重放工具使用。

## 2026-09-07 — 允许选择任意可写本地导出目录

`ExportCsv`、`ExportJson`、`BackupDatabase`、`ExportDiagnosticsReport` 与
`ExportCandidateEvidence` 的 `target_path` 不再限于 `%USERPROFILE%` / `%LOCALAPPDATA%`。
支持用户选择的其他本地盘符目录；继续拒绝 UNC、网络映射盘、设备路径、备用数据流及最终落到
网络位置的链接。覆盖和写入失败语义不变，错误码仍为 `ERR_EXPORT_FAILED`，消息格式无变化。
本条替代下文历史条目中的个人目录限制。

## 2026-09-07 — 采样分段与簇成员入账规则

排本窗口上限从 15 分钟改为 2 小时；每条 finder 组报文（`FINDER_STATE_NOTIFICATION`）或
`FINDER_ACTION` 之后立即把当前分段写入账本并开始新分段，行格式不变。
`zone_load` 组的成员观测不再逐条入账：只在簇成立时写入窗口内成员各一行，簇存续期间新出现的
opcode 追加一行。契约字段无变化。

## 2026-09-06 — 排本窗口采样

`CandidateObservation` 增加必填整数 `occurrences`（≥ 1）。普通观测恒为 1；
新增假设名 `QUEUE_WINDOW_SAMPLE`（`group_name = queue_window`）的行把排队登记到下一次进本簇
之间同一 (方向, opcode, 长度) 的全部报文合并为一行：`t_ms / observed_at_utc` 为首次出现，
`payload_hash12` 为首个报文摘要，`payload_hex` 恒 null（采样不走研究白名单）。
迁移 0006 加列，默认 1。证据导出随行携带该字段；`derive_fields.py` 忽略未知键，不受影响。

候选档案 `cn.2026.08.05.candidate` 的 `QUEUE_CANCELLATION` 改名 `FINDER_ACTION`
（取消与确认进入共用），`FINDER_STATE_NOTIFICATION` 语义改为操作后的状态更新；
旧观测行保留旧名，桌面端把两者都归为“副本查找器操作”。

## 2026-09-06 — 候选假设清单与自动跟随默认值

`CaptureStatus` 增加 `candidate_hypotheses`：当前客户端匹配到的候选档案所声明的假设
（`$defs/CandidateHypothesis`：`profile_id / name / label / opcode(0x 四位) / direction(C2S|S2C) /
group / expected_length / research_eligible`），仅供桌面端按用途、用中文名显示研究白名单，
不影响匹配、资格判定或证据。游戏未运行且目录里恰有一份候选档案时也会列出；无匹配为空数组。
档案格式 `hypotheses[].label` 为可选显示名。

`CaptureSettings.follow_game` 未写入时的默认值从 `false` 改为 **`true`**：抓包必须先于登录，
默认让 Collector 随游戏进程出现自动开始。旧键 `capture.autostart` 只有显式 `true` 才被当作意图。
自动开始被拒绝后按 30 秒退避重试，不再每秒刷新 `last_error_*`。
`hint` 文案改为面向用户的恢复说明；字段语义不变。

## 2026-09-05 — MRR-PASSIVE-VALIDATION 4.3

`UpdateCaptureSettings / CaptureSettings` 增加 `research_payload_opcodes` 字符串数组，
默认空、最多 32 项，规范输出 0x 加四位小写十六进制。null、重复、未知、混淆、
长度上界超过 256 或未开启候选验证时加入非空白名单均返回 ERR_BAD_REQUEST，更新不落盘。
清空立即停止后续负载保存；仅关闭验证会保留历史证据和白名单配置。

普通观测查询与实时事件不携带负载。专用导出每项附加可空 `payload_hex`；文件头
`contains_raw_payload` 和 `research_payload_opcodes` 来自同一快照内实际保存的负载，
所以关闭研究后导出旧证据仍明确标记。迁移 0005 的列约束再次限制长度和十六进制格式。
隐私例外先于代码写入 docs/privacy-boundary.md §5.1。

## 2026-09-05 — MRR-PASSIVE-VALIDATION 4.2

新增 `QueryCandidateObservations`（会话/UTC 范围过滤，倒序分页，每页最多 200）、
`ReviewCandidateObservation`（CORRECT/WRONG/UNSURE，备注最多 2000 字，request_id 幂等并追加历史）
和 `ExportCandidateEvidence`（可选本地 target_path，返回 JSON 路径、SHA256 与 sidecar 路径）。
账本独立保留最多 20000 行 / 30 天，清理时关联核对历史一并清理；重复核对请求返回原结果，
以不同内容复用 request_id 返回 ERR_IDEMPOTENCY_CONFLICT。缺失或过期观测返回
`ERR_CANDIDATE_OBSERVATION_NOT_FOUND`；路径越界或覆盖已有证据返回 ERR_EXPORT_FAILED。

`CaptureStatus` 附加 `candidate_validation_enabled / candidate_profile_id / candidate_observation_count`
（计数为当前保留的全部会话观测）。新事件 `CandidateObserved` 的 kind 为 `candidate_observed`，
只携带 name/group/t_ms 和观测/会话 UUID；不会发出 run 或 stats_invalidated 事件。
证据导出格式 `MentorRecorder.CandidateEvidence` v1，带 CANDIDATE 状态及原始负载声明。

## 2026-09-05 — MRR-PASSIVE-VALIDATION 4.1

`UpdateCaptureSettings` 和 `CaptureSettings` 增加可选布尔 `candidate_validation_enabled`，
默认 false。开启仅允许独立候选观测，正式解析保持 fail-closed；普通会话运行中启用返回
`ERR_CAPTURE_ALREADY_RUNNING`，须先停止。关闭立即停止候选观测，恢复正式选档需重启捕获。

`protocol_version` 仍然是 **1**。本文件记录的每一条都是**附加性（additive）**的：
把契约改成描述两端**已经实现**的传输格式，而不是要求任何一端改变已发出的字节。
唯一的例外是 `ERR_IDEMPOTENCY_CONFLICT`（第 12 条）。该情形此前被错误地归入
`ERR_BAD_REQUEST`，现在有了专用错误码。

## 2026-09-05 — 导随心得 / run reflections（MRR-REFLECTIONS）

`protocol_version` 仍然是 **1**，全部是**附加性**的：新增两条消息、三个 `$defs`、
`$defs/Run` 的一个可选属性与 `$defs/RunFilter` 的一个可选过滤位。既有字段的名字、
类型与语义都没有变，旧客户端不改一行也能继续工作。**没有新增错误码**：
「记录不存在」沿用 `ERR_NOT_FOUND`，参数问题沿用 `ERR_BAD_REQUEST`。

对应实现：`migrations/0003_run_reflections.sql`（schema 版本 3）、
`src/Collector/Storage/Repositories/RunReflectionRepository.cs`、
`src/Collector/Ipc/ReflectionHandlers.cs`、`src/Collector/Export/RunExporter.cs`。
由 `tests/Collector.IntegrationTests/{ContractSchemaTests,ContractRequestSampleTests,ReflectionIpcTests}.cs`
与 `tests/Collector.UnitTests/ReflectionTests.cs` 持续强制。

18. **新增 `$defs/ReflectionMood`**（`good` / `ok` / `bad`）。小写，因为它是用户在界面上
    挑选的取值，而不是从报文里判定出来的领域枚举（与 `trend_granularity` 同一条理由）。
19. **新增 `$defs/RunReflection`**（`{mood, text, created_at_utc, updated_at_utc}`，
    `text` 长度 1–2000）与 **`$defs/ReflectionEntry`**（`{run, reflection}`）。
20. **`$defs/Run` 新增可选 `reflection`**（`RunReflection` 或 `null`，**不进 `required`**）。
    每一处序列化 Run 的地方都会带上它：`QueryRuns.items`、`GetCurrentRun.run`、
    `MutationResult.run`、`LiveEvent.run` 与 JSON 导出；没有心得时是 `null`。
21. **`$defs/RunFilter` 新增可选 `with_reflection`**（bool，默认 `false`）：只保留有心得的记录。
    与 `include_deleted` 一样，它对统计口径没有影响。
22. **新增消息 `SetRunReflection`**（`$defs/SetRunReflectionRequest` →
    `Responses/SetRunReflection`）：写入、替换或清空一条记录的心得。
    - 没有 `reason`，也没有 `expected_revision`。心得是用户自行记录的内容，不是对观测事实的
      更正，因此**不递增 `revision`、不写 `run_revisions`**，存放在独立的 `run_reflections` 表里。
    - `text` 由 Collector `Trim()`。trim 后为空表示**删除**这条心得，应答的 `reflection` 为 `null`。
    - 记录不存在 → `ERR_NOT_FOUND`；**软删除的记录仍可写入心得**。
    - 按 `request_id` 幂等（与其它变更消息同一张 `ipc_idempotency` 表、同一套指纹规则）。
    - 写入成功后发布 `stats_invalidated`（`message = "reflection_changed"`）与
      `run_updated`（携带包含 `reflection` 的 run）两个实时事件。
23. **新增消息 `GetReflectionSummary`**（`$defs/GetReflectionSummaryRequest` →
    `Responses/GetReflectionSummary`）：返回 `{reflection_count, pending_completed_count,
    recent, next_pending}`。`recent` 按 `reflection.updated_at_utc` 倒序、排除软删除；
    `next_pending` 是按 `matched_at_utc` 倒序的第一条"已通关但还没写心得"的记录，
    即界面上「补录心得」的目标；没有时为 `null`。

## 2026-09-04 — 与实现对齐（MRR-CONTRACT-ALIGN）

对齐依据：C# 端 `src/Collector/Ipc/{Wire,IpcEnvelope,FrameCodec,LiveEventBus}.cs`、
`src/Collector/Ipc/MessageDispatcher.cs`、`src/Collector/Export/BackupService.cs`；
C++ 端 `src/Desktop/cpp/{IpcFraming.h,IpcClient.cpp,IBackend.cpp}`。
由 `tests/Collector.IntegrationTests/ContractSchemaTests.cs` 与
`tests/Collector.IntegrationTests/ContractRequestSampleTests.cs` 持续强制。

1. **单帧上限 8 MiB → 4 MiB。** 契约描述文字里的 8388608 与两端实现
   （`FrameCodec.MaxFrameBytes`、`kMaxFrameBytes`）都不一致；两端一直是 4 MiB。
2. **根 `oneOf` → `anyOf`。** 四种信封形状本来就互相包含：一个 `message_type = "Error"`
   的失败信封同时是 `ResponseEnvelope` 与 `ErrorEnvelope`，`oneOf` 会因为匹配到两个分支
   而判定失败。四个分支都保留 `additionalProperties: false`，"拒绝未声明字段"的能力未被削弱。
3. **`ResponseEnvelope` 声明 `ok`（bool，必填）与 `error`（`$defs/ErrorPayload`）。**
   并加上 `if ok === false then required: [error]`。
4. **`EventEnvelope` 声明 `ok`（必填，const `true`）。**
5. **`ErrorEnvelope` 声明 `ok`（const `false`）与 `error`，并把两者列为必填。**
   `error` 与 `payload` 是同一对象的两份拷贝，见 docs/architecture.md §3.2。
6. **`$defs/Run` 新增 `pending_review`（bool，必填）与 `note`（string|null）。**
   对应 schema v2 的两列；`Wire.Run` 一直在写它们。
7. **`$defs/LiveEvent` 新增 `kind`（枚举，必填）**，取值
   `run_state_changed` / `run_created` / `run_updated` / `stats_invalidated` /
   `collector_status`，并把 `sequence` 列为必填。
   `event_type` 枚举**未改动**：本实现发出的五个值（`StateChanged`、`RunStarted`、
   `RunUpdated`、`CaptureStatusChanged`、`DiagnosticsMessage`）本来就都在枚举里。
   缺少的不是分类，而是粒度。`stats_invalidated` 归入 `DiagnosticsMessage`，由 `kind` 区分。
8. **`CreateManualRunRequest` 新增 `note`（string|null，≤1000）。**
9. **`CorrectRunRequest.changes` 新增 `note`。** 与 `RunFields.Correctable` 一致。
10. **`BackupDatabaseRequest.target_path` 改为可选**（可省略或为 `null`）：
    省略时写入数据库旁的托管备份目录，文件名 `mentor_<yyyyMMdd_HHmmss>.db`。
11. **`Responses/BackupDatabase` 新增 `pruned_count`**（integer ≥ 0）：
    保留策略删掉的旧备份数量，`BackupService` 一直在返回它。
12. **`$defs/CollectorStatus` 新增四个可选字段** `game`、`npcap`、`oodle_mode`、
    `reads_game_executable`，对应 `src/Collector/Capture/CaptureWire.StatusExtras`
    为首启与诊断页补充的事实。`game` 与 `npcap` 两个子对象的内部结构由抓包层拥有，
    此处**故意保持开放**（`additionalProperties: true`），待抓包层稳定后再逐字段声明。
    在此之前，它们不受"拒绝未声明字段"保护。
13. **新增错误码 `ERR_IDEMPOTENCY_CONFLICT`**（同一 `request_id` 配不同请求内容）。
    此前该情形返回 `ERR_BAD_REQUEST` + `details.conflict = "idempotency"`；
    `details.conflict` 依旧保留，客户端可以继续按老方式识别。

## 2026-09-04 — 补齐诊断与趋势的四个缺口（MRR-IPC-GAPS）

`protocol_version` 仍然是 **1**，四条全部是**附加性**的：新增可选字段与一条新消息，
既有字段的名字、类型与语义都没有变，旧客户端不改一行也能继续工作。

补齐依据：`src/Collector/Capture/{CaptureDiagnostics,CaptureWire}.cs`、
`src/Collector/Protocol/Parsing/{ParserError,ProfileMessageParser}.cs`、
`src/Collector/Storage/Repositories/StatisticsRepository.cs`、
`src/Collector/Export/DiagnosticsReportExport.cs`。
由 `tests/Collector.IntegrationTests/{ContractSchemaTests,ContractRequestSampleTests,StatisticsTrendTests}.cs`
与 `tests/Desktop.Tests/IpcIntegrationTests.cpp` 持续强制。

14. **`$defs/CaptureStatus` 新增十个可选计数字段**：`connection_count`、
    `messages_decoded`、`decode_errors`、`parse_ok_count`、`parse_fail_count`、
    `duplicate_count`、`message_rate_per_second`、`uptime_ms`、
    `last_valid_event_at_utc`、`recent_parser_errors`。
    这些数字 `CaptureDiagnosticsSnapshot` 一直在测量，此前只出现在导出的脱敏报告里，
    而诊断页要求实时可见（docs/capture-diagnostics.md §5.1）。
    **全部可选**：客户端必须把"字段缺失"渲染成 `—`（未测量），
    不得替 Collector 补一个并未收到的 `0`。
15. **新增 `$defs/ParserErrorEntry`**，即 `recent_parser_errors[]` 的元素：
    `{at_utc, code, opcode, direction, message}`。上限 20 条（解析器自身保留 100 条环形缓冲）。
    `opcode` 是定宽十六进制字符串（`0xNNNN`），`direction` 取 `S2C` / `C2S` / `NONE`，
    `message` 是解析器写出的短句，**永不包含任何报文字节**，
    由 `tests/Collector.UnitTests/CaptureDiagnosticsTests.cs` 逐条正则断言。
16. **新增消息 `ExportDiagnosticsReport`**（`$defs/ExportDiagnosticsReportRequest` →
    `Responses/ExportDiagnosticsReport`）：把 §9 的脱敏诊断报告写成本地 JSON 文件，
    返回 `{target_path, byte_count, completed_at_utc}`。
    `target_path` 可省略（写入 `<db 目录>/diagnostics/diag_<yyyyMMdd_HHmm>.json`）；
    给定路径必须落在 `%USERPROFILE%` 或 `%LOCALAPPDATA%` 之内，否则 `ERR_EXPORT_FAILED`
    （与 `ExportCsv` / `BackupDatabase` 同一条 `ExportPaths` 规则）。
    此前桌面端通过 `QProcess` 调用 Collector 的 `--capture-doctor --json` 获取这份报告，
    该子进程通路已被本消息取代。
17. **`$defs/DashboardStats` 新增可选 `trend`**（`$defs/TrendSeries`），
    `$defs/StatsRequest` 新增可选 `trend_granularity`（`day` / `week` / `month`，默认 `day`）。
    趋势由 Collector 在 SQL 里聚合，桶按 **UTC** 的 `matched_at_utc` 划分、只数 `COMPLETED`、
    排除软删除，空桶以 `completed_count = 0` 显式出现（口径见
    docs/statistics-definitions.md §12.1）。桌面端只负责把 `start_utc` **按本地时区显示**，
    不再自行使用 `QueryRuns(page_size = 200)` 分桶，该做法在记录超过 200 条时会静默截断。
18. **`$defs/DungeonStatsRow` 的聚合口径与 `content_id` 语义**（描述性变更，字段不变）。
    仅按区域识别的记录，当该区域在副本表里只对应一个副本时，归入该副本的行并回报其 `content_id`
    （此前独立成行且 `content_id: null`，同一副本因此出现两行）；区域对应多个副本时保持原样。
    `$defs/RunFilter.content_id` 相应地同时命中 `content_id` 为 NULL 而 `territory_id` 唯一对应该副本的记录，
    使统计行与点进去的历史列表一致（docs/statistics-definitions.md §10）。

---

## 勘误 / Errata —— dc28a27..6d26de2 的四条提交信息与实际内容不符

这四条提交已经推送，重写历史的代价高于收益，因此用本节记录**它们实际改了什么**。
记在契约 changelog 中，是因为其中 `6d26de2` 确实改动了 `contracts/ipc-v1.schema.json`（+41 行），
而提交信息对此只字未提，据此追溯字段进入契约的时间会得到错误结论。
`protocol_version` 仍然是 **1**。本节不改变任何契约，只更正记录。

| 提交 | 提交信息声称 | 实际内容 |
|---|---|---|
| `a115a38` | “Add guidance record management functionality” | 与导随记录管理**无关**。实际是 Oodle 签名查找器（`tools/oodle-signature-finder/find_signatures.py`，Python 约 855 行）、签名档案加载与运行时（`src/Collector/Capture/OodleSignature*.cs`）与 capture trace 三件套。**无新表、无 migration 0004、无 `data/` 改动、无契约改动、无一行 SQL。** |
| `ff716bf` | “Gate live capture on game identity and track Oodle decode failures” | 身份门禁只加在**取证 / 验证**通路（`CaptureValidationController`）；`CaptureController.StartCore`——也就是真正的“live capture”——**没有任何身份检查**。Oodle 解码失败的追踪确实存在，但靠匹配 Machina 的英文 Trace 文本。 |
| `f9a7b81` | “Stabilize capture integration tests and completed-run handling” | **未改动任何生产代码**，纯测试改动；“completed-run handling”没有对应的行为变更。 |
| `6d26de2` | “Improve MentorRecorder verification and packaging workflow” | **未改动 `scripts/` 下任何验证或打包脚本**。实际内容是整个 capture-validation 子系统：IPC（`StartCaptureValidation` / `StopCaptureValidation` / `GetCaptureValidationStatus` / `AddCaptureValidationMarker` 四条消息与相应 `$defs`）+ 后端 + Qt 前端 + 契约。 |

结论：四条中只有 `6d26de2`
改动了 `contracts/`，即上表第四行的四条 capture-validation 消息，其余三条对契约没有改动。后续提交恢复 `docs/git-workflow` 要求的 conventional commits
（`feat:` / `fix:` / …），并保证提交信息描述的是实际改动。
