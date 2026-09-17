# 固件重放 / Fixture Replay

本工具以离线固件驱动解析器与状态机。本文面向用重放验证解析与判定逻辑的维护者。
运行重放**不需要游戏、不需要 Npcap、不需要真实 opcode**，
可确定性地覆盖 [`../../docs/state-machine.md`](../../docs/state-machine.md)
中的全部状态迁移，以及
[`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md) §9
中的全部解析拒绝分类。

重放不是一个独立的可执行文件，而是 Collector 自身的两个模式。

## 两种重放模式

|  | `--replay` | `--replay-decoded` |
|---|---|---|
| 输入 | **语义事件**固件 `tests/Fixtures/*.fixture.json` | **解码报文**固件 `tests/Fixtures/decoded/*.decoded.json` |
| 固件里是什么 | `kind` + 字段（`roulette_id` / `victory` / …） | `opcode` + `payload_hex` 字节 |
| 跳过了什么 | 抓包**和**协议解析 | 只跳过抓包 |
| 走的真实代码 | 状态机 → `SemanticEventProcessor` → SQLite | `ProfileMessageParser` → 状态机 → `SemanticEventProcessor` → SQLite |
| 需要协议档案吗 | 不需要（档案由固件的 `profile` 段**声明**） | 需要，`--profile`；省略时按固件的 `profile` 字段在已安装的 `protocol-profiles/` 里查找 |
| 覆盖的是 | 判定规则 | 字节 → 语义事件的解析规则，以及它与判定的接线 |

```
MentorRecorder.Collector.exe --replay <fixture.json> [--db <path>]
MentorRecorder.Collector.exe --replay-decoded <fixture.decoded.json> [--profile <file>] [--db <path>] [--json]
```

- `--db` 也可写作 `--database`，两者均被接受；Phase 1 的脚本使用后者。
- 省略 `--db` 时：`--replay` 写 `%TEMP%\MentorRecorder\replay\<固件哈希>.db`，
  `--replay-decoded` 写 `%TEMP%\MentorRecorder\replay-decoded\<固件哈希>.db`。
- 两个模式**都无条件将 JSON 报告输出到标准输出**，`--json` 对它们没有任何影响。
  该选项用于 `--serve` / `--version` / `--validate-profile` / `--list-profiles` /
  `--capture-doctor`。
- `--profile` **只能**与 `--replay-decoded` 同时使用；与 `--replay` 搭配时，命令行解析直接拒绝。

## 为什么可行

状态机被设计为单线程、无 I/O 的纯逻辑：输入是有序的语义事件序列，时间由事件携带，
输出是状态迁移与一组**待宿主执行的命令**（建记录、记进入、记职业、收尾、追加事件、
记解析拒绝）。它不访问数据库，也不读取时钟。

解析器同理：`ProfileMessageParser` 中**没有任何 opcode，也没有任何偏移**，
其读取内容全部来自 `ProtocolProfile`。

因此两个重放器可以把同一批命令交给**真实**的 SQLite 写入层 `SemanticEventProcessor`，
活体抓包使用的也是该组件，从而得到与线上完全一致的写入行为。
若重放另有一套持久化规则，固件通过便不再能证明活体抓包具有同样的行为。

## 输出

### `--replay`

| 字段 | 内容 |
|---|---|
| `fixture_id` / `fixture_sha256` | 固件身份与已校验的哈希 |
| `database_path` | 实际写入的数据库 |
| `profile_status` / `profile_usable` | 绑定的档案状态，以及状态机是否被允许工作 |
| `parsed_events[]` | **解析出的事件**：类型、事件键、观察时间、单调读数 |
| `transitions[]` | **状态迁移**：事件类型、`from_state`、`to_state`、是否被接受、是否为重复、所属记录 |
| `final_state` | 状态机的**终态** |
| `runs[]` | **最终记录**，契约 `$defs/Run` 形状 |
| `statistics` | 重放后的**仪表盘统计** |
| `writes` | **写入摘要**：`runs_created` / `runs_updated` / `events_appended` / `events_deduped` / `revisions_appended` / `duplicate_events` / `parser_errors` / `idempotent_replay` |

### `--replay-decoded`

在上表之外还有：

| 字段 | 内容 |
|---|---|
| `profile_path` / `profile_id` | 实际使用的档案文件与标识 |
| `build_matched` | 固件声明的 `game_build` 是否等于档案的 `game_build` |
| `messages_read` | 喂给解析器的解码报文条数 |
| `parser` | 解析器计数：`parse_ok` / `parse_failed` / `duplicates` / `ignored` / `errors[] = {code, count}`；`ignored` 是档案未声明的 opcode，不算失败 |

`build_matched = false` 时档案被**整个撤下**：`profile_id` 变为 `null`，
`profile_status` 变为 `UNSUPPORTED`，每条报文都被 `E_PROFILE_UNSUPPORTED` 拒绝，写入为零。
这是"游戏已更新而档案未更新"的离线复现。以旧结构解析新版本，正是本项目拒绝出现的失败。

## 幂等：重放两次不会产生重复

对同一个数据库重放同一个固件两次，第二次的 `runs_created`、`runs_updated`、
`events_appended`、`revisions_appended` 全为 0，`idempotent_replay` 为 `true`，
而 `runs` 与 `statistics` 与第一次逐字段相同。

两层机制共同保证这一点：

1. 记录 id 由 `SHA-256(<id 种子> + ":run:" + 序号)` 确定性生成，
   `--replay` 与 `--replay-decoded` 的种子均为 `fixture_id`，
   因此第二次重放能识别出上一次创建的记录；
2. `run_events.event_key` 上有 UNIQUE 索引，重复的观察在落库时由 `DO NOTHING` 丢弃。

第二条机制同时保证：**Collector 中途重启不会重复插入进行中的记录。**

> `idempotent_replay` 表示本次重放遇到了已经存在的记录。**零写入**的固件
> （`non_mentor_roulette_then_zone_v1`、`unknown_profile_v1`、`synthetic_non_mentor`、
> `synthetic_constraint_fail`、`synthetic_build_mismatch`）第二次运行仍为 `false`，
> 因为它们第一次也未创建任何记录。
> `ProtocolDecodedReplayTests.ReplayingTheSameFixtureTwiceWritesNothingTheSecondTime`
> 中的 `Assert.Equal(expected.RunCount > 0, second.Writes.IdempotentReplay)` 断言的正是这一点。

### 实际输出

```console
$ MentorRecorder.Collector.exe --replay tests/Fixtures/synthetic-completed-v1.fixture.json --db build/docs-replay/semantic.db
  … "final_state": "COMPLETED"
  … "writes": { "runs_created": 1, "runs_updated": 3, "events_appended": 4,
                "events_deduped": 0, "revisions_appended": 1, "duplicate_events": 0,
                "parser_errors": 0, "idempotent_replay": false }

$ MentorRecorder.Collector.exe --replay tests/Fixtures/synthetic-completed-v1.fixture.json --db build/docs-replay/semantic.db
  … "final_state": "COMPLETED"
  … "writes": { "runs_created": 0, "runs_updated": 0, "events_appended": 0,
                "events_deduped": 0, "revisions_appended": 0, "duplicate_events": 0,
                "parser_errors": 0, "idempotent_replay": true }
```

```console
$ MentorRecorder.Collector.exe --replay-decoded tests/Fixtures/decoded/synthetic_complete.decoded.json \
      --profile protocol-profiles/synthetic/synthetic-v1.json --db build/docs-replay/decoded.db
{
  "fixture_id": "synthetic_complete",
  "fixture_sha256": "0440bfdf2820f7e0fd3fc40d0676b8d5a1b912dd523a496b0dc80f9cc1dfd5fe",
  "profile_id": "synthetic-v1",
  "profile_status": "SYNTHETIC",
  "profile_usable": true,
  "build_matched": true,
  "messages_read": 4,
  "parsed_events": [
    { "event_type": "CONTENT_FINDER_POP",
      "event_key": "CONTENT_FINDER_POP:roulette_id=42:content_id=900001",
      "observed_at_utc": "2026-09-04T03:00:00.000Z", "monotonic_ms": 0 },
    …
  ],
  "final_state": "COMPLETED",
  "writes": { "runs_created": 1, "runs_updated": 3, "events_appended": 4,
              "events_deduped": 0, "revisions_appended": 1, "duplicate_events": 0,
              "parser_errors": 0, "idempotent_replay": false },
  "parser": { "parse_ok": 4, "parse_failed": 0, "duplicates": 0, "ignored": 0, "errors": [] }
}

$ # 同一个 --db 再跑一次
  … "writes": { "runs_created": 0, "runs_updated": 0, "events_appended": 0,
                "revisions_appended": 0, "idempotent_replay": true }
  … "parser": { "parse_ok": 4, "parse_failed": 0, "duplicates": 0, "ignored": 0, "errors": [] }
```

解析器计数第二次仍为 4：每次都实际执行了解析，只是其产出的行已存在于数据库中。

## fail-closed 场景的重放

两个模式各有自己的 fail-closed 用例：

- `--replay`：固件可以声明一个不可用的档案（`profile.status = "UNSUPPORTED_BUILD"`），
  于是状态机拒绝每一个事件：`runs` 为空，`events_appended` 为 0，
  `parser_errors` 等于事件总数，并写进 `parser_errors` 诊断表。
  `unknown_profile_v1` 即为该用例。
- `--replay-decoded`：`synthetic_build_mismatch` 声明 `game_build = synthetic-build-2`，
  而档案为 `synthetic-build-1`，因此档案被整个撤下，3 条报文全部记为
  `E_PROFILE_UNSUPPORTED`，`parsed_events` / `transitions` / `runs` 均为空。
  此时 `writes.parser_errors` 为 **0**，因为拒绝发生在解析器中，状态机未被触及；
  相应数字记在 `parser.parse_failed` 中。
  将任一份 decoded 固件与 `--profile protocol-profiles/cn/cn-unsupported.json` 搭配，
  结果相同。

## 覆盖情况

固件清单、每个固件断言的行为、以及两种格式的说明见
[`../../tests/Fixtures/README.md`](../../tests/Fixtures/README.md)。

- 语义固件覆盖：`COMPLETED`、`CANCELLED_BEFORE_ENTRY`、`LEFT_OR_ABANDONED`、
  `DISCONNECTED`、`INTERRUPTED`、`UNKNOWN_FINAL_STATE`、非导随 `roulette_id`（零写入）、
  重复事件去重、档案不可用（fail-closed）、连续两次导随。
- 解码固件覆盖：`COMPLETED`、`LEFT_OR_ABANDONED`、`CANCELLED_BEFORE_ENTRY`、
  非导随 `roulette_id`、重复报文，四种解析拒绝
  （`E_LEN_MISMATCH`、`E_OFFSET_OOB`、`E_FIELD_CONSTRAINT`、`E_PROFILE_UNSUPPORTED`），
  以及未声明 opcode 被计入 `ignored` 而非失败。

重启恢复得到的 `INTERRUPTED_PENDING_REVIEW` 不由固件覆盖。该状态不是事件驱动的，
而是启动时的一次性扫描，由
[`../../tests/Collector.IntegrationTests/CrashRecoveryTests.cs`](../../tests/Collector.IntegrationTests/CrashRecoveryTests.cs)
覆盖。

期望值分别写在
[`../../tests/Collector.IntegrationTests/ReplayIntegrationTests.cs`](../../tests/Collector.IntegrationTests/ReplayIntegrationTests.cs)
与
[`../../tests/Collector.IntegrationTests/ProtocolDecodedReplayTests.cs`](../../tests/Collector.IntegrationTests/ProtocolDecodedReplayTests.cs)
的 `Expectations` 表中，均为**按文档手工计算**的结果，并非抄录程序输出。

## 新增固件的步骤

1. 按 [`../../tests/Fixtures/README.md`](../../tests/Fixtures/README.md) 规定的格式编写文件；
2. 生成 `.sha256` 旁文件并更新同目录的 `SHA256SUMS`；
3. 解码固件还须将新文件登记进其所属的那份合成档案
   （`synthetic-v1.json` 或 `synthetic-cn-shape-v1.json`）的 `fixtures[]`，
   随后执行 `python tools/protocol-profile-validator/validate.py --stamp` 重新盖章；
4. 在对应的 `Expectations` 表中**手工计算**并填入期望值。
   计算依据为文档，不得抄录程序输出，否则该测试只是在记录既有缺陷。

## 真机 trace 与固件的关系

`--capture-trace` 产出的 `.jsonl` **不是固件**，既不能用于重放，也不得放入本目录。

|  | 抓包取证 trace | 解码固件 `*.decoded.json` |
|---|---|---|
| 来自 | 真机上的一次真实会话 | 手写 / 手算的离线样本 |
| 里面有 | opcode、方向、段类型、负载**长度**、负载 SHA-256 前 12 位、用户标记 | opcode + **完整的 `payload_hex` 字节** |
| 能不能重放 | **不能**（没有字节就没法解析） | 能 |
| 用来干什么 | 找出**候选** opcode | 断言"字节 → 语义事件"的解析规则 |
| 会不会进仓库 | **不会**，它是本机的证据 | 会 |

两者的先后顺序固定，不得对调：

1. `--capture-trace` 在真机上取证，`--trace-report` 给出候选 opcode；
2. 至少在**第二次独立会话**中复现同一个候选（见
   [`../../docs/live-validation-guide.md`](../../docs/live-validation-guide.md) §3.5）；
3. 完成上述两步后，才**手工编写**一份最小的解码固件，按
   [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)
   §3 声明的结构填写该 opcode 的报文体，并按上一节的四个步骤登记；
4. 在档案中补上 `evidence`（`method = "OBSERVED_LOCAL_TRAFFIC"`，注明样本数），
   状态先置为 `CANDIDATE`。

**不得**将 trace 转换为固件。trace 中没有负载字节，任何"补齐"出的字节都是编造的，
而编造的常量正是 [`../../docs/privacy-boundary.md`](../../docs/privacy-boundary.md)
§2 第 12 条所禁止的。
