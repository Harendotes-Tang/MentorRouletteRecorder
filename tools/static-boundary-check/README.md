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

扫描范围内的排除分三类，均在 `rules.json` 中声明，并由 `selftest.py` 固定：

- `exclude_dir_names`：在任意深度按目录名跳过，只限永远不会存放本项目源码的目录，即
  `.git`、`.vs`、`__pycache__`、`node_modules`。
- `exclude_output_dir_names`：构建输出目录 `bin`、`obj`，只在其所在目录含 `.csproj` 时跳过。
  .NET SDK 的默认编译范围只排除项目目录下的 `bin/` 与 `obj/`，更深处名为 `bin`、`obj` 的目录中的 `.cs`
  照常编译，因此照常扫描。同一个名字不得同时出现在 `exclude_dir_names` 中。
- `exclude_paths`：按仓库相对路径精确跳过的目录，以 `/` 结尾，目前只有检查器自身所在的
  `tools/static-boundary-check/`。条目必须按所写的大小写真实存在，必须位于某个扫描根目录之内，
  且不得等于或包含扫描根目录与 `scan_files`，否则检查器以退出码 `2` 结束。

因此 `src/` 下名为 `build`、`packages`、`artifacts`、`Testing`、`TestResults` 的目录、不在项目文件旁边的
`bin`、`obj`，以及所有以点开头的目录（含公开仓库镜像 `tools/shared-calibration/public-repo/.github/`）都照常扫描：
.NET SDK 会编译项目目录下的全部 `.cs`，按目录名跳过等于让这些文件绕过边界。

规则依据：[`../../docs/privacy-boundary.md`](../../docs/privacy-boundary.md)。

## 运行

```powershell
python tools/static-boundary-check/check.py            # 人类可读输出
python tools/static-boundary-check/check.py --json     # 机器可读输出
pwsh -File scripts/verify.ps1                          # 边界检查 + dotnet test
```

退出码：`0` 无违规；`1` 有违规；`2` 检查器自身无法运行（规则文件损坏等），或未能读完全部候选文件（见下文“扫描不完整即失败”）。

## 规则位置

全部模式定义在 `rules.json` 中，**不在** `check.py` 中。
因此检查器自身的源码不含任何字面命中。本目录同时被排除在扫描范围之外，
避免自我告警。

## 规则分类

下表按 `rules.json` 逐条列出（同一编号的多个条目写作 `编号/variant`）。精确的正则以 `rules.json` 为准；
`selftest.py` 会检查本表是否列出了每一个条目。

