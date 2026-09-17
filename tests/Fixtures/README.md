# 测试固件 / Test Fixtures

本目录存放**三类**离线数据，均不含任何真实报文。本文面向新增或修改固件的维护者，说明三类数据的用途、格式与改动要求。

```
tests/Fixtures/
  *.fixture.json          语义事件固件 —— 驱动状态机（--replay）
  decoded/*.decoded.json  解码报文固件 —— 驱动解析器 + 状态机（--replay-decoded）
  ipc-requests/*.json     IPC 请求样本 —— 契约测试用，不参与重放
```

前两类是 [`../../docs/state-machine.md`](../../docs/state-machine.md) 与
[`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)
的**可执行形式**：文档中写明的每一条终态、每一种解析拒绝，本目录都有一个固件将其执行一遍。
实现与文档任一方变更而另一方未同步时，这些固件会立即失败。

工具与命令见 [`../../tools/fixture-replay/README.md`](../../tools/fixture-replay/README.md)。

---

# 一、语义事件固件 `*.fixture.json`

本类固件跳过抓包**和**协议解析，直接将有序的语义事件送入状态机。

## 硬性要求

- 固件是**人工合成**的，**不是**抓来的真实报文。
- **不得**包含真实的 opcode、结构偏移、角色名、IP、MAC 或任何其他玩家的信息。
  固件里的 `roulette_id` / `content_id` / `territory_id` / `job_id` 全是编造的合成值。
- `contains_personal_data` 必须为 `false`（加载器会拒绝 `true`）。
- 固件**不得**声明 `profile.status = "VERIFIED"`：固件不是关于真实协议的证据，
  也不得声称自己是。加载器直接拒绝。
- 每个 `.fixture.json` 必须有同名的 `.sha256` 旁文件，且必须登记在 `SHA256SUMS` 里。
  三者不一致时加载会失败（`FixtureIntegrityTests` 会验证这一点）。

## 现有固件

| 文件 | 终态 | 断言的行为 |
|---|---|---|
| `synthetic-completed-v1.fixture.json` | `COMPLETED` | 匹配 → 进入 → 观察到职业 → 胜利 |
| `mentor_left_v1.fixture.json` | `LEFT_OR_ABANDONED` | 进入后离开副本 |
| `mentor_cancelled_v1.fixture.json` | `CANCELLED_BEFORE_ENTRY` | 未进入即取消；`entered_at_utc` 为空，**不计入 attempt** |
| `mentor_disconnected_v1.fixture.json` | `DISCONNECTED` | 进入后连接中断；绝不并入离开率 |
| `mentor_interrupted_v1.fixture.json` | `INTERRUPTED` | 事件序列出现空洞，置信度降为 `LOW` |
| `mentor_unknown_v1.fixture.json` | `UNKNOWN_FINAL_STATE` | 档案中途失效，收尾为 `UNKNOWN` |
| `non_mentor_roulette_then_zone_v1.fixture.json` | `IDLE` | 非导随的 `roulette_id`：**完全不创建任何记录** |
| `duplicate_events_v1.fixture.json` | `COMPLETED` | 重复的弹出/进入/胜利：只有一条记录、一次完成 |
| `unknown_profile_v1.fixture.json` | `IDLE` | 档案 `UNSUPPORTED_BUILD`：fail-closed，零写入，只累计解析拒绝计数 |
| `two_runs_sequence_v1.fixture.json` | `COMPLETED` | 背靠背两次完整导随：两条记录、两次完成 |

期望值不写在固件里，而写在
[`../Collector.IntegrationTests/ReplayIntegrationTests.cs`](../Collector.IntegrationTests/ReplayIntegrationTests.cs)
的 `Expectations` 表中。该表按文档手工计算得出，并非抄录代码输出。

## 格式（语义固件 v1）

```jsonc
{
  "schema_version": 1,
  "fixture_id": "mentor_left_v1",          // 必须与文件名前缀一致
  "description": "……",
  "contains_personal_data": false,          // 必须为 false
  "profile": {
    "profile_id": "synthetic/v1",
    "mentor_roulette_id": 42,               // 合成值
    "match_window_seconds": 45,
    "status": "SYNTHETIC"                   // 可选，见下
  },
  "session": {
    "capture_session_id": "10000000-0000-4000-8000-000000000011",
    "started_at_utc": "2026-09-04T02:00:00.000Z"
  },
  "events": [
    {
      "kind": "CONTENT_FINDER_POP",
      "event_key": "pop-1",
      "observed_at_utc": "2026-09-04T02:00:01.000Z",
      "monotonic_ms": 1000,
      "roulette_id": 42,
      "content_id": 900001
    }
  ]
}
```

`profile.status`（v1 的可选扩展）：

- 省略或 `"SYNTHETIC"` —— 绑定一个**可用的**离线合成档案，状态机正常工作。
- 其他任何值（如 `"UNSUPPORTED_BUILD"`、`"NONE"`、`"UNVERIFIED"`）—— 绑定一个
  **未经验证的 live 档案**，于是状态机 fail-closed，拒绝每一个事件。
  这是离线重放"档案不可用"分支的方式。
- `"VERIFIED"` —— **拒绝加载**。

`events[]` 的字段按 `kind` 取用：`roulette_id`、`content_id`、`territory_id`、`job_id`、
`is_duty_instance`、`victory`、`dropped_count`。事件必须按 `monotonic_ms` **非递减**排列。

**表达"重复观察"**：重复同一组 `(kind, event_key, monotonic_ms)`。
去重键由 `capture_session_id | direction | kind | monotonic_ms | payload_hash | event_key`
拼成，因此只有这三者全部相同才算同一次观察。`duplicate_events_v1` 即按此方式编写。

## 改动固件后必须做的事

1. 重算 `.sha256` 旁文件；
2. 同步更新 `SHA256SUMS`；
3. 更新 `ReplayIntegrationTests.Expectations` 中对应的手算期望值。

`FixtureIntegrityTests` 会检查前两项，`ReplayIntegrationTests` 会检查第三项。

---

# 二、解码报文固件 `decoded/*.decoded.json`

本类固件比语义固件低一层，内容是**字节**。跳过的只有抓包。
`ProfileMessageParser` 之后的一切，包括解析、状态机、`SemanticEventProcessor` 与 SQLite，
都是活体抓包所用的同一份生产代码。

这是本仓库中**唯一**一处在没有游戏的情况下将字节转为记录的位置。

## 硬性要求

- 固件是**人工合成**的，**不是**抓来的真实报文。
- 必须声明 `"synthetic": true`。加载器（`DecodedFixtureLoader`）对
  `false` 或缺失**直接拒绝**，以确保真实抓包无法被混入测试树并重放。
- `opcode` / `segment_type` / `payload_hex` 全是为
  `protocol-profiles/synthetic/synthetic-v1.json` 编造的值，不描述任何真实版本。
- 每个 `.decoded.json` 必须有同名的 `.sha256` 旁文件，且必须登记在
  `decoded/SHA256SUMS` 中（格式与语义固件的同名文件相同：`<64 位十六进制>␠␠<文件名>`）。
  三者不一致时加载会失败。
- 同一批文件还须登记进其所属的那份合成档案的 `fixtures[]`（路径 + SHA-256），
  使档案校验器同样核对它们，见
  [`../../tools/protocol-profile-validator/README.md`](../../tools/protocol-profile-validator/README.md) §7。

`ReplayDecodedTests` 逐条验证上述要求：旁文件哈希、`SHA256SUMS` 与文件集合一一对应、
将 `synthetic` 改为 `false` 会被拒绝、改动任一字节都会被检出。

## 格式（解码固件 v1）

```jsonc
{
  "fixture_id": "synthetic_complete",     // 必须与文件名前缀一致
  "format_version": 1,                    // 只支持 1
  "synthetic": true,                      // 必须为 true
  "description": "……",
  "profile": "synthetic-v1",              // 期望用哪份档案解析
  "game_build": "synthetic-build-1",      // 与档案的 game_build 比对，不等则档案整个撤掉
  "capture_session_id": "20000000-0000-4000-8000-000000000001",  // 可选，UUID；省略时按 fixture_id 确定性生成
  "started_at_utc": "2026-09-04T03:00:00.000Z",                  // 可选，t_ms = 0 对应的墙钟时间
  "messages": [
    {
      "t_ms": 0,                          // 相对毫秒，必须非递减
      "direction": "SERVER_TO_CLIENT",    // SERVER_TO_CLIENT | CLIENT_TO_SERVER
      "segment_type": 61440,
      "opcode": 61441,
      "epoch": 0,                         // bundle 头里的 epoch，参与去重键
      "payload_hex": "2a000000a1bb0d00"   // IPC 头**之后**的报文体，偶数个十六进制位
    }
  ]
}
```

`payload_hex` 中的空格会被忽略。`messages` 不得为空。
`direction` 只接受上述两个字面量。其余任何头部字段不合法时，加载失败而非跳过该字段。

## 现有固件

`--replay-decoded` 配 `protocol-profiles/synthetic/synthetic-v1.json` 时的期望结果
（手算于 `ProtocolDecodedReplayTests.Expectations`）：

| 文件 | 报文数 | 终态 | 记录 | `attempt` / `completed` | `parse_ok` / `parse_failed` / `duplicates` | 拒绝码 |
|---|---|---|---|---|---|---|
| `synthetic_complete` | 4 | `COMPLETED` | 1 × `COMPLETED` | 1 / 1 | 4 / 0 / 0 | —— |
| `synthetic_left` | 4 | `LEFT_OR_ABANDONED` | 1 × `LEFT_OR_ABANDONED` | 1 / 0 | 4 / 0 / 0 | —— |
| `synthetic_cancelled` | 2 | `CANCELLED_BEFORE_ENTRY` | 1 × `CANCELLED_BEFORE_ENTRY` | 0 / 0 | 2 / 0 / 0 | —— |
| `synthetic_non_mentor` | 2 | `IDLE` | **0** | 0 / 0 | 2 / 0 / 0 | —— |
| `synthetic_duplicates` | 7 | `COMPLETED` | 1 × `COMPLETED` | 1 / 1 | 7 / 0 / **3** | —— |
| `synthetic_len_mismatch` | 3 | `MENTOR_MATCHED` | 1 × `UNKNOWN` | 0 / 0 | 2 / 1 / 0 | `E_LEN_MISMATCH` |
| `synthetic_offset_oob` | 3 | `ENTERED_DUTY` | 1 × `UNKNOWN` | 1 / 0 | 2 / 1 / 0 | `E_OFFSET_OOB` |
| `synthetic_unknown_opcode` | 4 | `COMPLETED` | 1 × `COMPLETED` | 1 / 1 | 3 / 1 / 0 | `E_UNKNOWN_OPCODE` |
| `synthetic_constraint_fail` | 2 | `IDLE` | **0** | 0 / 0 | 1 / 1 / 0 | `E_FIELD_CONSTRAINT` |
| `synthetic_build_mismatch` | 3 | `IDLE` | **0** | 0 / 0 | **0 / 3 / 0** | `E_PROFILE_UNSUPPORTED` |

配 `protocol-profiles/synthetic/synthetic-cn-shape-v1.json` 的另有一个：

| 文件 | 报文数 | 终态 | 记录 | `attempt` / `completed` | `parse_ok` / `parse_failed` / `duplicates` | 拒绝码 |
|---|---|---|---|---|---|---|
| `synthetic_territory_duty` | 5 | `LEFT_OR_ABANDONED` | 1 × `LEFT_OR_ABANDONED` | 1 / 0 | 5 / 0 / 0 | —— |

要点：

- `synthetic_territory_duty` 使用**第二份**合成档案 `synthetic-cn-shape-v1`，
  其 `game_build` 为 `synthetic-build-3`。两份合成档案若共用同一版本标识，目录级
  选择将变为 `AMBIGUOUS`，两份同时失效。
  该固件按国服档案的**形状**编写：弹窗只有 `roulette_id`，进本标记没有任何可读字段，
  区域编号单独作为一条 `ZONE_TERRITORY`（偏移 2）。这是离线复现"记录靠区域播报拿到副本名"
  （[../../docs/state-machine.md](../../docs/state-machine.md) §3.11）的唯一方式，
  因为 `synthetic-v1` 的进本标记自带 `territory_id`，不会走到借用那一步。
  职业播报排在弹窗**之前**，覆盖"唯一一次职业观察发生在 `IDLE`"的情形。
  该固件的终态是 `LEFT_OR_ABANDONED` 而非国服的 `UNKNOWN`：离线合成绑定一律按
  "能观察结果"处理，与本固件要验证的识别接线无关。
- **被拒的报文不会到达状态机**，因此 `synthetic_len_mismatch` 的记录停在
  `MENTOR_MATCHED`（没有 `entered_at_utc`，因此**不计入 attempt**），
  `synthetic_offset_oob` 停在 `ENTERED_DUTY`。这不是"解析失败即收尾"，而是"未发生任何事"。
- `synthetic_unknown_opcode` 说明未知 opcode **只计数**：其余三条照常走完，记录仍是
  `COMPLETED`。计数之外不记录任何报文内容。
- `synthetic_build_mismatch` 声明 `game_build = synthetic-build-2`。
  版本不匹配会把档案**整个撤掉**（`profile_id` → `null`，`profile_status` → `UNSUPPORTED`），
  3 条全部被 `E_PROFILE_UNSUPPORTED` 拒绝，`parsed_events` 与 `transitions` 都是空的。
  此时 `writes.parser_errors` 为 **0**，因为拒绝发生在解析器，状态机从未被触及；
  相应数字记在 `parser.parse_failed` 中。
- 重复由**两个**独立机制处理：解析器的有界去重集合只负责 `parser.duplicates` 计数，
  仍会将重复事件向下传递；真正的去重由状态机规则与 `run_events.event_key` 上的
  UNIQUE 索引完成。`synthetic_duplicates` 的 7 条报文中有 3 条重复，最终仍只有一条记录。

`RunCount > 0` 的固件重放两次，第二次 `runs_created` / `events_appended` 为 0，
`idempotent_replay` 为 `true`；零写入的三个固件第二次仍为 `false`，因为它们第一次也未写入。

## 改动解码固件后必须做的事

1. 重算 `.sha256` 旁文件；
2. 同步更新 `decoded/SHA256SUMS`；
3. 更新**它所属那份档案**（`synthetic-v1.json` 或 `synthetic-cn-shape-v1.json`）的
   `fixtures[]` 并 `python tools/protocol-profile-validator/validate.py --stamp <该档案>`；
4. 更新 `ProtocolDecodedReplayTests.Expectations` 中对应的手算期望值。

---

# 三、IPC 请求样本 `ipc-requests/*.json`

**不是重放固件**，不参与状态机，也没有 `.sha256` 旁文件或 `SHA256SUMS`。

每个文件是一条**完整的请求信封**（`message_type` / `payload` / `protocol_version` /
`request_id`），由 Qt 桌面端**正式发布使用的** `IBackend` 包装器生成，并固定保存于本目录：

- 生成方：`tests/Desktop.Tests/IpcRequestTests.cpp`
  通过 `IpcBackend::buildRequestForTest` 构造，写到构建目录后与本目录逐字节比对；
- 校验方：[`../Collector.IntegrationTests/ContractRequestSampleTests.cs`](../Collector.IntegrationTests/ContractRequestSampleTests.cs)
  执行三项检查：
  1. 契约中**每一条业务消息**都恰好有一个同名样本（文件名 = `message_type`）；
  2. 每个样本都满足 [`../../contracts/ipc-v1.schema.json`](../../contracts/ipc-v1.schema.json)
     的根形状与 `$defs/RequestEnvelope`；
  3. 每个样本都被一个**真实运行的 Collector** 接受。业务性拒绝可以接受，
     `ERR_BAD_REQUEST` 则不可接受，它意味着两端对形状的理解已经不一致。

当前 23 个样本，与 [`../../docs/architecture.md`](../../docs/architecture.md) §3.1
的 23 条业务消息一一对应。

`StartCapture.json` 在第 3 步中被**跳过**。该样本刻意填入合成的
`adapter_id`（`NPF_TEST_ADAPTER`），仅为覆盖该字段而不绑定真实网卡。
它被拒绝反映的是本机的情况，而非契约的情况。

改动方式：**不得手写这些文件**。应修改 Desktop 侧的请求构造，运行 `tst_ipcrequests`，
将其写入构建目录的新文件复制回本目录，并审阅 diff。

**例外（共享校准，2026-09-15）**：`GetCalibrationShareCode`、`CheckSharedCalibration`、`ImportCalibrationCode`、
`AcceptSharedQueueInference`、`RejectSharedCalibration` 五个样本最初由 Collector 侧手写，以满足第 1 步“契约中每条消息都有样本”的要求。
桌面端包装器现已实现并加入 `tst_ipcrequests` 的消息列表，生成的载荷与此处逐字相同。`ImportCalibrationCode` 使用 `shared-calibration/vectors.json` 的第一条合法码。

**例外（在线语音与完整性校验，2026-09-16）**：`GetSpeechSettings`、`UpdateSpeechSettings`、`SynthesizeSpeech`、
`CheckDatabaseIntegrity` 四个样本同样最初由 Collector 侧手写。
`UpdateSpeechSettings` 一次带齐六个可选字段，其中 `api_key` 为明显的假值 `test-key-0000`，地址为 `api.example.com`。
`SynthesizeSpeech` 为 `{text, rate_percent: 100, test: false}`。ServerFixture 为 Collector 注入的是一个拒绝发送的传输层，
因此第 3 步中这两个样本即使配置齐全也不会联网。
其中 `CheckDatabaseIntegrity` 的桌面端包装器已在界面改版 P4a 实现（`IBackend::checkDatabaseIntegrity()`），
并已加入 `tst_ipcrequests` 的消息列表，生成的载荷与此处的 `{}` 逐字相同。三个语音样本的桌面端包装器在 P4b 由桌面端实现
（`IBackend::getSpeechSettings()` / `updateSpeechSettings()` / `synthesizeSpeech()`），同样已加入消息列表，
生成的载荷与此处逐字相同。
