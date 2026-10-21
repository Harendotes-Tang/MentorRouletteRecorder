# 构建与打包 / Build and Package

本文档说明本仓库的工具链要求、构建与测试命令、脚本职责以及发布打包流程。
读者为参与开发或需要自行构建发布产物的维护者。
文中的路径与版本以开发机的验证结果为基准，其他环境按 §3 列出的环境变量覆盖。

## 1. 开发机工具链（2026-09-04 验证）

下表的路径与版本在开发机上验证通过，脚本与 CMake 命令均以此为基准。

| 组件 | 版本 | 路径 |
|---|---|---|
| .NET SDK | 9.0.316 | `C:\Program Files\dotnet\dotnet.exe`（在 PATH 上） |
| .NET 运行时 | 8.0.30（目标框架 `net8.0-windows`，RID `win-x64`） | 同上 |
| Qt | **6.11.2**（MinGW 64 位） | `D:\APPS\Qt\6.11.2\mingw_64` |
| C++ 编译器 | GCC 13.1（MinGW-w64） | `D:\APPS\Qt\Tools\mingw1310_64\bin\g++.exe` |
| C 编译器 | 同上 | `D:\APPS\Qt\Tools\mingw1310_64\bin\gcc.exe` |
| Ninja | 随 Qt 安装 | `D:\APPS\Qt\Tools\Ninja\ninja.exe` |
| CMake | 随 Qt 安装（另有系统 cmake 3.28 在 PATH 上） | `D:\APPS\Qt\Tools\CMake_64\bin\cmake.exe` |
| Python | 3.11 | 在 PATH 上（`python`） |
| PowerShell | 7（`pwsh`），脚本要求 7 及以上；另有 Windows PowerShell 5.1（`powershell`），脚本拒绝在其中运行 | 在 PATH 上 |

> 开发机**未安装 MSVC C++ 工具集与 Windows SDK**，C++ 一律使用 MinGW 构建。
> 因此桌面端不使用任何依赖 MSVC 的 Qt 模块或第三方库。

已确认存在的 Qt 模块：`Qt6Quick`、`Qt6QuickControls2`、`Qt6Graphs`、`Qt6Quick3D`、
`Qt6ShaderTools`、`Qt6TextToSpeech`、`Qt6Charts`、`Qt6Svg`、`Qt6Widgets`。

> 注意：Qt Charts 自 Qt 6.10 起已弃用。本项目图表使用 **Qt Graphs**。

## 2. C# 侧（Collector）

```powershell
dotnet build MentorRecorder.sln -c Release
dotnet test  MentorRecorder.sln -c Release
```

统一属性来自根目录 `Directory.Build.props`：
`TargetFramework = net8.0`（Collector 覆盖为 `net8.0-windows`）、`RuntimeIdentifier = win-x64`、
`Nullable = enable`、`TreatWarningsAsErrors = true`、`LangVersion = latest`、
`InvariantGlobalization = false`（默认界面语言是简体中文，需要区域数据）。

### 版本号的唯一来源 / One source of truth for the version

`Directory.Build.props` 中的两行是**唯一**可以手写版本号的位置：

| 属性 | 含义 | 取值 |
|---|---|---|
| `VersionPrefix` | 发布号，始终是三段纯数字 | 如 `1.4.0` |
| `VersionSuffix` | 先行版标签；正式版留空 | 如 `beta.1` |

二者合成本次构建的完整版本号：留空时为 `1.4.0`，填写时为 `1.4.0-beta.1`。
交给使用者试用的测试包必须带后缀，正式发布时才把后缀清空，
以免一个发布号被一个并未发布的构建占用（详见
[release-checklist.md](release-checklist.md) 第 9 节）。

其余各处全部由这两行派生，不得出现第二个字面量。注意哪些位置只能容纳数字：
Windows 版本资源的 `FILEVERSION` / `PRODUCTVERSION` 是四段数字，放不下任何后缀。

| 位置 | 形状 | 版本来源 |
|---|---|---|
| Collector 程序集的 `AssemblyVersion` / `FileVersion` | 纯数字 | MSBuild 使用 `$(VersionPrefix).0` |
| Collector 程序集的 `InformationalVersion`（即资源中的 `ProductVersion`）、`--version` 横幅 | 完整 | MSBuild 使用 `$(Version)` |
| 根 `CMakeLists.txt` 的 `project(... VERSION ...)` | 纯数字 | `file(STRINGS)` 读 `VersionPrefix` |
| Desktop 版本资源的数字字段与 `FileVersion` 字符串 | 纯数字 | `app.rc.in` 中的 `@MR_VERSION@` |
| Desktop 版本资源的 `ProductVersion` 字符串 | 完整 | `app.rc.in` 中的 `@MR_VERSION_FULL@` |
| 标题栏与「关于」页显示的版本、`MentorRecorder.Desktop.exe --version` | 完整 | 编译定义 `MR_APP_VERSION`（取 `MR_VERSION_FULL`） |
| `BUILD-METADATA.json` 的 `version`、产物目录 / zip / 安装器文件名、`ISCC /DAppVersion` | 完整 | `scripts/package.ps1` 读 `Directory.Build.props` |
| 安装器自身版本资源 `ISCC /DAppVersionNumeric` | 纯数字 | 同上，取 `VersionPrefix` |

根 `CMakeLists.txt` 与桌面端的版本测试用**逐行正则**读这两个属性，取第一条匹配的行，
因此 `Directory.Build.props` 的注释里不得再出现带尖括号的这两个元素名——被注释掉的示例
会悄悄成为实际编译进桌面端的版本号。

`scripts/package.ps1` 在装配完成后核对每一处版本号，并按上表区分形状：两个可执行文件的
`FileVersion` 与数字部分比较，`ProductVersion` 与完整版本号比较，`BUILD-METADATA.json` 的
`version` 与完整版本号比较、`prerelease` 与是否带后缀比较，`CHANGELOG.md` 最上方的段落按下述
规则核对。任一处不一致即判定打包失败。该断言的由来是 0.2.2：当时 `app.rc` 中保留了独立的
版本字面量，导致桌面端在资源管理器中显示为 0.2.1。

**CHANGELOG 顶部段落**：当前版本是先行版时，最上方必须是 `## [Unreleased]`——测试包是从尚未
发布的工作中切出来的，它携带的条目仍属于未发布内容；当前版本是正式版时，最上方必须是
`## [x.y.z]` 且与版本号一致。

**发布规则**：已经打过 tag 的 CHANGELOG 段落不得再修改。同一步中的
`Assert-ReleasedChangelogSectionsUnchanged` 会针对每个 `vX.Y.Z` tag，将工作区
`CHANGELOG.md` 中的 `## [X.Y.Z]` 段落与 `git show <tag>:CHANGELOG.md` 中的同名段落比较：
标题行（含日期）与正文都必须逐字一致（区分大小写，换行统一为 LF 后比较），不一致则打包失败。
tag 中有该段落而工作区中找不到（段落被删除，或标题被改成别的版本号）同样算作改动；
tag 中原本不含该段落时，跳过。找不到 git 或当前目录不是仓库时跳过该检查并给出提示。已发布段落
记录的是对应 tag 中的内容，tag 之后的改动一律写入 `[Unreleased]` 或下一个版本。
`vX.Y.Z-beta.N` 形式的先行版 tag 不参与该比较：它标记的是测试包，其条目还在 `[Unreleased]`
中，本来就会继续改动。该关卡的由来见 `reviews/2026-09-08/fix-status.md`
（内部工作文档，不随仓库分发）的 H-9。

上述无副作用的函数（读取版本号、归一化两种形状、判定 CHANGELOG 顶部段落、取出并比较已发布段落，
以及只读查询 git 的 `Get-SourceTreeState`，见 §5）位于
`scripts/package-version.ps1`，由 `scripts/package.ps1` 点源引入，并由
`tools/package-verification/test_package_version.py` 逐条自测；该自测由
`scripts/run-python-tool-tests.ps1` 自动发现，`verify.ps1` 的「工具自测」关卡会运行它。
`run-python-tool-tests.ps1` 运行任何自测脚本之前，先在所用的解释器中逐个导入 `tools/requirements-ci.txt`
列出的包（`opencv-python-headless` 按 `cv2` 导入）；缺少任何一个即以退出码 1 失败，列出缺少的包，并提示运行
`python -m pip install -r tools/requirements-ci.txt`，或将 `MR_PYTHON_EXE` 指向已安装这些包的解释器。
unittest 报告的跳过数按脚本列在汇总末尾；跳过不改变退出码，但被跳过的测试在本机没有得到验证。

`installer/MentorRecorder.iss` 未提供版本号的默认值。手工调用必须写明 `ISCC.exe /DAppVersion=<version> installer\MentorRecorder.iss`，否则预处理器报错；
未另行指定 `/DAppVersionNumeric` 时，安装器自身的版本资源取 `AppVersion` 中第一个连字符之前的部分。

`Directory.Build.targets` 中的 `StripInjectionPayloadFromOutput` 目标会将
Machina.FFXIV 包中附带的原生注入载荷 `deucalion-*.dll` 从所有构建输出中删除
（见 [privacy-boundary.md](privacy-boundary.md) §3）。构建完成后可确认输出目录中
不存在该文件。

依赖：

| 包 | 版本 | 许可证 |
|---|---|---|
| `Machina.FFXIV` | 2.4.7.7 | GPL-3.0 |
| `Machina`（传递依赖） | 2.3.1.3 | GPL-3.0 |
| `Microsoft.Data.Sqlite` | 8.0.11 | MIT |
| `SQLitePCLRaw.*`（传递依赖） | 随上 | Apache-2.0 / MIT / public domain（SQLite 本体） |
| `SharpPcap` | 6.3.1 | MIT |
| `PacketDotNet`（传递依赖） | 1.4.8 | MPL-2.0 |
| `xunit` / `xunit.runner.visualstudio`（仅测试） | 2.9.3 / 4.0.0 | Apache-2.0 |
| `Microsoft.NET.Test.Sdk`（仅测试） | 18.10.1 | MIT |
| `JsonSchema.Net`（仅集成测试） | 7.3.4 | MIT |

## 3. C++ / Qt 侧（Desktop）

