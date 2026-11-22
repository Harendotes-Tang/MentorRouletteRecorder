# 协议档案：国际服

> ## 状态：`UNSUPPORTED`（fail-closed）；另有一份 `CANDIDATE`
>
> 本目录**没有**可正式记录的协议档案，
> 候选字段用于离线核对，正式记录所需的 `mentor_roulette_id` 仍为空。
> 因此 Collector 在国际服上的协议档案状态为 `UNSUPPORTED_BUILD`（读不到客户端版本时为 `NONE`）：
> `StartCapture` 返回 `ERR_PROFILE_UNSUPPORTED`（fail-closed），
> 不解析任何报文，也不写入任何记录。

## 文件

| 文件 | 状态 | 用途 |
|---|---|---|
| `global-unsupported.json` | `UNSUPPORTED` | 占位：零消息，表达"国际服已知，但没有可用档案" |
| `global.2026.11.03.candidate.json` | `CANDIDATE` | 国际服 `2026.11.03.0000.0000` 的 19 项 opcode 假设，供新区服采证页的「第二轮」记录报文内容 |

### `global.2026.11.03.candidate.json`

- **来源。** 全部假设来自 2026-11-20 一位测试者的一轮新区服采证（软件 1.9.1-beta.2，元数据 trace，
  SHA-256 `bf39692b384ae41de511fe6748edca1c029543e5b22526b16ef389e0f029e6ad`，由维护者保存，不在仓库中）。
  该 trace 只有方向、opcode、长度、时间与摘要；每一项假设在 `provenance.evidence` 中都有一条
  `hypotheses.<名称>` 的 `OBSERVED_LOCAL_TRAFFIC` 条目，写明出现次数。
- **内容。** `messages` 为空，`mentor_roulette_id` 为 `null`，不带 `calibration`。分组：
  `queue`（排本申请 `0x0215` 与另一候选 `0x022b`、两个匹配候选、两条附带报文）、`finder`（状态更新候选 `0x016f`）、
  `identity`（两个 136 字节副本识别候选、四个 16 字节职业候选）、`zone_load`（六个换区成员，作时间锚点）。
  不确定的候选各自保留为独立的假设，不预先择一。按国服形状推测的字段位置只写在 `note` 中，未经证实。
- **用途。** 玩家在新区服采证页顶部的「第二轮」勾选同意后，候选验证开启，全部长度上界不超过 512 字节的假设
  （19 项中的 14 项）保存负载，用来确定任务编号、副本与职业的字段位置
  （[`../../docs/privacy-boundary.md`](../../docs/privacy-boundary.md) §5.1.1，测试步骤见
  [`../../docs/global-server-testing.md`](../../docs/global-server-testing.md) §7）。
