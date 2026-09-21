# 静态硬边界检查 / Static Boundary Check

本工具在仓库源码中查找本项目**绝不允许出现**的标识符。本文面向需要新增规则或排查告警的
维护者，说明扫描范围、规则组织方式与自测要求。

扫描范围为 `src/`（含 `src/Desktop/qml/`）、`tests/`、`tools/`、`scripts/`、
`installer/`（随发布分发、以管理员身份运行的安装脚本）、`.github/`（在 CI 运行器中执行第三方代码的工作流），
以及仓库根目录的 `CMakeLists.txt`、`Directory.Build.props`、`Directory.Build.targets`、
`MentorRecorder.sln`，即 `rules.json` 的 `scan_files`。
命中任意一条规则即以非零退出码结束，并逐条打印 `文件:行号`。

`contracts/` 不在扫描范围内。IPC 契约以散文写明边界（"no TCP, no WebSocket"），属于文档而非代码。
`.md` 与 `.csv` 同理不列入 `include_extensions`。

规则依据：[`../../docs/privacy-boundary.md`](../../docs/privacy-boundary.md)。

## 运行

```powershell
python tools/static-boundary-check/check.py            # 人类可读输出
python tools/static-boundary-check/check.py --json     # 机器可读输出
pwsh -File scripts/verify.ps1                          # 边界检查 + dotnet test
```

退出码：`0` 无违规；`1` 有违规；`2` 检查器自身无法运行（规则文件损坏等）。

## 规则位置

全部模式定义在 `rules.json` 中，**不在** `check.py` 中。
因此检查器自身的源码不含任何字面命中。本目录同时被排除在扫描范围之外，
避免自我告警。

## 规则分类

| 分类 | 规则 | 拦截什么 |
|---|---|---|
| `process-injection` | `INJ-001`…`INJ-008` | `ReadProcessMemory` / `WriteProcessMemory` / `VirtualAllocEx` / `CreateRemoteThread` / `SetWindowsHookEx` 及底层注入原语；打开他进程句柄（`OpenProcess` / `DebugActiveProcess`）；内部会打开句柄读模块表的 `Process.MainModule` |
| `injected-hook` | `DEU-001`…`DEU-003` | 启用 Deucalion 注入式钩子、引用其 API 或其原生载荷 |
| `packet-send` | `CAP-001` `CAP-002` | `pcap_sendpacket` / `pcap_inject`（抓包必须严格只读） |
| `capture-mode` | `CAP-003` | `NetworkMonitorType.RawSocket`（只允许 Npcap 路径，无 raw-socket 回退） |
| `network-listener` | `NET-001`…`NET-005` | `HttpListener` / `TcpListener` / `WebSocket` / Kestrel / `QTcpServer` / `QLocalServer` 等；IPC 只能是命名管道 |
| `outbound-network` | `NET-006` | `HttpClient` / `WebClient` / `QNetworkAccessManager`；只放行两个文件：共享校准获取客户端 `src/Collector/Protocol/Sharing/SharedCalibrationClient.cs`（隐私边界 §8.2）与在线语音客户端 `src/Collector/Speech/OnlineSpeechClient.cs`（§8.3） |
| `outbound-network` | `NET-007`（`shared-calibration-hosts`） | 主机名片段 `jsdelivr` / `githubusercontent`（不分大小写）；只放行共享校准获取客户端、`tools/shared-calibration/` 目录，以及开发期下载公开数据表的 `tools/duty-data-generator/generate.py` |
| `outbound-network` | `NET-007`（`speech-host`） | 主机名片段 `tts.speech.microsoft.com`（不分大小写）；只放行在线语音客户端 `src/Collector/Speech/OnlineSpeechClient.cs` |
| `game-automation` | `AUT-001` | `SendInput` / `keybd_event` / `mouse_event` |

## 例外标记

包含 `BOUNDARY-ALLOW` 的行会被跳过。

该标记**仅供实施边界本身的代码**使用。典型场景是构建阶段将注入载荷从输出目录移除的
MSBuild 目标，该目标必须提及对应文件名。每一处使用都必须紧跟解释性注释。

**该标记不得用于绕过检查。** 评审会逐一核查每一处 `BOUNDARY-ALLOW`。

## 按路径放行

规则可声明自身不适用的文件。每一处放行都必须在
[`docs/privacy-boundary.md`](../../docs/privacy-boundary.md) 中写明理由，`NET-006` 与 `NET-007` 见 §8.2、§8.3：

| 字段 | 匹配方式 | 例子 |
|---|---|---|
| `allow_paths` | 仓库相对路径、正斜杠、**精确匹配**一个文件；不按文件名、不按目录名 | `src/Collector/Protocol/Sharing/SharedCalibrationClient.cs` |
| `allow_path_prefixes` | 仓库相对的目录前缀，**必须以 `/` 结尾**，所以 `tools/shared-calibration/` 不会放行 `tools/shared-calibration-old/` | `tools/shared-calibration/` |

- 放行仅对该条规则生效。放行文件中若出现其他被禁标识符，仍会报错。
- 同一规则编号可写多个条目，但每个条目必须带有自己的 `variant`（小写短词），编号与 `variant` 的组合不得重复。
  违规仍按编号报告，JSON 输出中另有 `rule_key`，形如 `NET-007/speech-host`。每个条目拥有各自的模式与放行名单：
  共享校准客户端可写 CDN 主机名，不可写语音主机名；在线语音客户端相反。
  同一编号下只要有一个条目带 `variant`，其余条目也必须带，否则退出码为 `2`。
- 出现反斜杠、以 `/` 开头、含 `.` 或 `..` 段、文件条目以 `/` 结尾、目录条目未以 `/` 结尾时，
  检查器以退出码 `2` 拒绝整个规则文件，而非扩大放行范围。
- 自测覆盖以下反例：位于其他目录的同名文件、放行文件的兄弟文件、名称相近的目录。

## 自测（反向测试）

`check.py` 自身通过不能说明问题，因为一个永不匹配的检查器同样会通过。
`selftest.py` 验证相反方向：它将每一个被禁标识符植入临时仓库树，
断言 `check.py` **失败**、命中正确的规则、覆盖**全部**应扫描的位置
（`src/`、`src/Desktop/qml/`、`tests/`、`tools/`、`scripts/`、CMake、MSBuild），
并确认 `BOUNDARY-ALLOW` 仍然有效。

```powershell
python tools/static-boundary-check/selftest.py       # 退出码 0 表示检查器确实会拦
python tools/static-boundary-check/selftest.py -v    # 逐条打印
```

`rules.json` 中每新增一条规则或一个 `variant` 条目（键写作 `编号/variant`），都必须在 `selftest.py` 的 `SAMPLES` 中配一个反例，
否则自测失败。该自测曾发现一处真实漏洞：`NET-003` 原本写作 `WebSocket\w*`，
前导的 `` 使 `ClientWebSocket`（.NET 实际的出站类型）完全漏过。