> 桌面端的构建脚本位于 `src/Desktop/CMakeLists.txt`，顶层配置会构建桌面程序、C++ 单元测试
> 与 QML 离屏加载测试。Windows 中文路径下的 Qt AUTOGEN 输出会自动转到临时 ASCII 路径。

> 下文的 `D:/APPS/Qt/…` 均为开发机上的**示例路径**，不是强制要求。
> 用户应替换为本机 Qt 的实际安装路径；仓库中不得出现任何带用户名的家目录路径。
>
> `scripts/build.ps1` 与 `scripts/test.ps1` 将同一组路径写为**可覆盖的默认值**。
> 下列四个环境变量可按需设置，其中的反斜杠会被自动替换为正斜杠：
>
> | 环境变量 | 覆盖的默认值 |
> |---|---|
> | `MR_QT_PREFIX` | `-DCMAKE_PREFIX_PATH` 的 Qt 前缀 |
> | `MR_MINGW_BIN` | MinGW 的 `bin`（gcc / g++） |
> | `MR_NINJA_EXE` | `ninja.exe` |
> | `MR_CMAKE_EXE` | `cmake.exe`（`scripts/test.ps1` 另用 `MR_CTEST_EXE` 指 `ctest.exe`） |

标准配置命令（MinGW + Ninja + Qt 6.11.2）：

```powershell
& "D:\APPS\Qt\Tools\CMake_64\bin\cmake.exe" `
    -S . -B build `
    -G Ninja `
    -DCMAKE_MAKE_PROGRAM="D:/APPS/Qt/Tools/Ninja/ninja.exe" `
    -DCMAKE_PREFIX_PATH="D:/APPS/Qt/6.11.2/mingw_64" `
    -DCMAKE_C_COMPILER="D:/APPS/Qt/Tools/mingw1310_64/bin/gcc.exe" `
    -DCMAKE_CXX_COMPILER="D:/APPS/Qt/Tools/mingw1310_64/bin/g++.exe" `
    -DCMAKE_BUILD_TYPE=Release
```

构建：

```powershell
& "D:\APPS\Qt\Tools\CMake_64\bin\cmake.exe" --build build
```

CMake 路径一律使用正斜杠 `/`。反斜杠在 CMake 中是转义字符。

### 3.0 界面翻译

译文文件位于 `src/Desktop/i18n/mentor_recorder_{zh_TW,en,ja}.ts`，源语言为简体中文。
构建时 `lrelease` 自动把它们编译为 `.qm` 并嵌入可执行文件（资源前缀 `/i18n`），发布包不需要额外文件。
修改或新增界面文字后，运行 `update_translations` 目标（`lupdate`）刷新三份 `.ts`，
再补齐其中 `type="unfinished"` 的条目：

```powershell
& "D:\APPS\Qt\Tools\CMake_64\bin\cmake.exe" --build build --target update_translations
```

`lupdate` 以 `-locations none -no-obsolete` 运行：不写行号，删除已不存在的源文本。
未翻译的条目在界面上显示简体中文原文。

### 3.1 桌面端验证

桌面目标链接 `Qt6::Quick`、`Qt6::QuickControls2`、`Qt6::Graphs`、
`Qt6::TextToSpeech`、`Qt6::Widgets`。该目标已按上述命令完成 configure 与构建，
并通过 C++ 单元测试与 QML 离屏加载测试，中文字体与布局另以截图检查。
上述自动化结果不等同于真实 Windows 桌面上的完整交互验收。

`Qt6::Widgets` 由**托盘图标**引入：`QSystemTrayIcon` 与 `QMenu` 属于 Qt Widgets，
因此 `main()` 使用 `QApplication` 而非 `QGuiApplication`。界面本身仍为纯
Qt Quick，没有任何 QWidget 参与渲染。

### 3.2 测试

```powershell
ctest --test-dir build --output-on-failure
```

| 测试 | 内容 |
|---|---|
| `MentorRecorderDesktopTests` | IPC framing、格式化、分页模型、职业统计（含契约字段 → 职能分组推导）、AppController、`RunFormValidator`（全部校验分支与前后对比 diff） |
| `MentorRecorderTtsService` | `TtsService` 模板替换与语速/音量映射（会发声的用例使用 Qt 的静音测试语音引擎，不经过本机语音；缺少该插件时这些用例明确跳过）、MockBackend 的 live 事件、契约 `$defs/LiveEvent` 每个 `kind` 的路由 |
| `MentorRecorderIpcRequests` | 每种消息的请求样本与 `tests/Fixtures/ipc-requests/` 对拍 |
| `MentorRecorderLifecycle` | `CollectorProcess` 的重启退避、单实例租约复用、主动停止；IPC 请求的时限；旧版本留下的设置文件（含已不再使用的说明确认记录）照常读取 |
| `MentorRecorderUpdateInstall` | 下载并安装：`update.download` 的状态映射与下载期间的状态重读、「立即安装」启动任何程序之前的三项核对（位置、普通文件、SHA-256）、安装程序从核对校验值到启动始终被持有且所有中止路径都释放它、启动之后的退出（退出时才释放）、正式构建交给系统外壳而模拟运行什么也不启动（按程序的实际依赖组合检查，而非由测试替换）、采集服务原样答回时的「没有开始下载，请稍候几秒再试。」、模拟后端，以及三处界面 |
| `MentorRecorderInstallerHold` | 在真实文件上检验 `InstallerHold`：按「立即安装」的方式持有的程序仍能启动（`whoami.exe` 的副本运行至退出码 0），持有期间及运行之后都不能以写入方式打开、改名、删除，也不能改名其所在文件夹，释放后可以删除；正被写入的文件无法持有；文件夹不能被持有；经由联接点到达的文件不被视为该路径上的文件；符号链接按链接本身持有且不算普通文件；通过所持句柄读取的内容与文件内容一致。本机无法创建所需的链接时相应用例跳过：创建文件符号链接需要相应权限，没有时符号链接一例跳过 |
| `MentorRecorderQmlUpdateDownload_<阶段>` | 软件内下载四个阶段的模拟截图：`downloading` 与 `verifying` 在总览横幅，`ready` 在设置页「通用」，`failed` 在「关于」；以 `--verify-text` 断言玩家读到的那一句 |
| `MentorRecorderIpcIntegration` | **拉起真实 Collector 子进程**（使用临时数据库），运行 `GetVersion` / `GetStatus` / `QueryRuns` / `CorrectRun` 的三条错误路径 / `GetRunRevisions` / `BackupDatabase` / live 事件 / 字段白名单 |
| `MentorRecorderQmlLoad` | 离屏加载整个场景图并抓帧 |
| `MentorRecorderQmlDetailPanel` | 打开历史记录详情浮层 |
| `MentorRecorderQmlEditDialog` | 在详情浮层之上打开手动修正对话框 |
| `MentorRecorderQmlBaselineDialog` | 首次启动 baseline 对话框 |

`MentorRecorderIpcIntegration` 在同目录（或 `../../src/Desktop`）找不到
`MentorRecorder.Collector.exe`、Collector 无法启动或拒绝服务时，以 **QSKIP** 跳过整个套件。
QtTest 与 ctest 都把这种跳过记为通过，因此仅安装 Qt 的检出直接运行 `ctest` 时，该套件同样显示通过。
`scripts/test.ps1`（`verify.ps1` 经由它运行全部测试）不接受这种结果：该套件未注册，或其 QtTest 统计行
缺失、通过数为 0、有任何失败或跳过，都判定测试失败（见 §4.1）。
该用例在 `cleanupTestCase` 中先对自己的子进程调用 `terminate()`，5 秒内未结束再调用 `kill()`，
并断言进程已结束，运行结束后不会遗留孤儿 Collector 进程。

QML 测试通过 `QQmlApplicationEngine::objectCreationFailed` 将 QML 错误转换为非零退出码。
每个截图用例还以 `--verify-text` 断言名称所指的内容确实出现在画面上（否则退出码 8）；
截图运行期间出现任何 QML / JavaScript 运行时警告时，画面照常写出，进程以退出码 10 结束，
ctest 另以 `FAIL_REGULAR_EXPRESSION` 匹配警告输出，任一检查命中都会导致用例失败。
模拟后端截图用例要求核对文字时，若测试目标并非「无法自动记录」提示、而该提示却遮挡了被测页面，画面仍会写出，
进程以退出码 13 结束；不以该提示为测试目标的截图用例因此都带 `--mock-recording-state listening`。

### 3.3 截图

```powershell
$env:PATH = "D:\APPS\Qt\6.11.2\mingw_64\bin;$env:PATH"
build\src\Desktop\MentorRecorder.Desktop.exe --screenshot out.png --page 5 --theme dark
```

仅 mock 后端可用：`--mock-live entered|matched|none`、`--mock-npcap-missing`、
`--mock-first-run`、`--mock-open-detail`、`--mock-open-edit`、
`--mock-open-reflection`（对第一条记录打开导随心得对话框）、
`--mock-calibration observing|ready|blocked|done`（本机校准卡片）、
`--mock-shared fetching|verifying|consent|verified|verified-auditing|verified-proven|imported-published|imported-unpublished|rejected|unavailable|user-rejected|none-for-build|share`
（校准卡片的共享校准一节；未提供 `--mock-calibration` 时自动补一个观察中或已完成的本机校准，且不执行任何下载。
`verified-auditing` 为登录时已核实、排本与进本仍在核对；`verified-proven` 为共享档案已在本机完整记录过一次、
卡片提供「这份校准好用，告诉其他玩家」；`imported-published` 与 `imported-unpublished`
为导入后命中／未命中公开仓库索引的校准码）、
`--mock-update-available`（更新检查报告有新版本）、
`--mock-update-download downloading|verifying|ready|failed`（软件内下载停在指定阶段，隐含 `--mock-update-available`；
不联网，模拟运行从不启动安装程序）。
两种后端都可用：`--open-detail`、`--open-edit`、`--show-disclosure`、
`--mock-open-create`（打开「新增遗漏记录」向导）、`--mock-wizard-step 1|2|3`（向导停留的步骤序号）、
`--mock-detail-tab info|events|revs|refl`（详情浮层默认页签，会一并打开浮层）、
`--mock-ui-style eorzea|classic`（本次运行固定界面风格，不改写 desktop.ini）、
`--export-target <dir>`、`--screenshot-delay <ms>`、`--screenshot-size <WxH>`。
默认使用 offscreen 平台，设置 `QT_QPA_PLATFORM=windows` 可渲染真实窗口。
详见 [ui-design.md](ui-design.md) §7。

对真实后端截图需要加上 `--backend ipc`，并将延迟提高到 2500 ms 以上，
否则首批 `GetStatus` 与 `QueryRuns` 尚未返回即已抓帧。

### 3.5 两个进程：开发期的运行方式

在发布布局中，`MentorRecorder.Collector.exe` 与 `MentorRecorder.Desktop.exe`
位于**同一目录**（`scripts/build.ps1` 会把 Collector 的输出复制到 `build/src/Desktop/`）。
桌面端以交互方式启动时默认使用 `--backend ipc`，并以 `--serve` 参数将该程序作为**子进程**拉起。
发布构建只在桌面端可执行文件的同目录及其 `collector\` 子目录中查找，路径不来自任何设置、
命令行参数或环境变量；只有以 `MR_DEV_COLLECTOR_DISCOVERY` 编译的开发构建另外读取
`MR_COLLECTOR_PATH` 并在源码树中查找（见 §3.6）。

日常开发有两种运行方式：

**A. 由桌面端自行拉起 Collector（最接近发布形态）**

```powershell
$env:PATH = "D:\APPS\Qt\6.11.2\mingw_64\bin;$env:PATH"
build\src\Desktop\MentorRecorder.Desktop.exe
```

数据库位于 Collector 的默认位置（`%LOCALAPPDATA%\MentorRecorder\`），
顶栏显示「后台服务已启动」。

**B. 先单独启动一个 Collector（用于指定数据库或查看服务端日志）**

```powershell
# 先用 fixture 灌一份可看的数据
$db = "$env:LOCALAPPDATA\MentorRecorder\dev.db"
Get-ChildItem tests\Fixtures\*.fixture.json | ForEach-Object {
    build\src\Desktop\MentorRecorder.Collector.exe --replay $_.FullName --db $db
}
build\src\Desktop\MentorRecorder.Collector.exe --replay-decoded `
    tests\Fixtures\decoded\synthetic_complete.decoded.json --db $db `
    --profile protocol-profiles\synthetic\synthetic-v1.json

