# 协议档案格式 / Protocol Profile Format

本文档定义协议档案（protocol profile）的文件格式、证据要求、加载校验流程与本机校准机制，
面向编写协议档案与评审其证据的读者。
档案中的每一个常量都必须可追溯到证据。加载器按本文档的规则逐项校验，任一项不满足即拒绝整份档案。

> **当前国服事件档案：`VERIFIED (cn.2026.08.05)`，2026-09-07 起。**
> 它声明 `CONTENT_FINDER_POP`（S2C `0x0323`，第 16 字节 = 随机任务编号）与
> `ZONE_INITIALIZATION`（S2C `0x014a`）、`ZONE_TERRITORY`（S2C `0x028d`）与
> `PLAYER_JOB`（S2C `0x0350`）四条消息，`mentor_roulette_id = 9`，
> **没有 `DUTY_RESULT`**：通关由按内容识别的通关结算给出（§12），
> 离开副本前没有收到它时，记录以 `UNKNOWN` 收尾并标记待复核
> （[state-machine.md](state-machine.md) §3.10）。同一版本的 `cn.2026.08.05.candidate`
> 继续作为候选档案与之并行，用于副本内采样。

## 1. 协议档案是什么

一份**协议档案（protocol profile）** 把"某个具体客户端版本的网络协议细节"
从代码中分离出来，做成可审计、可替换、可标记失效的数据文件。

代码中**不允许**出现任何硬编码的 opcode 或结构偏移。
`src/Collector/Protocol/Parsing/ProfileMessageParser.cs` 中不存在任何此类常量：
解析器读取哪个位置、读取多长、按何种字节序读取、允许何种取值，全部来自档案。
唯一的例外是 §12 的国服通关结算：它与 opcode 无关，按固定内容识别，
常量集中在 `src/Collector/Protocol/Parsing/DutyClearSignal.cs`，每一个都可追溯到 §12.2 的证据。

这样做的收益如下：

- 游戏更新时只需要新增一份档案，不需要改代码；
- 每个常量都能追溯到证据；
- 版本不匹配时可以明确按 fail-closed 规则停止解析，避免使用旧常量解析出错误数据。

## 2. 文件布局

```
protocol-profiles/
  README.md                 目录索引与状态含义
  profile.schema.json       JSON Schema（draft 2020-12 子集），加载器按它校验
  cn/
    README.md
    cn-unsupported.json     UNSUPPORTED 占位，无任何消息定义
    cn.2026.08.05.json      VERIFIED，弹窗、换区、区域编号、职业四条消息，无 DUTY_RESULT
    cn.2026.08.05.candidate.json CANDIDATE，仅 hypotheses，与正式档案并行观察
  global/
    README.md
    global-unsupported.json UNSUPPORTED 占位，无任何消息定义
  synthetic/
    synthetic-v1.json       SYNTHETIC，全部常量为编造值，仅供离线测试
    synthetic-cn-shape-v1.json SYNTHETIC，照国服档案的形状编（进本标记无字段 +
                            单独的 ZONE_TERRITORY），仅供离线测试
```

目录扫描是**递归**的。`profile.schema.json` 以及 `.json` 之外的文件都会被跳过，
因此放在目录中的 README 不会被当作档案。
`synthetic/` 下的档案会被复制到构建输出旁，但由
`CopyToPublishDirectory="Never"` 排除在 `dotnet publish` 产物之外
（见 [`../protocol-profiles/README.md`](../protocol-profiles/README.md)）。

`profile_id` 必须与**文件名主干一致**，全小写，只含 `a-z0-9.-`。
`region` 必须与所在目录一致（`cn` → `CN`，`global` → `GLOBAL`，`synthetic` → `UNKNOWN`）。
`SYNTHETIC` 档案只能放在 `synthetic/`；`synthetic/` 里也只能放 `SYNTHETIC` 档案。

`profile.schema.json` 会被**内嵌进 Collector 程序集**，
因此运行时校验使用的是构建时的那一份，而不是磁盘上可被任意修改的文件。

## 3. 档案结构（JSON）

```jsonc
{
  "schema_version": 1,
  "profile_id": "synthetic-v1",              // 必须等于文件名主干
  "region": "UNKNOWN",                       // CN | GLOBAL | UNKNOWN
  "game_build": "synthetic-build-1",         // 客户端版本判别符，选择档案的键之一
  "generated_at": "2026-09-04T00:00:00.000Z",
  "mentor_roulette_id": 42,                  // UNSUPPORTED 时必须为 null
  "compatibility_status": "SYNTHETIC",       // VERIFIED | CANDIDATE | UNSUPPORTED | SYNTHETIC
  "match_window_seconds": 45,                // 可选，默认 45

  "messages": [
    {
      "name": "CONTENT_FINDER_POP",          // 见 §3.1
      "opcode": 61441,
      "direction": "SERVER_TO_CLIENT",       // SERVER_TO_CLIENT | CLIENT_TO_SERVER
      "segment_type": 61440,                 // 可选；声明了就必须匹配
      "expected_length": 8,                  // 与 min_length/max_length 二选一
      "fields": [
        {
          "name": "roulette_id",
          "offset": 0,
          "type": "u16",                     // u8 | u16 | u32 | i32 | u64 | bytes
          "endian": "little",                // little（默认）| big
          "constraints": { "min": 1, "max": 65535 }
        },
        { "name": "content_id", "offset": 4, "type": "u32", "endian": "little" }
      ]
    },
    {
      "name": "DUTY_RESULT",
      "opcode": 61443,
      "direction": "SERVER_TO_CLIENT",
      "expected_length": 4,
      "victory_values": [1],                 // DUTY_RESULT 必须非空
      "fields": [
        { "name": "outcome", "offset": 0, "type": "u8", "constraints": { "in": [0, 1, 2] } }
      ]
    }
  ],

  "fixtures": [                              // 档案为之背书的离线固件
    { "path": "../../tests/Fixtures/decoded/synthetic_complete.decoded.json",
      "sha256": "…64 位十六进制…" }
  ],

  "calibration": {                          // 可选；本机校准模板，见 §11
    "finder_request": { "direction": "CLIENT_TO_SERVER", "expected_length": 24,
                        "roulette_field": { "name": "roulette_id", "offset": 0, "type": "u8" } },
    "finder_reply_max_ms": 1000
  },

  "provenance": {
    "summary": "这些常量是怎么来的",
    "capture_fixture": null,                 // 脱敏抓包固件路径
    "capture_fixture_sha256": null,
    "evidence": [ /* 见 §5 */ ]
  },

  "profile_sha256": "…64 位十六进制…"        // 见 §4
}
```

### 3.1 消息名与语义

`name` 只能取状态机识别的以下九个取值，每一个对应
`src/Collector/Domain/Events/SemanticEvent.cs` 中的一个事件类型：

