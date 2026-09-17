# 协议档案 / Protocol Profiles

> **`PROTOCOL_PROFILE_STATUS = CN_VERIFIED_POP_TO_EXIT`**（2026-09-07）
>
> `cn/cn.2026.08.05.json` 是第一份真实的 `VERIFIED` 档案，声明弹窗（S2C `0x0323`）与换区
> （S2C `0x014a`）两条消息，`mentor_roulette_id = 9`，不含 `DUTY_RESULT`。
> 其 `finder_state` 声明为 `"role": "selector"`（见下文）。该档案另带一个可选的
> `calibration` 模板段（本机校准的形状知识，见 `../docs/protocol-profile-format.md` §3.6），并据此重新盖章，
> `profile_sha256` 为 `8c6ffec309e0597f723a09e5c787759475828aaf6152402d3c94a24af92b3cd1`。
> 每个常量的来源均记录在其 `provenance.evidence` 中。`global/` 仍只有 `UNSUPPORTED` 占位。
> `synthetic/synthetic-v1.json` 的每个常量均为**编造值**，仅服务于离线测试。

**协议档案**将特定客户端版本的网络协议细节从代码中分离出来，
形成可审计、可替换、可失效的数据文件。
`src/Collector/Protocol/Parsing/ProfileMessageParser.cs` 中**不含任何硬编码常量**：
解析器读取的字段、长度、字节序与允许取值，全部来自本目录的文件。

## 目录

```
protocol-profiles/
  README.md                 本文件
  profile.schema.json       JSON Schema（draft 2020-12 子集），加载器按它校验
  cn/
    README.md               国服：档案状态与证据要求
    cn-unsupported.json     UNSUPPORTED 占位，零消息（其他版本）
    cn.2026.08.05.json      VERIFIED，弹窗 + 换区，无 DUTY_RESULT
    cn.2026.08.05.candidate.json CANDIDATE，仅 hypotheses，与正式档案并行观察
  global/
    README.md               国际服：为什么本目录为空
    global-unsupported.json UNSUPPORTED 占位，零消息
  synthetic/
    synthetic-v1.json       SYNTHETIC，全部常量为编造值，仅供离线测试
  oodle-signatures/
    README.md               Oodle 签名档案：状态规则、reproduction 字段、验证记录
    cn.<build>.json         按 exe 哈希绑定的 Oodle 函数签名表（与协议档案是两回事）
```

`profile.schema.json` 不是档案，扫描时予以跳过。该文件同时**内嵌于 Collector 程序集**，
运行时校验使用构建时的那一份，而非磁盘上可被任意修改的文件。

`profile_id` 必须等于文件名主干；`region` 必须与目录一致
（`cn` → `CN`、`global` → `GLOBAL`、`synthetic` → `UNKNOWN`）。

## 状态

`compatibility_status` 只有四个取值：

| 状态 | 含义 | 行为 |
|---|---|---|
| `VERIFIED` | 每一条消息的 opcode 都有非 `SYNTHETIC` 的证据条目 | **唯一**允许自动记录的状态 |
| `CANDIDATE` | 结构已提出，但未在真机确认 | fail-closed：解析器拒绝一切 |
| `UNSUPPORTED` | 该区服 / 该版本没有可用档案 | fail-closed；且**不得声明任何消息**，`mentor_roulette_id` 必须为 `null` |
| `SYNTHETIC` | 为离线测试编造 | 只能放在 `synthetic/`；活体档案选择里**隐形** |

另有一个**不出现在文件中**、由目录扫描产生的状态 `AMBIGUOUS`：
两份均通过校验的档案声明同一个 `(region, game_build)` 时，**两份均被拒绝**。

若按文件名、修改时间或状态择一，记录下来的数据将取决于哪份文件恰好排在前面。
协议常量不接受这种隐式选择。

查看本机已安装的档案：

```bash
MentorRecorder.Collector.exe --list-profiles [--profiles-dir <path>] [--json]
```

## 为什么存在占位档案

一条硬规则：**没有证据就不写常量。**

`global/` 仍然只有占位档案，原因是没有国际服的合法证据。`cn/` 自 2026-09-07 起有 `cn.2026.08.05`，
其每一条常量都可在档案的 `provenance` 中追溯到本机流量或用户核对。

占位文件本身有其作用：它将"该区服已知，但没有可用档案"表达为显式且可校验的事实，
而非目录为空的含糊状态。`UNSUPPORTED` 档案零消息、
`mentor_roulette_id` 为 `null`，校验器强制这两点。

逐目录的详细说明见 [`cn/README.md`](cn/README.md) 与 [`global/README.md`](global/README.md)。

## 一份 `VERIFIED` 档案需要什么