| 规则 | 分类 | 拦截什么 | 按路径放行 |
|---|---|---|---|
| `INJ-001` | `process-injection` | 读他进程内存：`ReadProcessMemory`（含 `Toolhelp32ReadProcessMemory`）、`NtReadVirtualMemory` / `ZwReadVirtualMemory`（含 `NtWow64ReadVirtualMemory64`）、转储进程内存的 `MiniDumpWriteDump` | — |
| `INJ-002` | `process-injection` | `WriteProcessMemory` | — |
| `INJ-003` | `process-injection` | 在他进程中分配内存或修改内存保护：`VirtualAllocEx`（含 `VirtualAllocExNuma`）、`VirtualAlloc2`、`VirtualProtectEx`、`NtAllocateVirtualMemory` / `ZwAllocateVirtualMemory`、`NtProtectVirtualMemory` / `ZwProtectVirtualMemory` | — |
| `INJ-004` | `process-injection` | `CreateRemoteThread` / `CreateRemoteThreadEx` | — |
| `INJ-005` | `process-injection` | `SetWindowsHookEx*` | — |
| `INJ-006` | `process-injection` | 底层注入原语：`NtWriteVirtualMemory` / `ZwWriteVirtualMemory`（含 `NtWow64WriteVirtualMemory64`）、`NtCreateThreadEx` / `ZwCreateThreadEx`、`RtlCreateUserThread`、`QueueUserAPC` / `QueueUserAPC2`、`NtQueueApcThread*` / `ZwQueueApcThread*` | — |
| `INJ-007` | `process-injection` | 打开他进程或其线程的句柄，或为此取得调试特权：`OpenProcess`、`NtOpenProcess` / `ZwOpenProcess`、`OpenThread`、`NtOpenThread` / `ZwOpenThread`、按窗口取进程句柄的 `GetProcessHandleFromHwnd`、逐个打开进程的 `NtGetNextProcess` / `ZwGetNextProcess`、`DebugActiveProcess`、`Process.EnterDebugMode` | — |
| `INJ-008` | `process-injection` | `Process.MainModule` / `Process.Modules`（内部以 `PROCESS_QUERY_INFORMATION` 与 `PROCESS_VM_READ` 打开句柄并读模块表），含点号后有空白的写法，以及 C# 属性模式中的写法（`{ MainModule.FileName: … }`、`{ Modules: … }`，名字位于 `{` 或 `,` 之后或行首，后跟 `.` 或 `:`） | — |
| `INJ-009` | `process-injection` | 会打开所属进程句柄的 `Process` 成员：`.StartTime` `.ExitTime` `.Handle`（方法调用 `.Handle(…)` 除外） `.SafeHandle` `.HasExited` `.ExitCode` `.EnableRaisingEvents` `.PriorityClass` `.PriorityBoostEnabled` `.ProcessorAffinity` `.TotalProcessorTime` `.UserProcessorTime` `.PrivilegedProcessorTime` `.MaxWorkingSet` `.MinWorkingSet` `.WaitForExit` `.WaitForExitAsync` `.WaitForInputIdle` `.Kill`（点号后可有空白），以及 `GetProcessById`；属性成员在 C# 属性模式与对象初始化器中同样拦截（`{ HasExited: false, StartTime: var s }`、`new Process { EnableRaisingEvents = true }`，名字位于 `{` 或 `,` 之后或行首，后跟 `:`、`.` 或 `=`）；PowerShell 管道中不带点号点名这些成员的写法同样拦截：同一行上先有进程来源（`Get-Process` / `gps` / `ps`，或 `[Process]::GetProcesses*`），其后某个 `\|` 之后的 `Select-Object`（含 `-Property`、`-ExpandProperty`）、`ForEach-Object`、`Sort-Object`、`Where-Object`、`Group-Object`、`Measure-Object`、`Format-Table`、`Format-List` 或其别名（`select` `foreach` `%` `sort` `where` `?` `group` `measure` `ft` `fl`）的参数中出现成员名（`Get-Process \| Select-Object StartTime`、`gps \| % Kill`），不分大小写，`#` 之后的注释不计；没有进程来源的同名属性（`$runs \| Sort-Object StartTime`）不构成命中 | 只放行驱动本软件**自身**进程的文件，均为精确路径：父进程看门狗 `src/Collector/Diagnostics/ParentProcessWatchdog.cs`（等待桌面端退出并读取其启动时间）；启动、等待、结束采集服务或替身父进程的测试与脚本。完整名单见 `rules.json`。只有一处命中且命中的并非 `Process` 成员的文件不整文件放行，改用登记在案的行内标记（见下文“例外标记”） |
| `DEU-001` | `injected-hook` | 启用 Deucalion 注入式钩子（`UseDeucalion = true` 等） | — |
| `DEU-002` | `injected-hook` | Deucalion 的 API（`DeucalionClient` / `DeucalionInjector` 等） | — |
| `DEU-003` | `injected-hook` | 注入载荷文件名 `deucalion-*.dll` | — |
| `DEU-004` | `injected-hook` | 注入载荷的加载入口 `InjectLibrary` / `ValidateLibraryChecksum` | — |
| `CAP-001` | `packet-send` | 发包：`pcap_sendpacket`、`pcap_sendqueue_*`、`Packet.dll` 的 `PacketSendPacket(s)`，以及 SharpPcap 的 `SendPacket` / `SendQueue`（抓包必须严格只读） | — |
| `CAP-002` | `packet-send` | `pcap_inject` | — |
| `CAP-003` | `capture-mode` | `NetworkMonitorType.RawSocket`（只允许 Npcap 路径，无 raw-socket 回退） | — |
| `CAP-004` | `capture-mode` | raw socket 抓包实现 `RawCaptureSocket` | — |
| `CAP-005` | `packet-persistence` | 把原始报文写成抓包文件：`pcap_dump*`、`pcap_breakloop`，以及 SharpPcap 的 `CaptureFileWriterDevice` | — |
| `CAP-006` | `capture-mode` | 直接加载 `wpcap` / `Packet` / `npcap` 原生库：同一行上 `DllImport` / `LibraryImport`（含具名参数与原始字符串）、`NativeLibrary.Load` / `TryLoad`、`LoadLibrary*`、`QLibrary` 后的第一个字符串字面量含库名；值为 `wpcap` / `Packet` 库名（可带路径与 `.dll`）的 `const string`；以及以这样的库名字面量开头、后跟 `,` 或 `)` 的续行（`DllImport(` 换行后才写库名的写法） | — |
| `NET-001` | `network-listener` | `HttpListener` | — |
| `NET-002` | `network-listener` | `TcpListener` | — |
| `NET-003` | `network-listener` | `WebSocket*`（含 `ClientWebSocket`、`QWebSocketServer`） | — |
| `NET-004` | `network-listener` | 内嵌 Web 服务器：含 `Kestrel` 的标识符（如 `UseKestrel`、`ConfigureKestrel`）、`Microsoft.AspNetCore`、Web 项目 SDK `Microsoft.NET.Sdk.Web`、最小托管入口 `WebApplication` | — |
| `NET-005` | `network-listener` | 桌面端监听：`QTcpServer` / `QHttpServer` / `QWebSocketServer` / `QLocalServer` / `QSslServer` / `QSctpServer`；IPC 只能是命名管道 | — |
| `NET-006` | `outbound-network` | HTTP 客户端：`HttpClient` / `WebClient` / `QNetworkAccessManager` / `QRestAccessManager` / `HttpMessageInvoker` / `SocketsHttpHandler` / `HttpClientHandler` / `HttpWebRequest` / `FtpWebRequest` / `WebRequest.Create`（含 PowerShell 的 `[WebRequest]::Create`），WinHTTP（`WinHttp*`），WinINet（`InternetOpen*` / `InternetConnect*` / `HttpOpenRequest*` / `HttpSendRequest*`），以及 `URLDownloadToFile*` / `URLOpen*Stream*` | 三个出站客户端：共享校准获取客户端 `src/Collector/Protocol/Sharing/SharedCalibrationClient.cs`（隐私边界 §8.2）、在线语音客户端 `src/Collector/Speech/OnlineSpeechClient.cs`（§8.3）、更新检查客户端 `src/Collector/Update/UpdateCheckClient.cs`（§8.4；用户要求下载新版本安装程序时的请求也由它发出，§8.6） |
| `NET-007/shared-calibration-hosts` | `outbound-network` | 主机名片段 `jsdelivr`（不分大小写） | 共享校准获取客户端、`tools/shared-calibration/` 目录、开发期下载公开数据表的 `tools/duty-data-generator/generate.py` |
| `NET-007/github-content-hosts` | `outbound-network` | 主机名片段 `githubusercontent`（不分大小写） | 共享校准获取客户端、更新检查客户端、`tools/shared-calibration/` 目录、`tools/duty-data-generator/generate.py`（§8.2、§8.4、§8.6） |
| `NET-007/speech-host` | `outbound-network` | 主机名片段 `tts.speech.microsoft.com`（不分大小写） | 在线语音客户端（§8.3） |
| `NET-008` | `network-socket` | 直接使用网络：套接字 `TcpClient` / `UdpClient` / `Socket` / `QTcpSocket` / `QUdpSocket` / `QSslSocket` / `QSctpSocket` / `QAbstractSocket`、QUIC（`QuicConnection` / `QuicListener` / `QuicStream`）、Winsock（`WSAStartup` / `WSASocket*` / `WSAConnect*`、以 `AF_` / `PF_` 为首个参数的 `socket(…)`）；域名解析 `Dns.*`（含 PowerShell 的 `[Dns]::…`）、`QHostInfo::lookupHost` / `fromName`、`QDnsLookup`、`getaddrinfo` / `gethostbyname` / `GetAddrInfo*`；`Ping`（`new Ping()`、`SendPingAsync`）与 `SmtpClient`；既不得外连也不得监听 | — |
| `NET-009/downloads` | `outbound-network` | 脚本、安装程序、QML 与桌面端自行联网：`Invoke-WebRequest` / `Invoke-RestMethod` / `Start-BitsTransfer`（不分大小写），Inno Setup 的 `DownloadTemporaryFile*(…)` / `CreateDownloadPage(…)`（不分大小写，与 Pascal 一致），`XMLHttpRequest` 与 COM 的 `MSXML2.(Server)XMLHTTP` / `Microsoft.XMLHTTP`，`source: "https://…"` / `source: 'https://…'` 形式的远程资源，以及 C++ 把远程地址交给 QML 引擎的 `load(…)` / `setSource(…)`（`engine.load(QUrl("https://…"))`，可带 `QStringLiteral`） | 不按路径放行。缺少 Npcap 时，安装程序 `installer/MentorRecorder.iss` 从 npcap.com 下载固定版本、校验 SHA-256 的官方安装程序并启动（Npcap 的免费许可证不允许随包分发，见仓库根目录的 `README.md` 与 `THIRD_PARTY_NOTICES.md`）；创建下载页的那一行带有登记在案的行内标记，同一文件中的其他下载照常拦截。Inno Setup 的两项按调用匹配，因此自带翻译文件 `installer/ChineseSimplified.isl` 在小节标题中提到的函数名不构成命中 |
| `NET-009/qml-remote` | `outbound-network` | 仅 `.qml` / `.js` / `.mjs`：给 `source` 赋远程地址（`image.source = "https://…"`），`import "https://…"` 远程模块，`url` 属性取远程地址（`property url x: "https://…"`），以及 `Qt.resolvedUrl("https://…")` / `Qt.include("https://…")` | — |
| `NET-009/shell-commands` | `outbound-network` | 仅脚本与工作流（`.ps1` `.psm1` `.bat` `.cmd` `.sh` `.yml` `.yaml`）：位于命令位置的下载命令 `curl` / `wget` / `iwr` / `irm` / `bitsadmin`（可带 `.exe`，可带路径）与 `certutil … -urlcache`，不分大小写。命令位置指行首，或 `;` `\|` `&` `(` `{` `=` `@` `!`、反引号与 YAML 的 `run:` 之后，也包括 shell 关键字 `if` `then` `do` `else` `elif` `while` `until` 与 `sudo` 之后、`cmd /c` 与 `Start-Process`（可带 `-FilePath`）之后，以及调用运算符 `&` 之后加引号的写法（`& "curl.exe" $url`）；`#` 之后的注释与其他引号内的文字不计，命令名后紧跟字母、数字、`_`、`.` 或 `-` 时也不计，因此散文、注释、恰好含这些词的标识符与 `curl-config` 这样的其他工具不构成命中 | — |
| `NET-009/xml-loads` | `outbound-network` | 以 XML 读取接口从远程地址加载（这些接口会自行下载所给的地址），不分大小写：`.Load(` 或 `]::Load(` 的第一个参数是 `http(s)://` 字面量（`XmlDocument` / `XDocument` / `XElement` 的 `Load`，PowerShell 的 `$doc.Load('https://…')` 与 `[XDocument]::Load(…)`，Pascal 中 MSXML 的 `load`；可带 C# 具名参数、`@` / `$` 前缀），`XmlReader.Create("https://…")`（含 `[XmlReader]::Create`），以及 `XmlTextReader` / `XPathDocument` 之后同一行的第一个字符串字面量是远程地址（`new XmlTextReader("https://…")`、`New-Object System.Xml.XmlTextReader 'https://…'`）。本地文件路径与存放在变量中的地址不构成命中；PowerShell 用 `[xml]` 转换下载结果时，其中的 `WebClient`、`Invoke-WebRequest`、`iwr` / `irm` 已由 `NET-006` 与 `NET-009` 拦截 | — |
| `NET-010` | `outbound-network` | 仅 `.py`：Python 脚本自行联网。拦截以 `import` / `from` 语句、`from urllib\|http\|xmlrpc import request\|client`、`__import__("…")` 或 `importlib.import_module("…")` 导入网络模块 `urllib.request`、`urllib3`、`http.client`、`requests`、`httpx`、`aiohttp`、`socket`、`ftplib`、`smtplib`、`poplib`、`imaplib`、`nntplib`、`telnetlib`、`xmlrpc.client`、`websockets`，以及在 `#` 注释与字符串之外调用 `urlopen(` / `urlretrieve(` / `asyncio.open_connection(`。只认导入语句与调用，因此测试中以字符串点名（`mock.patch("urllib.request.urlopen")`）或以假函数代替下载的写法不构成命中 | 只放行开发期从两个公开来源（XIVAPI 与 thewakingsands 的数据挖掘 CSV）下载任务数据表的 `tools/duty-data-generator/generate.py`，它是仓库中唯一建立连接的 Python 文件。`tools/shared-calibration/` 的 Python 只读取保存下来的 `gh` 输出文件，访问 GitHub 的 `gh` 调用位于同目录的 shell 脚本与工作流中，因此不需要放行 |
| `AUT-001` | `game-automation` | 模拟输入与窗口消息：`SendInput` / `keybd_event` / `mouse_event` / `SendKeys`，`PostMessage*` / `PostThreadMessage*` / `SendMessage*` / `SendNotifyMessage*`，以及向主窗口投递关闭消息的 `Process.CloseMainWindow` | — |

