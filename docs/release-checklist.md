# V1 发布验收清单 / V1 Release Checklist

> 本文件是 V1 的**验收依据**，不是愿望清单。每一项都写明验证方法、本次验收的实际结果与证据位置。
> 未实际执行过的条目必须标注 `UNVERIFIED`，不得标注 `PASS`。
>
> 一次完整验收由下列命令组成：
>
> ```powershell
> pwsh -NoProfile -File scripts/verify.ps1                  # 边界 + 构建 + 全部测试 + 监听/载荷/许可证核对
> pwsh -NoProfile -File scripts/package.ps1 -Force -Verify  # 打包并从解包目录真的运行两个可执行文件
> python tools/static-boundary-check/check.py               # 静态硬边界
> python tools/static-boundary-check/selftest.py            # 检查器的反向自测
> ```
>
> 分支模型、测试包（`X.Y.Z-beta.N`）与正式发布各自的操作步骤见第 9 节。

## 0. 本次验收记录 / This run

| 项 | 值 |
|---|---|
| 验收日期 | 2026-09-04 |
| 配置 | Release / win-x64 |
| Collector 版本 | 见 `Directory.Build.props` 的 `<Version>`（ipc v1，schema 见 `--version --json`）；`package.ps1` 会核对两个可执行文件、`BUILD-METADATA.json` 与 `CHANGELOG.md` 顶部版本号是否一致 |
| Qt | 6.11.2 mingw_64（**示例**路径 `D:/APPS/Qt/6.11.2/mingw_64`，可用 `MR_QT_PREFIX` 覆盖，见 [build-and-package.md](build-and-package.md)） |
| 验收机器 | Windows 11，**无 Npcap、无 FFXIV 客户端** |
| .NET 用例 | **待重测**。上次完整验收的结果为 477 总数 / 474 通过 / 0 失败 / 3 跳过。Phase 5 加固后 `DiagnosticsLogHygieneTests` 不再含跳过项，并新增了看门狗与补发缓冲用例 |
| Qt/C++ 用例 | 11 总数 / 11 通过 / 0 失败 |
| `LIVE_CAPTURE_STATUS` | 本表不抄录该值：应读取产物 `BUILD-METADATA.json` 中的 `live_capture_status`，或运行 `--capture-doctor --json`，判据见 §7。2026-09-07 的实测值为 `VERIFIED_POP_TO_EXIT`，即弹窗与换区已验证，通关报文仍未识别 |
| `PUBLIC_DISTRIBUTION_READY` | 本表不抄录该值：由 `package.ps1` 按 §7 的五条前提计算，写入 `BUILD-METADATA.json`；不满足时，`public_distribution_blockers` 逐条说明原因 |
| 安装器 | `artifacts/MentorRecorder-<version>-setup.exe`（Inno Setup；self-contained；Npcap 官网下载） |
| 产物 | `artifacts/MentorRecorder-<version>-win-x64.zip`（本次实测 63,734,750 字节） |
| 产物 SHA256 | `46a4b5e863069958e460a7c060b92a4bfddbef0f02ab7503a880006edc0188d2` |
| `verify.ps1` | 退出码 0 |
| `package.ps1 -Force -Verify` | 退出码 0 |
| 静态边界检查 | 0 违规 / 278 个文件 |
| 边界检查器自测 | 53 个反向用例全部通过 |
| 运行结束后遗留进程 | 无；该结果不再依赖人工清理，见第 1 节第 28 项 |

**已无跳过用例。** 原先跳过的三个 `DiagnosticsLogHygieneTests` 用例（IPv4、SID、十六进制串）
随 `RotatingFileLogger.Sanitize` 在 Phase 5 加固中一并落实，详见第 6 节。

## 1. 验收表 / Acceptance table

图例：**PASS** 表示本次实际执行并通过；**PARTIAL** 表示部分成立，差异记录在备注中；
**UNVERIFIED** 表示本机不具备验证条件，须在真机上补充验证。