| `name` | 必需字段 | 可选字段 | 产生的语义事件 |
|---|---|---|---|
| `CONTENT_FINDER_POP` | `roulette_id` | `content_id` | `ContentFinderPop` |
| `ZONE_INITIALIZATION` | —— | `territory_id` `content_id` `instance_id` `is_duty_instance` | `ZoneInitialization` |
| `ZONE_TERRITORY` | `territory_id` | —— | `TerritoryObserved` |
| `DUTY_RESULT` | `outcome` | —— | `DutyResult`（`outcome ∈ victory_values` 才是胜利） |
| `PLAYER_JOB` | `job_id` | —— | `PlayerJob` |
| `ZONE_LEFT` | —— | `territory_id` | `ZoneLeft` |
| `INSTANCE_LEFT` | —— | —— | `InstanceLeft` |
| `MATCH_CANCELLED` | —— | —— | `MatchCancelled` |
| `MATCH_ANNOUNCED` | —— | —— | `MatchAnnounced`（只通知匹配成功，不携带轮盘编号；见 §11.5） |

档案可以声明表中没有的字段名（例如 `padding`）。这类字段**会被读取并校验约束**，
但不会进入语义事件，从而使"该段必须为某个固定值"成为可表达的健壮性检查。

`CONTENT_FINDER_POP` 与 `ZONE_INITIALIZATION` 在 `messages` 中含字段定义的非 `UNSUPPORTED`
档案里**必须**齐全。`DUTY_RESULT` 为可选。缺少该消息时，只有国服档案能由按内容识别的通关结算（§12）
产生 `COMPLETED`；没有收到通关结算的离开一律以 `UNKNOWN` 收尾并标记待复核
（[state-machine.md](state-machine.md) §3.10）。
`ZONE_INITIALIZATION` 若不声明 `is_duty_instance`，事件中该标志为 null，
状态机按匹配窗口和换区顺序判定进入与离开（§3.2、§3.3、§3.5）。
仅含候选 `hypotheses` 的例外见 §3.5；既有带字段消息语义不变。

`ZONE_TERRITORY` 为**可选**，并且**不参与任何状态判定**，只用于确定本次记录对应哪个副本。
它与 `ZONE_INITIALIZATION` 分为两条，是因为国服客户端把区域编号放在**另一条**报文里，
而本项目用作进出副本标记的 `0x014a` 没有任何可读字段。
合并两者等于把进本判定迁移到只观察过一次的报文上；分开声明则既保留已验证的进出判定，
又能补齐副本名（[state-machine.md](state-machine.md) §3.11）。
声明该消息**不会**提高记录的置信度。

由 `ZONE_TERRITORY` 识别出的副本身份属于**推断**，不是协议证据，记录上带有
`duty_source = 'TERRITORY'` 的来源标注（[data-model.md](data-model.md) §1.4）。
它只写入 `territory_id` 与显示用的名称、分类，**绝不**把由区域反查出的 `content_id`
写进记录。原因在于：`content_id` 是副本统计的聚合列，而这条报文的字段偏移至今未在
副本内负载上核实过，错误的偏移读出的值有相当大的概率恰好落在合法 territory_id 的范围内。
取得副本内样本并确认偏移之后，该限制才具备放开的依据。

### 3.2 字段类型与字节序

`u8` `u16` `u32` `i32` `u64` `bytes`。
`bytes` 必须带 `length`；其余类型不得带 `length`。
`endian` 默认 `little`，且**不做猜测**：档案未声明为大端时，一律按小端读取。
`offset` 是相对于**IPC 头之后的报文体**起始处的字节偏移
（即 `DecodedMessage.Payload` 的起点）。

`u64` 会被收窄到有符号 64 位。超出范围的值将导致整条消息被拒绝，而不是回绕。

### 3.3 长度规则

每条消息**必须**声明 `expected_length`（精确长度），
**或者** `min_length` / `max_length`（区间），二者不能同时出现，也不能都没有。

加载时检查每个字段的 `offset + 大小` 不得超过
`expected_length`（或 `max_length`），以拦截**档案中的**错误。

运行时再检查一次 `offset + 大小 <= 实际报文体长度`，以拦截**报文中的**意外情况：
一条合法但仅略长于 `min_length` 的变长消息，仍然可能使某个字段越界。
两次检查缺一不可。

### 3.4 约束

`constraints` 可给 `min` / `max` / `in`。任一不满足 → 整条消息被拒绝，
计一次 `E_FIELD_CONSTRAINT`，状态机不会收到这条消息。

`in` 至少要列出一个值。schema 声明了 `minItems: 1`，空数组在 schema 关卡即被拒绝
（`E_PROFILE_SCHEMA`）。绕过 schema 关卡到达加载器的空 `in` 会被
`E_PROFILE_CONSTRAINT` 拒绝（"constraint 'in' must list at least one value"）。
理由是：`in: []` 在语法上合法，但它使该条消息 100% 被拒，表现与偏移写错完全一致。

#### 字段的 `role`：值，还是筛选器

字段可选地声明 `"role"`，取 `"value"`（默认）或 `"selector"`：

| `role` | 约束不命中时 |
|---|---|
| `value`（默认） | `E_FIELD_CONSTRAINT`：计入**解析失败**，进错误环，写 `parser_errors` |
| `selector` | `Ignore()`：计入 **`ignored`**（与记录无关），不进错误环，不写 `parser_errors` |

**适用 `selector` 的情形**：该字段的作用是判断这条报文是否为所需的那一条，
而不是从这条报文中读出一个取值。国服档案的 `CONTENT_FINDER_POP.finder_state` 即为典型：
同一个 opcode 承载申请回执、弹窗、离开后的状态更新，档案用 `finder_state` 挑出其中的弹窗。
约束不命中表示这条报文属于其他事件，而不是档案有误。
若按失败计入，一次完全正常的导随会在抓包页显示"失败 2"，
并掩盖真正需要关注的长度或偏移违规。

**不适用的情形**：真正需要读出取值的字段（`roulette_id`、`territory_id`、`outcome` 等）。
将它们标为筛选器，会使偏移写错这类档案结构错误无声地消失在 `ignored` 计数中。
一条消息可以有多个 `selector` 字段；其中任何一个不命中，整条消息即被忽略。

### 3.5 无字段候选 `hypotheses`

只有 `compatibility_status: "CANDIDATE"` 可以声明可选的 `hypotheses` 数组，最多 128 项。
它把观察到的 opcode 与已验证的字段消息分开，不产生语义事件，也不进入状态机。