## 例外标记

一行可以用 `BOUNDARY-ALLOW(<规则编号>): <理由>` 豁免**一条**规则，例如：

```csharp
/// <see cref="System.Diagnostics.Process.MainModule"/> (BOUNDARY-ALLOW(INJ-008): named here only to say
```

- 标记只豁免所写规则在所在这一行上的**一处**命中；同一行上其他规则的命中照常报告。
  写 `NET-007` 这样的编号时，该编号下全部 `variant` 的命中一并计算。
- 理由必须写在同一行的冒号之后，且至少包含一个文字字符。不带规则编号的旧写法 `BOUNDARY-ALLOW`、
  没有理由、或理由只有标点（如 `*/`、`-->`）时，标记**不生效**，该行的命中照常报告。
  理由不能以另一个标记开头：`BOUNDARY-ALLOW(A):BOUNDARY-ALLOW(B): 理由` 中 A 没有理由、不生效，B 照常生效。
- 标记所写的规则编号不在 `rules.json` 中时，检查器以退出码 `2` 结束。这通常意味着规则改名或笔误。
- 每个标记都必须在 `rules.json` 的 `allow_markers` 中按“文件 + 规则编号 + 所豁免命中的原文 `match`”登记，
  一个标记一条，例如 `{"file": "src/Collector/Storage/SqliteDatabase.cs", "rule": "INJ-009", "match": ".Handle"}`。
  `match` 是该规则的模式在这一行上匹配到的原文。只有当这一行上该规则恰有一处命中、且原文与登记的一致时，
  标记才豁免它；同一行上再出现第二处命中，或命中换成了别的写法，标记不生效，命中照常报告，且检查器以退出码 `2` 结束。
  未登记的标记同样不生效，且以退出码 `2` 结束；登记了却没有找到、找到的数量与登记的不一致、
  或所在行没有可豁免的命中，也以退出码 `2` 结束。因此新增标记必须同时修改 `rules.json`，评审在配置中即可看到豁免的是什么。
