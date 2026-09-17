# 离线字段推导

本工具基于 Python 3 标准库，输入为 Collector 的 `MentorRecorder.CandidateEvidence` schema 1 专用导出文件。
本文面向执行离线字段推导的维护者，说明使用方式、判定口径与输出含义。
本工具只在本地分析，不访问网络，不读取游戏，不修改已发布档案或正式记录。

先运行稳定性分析：

```powershell
python tools/candidate-evidence/derive_fields.py C:\evidence\candidate.json --output-dir C:\evidence\analysis-1
```

核对后由维护者**明确指定**哪一个 hypothesis 对应弹窗，以及要寻找的 roulette id 候选：

```powershell
python tools/candidate-evidence/derive_fields.py C:\evidence\candidate.json --popup-hypothesis FINDER_STATE_NOTIFICATION --roulette-id <用户提供的候选值> --output-dir C:\evidence\analysis-2
```

`FINDER_STATE_NOTIFICATION` 目前仅为候选名称，本工具不默认将其认定为弹窗。本仓库 `data/` 不含权威的
roulette id 来源，也没有内置指导者 id，因此必须通过 `--roulette-id` 输入 1..65535 的十进制或 `0x` 十六进制整数。
两项推导参数必须一并提供；同时省略时仅分析字节稳定性。

本工具输出两个新文件。任一目标文件已存在时拒绝覆盖：

- `field-candidates.md`：每个 byte offset 的常量、递增、变化分类，以及 roulette 字段候选的偏移、类型、样本支持数。
- `candidate-messages.json`：独立草稿容器，包含 `messages`、全部 `message_candidates`、对应的 `field_candidates` 证据和分组分析。
  `compatibility_status` 恒为 `CANDIDATE`，`mentor_roulette_id` 恒为 `null`。该文件不是可直接加载的完整协议档案。

默认输出目录为输入文件同级的 `<文件名去扩展名>.derived`。`--output-dir` 可指定其他本地目录。
UNC/URL 路径以及仓库 `protocol-profiles` 内的输出一律拒绝。输入证据可能包含原始负载，应使用个人本地目录。
报告和 JSON 不复制完整 payload，只保留推导所需的统计、候选 id 与输入文件 SHA-256。原始证据不应提交到仓库。

## 分组和支持数

分组键为 `profile_id / hypothesis_name / opcode / direction / payload_length`。不同档案版本、负载长度和方向的样本不会混用。
同一 observation id 只计一次；重复 id 的内容若冲突，则拒绝输入。单个独立样本只能标记为 `insufficient`，不足以推导字段。
`payload_hex` 缺失或为 null 的观测，以及 `ZONE_LOAD` 推断锚点，均跳过并计数。

- `constant`：至少两个独立样本在该字节相等。
- `incrementing`：存在同会话、同连接的时序比较，且所有此类比较中的时间和值均严格递增。
  各连接独立排序。单例连接不产生比较；相同时间戳、字节回绕与数值下降均不判定为递增。
- `random`：有变化但未满足上述条件，**不证明数据随机性**。

字段候选只使用维护者明确指定弹窗 hypothesis 的有负载、`CORRECT` 样本。
扫描 little-endian `u8` / `u16` / `u32`；同一偏移和宽度至少在两个不同 observation id 中等于输入值才进入备选。
`WRONG`、`UNSURE`、未核对和其他 hypothesis 不提供支持。
部分匹配可能来自不同随机任务，也可能出于巧合。报告同时列出匹配数和 CORRECT 样本总数，不足以据此认定字段语义。

偏移、宽度或分组存在歧义时，`messages` 为空。`message_candidates` 中每个元素都独立符合真实 profile schema 的
message/field 结构，可由维护者单独复制；其序号对应 `field_candidates` 的档案身份和支持信息。
只有唯一且全部样本支持的候选会自动进入 `messages`。部分支持必须显式选择：

```powershell
python tools/candidate-evidence/derive_fields.py C:\evidence\candidate.json --popup-hypothesis FINDER_STATE_NOTIFICATION --roulette-id <用户提供的候选值> --field 12:u16 --profile-id <证据中的档案id> --opcode 0x0323 --direction S2C --payload-length 40 --output-dir C:\evidence\analysis-selected
```

其中 `12:u16` 仅用于示范 CLI 语法，**不是任何真实报文的已确认偏移**。`--field` 只接受已有两个样本支持的备选。
`--profile-id`、`--opcode`、`--direction`、`--payload-length` 均为可选参数，仅用于缩小字段搜索范围，字节稳定性报告仍保留全部分组。
同一字段选择若仍对应多个分组，本工具继续报告歧义，不自动选定档案或 opcode。

## 退出状态和验证

成功写出报告时退出码为 0，`STATUS` 可为 `ANALYSIS_ONLY`、`INSUFFICIENT_EVIDENCE`、`NO_MATCH`、`PARTIAL_SUPPORT`、
`AMBIGUOUS` 或 `DRAFT_READY`。**`DRAFT_READY` 也仅表示存在可复核草稿**，不提升任何档案的信任状态。
格式、头声明、摘要、长度、参数、输出冲突或本地 I/O 错误时退出码为 2。
本工具核验每段负载的小写十六进制形式、长度、SHA-256 前 12 位，以及头部实际负载的 opcode 集合。
负载长度上限为 512 字节，与 Collector 的 `ResearchPayloadPolicy.MaxPayloadBytes` 及 `migrations/0007` 一致。

历史核对行随证据保留。推导只读取导出事务中各观测的最新核对摘要，不重放或改写历史。

```powershell
python -m unittest discover -s tools/candidate-evidence -p "test_*.py" -v
```

测试只在临时目录生成合成证据，覆盖输入拒绝、跨会话隔离、端序、歧义、CLI 文件保护和真实 profile schema 片段校验。
维护者仍须离线复核，并在另一次会话中复现字段。本工具不执行 VERIFIED 提升。
