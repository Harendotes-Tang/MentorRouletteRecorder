# Oodle 签名档案（`protocol-profiles/oodle-signatures/`）

Machina.FFXIV 通过对 `ffxiv_dx11.exe` 的**文件副本**执行签名扫描，定位 Oodle 网络压缩函数
（见 [privacy-boundary.md](../../docs/privacy-boundary.md) §4.1 `DEC-OODLE-01`）。
Machina 内置的签名表按国际服客户端维护。国服客户端的寄存器分配不同，
`OodleNetwork1TCP_Train` / `OodleNetwork1UDP_Train` 两条内置签名在国服上无法命中。
本目录存放按**客户端 exe 哈希**绑定的替代签名表。

## 文件

| 文件 | 说明 |
|---|---|
| `cn.<game_build>.json`、`global.<game_build>.json` | 签名档案：12 条调用点模式 + 解析出的 RVA + `exe_sha256` / `exe_size` 双绑定 + `profile_sha256` 自校验 + `status` + `reproduction` + `source` |
| `evidence/<build>.oodle-verification.trace.jsonl` + `.sha256` | 将档案升级为 `VERIFIED` 所依据的脱敏 opcode 级取证文件（`--capture-trace` 产物，不含任何报文内容 / 地址 / 角色名） |

档案由 `tools/oodle-signature-finder/find_signatures.py` 从本机 exe 生成，
运行时由 `src/Collector/Capture/OodleSignatureRuntime.cs` 加载。
region、game build、exe 大小、exe SHA-256 四项全部精确匹配方可使用；
12 条签名必须全部恰好命中一次且 RVA 与档案一致，否则拒绝安装，本次抓包不开始
（只有模式回退与本机查找的集合安装失败时才退回 Machina 内置表）。

**模式回退（0.3.1 起）。** 游戏补丁之后，没有任何档案能按哈希匹配新的 exe，而内置表在国服上
无法定位训练函数，补丁当日因此无法解码。此时运行时取同区服**最新的 VERIFIED** 档案作为
模式来源档案：先按文件字节核对 12 条通配模式各恰好命中一次，且相对跳转落在文件内；再对映射后的
镜像执行正式扫描，仅要求全部恰好命中一次，**不比对**档案中的 RVA。任一条核对失败即退回内置表，
不因此拒绝抓包。日志与 `CaptureStatus.oodle_signature_source` 记为 `pattern-fallback`，
`profile_id` 为提供模式的档案。模式回退仅用于使补丁后的客户端能够解码与校准；补丁后的正式签名档案仍须用
`find_signatures.py` 生成并验证。

**本机查找（1.9.1 测试版起，仅取证会话）。** 运行中的客户端没有按哈希匹配的档案，且没有可用的同区服模式来源档案时
（没有任何来源档案，例如尚未适配的国际服、繁中服；或有来源档案但其模式已不适配该文件，日志记
`oodle_signature_donor_rejected`，例如补丁后的国服），允许候选签名的取证会话（「新区服采证」与 `--capture-trace`）会由
`OodleSignatureDiscovery` 在本机查找 12 处调用点：先按原样尝试内置表与**各区服** VERIFIED 档案的模式；都不命中时，
再允许其中**恰好一个**字面字节不同（编译器改用另一个寄存器时的典型差异，国服的两条 `Train` 即属此类），把该字节改为通配后
仍须恰好命中一次。命中处只有满足以下条件才算调用点：模式及其后 4 字节调用操作数位于 PE 头标为可执行的节内；
直接调用（`e8`）须跳入可执行的节，间接调用（`ff 15`）读取的 8 字节指针槽须位于可读的节内。两条签名落在同一处调用时，
两条都不采用。12 处全部确定后按模式回退的同一道门槛安装（映射镜像中各恰好命中一次，不比对 RVA），
`oodle_signature_source` 仍记为 `pattern-fallback`，`oodle_profile_id` 以 `discovered.` 开头，状态为 `CANDIDATE`。
正式记录（`StartCapture`）不做这一步。查找结果是推断而不是证据，不会写入任何记录；但 Machina 会在采集器进程内
调用这些位置上的函数，猜错时可能使采集器直接退出，而不只是表现为解码失败计数。

为此，本机查找到的集合交给 Machina 之前，运行时在采集器数据目录（数据库所在目录）写入标记文件
`oodle-discovered-<exe sha256>.marker`，内容只有该哈希；该轮抓包正常停止时删除，安装被拒绝时也删除。
下次查找时若发现同一哈希的标记，说明上一轮没有正常结束：本安装不再对该 exe 查找，本轮改用内置表，日志
`oodle_signature_source` 的 `fallback_reason` 写明「a signature set found on this machine did not end its last round
cleanly; not searched again」，并以 `reason = crashed` 写出下述最后手段的内容。标记文件保留；手动删除后恢复查找。