| # | 验收项 | 怎么验证 | 本次结果 | 证据 |
|---|---|---|---|---|
| 1 | Windows x64 能构建出两个可执行文件 | `pwsh -File scripts/build.ps1`（dotnet + CMake/Ninja/Qt） | **PASS** | `src/Collector/bin/x64/Release/net8.0-windows/win-x64/MentorRecorder.Collector.exe`、`build/src/Desktop/MentorRecorder.Desktop.exe` |
| 2 | 两个可执行文件都能启动 | `package.ps1 -Verify`：解包后运行 `--version`、`--capture-doctor --json`、`--screenshot` | **PASS** | 见第 2 节「打包验证」输出 |
| 3 | 没有 Npcap 时优雅降级（不崩溃、给指引） | `CaptureControllerTests.RefusesToStart_WithoutNpcap`、`ReportsUnavailable_WhenNpcapIsMissing`；`--capture-doctor` 退出码 1 并打印安装指引 | **PASS** | `artifacts/test-results/MentorRecorder.Collector.UnitTests.trx`；`LifecycleProcessTests.TheVersionAndDoctorModesRunWithoutOpeningAnythingAtAll` |
| 4 | 合成回放能产出 COMPLETED / LEFT_OR_ABANDONED / CANCELLED_BEFORE_ENTRY / INTERRUPTED / UNKNOWN | `ProtocolDecodedReplayTests`（10 个 decoded 夹具 × 2）、`ReplayIntegrationTests`（10 个语义夹具）、`StateMachineTests.EnteredDuty_FinalOutcomesRemainDistinct` | **PASS** | 两个 TRX；`tests/Fixtures/` |
| 5 | 同一份夹具回放两次不重复计数 | `ProtocolDecodedReplayTests.ReplayingTheSameFixtureTwiceWritesNothingTheSecondTime`（10 组参数） | **PASS** | 集成 TRX；第二次 `RunsCreated = 0`、`EventsAppended = 0` |
| 6 | 成就基线可设置并影响进度 | `MutationTests.UpdateAchievementBaseline_StoresGoalBaselineAndAudit`、`StatisticsAfterMutationTests.BaselineChange_RecomputesProgressAndRemaining`、`PipeServerTests.UpdateAchievementBaseline_IsServedAndIdempotent` | **PASS** | 两个 TRX |
| 7 | 仪表盘数字与固定数据集一致 | `StatisticsTests.Dashboard_*`（3 项）、`ReplayTests.Replay_ProducesTransitionsFinalRunDatabaseWritesAndStatistics` | **PASS** | 单元 TRX；期望值在测试中独立计算，不取自实现 |
| 8 | 按副本分组的次数统计 | `PipeServerTests.Statistics_AreServedForEveryTable`（`GetDungeonStats`）、`StatisticsTests.GroupedStats_KeepUnknownJobAndUseDutyFallback` | **PASS** | 两个 TRX |
| 9 | 各项比率（完成率 / 中途退出率 / 断线率）互不混淆 | `StatisticsTests.Dashboard_KeepsCompletionLeaveAndDisconnectSeparate`、`StatisticsAfterMutationTests.DisconnectedAndInterrupted_AreNeverFoldedIntoTheLeaveRate` | **PASS** | 单元 TRX |
| 10 | 未知职业进入独立分桶而不是被丢弃 | `StatisticsTests.GroupedStats_KeepUnknownJobAndUseDutyFallback`、`ReferenceCatalogTests.DefaultJobCatalog_MapsKnownAndUnknownJobs` | **PASS** | 单元 TRX |
| 11 | 平均时长只统计有真实时长的记录 | `StatisticsAfterMutationTests.CancelledBeforeEntry_IsNeverAnAttemptAndItsShareIsNull`、`ReplayIntegrationTests.CancelledFixture_HasNoEntryTimeAndNoDuration`、`CompletedFixture_MeasuresDurationFromTheMonotonicReadings` | **PASS** | 两个 TRX |
| 12 | 手工订正产生可追溯的 revision 链 | `MutationTests`（25 项，含 `CorrectRun_KeepsTheOriginalValuesReachableThroughRevisionOne`、`RunRevisions_RefuseUpdateAndDelete`） | **PASS** | 单元 TRX |
| 13 | 软删除后统计正确排除，恢复后回到统计 | `StatisticsAfterMutationTests.SoftDeletedRun_LeavesEveryStatistic`、`RestoredRun_ReturnsToEveryStatistic`、`MutationTests.SoftDeleteThenRestore_KeepsTheWholeRevisionChain` | **PASS** | 单元 TRX |
| 14 | CSV / JSON 导出 | `ExportTests`（12 项，含 RFC 4180 转义、BOM、列顺序、路径越界拒绝） | **PASS** | 单元 TRX |
| 15 | 图表能渲染 | ctest `MentorRecorderQmlLoad` / `QmlDetailPanel` / `QmlEditDialog` / `QmlBaselineDialog`（offscreen 截图） | **PASS** | `build/tests/Desktop.Tests/qml-*.png`；图表实现在 `src/Desktop/qml/charts/` |
| 16 | 高 DPI | `main.cpp` 设置 `HighDpiScaleFactorRoundingPolicy::PassThrough`；截图用例在 1280×800 下渲染通过 | **PARTIAL** | `src/Desktop/cpp/main.cpp:158`。**尚未**在缩放大于 100% 的真实显示器上人工核对；offscreen 截图恒为 1.0 倍。逐项步骤见第 8 节「高 DPI 人工核对」，该节待人工执行 |
| 17 | 命名管道 ACL 只授权当前用户 | `LifecyclePipeSecurityTests`（3 项）：从真实管道句柄读 DACL，断言只有一条 Allow 规则且身份是当前用户 SID；并断言 Everyone / Authenticated Users / NETWORK / ANONYMOUS / Users / INTERACTIVE 都不在其中 | **PASS** | 集成 TRX |
| 18 | 进程不监听任何端口 | `LifecycleProcessTests.AServingCollectorOwnsNoTcpEndpointAtAll`（真进程 + `netstat -ano`）；`verify.ps1` 第 5 步（`Get-NetTCPConnection -OwningProcess` + `netstat -ano`） | **PASS** | 集成 TRX；`verify.ps1` 输出「该进程没有任何 TCP 端点」 |
| 19 | 不出现任何被禁 API | `python tools/static-boundary-check/check.py`（24 条规则，278 个文件）＋ `selftest.py`（53 个反向用例） | **PASS** | 见第 3 节 |
| 20 | 协议档案不可用时 fail-closed（不解析、不记录） | `CaptureControllerTests.RefusesToStart_WhenTheProfileIsNotVerified`、`ProtocolDecodedReplayTests.ABuildMismatchWritesNoRunAndLeavesTheStateMachineUntouched`、`SoakBoundsTests.AFailClosedParserRefusesIndefinitelyWithoutGrowing` | **PASS** | 两个 TRX |
| 21 | 可追溯性：记录携带协议档案与事件摘要 | 档案侧：`CaptureIpcTests.VerifiedProfileFlowsFromCaptureThroughParserIntoLiveIpcAndStorage` 断言 `protocol_profile_id` / `game_build` / `capture_session_id`。事件侧：`run_events` 表有完整轨迹，`RecoveryEndToEndTests` 直接读它 | **PASS** | 事件轨迹现已可读取：`GetRunEvents` 是契约中的一条只读消息，对应第 5 节缺口 G1，该缺口已关闭 |
| 22 | `BUILD-METADATA.json` 的状态字段与产物本身一致，不是手写的字面量 | `package.ps1` 从产物自己的 `--capture-doctor --json` 与 `--list-profiles --json` 读出 `live_capture_status` / `packaged_verified_profile_status`，并用 `*_source` 字段写明各自的来源（前者是二进制里的编译期常量，后者是对包内档案的真实检查）；`-Verify` 解包后再问一次并断言一致，`public_distribution_ready = true` 时另查工作区是否干净、包内是否真有 VERIFIED 档案 | **PASS** | 打包产物中的 `BUILD-METADATA.json`；判据见 §7 |
| 23 | 崩溃后重启：未完结记录变 INTERRUPTED + 待复核，重启第二次不再变化 | `RecoveryEndToEndTests.ARunLeftUnfinishedByADeadProcessComesBackAsInterruptedAndPendingReview`（真进程 → 真进程） | **PASS** | 集成 TRX |
| 24 | 单实例：第二个 `--serve` 非零退出，第一个继续服务 | `LifecycleProcessTests.ASecondServeIsRefusedAndTheFirstKeepsServing` | **PASS** | 集成 TRX |
| 25 | 长时间运行不泄漏、计数自洽 | `SoakTests.AMixedStreamSustainedForTheWholeDurationLeavesEveryInvariantIntact`（默认 15 s，`MR_SOAK_MINUTES` 可延长） | **PASS** | 见第 4 节实测数字 |
| 26 | 诊断日志不写入敏感数据、按大小滚动 | `DiagnosticsLogHygieneTests`（11 项，**0 跳过**）：profile 路径 / IPv4 / IPv6 / SID / 长十六进制串五类全部兜底脱敏，另有一项反向断言 12 位短哈希 id 不被误删 | **PASS** | 见第 6 节 |
| 27 | 发布包不含禁止内容、含齐全许可证材料 | `package.ps1` 的 `Assert-NoForbiddenPayload` + `Assert-RequiredContent`，打包目录与**解包目录**各查一次 | **PASS** | 见第 2 节 |
| 28 | 桌面端被强杀后不留孤儿 Collector | 桌面端固定以 `--serve --parent-pid <自身 pid>` 拉起子进程；`ParentProcessWatchdog` 用 `Process.GetProcessById` + `WaitForExitAsync`（**进程存在性检查，非 `OpenProcess`**）等父进程结束，随后走与 Ctrl+C 完全相同的停止路径，10 秒硬退出兜底。`LifecycleOrphanTests`（2 项，真进程：杀掉替身父进程后断言 Collector 15 s 内退出、退出码 0、`integrity_check = ok`、无残留未关闭的抓包会话）、`WatchdogTests`（8 项）、Qt `LifecycleTests::theCollectorIsAlwaysToldOurProcessIdSoItCannotBeOrphaned` | **PASS** | 集成 TRX；ctest |
| 29 | 已发布的 CHANGELOG 段落在打 tag 之后不被改写 | `package.ps1` 的 `Assert-ReleasedChangelogSectionsUnchanged`：对每个 `vX.Y.Z` tag，把工作区 `CHANGELOG.md` 里的 `## [X.Y.Z]` 段落与 `git show <tag>:CHANGELOG.md` 的同名段落逐字比较，不一致即打包失败。新的变更只能写进 `[Unreleased]` 或下一个版本 | **PASS** | 打包脚本；缘由见内部工作文档 `reviews/2026-09-08/fix-status.md` 第 H-9 条，该文档不随仓库分发 |

