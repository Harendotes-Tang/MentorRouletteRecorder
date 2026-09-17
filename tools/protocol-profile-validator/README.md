# 协议档案校验器 / Protocol Profile Validator

本工具校验一份[协议档案](../../docs/protocol-profile-format.md)是否可被 Collector 接受。
本文面向新增或修改协议档案的维护者，说明校验流程、逐项检查规则与两份实现之间的差异。

**校验逻辑的权威实现位于 Collector**（`src/Collector/Protocol/Profiles/`）。
本目录下的 `validate.py` 是其**独立复刻**，用于 CI，以及不启动 .NET 而修改档案的场景。
两份实现刻意分别编写，使任何一方的疏漏在另一方暴露。

```bash
# 权威实现（C#，与运行时用的是同一份代码）
MentorRecorder.Collector.exe --validate-profile <file> [--json]
MentorRecorder.Collector.exe --list-profiles [--profiles-dir <path>] [--json]

# 复刻实现（Python 3，无第三方依赖）
python tools/protocol-profile-validator/validate.py [--stamp] <file>...
```

退出码（两侧一致）：`0` 通过；`1` 校验失败；`2` **校验器自身无法运行**。

「无法运行」在两侧的含义不同，但均不表示档案有问题。
Collector 侧指命令行解析失败，或内嵌的 schema 资源缺失；
`validate.py` 侧指无法读取 `protocol-profiles/profile.schema.json`。
**档案不可读或 JSON 不合法记为 1，不记为 2**，Collector 分别记 `E_PROFILE_UNREADABLE` 与
`E_PROFILE_PARSE`。

---

## 检查项

`ProfileLoader.Validate` 按以下顺序执行。**任一步失败即整份拒绝**：不降级，不修补，
不以默认值补齐缺失的常量。

### 1. 可读、可解析

文件不可读 → `E_PROFILE_UNREADABLE`；JSON 不合法 → `E_PROFILE_PARSE`。

### 2. JSON Schema

按 `protocol-profiles/profile.schema.json` 校验，失败记 `E_PROFILE_SCHEMA`，
且**在该步返回**。后续语义检查不会在结构不合法的文档上继续执行。

校验器为本仓库自行实现的 `JsonSchemaValidator.cs`，只支持 draft 2020-12 的一个子集：
`$ref`（仅本地指针）、`type`、`enum`、`const`、`required`、`properties`、
`additionalProperties`、`items`、`minItems`、`maxItems`、`minimum`、`maximum`、
`minLength`、`maxLength`、`pattern`。
未采用现成的库，是因为**档案 schema 是一条硬安全边界**。解析器从字节缓冲中读出的一切
都须先通过这一关，因此这段代码保留在本仓库内，并沿用本仓库的许可证。
正则匹配设有 200 ms 超时。`$defs` 之外的未知关键字被忽略，而非视为"已满足"。
schema 仅使用上述关键字这一点本身也有测试覆盖。

Schema 在构建时**内嵌进 Collector 程序集**，因此运行时校验使用的是构建时的版本，
而非磁盘上可被任意修改的文件。

### 3. `profile_sha256`：规范化哈希

`profile_sha256` 为移除该字段后、将文档按**规范化 JSON**序列化再取的 SHA-256（小写十六进制）。
不匹配 → `E_PROFILE_HASH`。

规范化语法刻意收窄，实现见 `CanonicalJson.cs` 与 `validate.py` 的 `canonical()`：

- 对象键按**序数序**排序；
- 无任何空白；
- **数字必须是整数**。出现小数时直接拒绝档案（`E_PROFILE_CANONICAL`），而不是四舍五入。
  取决于格式化选择的哈希不成其为哈希；
- 字符串只用 `\" \\ \b \f \n \r \t` 短转义，其余 `< 0x20` 的控制字符用 `\u00xx`，
  非 ASCII 原样以 UTF-8 输出。

重新盖章：

```bash
python tools/protocol-profile-validator/validate.py --stamp protocol-profiles/synthetic/synthetic-v1.json
```

`--stamp` 仅 Python 侧提供，该选项会**改写文件**，且先按 schema 校验、通过后才写入。
Collector 侧只校验，不写入。

### 4. 身份规则

| 检查 | 代码 |
|---|---|
| `profile_id` 必须等于文件名主干 | `E_PROFILE_ID` |
| `region` 必须与所在目录一致（`cn`→`CN`、`global`→`GLOBAL`、`synthetic`→`UNKNOWN`） | `E_PROFILE_REGION` |
| `SYNTHETIC` 只能在 `synthetic/`，`synthetic/` 里也只能放 `SYNTHETIC` | `E_PROFILE_SYNTHETIC_LOCATION` |

目录名不是 `cn` / `global` / `synthetic` 时，C# 侧跳过后两项检查，因此校验临时目录中的
草稿档案不受目录名影响。Python 侧的 `SYNTHETIC` 位置检查则是无条件的。
对于放在任意目录中的同一份 `SYNTHETIC` 档案，Python 会报错而 Collector 不会。
这是两份实现目前**唯一**已知的行为差异，详见文末。

