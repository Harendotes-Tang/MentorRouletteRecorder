# Oodle 签名档案（`protocol-profiles/oodle-signatures/`）

Machina.FFXIV 通过对 `ffxiv_dx11.exe` 的**文件副本**执行签名扫描，定位 Oodle 网络压缩函数
（见 [privacy-boundary.md](../../docs/privacy-boundary.md) §4.1 `DEC-OODLE-01`）。
Machina 内置的签名表按国际服客户端维护。国服客户端的寄存器分配不同，
`OodleNetwork1TCP_Train` / `OodleNetwork1UDP_Train` 两条内置签名在国服上无法命中。
本目录存放按**客户端 exe 哈希**绑定的替代签名表。

## 文件

| 文件 | 说明 |
|---|---|
| `cn.<game_build>.json` | 签名档案：12 条调用点模式 + 解析出的 RVA + `exe_sha256` / `exe_size` 双绑定 + `profile_sha256` 自校验 + `status` + `reproduction` |
| `evidence/<build>.oodle-verification.trace.jsonl` + `.sha256` | 将档案升级为 `VERIFIED` 所依据的脱敏 opcode 级取证文件（`--capture-trace` 产物，不含任何报文内容 / 地址 / 角色名） |

档案由 `tools/oodle-signature-finder/find_signatures.py` 从本机 exe 生成，
运行时由 `src/Collector/Capture/OodleSignatureRuntime.cs` 加载。
region、game build、exe 大小、exe SHA-256 四项全部精确匹配方可使用；
12 条签名必须全部恰好命中一次且 RVA 与档案一致，否则拒绝安装并回退到 Machina 内置表。

**模式回退（0.3.1 起）。** 游戏补丁之后，没有任何档案能按哈希匹配新的 exe，而内置表在国服上
无法定位训练函数，补丁当日因此无法解码。此时运行时取同区服**最新的 VERIFIED** 档案作为
模式捐赠者：先按文件字节核对 12 条通配模式各恰好命中一次，且相对跳转落在文件内；再对映射后的
镜像执行正式扫描，仅要求全部恰好命中一次，**不比对**档案中的 RVA。任一条核对落空即退回内置表，
不因此拒绝抓包。日志与 `CaptureStatus.oodle_signature_source` 记为 `pattern-fallback`，
`profile_id` 为捐赠档案。模式回退仅用于使补丁后的客户端能够解码与校准；补丁后的正式签名档案仍须用
`find_signatures.py` 生成并验证。

## 状态规则

| `status` | 含义 | 何时使用 |
|---|---|---|
| `CANDIDATE` | 工具产出，未经真实流量验证 | 仅 `--capture-trace` 与 IPC 取证会话（显式 opt-in）加载 |
| `VERIFIED` | 一次真实会话解码 ≥ 1000 条消息且解码错误为 0 | 常规抓包（`StartCapture`）直接加载 |

升级为 `VERIFIED` 时仅修改 `status` 并重算 `profile_sha256`（canonical JSON，
与 `CanonicalJson.cs` 同一语法），同时将取证文件与其 `.sha256` 置入 `evidence/`。

## `reproduction`：是否与已知正确答案比对过

档案可包含一个可选的根字段 `reproduction`：

| 取值 | 含义 |
|---|---|
| `"checked"` | 生成时该 exe 位于 `find_signatures.py` 的 `KNOWN_GOOD` 表中，工具已将解析出的 RVA 与已知正确答案逐条比对，且全部一致 |
| `"unchecked"` | 无可比对的已知正确答案，属新客户端版本的正常情况。签名仍要求 12 条全部恰好命中一次，但未经第二来源交叉验证 |
| 字段不存在 | 工具记录该项之前生成的旧档案。照常加载，`OodleSignatureProfile.Reproduction` 为 null |

其他取值（包括 `"failed"`）或非字符串一律拒绝加载：
`reproduction must be checked or unchecked`。工具本身也不会写出 `"failed"`：
复现检查不通过时，工具在更早的环节即拒绝生成档案。

该字段**在 `profile_sha256` 的规范化哈希范围内**，事后手工将
`unchecked` 改为 `checked` 会导致哈希不匹配，档案被拒绝加载。该字段是记录，不是开关：
`reproduction` 的取值**不**影响档案是否被使用，是否使用由 `status` 决定。

## 验证记录

### cn.2026.08.05：VERIFIED（2026-09-05）

| 项 | 值 |
|---|---|
| 客户端 | 国服 `2026.08.05.0000.0000`，`ffxiv_dx11.exe` SHA-256 `e06704e3…dd4e1a6`，51,952,384 字节 |
| Npcap | 1.88（WinPcap 兼容模式），网卡 Meta Tunnel |
| 取证方式 | 标题画面启动 `--capture-trace --adapter <Meta> --duration-seconds 300`，随后登录角色并传送一次 |
| 结果 | 3935 条消息全部解码，解码错误 0，丢弃 0，3 条连接（大厅 16 / 区域 3865，120 种 opcode / 聊天 54） |
| 证据 | `evidence/cn.2026.08.05.oodle-verification.trace.jsonl`，SHA-256 `7d74758061481862a943d659e52a8d29942cfa53a6c4a6b871364e91a82a370f` |
| 档案哈希 | `profile_sha256 = bf74fb5b1a5c18e153b619653b3f5db301145651752fd00a3eef2acfe350e2a4` |

观察结论：中途接入已有连接时全部报文分帧失败（Oodle 字典状态不完整），
此为预期行为，并非签名错误。国服客户端在传送换区时**不会**新建区域连接，
因此取证必须在登录之前开始；标题画面为合适时机，此时游戏进程没有 TCP 连接。

本记录仅证明 **Oodle 解压链路** 在该客户端上正确，**不**证明任何协议 opcode。
协议层的活体状态由 `--capture-doctor` 如实报出
（`CaptureDiagnostics.LiveCaptureStatus`，当前为 `VERIFIED_POP_TO_EXIT`）。
请以该自检报告为准，本文件不复述会过期的状态词。
见 [live-validation-guide.md](../../docs/live-validation-guide.md)。