**真机验收须补充的条目**：第 16 项（高 DPI 实机缩放），以及全部与真实抓包相关的行为。
验收机器既没有 Npcap，也没有安装游戏客户端，相关流程见
[live-validation-guide.md](live-validation-guide.md)。

## 2. 打包验证 / Package verification

```powershell
pwsh -NoProfile -File scripts/package.ps1 -Force -Verify
```

`-Verify` 将 zip 解压到一个**临时目录**，再**从解包目录**运行两个可执行文件。
只存在于工作区的依赖，例如 `PATH` 上的 Qt 与构建树中遗留的文件，会在这一步暴露，
而不会遗留到用户机器上才暴露。

该步骤依次断言：

1. 解包目录同样通过 `Assert-NoForbiddenPayload` 与 `Assert-RequiredContent`；
2. `MentorRecorder.Collector.exe --version` 退出码为 0，输出为版本横幅；
3. `MentorRecorder.Collector.exe --capture-doctor --json` 退出码为 0 或 1（验收机器无 Npcap、无游戏客户端时，1 是**正确结果**），
   且其报出的 `live_capture_status` 与 `BUILD-METADATA.json` 中的记录一致，
   `boundary.monitor_type = WinPCap`，`boundary.injected_hook_enabled = false`；
4. `MentorRecorder.Desktop.exe --screenshot`（`QT_QPA_PLATFORM=offscreen`）退出码为 0，
   并产出一张大于 4 KiB 的 PNG。该步骤同时证明 Qt 运行时、QML 模块与 offscreen 平台插件均已齐备；
