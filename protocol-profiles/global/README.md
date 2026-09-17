# 协议档案：国际服

> ## 状态：`UNSUPPORTED`（fail-closed 占位）
>
> 本目录**没有**任何真实的协议档案，`LIVE_CAPTURE_STATUS = UNVERIFIED`。
> 本目录**不含任何 opcode、任何结构偏移、任何 `mentor_roulette_id`**。
> 因此 Collector 在国际服上的协议档案状态为 `NONE`：
> `StartCapture` 返回 `ERR_PROFILE_UNSUPPORTED`（fail-closed），
> 不解析任何报文，也不写入任何记录。

## 档案文件格式

档案是一个 JSON 文件，命名为 `<profile-id>.json`，
`profile-id` 形如 `global-<游戏版本>-<构建判别符>`（全小写，只含 `a-z0-9.-`），
且必须与文件名一致，`region` 字段必须为 `GLOBAL`。

完整的字段说明、类型、健壮性检查要求与**证据（evidence）要求**，
见 [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)。

## 为什么本目录为空

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

开发机上既未安装 FINAL FANTASY XIV，也未安装 Npcap
（`LIVE_CAPTURE_STATUS = UNVERIFIED`），因此**不存在**任何合法证据，
本目录随之为空。此为有意为之，并非遗漏。

## 贡献档案

请先阅读：

1. [`../../docs/protocol-profile-format.md`](../../docs/protocol-profile-format.md)：格式与证据要求
2. [`../../docs/live-validation-guide.md`](../../docs/live-validation-guide.md)：真机验证流程
3. [`../../docs/privacy-boundary.md`](../../docs/privacy-boundary.md)：硬边界

档案中只应填写**贡献者亲自在本机观察到并可复现**的常量。`compatibility_status` 先设为 `"CANDIDATE"`（枚举只有 `VERIFIED` / `CANDIDATE` / `UNSUPPORTED` / `SYNTHETIC`，没有 `UNVERIFIED`），
所有被使用的常量均具备 evidence 之后，方可改为 `"VERIFIED"`。

提交时**不要**粘贴原始报文、角色名或任何其他玩家的信息。
来源不明的 opcode 表不予接受。
