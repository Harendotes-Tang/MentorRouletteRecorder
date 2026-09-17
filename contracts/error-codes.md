# IPC 错误码 / IPC Error Codes (protocol_version = 1)

本文件是错误码的**规范来源**。`contracts/ipc-v1.schema.json` 中
`$defs/ErrorPayload/properties/code` 的枚举必须与本表逐字一致。

错误响应的信封如下。`error` 与 `payload` 是同一个对象的两份拷贝，
分别对应 Desktop 客户端的 `ok` 分支与 `message_type == "Error"` 分支。
`message_type` 在能识别出请求类型时回填原类型，否则填 `"Error"`。

```json
{
  "protocol_version": 1,
  "request_id": "6f8f4c7e-2a71-4f0e-9b6e-0f2a5f9f9f11",
  "message_type": "CorrectRun",
  "ok": false,
  "error": {
    "code": "ERR_REVISION_CONFLICT",
    "message": "该记录已被修改（当前 revision = 4），请刷新后重试。",
    "details": { "run_id": "…", "current_revision": 4, "expected_revision": 3 },
    "field": null,
    "retryable": false
  },
  "payload": {
    "code": "ERR_REVISION_CONFLICT",
    "message": "该记录已被修改（当前 revision = 4），请刷新后重试。",
    "details": { "run_id": "…", "current_revision": 4, "expected_revision": 3 },
    "field": null,
    "retryable": false
  }
}
```

约定：

- `message` 默认使用简体中文，面向最终用户；`code` 面向程序。
- `details` 只包含结构化、非敏感的上下文，**永远不包含原始报文负载**。
- `retryable = true` 表示同一 `request_id` 原样重发是安全且可能成功的。
- 所有可变更消息（`CreateManualRun` / `CorrectRun` / `SoftDeleteRun` /
  `RestoreRun` / `UpdateAchievementBaseline` / `SetRunReflection`）按 `request_id` 幂等。
  重放已应用的请求返回原结果并置 `idempotent_replay = true`，不再次变更，也不报错。
  仅当**同一个 `request_id` 配上不同的请求内容**时才拒绝，错误码为
  `ERR_IDEMPOTENCY_CONFLICT`（见下表）。

`ReviewCandidateObservation` 同样按 `request_id` 幂等。其响应按候选契约仅返回
`observation_id / review_verdict / reviewed_at_utc` 三项，重放时返回原结果，
不额外添加 `idempotent_replay` 字段。候选核对不会创建正式记录、修订或统计失效事件。

## 错误码表