- 每次运行都会列出全部生效的标记及其数量：文本输出在末尾列出，JSON 输出给出 `allow_marker_count`
  与 `allow_markers`。每条记录文件、行号、规则编号、理由、被豁免命中的原文 `match`，以及被豁免的命中数 `lifted`（总是 1）。

该标记**仅供实施边界本身的代码**使用，或用于某个文件中唯一一处、实际并非被禁行为的命中
（例如文档注释以 `Process.StartTime` 说明取值格式，或测试读取结果记录的 `ExitCode` 属性），
以及安装程序创建 Npcap 下载页的那一行，以免为一行而放行整个文件。目前登记的标记见 `rules.json` 的 `allow_markers`。

**该标记不得用于绕过检查。** 新增的标记会出现在每次运行的输出中，评审会逐一核查。

## 扫描不完整即失败

以下情况检查器以退出码 `2` 结束，而不是报告通过：

- 候选文件无法读取属性（stat）或无法读取内容，或扫描范围内的目录无法列出；
- `scan_dirs`、`scan_files` 或 `exclude_paths` 中的条目不存在（按所写的大小写逐级核对），
  或无法读取属性；属性用 `os.stat` 读取，在 Python 3.11 至 3.14 上都能发现失败
  （3.14 的 `Path.is_dir()` 会把任何错误当作“不存在”）；