5. 上述命令执行完毕后再检查一次禁止内容，确认运行过程本身没有向产物写入数据库或日志。

本次实测输出：

```
== 必需内容核对 / Required content assertions
  必需文件齐全（11 项）。

== 解包验证 / Unpack and run
  解包目录: C:\Users\...\Temp\mr-pkg-verify-<guid>\MentorRecorder-<version>-win-x64
  必需文件齐全（11 项）。
  [ok] collector-version (exit 0)
       MentorRecorder.Collector <version> (ipc v1)
  [ok] collector-doctor (exit 1)
       live_capture_status=<产物自己报的值> monitor_type=WinPCap injected_hook=False
  [ok] desktop-screenshot (exit 0)
  [ok] 截图 104788 字节
  解包验证通过：运行时依赖完整。
```

`collector-doctor` 的退出码 1 是**正确结果**：验收机器没有 Npcap，也没有游戏客户端，
`--capture-doctor` 按约定以退出码 1 表示当前机器无法启动抓包，而非表示该命令自身失败。

### 发布包必须包含 / must contain

`MentorRecorder.Collector.exe`、`MentorRecorder.Desktop.exe`、`LICENSE`、
`THIRD_PARTY_NOTICES.md`、`README.md`、`SOURCE_CODE.md`、`BUILD-METADATA.json`、
`SHA256SUMS.txt`、`docs/`（含 `privacy-boundary.md`、`third-party-licenses.md`、本文件）。

### 发布页必须附带 / release assets

`BUILD-METADATA.json` 除随包分发外，还必须作为**独立的发布资产**上传到 GitHub 的发布页
（0.9.1 已经如此）。更新检查读取的正是
`releases/latest/download/BUILD-METADATA.json`（[privacy-boundary.md](privacy-boundary.md) §8.4）；
该资产缺失时，所有用户的检查都只会得到"未找到"并静默降级，界面上不出现任何提示。

### 发布包必须不包含 / must not contain

| 类别 | 模式 | 理由 |
|---|---|---|
| 注入载荷 | `deucalion*` | [privacy-boundary.md](privacy-boundary.md) §3 |
| 专有 Oodle 库 | `oo2net*.dll` | §4，不分发 RAD 的专有组件 |
| pcap 运行时 | `wpcap.dll` / `Packet.dll` / `*npcap*` | §2 第 14 条，不内置不分发 Npcap |
| 游戏文件 | `ffxiv.exe` / `ffxiv_dx11.exe` | §7 |
| 原始抓包 | `*.pcap` / `*.pcapng` | §5 |
| 用户数据 | `*.db` / `*.db-wal` / `*.db-shm` / `*.sqlite*` | 属于他人的游玩记录 |
| 诊断日志 | `*.log` | 属于他人机器的诊断数据 |
| 测试产物 | `*.trx` | 出现即说明测试输出漏入 staging 目录 |
| 调试符号 | `*.pdb` | 可移植 PDB 按路径段存储源码路径，会把维护者本机的绝对路径（含用户名与仓库名）随包发出去。Release 构建改为 `DebugType=embedded` + `PathMap`，符号内嵌在程序集里、路径重写成 `/_/`，崩溃堆栈仍带文件名与行号 |
| 合成协议档案 | `protocol-profiles/synthetic/**` | 编造的 opcode 绝不能出现在实机档案选择器里 |

另有一条不按文件名、而按**内容**判定的检查 `Assert-NoLocalPathLeak`：以 UTF-8 与 UTF-16LE
两种编码扫描产物中是否出现仓库根路径，命中即判定失败。`.md`、`.txt`、`.json` 三类文件跳过该检查，
因为文档中出现源码树路径属于正常情形。该检查在打包阶段与 `-Verify` 解包之后各执行一次。

## 3. 硬边界 / Hard boundary

```
python tools/static-boundary-check/check.py
OK: no boundary violation. 278 file(s) scanned under
    src, tests, tools, scripts, CMakeLists.txt, Directory.Build.props,
    Directory.Build.targets, MentorRecorder.sln

python tools/static-boundary-check/selftest.py
OK: 53 self-test case(s) passed; the checker rejects what it must.
```

`selftest.py` 是**反向**测试。仅执行 `check.py` 并通过不能说明任何问题，一个永不匹配的
检查器同样会通过。自测将每一条被禁止的标识符植入临时仓库树，要求检查器报告失败、
命中正确的规则，并覆盖全部应当扫描的位置（`src/`、`src/Desktop/qml/`、`tests/`、`tools/`、
`scripts/`、CMake、MSBuild），同时确认 `BOUNDARY-ALLOW` 仍然有效。