完整规范见 [`../docs/protocol-profile-format.md`](../docs/protocol-profile-format.md)，
此处仅列出门槛：

1. **每一条消息的 opcode** 在 `provenance.evidence` 里都有一条
   `field = "messages.<NAME>.opcode"` 的条目，且 `method` 不是 `SYNTHETIC`；
2. 允许的 `method` 只有三种：
   `OBSERVED_LOCAL_TRAFFIC`（在本机观察自身流量，具备可复现的样本计数）、
   `PUBLIC_DOCUMENTATION`（公开且许可证兼容的文档，须给出 URL 与访问日期）、
   `USER_CONFIRMED`（使用者按[真机验证流程](../docs/live-validation-guide.md)确认）；
3. `mentor_roulette_id` 非 `null`，且 `CONTENT_FINDER_POP` / `ZONE_INITIALIZATION` 齐全；
   `DUTY_RESULT` 可选，一经声明必须带非空 `victory_values`，未声明时离开副本的记录以
   `UNKNOWN` 待复核收尾（[state-machine.md](../docs/state-machine.md) §3.10）；
4. `profile_sha256` 为规范化文档的哈希，
   通过 `python tools/protocol-profile-validator/validate.py --stamp <file>` 盖章；
5. `MentorRecorder.Collector.exe --validate-profile <file>` 返回 0。

## 筛选器字段 `"role": "selector"`

字段可声明 `"role"`，取值为 `"value"`（默认值，省略时即为该值）或 `"selector"`。

一个 opcode 常常承载多种用途。国服的 `0x0323` 既是申请回执，又是弹窗，还是离开后的状态
更新，档案通过 `finder_state == 3` 筛选出弹窗这一种。此类字段的约束未命中，表示该报文
承载的是其他用途，而非档案有误。标记为 `selector` 后，未命中走
`Ignore()`（计入"与记录无关"），不再计为解析失败，不进入错误环，也不写入 `parser_errors`。
一次完全正常的导随因此不再在抓包页显示"失败 2"。

仅将真正起筛选作用的字段标记为 `selector`。需要读取取值的字段（`roulette_id`、
`territory_id`、`outcome` 等）保持默认的 `value`，否则偏移填写错误将被静默
计入 `ignored`。规则详见
[`../docs/protocol-profile-format.md`](../docs/protocol-profile-format.md) §3.4。

**不接受**：推测值、试错得出的数值、凭外观判断的数值、从 AGPL 项目复制的常量表、
从客户端逆向得到的内部结构，以及任何无法说明来源的数字。

贡献流程依照
[`../docs/protocol-profile-format.md`](../docs/protocol-profile-format.md) §10 执行：
先从占位档案复制，`compatibility_status` 先写为 `"CANDIDATE"`；
真机验证通过并补齐 evidence 之后，方可改为 `"VERIFIED"` 并重新盖章。
提交时**不要**粘贴原始报文、角色名或任何其他玩家的信息。

## 合成档案永远不进发布包

`synthetic/*.json` 复制至**构建**输出目录，供离线重放与测试使用，
但**不会**进入 `dotnet publish` 的产物：
`src/Collector/MentorRecorder.Collector.csproj` 对其声明了
`CopyToPublishDirectory="Never"`。

理由在于：用于真机验证的发布包**绝不能**将编造的 opcode 作为"已安装的档案"
呈现。合成档案仅在显式传入路径（`--profile` / `--replay-decoded`）
或调用方显式指定 `allowSynthetic` 时使用，活体抓包路径不会读取。

合成档案随源码仓库一起维护，供离线测试使用，但 `scripts/package.ps1` 会断言发布目录中
不存在 `protocol-profiles/synthetic/`，任何合成档案进入发布包都会令打包失败。

## 相关文档

| 文档 | 内容 |
|---|---|
| [`../docs/protocol-profile-format.md`](../docs/protocol-profile-format.md) | 字段规范、证据要求、加载流程、解析失败分类 |
| [`../tools/protocol-profile-validator/README.md`](../tools/protocol-profile-validator/README.md) | 校验器的每一条检查与实际输出 |
| [`../docs/live-validation-guide.md`](../docs/live-validation-guide.md) | 真机验证流程 |
| [`../docs/privacy-boundary.md`](../docs/privacy-boundary.md) | 硬边界：不推测 opcode，不长期保存原始报文 |
| [`oodle-signatures/README.md`](oodle-signatures/README.md) | Oodle 签名档案：状态规则、`reproduction` 字段、验证记录 |
| [`../tests/Fixtures/README.md`](../tests/Fixtures/README.md) | `synthetic-v1.json` 背书的解码固件 |
