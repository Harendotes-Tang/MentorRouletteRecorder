# IPC 契约变更记录 / IPC contract changelog

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
    撤下生效中的共享档案（删除文件、重新选择、重新布防校准），丢弃正在核实的候选，并把"用户拒绝"与矛盾记录分开存放。
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
`pending_review: true -> false`）；记录不在待复核状态时仍返回 `ERR_NO_CHANGES`；
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
供已持久化的旧行与固件回放工具使用。

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
异内容复用 request_id 返回 ERR_IDEMPOTENCY_CONFLICT。缺失或过期观测返回
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
把契约改成描述两端**已经在实现**的线格式，而不是要求任何一端改变已发出的字节。
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
      更正，因此**不 bump `revision`、不写 `run_revisions`**，存放在独立的 `run_reflections` 表里。
    - `text` 由 Collector `Trim()`。trim 后为空表示**删除**这条心得，应答的 `reflection` 为 `null`。
    - 记录不存在 → `ERR_NOT_FOUND`；**软删除的记录仍可写入心得**。
    - 按 `request_id` 幂等（与其它变更消息同一张 `ipc_idempotency` 表、同一套指纹规则）。
    - 写入成功后发布 `stats_invalidated`（`message = "reflection_changed"`）与
      `run_updated`（携带带 `reflection` 的 run）两个实时事件。
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

---

## 勘误 / Errata —— dc28a27..6d26de2 的四条提交信息与实际内容不符

这四条提交已经推出，重写历史的代价高于收益，因此用本节记录**它们实际改了什么**。
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