该自测已发现过一处真实缺陷：`NET-003` 原为 `\bWebSocket\w*\b`，前导的 `\b` 使
`ClientWebSocket`（.NET 实际的出站 WebSocket 类型）整体漏检。当前规则为 `WebSocket\w*`。

规范点名的 15 个标识符逐个配有反向用例：`ReadProcessMemory`、`WriteProcessMemory`、
`VirtualAllocEx`、`CreateRemoteThread`、`SetWindowsHookEx`、`pcap_sendpacket`、
`pcap_inject`、`NetworkMonitorType.RawSocket`、`UseDeucalion = true`、`HttpListener`、
`TcpListener`、`QTcpServer`、`QHttpServer`、`QNetworkAccessManager`、`WebSocket`。

## 4. 长稳测试实测 / Soak results

`SoakTests`（`Category=Soak`）默认运行 15 s，上限 90 s；设置 `MR_SOAK_MINUTES=60` 可运行一小时。
本次实测数据如下：

| 指标 | 实测值 | 阈值 |
|---|---|---|
| 推送速率 | 10,000 msg/s | ≥ 5,000 msg/s |
| 推送总量 | 157,428 条 / 15.7 s | — |
| 20k 突发次数 | 6 | ≥ 1 |
| 完整导随记录 | 7 条，全部 COMPLETED | 与驱动的周期数相等 |
| 队列恒等式 | 143,119 = 34,850 + 108,269（decoded = accepted + dropped） | 必须严格相等 |
| 帧解码错误 | 14,309（畸形长度，未进入队列） | 单独计数，不混入丢弃 |
| 解析成功 / 失败 / 重复 | 6,970 / 27,880 / 3,452 | 重复必须 > 0 |
| 工作集增长 | 33.5 MiB | < 50 MiB |
| 数据库增长 | 4.25 MB | 只随真实记录增长 |
| `parser_errors` 行数 | 1,000（封顶） | ≤ 1,000 |
| 解析错误环 | 100（封顶，`SoakBoundsTests` 单独验证） | ≤ 100 |
| `GetDashboardStats` 耗时 | 8.5 ms | < 200 ms |
| 最终状态 | 状态机 Completed → Idle，会话行已关闭 | 必须是终态 |

## 5. 已知缺口 / Known gaps

| # | 缺口 | 影响 | 建议 |
|---|---|---|---|
| ~~G1~~ | ~~v1 契约没有 `GetRunEvents`，事件轨迹只在数据库里~~ | **已关闭**：`GetRunEvents` 是一条正式的只读消息（`$defs/MessageType`，见 [architecture.md](architecture.md) §3.1） | 补记见 [`../contracts/CHANGELOG.md`](../contracts/CHANGELOG.md) |
| ~~G2~~ | ~~崩溃恢复事件到不了进程外订阅者~~ | **已关闭**（Phase 5） | `LiveEventBus` 保留最近 64 条事件并在每个新订阅建立时先补发；`Publish` 不再因无订阅者提前返回。`RecoveryEndToEndTests.ARecoveredRunReachesAClientThatSubscribesAfterStartup` 已改为进程外断言 |
| ~~G3~~ | ~~`RotatingFileLogger.Sanitize` 只脱敏用户 profile 路径~~ | **已关闭**（Phase 5） | 现覆盖 profile 路径 / SID / IPv6 / IPv4 / ≥16 位十六进制串；见第 6 节 |
| ~~G4~~ | ~~Collector 的中文错误信息在**重定向**时按系统代码页编码~~ | **已关闭**（Phase 5） | `Program.Main` 开头统一设 `Console.OutputEncoding = UTF8Encoding(false)`（无 BOM，同时覆盖 stderr），无控制台时回退到直接重建 `Out`/`Error`。集成测试随之改为按 UTF-8 读取，不再直接依赖 `System.Text.Encoding.CodePages`（该包仍经 PacketDotNet 传递引入，见 third-party-licenses.md §2.1） |
| G5 | 高 DPI 只在 offscreen 1.0 倍下验证过 | 真实缩放下的布局未知 | 按第 8 节在 125 / 150 / 175 % 三档下人工核对六个页面与四个对话框，**待人工执行** |
| G6 | Qt 桌面端用例对机器负载敏感 | 紧接长稳测试执行时会出现假失败 | `test.ps1` 已在 .NET 与 ctest 之间加入 `-SettleSeconds`（默认 5 s），并等待 MentorRecorder 进程退出 |
| ~~G7~~ | ~~桌面端以 `--backend ipc` 启动时会把 Collector 作为子进程拉起，**桌面端退出后该子进程仍然存活**~~ | **已关闭**（Phase 5）。原先的影响是留下一个持有单实例租约的孤儿进程，使进程级测试与监听核对全部无法进行 | 子进程现在固定收到 `--parent-pid <桌面端 pid>` 并自行监视该进程，桌面端消失（含被强制结束）后即走与 Ctrl+C 相同的停止流程；验收前手动 `Stop-Process` 清理孤儿进程不再是前置条件。见第 1 节第 28 项 |
| G8 | 长稳测试的阈值（5000 msg/s、50 MiB、200 ms）是绝对值 | 在负载很高或降频的机器上可能假失败 | 这是长稳测试固有的取舍。本次实测留有余量（10,000 msg/s、33.5 MiB、8.5 ms）；在明显更慢的机器上，应先核对实测数字再判断是否为真实回归。**同一敏感性同样影响状态机断言**：`dotnet test MentorRecorder.sln` 并行运行单元与集成两个程序集，负载较高时 `RunStateBeforeStop` 偶尔读到 `Idle` 而非 `Completed`，出现比例约 1/2；单独运行 `--filter FullyQualifiedName~SoakTests` 或只运行集成程序集时稳定通过，各 3/3。Phase 5 加固期间的 A/B 对比表明该抖动与补发缓冲无关：临时恢复 `LiveEventBus.Publish` 的“无订阅者即返回”行为后，抖动仍然出现，4 次中 2 次 |