# 再起服务端，然后启动桌面端
build\src\Desktop\MentorRecorder.Collector.exe --serve --db $db
build\src\Desktop\MentorRecorder.Desktop.exe
```

指定 `--db` 之后，诊断日志随之落在该数据库旁边的 `logs\` 目录，而不是
`%LOCALAPPDATA%\MentorRecorder\logs`。否则一次开发调试便可能触发日志轮转，
并按保留期删除用于排查问题的那部分日志。日志目录可另用 `--log-dir <path>` 指定；
将数据库、日志、备份与默认导出目录一并迁移，则设置环境变量 `MR_DATA_DIR`
（见 [architecture.md](architecture.md) §2.3，退出码表见 §2.4）。

此时桌面端拉起的子进程会因**每用户单实例租约**立即退出，顶栏显示
「后台服务运行中（沿用之前启动的）」。这是正常行为，桌面端不会因此进入重启循环。
详见 [ui-design.md](ui-design.md) §4.8。

Collector 非预期退出时，桌面端按 0.8 s 至 30 s 的指数退避自动重启，并弹出一条 toast 提示；
子进程连续运行满 60 秒后再退出，退避才重新从 0.8 s 开始。以下情形不再重启，顶栏显示
「后台服务无法启动：<原因>」，同一原因只提示一次：退出码 2（无法识别启动参数）、
尚未开始服务就以退出码 3 退出（数据库或数据目录不可用，原因取自子进程标准错误的最后一行），
以及 Windows 连续两次拒绝运行该程序。其中退出码 3 每 5 分钟静默重试一次，因为占用数据库的
程序可能稍后释放它。

桌面端正常退出（托盘「退出」，或在「关闭时最小化到托盘」关闭的情况下关窗）时，若子进程是本软件
拉起的、且 `serve.pid` 尚未写出或记录的正是它，先置位 Collector 的停止事件并最多等待 10 秒，
让它正常收尾；没有停止事件可用或到时仍未退出，才 `terminate()`，0.5 秒后 `kill()`。
使用 `Stop-Process` 或任务管理器强制结束桌面端不会执行析构；子 Collector 以 `--parent-pid`
监视桌面端进程，发现其结束后自行走正常停止流程，10 秒内未完成则强制退出。
验证结束后应确认没有残留进程：

```powershell
Get-Process -Name "MentorRecorder*" -ErrorAction SilentlyContinue
```

### 3.4 运行时 DLL

MinGW 构建的 Qt 应用在运行时需要 Qt 的 `bin` 目录与 MinGW 的 `bin` 目录位于 `PATH` 上：

```powershell
$env:PATH = "D:\APPS\Qt\6.11.2\mingw_64\bin;D:\APPS\Qt\Tools\mingw1310_64\bin;$env:PATH"
```

发布时改用 `windeployqt`（见 §5）。

### 3.6 IDE 构建时 Collector 的暂存（`MR_STAGE_COLLECTOR`）

桌面端启动时会在自身目录中查找并拉起 `MentorRecorder.Collector.exe`。
仅使用 Qt Creator 构建而不运行 `scripts/build.ps1` 时，构建目录中没有
Collector，顶栏提示「缺少后台服务，软件可能没有安装完整。请重新安装后重试。」（以
`MR_DEV_COLLECTOR_DISCOVERY` 编译的开发构建改为给出期望路径与构建建议，见本节末条），
Npcap 与 FF14 等状态停留在默认值。

`src/Desktop/CMakeLists.txt` 提供 `mr_stage_collector` 目标，默认随 `ALL`
构建：只要 PATH 上存在 `dotnet`，该目标先执行 `dotnet build` 构建 Collector，再由
`src/Desktop/cmake/StageCollector.cmake` 将输出复制到 `MentorRecorder.Desktop.exe`
所在目录，复制时拒绝任何 `deucalion*`，并跳过 `*.db` 与 `*.log`。

- 关闭：`-DMR_STAGE_COLLECTOR=OFF`。
- 未安装 .NET SDK 时打印一条 `Collector staging skipped` 并跳过，不影响桌面端构建。
- 查找顺序（`CollectorProcess::resolveDefaultExecutable`）：exe 同目录 → `collector/` 子目录。
- 开发期的额外查找由 CMake 选项 `MR_DEV_COLLECTOR_DISCOVERY` 控制，默认 `OFF`，
  `scripts/build.ps1` 总是显式以 `-DMR_DEV_COLLECTOR_DISCOVERY=OFF` 配置。只有手动以
  `-DMR_DEV_COLLECTOR_DISCOVERY=ON` 配置的开发构建才读取环境变量 `MR_COLLECTOR_PATH`
  （设置后它是唯一的候选，文件不存在也不回退到其他位置），并在上述两处之后从构建目录逐级向上
  查找源码树中的 `src/Collector/bin/{x64/,}{Release,Debug}/net8.0-windows/win-x64/`。
  `scripts/package.ps1` 拒绝打包以该选项编译的桌面端（`Assert-DesktopExecutable`，见 §5）。
- 仍未找到时，发布构建的顶栏提示「缺少后台服务，软件可能没有安装完整。请重新安装后重试。」；
  以 `MR_DEV_COLLECTOR_DISCOVERY` 编译的开发构建提示「未找到 Collector（期望位置：<路径>）。请运行
  scripts/build.ps1、在 CMake 中启用 MR_STAGE_COLLECTOR，或设置 MR_COLLECTOR_PATH」，便于排查。

## 4. 脚本

全部脚本位于 `scripts/`，以 PowerShell 编写，要求 PowerShell 7（`pwsh`）：每个脚本首行为
`#Requires -Version 7.0`，在 Windows PowerShell 5.1 中运行会被直接拒绝，而不是在中文 Windows 上因编码
误读而出错。

| 脚本 | 作用 |
|---|---|
| `bootstrap.ps1` | 检查 .NET 8 运行时、CMake、Ninja、MinGW、Qt 路径；报告 Npcap 是否安装（**只检测，不下载**） |
| `build-local-ai-ocr-worker.ps1` | 使用指定的构建 Python 和本地模型生成自包含 CPU OCR；显式传入 `-InstallBuildDependencies` 时才安装固定的构建依赖 |
| `bootstrap-ocr.ps1` | 校验或复制已构建的固定 OCR 运行目录，核对 SHA256、文件闭包与许可证；不联网、不安装软件、不改 PATH |
| `build.ps1` | `dotnet build -c Release`；若 `src/Desktop/CMakeLists.txt` 存在则再执行 CMake configure 与 build，将完整的 Collector 及已准备的固定 OCR 目录部署到 Desktop 同目录；未提供 OCR 时明确报告资产部署未执行 |
| `test.ps1` | 默认完整构建后运行全部 .NET / Qt / QML 测试，并**解析 TRX 报告真实用例数**；`-NoBuild` 复用已有产物 |
| `verify.ps1` | 环境自检 + 完整依赖安全审计 + 静态边界检查 + 架构依赖门禁 + 协议档案校验 + `tools/` 下全部 Python 自测（含检查器反向自测）+ 全部测试 + C++ cppcheck + 监听端口核对 + `LIVE_CAPTURE_STATUS` 断言 + 注入载荷扫描 + 许可证材料核对 + 构建目录已部署 OCR 时的离线 OCR 运行验证（提交前必须运行）；`-NoBuild` 复用已有构建产物，`-TestFilter` 转发 xunit 特征筛选，`-SkipGate` 显式跳过单个关卡；带其中任一参数的运行是部分验证（见 4.4） |
| `package.ps1` | 先运行 `verify.ps1`，再发布 Collector、部署 Qt 运行时、补齐许可证与 docs，生成 zip 与 SHA256；`-Verify` 额外解包并运行两个可执行文件，未带 `-Verify` 的产物不得公开分发（见 §5） |
| `static-analysis.ps1` | 对桌面端 C++ 逐编译单元运行 clang-tidy 与 cppcheck，按检查项汇总并将原始输出写入 `artifacts/static-analysis/`；只有编译错误、`clang-analyzer-*` 告警与 cppcheck 的 error 级结果令退出码非零（见 4.5） |