无论是否找全，本机日志都会写一条 `oodle_signature_discovery`：`exe_sha256`（8 位一组）、`exe_size`、每处采用的模式及其来源、
被放宽的字节位置与实际取值、缺失与歧义的签名名称；安装后的 `oodle_signature_scan` 另记各调用解析出的 RVA（`rvas`）。
这些内容随取证回传的 `collector-*.log` 一并到达维护者（这一轮用了本机查找到的集合或查找失败时，采证页把日志列为必须回传），
告诉维护者在该客户端上找到了什么、在哪里。但目前还不能只凭日志写出正式档案：`find_signatures.py` 必须以 `--exe`
读取客户端文件，只重新推导两条 `Train`，其余 10 条照搬 Machina 内置表并要求各恰好命中一次；内置模式缺失或须放宽字节的客户端
会被它拒绝。为这类客户端写正式档案，目前仍须取得其 exe，并扩展该工具或手工编写档案（手工计算 `profile_sha256`，
且没有工具的调用目标与分类核对）。
内置表扫描失败时，日志另有 `oodle_signature_missing`，只记缺失签名的枚举名。

**最后手段：调用点附近的指令字节（不轻易使用）。** 只有三种情况下，日志才会额外写入
`oodle_signature_sections`（PE 节表：各节的 RVA、文件偏移与大小）和若干条 `oodle_signature_context`：

1. 本机查找没能确定全部 12 处（`reason = incomplete`）；
2. 12 处都确定了，但这一轮取证结束时没有解出任何 IPC 分段（控制分段不计）且出现了解码失败（`reason = decode_failed`），
   说明其中有一处找错了；
3. 上一轮使用本机查找到的集合时没有正常结束，见上文标记文件（`reason = crashed`）。

这些情况下，仅凭签名名称无法在维护者一侧写出档案，不留这些内容就只能请测试者再测一次。每条 `oodle_signature_context`
是游戏主程序 `ffxiv_dx11.exe` 里的一小段**机器码**，按同一规则截取：从命中处之前 N 字节起，到其后 4 字节调用操作数之后
M 字节止，每段不超过 384 字节，并截在文件范围内。已确定的调用点 N = 96、M = 288，实际即从调用点前 96 字节起的 384 字节
（`Train` 两条模式即由 `TCP_State_Size` 之后的指令推导）；未确定的签名 N = M = 32，取全文件中最接近的至多 5 处
（相差的字面字节最少者优先，相同时取靠前者；与已知模式相差不超过 3 个字面字节）。
内容不含任何报文、进程内存地址、网络地址、角色或账号信息（RVA 与文件偏移是游戏文件内的位置），性质与本目录公开的签名模式
相同，只是更长。查找成功且解码正常时不写入这些内容。

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
协议层的实时采集验证状态由 `--capture-doctor` 如实报出
（`CaptureDiagnostics.LiveCaptureStatus`，当前为 `VERIFIED_POP_TO_EXIT`）。
请以该自检报告为准，本文件不复述会过期的状态词。
见 [live-validation-guide.md](../../docs/live-validation-guide.md)。

### global.2026.11.03：VERIFIED（2026-11-20）

| 项 | 值 |
|---|---|
| 客户端 | 国际服 `2026.11.03.0000.0000`，`ffxiv_dx11.exe` SHA-256 `ceb4260b…f2f39339`，51,963,136 字节 |
| 来源 | `source = collector/oodle-signature-discovery`：模式与 RVA 取自测试者电脑上「本机查找」的日志（`oodle_signature_discovery`、`oodle_signature_scan`），不是 `find_signatures.py` 对 exe 的产物，因此 `reproduction = unchecked`。12 处全部按原样命中，没有放宽任何字节；两条 `Train` 用的是国服档案的模式，其余 10 条与 Machina 内置表相同 |
| 取证方式 | 测试者在 1.9.1-beta.2 中完成一轮「新区服采证」（自动选择网卡，使用加速器），从标题画面开始，登录后完成一次指导者任务 |
| 结果 | 41,965 条消息全部解码，解码错误 0，丢弃 0，3 条连接 |
| 证据 | 取证文件 SHA-256 `bf39692b384ae41de511fe6748edca1c029543e5b22526b16ef389e0f029e6ad`，记于 `evidence/global.2026.11.03.oodle-verification.trace.jsonl.sha256`。**文件本身不入库**：它含测试者这一局排队、进本与通关的时间线，由维护者另行保存 |
| 档案哈希 | `profile_sha256 = 0c04c3e321244a647652950c8a1411944da54b4e990a83988dc5a83bcf4f3dda` |

`source` 字段只接受两个取值：`tools/oodle-signature-finder`（由查找工具对 exe 生成）与
`collector/oodle-signature-discovery`（由某台电脑上的本机查找得出、从该轮日志抄录）。后者不声称工具的交叉校验，
升级为 `VERIFIED` 的门槛相同。本记录同样只证明 Oodle 解压链路正确，不证明任何协议 opcode；国际服目前仍没有协议档案。