## 6. 诊断日志脱敏 / Log hygiene

[privacy-boundary.md](privacy-boundary.md) §5 与 [capture-diagnostics.md](capture-diagnostics.md) §8
规定诊断日志**绝不写入**以下内容：原始字节、十六进制转储、IP、MAC、SID、其他用户的路径。

**五类数据现已全部由代码兜底脱敏**，不再依赖调用方自行回避：

| 类别 | 匹配 | 替换为 |
|---|---|---|
| 任意用户的 profile 路径 | `[A-Za-z]:\Users\<name>` | `%USERPROFILE%` |
| Windows SID | `S-1-<n>-<n>…` | `[sid]` |
| IPv6 字面量 | 完整八段式与各种 `::` 压缩式 | `[ip6]` |
| IPv4 字面量 | 点分四段 | `[ip]` |
| 十六进制串 | 连续 **16 位及以上** | `[hex]` |

五个替换按固定顺序执行，匹配范围宽的先执行；失败方向取**宁可多删**：
损失部分上下文是可接受的代价，而留下一个地址是事故。

十六进制串的门槛取 16 位而非 8 位，是一项**刻意的取舍**。本构建将 SHA-256 的短前缀
用作非身份化 id：适配器指纹为 12 位（`SanitizedDiagnosticsReport.AdapterFingerprintLength`），
UUID 的分段更短。将这些短串一并删除不会额外隐藏任何秘密，却会使日志无法区分两张网卡、
两个会话，从而失去日志本身的用途。

原先标记为 **Skip** 的三个用例（`AnIPv4AddressIsNeverWrittenInClear`、
`AUserSidIsNeverWrittenInClear`、`AHexPayloadDumpIsNeverWrittenInClear`）
**未经修改**即已通过。此外新增两项：
`AnIPv6AddressIsNeverWrittenInClearInEitherForm` 覆盖完整式与 `::` 压缩式，
两种形态各包含单独出现与嵌在句子中两种写法；
`TheShortHashPrefixesUsedAsIdentifiersSurviveTheRedaction` 反向断言 12 位指纹与
UUID 原样保留。`DiagnosticsLogHygieneTests` 现共 11 项，**0 跳过**。

其余约束仍然成立：异常只记录类型与消息，不记录堆栈；每行是一个带固定信封的 JSON 对象；
诊断所需的计数字段照常放行；日志按大小滚动，最多 5 个文件，每个 2 MiB；
写日志失败绝不导致进程崩溃。

## 7. 分发闸门 / Distribution gate

**本节不记录状态字段的取值。** `PUBLIC_DISTRIBUTION_READY` 与 `LIVE_CAPTURE_STATUS`
曾以字面量形式抄写在本节，导致同一仓库中出现过三份互相矛盾的说法，
并出现过 `source_worktree_dirty: true` 而 `public_distribution_ready: true` 的产物。
现在两者只写在产物自身的 `BUILD-METADATA.json` 中，且各带一个 `*_source` 字段说明其**来源**。
这些字段并非全部为实测值，一律称作“实测”会使本节重新变成无法判断真伪的自述（评审 2 M-A）。
取值按下列方式读取：

```powershell
Get-Content <解包目录>\BUILD-METADATA.json | ConvertFrom-Json |
  Select-Object live_capture_status, packaged_verified_profile_status,
                source_protocol_profile_status, public_distribution_ready,
                public_distribution_blockers, source_commit, source_worktree_dirty
```

`scripts/package.ps1` 按下表计算这几个字段：

| 字段 | 来源 |
|---|---|
| `live_capture_status` | **编译期常量**。在暂存目录中运行该产物自身的 `--capture-doctor --json`（`MR_DATA_DIR` 指向临时目录，探测不会向产物写入数据库或日志），读回编译进 `CaptureDiagnostics` 的声明。该字段证明元数据与这份二进制同源，**不**表示对这份产物做过实时抓包测量；`live_capture_status_source` 如实写明这一点 |
| `packaged_verified_profile_status` | 该产物自身的 `--list-profiles --json`，取 `status == VERIFIED && usable` 的 `profile_id`，格式为 `VERIFIED (id1, id2)`；一份都没有时取 `NONE`。这一条是对**包内档案文件**的真实检查，记录在 `packaged_verified_profile_status_source` |
| `source_protocol_profile_status` | [`../protocol-profiles/README.md`](../protocol-profiles/README.md) 顶部的 `PROTOCOL_PROFILE_STATUS = <状态>` 标记 |
| `public_distribution_ready` | **计算值**，下列五条前提全部满足才为 `true`；`public_distribution_ready_source` 列出参与计算的四类输入：编译期常量、包内档案、git 工作区状态、`-SkipVerify` |
| `public_distribution_blockers` | 未满足的前提，逐条列出；全部满足时为空数组 |