```jsonc
{
  "name": "FINDER_STATE_NOTIFICATION",
  "direction": "SERVER_TO_CLIENT",
  "opcode": 803,                // 0x0323，任务书 §1 的候选
  "expected_length": 40,
  "note": "取消排队后出现两次；弹窗可能同族，但未确认。",
  "group": "finder"            // 可选；区域簇成员使用 zone_load
}
```

每项必须包含 `name / direction / opcode / note`，不能包含 `fields` 或任何偏移。
名称为大写字母、数字、下划线，长度不超过 64；分组为小写字母、数字、下划线，
长度不超过 32。名称及 `(direction, opcode)` 都必须唯一。
方向沿用 `SERVER_TO_CLIENT / CLIENT_TO_SERVER`，opcode 为 0–65535 整数。
长度规则沿用 §3.3：精确长度与区间二选一，最小值不得大于最大值。

只声明非空 `hypotheses` 时允许 `messages: []`，并且要求 `mentor_roulette_id: null`；
不再要求三条正式消息。含字段 `messages` 的旧候选继续满足原有完整消息和字段规则。
`hypotheses` 即使为空也不允许出现在 VERIFIED、UNSUPPORTED 或 SYNTHETIC 档案中。

`cn.2026.08.05.candidate` 包括排队候选、相邻 `0x0104` 及全部 18 个区域簇成员。
证据引用为本地 trace 相对路径和 SHA-256，`provenance.evidence[].t_ms` 可记录用户确认的
相对毫秒时间：排队登记 234100、取消排队 276500。这些时间是证据锚点，不是协议字段。
`zone_load` 簇在同连接 3 秒内命中至少 5 个不同成员时产生 `ZONE_LOAD` 候选锚点；
登录与传送同样会触发该锚点，因此不能据此确认进本、完成或离开。

## 4. `profile_sha256` 与规范化 JSON

`profile_sha256` 是**移除该字段之后**对文档做规范化，再取 SHA-256 的结果。

规范化 JSON 的语法经过刻意收窄，以保证 C# 与 Python 两份实现不会发生漂移：

- 对象的键按**字符序数**排序；
- 没有任何空白；
- **数字必须是整数**（出现小数会直接拒绝档案，而不是四舍五入）；
- 字符串只用 `\" \\ \b \f \n \r \t` 这些短转义，其余 `< 0x20` 的控制字符用
  `\u00xx`，非 ASCII 字符原样以 UTF-8 输出。

实现：`src/Collector/Protocol/Profiles/CanonicalJson.cs` 与
`tools/protocol-profile-validator/validate.py` 的 `canonical()`。

写入校验哈希：

```bash
python tools/protocol-profile-validator/validate.py --stamp protocol-profiles/**/*.json
```

## 5. 证据要求（Evidence）

**没有证据就不写常量。**

```jsonc
{
  "field": "messages.DUTY_RESULT.opcode",
  "method": "OBSERVED_LOCAL_TRAFFIC",
  "recorded_at_utc": "2026-01-01T00:00:00.000Z",
  "sample_count": 12,
  "url": null,
  "note": "在本机 12 次副本完成时稳定出现，且未在其他场景出现。"
}
```

| `method` | 含义 |
|---|---|
| `OBSERVED_LOCAL_TRAFFIC` | 在本机观察本机流量获得，并具有可复现的样本计数 |
| `PUBLIC_DOCUMENTATION` | 来自公开的、许可证兼容的文档；必须给出 URL 与访问日期 |
| `USER_CONFIRMED` | 由使用者在真机验证流程中确认（见 [live-validation-guide.md](live-validation-guide.md)） |
| `SYNTHETIC` | **编造值**，只允许出现在 `SYNTHETIC` 档案里，且不计入 `VERIFIED` 的覆盖检查 |

**不允许**的来源：猜测、"试出来的"、"看着像"；从 AGPL 项目复制的常量表；
从游戏客户端反编译/逆向得到的内部结构；任何无法在本文件中说明来源的数字。

`compatibility_status = "VERIFIED"` 时，**每一条消息的 opcode** 都必须在
`provenance.evidence` 里有一条 `field = "messages.<NAME>.opcode"` 且 `method`
不是 `SYNTHETIC` 的条目，否则档案被拒绝。

## 6. `compatibility_status` 与 fail-closed

| 状态 | 含义 | 行为 |
|---|---|---|
| `VERIFIED` | 所有被使用的常量都有证据 | **唯一**允许自动记录的状态。随包档案与本机校准写出的档案（§11）同等对待 |
| `CANDIDATE` | opcode 或结构假设，尚未完成真机字段验证 | 默认不加载；显式候选模式可观察 hypotheses；正式解析始终 fail-closed |
| `UNSUPPORTED` | 该区服 / 该版本没有可用档案 | fail-closed；且**不得声明任何消息**，`mentor_roulette_id` 必须为 `null` |
| `SYNTHETIC` | 为离线测试编造 | 只有显式传入路径（`--profile` / `--replay-decoded`）时才会被加载；实时采集选择档案时，必须显式启用 `allow_synthetic` 才会将其纳入选择范围 |
| `AMBIGUOUS` | **不是**档案里的取值 | 同类别的两份档案声明同一个 `region` + `game_build` 时两份都拒绝；正式与候选类别分别检查 |

`AMBIGUOUS` 被有意设计为互相否决。按文件名、修改时间或 `compatibility_status`
择其一，等于让记录下来的数据取决于哪份文件恰好排在前面。
协议常量不接受这种无声的选择。

## 7. 加载与校验流程

`ProfileLoader.Validate` 依次执行，任一步失败就**整份拒绝**（不降级、不修补）：

1. 文件可读、JSON 可解析；
2. 满足 `protocol-profiles/profile.schema.json`；
3. `profile_sha256` 等于规范化文档的哈希（§4）；
4. 身份规则：`profile_id` = 文件名主干、`region` = 目录、`SYNTHETIC` 位置正确；
5. 消息规则：消息名不重复、`(direction, opcode)` 不重复、长度规则唯一、
   字段名不重复、字段不越界、必需字段齐全、`DUTY_RESULT.victory_values` 非空；
6. 状态规则（§6）；
   候选同时检查 hypotheses 名称/身份唯一和长度规则，禁止混入其他状态；
7. `fixtures[]` 中**存在**的文件必须匹配其 SHA-256（不匹配 → 拒绝）；
   **不存在**的只记一条警告并把 `fixture_verified` 置为 `false`
   （测试树不会被部署到档案旁边，缺失不等于被篡改）。

命令行：

```bash
MentorRecorder.Collector --validate-profile <file> [--json]   # 0 通过 / 1 拒绝 / 2 无法执行
MentorRecorder.Collector --list-profiles [--profiles-dir <path>] [--json]
python tools/protocol-profile-validator/validate.py [--stamp] <file>...
```