### 5. 消息规则

- 消息名不得重复（`E_PROFILE_DUPLICATE_MESSAGE`）；
- `(direction, opcode)` 不得重复（`E_PROFILE_DUPLICATE_OPCODE`）；
- `expected_length` 与 `min_length`/`max_length` **二选一**，不能都有也不能都没有；
  `max_length < min_length` 也拒绝（`E_PROFILE_LENGTH_RULE`）；
- 字段名不得重复（`E_PROFILE_DUPLICATE_FIELD`）；
- `bytes` 字段必须带 `length`，其余类型不得带（`E_PROFILE_FIELD_LENGTH`）；
- 字段的 `offset + 大小` 不得超过 `expected_length`（或 `max_length`）
  （`E_PROFILE_FIELD_OOB`）；
- `constraints.max < constraints.min` 拒绝（`E_PROFILE_CONSTRAINT`）；
- 必需字段齐全（`E_PROFILE_MISSING_FIELD`）：
  `CONTENT_FINDER_POP.roulette_id`、`DUTY_RESULT.outcome`、`PLAYER_JOB.job_id`；
  `ZONE_INITIALIZATION` / `ZONE_LEFT` / `INSTANCE_LEFT` / `MATCH_CANCELLED` 无必需字段；
- `DUTY_RESULT` 的 `victory_values` 必须非空（`E_PROFILE_NO_VICTORY`）。

### 6. 状态规则

| `compatibility_status` | 要求 | 违反时 |
|---|---|---|
| `UNSUPPORTED` | **不得声明任何消息**；`mentor_roulette_id` 必须为 `null` | `E_PROFILE_UNSUPPORTED_MESSAGES` / `E_PROFILE_UNSUPPORTED_ROULETTE` |
| `CANDIDATE` / `VERIFIED` / `SYNTHETIC` | `mentor_roulette_id` 非 `null`；`CONTENT_FINDER_POP`、`ZONE_INITIALIZATION`、`DUTY_RESULT` 三条消息齐全 | `E_PROFILE_NO_ROULETTE` / `E_PROFILE_MISSING_MESSAGE` |
| `VERIFIED` | **每一条消息的 opcode** 都要在 `provenance.evidence` 里有一条 `field = "messages.<NAME>.opcode"` 且 `method != "SYNTHETIC"` 的条目 | `E_PROFILE_NO_EVIDENCE` |

`SYNTHETIC` 的特殊之处仅在于此：它**通过**校验，编造常量正是其用途。
但它在活体档案选择中不可见。只有显式传入路径（`--profile` / `--replay-decoded`），
或调用方显式指定 `allowSynthetic`，才能选中它（`ProfileSelector`）。
`AMBIGUOUS` 不是档案中可以写的取值，它由**目录级**判定产生，见下一节。

### 7. 固件哈希

`fixtures[]` 中**存在**的文件必须匹配其 `sha256`，不匹配 → `E_PROFILE_FIXTURE_HASH`，拒绝该档案。
**不存在**的文件只记一条警告 `W_PROFILE_FIXTURE_MISSING`，并将 `fixture_verified` 置为 `false`。
测试树不会随档案一同部署，缺失不等于被篡改。
路径按档案文件所在目录解析。

---

## `--list-profiles` 与 `AMBIGUOUS`

单文件校验无法发现两份档案声明同一版本的情况。`ProfileCatalog` 递归扫描整个目录树
（`profile.schema.json` 自身跳过），逐个校验，然后执行一次**冲突判定**：

> 两份**都通过校验**的档案声明了同一个 `(region, game_build)` → **两份都判为 `AMBIGUOUS`，
> 两份都不用。**

按文件名、修改时间或 `compatibility_status` 择一，等于让记录的数据取决于
哪份文件恰好排在前面。协议常量不接受这种沉默的选择。

档案根目录的查找顺序为：`--profiles-dir` 显式指定；否则从可执行文件目录与当前工作目录
出发，逐级向上查找名为 `protocol-profiles` 的目录。均未找到时按空目录处理，整体 fail-closed。

---

## 实际输出

### 通过

```console
$ MentorRecorder.Collector.exe --validate-profile protocol-profiles/synthetic/synthetic-v1.json
<repo-root>\protocol-profiles\synthetic\synthetic-v1.json
  profile_id:  synthetic-v1
  region:      UNKNOWN
  game_build:  synthetic-build-1
  status:      SYNTHETIC
  messages:    7
  fixtures:    verified
  result:      VALID
$ echo $?
0
```

### 被拒（将 `mentor_roulette_id` 由 42 改为 43，且未重新盖章）