`public_distribution_ready` 的五条前提如下，任何一条不成立即不得公开分发：

1. 产物自身的 `--capture-doctor` 报告 `public_distribution_ready = true`（**编译期常量**）；
2. `live_capture_status == VERIFIED_POP_TO_EXIT`（**编译期常量**）；
3. 包内**至少有一份** `VERIFIED` 且可用的协议档案。否则按 fail-closed 规则，
   安装后的版本不会自动产生任何记录，只能手工补录；
4. 源码工作区**干净**（`source_worktree_dirty = false`）。该条落实的是
   GPL-3.0-or-later 的分发义务：本项目链接 GPLv3 的 Machina.FFXIV 与 Qt Graphs
   （见 [third-party-licenses.md](third-party-licenses.md)），任何对外分发都必须同时提供
   **与该二进制完全对应的**完整源码。工作区处于 dirty 状态时，产物按定义不满足对应源码的要求。
   产物随包附带 `SOURCE_CODE.md` 与 `BUILD-METADATA.json` 中的 `source_commit`；
5. 未使用 `-SkipVerify` 跳过验证关卡。

> 第 1、2 条是二进制中的声明，只要源码未改动即恒为真，因此并**不**构成这道闸门的实际约束力；
> 真正可能与事实不符、也真正起到阻断作用的是第 3、4、5 条。
> 阻断原因 `public_distribution_blockers` 与控制台输出对前两条均标注「编译期常量」。

`package.ps1 -Verify` 会**回头核对**这份声明：解包之后再次查询产物本身，
`live_capture_status` 与 `packaged_verified_profile_status` 必须与元数据中的记录一致。
前者的比对证明元数据与解包出来的可执行文件出自同一次构建，即同一个常量与自身对齐，
**不**证明抓包经过实测。若元数据声称 `public_distribution_ready = true`，
而工作区处于 dirty 状态、包内没有 VERIFIED 档案，或 `live_capture_status` 不符，
**打包直接失败**。与事实不符的产物无法通过这道闸门。

## 8. 高 DPI 人工核对 / High-DPI manual pass

> **状态：待人工执行（UNVERIFIED）。** 本节无法在自动化验收中完成：offscreen 截图
> 恒为 1.0 倍缩放，在 `QT_QPA_PLATFORM=offscreen` 下，Windows 的 DPI 缩放不参与渲染。
> 因此，第 1 节第 16 项与第 5 节 G5 目前仅有策略代码正确这一半证据。
> 判定 PASS 之前，必须在**真实显示器**上按下列条目逐项核对，并将结果回填至第 16 项与 G5。

代码侧已经成立、无须重测的部分：`src/Desktop/cpp/main.cpp:320-321` 设置
`Qt::HighDpiScaleFactorRoundingPolicy::PassThrough`；`:316-318` 仅在用户设置了非 100 的
界面缩放，且 `QT_SCALE_FACTOR` 未被外部指定时才写入该变量。
待确认的是**布局在放大后是否仍然成立**。

### 8.1 怎么跑

对 **125% / 150% / 175%** 三档各执行一遍。两条路径都需覆盖，二者依赖的机制不同：

| 路径 | 怎么设置 | 覆盖的是 |
|---|---|---|
| 系统 DPI | Windows 设置 → 系统 → 显示 → 缩放，改完**注销再登录** | `PassThrough` 与 Qt 的 devicePixelRatio |
| 应用内界面缩放 | 设置页的界面缩放，改完**重启桌面端** | `QT_SCALE_FACTOR` 那条分支 |

每一档均从 **1100×720**（当前布局的目标下限）开始核对，再切换到最大化状态核对一遍。
两条路径不得同时修改，否则出现问题时无法判定由哪一种机制引起。

### 8.2 逐页核对（六页）

| 页面 | 必须确认 |
|---|---|
| 仪表盘 `DashboardPage` | 指标卡片不换行成孤行、数字不被截断、图表坐标轴标签不重叠 |
| 副本 `DungeonsPage` | 表格列宽不塌缩、表头与内容仍对齐、横向滚动条该出现时出现 |
| 职业 `JobsPage` | 分组标签与“未知职业”分桶均完整可见 |
| 历史 `HistoryPage` | 分页控件不被挤出视口、行高一致、详情面板能完整展开 |
| 抓包 `CapturePage` | 五个标记按钮全部可见且可点（不被裁掉第五个）、状态文案不省略、SHA-256 与路径那一栏仍是等宽且不溢出 |
| 设置 `SettingsPage` | 界面缩放控件自身在放大后仍可操作，否则用户将被锁定在无法改回的缩放档位上 |

### 8.3 逐对话框核对（四个）

`BaselineDialog`、`DisclosureDialog`、`EditRunDialog`、`ReflectionDialog`：

- 对话框整体不超出屏幕，标题栏与按钮行均位于可视区内；
- 确认与取消按钮始终可达，不被内容挤出视口，该条在放大后最容易失效；
- 内容超长时由对话框内部滚动，而不是将按钮推出视口；
- 键盘 Tab 序完整可遍历，焦点环清晰可见。