- **猜测位置。** 三项假设带未证实的 `guesses`（[`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md) §3.5.1），
  依据是 2026-11-21 收到的一份第二轮证据（软件 1.9.1-beta.3，SHA-256
  `af709ee5d0c2f04ab735762c720f69a3eb7e981175e886e77681c04e51be4cdb`，由维护者保存；该版本的记录在排本申请后中断，只有前半段）：
  `QUEUE_REGISTRATION` 第 0 字节为随机任务编号（两次申请为 2、9）；`FINDER_STATE_NOTIFICATION` 第 1 字节猜测为职业编号
  （换职业前后为 40、24）；`TERRITORY_CANDIDATE_00AB` 的 u16@2 猜测为区域编号（只见过 641）。它们只用于「第二轮」卡片显示
  软件读到的任务、职业与副本，供测试者改正，不是字段，不参与解析。同一份证据还表明 `FINDER_ACTION_022B` 是移动报文、
  `POP_CANDIDATE_0332` 在未排本的登录后出现、`ZONE_INIT_031F` 含可读的文字标签；这三项的处理留待完整的一轮证据。
- **不做什么。** 普通档案目录不加载候选（`ProfileCatalog.LoadDefault()` 默认 `allowCandidate: false`），
  正式选择排除候选（`ProfileSelector.Select`），模板选择只取 `VERIFIED`（`CalibrationTemplate.Select`）。
  因此这份档案不会让国际服开始记录、不会启动本机校准，没开启「第二轮」的玩家看到的仍是"暂不支持"。
- `0x02fc` 与国服候选同号但长度不同（664 / 104 字节），是两条不相干的报文；研究白名单只要求至少一份候选档案把它声明为可保存，
  观测时再按当次会话的档案判断，因此国际服这一项（超过 512 字节，不保存负载）不影响国服。

## 2026.11.03 离线验证

11 月 20 日和 21 日的两场指导者随机分别进入奇坦那神影洞和博物战舰无限回廊。两次均在进本前出现一次 40 字节的 `0x03bf`。21 日的正文在偏移 2 读出 9，仍缺普通随机实际匹配的对照。24 字节的 `0x02cb` 在两场分别出现四次和三次，无法据此确定本人点击确认的时刻。

21 日的排本申请依次为 2、9，状态更新中的职业为 25、40。进本时 `0x00ab` 的 u16@2 为区域 430，u16@6 为 35。该副本内的 `0x027d` 有 48/136 字节为 FF。20 日的完整 trace 只有摘要，缺少可还原区域的正文。19 日没有报文，20 日 beta.3 的记录在排队申请后中断。

维护者可用 `tools/candidate-evidence/replay_global.py` 核对带正文的候选导出。回放在同一会话和连接内核对导随申请、候选匹配通知与副本区域；时间窗口从通知起算。如在弹窗后撤回、120 秒内再独立进入已知副本，这三个信号仍可能产生候选进本；缺少拒绝或本人确认的独立证据。返回的条数用于核对可能的导随进本，通关另需结算证据。国际服登录角色仍需手选。下一次只采普通随机实际进本，见[测试步骤](../../docs/global-server-testing.md#current-test)。

## 档案文件格式

档案是一个 JSON 文件，命名为 `<profile-id>.json`，
`profile-id` 形如 `global.<游戏版本>`（与国服的 `cn.2026.08.05` 同一写法；全小写，只含 `a-z0-9.-`），
且必须与文件名一致，`region` 字段必须为 `GLOBAL`。

完整的字段说明、类型、健壮性检查要求与**证据（evidence）要求**，
见 [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)。

## 为什么还没有可正式记录的档案

本项目有一条硬规则：

> **没有证据就不写常量。**

每一个非 `null` 的 `opcode` / `offset` / `mentor_roulette_id` 都必须在档案的
`evidence` 数组里有对应条目，说明它是怎么来的：

| `method` | 含义 |
|---|---|
| `OBSERVED_LOCAL_TRAFFIC` | 在自己的机器上观察自己的流量得到，并有可复现的样本计数 |
| `PUBLIC_DOCUMENTATION` | 来自公开且许可证兼容的文档，须给出 URL 与访问日期 |
| `USER_CONFIRMED` | 由使用者按真机验证流程确认 |

**不接受**：推测值、试错得出的数值、从 AGPL 项目复制的常量表、
从客户端逆向得到的内部结构，以及任何无法说明来源的数字。

在 2026-11-20 之前，本项目没有任何国际服客户端在本机观察到的抓包样本，因此本目录只有占位档案。
现有的元数据样本足以提出 opcode 假设（上面的候选档案），但不足以确定任何字段偏移，
所以仍没有可正式记录的档案。这是有意设置的限制，并非遗漏。

## 贡献档案

请先阅读：

1. [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)：格式与证据要求
2. [`../../docs/live-validation-guide.md`](../../docs/live-validation-guide.md)：真机验证流程
3. [`../../docs/privacy-boundary.md`](../../docs/privacy-boundary.md)：硬边界

档案中只应填写**贡献者亲自在本机观察到并可复现**的常量。`compatibility_status` 先设为 `"CANDIDATE"`（枚举只有 `VERIFIED` / `CANDIDATE` / `UNSUPPORTED` / `SYNTHETIC`，没有 `UNVERIFIED`），
所有使用的常量均有对应的 evidence 之后，方可改为 `"VERIFIED"`。

提交时**不要**粘贴原始报文、角色名或任何其他玩家的信息。
来源不明的 opcode 表不予接受。