```console
$ MentorRecorder.Collector.exe --validate-profile build/docs-replay/bad/synthetic-v1.json
<repo-root>\build\docs-replay\bad\synthetic-v1.json
  profile_id:  synthetic-v1
  region:      UNKNOWN
  game_build:  synthetic-build-1
  status:      SYNTHETIC
  messages:    7
  fixtures:    not verified
  warning W_PROFILE_FIXTURE_MISSING at ../../tests/Fixtures/decoded/synthetic_complete.decoded.json: referenced fixture is not present
  …（其余 9 条同类警告省略）
  error E_PROFILE_HASH at $.profile_sha256: profile_sha256 does not match the document
  result:      REFUSED
$ echo $?
1
```

固件警告源于该副本位于 `build/` 下，相对路径无法指向测试树。
该警告只会使 `fixture_verified` 变为 `false`，并非拒绝的原因。

### `--json`

```console
$ MentorRecorder.Collector.exe --validate-profile protocol-profiles/cn/cn-unsupported.json --json
{
  "path": "D:\\PRJ\\\u5BFC\u968F\u8BB0\u5F55\\protocol-profiles\\cn\\cn-unsupported.json",
  "profile_id": "cn-unsupported",
  "region": "CN",
  "game_build": "unknown",
  "status": "UNSUPPORTED",
  "message_count": 0,
  "fixture_verified": true,
  "errors": [],
  "warnings": [],
  "ok": true
}
```

`errors[]` / `warnings[]` 的元素形如 `{"code": …, "path": …, "message": …}`。
`ok` 表示 `errors` 为空。

### `--list-profiles`

```console
$ MentorRecorder.Collector.exe --list-profiles --profiles-dir protocol-profiles
protocol profiles root: <repo-root>\protocol-profiles
  cn-unsupported       CN       unknown                UNSUPPORTED fail-closed messages=0
  global-unsupported   GLOBAL   unknown                UNSUPPORTED fail-closed messages=0
  synthetic-cn-shape-v1 UNKNOWN synthetic-build-3      SYNTHETIC   usable      messages=4
  synthetic-v1         UNKNOWN  synthetic-build-1      SYNTHETIC   usable      messages=7
```

第四列为**目录级**状态，取值可能为 `AMBIGUOUS`；第五列表示该绑定是否允许状态机工作。
`synthetic-v1` 显示 `usable`，因为合成绑定对**离线重放**可用；
它在活体抓包路径上仍不可见。`--json` 另外给出 `path` / `fixture_verified` /
`error_count`。

### Python 侧

```console
$ python tools/protocol-profile-validator/validate.py protocol-profiles/synthetic/synthetic-v1.json protocol-profiles/cn/cn-unsupported.json protocol-profiles/global/global-unsupported.json
protocol-profiles/synthetic/synthetic-v1.json: OK
protocol-profiles/cn/cn-unsupported.json: OK
protocol-profiles/global/global-unsupported.json: OK
$ echo $?
0
```

失败时逐条打印原因，退出码取所有文件中最差的一个：

```console
$ python tools/protocol-profile-validator/validate.py build/docs-replay/bad/synthetic-v1.json
build/docs-replay/bad/synthetic-v1.json: FAILED
  - profile_sha256 mismatch: expected 305694db4442593ebf3cb8ebd3c319c809af116d05fb6f7a940cc194904c07b1
  - a SYNTHETIC profile may only live in protocol-profiles/synthetic/
```

---

## 两份实现的已知差异

除 §4 中 `SYNTHETIC` 位置检查的适用范围之外，两侧检查项一一对应：
Python 校验器接受的档案 Collector 亦接受，反之亦然。
此处保留而非抹平该差异，是因为 Python 侧更严格的一侧正是期望的默认。
**若将来统一实现，应由 C# 向 Python 看齐，而非放松 Python 侧。**

另有一处**表述**差异，判定结果相同：Python 侧对 `DUTY_RESULT.victory_values`
的非空检查不区分 `compatibility_status`，C# 侧对 `UNSUPPORTED` 跳过该检查。
由于 `UNSUPPORTED` 档案本就不允许声明任何消息，两侧结论始终一致。

---

## 相关文件

| 文件 | 作用 |
|---|---|
| [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md) | 格式规范、字段语义、证据要求 |
| [`../../protocol-profiles/README.md`](../../protocol-profiles/README.md) | 目录索引与状态含义 |
| `src/Collector/Protocol/Profiles/ProfileLoader.cs` | 权威校验流程 |
| `src/Collector/Protocol/Profiles/JsonSchemaValidator.cs` | 最小 JSON Schema 子集 |
| `src/Collector/Protocol/Profiles/CanonicalJson.cs` | 规范化 JSON 写入器 |
| `src/Collector/Protocol/Profiles/ProfileValidationReport.cs` | 报告与 `--json` 形状 |
| `src/Collector/Protocol/Profiles/ProfileCatalog.cs` | 目录扫描与 `AMBIGUOUS` 判定 |
| `src/Collector/Protocol/Profiles/ProfileSelector.cs` | `region` + `game_build` 选档，`allowSynthetic` |
| `tests/Collector.UnitTests/ProtocolProfileTests.cs` | 上述每一条检查的测试 |