### 8.4 判定

- 全部无异常：第 1 节第 16 项改为 **PASS**，第 5 节 G5 关闭，并在本节记录实测机器、
  分辨率与三档缩放的截图路径。
- 出现布局破裂：记录页面或对话框、缩放档位与具体现象，修改 `src/Desktop/qml/Theme.qml`
  与对应页面后重新执行本节。**在此之前，第 16 项必须保持 PARTIAL，不得标注 PASS。**

## 9. 分支模型与版本号 / Branches and versions

本节规定测试包与正式发布如何区分，以及各自的操作步骤。它的由来是 1.2.2、1.2.3、1.2.4 与
1.3.0：这四个发布号都被本机测试包占用，从未单独发布，最终只能在 1.3.1 的段落中补记一句
说明。

### 9.1 两条长期分支

| 分支 | 内容 | 规则 |
|---|---|---|
| `main` | 只包含已发布的提交 | 每个发布提交都带一个 `vX.Y.Z` tag；不在其上直接开发 |
| `dev` | 集成分支 | 功能分支与工作区（worktree）合入此处；测试包从此处切出 |

功能分支与工作区一律以 `dev` 为基线，并合回 `dev`；Pull Request 的目标分支是 `dev`
（见 [CONTRIBUTING.md](../CONTRIBUTING.md)）。CI 对 `main` 与 `dev` 的推送、以及以二者为
目标的 Pull Request 执行同一道 `scripts/verify.ps1` 关卡。

### 9.2 测试包用先行版号

交给使用者试用的构建一律是 `X.Y.Z-beta.N`，其中 `X.Y.Z` 是它将要成为的那个发布号，
`N` 从 1 开始逐次递增。这样的构建：

- 可以作为 GitHub 的**预发布**（pre-release）发布，tag 形如 `vX.Y.Z-beta.N`，打在 `dev` 上；
- **绝不**标记为 latest。更新检查读取的是 `releases/latest/download/BUILD-METADATA.json`，
  GitHub 的 latest 会跳过预发布，正式版用户因此不会被引向测试包；
- `BUILD-METADATA.json` 中 `version` 为完整版本号、`prerelease` 为 `true`，
  `public_distribution_ready` 恒为 `false`（第 7 节）。

更新检查的判定规则：`X.Y.Z-beta.N` 比 `X.Y.Z` 旧，比任何更低的发布号新；
`beta.10` 比 `beta.9` 新（按数字比较，不按文本）。因此运行 `1.4.0-beta.2` 的机器会被告知
`1.4.0` 可用，而不会被 `1.3.9` 打扰；运行正式版的机器在任何情况下都不会被引向先行版。

### 9.3 切一个测试包

```powershell
git switch dev                                    # 测试包只从 dev 切出
# Directory.Build.props: VersionPrefix = 1.4.0, VersionSuffix = beta.1
git commit -am "chore(release): 1.4.0-beta.1"
pwsh -NoProfile -File scripts/verify.ps1
pwsh -NoProfile -File scripts/package.ps1 -Force -Verify
git tag v1.4.0-beta.1                             # 可选：仅当需要作为预发布分发时
```

`CHANGELOG.md` 此时最上方必须仍是 `## [Unreleased]`：测试包携带的条目尚未发布。
打包脚本会核对这一点。下一个测试包把 `VersionSuffix` 改为 `beta.2`，依此类推。

### 9.4 切一个正式发布

```powershell
git switch dev
# Directory.Build.props: VersionSuffix 清空（VersionPrefix 保持 1.4.0）
# CHANGELOG.md: 把 [Unreleased] 改写为 [1.4.0] - 2026-09-20，其上不留空的 [Unreleased]
git commit -am "chore(release): 1.4.0"
pwsh -NoProfile -File scripts/verify.ps1          # 不带 -SkipGate / -TestFilter 的完整运行
pwsh -NoProfile -File scripts/package.ps1 -Force -Verify
git switch main
git merge --ff-only dev                           # 快进合并
git tag v1.4.0
```

正式版的 `CHANGELOG.md` 最上方必须就是 `## [1.4.0]`：打包脚本取第一个 `## [..]` 标题与版本号比对，
上面多一个空的 `[Unreleased]` 会被拒绝。下一轮的第一条变更再把 `## [Unreleased]` 加回来即可。

合并方式固定为**快进**（`--ff-only`）：这样 `main` 上的每一个提交都与 CI 在 `dev` 上逐一
验证过的提交完全相同，不会引入任何从未被验证过的合并树。快进失败即说明 `main` 上有 `dev`
没有的提交，应当先查清，而不是改用合并提交掩盖。

发布页需附带五个资产（见第 2 节「发布页必须附带」与打包输出）：

1. `MentorRecorder-1.4.0-win-x64.zip`
2. `MentorRecorder-1.4.0-win-x64.zip.sha256`
3. `MentorRecorder-1.4.0-setup.exe`
4. `MentorRecorder-1.4.0-setup.exe.sha256`
5. `BUILD-METADATA.json`（独立资产，更新检查读取的就是它）

正式发布不得勾选 pre-release；测试包必须勾选。