用法：

```powershell
pwsh -File scripts/bootstrap.ps1
pwsh -File scripts/build.ps1
pwsh -File scripts/verify.ps1
pwsh -File scripts/static-analysis.ps1
pwsh -File scripts/package.ps1 -Force -Verify
```

### 离线截图识别依赖

截图识别随应用部署 `ocr/local-ai-ocr.exe`、随包运行依赖及 `models/` 下的三份模型。
当前固定版本为 CPython 3.11.9、RapidOCR 3.10.0、ONNX Runtime 1.31.0，采用 CPU。
当前 helper 2 在整图检测后对符号区域限量复读，文字模型和职业分类器权重不变；完整源代码与冻结产物仍逐文件固定。
文字检测、识别和方向分类模型分别为 `PP-OCRv6_det_small.onnx`、`PP-OCRv6_rec_small.onnx`、
`ch_ppocr_mobile_v2.0_cls_mobile.onnx`；来源、SHA256、完整运行依赖及许可证登记在
[dependency-manifest.json](licenses/ocr/dependency-manifest.json)。模型必须提前放在本机，运行时禁止下载。
职业分类器由桌面端 C++ 执行，模型与训练来源另见
[job-icon-classifier.metadata.json](../src/Desktop/resources/models/job-icon-classifier.metadata.json)。
架构、旧新同图测试与本机资源测量见 [README 的 OCR 部分](../README.md#离线截图-ocr)。

维护者先准备 Windows x64 的 Python 3.11.9 与本地模型，使用固定的
[requirements-build.txt](../tools/LocalAiOcr/requirements-build.txt) 构建自包含运行目录，再校验并准备缓存：

```powershell
pwsh -File scripts/build-local-ai-ocr-worker.ps1 `
    -PythonExecutable C:/Tools/Python311/python.exe `
    -ModelsDirectory D:/OCR/models `
    -OutputDirectory D:/OCR/runtime `
    -InstallBuildDependencies

pwsh -File scripts/bootstrap-ocr.ps1 `
    -RuntimeDirectory D:/OCR/runtime
```

替换示例中的绝对路径；输出目录必须尚不存在，以保留已有产物。
`-InstallBuildDependencies` 是维护者显式选择的构建阶段 pip 安装；依赖已准备好时省略此开关。
发布 payload 必须与固定清单完全一致，构建环境或产物变化不会自动放宽哈希检查。
`requirements-build.txt` 固定的是已发布 payload 构建时使用的版本（其中 `setuptools` 为 65.5.0）。
`tools/package-verification/test_ocr_manifest.py` 核对该文件（换行统一为 LF 后）的 SHA-256 与依赖清单记录的
`requirements_sha256` 一致，并核对清单中每个来自 PyPI 的组件在该文件中固定为同一版本。因此升级其中任何依赖，
包括 Dependabot 提出的升级，都必须在同一改动中重新构建 OCR payload 并更新依赖清单，否则工具自测失败。
准备结果默认位于已忽略的 `artifacts/dependencies/ocr/<cache_id>/runtime`。
`MR_OCR_DIR` 可指定已有的完整离线运行目录；不带参数运行 `bootstrap-ocr.ps1` 可校验并试运行已准备的目录。
常规应用构建、打包、准备缓存和用户运行均不为 OCR 下载文件，不搜索系统 Python、Tesseract 或 PATH。
用户机器无需 Python、7-Zip 或 GPU 运行库。
未配置 `MR_OCR_DIR` 且没有缓存时，开发构建允许继续，截图识别显示依赖缺失，
表格导入可用；显式指定路径或已有缓存但内容不完整时构建失败。
`verify.ps1` 未发现已部署引擎时明确报告 OCR 运行验证未执行；提供引擎后会核对
哈希并在只含 Windows 系统目录的子进程 PATH 中实际识别中文测试图。
发布打包要求完整固定依赖及许可证，`package.ps1 -Verify` 对解包后的 OCR 再做相同验证。

### 4.1 `test.ps1` 解析 TRX 的原因

`test.ps1` 不单凭 `dotnet test` 的退出码判定结果：一次运行即使一个用例也没有执行，退出码也可能为 0，
这不能算作通过。该脚本枚举 `tests/` 下声明 `IsTestProject=true` 的项目（`MentorRecorder.sln`
同样包含这两个测试项目），逐个运行测试、写出 TRX、读取 `ResultSummary/Counters` 并与逐条用例的结果
相互核对，在下列任一情况下判定失败：

- 任一项目失败数 > 0；
- 任一项目用例总数或实际执行数为 0（空测试不视为成功）；
- 找不到测试项目，或没有生成 TRX；
- TRX 的汇总计数与逐条结果不一致，或其中出现未知结果、缺失或重复的执行标识；
- `dotnet test` 的退出码非零，而 TRX 中没有失败用例；
- 带 `-NoBuild` 运行时，`bin\x64\<配置>\` 下找不到该项目已构建的测试程序集；
- `ctest` 未注册任何用例、输出中读不到用例统计、退出码非零，或没有写出 JUnit 结果；
- `MentorRecorderIpcIntegration` 未注册，或其 QtTest 统计行缺失、通过数为 0、有失败或跳过（见 §3.2）。

跳过数按 `total - executed` 计算：VSTest 将跳过数记录在此处，而非 `notExecuted`。

每次运行的 TRX 与 ctest 的 JUnit 结果写入独立的 `artifacts/test-results/run-<UTC 时间>-<随机编号>/`，
不覆盖此前或并行运行的结果；.NET 用例统计之后打印本次的目录。

### 4.2 `-SettleSeconds`

`test.ps1` 在 .NET 阶段与 `ctest` 之间先等待所有 `MentorRecorder*` 进程退出，
再静置 `-SettleSeconds` 秒（默认 5）。原因是 .NET 套件以长稳测试收尾，
每秒推送上万条消息；而多个 Qt 用例断言的是重启退避与截图时序，
在负载尚未回落的机器上运行会因与桌面端无关的原因失败。
此处采用等待，而不是反复重试直至通过。

### 4.3 单实例租约与并发

Collector 的单实例租约**按用户**划分，而非按数据库划分。因此只要有另一个
`MentorRecorder.Collector.exe` 正以当前用户身份 `--serve`，无论它是开发者手动启动的、
上一次运行遗留的，还是同一台机器上另一个自动化会话启动的，进程级测试与
`verify.ps1` 的监听端口核对都无法运行。

`verify.ps1` 第 1 步会拒绝启动并列出占用进程；进程级测试与监听核对各自在设定时限内等待，超时
后放弃，因为租约被刚刚结束的进程短暂持有属于正常情况。清理命令：

```powershell
Get-Process MentorRecorder* | Stop-Process -Force
```

### 4.4 CI 执行的是同一个 `verify.ps1`

`.github/workflows/ci.yml` 不维护第二套构建命令，而是设置 `MR_QT_PREFIX`、
`MR_MINGW_BIN`、`MR_NINJA_EXE`、`MR_CMAKE_EXE`、`MR_CTEST_EXE` 指向 runner 上的
工具链，先单独校验 `protocol-profiles/` 下的全部档案，然后直接运行：

```powershell
pwsh -File scripts/verify.ps1 -Configuration Release -TestFilter 'Category!=Soak'
```

CI 因此执行 `verify.ps1` 的绝大部分关卡：完整 NuGet 解析图审计、静态边界检查与架构依赖门禁、
`tools/` 下的 Python 自测、桌面端与真实 Collector 之间的
`MentorRecorderIpcIntegration` 与 `MentorRecorderLifecycle`（`build.ps1` 会把
Collector 运行目录部署到测试二进制旁边）、TRX 真实计数与空测试保护、C++ cppcheck、监听端口核对、
`LIVE_CAPTURE_STATUS` 断言、注入载荷扫描与许可证材料核对。CI 不执行的部分见下列差异。

与本地发布验收相比有以下差异：

- 推送与 Pull Request 触发的运行带 `-TestFilter 'Category!=Soak'`，只排除长稳套件。
  该套件断言 5000 msg/s 的吞吐下限，共享 runner 没有稳定的吞吐基线，因此该关卡在本地发布验收中运行
  （见 [release-checklist.md](release-checklist.md)）；在 Actions 页面手动触发并勾选
  「Run the Soak suite」时，`verify.ps1` 不带任何筛选运行。带筛选的运行是部分验证，
  结论中不评定 `PUBLIC_DISTRIBUTION_READY`。
- CI 不准备离线 OCR 运行目录（没有设置 `MR_OCR_DIR`，检出中也没有 `artifacts/dependencies/ocr/` 缓存），
  `build.ps1` 因此不部署 OCR，`verify.ps1` 将 `ocr-runtime` 关卡记为跳过。CI 的每一次运行，包括勾选
  「Run the Soak suite」的手动运行，因此都是部分验证，不评定 `PUBLIC_DISTRIBUTION_READY`；
  截图识别依赖的运行验证只在本地完成（见「离线截图识别依赖」）。
- **未**传入 `-SkipGate`。CI 上没有任何关卡需要 Npcap 或游戏客户端：抓包相关的
  断言核对的是 `src/Collector/Capture/CaptureDiagnostics.cs` 中的编译期常量
  （`LiveCaptureStatus` / `MonitorType` / `InjectedHookEnabled`），
  而 `--capture-doctor` 返回 1（表示该机器当前无法启动抓包）本身就是合法结果。
- CI **不**运行 `package.ps1`（打包、版本与 CHANGELOG 一致性核对、`-Verify` 解包运行、安装器），
  这一步只在本地发布验收中执行。
- CI 安装固定版本的 cppcheck 2.21.0（核对安装包的大小、SHA-256 与版本号），`verify.ps1` 只以
  `static-analysis.ps1 -Tool cppcheck` 调用它；clang-tidy 不在 CI 中运行，需在本地单独运行
  `static-analysis.ps1` 并记录结果。
- CI 不执行 `security-quality.ps1` 的 `PrePush`、`PostPush`、`Merge` 与 `Release` 阶段（远端读回）。

`-SkipGate` 接受 `tool-selftests`、`listener-check`、`live-capture-status`、
`injection-payload`。被跳过的关卡会在跳过时与结论中各提示一次，不会静默通过。

`PUBLIC_DISTRIBUTION_READY` 是对一次完整运行的判断。带 `-SkipGate`、`-TestFilter` 或 `-NoBuild`
的运行，以及构建目录中未部署 OCR、`ocr-runtime` 关卡因此被记为跳过的运行，即使全部通过、退出码为 0，
结论中也只说明这是一次部分验证，不打印 `PUBLIC_DISTRIBUTION_READY = true`；`-NoBuild` 复用磁盘上
已有的构建产物，它们可能早于正在验证的源码。
这类运行不能作为发布验收。

另有一个 `markdown` job，使用固定版本的 `markdownlint-cli@0.49.1` 检查 `docs/`、`README.md`、
`CHANGELOG.md`、`CONTRIBUTING.md`、`SECURITY.md`；该版本号写在工作流中，Dependabot 不跟踪，需手动升级。
规则见 `.markdownlint.json`，
排除项见 `.markdownlintignore`；其中 `docs/reviews/`、`docs/plans/`、`openspec/`
是不入库的本地工作材料，本地检出中仍可能存在。

另有独立的 `security-quality` job，运行安全门禁检查器的正反例。普通 PR 的
`verify.ps1` 只执行本地依赖审计，不要求 PR 令牌访问特权安全接口。
推送、合入和发布的远端读回规则见 [安全与质量门禁](security-quality-gates.md)。

### 4.5 `static-analysis.ps1`：clang-tidy 与 cppcheck

输入是 `build/compile_commands.json`（`MR_BUILD_DIR` 与 `build.ps1` 同义），因此需要先
完成一次构建再运行。分析范围是 `src/Desktop/cpp` 与 `tests/Desktop.Tests` 下的每个
`.cpp`；C# 侧由编译器的 `TreatWarningsAsErrors` 与 `tools/static-boundary-check`
覆盖，不在这里。

`build.ps1` 显式导出编译数据库。默认每次分析写入独立的
`artifacts/static-analysis/run-<id>/`，避免覆盖并行会话的日志；每个工具的整个进程树
由 Windows Job Object 管理，受 `-ToolTimeoutSeconds` 预算约束。cppcheck XML 缺少必需
结构或分析未完成时按失败处理，不能解释为零告警。

`verify.ps1` 以 `-Tool cppcheck` 调用本脚本（`-Jobs` 取 2 与处理器数中的较小者）。脚本无法运行（例如找不到
`compile_commands.json`）或退出码非零时，`verify.ps1` 将其记为 `cpp-static-analysis` 关卡失败，
其后的关卡与结论照常执行。

```powershell
pwsh -File scripts/static-analysis.ps1                    # 两个工具，全部文件
pwsh -File scripts/static-analysis.ps1 -Tool cppcheck
pwsh -File scripts/static-analysis.ps1 -Path IpcFraming   # 只看路径含该片段的文件
pwsh -File scripts/static-analysis.ps1 -Strict            # 任何告警都算失败
```

工具位置沿用 `build.ps1` 的约定：`MR_CLANG_TIDY_EXE`（默认取 Qt 安装里的
llvm-mingw：`D:/APPS/Qt/Tools/llvm-mingw1706_64/bin/clang-tidy.exe`）与
`MR_CPPCHECK_EXE`（默认 `C:/Program Files/Cppcheck/cppcheck.exe`）。

检查项在仓库根的 `.clang-tidy`，每个关掉的检查旁边写着为什么；cppcheck 的抑制项
在脚本里，同样逐条注明。退出码只由“硬”结果决定：clang-tidy 的编译错误或
`clang-analyzer-*` 告警、cppcheck 的 error 级结果。`bugprone-narrowing-conversions`、
`performance-*` 一类风格告警照常列出，但不阻塞；`-Strict` 让它们也阻塞。

脚本已规避下列三个问题，变更工具链时需要确认它们是否仍然存在：

- llvm-mingw 的 clang-tidy 默认找 libc++，而 `compile_commands.json` 来自 MinGW g++，
  标准库头文件只在 GCC 目录里，因此脚本加 `--extra-arg=-stdlib=libstdc++`。
- cppcheck 用 ANSI 文件 API，带非 ASCII 字符的绝对路径打不开；脚本以仓库根为工作目录
  只传相对路径，编译宏写进一个强制包含的头文件（`artifacts/static-analysis/cppcheck/
  compile-defines.h`）而不是命令行。
- cppcheck 不解析 Qt 头文件（太慢，`--library=qt` 已描述其语义），因此
  `Q_OS_WIN` 这类由 Qt 头文件从编译器宏推导出来的宏要显式定义，否则
  `#ifdef Q_OS_WIN` 分支会被当作死代码而报出恒真/恒假条件。

clang-tidy 会把落在 Qt 头文件里的分析器路径告警（`QPointer` / `QSharedPointer`
的引用计数在分析器看来像 use-after-free）一并报出，`HeaderFilterRegex` 管不到它们；
脚本按告警位置把这类结果单独计数并忽略，明细仍在 `findings.csv` 里。

## 5. 打包

`scripts/package.ps1` 生成一个用于**本地验证与联调**的发布目录及同名 zip：

```
MentorRecorder/
  MentorRecorder.Desktop.exe        Qt 应用
  MentorRecorder.Collector.exe      .NET 应用
  MentorRecorder.Collector.dll      Collector 主程序集与其依赖
  Qt6*.dll, platforms\, qml\ …      windeployqt 产出
  libgcc_s_seh-1.dll 等             MinGW 运行时
  LICENSE                           GPL-3.0 全文
  README.md
  BUILD-METADATA.json                构建版本、源码 revision、dirty 状态与验收边界
  SOURCE_CODE.md                    完整对应源码的去处（含 BUILD-METADATA.json 里的 source_commit）
  SHA256SUMS.txt
  THIRD_PARTY_NOTICES.md
  docs\                             用户可读的说明
```

运行：

```powershell
pwsh -File scripts/package.ps1
pwsh -File scripts/package.ps1 -Configuration Debug -OutputDir out\pkg
pwsh -File scripts/package.ps1 -Force           # 仅替换脚本自己的同名目录、zip、zip.sha256 与同版本的安装器及其 .sha256
pwsh -File scripts/package.ps1 -Force -Verify   # 再解包运行一次，证明运行时依赖完整
```

默认行为：

1. 先读取一次源码状态（提交号、工作区是否有未提交改动、git 能否回答），再执行
   `scripts/verify.ps1 -Configuration <...>`。
2. 再执行 `scripts/build.ps1` 补齐构建产物，并对 `build\src\Desktop\MentorRecorder.Desktop.exe` 执行
   `Assert-DesktopExecutable`（见 §5.1）；随后执行
   `dotnet publish src/Collector/MentorRecorder.Collector.csproj -r win-x64 --self-contained true`。
3. 复用 `build\src\Desktop\MentorRecorder.Desktop.exe`，对其运行 `windeployqt --release --compiler-runtime --no-translations --no-ffmpeg --exclude-plugins ffmpegmediaplugin`
   （在线语音只播放 WAV，只需要 Windows 多媒体后端，见 [third-party-licenses.md](third-party-licenses.md) §3）。
4. 若 `windeployqt` 未补齐 MinGW 运行时，则显式复制 `libgcc_s_seh-1.dll`、`libstdc++-6.dll`、`libwinpthread-1.dll`。
5. 显式补入 `platforms\qoffscreen.dll`，保证发布包中的 `--screenshot` 离屏验收入口可运行。
6. 输出 `MentorRecorder-<version>-win-x64\`（Debug 配置为 `MentorRecorder-<version>-debug-win-x64\`）、同名 `.zip` 与 `.zip.sha256`。默认拒绝覆盖，只有显式指定 `-Force` 才替换同名产物。同版本的 `MentorRecorder-<version>-setup.exe` 与其 `.sha256` 同样算作本次运行的产物：即使本次不生成安装器（`-NoInstaller`，或本机没有 Inno Setup），它们存在时也要求 `-Force`，并在打包开始时被删除，以免上一次的安装器留在新 zip 旁边、被当作它的安装器。目录名包含版本号是有意设计：早期命名不含版本，未加 `-Force` 的重新打包会让上一个版本的目录原样留在原地。`<version>` 是完整版本号，先行版因此得到 `MentorRecorder-1.4.0-beta.1-win-x64\` 与 `MentorRecorder-1.4.0-beta.1-setup.exe`，与正式版的产物不会重名。
   先行版的 `public_distribution_ready` 恒为 `false`，`public_distribution_blockers` 中写明「版本 … 是先行版（测试包），按定义不作为正式发布分发」；打包本身照常成功，因为产出测试包正是此时的目的。
7. synthetic profile 保留在开发与测试构建目录中，不进入发布包；发布包只安装真实区域目录中的档案与 fail-closed 占位。
8. 对暂存目录执行三组断言：`Assert-NoForbiddenPayload`（禁止内容）、`Assert-MultimediaLayout`
   （`multimedia\` 只有 `windowsmediaplugin.dll`，任何位置都没有 FFmpeg）与 `Assert-RequiredContent`（必需文件）。

### 5.1 内容断言

**必须包含**：`MentorRecorder.Collector.exe`、`MentorRecorder.Desktop.exe`、`Qt6Multimedia.dll`、
`multimedia\windowsmediaplugin.dll`、`LICENSE`、
`THIRD_PARTY_NOTICES.md`、`README.md`、`SOURCE_CODE.md`、`BUILD-METADATA.json`、
`SHA256SUMS.txt`、`docs\`（含 `privacy-boundary.md`、`third-party-licenses.md`、
`release-checklist.md`）。

**必须不包含**：`deucalion*`、`oo2net*.dll`、`wpcap.dll` / `Packet.dll` / `*npcap*`、
`ffxiv*.exe`、`*.pcap` / `*.pcapng`、`*.db` / `*.db-wal` / `*.db-shm` / `*.sqlite*`、
`*.log`、`*.trx`、**`*.pdb`**、FFmpeg（所有文件名含 `ffmpeg` 的文件，含 OpenCV 可选视频 DLL；`avcodec-*.dll`、`avformat-*.dll`、
`avutil-*.dll`、`avdevice-*.dll`、`avfilter-*.dll`、`swresample-*.dll`、`swscale-*.dll`、`postproc-*.dll`）、
以及整个 `protocol-profiles/synthetic/`。

数据库与日志被明确列入禁止内容，原因是它们并非多余文件，而是**其他用户的游玩记录**
与**其他机器的诊断数据**。发布包中出现其中任何一项都属于隐私事故，而非打包瑕疵。

`BUILD-METADATA.json` 除随包分发外，还必须作为**独立的发布资产**上传到 GitHub 的发布页
（0.9.1 已经如此）：更新检查读取的地址是 `releases/latest/download/BUILD-METADATA.json`
（[privacy-boundary.md](privacy-boundary.md) §8.4）。该资产缺失时，检查只会得到"未找到"并静默降级，
用户不会收到任何新版本提示。

安装程序 `MentorRecorder-<版本>-setup.exe` 与其校验和 `MentorRecorder-<版本>-setup.exe.sha256`（§5.3 生成，内容为
64 位十六进制 SHA-256、两个空格与文件名）同样必须作为发布资产上传到标签为 `v<版本>` 的发布页：
「下载安装包」先读取 `.sha256`，读不到或内容不是校验值时不下载安装程序；下载到的安装程序与之不一致时被删除
（[privacy-boundary.md](privacy-boundary.md) §8.6）。浏览器下载使用的是同一个安装程序地址。

另有一条按**内容**而非文件名判定的断言 `Assert-NoLocalPathLeak`：以 UTF-8 与 UTF-16LE
两种编码扫描产物中是否出现仓库根路径，命中即失败；`.md`、`.txt`、`.json` 不在扫描范围内，
因为文档中出现源码树路径属于正常情况。该断言在打包阶段与 `-Verify` 解包后各执行一次。

`Assert-DesktopExecutable` 直接从 PE 资源目录读出桌面端可执行文件内嵌的应用程序清单
（`RT_MANIFEST` 1；源文件为 `src/Desktop/resources/app/MentorRecorder.Desktop.manifest`，由 `app.rc.in`
编入），要求 `requestedExecutionLevel` 为 `asInvoker`、声明 `longPathAware = true`，并声明支持
Windows 10/11；没有内嵌清单或任一项不符即打包失败。可执行文件中出现以 NUL 结尾的窄字符串
`MR_COLLECTOR_PATH` 时（开发构建传给环境变量读取函数的完整名称）同样失败；内嵌更新记录和翻译
资源中的普通文字引用不作为开发代码。该断言在暂存之前与 `-Verify`
解包后各执行一次。清单不再以可执行文件旁的单独文件分发，构建目录中残留的旧外部清单
`MentorRecorder.Desktop.exe.manifest` 不进入发布包。

#### 调试符号：内嵌并重写路径，不分发 `.pdb`

`Directory.Build.props` 对 **Release** 配置设置了两项：

```xml
<PathMap Condition="'$(Configuration)' == 'Release'">$(MSBuildThisFileDirectory)=/_/</PathMap>
<DebugType Condition="'$(Configuration)' == 'Release'">embedded</DebugType>
```

- `DebugType=embedded` 将符号写入程序集本身，发布目录中因此**不含任何 `.pdb`**，
  崩溃堆栈仍带有文件名与行号；
- `PathMap` 将编译器记录的每一条源码路径中的仓库根重写为 `/_/`，其中包括写入 PE 调试目录的
  那条 pdb 路径。

这样处理的原因是：可移植 PDB 按**路径段**存储源码路径。0.2.3 的发布包中包含
`MentorRecorder.Collector.pdb`，其中逐段保存了维护者本机的绝对路径，包括盘符、用户目录
与仓库名。对于一个截取游戏流量、并按 GPL 承诺提供源码的项目，这些信息可能暴露维护者的身份。
**Debug 构建不受影响**，仍生成并列的 `.pdb` 并保留真实路径，本机调试不受限制。

### 5.2 `-Verify`：从解包目录实际运行一次

`-Verify` 将 zip 解压到临时目录，然后**从解包目录**运行两个可执行文件。
该步骤不使用工作区中的任何内容，因此仅在开发机上存在的依赖（PATH 上的 Qt、
构建树中遗留的文件）会在此处暴露，便于在分发前发现依赖遗漏。

该步骤依次断言：

1. 解包目录同样通过 `Assert-NoForbiddenPayload`、`Assert-NoLocalPathLeak`、`Assert-MultimediaLayout`、
   `Assert-RequiredContent`、`Assert-StagedVersion` 与 `Assert-ReleasedChangelogSectionsUnchanged`，
   解包出的桌面端可执行文件再通过一次 `Assert-DesktopExecutable`；
2. `MentorRecorder.Collector.exe --version` 退出码 0，且输出是版本横幅；
3. `MentorRecorder.Collector.exe --capture-doctor --json` 退出码为 0 或 1。
   **在没有 Npcap、没有游戏的机器上，退出码 1 是正确结果**，说明降级路径可用。
   该命令报出的 `live_capture_status` 必须为 `VERIFIED_POP_TO_EXIT` 且与 `BUILD-METADATA.json`
   中记录的一致，并且 `boundary.monitor_type = WinPCap`、`boundary.injected_hook_enabled = false`。
   这些状态值都是二进制中的**编译期常量**，因此本步证明的是元数据与解包出来的可执行
   文件同源，而不是对这份产物测试过实时抓包。
   `packaged_verified_profile_status` 同样按解包产物自身的 `--list-profiles --json` 复核。
   元数据若声称 `public_distribution_ready = true`，则下列任一情况即失败：工作区脏、元数据没有记录
   `source_commit`、此时 git 无法读出工作区状态、打包之后工作区又出现未提交改动、包内无 VERIFIED 档案、
   `live_capture_status` 不符（判据见 [release-checklist.md](release-checklist.md) §7）；
4. `MentorRecorder.Desktop.exe --screenshot`（`QT_QPA_PLATFORM=offscreen`）
   退出码为 0，且产出一张大于 4 KiB 的 PNG。该步骤同时证明 Qt 运行时、QML 模块
   与 offscreen 平台插件齐备；截图期间出现 QML / JavaScript 运行时警告时退出码为 10，本步随之失败；
5. `MentorRecorder.Desktop.exe --speech-selftest <静音 WAV>`（隐藏开关）退出码为 0。
   该开关走在线语音相同的 `QSoundEffect` 路径，仅依赖包内的 Qt 播放一段 0.3 秒静音，
   用于证明多媒体运行时齐备。退出码 6 表示该机器没有音频输出设备，与产物无关，同样接受；
6. 上述步骤结束后**再次检查**禁止内容，确认运行过程本身没有向产物中写入数据库或日志。

验收清单见 [release-checklist.md](release-checklist.md)。

打包时必须满足（GPLv3 与本项目边界的共同要求）：

1. 随二进制提供 `LICENSE`（GPL-3.0 全文）与 `THIRD_PARTY_NOTICES.md`。
2. 提供或明确指向**完整对应源码**（GPLv3 第 6 条）。
3. **不打包 Npcap**（其免费版禁止外部再分发，见 [third-party-licenses.md](third-party-licenses.md)）。
4. **不打包** `oo2net_9_win64.dll` 或任何游戏文件。
5. **不打包** `deucalion-*.dll`（构建阶段已剔除；打包脚本需再做一次断言）。
6. 默认先运行 `verify.ps1`，静态边界检查必须通过；只有显式指定 `-SkipVerify` 才允许跳过，此时产物仅适用于本地联调。

`PUBLIC_DISTRIBUTION_READY` **不是固定字面量**，而是 `package.ps1` 按一组前提计算并
写入 `BUILD-METADATA.json` 的实测值。前提不满足时，`public_distribution_blockers` 会逐条
列出原因。前提与核对方式见 [release-checklist.md](release-checklist.md) §7。
其中源码状态由 `Get-SourceTreeState` 读取：git 必须能给出源码提交号并读出工作区状态。
找不到 git、目录不是仓库、git 因所有权不符（dubious ownership）拒绝该仓库，或任一 git 命令失败时，
`source_commit` 与 `source_worktree_dirty` 记为 `null`，这种情况不会被当作工作区干净，
而是作为一条阻断原因写入 `public_distribution_blockers`。
写入元数据时再读一次源码状态，并与「默认行为」第 1 步、即运行 `verify.ps1` 与 `build.ps1` 之前读到的状态比较：
提交号、是否有未提交改动或 git 能否回答，任一项不同即打包失败（「源码在打包过程中发生了变化…」），
因为产物中的程序已无法确定来自哪一份源码。
`BUILD-METADATA.json` 另以 `unpack_verified` 记录本次是否带 `-Verify` 运行，`unpack_verified_source` 说明
解包运行在写出元数据之后进行、失败时整次打包失败。解包验证失败时，此前写出、记为 `unpack_verified = true` 的压缩包、
它的 `.sha256` 与暂存目录中的 `BUILD-METADATA.json` 一并删除（未能删除的在错误信息中列出，须手动删除、不得分发），
暂存目录的其余内容保留以便排查。未带 `-Verify` 时 `unpack_verified` 为 `false`，
`public_distribution_blockers` 写明「未按 -Verify 解包并运行产物（unpack_verified=false）」，产物不得公开分发。
许可证一侧的前置条件（[third-party-licenses.md](third-party-licenses.md) §7）已全部落实：
`docs/licenses/` 下的 LGPL 与 GCC 运行时例外文本随 `docs/` 一并进入发布包。

### 5.3 安装器

`package.ps1` 在生成 zip 之后，若找到 Inno Setup（`ISCC.exe`，在 `Inno Setup 6` 安装目录下查找，
`winget install JRSoftware.InnoSetup`），会按 [`installer/MentorRecorder.iss`](../installer/MentorRecorder.iss) 生成
`artifacts/MentorRecorder-<版本>-setup.exe` 与 `.sha256`。编译需要 Inno Setup 6.7 或更新版本：
`.iss` 显式开启的 `RedirectionGuard` 从 6.7.0 起才受支持，更旧的编译器由预处理器 `#error` 直接拒绝。
`-NoInstaller` 跳过这一步；同版本的旧安装器与校验和仍会在打包开始时被删除（见 §5「默认行为」第 6 条）。

- Collector 以 **self-contained** 方式发布，安装器因此自带 .NET 运行时；Qt 与 MinGW 运行时由
  `windeployqt` 与脚本补齐。用户无需另行安装任何运行库。
- 默认安装目录：只有当 **D: 是本地固定磁盘**（`GetDriveType == DRIVE_FIXED`）**且可用空间
  不少于 512 MB** 时才默认 `D:\MentorRecorder`，否则回落到 `Program Files\MentorRecorder`。
  光驱、读卡器、U 盘、映射的网络盘以及空间不足的磁盘一律回落，因为 `DirExists('D:\')` 对它们
  全部为真，据此安装会中途失败，或者把软件装入随时可能被移除的介质。取不到驱动器类型时按
  `DRIVE_UNKNOWN` 处理，走 Program Files 分支，以免安装器在目录页崩溃。目录页始终显示，用户仍可修改。
  目录页拒绝三类位置并要求另选：整个磁盘的根目录（如 `D:\`）；本身是链接（联接点或符号链接）的文件夹；
  以及已有其他文件、却不是本软件此前安装位置的非空文件夹。此前的安装位置须同时满足两项：本软件的卸载项
  （`HKLM` 下以本软件 AppId 命名，只有管理员能够写入）中的 `Inno Setup: App Path` 指向该文件夹，且其中存在
  `MentorRecorder.Desktop.exe` 或 `MentorRecorder.Collector.exe`。仅有同名的可执行文件不算，解压出的便携副本
  或登记在别处的副本因此会被拒绝。卸载后只剩 `note-images` 的文件夹同样可以直接安装，`note-images` 与其他
  文件并存则不算安装痕迹。这项检查只为避免误选，不是安全边界：下一条的保护不依赖它，磁盘根目录与链接在
  `ssInstall` 还会再检查一次，未知的子文件夹也在那时检查（见下一条），静默安装同样适用。
- **安装文件夹的访问权限。** 数据盘（如 `D:\`）上的文件夹沿用盘符根目录的权限，常见情况下本机任何用户
  都可以修改其中的程序与卸载程序。安装与升级在写入任何文件之前（`CurStepChanged(ssInstall)` 中的
  `ProtectInstallDirectory`）保护 `{app}`：先以 `System32\icacls.exe /setowner` 把文件夹的所有者设为
  Administrators，再通过 `SetFileSecurityW` 将其 ACL 替换为受保护的访问控制列表——Administrators 与 SYSTEM
  完全控制、Users 读取和执行，可向下继承。这两步都只改动文件夹自身，不会把权限传播给其下已有的文件和
  文件夹：硬链接与它所链接的文件共用同一份安全描述符，传播会连带改动安装文件夹之外的那个文件。此后新建的
  文件和文件夹在创建时继承这份 ACL。账户按众所周知的 SID 指定，不依赖本地化的账户名；`/L` 使 icacls
  只作用于链接本身，icacls 以隐藏窗口运行，全程不使用递归（`/T`）。
  ACL 生效之后，安装程序再次确认 `{app}` 仍是真实的文件夹而不是链接，随后检查其中的子文件夹。Qt 会从
  应用程序所在文件夹中的子文件夹加载插件与 QML 模块，而事先建立的文件夹保留着创建者赋予的所有者与权限，
  因此只要有一个文件夹（含指向文件夹的链接）既不是 `note-images`、也不在 `[InstallDelete]` 整个清除之列，
  安装即停止并指出该文件夹；安装程序不删除它，也不改动 `note-images` 与 `{app}` 中的文件，由用户删除或移走
  该文件夹后重新运行安装程序。通过这项检查之后，安装程序逐个处理此前已直接位于 `{app}` 中的文件：
  所有者改为 Administrators，并清除其显式授权，使其只继承上述 ACL——安装之前由其他账户放入的
  文件因此不能保留原属主或自带的授权。随后即被 `[InstallDelete]` 删除的 `*.dll` 与 `qt.conf` 跳过。
  处理每个文件之前先读取其硬链接数：文件是重解析点、另有硬链接，或链接数无法读取时，安装停止并指出该文件，
  不删除也不改动任何内容。允许留下的子文件夹分两类：代码与资源目录不在 ssInstall 删除，而是由 `[InstallDelete]`
  首条目的 `BeforeInstall`（`ClearCodeDirectories`）在该段落执行之前整个删除（遇到链接只删除链接本身，不进入链接
  所指的位置），再由 `[Files]` 重新建立，随之继承新的 ACL。放在这里是因为安装程序按用户在准备页的选择关闭仍在运行
  的本软件（Restart Manager）发生在安装过程开始时——在 ssInstall 之后、`[InstallDelete]` 之前（Inno Setup 源码
  `PerformInstall`）——而界面程序正占用着这些文件夹里的 Qt 插件：1.8.0-beta.1 在 ssInstall 清理，早于关闭，
  本软件运行时升级必定停在 `imageformats`。用户在准备页选择不关闭本软件，或自动关闭失败后选择忽略时，本软件仍在
  运行，这一步同样删不掉它占用的插件文件夹，安装停止并指出该文件夹，提示关闭本软件后重新运行安装程序；安装程序
  不会自行结束本软件。`[InstallDelete]` 删除失败时并不报错，因此这一步删除之后仍然存在的文件夹——例如其他账户事先
  建立、设置为管理员无法删除的同名文件夹——同样使安装停止并指出该文件夹，由用户删除后重新运行安装程序；此时已删除的
  其他代码与资源目录在重新安装时恢复。在这个位置停止要走安装程序自己的取消路径（`BeforeInstall` 过程抛出的异常会被
  Inno Setup 吞掉）：`WizardForm.Close` 触发取消，`CancelButtonClick` 免去“退出安装程序吗”的询问，安装程序在
  `[InstallDelete]` 之后、`[Files]` 之前中止，没有写入任何文件；`/VERYSILENT` 或 `/NOCANCEL` 下取消路径不可用时，
  `[Files]` 条目的 `Check`（`CodeFoldersCleared`）抛出异常，同样在写入任何文件之前中止。`note-images` 见下。
  `note-images` 是唯一允许普通用户写入的子文件夹。它不存在时由安装程序创建；它若是指向别处的链接，
  安装停止并提示删除该链接，以免写入权限落到链接所指的位置。它的所有者同样设为 Administrators，ACL 同样以
  不向已有文件传播的方式替换为受保护的访问控制列表——Administrators 与 SYSTEM 完全控制、Users 修改，可向下继承；其中已有的
  备注图片保持原有的权限不变。`[Dirs]` 中的 `note-images` 条目只保留 `uninsneveruninstall`，不再经由
  `Permissions` 授权。
  上述任一步失败（例如该位置不是本机 NTFS 磁盘），或 `{app}` 是链接或磁盘根目录，安装即停止并说明原因。
  两个文件夹最终的 ACL 会以 `icacls` 列表的形式写入 Setup 日志。`.iss` 另以 `RedirectionGuard=yes` 在
  Windows 11 与 Windows 10 22H2 上阻止安装与卸载程序跟随非提升进程创建的链接；更早的系统靠上述自行检查。
- **`[InstallDelete]` 在复制文件之前清除旧内容。** 各代码与资源子文件夹此时已由上一条的删除步骤删除，
  `[InstallDelete]` 仍列出它们，`package.ps1` 据此核对发布的每个子文件夹都在清除之列。此外还删除直接位于 `{app}` 的
  全部 `*.dll` 与 `qt.conf`：安装包不附带 `qt.conf`，自带的 DLL 随后照常写回，安装之前被放入该文件夹的
  DLL 或 `qt.conf` 因此不会被程序加载。可执行文件不在此列（卸载程序 `unins*.exe` 由 Setup 在每次升级时
  重写）。从 1.5.0 及更早版本升级时，另删除已不再使用的外部应用程序清单
  `{app}\MentorRecorder.Desktop.exe.manifest`（清单已内嵌在程序中）。
- **这项保护的范围。** 它针对的是安装文件夹自身，以下情形不在其内，与不受信任的用户共用的电脑应安装到
  `Program Files`：上级目录若允许普通用户删除或重命名其中的条目（例如对 Users 授予完全控制的数据盘根目录），
  其他本机用户仍可在软件未运行时把整个安装文件夹改名并换成自己的文件夹；`[InstallDelete]` 的删除失败时
  Setup 不报错：被其他进程占用而删不掉的外来 DLL 会留在原处（代码与资源目录不在此列：删不掉时安装停止，
  见上）；从文件夹可被任何用户写入的 1.5.0 升级时，
  原有的卸载日志 `unins000.dat` 会被沿用一次。
- **最低要求在 `InitializeSetup`（`[Code]`）中检查**：低于 Windows 10 版本 1809（内部版本 17763，Qt 6.11 的下限；
  .NET 8 只需 1607）直接拒绝并说明所需版本；ARM 处理器上的 Windows 10 拒绝安装，因为没有 x64 模拟；
  Windows 11 on ARM 提示抓包未验证后继续；`System32\mfplat.dll` 不存在（Windows “N” 版本）时提示
  语音播报需要媒体功能包后继续。`MinVersion=10.0` 仍然保留，作为 Inno Setup 自身的粗筛。
- **Npcap 不随安装器分发。** 安装器检测不到 Npcap 时，从 `https://npcap.com/dist/npcap-<版本>.exe`
  下载官方安装程序（版本与 SHA-256 固定在 `.iss` 中），校验通过后启动它。Npcap 免费版没有静默模式，
  用户应在其向导中保持默认选项，其中包含 WinPcap API-compatible Mode。下载失败或用户取消时，
  本软件照常安装，界面提示 Npcap 缺失。升级 Npcap 版本时需同时更新 `NpcapVersion` 与 `NpcapSha256`。
- 卸载时询问是否删除 `%LOCALAPPDATA%\MentorRecorder`（记录、备份、设置），默认保留。
  提示同时写明备注图片不在其中：它们保存在安装文件夹的 `note-images` 中，无论选择哪一项都会保留
  （`uninsneveruninstall`），不再需要时可在卸载完成后手动删除。
- 中文界面来自 `installer/ChineseSimplified.isl`（Inno Setup 官方仓库的用户贡献翻译）。

### 5.4 GitCode 镜像

本仓库及其发布页同步到 GitCode 上的同名镜像 `https://gitcode.com/Harendotes-Tang/MentorRouletteRecorder`；
更新检查与软件内下载在 GitHub 不可达时读取该镜像（见 [privacy-boundary.md](privacy-boundary.md) §8.4、§8.6）。
校准数据仓库 `MentorRecorder-Calibrations` 另有 GitCode 镜像（§8.2），其分支清理同样由本仓库的工作流执行。
同步由下列三个工作流完成，脚本位于 `tools/release-mirror/`。三个工作流都只在仓库
`Harendotes-Tang/MentorRouletteRecorder` 中运行，权限为 `contents: read`，并且总是检出 `main`，运行的是
`main` 上的脚本与批准计划：`dev` 上对它们的修改在快进合并到 `main` 之后才生效。每个工作流先运行相应脚本的
自测，自测通过后才访问镜像。

| 工作流 | 触发条件 | 作用 |
|---|---|---|
| `mirror-repository.yml` | 任一分支或标签的推送；手动触发；每小时一次 | `sync_refs.py` 将 GitHub 的全部分支与标签推送到镜像 |
| `mirror-release.yml` | GitHub 发布页的发布或编辑（草稿除外）；`main` 上该工作流或 `tools/release-mirror/` 的改动；手动触发，可填写标签；每 6 小时一次 | 先以 `sync_refs.py` 同步引用，再以 `sync_release.py` 复制发布 |
| `cleanup-mirror-branches.yml` | `main` 上该工作流、`cleanup_refs.py`、其测试、`cleanup-plan.json` 或 `sync_refs.py` 的改动；GitHub 上删除分支；每 6 小时一次 | `cleanup_refs.py` 按批准计划删除镜像上的分支 |

**令牌。** 访问 GitCode 的令牌只来自仓库的 Actions secret `GITCODE_MIRROR_TOKEN`，它需要两个镜像仓库
（本仓库与 `MentorRecorder-Calibrations`）的推送权限。未配置时，引用同步与发布复制以
「Configure the repository Actions secret GITCODE_MIRROR_TOKEN first」失败，分支清理在删除步骤以
「Configure GITCODE_MIRROR_TOKEN before execution」失败。令牌只经由 Git 子进程的环境或发往 GitCode API 的
请求头传递，不出现在命令行参数与日志中，也不进入软件；客户端只使用匿名读取接口。

**引用同步（`sync_refs.py`）。** 取得 GitHub 的全部分支与标签（不含 Pull Request 引用），确认取得期间 GitHub
没有变化，再以精确 lease 原子推送有变化的引用，推送后回读核对。镜像上独有的引用一律保留，不被删除。
该脚本可以重复执行。

**发布复制（`sync_release.py`）。** 由发布事件或填写了标签的手动运行触发时，复制该标签的发布；其他触发方式
补齐 GitHub 上的最新正式版，以及最近 20 个发布中最晚发布的测试包，并核对已撤回的正式版（见下）。

- 只复制已发布（非草稿）的发布，且镜像上的同名标签必须已指向与 GitHub 相同的提交；该脚本从不创建或改动标签。
- 正式版复制安装程序、其 `.sha256` 与 `BUILD-METADATA.json`；测试包复制安装程序与 zip 及二者的 `.sha256`。
  附件从 GitHub 下载后核对大小、GitHub 记录的 SHA-256 与 `.sha256` 文件；正式版的 `BUILD-METADATA.json`
  还必须记录同一版本号、`prerelease = false`、与标签相同的 `source_commit`、`source_worktree_dirty = false`
  与 `public_distribution_ready = true`，否则不复制。
- 镜像上尚无该发布时，以 GitHub 的标题与说明新建：测试包标为预发布；正式版只有在它是 GitHub 当前的最新
  正式版时才标为最新版本。
- 镜像上已有该发布时，先核对标签与类别（正式版或测试包），并逐个以匿名下载核对已有附件与 GitHub 的原文件
  一致；全部一致后，GitHub 上被编辑过的标题或说明随之更新到镜像，再补传缺少的附件。附件内容冲突、类别不符，
  或正式版已有 `BUILD-METADATA.json` 却缺少其他附件时，运行失败，不替换也不删除任何附件。
- 每个附件上传后都以匿名下载核对 SHA-256，正式版的 `BUILD-METADATA.json` 最后上传；运行结束前再读回一次，
  核对标题、说明与附件是否齐全。

**已撤回的正式版。** 补齐运行还读取 GitCode 发布清单的第一页（最多 20 个，与采集服务的更新后备读取同一清单），
并就其中每个正式版向 GitHub 查询同一标签。GitHub 答复该发布不存在（HTTP 404）时，运行失败，并逐个写明
「<标签>: deleted on GitHub but still published on GitCode; delete the GitCode release by hand」。
脚本从不删除镜像上的发布：维护者确认该版本确应撤回后，在 GitCode 上手动删除该发布，此后不再报告。

**分支清理（`cleanup_refs.py`）。** 引用同步不删除镜像上的分支，需要删除的分支逐条写入
`tools/release-mirror/cleanup-plan.json`。该计划是清理的唯一依据，命令行不能另行指定计划、分支、提交或目标。
每条记录写明仓库、分支（只接受 `refs/heads/` 下的名称，`main` 与 `dev` 不能列入）、镜像上该分支应有的提交号，
以及一个必须是 GitHub `main` 祖先的锚点提交（分支已合入时写 `merge_anchor`，放弃时写 `discard_anchor`）。
工作流先不带令牌对两个镜像预览，再带令牌执行：GitHub 上仍存在的分支保留，镜像上已不存在的跳过，其余以精确
lease 原子删除并匿名回读核对。镜像上的提交号与记录不符时拒绝执行，须重新核对后更新计划；删除期间该分支在
GitHub 上重新出现时，脚本在镜像上将其恢复，并以清理未完成失败。

**并发与 Dependabot。** 仓库同步与分支清理共用一个并发组，同一时刻只运行其中一个。`mirror-release.yml` 的
每次运行，不论由发布事件、推送、定时还是手动触发，都使用它自己的另一个并发组：发布事件的运行与补齐运行不会
同时处理同一个标签，仓库同步与分支清理也不会顶替尚在等待的发布复制运行；同一组内只保留一个等待中的运行，
后到的发布复制运行会顶替它，之后的定时补齐仍会处理最新的正式版与测试包。正在进行的运行都不会被取消。
发布复制可能与仓库同步同时进行，其引用同步失败时（例如精确 lease 因此不符），30 秒后从新的快照重试一次。
仓库同步与分支清理跳过由 Dependabot 触发的推送与删除事件（这类运行取不到 Actions secret），由定时运行补齐。

**运行失败时。** 失败原因写在日志中 `Repository mirror failed:`、`Release mirror failed:` 或
`Approved mirror cleanup failed:` 之后，不含令牌、签名地址或远端的原始输出。常见情形的处理如下：

- 缺少令牌：配置 `GITCODE_MIRROR_TOKEN` 后重新运行。
- 「Mirror tag not found」或「Mirror tag differs from GitHub」：镜像上的标签缺失或不一致；重新运行
  `mirror-release.yml`，它在复制之前先同步引用。引用同步本身失败时，日志提示检查令牌、镜像的分支保护、
  网络连接或服务端对 lease 与原子推送的支持。
- 「Source release assets incomplete」：GitHub 发布页的附件尚未全部上传；上传完毕后在 Actions 中以该标签
  手动运行 `mirror-release.yml`，或等待定时运行。
- 「Source build metadata is not a clean distributable build of the release tag」：正式版发布页上的
  `BUILD-METADATA.json` 不是由该标签的干净提交打出、`public_distribution_ready = true` 的产物；按
  [release-checklist.md](release-checklist.md) §9.4 重新打包并替换 GitHub 上的附件后重新运行。
- 附件内容冲突，或镜像上已有的发布与 GitHub 不符：脚本不做任何修复；维护者在 GitCode 上核对该发布，
  手动删除错误的附件或发布后重新运行。
- 已撤回的正式版：见上。
- 时限用尽或网络错误：直接重新运行。发布复制只补传缺少的附件，已核对一致的附件不会重复上传。

## 6. 已知构建注意事项

- `SupportedOSPlatformVersion` 不要在 `net8.0-windows` 上设为 `10.0.x`：
  该写法要求目标框架写成 `net8.0-windows10.0.19041.0`，否则报 `NETSDK1135`。
- Machina.FFXIV 在 `lib/netstandard2.0/` 中放置了一个原生 DLL，
  会导致 MSBuild 报 `MSB3246`。该警告已在 `Directory.Build.props` 中降级为消息，
  并在 `Directory.Build.targets` 中将该文件从输出中剔除。
- `TreatWarningsAsErrors = true` 对所有 C# 项目生效，新增依赖时应先确认构建零警告。

### 6.1 Qt Creator 报 `appman-controller.exe does not exist`

现象：在 Qt Creator 中打开本工程后构建/运行，输出
`-1: error: Process failed: The program "appman-controller.exe" does not exist or is not executable.`

原因：这是 Qt Creator 的 **Qt Application Manager** 插件
（`QtApplicationManagerIntegration`）为 CMake 工程自动添加的
“通过 appman 部署/运行”配置。本项目是普通的 Windows 桌面程序，
仓库中没有任何 appman 相关内容，也不需要该插件。

处理（任选其一）：

1. Projects → 当前 Kit → Run → “Run configuration” 选择 `MentorRecorder.Desktop`，
   并删除 Deploy 步骤中的 appman 条目；
2. Help → About Plugins → 取消勾选 `QtApplicationManagerIntegration`，重启 Qt Creator。

此外应使用 **MinGW 64-bit** 的 Qt 6.11.x Kit，不要选择 llvm-mingw 或 MSVC；
也可直接使用 `scripts/build.ps1` 在命令行构建。Qt 安装路径不同时，按 §3 调整参数。