- 扫描根目录内遇到指向目录的符号链接或目录联接（junction）：既不跟随，也不静默跳过，而是报告；
- 候选文件大于 `max_file_bytes`（当前为 4 MiB）；
- 候选文件含 NUL 字节，即 UTF-16 编码或二进制文件，按 UTF-8 读取只会得到噪声；
- 一次运行没有扫描到任何文件；
- 例外标记未登记、登记的标记缺失或数量不符、标记所在行没有可豁免的命中，或所在行的命中不止一处、
  原文与登记的 `match` 不一致（见上文）；
- 规则文件不可用：`include_extensions` 或某条规则的 `only_extensions` 中有不带前导点、含大写字母或
  不是字符串的条目（`only_extensions` 的条目还必须出现在 `include_extensions` 中），
  `max_file_bytes` 不是正整数，排除项或标记登记的写法不合要求（标记登记缺少 `match`、`match` 为空或含换行，
  同一目录名同时出现在 `exclude_dir_names` 与 `exclude_output_dir_names` 中），或仍使用已废止的 `exclude_dirs`。

这些问题都会逐条打印，JSON 输出另有 `error_count` 与 `errors`。

## 按路径放行

规则可声明自身不适用的文件。每一处放行都必须在
[`docs/privacy-boundary.md`](../../docs/privacy-boundary.md) 中写明理由，`NET-006` 与 `NET-007` 见 §8.2、§8.3、§8.4、§8.6，
`NET-010` 见 §8 中开发期联网一段；`INJ-009` 与 `NET-010` 的放行理由另见 `rules.json` 的 `$comment` 与规则消息。
`NET-009` 不按路径放行，安装程序的 Npcap 下载用的是行内标记：

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
（`src/`、`src/Desktop/qml/`、`tests/`、`tools/`、`scripts/`、`installer/`、`.github/`、CMake、MSBuild，
以及扫描根目录深处名为 `build`、`packages` 等的目录和以点开头的目录）
与**全部**应扫描的扩展名（`SCANNED_EXTENSIONS` 与 `include_extensions` 必须完全一致），
且排除项恰为固定的集合（`EXCLUDED_DIR_NAMES`、`EXCLUDED_OUTPUT_DIR_NAMES` 与 `EXCLUDED_PATHS`），
不在 `.csproj` 旁边的 `bin`、`obj` 照常扫描。
它还确认：`MORE_SAMPLES` 中的每一种写法都被拦截；形似而合法的写法不被误报（`LOOK_ALIKES`）；
按路径放行只对所列文件、所列规则生效；`BOUNDARY-ALLOW(<规则编号>): <理由>` 只豁免一条规则在一行上的一处命中，
且只在按原文登记后生效；扫描不完整时以退出码 `2` 失败，其中“没有扫描到任何文件”由一棵扫描根目录齐全、
只是没有可扫描文件的树单独验证；本文的规则表列出了 `rules.json` 的每一个条目。

