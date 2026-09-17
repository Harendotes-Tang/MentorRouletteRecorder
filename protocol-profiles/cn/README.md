# 协议档案：国服

> ## 状态：`VERIFIED`（`cn.2026.08.05`，2026-09-07）
>
> 自任务确认弹窗至离开副本的流程可自动记录，通关判定尚未补齐。
> `cn.2026.08.05.json` 声明 `CONTENT_FINDER_POP`（S2C `0x0323`，第 16 字节为随机任务编号，
> 第 9 字节为 3 表示已匹配）与 `ZONE_INITIALIZATION`（S2C `0x014a`），`mentor_roulette_id = 9`。
> 该档案不含 `DUTY_RESULT`，因此离开副本的记录以 `UNKNOWN` 收尾并标记待复核
> （[state-machine.md](../../docs/state-machine.md) §3.10）。
> `cn.2026.08.05.candidate.json` 与之并行，仅用于观测与副本内采样。
> 其他国服版本仍由 `cn-unsupported.json` fail-closed。

## 档案文件格式

档案是一个 JSON 文件，命名为 `<profile-id>.json`，
`profile-id` 形如 `cn-<游戏版本>-<构建判别符>`（全小写，只含 `a-z0-9.-`），
且必须与文件名一致，`region` 字段必须为 `CN`。

完整的字段说明、类型、健壮性检查要求与**证据（evidence）要求**，
见 [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)。

## 证据要求

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

`cn.2026.08.05` 的证据全部来自本机被动抓包与用户核对，逐条记录在档案的
`provenance.evidence` 中（见 [live-validation-guide.md](../../docs/live-validation-guide.md) §7）。

## 贡献档案

请先阅读：

1. [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)：格式与证据要求
2. [`../../docs/live-validation-guide.md`](../../docs/live-validation-guide.md)：真机验证流程
3. [`../../docs/privacy-boundary.md`](../../docs/privacy-boundary.md)：硬边界

档案中只应填写**贡献者亲自在本机观察到并可复现**的常量。`compatibility_status` 先设为 `"CANDIDATE"`（枚举只有 `VERIFIED` / `CANDIDATE` / `UNSUPPORTED` / `SYNTHETIC`，没有 `UNVERIFIED`），
所有被使用的常量均具备 evidence 之后，方可改为 `"VERIFIED"`。

提交时**不要**粘贴原始报文、角色名或任何其他玩家的信息。
来源不明的 opcode 表不予接受。