两侧的检查项一一对应：Collector 拒绝的档案，Python 校验器也拒绝；反过来并不处处成立，
已知的差异都是 Python 侧更严格，即 Python 拒绝而 Collector 接受。

> **已知差异**：例如 `SYNTHETIC` 档案的位置检查。C# 侧只在目录名恰好是
> `cn` / `global` / `synthetic` 时执行该检查，Python 侧无条件执行。
> 因此，放在**其他任意目录**中的 `SYNTHETIC` 档案会被 `validate.py` 拒绝，
> 被 `--validate-profile` 接受。其余几处（重复的 JSON 键、UTF-8 BOM、非法 UTF-8 字节）与逐条比较见
> [`../tools/protocol-profile-validator/README.md`](../tools/protocol-profile-validator/README.md)
> 的「两份实现的已知差异」。仓库中的档案都位于规定目录下，两侧结论一致。

## 8. 档案选择与版本识别

`ProfileSelector` 的输入是 `region` + `game_build`。实时抓包中二者来自游戏进程检测（`GameProcessDetection`）：
版本号读自客户端同目录下启动器的 `ffxivgame.ver` **文本文件**，区服依据安装路径判定
（[capture-diagnostics.md](capture-diagnostics.md) §3），不打开游戏进程。
`IGameBuildSource` 的默认实现 `UnknownGameBuildSource` 不提供任何版本信息，因此始终 fail-closed。

- 识别不出版本 → `UNSUPPORTED`；
- 没有档案匹配该 `region` + `game_build` → `UNSUPPORTED`（**不做"最接近"匹配**）；
- 有两份匹配 → `AMBIGUOUS`，两份都不用；
- 匹配到的档案状态不是 `VERIFIED`（或显式允许的 `SYNTHETIC`）→ fail-closed。

`ProfileCatalog.Load(root, allowCandidate: false)` 与 `LoadDefault()` 默认目录不保留候选。
用户通过 `UpdateCaptureSettings` 显式设置 `capture.candidate_validation_enabled = true` 后，
验证通路才使用 `allowCandidate: true` 和 `SelectCandidate(region, gameBuild, enabled)`。
候选选择要求已知区服、精确版本和唯一候选；关闭或缺失/歧义均返回空。
普通 `Select` 始终排除候选，`SelectCandidate` 不更新正式 `Current`、binding 或 `ProfileStatus`。
即使对候选显式调用 `ToBinding()`，结果仍不可用于正式解析。

会话开始时冻结正式/候选模式：候选会话的正式 binding 固定为不支持，只执行候选观察。
运行中的普通会话不能中途开启候选模式，必须先停止捕获；关闭候选模式立即停止观察，
当前候选会话不因此恢复正式解析，重新启动捕获后才重新选择正式档案。
候选观测不会写入 `mentor_runs`、统计、成就进度或 TTS。

`IProfileStatusProvider.GetProfileStatus()` 给抓包层与 IPC 一个只读快照：
`profile_id / region / game_build / status / message_count / fixture_verified / last_error`。
里面只有元数据，没有任何 opcode、偏移或报文内容
（见 [privacy-boundary.md](privacy-boundary.md) §5）。

## 9. 解析失败的分类

| 代码 | 含义 |
|---|---|
| `E_UNKNOWN_OPCODE` | 没有任何档案消息认领这个 `(direction, opcode[, segment_type])`。**实时解析器自 contracts/CHANGELOG.md 第 18 条起不再将其记为失败**：只计入 `ignored`，不进入错误环、不落库；该代码保留给样本重放工具与历史行 |
| `E_LEN_MISMATCH` | 报文体长度不满足声明的长度规则 |
| `E_OFFSET_OOB` | 某个字段会读到报文体之外 |
| `E_FIELD_CONSTRAINT` | 字段值落在 `constraints` 之外。**`role: "selector"` 的字段除外**：它不命中只计入 `ignored`，不进环、不落库（§3.4） |
| `E_PROFILE_UNSUPPORTED` | 当前没有可用档案，什么都不解析 |
| `E_INTERNAL` | 解析器自身出错（已捕获并计数，**绝不抛给抓包线程**） |

失败**只计数**，绝不记录报文内容，未知 opcode 同样如此。
最近 100 条保留在有界环中，通过 `IParserStats` 暴露。`GetCaptureStatus` 应答中的字段名为
`parse_ok_count` / `parse_fail_count` / `duplicate_count` / `ignored_count` /
`last_valid_event_at_utc` / `last_valid_event_kind` / `recent_parser_errors`；
诊断报告中记为 `parse_ok` / `parse_fail` / `ignored` / `duplicates`。

失败的消息**不会**到达状态机：不产生事件、不创建记录、不改变状态。
UI 相应地显示"协议不受支持"或"协议解析异常"。

## 10. 贡献一份档案

1. 复制 `protocol-profiles/<region>/<region>-unsupported.json`，
   改名为 `<region>-<游戏版本>-<构建判别符>.json`；
2. `compatibility_status` 先设为 `"CANDIDATE"`；
3. 只填写**在本机亲自观察到**且可复现的常量，同时补齐 `evidence` 条目；
4. `python tools/protocol-profile-validator/validate.py --stamp <file>`；
5. `MentorRecorder.Collector --validate-profile <file>` 必须返回 0；
6. 在真机上按 [live-validation-guide.md](live-validation-guide.md) 走一遍验证流程；
7. 所有被使用的常量都有证据之后，才可以把状态改成 `"VERIFIED"` 并重新写入校验哈希。

提交时**不得**粘贴原始报文、角色名或任何其他玩家的信息。
包含来源不明 opcode 表的提交将被直接关闭。

## 11. 本机校准档案与 `calibration` 模板段

游戏的每个补丁都会重排 opcode，随包档案随之失效，
而报文的**结构**（长度、字段偏移、约束）跨补丁稳定。
本机校准以一份随包 VERIFIED 档案作为模板，
在新版本的被动流量中找出满足同样结构与时序关系的报文，重新推定 opcode，
交由用户核对事件时间线，然后在用户机器上写出一份新的 VERIFIED 档案。

### 11.1 `calibration` 段（模板）

只有 `VERIFIED` 与 `SYNTHETIC` 档案可以携带（`E_PROFILE_CALIBRATION_STATUS`）：

| 字段 | 含义 |
|---|---|
| `finder_request.direction` / `expected_length` | 玩家点选随机任务时客户端发出的那条报文的方向与精确长度 |
| `finder_request.roulette_field` | 其中轮盘编号所在的字段（普通字段定义，`name` 必须是 `roulette_id`，`E_PROFILE_CALIBRATION_FIELD`；越界为 `E_PROFILE_FIELD_OOB`） |
| `finder_reply_max_ms` | 服务器回执（形状与 `CONTENT_FINDER_POP` 相同、回传同一编号）最晚多久到达 |