“每种写法都有反例”由变异检查机械保证：自测在内存中逐一删除每条规则模式中的每个分支
（顶层的 `a|b|c`，以及任一分组内的分支，如 `(?:Nt|Zw)`），用删减后的规则扫描该规则的全部反例，
要求至少有一个反例因此不再被报告。某个分支没有任何反例依赖它时，自测失败并列出该分支。

```powershell
python tools/static-boundary-check/selftest.py       # 退出码 0 表示检查器确实会拦
python tools/static-boundary-check/selftest.py -v    # 逐条打印
```

`rules.json` 中每新增一条规则或一个 `variant` 条目（键写作 `编号/variant`），都必须在 `selftest.py` 的 `SAMPLES` 中配一个反例，
并在本文的规则表中列出，否则自测失败；为已有规则新增的每一种写法，都必须在 `MORE_SAMPLES` 中配一个只有它能拦截的反例，
否则变异检查失败。
该自测曾发现一处真实漏洞：`NET-003` 原本写作 `\bWebSocket\w*\b`，
前导的 `\b` 使 `ClientWebSocket`（.NET 实际的出站类型）完全漏过。

## 已知局限

本检查器逐行匹配正则表达式，不解析任何语言，因此不可能完备。以下写法**不会**被发现，
只能由代码评审兜底：