| Code | 触发条件 | 典型消息类型 | retryable | 客户端应有的处理 |
|---|---|---|---|---|
| `ERR_PROTOCOL_VERSION` | 请求信封的 `protocol_version` 不是 1，或帧无法按 4 字节长度前缀解析 | 任意 | false | 提示版本不匹配，停止使用该连接；Desktop 应提示重新安装以使两个进程版本一致 |
| `ERR_BAD_REQUEST` | JSON 结构不合法、缺少必填字段、字段类型错误、`page_size > 200`、时间戳不是带毫秒的 UTC ISO-8601 等；**以及**所选网卡在开始抓包时已不在 Npcap 设备列表中（例如用户中途启用加速器 / VPN、切换 Wi-Fi 或重新获取 IP） | 任意；网卡情形仅出现在 `StartCapture` | 一般为 false；网卡情形为 **true** | 视为编程错误；`payload.field` 指出出错字段路径。网卡情形下 `field = "adapter_id"`，`details.adapter_id` 为原网卡；客户端应重新选择网卡后按秒重试。该错误不表示 Npcap 未安装 |
| `ERR_NOT_FOUND` | `run_id` 不存在（含从未创建过的 UUID） | `CorrectRun` `SoftDeleteRun` `RestoreRun` `GetRunRevisions` `SetRunReflection` | false | 刷新列表 |
| `ERR_CANDIDATE_OBSERVATION_NOT_FOUND` | 候选观测不存在或已按容量/保留期清理 | `ReviewCandidateObservation` | false | 刷新对照核对列表 |
| `ERR_REASON_REQUIRED` | 需要理由的操作未提供非空 `reason` | `CreateManualRun` `CorrectRun` `SoftDeleteRun` `RestoreRun` `UpdateAchievementBaseline` | false | 在 UI 中强制填写理由后重试（新 `request_id`） |
| `ERR_TIME_ORDER` | 时间顺序非法：`matched_at > entered_at`，或 `entered_at > ended_at` | `CreateManualRun` `CorrectRun` | false | 在对话框内高亮时间字段 |
| `ERR_NEGATIVE_DURATION` | 显式给出的 `duration_ms < 0`，或按修改后的时间推算出的时长为负 | `CreateManualRun` `CorrectRun` | false | 高亮时长字段 |
| `ERR_NO_CHANGES` | `changes` 为空，或所有字段的新值与当前值相同（不产生新 revision） | `CorrectRun` | false | 不写入修订记录；提示"未做任何修改" |
| `ERR_REVISION_CONFLICT` | `expected_revision` 不等于当前 `revision`（并发修改） | `CorrectRun` `SoftDeleteRun` `RestoreRun` | false | 重新拉取记录，展示差异后由用户决定是否重试 |
| `ERR_ALREADY_DELETED` | 对 `soft_deleted = 1` 的记录再次执行软删除 | `SoftDeleteRun` | false | 刷新状态 |
| `ERR_NOT_DELETED` | 对 `soft_deleted = 0` 的记录执行恢复 | `RestoreRun` | false | 刷新状态 |
| `ERR_UNDO_NOT_ALLOWED` | `UndoRevision` 指向的是 `revision = 1`，也就是创建记录本身 | `UndoRevision` | false | 撤销只能作用在创建之后的修订上；需要移除记录请改用软删除。`field = "expected_revision"`，`details.run_id` 给出记录标识 |
| `ERR_IDEMPOTENCY_CONFLICT` | 同一个 `request_id` 被用于**内容不同**的请求（幂等行里保存的请求指纹不匹配） | `CreateManualRun` `CorrectRun` `SoftDeleteRun` `RestoreRun` `UpdateAchievementBaseline` `SetRunReflection` | false | 这是客户端 bug：改动内容变了就必须换一个新的 `request_id` 重发；`details.conflict = "idempotency"`。**另一种触发条件**：幂等表按 24 小时清理之后到达的迟到重放——幂等行已经不在，但 `run_revisions.request_id` / `candidate_reviews.request_id` 证明它执行过；此时 `details.reason = "RESPONSE_EXPIRED"`、`field = "request_id"`，同样是换一个新的 `request_id` 重发 |
| `ERR_NPCAP_MISSING` | 未检测到 Npcap（注册表与 `wpcap.dll` 均不存在），或 Npcap 存在但无法打开设备列表 | `StartCapture` `ListCaptureAdapters` | false | 显示安装指引；**本软件不内置、不下载、不分发 Npcap** |
| `ERR_FFXIV_NOT_RUNNING` | 未找到 FFXIV 游戏进程/窗口，无法确定要观察的会话 | `StartCapture` | true | 提示先启动游戏；可在游戏启动后重试 |
| `ERR_CAPTURE_ALREADY_RUNNING` | 抓包已处于 `STARTING` / `RUNNING` / `DEGRADED` 时再次请求启动 | `StartCapture` | false | 同步 UI 状态，不重复启动 |
| `ERR_CAPTURE_NOT_RUNNING` | 抓包处于 `STOPPED` / `FAILED` 时请求停止 | `StopCapture` | false | 同步 UI 状态 |
| `ERR_PROFILE_UNSUPPORTED` | 协议档案状态为 `NONE` 或 `UNSUPPORTED_BUILD`：客户端版本未知或无对应档案 | `StartCapture` `GetProtocolProfileStatus` | false | **fail-closed**：不解析任何报文、不写入任何记录；提示用户仅能手动补录。自 0.3.0 起，"无对应档案但有随包模板"的情况不再触发它，而是进入本机校准（`CaptureStatus.calibration.state = OBSERVING`） |
| `ERR_CALIBRATION_NOT_READY` | 本机校准还没有可核对的草稿：没有在校准、还在观察、或已阻塞 | `ConfirmCalibration` | false | 按 `CaptureStatus.calibration.blockers` 提示用户继续游玩或导出证据 |
| `ERR_CALIBRATION_REJECTED` | 用户把校准时间线里的某一项标为"错"，草稿作废，被否决的候选在本次会话内不再提出 | `ConfirmCalibration` | false | 提示"再打一把随机任务"，界面回到校准中 |
| `ERR_SHARE_CODE_UNAVAILABLE` | 当前生效的档案给不出校准码：没有可用档案（`details.reason = NO_PROFILE`）、是随包档案（`NOT_LOCAL`）、是其他玩家分享的档案（`SHARED`，共享来的不再转手）、或本机档案文件读不出 / 已被改动 / 不是校准写出的形状（`NOT_SHAREABLE`） | `GetCalibrationShareCode` | false | 不显示「分享给其他玩家」；按 `details.reason` 说明原因，不重试 |
| `ERR_SPEECH_DISABLED` | 环境变量 `MR_DISABLE_ONLINE_SPEECH` 已设置（除空、`0`、`false` 以外的值）。最先检查，连缓存也不读 | `SynthesizeSpeech` | false | 本次用本机语音播报；这是验证、测试、打包时的正常状态 |
| `ERR_SPEECH_NOT_CONFIGURED` | 服务为 `none`，或所选服务缺区域 / 地址 / 模型 / 音色，或没有为当前目标保存密钥（换服务、换区域、换地址会删除旧密钥） | `SynthesizeSpeech` | false | 用本机语音播报；设置页提示去填写 |
| `ERR_SPEECH_AUTH` | 语音服务返回 401 / 403 | `SynthesizeSpeech` | false | 用本机语音播报，并提示一次"密钥无效或与区域不匹配"；`details.http_status` |
| `ERR_SPEECH_QUOTA` | 语音服务返回 429 | `SynthesizeSpeech` | true | 用本机语音播报，并提示一次"额度或频率用完"；`details.http_status` |
| `ERR_SPEECH_NETWORK` | 其他非 2xx 状态（`details.reason = HTTP_STATUS`）、任何 3xx（`REDIRECT_REFUSED`，从不跟随）、连接或读取失败（`details.reason` 为 `NAME_RESOLUTION_ERROR`、`CONNECTION_ERROR` 之类的大写记号） | `SynthesizeSpeech` | true | 用本机语音播报，并提示一次；响应正文从不回传 |
| `ERR_SPEECH_TIMEOUT` | 单次请求连接与读取合计超过 8 秒；或队列已满（同时一条发送中、三条等待，第五条直接失败，`details.reason = QUEUE_FULL`）；或等待超过 8 秒仍未轮到（`QUEUE_WAIT`）；或采集服务正在停止（`CANCELLED`） | `SynthesizeSpeech` | true | 该句改用本机语音播报。超出队列的句子不排队，以免播报持续滞后 |
| `ERR_SPEECH_FORMAT` | 响应 `Content-Type` 不是 `audio/*` / `application/octet-stream`（`details.reason = CONTENT_TYPE`）、超过 5 MB（`TOO_LARGE`）、不是 RIFF/WAVE 16 位 PCM（`NOT_RIFF_WAVE`、`NOT_PCM`、`NOT_16_BIT`、`BAD_FMT`、`NO_DATA` 等），或读音写不进 `tts-cache\`（`CACHE_WRITE`） | `SynthesizeSpeech` | false | 用本机语音播报，并提示一次；这样的响应不写缓存 |
| `ERR_DB_BUSY` | SQLite 返回 `SQLITE_BUSY` / `SQLITE_LOCKED`，且已超过重试预算 | 任意写操作；`CheckDatabaseIntegrity` | true | 稍后以相同 `request_id` 重试（幂等） |
| `ERR_DB_INTEGRITY` | `PRAGMA integrity_check` 失败、迁移校验失败、或检测到 schema 版本高于本程序支持的版本 | 任意 | false | 进入只读/降级模式；建议先执行 `BackupDatabase` 再处理 |
| `ERR_EXPORT_FAILED` | 目标路径不可写、磁盘空间不足、目标已存在且 `overwrite = false`、路径穿越等 | `ExportCsv` `ExportJson` `BackupDatabase` `ExportDiagnosticsReport` | false | 让用户重新选择路径 |
| `ERR_ALREADY_RUNNING` | 本机已有一个 Collector 在服务同一条管道：单实例租约已被占用，或管道已存在 | 进程启动（`--serve`，不经由 IPC 返回） | false | 连接正在运行的实例，不再启动新实例；进程退出码为 **4**，与本机数据故障（3）区分。仅当管道**存在且可接受连接**、占用者却连续 6 次（每次 500 ms，合计 ≥ 3 秒）不应答 `GetVersion` 探活时，退出码改为 **6**，表示占用者已无响应：应先请其停止并等待退出，必要时结束该进程（进程号记录在日志目录下的 `serve.pid`，或 `--pipe` 自定义管道对应的 `serve.<管道名>.pid`），然后重试。租约被占用但管道尚未建立或已拆除（对方正在启动或停止），以及管道实例已满（对方正在服务其他客户端），退出码仍为 **4** |
| `ERR_INTERNAL` | 其他未归类的异常 | 任意 | false | 记录到本地诊断日志（不含报文负载），提示用户上报**手动复制**的日志 |

## 非法状态的显式约定

以下情形**必须**返回上表中的显式错误码，不允许静默成功、不允许返回空结果：

1. 抓包未运行时请求停止 → `ERR_CAPTURE_NOT_RUNNING`。
2. 抓包已运行时请求启动 → `ERR_CAPTURE_ALREADY_RUNNING`。
3. 协议档案不可用时启动抓包 → `ERR_PROFILE_UNSUPPORTED`（fail-closed）。
4. 修改一条不存在的记录 → `ERR_NOT_FOUND`（而不是创建它）。
   写心得（`SetRunReflection`）同理；但**软删除的记录仍可写心得**，那不是非法状态。
5. 修改与当前值完全相同 → `ERR_NO_CHANGES`（而不是写一条空修订）。
6. 并发修改 → `ERR_REVISION_CONFLICT`（而不是后写覆盖）。
7. 重复软删除 / 重复恢复 → `ERR_ALREADY_DELETED` / `ERR_NOT_DELETED`。
8. 同一个 `request_id` 用于内容不同的请求 → `ERR_IDEMPOTENCY_CONFLICT`
   （而不是返回上一次的结果，也不是按新内容再改一次）。
9. 导出诊断报告时目标文件已存在，且该文件名**不是**本程序自己生成的
   `diag_<yyyyMMdd_HHmm>.json` → `ERR_EXPORT_FAILED`（而不是直接覆盖）。
   只有请求里显式带上 `overwrite = true` 才允许替换，以免文件对话框中的一次误操作
   覆盖用户既有的文档。
10. 本机已有 Collector 在服务时再启动一个 → `ERR_ALREADY_RUNNING`，退出码 4
    （而不是 `ERR_INTERNAL` + 退出码 3）。这是正常结果，不是故障，
    调用方应当连接正在运行的那一个实例。
11. 管道的占用者不响应探活 → 同样是 `ERR_ALREADY_RUNNING`，但退出码 **6**
    （而不是 4）。退出码 4 对应连接既有实例，退出码 6 对应结束既有实例，
    两者不得依靠匹配错误文本来区分。
12. 撤销 `revision = 1` → `ERR_UNDO_NOT_ALLOWED`（而不是 `ERR_BAD_REQUEST`）。
    创建记录本身不是可撤销的修订，不属于请求格式错误。
13. 幂等结果已超出 24 小时保留期，但该 `request_id` 确实执行过 →
    `ERR_IDEMPOTENCY_CONFLICT` + `details.reason = "RESPONSE_EXPIRED"`
    （而不是 `ERR_INTERNAL`，也不是把这次变更再应用一遍）。
14. 在线语音的任何失败 → 七个 `ERR_SPEECH_*` 之一，`details` 最多带 `http_status` 与一个大写 `reason`，
    而不是 `ERR_INTERNAL`。语音服务的响应正文一律不回传，该正文可能引用请求头里的密钥。
    `UpdateSpeechSettings` 的字段不合法仍是 `ERR_BAD_REQUEST`，并用 `field` 指出是哪一项。
15. 完整性校验没通过 → `CheckDatabaseIntegrity` 正常应答 `passed = false`（而不是 `ERR_DB_INTEGRITY`）。
    该消息查询的正是校验结果，未通过是应答内容，不是故障。

## 与 HTTP 的关系

无。IPC 只经由命名管道 `MentorRecorder.<UserSidHash>.v1`。
不存在 HTTP 状态码映射，也不存在任何监听端口。