VERIFIED 模板还必须有 `field = "calibration.finder_request"` 的非 SYNTHETIC 证据条目
（`E_PROFILE_NO_EVIDENCE`），与消息 opcode 的证据要求一致。换区簇的识别阈值是代码常量
（`CalibrationObserver`），不写入档案。正式解析器**不读取**该段。

### 11.2 本机档案

- 位置：`<数据目录>\protocol-profiles\<region>\`（`%LOCALAPPDATA%\MentorRecorder\` 或 `MR_DATA_DIR`），
  文件名 `<region>.<build>.local.json`，`profile_id` 与文件名主干一致（如 `cn.2026.09.01.0000.0000.local`）。
- 状态 `VERIFIED`；`messages` 是模板结构 + 新 opcode；`mentor_roulette_id` 与 `match_window_seconds`
  继承模板；`fixtures` 为空；**不带** `hypotheses`，**不带** `calibration`，因此永远不会被选为下一版的模板，
  推断不会逐版累积。
- 证据：每条消息一条 `OBSERVED_LOCAL_TRAFFIC`（样本数与判据）；`CONTENT_FINDER_POP` 与
  `ZONE_INITIALIZATION` 各加一条 `USER_CONFIRMED`（用户核对时间线的时刻）。`provenance.summary`
  写明模板 id、客户端版本与核对涉及的轮盘编号，并说明指导者轮盘编号是继承的、不是观察到的。
- 目录合并规则（`ProfileCatalog.LoadMerged`）：随包、本机与共享三个目录合并后，同一 `(region, game_build)`
  的非候选档案中，只有自身绑定可用（`ToBinding().IsUsable`）的档案参与排序。排序先看是否认得出服务器的匹配报文
  （认得出的排在按排本推断匹配的之前），再按随包、本机、共享的顺序。排在最前的档案使同版本的其余非候选档案
  （无论是否可用）标记为 `Shadowed`：它们不参与选择，也不会与之构成 `AMBIGUOUS`。同版本没有任何可用档案时，
  不标记任何档案。剩余条目再按 §6 的歧义规则检查。候选档案从不参与排序与遮蔽。
  `ProfileCatalog.LoadDefault()` 仍然只读随包目录。
- 每区服只保留最新 3 份，采集服务启动时清理。
- 写出前先经 `ProfileLoader.Validate`，写出后 Collector 重新从磁盘读目录并按路径重选。
  只有当选中的档案正是刚写出的那一份、且状态为 VERIFIED 时才绑定；绑定从不接受内存中的草稿对象。
- 界面与诊断报告把它标为 `profile_origin = LOCAL_CALIBRATION`（"本机校准"），
  `capture_sessions.protocol_profile_id` 记录它的 id。

### 11.3 校准的判据（观察用，不进状态机）

| 消息 | 判据 |
|---|---|
| `CONTENT_FINDER_POP` | 同一连接上，形状等于 `finder_request` 且编号在轮盘表内的客户端报文，在 `finder_reply_max_ms` 内被一条形状等于模板 `CONTENT_FINDER_POP`、回传同一编号的服务器报文回应。同一对 opcode 命中 ≥ 2 次（不同编号、相隔 ≥ 1 秒）或命中 1 次并另见一次**非回执**的弹窗（选择器字段命中）即锁定；两对都达标则阻塞。弹窗若落在回执窗口内，判为结构变化，阻塞。另有三条约束，因为客户端的移动报文与排本申请形状相同且持续发送：本会话出现超过 2000 次的客户端 opcode 从不算作申请；某个回执 opcode 已有占多数的申请 opcode 后，只有该申请 opcode 能与它配对；形状相符的客户端报文只是候选，得到服务器回执之后才算玩家的排本，才打开回执窗口，并据此开始轮盘编号回显、任意位置扫描与按出现时机的统计 |
| `ZONE_INITIALIZATION` | 换区簇（3 秒内 ≥ 1 条 ≥ 2000 字节且 ≥ 5 种 ≥ 256 字节的服务器报文，开簇前 5 秒静默）里长度等于模板长度的服务器报文：至少在 3 个簇里各恰好出现一次，所选匹配对应的进本簇与出本簇里也各恰好一次；簇外出现不超过 2 次，或簇外次数乘以 3 不超过它恰好出现一次的簇数（换区簇的边界由本软件自行划定，一次较慢的加载会把簇内的标记计到簇外） |
| `ZONE_TERRITORY` | 进本簇里恰好一次、长度等于模板、区域编号命中副本表的报文；未找到时不声明 |
| `PLAYER_JOB` | 进本簇与出本簇都出现、至少半数簇出现、簇内取值稳定且满足约束、没有任何簇与之矛盾。满足条件的 opcode 不止一个时，若它们在每个簇内读到的职业编号都一致（2026.09.15 国服客户端同时用两条报文通告职业），取 opcode 最小者，以便同一版本的所有机器写出相同的档案与校准码；读数不一致则不声明 |

观测器不保留任何报文正文。模板在报文到达时求值，只留下 id 类字段的取值与时间。
任何有界表溢出都会使"簇外出现次数"的统计失效并阻塞，而不是退化为更小的样本。

时间线上标为「错」的一行只否定它自己所说的内容：「排本」一行否定该次申请与回执的配对；「进入副本」「离开副本」
一行否定该次换区是这场匹配所进、所出的副本，不否定换区标记本身；「匹配弹窗」一行否定宣布匹配的那条报文
（按出现时机认出的通知、回执中表示"已匹配"的状态取值，或独立的匹配报文），不否定回执 opcode（它仍回应每一次申请）。
标错之后校准重新观察，再完成一次随机任务即可重新核对；「清空进度并重新观察」清除此前的全部否定，
只保留被本机流量证伪的弹窗 opcode（§11.4）。

「匹配成功」作为独立报文识别时（回执不携带匹配状态的版本），候选还须通过一致性检查：
排本申请尚未结束期间，同一形状（opcode 与长度）的报文在轮盘编号偏移上携带其他取值达到 2 次，即不予采信。
匹配通知在排队期间每次发出都携带所排的轮盘编号；逐行列举小序号的列表类报文（例如 2026.09.15 国服客户端的雇员列表，
长度 80，第 16 字节为栏位序号 0–9）每次发送都包含与之相同的一行，仅凭“进本前出现过所排编号”无法将二者区分。

### 11.4 运行中的证伪与撤下

本机档案的 `CONTENT_FINDER_POP` 为服务器发出且不含选择器字段时，该 opcode 的每一条报文都会被读作一次匹配，
因此在记录期间持续接受检查：1 秒内读到 3 个及以上互不相同的轮盘编号，即判定所认报文不是匹配通知
（任务搜索器同一时刻只提供一个任务）。此时：

- 进行中的记录按抓包停止的方式收尾，该档案立即停止使用；
- 由该档案生成的记录以系统修订标记待复核，并写明原因；用户已亲自修改过结果或复核状态的记录保持原样；
- 档案文件改名为 `<profile_id>.json.contradicted` 保留在原目录，目录加载只读取 `*.json`，不再选中它；
- 该 opcode 在本次运行内不再作为候选，本机校准重新进入观察。

随包档案、共享档案（另有核实与撤下机制，见隐私边界 §8.2）、带选择器的本机档案与按排本推断的本机档案不在此检查之内。

### 11.5 `MATCH_ANNOUNCED`：按出现时机认出的匹配通知

国服 2026.09.15 客户端的匹配通知在**任何字节位置**都不携带所排的轮盘编号，
按数值寻找的三条路径（回执状态、模板偏移、任意偏移扫描）因此全部落空，
档案只能按排本申请推断匹配（`CONTENT_FINDER_POP` 方向为 CLIENT_TO_SERVER）。
这样的档案可以正确记录，但无法确定匹配弹窗出现的时刻，因此不能在弹窗出现时播报。

`MATCH_ANNOUNCED` 补上这个时刻，且**只**补这个时刻：

- 只有 opcode、方向与 `expected_length`，**不声明任何字段**；所排的轮盘仍由排本申请确定。
- 方向必须为 `SERVER_TO_CLIENT`；只允许出现在 `CONTENT_FINDER_POP` 方向为 `CLIENT_TO_SERVER`
  的档案里。两条规则在 `ProfileLoader` 与 `tools/protocol-profile-validator/validate.py`
  中一致实现，违反其一即 `E_PROFILE_MESSAGE_CONTEXT`。
- 可选：缺少它的档案与 1.2.x 的行为完全相同。
- **不写入校准码**：该 opcode 由本机的时机证据认定，证据不随分享码传播，
  因此带有它的档案分享出去仍是纯 QueueRequest 档案（格式 v1 不变），
  接收方自行在本机寻找自己的那一条。

判据（`CalibrationDraft.LockTimedAnnouncement`，仅在 QueueRequest 推断成立时尝试）：

| 子句 | 要求 |
|---|---|
| 保留情况 | 候选仍保留在观察器的时机表中（离群比例 ≤ 1/5、总数 ≤ 400），且 `timing_overflow` 为 0 |
| 位置 | 不是任何换区簇的成员；不是回执 opcode；不在用户拒绝过的 opcode 里 |
| 覆盖 | 每一次「自己申请后进入已知副本」之前的模板匹配窗口内都至少出现一次，且晚于对应申请 |
| 样本 | 这样的进本至少 2 次，且涉及至少 2 个不同的轮盘 |
| 频次 | 每个排本窗口（申请到进本）内出现不超过 8 次 |
| 提前量下限 | 各次进本的最小提前量不少于 3 秒。弹窗与读条之间隔着玩家与队友的确认和倒计时；只提前一秒的报文宣布的是读条，不是匹配。并列判据只在候选之间比较，没有下限时，真正的弹窗一旦不在候选之列，这类报文就会无对手地胜出 |
| 其后有副本 | 表中保留的记录中，至少一半在模板匹配窗口内随后发生过一次已知副本的加载（自己排的或队长排的均可）。排本申请要到换区才清除，长时间排队期间在城里首次见到的报文都算「排本期间出现」且不会被归为离群；匹配通知与它们的区别在于其后几乎总会进入副本 |
| 并列 | 取「各次进本的最小提前量」最大者；提前量再并列则不出结论 |

时间线上每次进本给出一行「匹配弹窗：<轮盘名>（按出现时机认出，读条前约 N 秒）」，需要用户确认；
提前秒数是玩家唯一能与自己的记忆对照的数字。
把这样一行标为「错」只否决该 opcode 作为匹配通知，不影响同一份草稿的排本申请部分，
因此玩家已经在用的记录方式不受影响。

状态机的处理见 [state-machine.md](state-machine.md) §3.12。

### 11.6 重新校准：按用户要求停用本机档案

本机档案一旦生效，校准卡片连同「清空进度并重新观察」与「导入校准码」就从界面上消失
（采集服务在没有校准进行时拒绝导入）。玩家若怀疑本机认错了报文，或者拿到了其他玩家更好的校准码，
此前只能自行到文件夹中修改文件名。「重新校准」为此提供操作入口：`DiscardCalibration` 的可选请求字段
`retire_local_profile = true`（附加式契约变更，缺省 `false` 即原来的「重新观察」）。
当前生效的是本机档案（`profile_origin = LOCAL_CALIBRATION`）时：

- 进行中的记录按抓包停止的方式收尾，该档案立即停止使用；
- 档案文件改名为 `<profile_id>.json.retired` 保留在原目录（**不删除**，与 §11.4 的
  `.contradicted` 后缀区分开：一个是流量证伪，一个是玩家要求）。改名失败时该 id 仍在本次运行内
  被拒绝绑定，不会被重新读回；
- 重新读目录并重选，校准在当前会话内重新进入观察，其后可以再次导入校准码；
- 已积累的校准证据按「重新观察」一并丢弃。

与 §11.4 的区别只有一处，但很关键：**它生成的记录不标记待复核，它所认的 opcode 也不被拒绝**。
是玩家要求重新校准，而不是流量证伪了它；玩家也可能判断错，重新校准后再次认出同一条报文是允许的。

当前生效的不是本机档案（随包档案、共享档案，或未选中任何档案）时，该字段无效，等同于「重新观察」。

#### 11.6.1 回退：`restore_local_profile`

停用之后可能发现是误判。真实例子：一位玩家的本机档案本来读得出服务器的匹配报文（弹窗有语音），
她为了试朋友的校准码把它停用了，客户端先下载到按排本推断的公开校准码，她同意了，
此后便一直使用能力较弱的共享档案，而自己的那份仍保存在磁盘上，只是改了文件名，界面却没有恢复入口。
停用只要一次点击，撤销也必须只要一次点击。

`DiscardCalibration` 的第二个可选字段 `restore_local_profile = true`（与 `retire_local_profile` 互斥，
同时为真时返回 `ERR_BAD_REQUEST`）：

- `<profile_id>.json.retired` **改名回** `.json`，是改名不是重写——重写会改变
  `profile_sha256`，那就不再是玩家当初核对过的那一份；
- 同名 `.json` 已存在时**拒绝**：那是此后确认的新校准，悄悄覆盖它等于用旧答案换掉新答案；
- `.json.contradicted`（§11.4，流量证伪后自动撤下的）**永不可恢复**：不是玩家选择停用的，
  明天它读到的仍是同一条错误报文；
- 改名之后先重新读取目录，再处理当前生效的档案。目录暂时读不到时，文件改回 `.retired`，当前生效的档案不受影响，
  并以 `ERR_CALIBRATION_NOT_READY` 答「暂时读不到校准档案，上一份本机校准还没有换回来，请稍后再试一次。」；
- 重新读出的目录按 §11.2 的目录合并规则仍选中当前生效的档案时，它不停用，进行中的记录不受影响；
  否则当前生效的档案（含正在使用的共享档案）先按停止捕获收尾并解绑，再按同一规则重选。
  通常恢复出来的本机档案会被选中：同为认得出匹配报文、或同为按排本推断的档案时，本机排在共享之前；
- 共享校准只是**让位给更优先的档案**，并非被撤下：选择结果不再是共享档案时，共享会话自行释放该绑定
  （`SharedCalibrationSession.ReconcileBound`），因此不注销校准码、不记录玩家拒绝、
  不给任何记录打待复核标记。那份校准码并未被证伪，只是被更高优先级的档案替代；
- 校准证据**不**丢弃（这不是「重新观察」）；角色随即重新求值，例如缺少 `PLAYER_JOB`
  的非排本推断档案会重新进入 §11.7 的补全角色；
- 恢复出来的文件与其他档案一样要过 `ProfileLoader` 校验。重选之后它没有被选中，分两种情况：
  - **被更优先的档案遮蔽**：文件能通过校验、可以用于记录，只是 §11.2 把当前在用的档案排在它之前——
    它按排本推断匹配、而在用的档案认得出匹配报文，或这一版游戏有同类的随包档案。这不是坏文件：它保留为 `.json`
    （不再是 `.retired`，`retired_local_profile_available` 随之为假），不拒绝任何东西，在用的档案日后不再可用时由它接替；
    在用的档案仍被选中时，它与进行中的记录都不受影响（见上一条）。应答仍是 `ERR_CALIBRATION_NOT_READY`，
    但说明的是档案已经放回、为何继续用现有校准：
    「上一份本机校准已经放回，但它是按排本推断匹配的，现在用的校准能直接认出匹配通知，所以继续用现在这份记录。」，
    在用的是随包档案时为「……但这一版游戏已有随软件附带的档案，所以继续用它记录。」，
    其他情形为「……但这一版游戏另有一份优先使用的校准，所以继续用它记录。」
  - **过不了校验**（由更早版本写出、或已损坏）：它会被重新改回 `.retired`，避免此后每次选择都在拒绝同一个文件，
    并以 `ERR_CALIBRATION_NOT_READY` 答「上一份本机校准已经无法使用，请重新校准。」

`CaptureStatus.calibration.retired_local_profile_available`（可选布尔）告诉桌面端这条退路是否存在：
磁盘上有 `.json.retired`、且当前没有本机档案生效时为真。它在**每次读取状态快照时**即时计算
（一次 `File.Exists`），而不是缓存：状态读取都由请求驱动（`GetCaptureStatus`、桌面端两秒一次的
记录轮询、诊断报告），从不在抓包线程的报文处理路径上计算；使用缓存则需要感知本进程之外的文件变化才能使缓存失效，
玩家自己在资源管理器里把文件改回去，正是本功能要替代的那种操作。

### 11.7 补全职业：已完成的本机档案自行补上 `PLAYER_JOB`

1.2.3 起，按排本推断的本机档案（`MatchFromQueue`）可以在后续草稿里补上缺少的可选消息，
因为这类档案下校准本来就一直开着。按排本推断的共享档案同样如此：校准码不携带匹配通知（§11.5），
接收方在本机自行寻找；核对确认后写出的本机档案在目录中排在共享档案之前（§11.2），并在当前会话的两场记录之间换用。
读得出服务器匹配报文的档案则不然：校准已经结束，
卡片与观察器都已收起，在职业判据修复之前写出的档案会让**这一版游戏的每一条记录**始终记成「职业未知」。

因此这类档案旁边也保留一个不出现在界面上的校准角色（`LiveProtocolPipeline.CalibrationRoles()` 的
`Completing`）：

- 触发条件：区服可校准、选中档案可用、`profile_origin = LOCAL_CALIBRATION`、
  档案不是 `MatchFromQueue`、档案没有 `PLAYER_JOB`、且模板声明了 `PLAYER_JOB`；
- 校准照常观察、证据照常落盘，但状态快照对外一律报 `IDLE`（卡片不出现、不打扰玩家），
  直到草稿同时满足两条：**声明了 `PLAYER_JOB`**，且**除职业外所声明的每一条消息都与当前生效档案
  逐条同名且完全相同**（按写入档案的 JSON 文本比较，与校准码采用同一套比较）。
  两侧都不允许比对方多出一条消息；
- 满足时状态转为 `READY`，走平常的确认流程，第一条说明写作
  「这份校准可以补上职业了：核对下面的时间线后，之后的记录会自动带上职业。」；
- 确认后按同一 `profile_id` 重写档案。会话内既没有记录在进行、也没有暂存的排本申请时，即刻换用新解析器；
  否则在这一场结束之后于同一会话内换用（[state-machine.md](state-machine.md) §7.4）。角色随即重新求值，
  档案有了职业便不再处于这一角色。

匹配报文与当前档案不一致的草稿**永远不会**在这里提示用户确认：那是替换而不是补全，
悄悄换掉一条今天工作正常的匹配报文，比「职业未知」更糟。

## 12. 通关结算信号（按内容识别，不属于档案）

国服客户端的副本通关结算不是一条单独的报文，而是服务器下发的通用控制报文的一种内容。
同一个 opcode 还承载副本开始、剩余时间、进度等内容（2026-10-05 的取证中，约 36 分钟的会话里共出现 858 次），
并且随每次游戏更新改变。在档案中声明它，就要在每次更新后重新找出这个 opcode；
按「整场只出现一次」寻找结算报文的做法（[live-validation-guide.md](live-validation-guide.md) §7.5）也找不到它。
结算的内容却是完全确定的，因此采集服务不读取 opcode，只比较内容。
这是代码中唯一按常量识别报文的地方（§1），全部常量集中在 `src/Collector/Protocol/Parsing/DutyClearSignal.cs`。

### 12.1 判据

服务器发往客户端、段类型为 3（IPC）的报文，其报文体（IPC 头之后，与 §3.2 的偏移同一起点）
同时满足下表每一行时，即为通关结算：

| 偏移 | 类型 | 取值 | 说明 |
|---|---|---|---|
| —— | 长度 | 恰好 40 字节 | 不设容差，见 §12.3 |
| 0 | u16，小端 | `0x006D` | 类别 |
| 2 | u16，小端 | 0 | |
| 4 | u16，小端 | 任意 | 副本编号（样本中为 `0x0056`），随副本而变，不作约束 |
| 6 | u16，小端 | `0x8003` | 副本类型，即偏移 4 处 u32 的高 16 位 |
| 8 | u32，小端 | `0x40000003` | 命令：通关 |
| 12 至 39 | 字节 | 全部为 0 | |

opcode 不参与判断：同一内容出现在任何 opcode 上都按通关结算处理。
方向、段类型、长度、任一固定字段或任一置零字节不符的报文都不是通关结算，照常交给档案认领；
档案没有声明其 opcode 时只计入 `ignored`，不计为解析失败，也不写 `parser_errors`
（[state-machine.md](state-machine.md) §7.2）。

比较在认领档案声明的消息之前进行，因此档案即使在同一 opcode 上声明了其他消息，也不会使它失效；
通关结算的内容几乎完全确定（除 2 字节的副本编号外，前 12 字节固定，其余 28 字节全为 0），不是其他报文的合理内容。
命中时，解析器产生一个胜利的 `DutyResult` 事件（语义键 `DUTY_RESULT:clear_signal`），计入 `parse_ok`，
与档案声明的 `DUTY_RESULT` 经同一条路径进入状态机（[state-machine.md](state-machine.md) §3.4.1）。
比较不保留任何字节；事件键与其他事件的键构成相同，负载只以 SHA-256 摘要出现
（[state-machine.md](state-machine.md) §7.3）。

### 12.2 证据

| 项 | 内容 |
|---|---|
| `method` | `OBSERVED_LOCAL_TRAFFIC` |
| 来源 | 本机取证文件 `duty-trace-2026-10-05.jsonl`（`--capture-trace`，不含负载），SHA-256 `c398a7b5be6c5d1967eac585598947d1df27f1ff7695bec67ebe423a30541ad2`。整份文件只保存在取证的电脑上，不作为公开附件（[privacy-boundary.md](privacy-boundary.md) §5） |
| 环境 | 国服客户端 `2026.09.15.0000.0000`，采集服务 1.6.0-beta.1，Npcap 1.88 |
| 记录日期 | 2026-10-05 |
| `sample_count` | 1（一局副本、一次通关） |
| 所在行 | `seq 62088`：`t_ms 2046833`，连接 `7a91482e`，S2C，段类型 3，opcode `0x0204`，长度 40，`h12 1b7b8dd132a5` |
| 尚未观测 | 其他副本类型（样本副本的时限为 120 分钟）；中途退出、灭团、超时等通关以外的结局 |

取证文件不含负载，只记录每条报文负载 SHA-256 的前 12 位（`h12`，[live-validation-guide.md](live-validation-guide.md) §3.5.1）。
通关结算的内容可以完全预测，于是对候选取值逐一计算哈希，再到取证文件中查找。
由 §12.1 的常量与副本编号 `0x0056` 构成的 40 字节报文体为 `6d000000 56000380 03000040` 后接 28 个 `00`，
其 SHA-256 为 `1b7b8dd132a57e397bdaf964397fb17e4ea20f5a6a62f4bebc9c0f1ab0bf8566`，前 12 位正是该行的 `h12`；
任一常量取错，这一等式即不成立。单元测试 `DutyClearSignalTests.TheObservedBodyHashesToTheEvidenceRow`
由常量重建报文体并断言这一前缀，`DutyClearSignal.EvidenceSha256` 记下取证文件的 SHA-256。

同一取证中，副本类型与副本编号都相同的其余 15 条控制报文彼此自洽：开始时给出 7200 秒的时限，
其后的剩余时间每隔 300 秒减少 300，进度从 1 递增到 5；通关结算出现在离开副本前 13.7 秒。
在约 9.4×10⁶ 个预测哈希与约 1.7×10⁴ 种实际内容之间，48 位前缀偶然相同的期望次数约为 6×10⁻⁴。

档案中的常量以 `provenance.evidence` 条目说明来源（§5）。这些常量不在任何档案中，等价的做法是本节的证据表与固定哈希的单元测试。

### 12.3 长度：恰好 40 字节，不设容差

- 取证中承载通关结算的 opcode 共 858 条，全部为 40 字节。
- 早先资料中的 32 字节在这个版本上没有命中，40 字节报文体的前 32 字节也不在取证文件中；
  对 8 至 64 字节的每一种长度各算一遍后，命中的是 40 字节。
- 接受其他长度，等于接受这个客户端从未发出过的形状；长度变化是协议变化，字段也可能随之移动。
  不命中的代价正是此前的行为：离开副本时记录以 `UNKNOWN` 收尾并标记待复核，由用户确认结果
  （[state-machine.md](state-machine.md) §3.10）；误命中则会把一局没有通关的副本记为通关。

### 12.4 区服

是否比较由 `ProtocolProfile.ObservesDutyClear` 决定：档案不声明 `DUTY_RESULT`，
且 `region` 为 `CN`（离线测试中另含 `SYNTHETIC` 档案）时才比较；与其他档案能力一样，
只有 `VERIFIED` 档案（或显式的合成档案）的绑定才会启用它。

- **国服**：随包档案、本机校准档案与共享校准档案目前都不声明 `DUTY_RESULT`，因此都会比较。
  其他客户端版本上的布局若有不同，比较不会命中，按 fail-closed 处理。
- **国际服**：不比较。没有任何国际服客户端上的观测，而长度从 32 字节变为 40 字节说明这一布局会随版本移动。
  国际服目前也没有可用档案。
- **声明了 `DUTY_RESULT` 的档案**：不比较，行为与此前完全相同，结局由档案自身的结算报文给出（§3.1）。
- **`SYNTHETIC` 档案**（只用于离线测试，`region` 必须为 `UNKNOWN`）：不声明 `DUTY_RESULT` 时比较，
  以便通过解码测试样本重放这一信号（[`../tests/Fixtures/README.md`](../tests/Fixtures/README.md)）。

### 12.5 为什么不纳入档案、校准与校准码

- 判据与 opcode 无关，游戏更新后不需要重新学习，也就没有需要写进档案的数值。
- `profile.schema.json` 没有对应字段；本机校准不观察它，本机档案文件与共享校准码都不携带它，也不因此改变。
  此前写出的本机档案与他人分享的校准码，只要满足 §12.4，同样会比较。
- 随包档案 `cn.2026.08.05.json` 未作修改，其中「没有 `DUTY_RESULT`」的说明仍然属实。

### 12.6 常量的修改

常量只因新的证据而修改，并依次做到：取得新的证据（§5 允许的来源），在 §12.2 中新增一行证据，
先写出在现有常量下失败的测试，再修改 `DutyClearSignal`。常量**不得**为了多认一些报文而放宽：
不为长度设容差，不放开固定字段，不接受客户端方向或其他段类型。