- **跨行拆开的标识符或调用**：`process` 换行后再写 `.Kill()`、`Invoke-WebRequest` 用续行符拆开、
  `curl` 写在 here-string 或多行命令的中间、PowerShell 管道在 `|` 处换行（`Get-Process |` 的下一行才写
  `Select-Object StartTime`），或进程对象先存入变量、在另一行交给管道。`CAP-006` 只额外覆盖了库名写在 `DllImport(` 下一行的写法。
- **拼接或计算出的名字**：`"Read" + "ProcessMemory"`、`EntryPoint = Prefix + "Memory"`、`"wp" + "cap.dll"`、
  由变量给出的命令（`& $tool $url`、`Start-Process $tool`）、先存进变量再交给 QML 引擎的远程地址，
  以及经反射或 `GetProcAddress` 动态查找、名字不以整词出现在一行之内的调用。
- **标识符转义与别名**：C# 的 Unicode 转义标识符（`SendInput`）、在另一文件中以非 `const` 字段保存、
  随后传给 `NativeLibrary.Load` 的库名、为被禁类型另起的 `using` 别名。
- **未列出的 API**：规则只认识表中列出的名字。`NET-010` 只认网络模块的导入与 `urlopen` / `urlretrieve` /
  `asyncio.open_connection` 的调用：Python 经由 `subprocess` 运行的下载命令（`subprocess.run(["curl", url])`）、
  借道已放行模块的下载（导入 `generate` 后调用 `generate.http_get(url)`），以及表中未列出的模块都不会被发现。
  `NET-009/xml-loads` 只认 `Load`、`XmlReader.Create`、`XmlTextReader` 与 `XPathDocument`，`DataSet.ReadXml` 等其他接受地址的
  读取接口不在其内，地址来自变量时也不会被发现。
- **不点名成员的读取**：`INJ-009` 只在成员名写在同一行上时生效。PowerShell 整体输出或序列化进程对象时会读取其全部属性
  （`Get-Process` 的默认表格输出读取 `CPU`，即 `TotalProcessorTime`；`Select-Object *`、`Format-List *`、`ConvertTo-Json`、
  `Export-Csv`），PowerShell 为进程对象附加的 `CPU`、`Path`、`Company`、`FileVersion` 等属性内部同样打开句柄，
  `Stop-Process`、`Wait-Process` 也打开进程句柄；这些写法都不会被发现。
- **只看名字、不看类型**：`INJ-008` 与 `INJ-009` 在属性模式与对象初始化器中按成员名匹配，
  本软件自己的类型若有名为 `StartTime`、`Handle`、`ExitCode` 等的成员，写进 `{ … }` 也会命中，需要改写或登记行内标记。
  `INJ-009` 的 PowerShell 管道写法以同一行上的进程来源（`Get-Process` 等）代替类型判断：一行中先取进程、
  后把其他对象交给管道时同样会命中。
- **注释与字符串**：规则同样作用于注释和字符串，所以散文提到被禁名字也会命中（需要登记的行内标记）；
  反过来，`NET-009/shell-commands` 只把 `#` 之后与引号之内的文字当作非命令，PowerShell 的 `<# … #>`
  块注释与 here-string 中位于行首的 `curl` 仍会被当作命令，`@{ curl = 1 }` 这样的哈希表键也会误报。
  `INJ-009` 的 PowerShell 管道写法同样不计 `#` 之后的文字，但引号之内照常计入（交给 `powershell -Command` 的字符串会被执行），
  `#` 写在进程来源之前的字符串中时则会漏检；`NET-010` 的调用写法不计 `#` 之后与引号之内的文字，导入语句只在行首匹配，
  因此多行文档字符串中恰好以 `import requests` 开头的一行仍会命中。
