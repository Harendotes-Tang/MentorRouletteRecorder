# 桌面端 UI 设计说明 / Desktop UI Design

本文件描述 `src/Desktop`（Qt 6 Quick 桌面壳）当前实现的界面结构、主题和交互约束。
读者是桌面端的开发者与评审者。

当前视觉依据均随源码提供：色板、字号、圆角和动效取自
[`Theme.qml`](../src/Desktop/qml/Theme.qml)，壳与导航取自
[`Main.qml`](../src/Desktop/qml/Main.qml)，布局与操作取自 `qml/pages/`、
`qml/components/`、`qml/dialogs/` 及对应业务目录。`docs/screenshots/` 提供已保存的效果参考；
核对新改动时仍需生成当前构建的截图，不能用历史截图代替运行验证。
本文件归纳这些受控实现，不要求读者取得私有设计材料。

下文「原型」及 `ff14.css`、`workbench.css` 等名称仅说明历史设计来源。
本地 `DOC/` 不随公开仓库分发，不再承担当前布局或参数的权威来源。
艾欧泽亚风格的**色值**自 1.2.0 起对照游戏自身窗口配色（深色 = 暖灰石板配浅字，浅色 = 羊皮纸配深棕字，金色只留给标记、进度环与柱条），当前取值见 `Theme.qml` 及 6.1 节。
第三种风格 **Harendotes** 与艾欧泽亚共用布局与圆角，色板取自角色毛色：
深色「夜色狼身」以海军蓝黑为底、橙焰为主色、古金仅作点缀；浅色「白胸冷光」以纯白为面板、冷调蓝灰为背景，
顶栏与侧栏同为浅色，底边绘制一条由橙焰过渡到古金的细线。标题用冷白宋体（Noto Serif SC）而非金色 Cinzel。面板不画金色四角，外框为 1 px 对角渐变：自左上角的橙焰经古金过渡，到右下角完全透明并露出面板自身的分割线色边框（components/PanelDecoration.qml，由四段直线渐变和三段圆角弧组成，不依赖 Canvas 或 Qt Quick Shapes）；侧栏状态面板在此风格下使用同一外框。

**「经典 / classic」界面风格**自 2026-09 改版起为**默认风格**，当前取值由
`Theme.qml` 的 classic 分支定义，历史来源为 workbench 设计。
`Theme.normalizeStyle()` 将设置规范化为 `classic`、`eorzea` 或 `harendotes`；classic 分支包含
色板、圆角、六级字号与 IBM Plex Mono 数字；页面代码只绑定主题变量（token），见 §6。
默认值仅影响从未手动选择过风格的用户。`setUiStyle()` 不写入与当前值相同的风格，
因此配置文件中存在的 `eorzea` 必定来自用户的主动选择。
历史原型可以辅助理解设计演变；当前视觉主题与「导随心得」实现直接核对对应 QML、主题及测试。

---

## 1. 壳与导航

| 区域 | 尺寸 | 说明 |
|------|------|------|
| 窗口 | 1280 × 800（最小 1100 × 720） | 无边框窗口（`Qt.FramelessWindowHint`），标题栏由界面自行绘制，边缘缩放交给系统（`startSystemResize`） |
| 标题栏 | 高 40 | 菱形徽标（仅艾欧泽亚系风格）+「导随记录器」+ 版本号 `v<版本>` + 后台服务状态（§4.8）+ 主题切换按钮 +「统计浮窗」按钮（§4.9.1）+ 最小化 / 最大化 / 关闭 |
| 侧边栏 | 宽 200 | `Achievement` kicker + 衬线金色品牌块 + 渐变分隔线 + 6 个页面 + 底部状态面板（`.status-panel`：自带底色和边框的扁平 `Card`，`Theme.statusPanelBackground/Border`）。状态面板只有原型的三行 FF14 / Npcap / 捕获；行首是按状态色（绿 / 灰 / 橙）绘制的 Lucide 图标（gamepad-2 / network / radio），取代原型的 8 px 圆点。Npcap 一行只在已连上采集服务、且读到的捕获状态带有 `npcap_installed` 时才写「就绪」或「未安装」，此前显示「—」并用灰色 |
| 内容区 | 余下空间 | 左右内边距 28，上 22 下 32 |

窗口没有系统边框，标题栏承担系统标题栏的职责：按住空白处拖动窗口，双击切换最大化；
右端的 ─ / ☐ / ✕ 三个按钮分别最小化、最大化（已最大化时改为还原图标）与关闭窗口，关闭按钮悬停时为红底白字。
关闭窗口同样受「关闭时最小化到托盘」约束（§4.5）。标题栏不绘制 macOS 交通灯圆点。

导航项（`.navi`）选中时为 90° 金色渐变底加左侧 2 px 金条，金条带一层柔光；
未选中时为 `--color-text-2`，悬停时叠加 `--color-fill`。
原型的序号列放置页面的 Lucide 图标，颜色随标签变化，两种风格一致（2026-09-17 起）。

页面顺序与原型一致：01 总览 / 02 历史记录 / 03 副本统计 / 04 职业统计 / 05 捕获诊断 / 06 设置。

## 2. 滚动模型

原型的 `<main>` 为 `overflow:auto`。实现中**每个页面的根都是一个垂直 `Flickable`**，
`contentHeight` 绑定到内容列的 `implicitHeight`，滚动条按需出现。
设置页是例外：页头与子导航固定，只有右侧面板列滚动，对应原型中子导航的 `position: sticky`。

* 表格使用 `Repeater` 与 `ColumnLayout`，而非 `ListView`。表格高度由内容决定，
  既不会被固定高度截断，也不会在页面底部留下大片空白。
* 历史记录每页行数随窗口高度变化（`HistoryPage.rowsThatFit`）：页头、筛选栏、表头与分页条之外
  能放下多少 44 px 的行就取多少，最少 5 行，因此一页记录与分页条总是完整可见、不留大片空白。
  窗口尺寸变化经 150 ms 防抖后写入 `RunListModel.pageSize`，模型保持当前页首行仍在视野内，
  仅调整其所在页的页码。右下角的「无法自动记录」提示卡浮在页面之上时，`Main.qml` 把它盖住的高度
  绑到 `HistoryPage.reservedBottom`，行数相应减少，分页条不会被它挡住。
  列标题居中，窄列的单元格随之居中，只有「副本」列左对齐。
* 捕获诊断页在降级模式面板或提示条出现时、以及打开维护者工具时会变高，此时页面可滚动。

该模型的代价是：很长的列表（例如「全部」副本）会使页面变长，而不是在表格内部滚动。
按本软件的数据量衡量（副本数十个、历史记录每页只取窗口放得下的行数），这是恰当的取舍。
分页由后端负责，不在视图层实现。

## 3. 字体与数字

正文字体栈（`QFont::setFamilies`，`main.cpp`）在 Windows 上为：

```
"Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", "Segoe UI"
```

其他平台仍按原型以 `"Noto Sans SC"` 居首。Windows 11 装的思源黑体是一个可变字体
（`NotoSansSC-VF.ttf`），Qt 的 Windows 字体库只取到它的默认字重：粗体靠加粗模拟、笔画糊成一片，
常规字重在 12–13 px 下偏细且无 hinting，可读性明显不如微软雅黑 UI 的静态字面；
原型的字体栈本就把微软雅黑列为 Windows 回退，因此在 Windows 上让它居首。

**标题字体（仅 eorzea 风格）** 对应 `ff14.css` 的
`--font-heading: "Cinzel","Noto Serif SC","Songti SC","SimSun",serif`：

| Theme 属性 | 值 | 用途 |
|---|---|---|
| `Theme.headingFamily` | `Cinzel` | 拉丁字母与数字（统计数字、进度环） |
| `Theme.headingFamilyCjk` | `Noto Serif SC` | 中文标题 |
| `Theme.numFamily` | 同 `headingFamily` | 大号数字 |
| `Theme.headingFamilyFor(text)` | 二选一 | 见下 |

QML 的 `font` 值类型**没有** `families` 列表，只有 `family`，因此无法像 CSS 那样声明字体栈。
`Theme.headingFamilyFor(text)` 以一个 CJK 区间正则判断文本本身是中文还是拉丁，
再返回对应的字体家族，`components/HeadingLabel.qml` 即按此实现。

Cinzel 随程序分发，许可为 SIL OFL 1.1。字体文件位于 `src/Desktop/resources/fonts/`，
包括 `Cinzel-Regular.ttf`、`Cinzel-Bold.ttf` 与 `OFL.txt`，
由 `CMakeLists.txt` 的 `qt_add_resources(... "mr_fonts" ...)` 编入 `MentorRecorderDesktopLib`，
并由 `main.cpp` 的 `installApplicationFont()` 调用 `QFontDatabase::addApplicationFont()` 注册。
此处采用静态字重而非 google/fonts 的可变字体 `Cinzel[wght].ttf`，
以便在离屏（freetype）后端上稳定获得真正的 Bold 字形。
许可见 [third-party-licenses.md](third-party-licenses.md) §3.2。

`Noto Serif SC` **不**随程序分发。`main.cpp` 登记了

```cpp
QFont::insertSubstitutions("Noto Serif SC",
    {"Noto Serif CJK SC", "Songti SC", "SimSun", "NSimSun"});
```

因此未安装思源宋体的机器会回落到系统自带的宋体，而不是无提示地退回无衬线正文字体。

offscreen 平台不会发现系统字体，因此**仅在 `--screenshot` 模式下**额外注册
`%WINDIR%\Fonts\msyh.ttc` 与 `simhei.ttf` 作为兜底。
这两个文件属于 Windows 系统资产，不随本软件分发。

**数字（仅经典风格）** 对应 `workbench.css` 的 `--font-mono: "IBM Plex Mono"`。
带 `tabular-nums` 的文字（`font.features: ({ "tnum": 1 })`），
以及艾欧泽亚风格下用 Cinzel 排版的大号数字，在经典风格中一律使用 IBM Plex Mono、字重 600：

| Theme 属性 | 艾欧泽亚 | 经典 |
|---|---|---|
| `Theme.numFamily` | `Cinzel` | `IBM Plex Mono`（统计卡、进度环） |
| `Theme.figureFamily` | 正文字体（与不设字体时相同） | `IBM Plex Mono`（其余 tnum 文字） |
| `Theme.figureWeight(bold)` | 保持原来的粗细 | `Font.DemiBold` |
| `Theme.monoFamily` | Consolas 等系统等宽字体 | 优先 `IBM Plex Mono` |

IBM Plex Mono 随程序分发，许可为 SIL OFL 1.1。字体文件位于 `src/Desktop/resources/fonts/`，
包括 `IBMPlexMono-Medium.ttf`、`IBMPlexMono-SemiBold.ttf` 与 `IBMPlexMono-OFL.txt`，
与 Cinzel 使用同一个 `mr_fonts` 资源，并在同一处调用 `addApplicationFont()` 注册。
仅包含 500 与 600 两个字重：600 用于数字，500 用于等宽文本（导航序号已改为图标）。
许可见 [third-party-licenses.md](third-party-licenses.md) §3.3。

**字号（仅经典风格）**：页面代码中使用的仍是艾欧泽亚风格的像素值，统一经过 `Theme.fs(px)` 换算。
经典风格将其归入 workbench 的六级字号 t1–t6，即 18 / 15 / 13 / 12.5 / 11.5 / 11。
`pixelSize` 为整数，因此 t4 与 t5 落在 13 与 12：

| 写的值 | ≤11 | 12 | 13 | 14–16 | 18–22 | 24–28 | 40 |
|---|---|---|---|---|---|---|---|
| 经典 | 11 | 12 | 13 | 15 | 18 | 22 | 32 |

以下位置不经该换算：页标题 `Theme.pageTitleSize`（经典 18）、对话框标题 `Theme.dialogTitleSize(px)`
（经典 15）、卡片小标题 13 / 600 正文色、表头 11 常规 `textMuted`、导航项 15（选中 600）。
职业与类型图标中的字母、侧栏 `◆`、窗口按钮的符号随图形尺寸确定，仍使用固定像素值。

原型中标注 `font-variant-numeric: tabular-nums` 的位置，实现使用
`font.features: ({ "tnum": 1 })`，涉及时间、耗时、次数、百分比与页码。
只有原型明确使用 `ui-monospace` 的位置仍使用等宽字体：
捕获诊断页「最近失败」的错误码（只在维护者视图中显示），以及维护者工具底部的状态块。

## 4. 各页面要点

### 4.1 总览（page 1）

* 成就进度环、8 张统计卡、当前导随卡、完成趋势与结果分布，布局与原型一致。
  统计卡在有记录时另出现两张：「未知结果」（`result_breakdown` 中 `UNKNOWN` 非零时）与「中途加入」
  （`joined_in_progress_count` 非零时，值为「次数 · 占比」，占比的分母是看到过进入方式的尝试，
  [statistics-definitions.md](statistics-definitions.md) §12.2；采集服务没有给占比时只显示次数）。
  卡片网格按张数算行数（每行 4 张，`cardRows`），第 9 张起多一行而不溢出。
* 趋势图：`charts/TrendChart.qml` 在 `GraphsAvailable` 为真时加载
  `charts/GraphsTrendChart.qml`，即 Qt Graphs 的 `GraphsView` 与 `BarSeries`；
  否则回落到手绘的 `components/TrendBars.qml`。
  `main()` 中的 `probeGraphs()` 以一个最小 QML 片段实例化 `GraphsView` 进行判定，
  失败时将原因写入 stderr。两条路径都提供 `MM-dd · N 次` 的悬停提示。
  数据桶来自 `GetDashboardStats.trend`，由 Collector 在 SQL 中聚合。
  桌面端只把每个桶的 `start_utc` 按**本地时区**渲染成标签，不自行分桶。
  统计口径见 [statistics-definitions.md](statistics-definitions.md) §12.1。
* `components/UpdateNotice.qml`（`updateNotice`）位于页头之下，在 `App.update.updateAvailable` 为真、
  且本机未忽略该版本或用户已开始下载该版本（`App.update.downloadEngaged`：下载中、校验中或已就绪）时可见，
  内容全部来自 `GetStatus` 应答中的可选对象 `update`。
  「检查新版本并提示」关闭时采集服务报告的 `update_available` 恒为假，此前查到的新版本横幅随即消失。
  卡片自上而下为标题「有新版本 x.y.z」、说明文字、下载状态（`UpdateDownloadStatus`，前缀 `updateNotice`，见下文）
  与按钮行：主按钮（`downloadInstallerButton`）、「忽略此版本」（`dismissUpdateButton`，下载开始后隐藏）
  与透明文字按钮（ghost）「查看更新说明」（`openReleasePageButton`）。
  说明一行在采集服务支持软件内下载时为「当前 <版本>。点「下载安装包」只下载安装包并校验；准备好后，再点「立即安装」启动安装程序。」，
  否则为「当前 <版本>。点「下载新版本」用浏览器下载安装包，再自行运行安装。」。
  主按钮的文字与作用随下载阶段（`App.update.downloadPhase`）变化，三处更新位置（本横幅、设置页「通用」与「关于」）相同，
  见 [privacy-boundary.md](privacy-boundary.md) §8.6：

  | 阶段 | 出现条件（`update.download.state`） | 主按钮 | 下载状态中的一句（`<前缀>DownloadStatusText`） |
  |---|---|---|---|
  | `offer` | 有新版本，`IDLE` | 「下载安装包」：发出 `StartUpdateDownload`（采集服务未开始下载时只显示一条提示，见下文） | 无 |
  | `downloading` | `DOWNLOADING` | 「取消」（横幅中改为次要按钮）：发出 `CancelUpdateDownload` | 「<名称>：已下载 P%（R / T MB）」；服务器未声明大小时为「<名称>：已下载 R MB」，不显示进度条 |
  | `verifying` | `VERIFYING` | 「取消」 | 「<名称>：已下载完毕，正在核对发布时公布的校验值…」，进度条满格 |
  | `ready` | `READY` | 「立即安装」；桌面端自行核对期间为「正在校验…」且不可用 | 「<名称>：已下载并通过校验。点「立即安装」后本软件会关闭，由安装程序完成更新；Windows 会请你批准它以管理员身份运行。」；核对期间为「<名称>：正在校验，通过后启动安装程序并关闭本软件。」 |
  | `failed` | 有新版本，`FAILED` | 「重试」：再次发出 `StartUpdateDownload`（同上） | 采集服务给出的 `download.message`，橙色；没有时为「下载没有完成。可以点「重试」，或点「在浏览器中下载」改用浏览器下载。」 |
  | `browser` | 有新版本，采集服务不报告 `download`，或对 `StartUpdateDownload` 答 `ERR_BAD_REQUEST` / `ERR_UNKNOWN_MESSAGE` | 「下载新版本」：交给系统浏览器下载 | 无 |

  其中「<名称>」为「x.y.z 版的安装程序」（版本未知时为「新版本的安装程序」），P、R、T 为百分比、已下载与总大小的兆字节数（保留一位小数）。
  `components/UpdateDownloadStatus.qml` 是三处共用的下载状态：大小已知时的进度条（`<前缀>DownloadProgress`）、上表的一句、
  点击「立即安装」后未能启动安装程序的原因（`<前缀>InstallProblemText`，红色），以及透明文字按钮「在浏览器中下载」
  （`<前缀>BrowserDownloadButton`，只在 `failed` 阶段、或「立即安装」没有启动安装程序之后出现）。
  下载或校验进行中，`UpdateController` 约每秒请总控重读一次 `GetStatus`（`kDownloadPollMs`），状态改变、管道断开或程序退出时停止。
  没有新版本时（例如维护者工具重新下载最新正式版时）失败的说明只以提示给出一次。
  采集服务两次开始下载之间至少相隔 5 秒，取消的下载收尾之前也不开始新的下载；此时它原样返回当前状态
  （仍为 `IDLE`，或与点击前相同的失败），桌面端只显示一次「没有开始下载，请稍候几秒再试。」的提示，不显示为失败。

  「立即安装」只在 `ready` 阶段可用。桌面端先确认当前没有正在进行的导随，再按报告的路径核对文件：
  路径位于数据目录（`CollectorProcess::collectorDataDirectory`）下的 `updates\`、文件名恰为该版本的安装程序名，
  是普通文件且本身与所在文件夹都不是链接（`InstallerVerifier`）。随后以 `InstallerHold` 打开该文件：只读打开，
  只允许其他程序读取，路径末端是链接时打开链接本身而不跟随。从此直到安装程序已经启动、程序退出，或这次安装被放弃，
  文件始终被持有，期间任何程序都不能写入、改名或删除它，也不能改名它的上级文件夹。持有文件之后，
  再核对一次其类型与位置：它须是磁盘上的普通文件，系统为它解析出的完整路径（沿途链接全部展开）须与报告的路径相同（只忽略字母大小写）。
  SHA-256 通过同一次打开以每次 1 MiB、分多轮事件循环读取计算，与报告的值比较。全部通过、并再次确认没有正在进行的导随
  且持有的文件仍在原路径之后，在持有的状态下经系统外壳启动该文件（Windows 随即请求管理员批准），
  启动成功即经 `TrayController::quitApplication()` 正常退出，退出时才释放文件（`stopForQuit`）。
  任何一项不通过都不启动，立即释放文件（`refuseInstall`），原因以提示与红字给出，同时出现「在浏览器中下载」：
  「还没有读到当前是否在导随中，暂时不能安装，请稍后再试。」；
  「正在导随（已匹配或已在副本中），现在安装会中断这次记录。请在这次导随结束后再点「立即安装」。」；
  报告的路径不在数据目录下的 `updates\` 时为
  「下载的安装程序不在本软件的数据目录中，为安全起见没有启动它。可以改为在浏览器中下载。」；
  持有的文件解析出的路径与报告的路径不同时（数据目录经由链接、SUBST 映射的盘符或映射的网络驱动器到达即属此类）为
  「无法确认下载的安装程序位于本软件的数据目录中（数据目录可能经由链接或映射的盘符到达），为安全起见没有启动它。可以改为在浏览器中下载。」；
  「下载的安装程序不是一个普通文件，为安全起见没有启动它。可以改为在浏览器中下载。」；
  无法这样打开（例如另有程序正以写入方式打开它）或读取失败时为
  「无法读取下载的安装程序，没有启动它。可以改为在浏览器中下载。」；
  「下载的安装程序与发布时公布的校验值不一致，为安全起见没有启动它。可以改为在浏览器中下载。」；
  启动失败（例如在 Windows 的确认窗口中选择了「否」）时为
  「安装程序没有启动（可能是在 Windows 的确认窗口中选择了「否」）。本软件继续运行，可以再点「立即安装」，或改为在浏览器中下载。」。
  模拟运行从不启动安装程序：使用模拟数据时，`AppController` 将「立即安装」的启动函数替换为不执行启动操作的函数，
  其他后端使用系统外壳。

  「在浏览器中下载」与 `browser` 阶段的「下载新版本」把采集服务给出的安装程序地址（`update.installer_url`）交给系统浏览器，
  由浏览器下载，安装程序由用户自行运行。
  桌面端只接受本项目仓库 `releases/download/<标签>/` 下以 `-setup.exe` 结尾、不带查询串与片段的地址
  （`UpdateController::isInstallerUrl`）；没有这样的地址时改为打开发布页，并以提示说明已改为打开发布页。
  「查看更新说明」把固定的公开发布页交给系统浏览器。「忽略此版本」只对该版本有效，升到更高的版本后会再次提示。
  不点击就不下载、不安装；采集服务从不执行它下载的文件，见 [privacy-boundary.md](privacy-boundary.md) §8.4、§8.6。
* 「当前导随」卡片的**当前职业**一栏始终显示：存在职业编号时显示职业图标、职业名与职能图标，
  职业尚未识别时显示「未知」（职业报文在登录与换职业时出现，识别后该栏随记录更新）。
  结束后的结果与心得对话框提供可选的职业补录，与确认结果一并保存；
  用户不选择时留待后续补录。
* 「待处理」卡片（`dashboardPendingWorkCard`，内容为 `components/PendingWorkContent.qml`）在有待处理的记录、
  或待处理列表读取出错时出现：标题「待处理 · N」，只列出最新的一条；条数多于一条或读取出错时另有
  「查看全部待处理」，打开完整列表。待处理列表的内容与规则见 §4.5.2。

### 4.2 历史记录（page 2）

* 筛选栏按原型压缩为**一行**（`Flow`，间距 6），自左至右依次为：
  搜索框（`historySearchField`，占据剩余宽度，最多 200 字，占位文字「搜索 副本 / 职业 / 备注」）；
  日期范围（`historyDateRange`，`components/DateField.qml` 的范围模式，238 px，占位文字「日期范围」，
  填写后显示为「开始 — 结束」；点击打开月历 `MonthGrid`，弹层上方另有「开始日期」「结束日期」两个输入框，
  可手动输入 yyyy-MM-dd，对应原型的 `<input type="date">`）；类型 / 职业 / 结果 / 来源四个窄下拉框
  （宽度按占位文字计算，首项即占位文字，不再使用「全部…」）；多选下拉框「标记类型」（`historyMarkFilter`，118 px，
  选中若干项时写作「标记类型 · N」），其中为 待复核 / 已修正 / **有笔记** / **中途加入** / 已删除 五个开关；
  以及 `清除`（ghost 纯文字，至少 40 px）。
  页面宽度不足 1020 px 时「来源」收进「更多筛选」，其余筛选仍在原位；已选的来源以可移除的 chip 显示在筛选栏下方。
* 行首复选框按稳定编号跨页保留选择，全选仅覆盖当前页。改变筛选前提示清空选择，取消时保留原筛选与选择。
  已选记录可导出 CSV/JSON、批量软删除或恢复；永久删除只允许已软删除记录，并明确提示数量及不可恢复。
  设置页提供删除记录的保留天数，默认 30 天，亦可永不自动清理。
* 历史日期排序和筛选使用独立 `history_date` 投影。历史导入缺游戏时间只标「待补充」，
  已确认结果仍计入总览统计，未知游戏时间不由原站日期替代。
* 原型**删除了「副本」下拉框**，但副本统计页的下钻（`filter.content_id`）必须继续可用。
  实现的做法是：`App.historyFilter.content_id` 非空时，在筛选栏下方的一行中增加一枚以副本名命名的
  可移除 chip（`removable`，点击时发出 `removed()`）；点击该 chip 即清除 `content_id` 并重新查询。
  跳转并筛选仍经由 `App.showHistoryForContent()`，不存在第二套筛选状态。
* 「标记类型」中的开关分别在 `RunFilter` 中加入 `pending_review: true`（待复核，`pendingReviewChip`）、
  `corrected_only: true`（已修正）、`with_reflection: true`（有笔记，`historyReflectionFilter`）、
  `joined_in_progress: true`（中途加入，`historyJoinedInProgressFilter`，只列出记为中途加入的记录）与
  `deleted_only: true`（已删除）。
* 「标记」列可依次显示 待复核（非导入记录的 `pending_review`）/ 待补充（导入记录缺少游戏时间或结果等信息）/
  **中途加入**（`run.joined_in_progress === true`；`false` 与 `null` 都不显示）/ 已修正或已确认 / 已删除 /
  手动创建 / **有笔记**（`run.reflection` 非空）。
  记录详情的「进入方式」一栏按 `Fmt.joinedInProgressLabel` 显示 中途加入 / 开局进入 / 未知。
  已修正 取自 `run.manually_corrected`（人改过软件记下的内容）；已确认 表示该记录有过修订、已不在待复核，
  但没有任何一次改动推翻软件的记录——通常是用户回答了软件留待确认的结果，或补上了软件没有认出的职业、副本。
* **筛选实时生效**，带 300 ms 防抖。先前实现中的「应用筛选」按钮已移除。
* 日期输入接受 `yyyy-MM-dd`，且须是真实存在的日期（`DateField.parse()`）。
  值不完整或非法时该输入框的文字转为红色并保持原样，填写完整之前不发出查询，也不会以空值静默查询，
  列表保留上一次的结果；在月历中只点了起点、尚未点终点时同样不查询。开始日期晚于结束日期时也不查询，
  筛选栏下方以橙字提示「开始日期晚于结束日期，筛选未更新。」（`historyRangeHint`）。
  有记录的日子只决定月历中哪些日期可以点选（`restrictToRecords`，取自 `App.runs.calendarAvailableDays`）：
  已填入的完整日期始终有效并参与查询，即使改变其他筛选之后那一天已没有记录。
* 历史记录读取失败时，页头小字为「读取失败」，列表位置以橙字说明「历史记录读取失败：<原因>」，
  不显示为「0 条匹配」或「没有符合筛选条件的记录。」（`RunListModel.loadError`）。
* 点击表头排序：再点同一列切换升降序，点另一列改为按该列降序；同一排序键只在一列上显示箭头。
  默认按「日期」列的键 `history_date` 由新到旧排序，因此打开页面时该列即带箭头。
* **表头可以自定义**（`components/HistoryTableHeader.qml`、`components/HistoryColumnsDialog.qml`；
  列的设置由 `App.historyColumns`（C++ `HistoryColumnsController`）保存，页面按它算出一份表头与各行共用的
  列布局 `columnLayout`，单元格 `components/HistoryCell.qml` 按布局定位，因此标题与数据不会错位）：
  * **显示哪些列**：分页条左端的「自定义列」按钮（`historyCustomizeColumns`，放在表格脚下，不占用各行的宽度），
    或在表头任意位置点右键，打开「自定义列」对话框，按当前顺序逐列勾选；至少保留一列，只剩一列时它的勾选框不可取消。
    「恢复默认」一并恢复显示、顺序与列宽，没有自定义过时不可用；「完成」关闭。行首复选框列固定在最左，不参与自定义。
  * **拖动排序**：按住某列标题左右拖动，标题浮起到一块带描边的底板上并跟随指针，其余标题滑开让出位置，
    表头与表格上贯穿一条落点指示线（`historyColumnDropGuide`），松手后生效；放回原位或拖动被取消时不作改动。
  * **拖动列宽**：标题右边缘 7 px 是宽度把手（`historyColumnResize_<键>`，指针变为左右分割箭头，悬停时显示边线），
    拖动时该列与各行同步变宽变窄，并沿新的右边缘贯穿一条竖线（`historyColumnResizeGuide`），松手后保存；
    拖回原宽或拖动被取消时不保存，**双击把手恢复该列的默认宽度**。每列有各自的最窄宽度
    （日期 80、进本 / 结束 62、副本 120、类型 56、职业 / 结果 64、耗时 50、来源 48、标记 60，整体下限 40、上限 800；
    文件里更窄的值按该列最窄处理），拖宽以表格放得下为限：其余列按所存或默认宽度、「副本」按最窄宽度计算余量，
    拖到余量用尽即停。窗口变窄放不下时，正在拖动的列仍跟随指针，其余列按各自可让出的余量等比收窄；
    收窄状态下碰到把手不会跳变，也拖不宽，只能拖窄。日期、时间与耗时的文字在列被收窄时省略号截断，不会溢出到相邻列。
  * **两种拖动都不会触发排序**：点击标题仍按该列排序；指针移动超过拖动阈值后，这一次按下就只是拖动。
    把手是独立的鼠标区域，点击或双击把手都不排序；右键也不排序。
  * **默认宽度与原先相同**（日期 96 / 86、进本与结束 70 / 64，经典 / 艾欧泽亚；类型 84、职业 112、结果 92、耗时 66、
    来源 60、标记 92）。「副本」仍是唯一的弹性列：没有为它拖出宽度时吃掉其余列剩下的宽度（1280 宽的窗口下经典风格为 225 px，
    与改版前的 `RowLayout` 相同），最窄 120；拖出宽度之后按该宽度固定，表格右端留空。隐藏某列后，空出的宽度同样归「副本」。
  * **按用户保存**：显示、顺序与宽度以列的键（`date` / `entered` / `ended` / `duty` / `category` / `job` /
    `result` / `duration` / `source` / `marks`）写入 `desktop.ini` 的 `history/column_order`、`history/hidden_columns`
    与 `history/column_width/<键>`（只写与默认不同的部分），重启后恢复；键与界面语言无关，切换语言不改变保存的设置。
    文件中未知的键被忽略，缺少的列按默认顺序补在后面（新版本新增的列因此自动出现），全部隐藏视为全部显示，
    宽度限制在 40–800 px。
  * 表头设置只影响屏幕上的表格：筛选、排序、勾选与导出 CSV / JSON（由采集服务生成，§4.7）不受影响，
    导出的内容仍是完整字段。
* 点击行打开右侧 380 px 的详情浮层（`dialogs/RunDetailPanel.qml`），
  含四个页签 详情 / 事件摘要 / 修改记录 / **笔记**，底部为「手动修正」、「确认已复核」（仅待复核的记录），
  以及「删除记录」（软删除）或「恢复记录」（已软删除的记录）。
  详情页签采用原型的两列键值网格。修改记录只列出详情页同样展示的字段，字段名、取值、修订种类与
  操作者均以中文显示（`Fmt.revisionFieldVisible` / `revisionFieldValue` / `changeKindLabel` /
  `revisionActorLabel`）；事件摘要见 §8 第 2 条。内部编号与原始取值只在维护者工具打开时显示。
  详情页签的「待复核」一行（`pendingReviewText`）按结果区分：结果未知时为
  「是 · 结果尚未确认，请核对是否通关」；结果已知而仍待复核时（例如崩溃恢复记为中断的记录，
  或生成记录的校准事后被撤下、保留原结果的记录，后者可能已是通关），由系统修订加上待复核标记的为
  「是 · 需要你确认这条记录（原因见“修改记录”）」，其余为「是 · 需要你确认这条记录」。
* 详情页签末尾的「备注」区块：备注文字（经手动修正编辑）之下是该记录的图片条
  （`components/NoteImageStrip.qml`，64 px，点击缩略图以浮层查看原图），包含「添加图片」格子与每张图片右上角的移除按钮；
  在这里添加或移除是即时的（`NoteImages.addPicked()` / `removeOne()`），不经向导、不需理由、不产生修订，
  失败原因以红字显示在图片条下方；已软删除的记录不可编辑图片。该区块始终显示，以便入口可见。
  图片来自安装目录的 `note-images\<run_id>\`（C++ `NoteImageStore`，QML 运行时入口 `Runtime.noteImages`），
  采集服务与数据库不参与；面板监听 `NoteImages.imagesChanged` 重新读取。
* 列表第二行的「资料片 · 等级」由 `DutyCatalog::enrich` 从随包资料表补充：先按 `content_id`，
  没有内容编号（区域反查识别的记录）时按 `territory_id` 回查（`lookupByTerritory`），
  同一区域对应多个副本时只补全它们一致的字段。
* 选中的行使用 `--color-accent-100` 底色，加左侧 2 px 金色内嵌条，
  对应原型的 `box-shadow: inset 2px 0 0 var(--color-gold)`。
* **详情浮层是绝对定位的覆盖层**，与原型的
  `position:absolute; right:12px; top:12px; bottom:12px` 一致。
  浮层位于页面内容*之上*，不占据布局宽度，表格因此始终保持满宽。
  早先的实现为 `Flickable` 设置 `rightMargin: 392`，浮层打开时「副本」列被压缩为「伊…」。
  页面仍可在浮层下方滚动。关闭方式为 ✕ 按钮，或点击浮层以外的任意位置；
  后者由一层透明 `MouseArea` 实现，仅在浮层打开时启用，且不拦截滚轮事件。
  浮层根部的 `TapHandler` 接住落在浮层自身空白处（页头、正文文字）的点击，这类点击不会关闭浮层。
* 详情页签的**职业**字段横跨两列，显示职业图标、职业名、职能图标与职能名。
  职业未识别时显示 All-Rounder 图标与 `未知 · job_detection=UNKNOWN`。
* 表格「职业」列默认宽仅 112 px，不足以容纳职能图标，因此只保留职业图标与名称，
  悬停时显示 `职业 · 角色` 的 tooltip；拖宽该列也不加入职能图标。

### 4.3 副本统计（page 3）/ 职业统计（page 4）

* 左侧为柱状列表，右侧为表格；点击有副本或职业标识的行，跳转到按该副本或职业筛选的历史记录。
  `content_id` 为空的副本行（「未知副本」，以及同一区域对应多个副本的行）与 `job_id` 为空的职业行
  不跳转，停留在原页并以 toast 提示历史记录无法单独列出它们（`App.showHistoryForContent` /
  `showHistoryForJob`）。
* 统计读取失败时，页头小字为「读取失败」，列表上方以橙字说明
  「副本统计读取失败，下方没有统计结果：<原因>」（职业统计同理），不显示为「0 个副本」。
* **职业统计的「角色」列使用 `Jobs.roleGroup()`**，即图例的六个分组
  坦克 / 治疗 / 近战 / 远程物理 / 魔法 / 未知，不出现契约中的粗粒度 `DPS`（「输出」）。
  `Fmt.roleLabel()` 仍然保留，但只用于确实需要契约 `Role` 三分类的位置。
* 职业图标使用带职能边框的图标。
* **「角色」列与圆环图例均使用真实的游戏内职能图标**（`components/RoleIcon.qml`，
  默认 20 px，启用 `smooth` 与 `mipmap`，`sourceSize` 固定 64×64，只降采样一次）。
  图例保留一条 3 px 的调色板色条，使图例与圆环的配色一一对应；
  名称 / 占比 / 次数三列不变。
* **两张表格各有一列「中途加入」**（每行的 `joined_in_progress_count`，口径见
  `docs/statistics-definitions.md` §12.2），位于「次数」之后，与「完成率」同样式、右对齐；
  列宽至少 58 px（四个汉字），标题更宽的语言（英文「Joined late」）按标题实际宽度加宽；
  没有中途加入的行显示 0，旧版采集服务不发送该字段时同样显示 0。原型没有这一列：
  职业统计的「次数」条形列由原型的 34 %（1280 宽下约 220 px）改为有空间时 180 px 为它让出位置；
  表格放不下时该列先按页面宽度算出的余量收窄（`runsColumnWidth`，最窄 48 px），再轮到「职业」列，
  其余列宽固定且表头与数据行取同一组数值（`Layout.minimumWidth` 等于列宽，布局不会把它们压窄），
  各列因此始终对齐，窗口最小宽度 1100 下不溢出。
* 副本统计页的「副本类型占比」饼图（`DungeonStatsModel::categoryBreakdown`）按副本类型统计通关次数
  （各行的 `completed_count`），分母是同一筛选下全部行的 `completed_count` 之和
  （[statistics-definitions.md](statistics-definitions.md) §12.1）。分段依次为 四人迷宫 / 讨伐歼灭战 / **极神** /
  大型任务 / 团队任务 / 行会令，其后是这五类之外出现的其他类型与「未识别」。「极神」是从讨伐歼灭战中拆出、
  只在界面上存在的分段（`DutyCatalog::isExtremeTrial`）：一行的内容编号在随包副本目录中对应一个讨伐歼灭战，
  且其名称属于极难度（以「歼殛战」等极神副本的名称结尾，或为「终极之战」），该行的通关计入「极神」；
  名称以「幻巧战」结尾的幻巧战不计入「极神」。其余讨伐歼灭战，包括内容编号在目录中查不到的行，仍计入「讨伐歼灭战」，
  因此「讨伐歼灭战」分段不含极神，两段之和才是该类型的全部通关。「极神」为 0 时同样列出，颜色为 `yellow`，
  其他分段的颜色不因它的插入而改变。记录、采集服务的统计与导出都不区分极神。

### 4.4 捕获诊断（page 5）

本页自 2026-10 改版起按设计稿「三皮肤整合版 · 捕获诊断」排布：单列，最大宽度 1080，靠左对齐，
卡片之间间距 12 px。页面拆分为下列文件，均位于 `qml/pages/`：

| 块 | 文件 | 何时出现 |
|---|---|---|
| 标题行 | `CapturePage.qml` | 始终。标题「捕获诊断」+「被动监听 · 不发包 · 不注入」；右侧普通用户是带状态点的状态标签（`captureHeaderStatus` / `captureHeaderStatusText`，`components/StatusTag.qml`：自动监听中 / 等待游戏启动 / 校准中 / 校准待核对 / 暂时读不到游戏数据 / 无法自动记录 / 需手动记录 / 等待选择游戏 / 检查中，与首张卡片的判断同源，颜色按 ok / warn / bad / off 四种语气），维护者是捕获按钮（`captureHeaderAction`，文字取 `App.captureActionLabel`，如「开始捕获」「停止捕获」，点击调用 `toggleCapture()`） |
| 首张卡片（状态 + 连接状态） | `CaptureHeroPanel.qml`（文案 `CaptureHeroCopy.qml`，连接状态数据 `CaptureChainModel.qml`） | 始终。上半是一盏状态灯、一句标题、一句说明和该状态的按钮，降级模式也在这里；下半是「连接状态」的总结与四个节点 |
| 记录对象 | `GameSelectionPanel.qml` | 有游戏窗口可选时 |
| 提示与校准 | `CaptureNotices.qml` | 各自按需；全部不出现时整块不占位。含网卡提示、自动跟随提示、校准行（`calibration/CalibrationCard.qml`：本机校准 ｜ 共享校准）与「版本支持」一行（协议档案卡） |
| 解析 ｜ 工具 | `CaptureParsePanel.qml` ｜ `CaptureToolsPanel.qml` | 始终；并排 2 : 1，顶端对齐 |
| 新区服采证步骤 | `CaptureValidationEvidenceCard.qml`（`guidedMode`，`templateObservationGuideCard`） | 点「工具」中的「采证步骤」后出现，再点一次收起 |
| 维护者工具 | `CaptureMaintainerSection.qml`（+ `CaptureValidationEvidenceCard.qml`） | 仅 `App.maintainerToolsVisible` |

页面只读取 `App`、`Fmt`、`Theme`，不读取 `Settings`。
因此 `SharedCalibrationCardTests` 与 `CapturePageTests` 只需提供这三个上下文便可加载整页。
启动自检的禁词不变：普通页面不得出现「开始捕获」「停止捕获」「开始验证」「维护者工具」「对照核对」。

**首张卡片**（`captureHeroPanel`）的上半部分由 `CaptureHeroCopy` 决定说什么。还没有读到捕获状态时
（没有连上采集服务，或已连上而尚未读到带 `npcap_installed` 的捕获状态，`statusKnown` 为假）显示「连接中」；
其余情况按 `AutomaticRecordingController::project()` 的优先级依次判断 Npcap 不可用（降级模式）、
等待选择游戏、读不到游戏数据、本机校准（含共享校准）、游戏未运行、受阻、正常监听，都不成立时为检查中。
本机校准只在自动记录控制器也处于校准状态、游戏未运行、或已按校准出的档案正常监听时说话
（`calibrationSpeaks`）：采集受阻、设置尚未确认等状态优先于一份已完成的校准，页面标签因此与侧栏一致；
此时校准行自己把那句话说出来（`CaptureNotices.heroSpeaksCalibration`）。
每种状态给出一盏状态灯（`components/StatusLed.qml`，ok 绿 / warn 橙 / bad 红 / off 灰）、
一句标题（`captureHeroHeadline`）与一句说明（`captureHeroText`）；读不到游戏数据时另有一行
「成功读取 N 条 · 读取失败 N 次」（`captureHeroDetail`）。

* 连接中：标题为「正在连接自动记录服务…」（没有管道）或「正在获取自动记录状态…」（已连上、尚未读到状态），
  灯为灰色，页面标签为「检查中」；没有管道时说明取标题栏的后台服务状态句（`App.collectorStatusText`，§4.8），
  已连上时没有说明。此时页面不对 Npcap 下任何结论，不出现安装步骤与安装按钮。
  自动记录控制器仍保留着一次受阻（例如断线之前已提示的故障）时，按受阻显示，与侧栏一致。
* 正常监听：标题「自动记录已开启，正在读取游戏数据」，说明「进本和出本会自动记录。识别到通关后会自动填写结果；
  如果没有识别到，请在离开副本后打开记录补上结果。」。
* 游戏未运行：标题「等待游戏启动」，说明按已安装版本的档案结论变化。
* 受阻：标题「暂时不会自动记录」，说明取自动记录控制器的句子，去掉末尾的「请查看诊断」与括号中的错误码。

右侧是两个透明文字按钮「重新检测游戏」（`rescanGameButton`：重读 `GetStatus`、协议档案状态与验证状态）
与「重新检测网卡」（`rescanAdaptersButton`，`ListCaptureAdapters`）。

**降级模式**就是这张卡片的 Npcap 状态，只在已读到捕获状态时判断：标题「未安装 Npcap，自动记录已停用」
（已安装但不可用时为「Npcap 暂时用不了，自动记录已停用」）、说明「自动记录需要 Npcap 驱动，请自行下载安装。
安装前仍可手动记录、查看统计、导出和备份。」，右边是四步安装说明（`captureNpcapPanel`），
按钮换成「打开 npcap.com」与「重新检测」（`ListCaptureAdapters`），页面标签为「需手动记录」。
已安装但不可用时（`NOT_WINPCAP_COMPATIBLE` / `NPCAP_ADMIN_ONLY` / `LOAD_FAILED`），
说明改为一句中文原因，加上采集服务自身的 `npcap.install_hint`（经 `install_hint_ref` 按界面语言显示）。
该提示给出的正是具体的修复方式，界面与 `--capture-doctor` 因此不会给出两种说法。
未安装时不显示 `install_hint`（其全文见 [capture-diagnostics.md](capture-diagnostics.md) §2），
由上述固定说明与安装步骤代替。

**提示与校准**位于首张卡片下方，顺序固定：

* 记住的网卡没有游戏流量（`adapterPreferenceStaleNotice`，标题「记住的网卡上已经没有游戏流量了」）：
  `preference_stale` 与 `recommended` 两个适配器都已知时出现。「使用推荐网卡」（`useRecommendedAdapterButton`）
  写入 `CaptureSettings.adapter_id`。
* 自动跟随（`captureReadinessNotice`，仅维护者可见，且仅在未捕获时出现）：关闭时显示橙框与「开启自动跟随」。
* 校准行（`calibrationCard`，本机校准面板 `calibrationLocalPanel` 在左、共享校准面板
  `calibrationSharedSection` 在右，宽度 2 : 1，卡片窄于 640 px 时上下叠放，见 4.4.1、4.4.2）：
  `calibration.state != IDLE` 时出现。状态的那句话由首张卡片说（`headlineInHero`），
  本机校准面板只摆进度；共享档案正在记录时本机校准面板隐藏，共享校准面板占满整行。
* 协议档案（`protocolProfileCard`，小标题「版本支持」，一行：小标题、句子与说明、右侧按钮）：
  正在使用本机校准或共享校准的档案、可以「分享给其他玩家」（`protocolShareHint` / `protocolShareButton`）、
  「重新校准」（`protocolRecalibrateButton`，仅本机校准）或
  「恢复上一份本机校准」（`protocolRestoreButton`，仅在有被停用的档案待恢复时），
  或游戏正在运行而档案不是 `VERIFIED`（校准行存在时由校准行解释）时出现。
  普通用户看到的标题是「正在使用这台电脑完成的校准」「正在使用其他玩家分享的校准」「当前游戏版本暂不支持自动记录」
  一类语句，**不出现档案编号与状态令牌**；维护者在 `VERIFIED` 时看到「已就绪：`<profile_id>`」。
  游戏未运行时，措辞中的「当前游戏版本」改为「已安装的游戏版本」或「安装在这台电脑上的游戏版本」；
  档案不可用的提示此时不出现，相关信息已由首张卡片按已安装的版本给出。

读不到游戏数据不再是单独的提示条：`captureMidstreamSuspected`、`captureSilent` 或 `recording.silent`
且游戏正在运行时，首张卡片的标题依次为「已连接游戏，但还读不到游戏数据」/「正在读取游戏数据，但还没有识别成功」/
「正在监听，但还没有收到游戏数据」，页面标签为「暂时读不到游戏数据」，说明取采集服务的 `hint`，
缺失时取自动记录控制器的提示，下面一行为「成功读取 N 条 · 读取失败 N 次」。

**连接状态**是首张卡片的下半部分（数据来自 `CaptureChainModel.qml`）：一条分隔线下是小标题「连接状态」与一句总结。
总结（`captureChainSummary`）按顺序给出**第一个**阻碍记录的原因，依次为：
尚未读到捕获状态（与首张卡片相同的「正在连接自动记录服务…」/「正在获取自动记录状态…」，灰字）；
验证正在等待所选游戏重启；等待选择游戏；Npcap 未安装或不可用；游戏未运行（见下）；
待核对 / 校准受阻 / 正在重新校准；正在检查版本支持；版本未识别、不支持、支持文件有冲突或没有适用的支持文件
（与协议档案卡使用同一套说法）；
记住的网卡没有流量；`last_error_code`（「暂时无法读取游戏数据 · <`Fmt.captureErrorLabel`>」）；
监听尚未启动（「正在启动监听…」）；读不到游戏数据；其他受阻情况（「暂时不会自动记录 · 见上方说明」）。
以上均不成立时，总结为「游戏、驱动、网卡和版本支持均已就绪」。问题用橙字，等待与就绪用灰字。

游戏未运行时的总结取决于采集服务是否已从本机记住的安装目录读到客户端版本
（`capture.ffxiv_running` 为假而 `capture.game_build` 与 `region` 已知）：

* 版本未知（首次使用，尚未见过游戏）：「等待游戏启动 · 启动后会检查能否自动记录」，灰字。
* 版本已知、正在检查：「等待游戏启动 · 正在检查已安装的游戏版本」，灰字。
* 版本已知且档案为 `VERIFIED`：「等待游戏启动 · 已支持安装的游戏版本，启动后会自动记录」，灰字。
* 版本已知而没有可用档案：橙字，按情况为「等待游戏启动 · 请先在「本机校准」中核对结果」、
  「等待游戏启动 · 校准遇到问题，请查看「本机校准」」、「等待游戏启动 · 当前安装的游戏版本需要重新校准」、
  「等待游戏启动 · 游戏版本支持文件有冲突，暂时不能自动记录」或「等待游戏启动 · 当前安装的游戏版本暂不支持自动记录」；
  共享校准正在获取、等待核实或等待同意时为「等待游戏启动 · 请查看「共享校准」中的进度」，不与校准行的说法冲突。

游戏未运行时一律称「已安装」或「当前安装」的游戏版本，而不称「最新」：补丁日启动器完成更新之前，
磁盘上仍是旧版本，游戏启动后以运行中的客户端为准。此时界面不给出「去打一把副本」一类的操作指示。

总结下方是四列等宽的节点（`captureChain_<key>`，值为 `captureChainValue_<key>`，
副行为 `captureChainSub_<key>`）。每列顶部一条 2 px 色条，色条、状态灯（`StatusLed`）与值的颜色同取节点语气：
绿为就绪、灰为等待、橙为校准中或网卡提示、红为 Npcap 缺失或档案不可用；名称 11 px，值 16 px 数字字体，副行 12 px。

| 列 | 值 | 副行 | 来源 |
|---|---|---|---|
| FF14 | `ffxiv_dx11.exe`（固定）/ 未运行 / 待选择 | PID n / 已安装版本 2026.09.01（游戏未运行而版本已知，`Fmt.gameVersionLabel`）/ 启动游戏后自动检测 / 检测到 N 个客户端 | `capture.ffxiv_running` / `ffxiv_process_id` / `game_build`，`App.gameSelection` |
| Npcap | 版本（以数字开头时加「v」；只读得到 libpcap 版本时原样显示 `libpcap x.y.z`，`Fmt.npcapVersionLabel`）/ 已安装（读不到版本）/ 未安装；尚未读到捕获状态时为「—」 | WinPcap 兼容模式 / 驱动缺失 / 未启用 WinPcap 兼容模式 / 仅限管理员使用 / 驱动文件无法加载；尚未读到捕获状态时为「尚未读取」，节点为灰色 | `GetStatus.npcap.version`、`status`，`capture.npcap_*` |
| 网卡 | 正在用的网卡友好名 / 未选择 | 自动选择（有 FF14 连接）/ 手动指定 / 之前选择的网卡收不到游戏数据 | `capture.adapter_id` 对上 `App.captureAdapters[]` 的 `friendly_name`（没有时用 `description`）；对不上任何一行时普通用户看到「已选择」，维护者看到 `adapter_id`，普通用户从不看到不透明的 adapter_id；`captureSettings.adapter_id` 为空即自动 |
| 版本支持 | 普通用户：版本已支持 / 本机校准 / 共享校准 / 待游戏启动（仅版本未知时）/ 校准中 / 待校准（游戏未运行）/ 待核对 / 需要处理 / 检查中 / 未识别版本 / 版本不支持 / 支持文件有冲突 / 未匹配；维护者：`profile_id` | 与游戏版本匹配 / 与已安装的游戏版本匹配 / 进度见下方本机校准 / 启动游戏后重新校准 / 当前版本暂时不会自动记录 / 已安装版本暂时不会自动记录 / 启动游戏后自动检查（仅版本未知时）/ …；维护者：状态令牌 · build | `capture.profile_status` / `profile_origin`、`App.calibration.state`、`App.protocolProfile` |

网卡只有在监听运行时才显示绿点；游戏未运行时，即使采集服务已记住网卡也显示灰点。

**解析**包含小标题「数据读取」与说明「无法识别的数据会跳过，不会据此新增或修改记录」，下方是一行指标：
「成功率」在艾欧泽亚系皮肤用 30 px 标题字体、金色，经典皮肤用 24 px IBM Plex Mono、正文色，
其下是一条迷你柱状线（`captureParseSparkline`：最近 12 次读到的成功率，每 5 秒至多记一次，不足两次时不显示）；
「读取速度」为 `n / s`；「失败 · 重复」为 `a · b`；「最近识别的操作」为本地时间加 `Fmt.eventKindLabel(last_valid_event_kind)`，
例如「21:38:04 副本结算」。未知类型只显示时间，契约要求将未知令牌视为「某个事件」。
`last_valid_event_kind` 的取值含义如下：`CONTENT_FINDER_POP` 匹配成功、
`ZONE_INITIALIZATION` 进入区域（每次换区都会出现，不限于进入副本）、`ZONE_TERRITORY` 识别所在区域、
`DUTY_RESULT` 副本结算、`PLAYER_JOB` 识别职业、`ZONE_LEFT` 离开副本区域、`INSTANCE_LEFT` 退出副本、
`MATCH_CANCELLED` 匹配取消、`MATCH_ANNOUNCED` 匹配成功通知（按出现时机认出的匹配通知，
见 [protocol-profile-format.md](protocol-profile-format.md) §11.5）、`DUTY_COMMENCED` 副本开始、
`DUTY_TIME_REMAINING` 副本剩余时间。

**采集服务未发送的计数一律渲染为 `—`**。`AppController::captureCounters` 只复制实际到达的键，
`last_valid_event_kind` 同样如此，因此界面上的 `0` 必定是实测得到的 0。
`解析成功率` 由 `parse_ok_count / (parse_ok_count + parse_fail_count)` 推导，两者均为 0 时显示 `—` 而非 100%。
采集服务完全没有报告解析计数时（`AppController::parserStatsAvailable` 为假），整行指标不出现，
只显示一句「还没有拿到解析统计……」。

「最近未识别的数据」位于一条分隔线之下，行间有分隔线，最新的一行在上。普通用户看到的每行依次为
64 px 时间与说明；维护者视图在两者之间另有一列 150 px、红色 11 px 的等宽错误码。
普通用户看到的说明来自 `Fmt.parserErrorLabel(code)`，例如「游戏数据的格式不符合预期，已跳过」；
六个 `ParserErrorEntry.code` 各对应一句，未知码有通用句。
普通用户视图**不显示 opcode、方向与采集服务原文**，原文中含有十六进制数值；
最多显示 5 行，超出部分写为「另有 N 条更早的失败未列出，都不会生成记录。」；没有失败时为「最近没有数据读取失败记录。」。
维护者可以看到全部行，说明为「方向 opcode · 采集服务原文」。
模拟后端的示例行下方有橙色提示，说明该行使用模拟数据。

**工具**面板的行与行之间有分隔线，复用 `SettingsRow`。

* 「共享校准证据」（`calibrationEvidenceRow`，说明「先预览公开摘要，再由你提交到 GitHub」）与
  「帮助适配其他区服」（`templateObservationGuideRow`，说明「按步骤采集报文元数据，未适配区服仍不正式记录」）
  在提供 `CalibrationEvidence`（`EvidenceSubmissionController`）时出现。前者的「预览」生成校准证据的公开摘要，
  只有用户在预览中确认后才复制摘要并打开固定的 GitHub 表单；后者的「采证步骤」展开或收起下文的新区服采证步骤。
* 「导出诊断报告」一行包含说明「不含游戏内容、网络地址与账号」（维护者视图为 `ExportDiagnosticsReport` 的技术说明）
  与按钮「导出」（`exportDiagnosticsButton`），发送 `ExportDiagnosticsReport`（contracts/CHANGELOG.md 第 16 条）。
  报告由**当前正在运行的**采集服务写成本地 JSON，成功后 toast 给出路径与字节数。
  目标路径必须位于 `%USERPROFILE%` 或 `%LOCALAPPDATA%` 之内，否则采集服务返回 `ERR_EXPORT_FAILED`，
  桌面端原样显示其文案。在文件对话框中取消时不发送任何请求。
* 「离线回放测试样本」（`offlineReplayRow`，仅维护者可见，只有说明没有按钮）显示
  「使用 --replay 回放样本；默认写入独立临时数据库，重复回放同一样本不会重复新增。」，因为 `ipc-v1` 没有回放消息。
* 本页**没有「诊断模式」开关**（决策 3：`Settings.diagnosticsMode` 不产生任何行为）。
  每条导随的脱敏事件行仍在「历史记录 → 事件摘要」中读取（`GetRunEvents`）。
* 「后台服务提示」（`collectorWarningsList`）：`GetStatus.warnings` 非空时，在面板底部显示一列橙色小字。

**新区服采证步骤**（`templateObservationGuideCard`）与维护者的桌面验证取证是同一个组件（`CaptureValidationEvidenceCard.qml`），
以 `guidedMode` 面向玩家：小标题「新区服采证步骤」，三步说明，按钮「开始采证」（捕获运行中为「切换为采证」，
进行中为「停止采证」，等待中为「取消等待」）与「预览候选摘要」（采证已保存且已停止时可用）。
普通用户（未打开维护者工具）看到的网卡下拉框只列网卡名称，推荐的一项写作「<名称> · 推荐」，不出现 `adapter_id`；
采集服务给出错误码时只显示其说明句，没有说明句时显示「操作没有完成，原因不明。」，从不显示「错误码：…」；
采证文件写作「采证文件：<路径>」（路径折叠到 `%USERPROFILE%`），不显示 SHA-256。
捕获运行中、协议档案为 `VERIFIED` 时，橙字说明「当前正在正式记录，不能切换为候选采证。……」；
「当前正在正式记录；以下为最近一次验证取证证据。」同样只在档案为 `VERIFIED` 时出现。
打开维护者工具后，这张卡片与维护者视图一样显示全部技术细节（`technical`）。

**维护者工具**（`captureMaintainerSection`）位于页面最下方，包含小标题「维护者工具」与一条分隔线，
其行为与改版前一致：

1. 桌面验证取证（`validationEvidenceCard`）：适配器与区服覆盖、五个验证标记（`validationMarker_<code>`，启动自检的键盘检查依赖它）、
   取证计数与 trace 路径（折叠到 `%USERPROFILE%`）。
2. 候选档案验证与研究白名单（`CandidateValidationCard`）。
3. 网络适配器表（`captureAdapterTable`，`ListCaptureAdapters`）：友好名、描述、已由采集服务掩码到 /24 的 IPv4、回环 / 推荐标记、up 状态点。
   桌面端从不持有完整地址。
4. 指标格（`captureMetricGrid`，`components/CapCell.qml`，十二格，原始令牌照常显示）：

   | 标题 | 值 | 副标题 | 来源字段 |
   |------|----|--------|----------|
   | Npcap | 版本（规则同连接状态的 Npcap 列）/ `v?`（读不到版本）/ 未安装 | `status` · WinPcap 兼容 · 仅管理员 | `capture.npcap_*` + `GetStatus.npcap` |
   | 适配器 | `adapter_id` / 未选择 | 可用 N 个 · 未开始捕获时不占用任何网卡 | `capture.adapter_id` + `ListCaptureAdapters` |
   | FF14 PID | 进程号 / 未运行 | 实例 N · build … · region … | `capture.ffxiv_*` + `GetStatus.game` |
   | 捕获状态 | 监听中 / 已停止 / 降级监听 / 无有效报文 / … | 自 hh:mm:ss / 需回到标题画面重新登录 | `capture.state` |
   | 链路已观察报文 | `packets_observed` | 队列 x/y · 丢包 N | `capture.packets_*` / `queue_*` |
   | 消息速率 | n 条/秒 | 解码 · 解码失败 · 连接 | 计数字段 |
   | 解析成功率 | 百分比 | 成功 · 失败 · 去重 · 与记录无关 | 计数字段 |
   | 最近有效事件 | 时间 | kind 令牌 | `last_valid_event_*` |
   | 最近错误码 | `last_error_code` / 无 | `UNAVAILABLE` 时说明本机不具备前提条件 | `capture.last_error_code` |
   | 协议档案 | `profile_id` / 无 | status … · build … | `GetProtocolProfileStatus` |
   | Oodle 签名 | 签名来源 | 档案 · 状态 | `capture.oodle_*` |
   | 边界常量 | `monitor_type` | 注入式 hook 否 · 读取游戏可执行文件 是/否 | `capture.monitor_type` / `injected_hook_enabled` / `reads_game_executable` |

   「边界常量」是用户在运行时**唯一可自行验证边界**的位置（DEC-OODLE-01）。
5. 等宽状态块（`liveCaptureStatusBox`）**始终**打印以下内容：

   ```
   LIVE_CAPTURE_STATUS = VERIFIED_POP_TO_EXIT
   PROTOCOL_PROFILE_STATUS = <后端上报，mock = SYNTHETIC_ONLY>
   OODLE_MODE = <GetStatus.oodle_mode，本机实测 FfxivTcp>
   READS_GAME_EXECUTABLE = <GetStatus.reads_game_executable>
   PUBLIC_DISTRIBUTION_READY = true
   ```

   `LIVE_CAPTURE_STATUS` 与 `PUBLIC_DISTRIBUTION_READY` 是编译期常量
   （`AppController::liveCaptureStatus()` 与 `publicDistributionReady()`），
   不会因为捕获正在运行而变为 `RUNNING`，参见 `docs/live-validation-guide.md`。
   `profile_status` 仍为契约枚举，mock 返回 `UNVERIFIED`。
   `profile_status_label` 是后端可选的、更精确的标签，mock 返回 `SYNTHETIC_ONLY`，维护者视图优先显示后者。

维护者的「开始捕获」**不**依据缓存的 `npcap_installed` 提前拒绝。请求照常发出，
采集服务返回的 `ERR_NPCAP_MISSING` 文案（含安装指引）放入 toast。

本页可用的 Mock 开关：`--mock-recording-state listening|waiting`（正常 / 游戏未运行）、
`--mock-npcap-missing`、`--maintainer-tools`、`--mock-midstream`、`--mock-calibration`、`--mock-shared`。
模拟后端的 `GetStatus` 同样带有 `npcap` 对象，与 `CaptureWire.StatusExtras` 同形。
QtTest `MentorRecorderCapturePage`（`tests/Desktop.Tests/CapturePageTests.cpp`）覆盖：
标题行的两种身份、连接状态的各种情况（含尚未读到捕获状态与断线前保留的故障）、新区服采证步骤的玩家视图、
最近有效事件的已知与未知类型、没有解析计数时的说明，
以及普通用户页面不出现 opcode、`0x`、`ERR_` 与维护者文案。

#### 4.4.1 本机校准（0.3.0）

游戏更新后没有匹配档案时，Collector 进入本机校准。
此时界面只增加三处内容，全部面向玩家，且不置于「维护者工具」之后：

* **校准行**（`calibrationCard`，位于捕获页首张卡片之下，`capture.calibration.state != IDLE` 时显示）：
  左边的本机校准面板（`calibrationLocalPanel`）是小标题「本机校准」加一句进度说明（`calibrationProgressContext`）：
  读到进度时为「已识别 N / 4 项 · 进度会保留，显示的是已识别的步骤，不是当前游戏状态」（只数排本、匹配、进本、出本四项必需证据），
  采集服务没有报告进度时为「退出游戏或重启软件不会清空进度。以下是已识别的步骤，不是当前游戏状态。」；
  下面是五步进度线 排本 / 匹配弹窗（按排本推断记录时为「匹配成功」）/ 进本 / 出本 / 职业
  （`calibrationProgress_<key>`：圆圈 ✓ 已识别、! 看到了相近证据、空圈尚未识别；步名
  `calibrationProgressLabel_<key>`，状态 `calibrationProgressText_<key>`，取自 `calibration.progress`；
  面板窄于 400 px 时改为上下排列），再是「最近检测到登录：…」「最近检测到进本：…」（`calibrationEvidenceTimes`）
  与「下一步」（已有档案在记录时为「接下来」；`calibrationBlockers`：`blockers[]` 逐句原样显示，
  Collector 写入的即是面向玩家的完整句子；读不到游戏数据时改为本卡片自己的一句橙字说明）。
  状态本身的那句话（`calibration/CalibrationCopy.qml`）由首张卡片说：`READY` 时为
  「还差最后一步：核对 N 件事，就能开始自动记录。」；若此时已有档案在记录
  （共享档案在用、`profile_status = VERIFIED`，或 `calibration.local_profile_id` 非空，例如按排本推断
  或缺职业的本机档案），改为「记录照常进行。再核对 N 件事，之后的记录会更准确。」（`recordingBesideOffer`）；
  观察中为「游戏更新了，正在重新校准」，游戏未运行时为「启动游戏后会重新校准」，受阻时为「校准遇到了问题，暂时不能自动记录。」，
  完成后为「校准完成，已经可以正常记录」，按排本推断记录时为「已经可以自动记录，后台仍在完善校准。」；
  已有档案可以记录而游戏未运行时为「启动游戏后会自动记录」。
  单独加载卡片（测试）时这句话显示在卡片顶部（`calibrationHeadline` / `calibrationExplanation`）。
  面板有两个按钮：**核对并启用**（`calibrationConfirmButton`，仅在 `READY` 时出现，打开核对对话框）与
  **清空校准进度**（`calibrationDiscardButton`，校准完成后不再出现；点击即发送 `DiscardCalibration`，
  清空尚未完成的校准进度，已保存的记录与正在使用的校准不受影响）。
* **核对对话框**（`calibrationDialog`，标题「核对这几件事」）按时间列出 `calibration.events`，每行为 `time_text` 与 `label`，
  登录与换区行灰显且没有按钮。每条 `requires_confirmation` 的事件带一对 **对 / 错** 按钮
  （`calibrationVerdictCorrect_<event_id>` 与 `calibrationVerdictWrong_<event_id>`）。
  带随机任务编号的事件点「错」后，下方追问「你当时排的是」（`calibrationRelabelChoice_<event_id>`），
  选项为游戏内的随机任务与「这个时间我没有排本」。尚未选择时下拉框显示「请选择…」，不预选任何一项，
  按钮旁以灰字提示「请在“你当时排的是”里选出实际排的随机任务。」（`calibrationDialogNameHint`），
  「核对并启用」（`calibrationDialogConfirm`）保持停用。选出随机任务记为改名（`RELABEL`，校准继续），
  选「这个时间我没有排本」记为错（`WRONG`）。全部回答之后方可点击「核对并启用」，发送 `ConfirmCalibration`；
  「以后再说」关闭对话框，请求在途时它与 Esc 都不可用。
  成功后对话框关闭并刷新。应答的 `bound_in_session` 为假时（采集服务没有当场换上新档案，
  例如这一把正在进行），另以 toast 提示「校准已保存，从下一次副本开始使用。本次副本的记录方式不变。」
  返回 `ERR_CALIBRATION_REJECTED` 时显示
  「有内容与你的实际游戏情况不符，本次校准需要重来。请照常完成随机任务，之后会再次提醒你核对。」并关闭，
  该句同时保留在卡片上（`App.calibration.error`）。这句话与其他失败说明在校准状态不变、或回到观察
  （`OBSERVING` / `WAITING`）时保留；状态改为其他任何状态（下一份待核对的草稿、完成、空闲或受阻）
  或游戏版本改变时清除。
* **横幅与侧栏**：`AutomaticRecordingController` 提供 `calibrating`、`calibration_ready` 与
  `calibration_blocked` 三种投影，均只显示主窗口右下角的橙色提示卡，**不**弹出「无法自动记录」对话框，
  因为补丁日的档案不匹配属于预期情况，而非故障。提示卡文案在观察中为
  「游戏更新了，正在重新校准。请照常完成随机任务，再按提示核对。完成前暂时不能自动记录。」；
  就绪时为「还差最后一步：核对 N 件事，就能开始自动记录。」并附「核对并启用」按钮；
  阻塞时为第一条 blocker 加「请查看「捕获诊断」。」（没有 blocker 时为「校准遇到了问题，暂时不能自动记录。请查看「捕获诊断」。」）。
  提示卡另有「查看诊断」与「收起」。侧栏「捕获」一行显示 校准中 / 待核对 / 无法自动记录。
* **设置页**提供开关「游戏更新后自动校准」（`autoCalibrationToggle`，写入
  `UpdateCaptureSettings.auto_calibration_enabled`，由 Collector 持有，默认开启）。
* 数据流：每次 `GetStatus`、`GetCaptureStatus` 与 `CaptureStatusChanged` 都将 `capture.calibration`
  写入 `App.calibration`。`calibration_changed` 事件只触发一次 `GetCaptureStatus`，
  不刷新正式视图，也不播报。
* Mock：`--mock-calibration observing|ready|blocked|done|idle`，不开启维护者工具；
  `idle` 表示上一次校准已完成、重启之后由本机档案直接记录，此时校准卡片不出现。
  截图目标为 `MentorRecorderQmlCalibration_{observing,ready,blocked,done}` 与 `MentorRecorderQmlCalibrationBanner`。
  玩家页禁词表不变，新增文案中不得出现 opcode、`0x`、契约枚举与 `ERR_`。

#### 4.4.2 共享校准（0.7.11 之后的版本）

采集服务自 0.7.11 起获取并核实共享校准，桌面端界面在此之后提供。
本节内容全部面向玩家。句子由 `SharedCalibrationController`（`App.calibration.shared`）在 C++ 侧拼装，
并已通过词表检查，QML 只负责摆放句子与按钮。

**校准行右边的「共享校准」面板**（`SharedCalibrationSection.qml`，`calibrationSharedSection`）与本机校准面板并排。
`calibration.shared` 缺失时（旧版采集服务）整节不显示。
显示内容先按 `user_rejected` 判定，再看共享档案是否在用（`profile_origin = SHARED_CALIBRATION`），最后按 `phase` 判定。
面板自上而下为小标题「共享校准」、一句标题（`sharedCalibrationHeadline`）、一句灰字说明（`sharedCalibrationDetail`）、
征求同意时的橙框、分享说明与按钮行。按钮行中，「立即检查」与分享按钮直接可见；手动导入与改为本机校准两项备用操作
收在「其他方式」（`sharedOtherWaysButton`，展开后为「收起其他方式」）之后，展开后为
「手动导入校准码」（`sharedImportButton`）与「改为本机校准」（`sharedRejectButton`），阶段改变时重新折叠。

| 情况 | 标题 | 灰字说明 | 按钮 |
|---|---|---|---|
| `FETCHING` | 正在查找其他玩家分享的校准，本机校准也会继续。 | 无 | 其他方式：手动导入校准码 |
| `VERIFYING`，未被拒绝的候选全为 `provenance = IMPORTED` | 校准码已导入。登录后申请随机任务并进入副本，通过检查后才能开始记录。 | 检查通过后才能开始自动记录。照常游戏即可；如果不能使用，会继续本机校准，已有进度会保留。 | 其他方式：手动导入校准码、改为本机校准 |
| `VERIFYING`，某个未被拒绝的候选的 `CONTENT_FINDER_POP` 判据为 `gate = AUDIT`（仓库发布的码，且本机没有可用校准、未被标为有分歧、没有提交人数更多的候选） | 已找到共享校准，等待本机验证。 | 无需手动导入。登录游戏时会自动检查，检查通过后才能开始自动记录。如果不能使用，会继续本机校准，已有进度会保留。 | 同上 |
| `VERIFYING`，未被拒绝的候选的该判据全为 `gate = REQUIRED`（或为手动导入的码） | 已找到共享校准，等待本机验证。 | 无需手动导入。请申请随机任务并进入副本，检查通过后才能开始自动记录。…… | 同上 |
| `VERIFYING`，候选未报告 `provenance` 或判据的 `gate`（1.1.0 之前的采集服务，或候选尚未判定） | 已找到共享校准，等待本机验证。（候选全是手动导入的：校准码已导入。登录或排本时会自动检查是否可用。） | 无需手动导入。登录或排本时会自动检查，……（候选全是手动导入的：检查通过后才能开始自动记录。……） | 同上 |
| `VERIFYING`，且 `profile_status = VERIFIED`（已有档案在记录） | 找到了更完整的共享校准，正在检查是否可用。自动记录照常进行。（候选全为 `IMPORTED`：校准码已导入，正在检查是否可用。自动记录照常进行。） | 自动记录照常进行。检查通过后会自动换用新的共享校准，已保存的记录不会改动。 | 同上 |
| `AWAITING_CONSENT` | 共享校准已通过检查，请确认是否使用。 | 无；橙框内说明代价（`consentText`），末句为「如果你有其他玩家发来的校准码，也可以在「其他方式」中手动导入校准码。」 | 同意，开始记录（橙框内）；其他方式：手动导入校准码、改为本机校准 |
| `VERIFIED`，或 `profile_origin = SHARED_CALIBRATION` | 正在使用共享校准，已通过本机检查。 | 已确认这份共享校准能在你的游戏中使用，照常游戏即可。 | 其他方式：改为本机校准 |
| 同上，且 `audit_pending = true` | 正在使用共享校准，登录检查已通过。 | 排本和进本还在核对中，记录照常进行。如果之后发现不匹配，会停止使用这份校准并继续本机校准，期间生成的记录会标为“待复核”，请检查并补正。 | 其他方式：改为本机校准 |
| `REJECTED` | 这份共享校准不适用于你的游戏，已改为本机校准。 | 本机校准会继续，已有进度会保留，照常游戏即可。 | 立即检查；其他方式：手动导入校准码 |
| `UNAVAILABLE` | 暂时没能下载共享校准，将继续本机校准。 | 稍后可点「立即检查」重试。也可以向已完成校准的玩家索取校准码，在「其他方式」中手动导入。 | 同上 |
| `NONE` 且 `last_fetch_status = NONE_FOR_BUILD` | 暂时没有当前游戏版本的共享校准，将继续本机校准。 | 本机校准完成后，可以点「分享给其他玩家」，让同一游戏版本的玩家也能使用。 | 同上 |
| `NONE` 且 `last_fetch_status = DISABLED` | 自动获取共享校准已关闭，将继续本机校准。 | 想自动查找共享校准，可在设置中开启「游戏更新后获取其他玩家的共享校准」。也可以在「其他方式」中手动导入校准码。 | 同上 |
| `NONE`，尚未获取过 | 无 | 有其他玩家发来的校准码，可以在「其他方式」中手动导入。通过检查后才会使用。 | 同上 |
| `user_rejected = true` | 已改为本机校准，当前游戏版本不再使用共享校准。 | 校准仍在进行时：想重新使用共享校准，点「清空校准进度」。本机尚未完成的校准进度也会一起清空。 | 无 |
| `profile_origin = LOCAL_CALIBRATION`（校准卡片仍显示时，即刚完成校准的那一次） | 一行分享说明（浏览器打开 GitHub 分享页、复制校准码、软件不上传） | — | 分享给其他玩家 |
| `profile_origin = SHARED_CALIBRATION`，首个候选 `status = PROVEN`、`audit_pending` 为假、`provenance = PUBLISHED`，且带 `preferred` 字段（更早的采集服务对这种档案拒绝给码）（共享档案已在本机完整记录过一次，`shareKind = verified`） | 灰字「会打开 GitHub 页面，把这份校准记为你也核实过，让其他玩家的软件更优先使用它。」；应答 `basis = VERIFIED` 时 toast 说明提交后这份共享校准会多一个核实通过的账号，`SHARED_NOT_PROVEN` 时说明还在核对中；候选的 `disputed`、`preferred`、`shape_prior` 不显示 | — | 这份校准好用，告诉其他玩家（同一个 `share()`；校准卡片不在时由协议档案卡提供，规则同上） |

「立即检查」（`sharedCheckButton`）只在获取没有结果的阶段（尚未获取、已关闭、下载失败、没有当前版本的、
被本机流量否定）出现；「手动导入校准码」不限阶段。两者都要求本机仍处于校准（`WAITING` / `OBSERVING`）、
没有共享档案正在记录，且用户没有改为本机校准。「改为本机校准」在核实中、待同意与已使用三个阶段出现。本机保存的共享校准记录暂时读不到（文件被其他程序占用）时，「立即检查」的回答仍是 `NOT_NEEDED`，但附带说明文字（`sharing.check.state_unreadable`），界面显示该说明而不是「暂时不需要查找」。

**「分享给其他玩家」同时出现在协议档案卡上**（`protocolProfileCard`，即校准行下方的协议档案一行），
只要具备分享条件即会出现。校准卡片仅在 `calibration.state != IDLE` 时出现，
而重启软件之后本机档案直接生效，校准不再处于观察状态，校准卡片随之消失。
因此，正在使用的档案由本机校准产生（`profile_origin = LOCAL_CALIBRATION`），或是上表中可以告诉其他玩家的共享档案（`shareKind = verified`），
且采集服务支持共享校准时，协议档案卡上增加一行分享说明与一个 `protocolShareButton`。
该按钮与校准卡片上的 `sharedShareButton` 调用同一个 `App.calibration.shared.share()`，
按钮文字与说明句也取自同一处（C++ 的 `shareButtonText` 与 `shareHintText`，随 `shareKind` 变化）。
**校准卡片存在时该按钮不出现**：校准卡片正在说明刚完成的校准，分享入口放在该卡片上更为连贯，
同一屏内不应出现两个功能相同的按钮。
「手动导入校准码」不随之迁移，因为采集服务只在校准进行中才接受导入。

**「重新校准」也出现在协议档案卡上**（`protocolRecalibrateButton`，次要按钮），
条件是 `profile_status = VERIFIED` 且 `profile_origin = LOCAL_CALIBRATION`——
本机档案生效后校准卡片消失，「清空校准进度」与「手动导入校准码」也随之失去入口；
怀疑本机识别错了报文、或拿到了更好校准码的玩家，经由这个按钮重新校准或导入校准码。
点击先打开确认框（`protocolRecalibrateDialog`，沿用 `DialogFrame`）：
标题「重新校准这一版游戏？」，正文「将停止使用当前的本机校准，并重新开始校准。旧校准会保留，之后可以恢复。
重新校准完成前暂时不能自动记录；也可以导入校准码，通过检查后使用。已保存的记录不受影响。」，
按钮 **取消** 与 **停用并重新校准**（`protocolRecalibrateConfirm`）。
确认后发送 `DiscardCalibration` 并带上 `retire_local_profile = true`，随即重读一次捕获状态。
`App.currentRunState` 为 `MENTOR_MATCHED` 或 `ENTERED_DUTY` 时按钮与确认框中的确认按钮都停用，
已打开的确认框随之关闭，按钮旁给一行灰字「副本进行中，结束后再试」（`protocolRecalibrateHint`）——
停用档案会把这一把按停止捕获收尾。采集服务同样拒绝这一时段的请求：带 `retire_local_profile` 或 `restore_local_profile`
的 `DiscardCalibration` 在记录处于 `MENTOR_MATCHED` 或 `ENTERED_DUTY` 时以 `ERR_CALIBRATION_NOT_READY` 拒绝，说明为
「正在进行的这一把导随还没结束，现在更换校准会中断这一把的记录。请在这一把结束后再试。」（文字目录键
`calibration.change_during_run`）；不带这两个字段的「清空校准进度」不受此限制。
按排本推断匹配的档案保留着一条仍在匹配窗口内的排本申请（玩家已在排队、还没有记录）时，采集服务同样拒绝带这两个字段的请求，
说明改为「你正在排导随，现在更换校准会让接下来这一把记不下来。请等这一把结束后再试。如果已经取消排队，软件可能察觉不到，请退出并重新启动游戏后再试，或在开始排队一小时后再试。」
（`calibration.change_while_queued`）；桌面端的停用条件只看 `App.currentRunState`，排队期间按钮仍可点击，玩家看到的是采集服务的这句拒绝。
共享档案由共享校准面板的「改为本机校准」停用，随包档案与「没有档案」都不出现此按钮。
行为见 [protocol-profile-format.md](protocol-profile-format.md) §11.6。

**「恢复上一份本机校准」**（`protocolRestoreButton`，同一行、同一张卡）是它的撤销：
`calibration.retired_local_profile_available` 为真时出现，即被停用的那份档案还在磁盘上、
且当前没有本机档案生效。它与「重新校准」互斥——一个要求有本机档案生效，另一个要求没有——
同屏只会出现其中之一。确认框（`protocolRestoreDialog`）标题「恢复上一份本机校准？」，
正文「将恢复你上次停用的本机校准。如果可以使用，软件会用它替换当前的校准来记录。如果当前使用的校准优先级更高（例如软件自带的版本），则继续使用当前校准，恢复的这份留作备用。已保存的记录不受影响。」，
按钮 **取消** 与 **恢复**（`protocolRestoreConfirm`），确认后发送
`DiscardCalibration` 并带上 `restore_local_profile = true`；应答到达后，不论成功还是被拒，都重读一次捕获状态。
副本进行中时按钮与确认按钮同样停用、已打开的确认框随之关闭，并复用「重新校准」那一行灰字。
停用之后校准卡片会重新出现，若不把这条退路算进协议档案卡的可见条件，整张卡会被隐藏、
按钮也就无法使用，因此 `profileCardVisible` 把它一并计入。
采集服务拒绝时（没有可恢复的、同名档案已存在、恢复后无法通过校验，或副本正在进行、正在排队），它给的中文句子
原样显示在卡片的 `protocolCalibrationError` 一行——此时校准卡片未必在场，这是唯一的说明位置。
恢复的档案因优先级较低而未被采用时，采集服务的应答说明档案已恢复，以及为何继续使用现有校准，
在用的校准不被停用（[protocol-profile-format.md](protocol-profile-format.md) §11.6.1）。
这一应答与真正的拒绝使用同一个错误码，桌面端不解析句子，而是按重读到的状态区分：
`retired_local_profile_available` 仍为真，说明什么都没有放回，句子留在 `protocolCalibrationError`；
已为假，说明档案已经放回，句子改以灰色说明文字显示在 `protocolCalibrationNotice` 一行，
恢复按钮随状态消失，协议档案卡在这行说明显示期间保持可见。
下一次校准请求发出，或在用档案的状态、来源或游戏版本改变时，这行说明清除。

* `last_refusal`、候选的 `sha12`、`criteria` 与 `last_index_attempts` 都不显示在卡片上，
  它们属于脱敏诊断报告的内容。
* **共享档案正在记录**：在共享档案完整记录一次进出副本之前，采集服务继续观察校准事件，
  `calibration.state` 仍为 `OBSERVING`。此时本机校准面板隐藏，共享校准面板占满整行；
  首张卡片的标题为「正在用其他玩家分享的校准自动记录。」，共享校准面板不再重复标题与「已通过本机检查」时的说明，
  采集服务的说明句（`blockers[]`）以灰字列在面板中（`sharedRetainingText`）。侧栏「捕获」显示「自动监听中」。
* **绑定后仍在核对（`audit_pending = true`）**：核实门槛按来源分级之后，仓库发布的校准只在本机没有可用校准、
  未被仓库标为有分歧、且没有提交人数更多的候选时，才在登录簇核实通过即开始记录，排本与进本改为绑定后审计；
  其余情况下排本与进本须先核实通过。此时首张卡片的标题改为
  「正在用其他玩家分享的校准记录，排本和进本还在核对。」；共享校准面板的灰字说明照常显示（这是唯一说明该状态的地方），
  内容即上表中「登录检查已通过」一行的说明。按钮、面板布局与侧栏与上一条相同，审计期间不弹任何对话框。
  旧采集服务不报 `audit_pending`，桌面端按"未报告"处理，措辞与上一条一致。
* **同意提示**：代价说明与本机临时档案的说明共用 `CalibrationController.queueInferenceText`。
  `AWAITING_CONSENT` 期间提示卡改为
  「共享校准已通过检查。请在「捕获诊断」的「共享校准」中阅读说明，点「同意，开始记录」。」，
  仍然只显示橙色提示卡，不弹出对话框。
* **手动导入校准码对话框**（`sharedImportDialog`，标题「手动导入校准码」）包含一个文本框与「取消」「导入」两个按钮。
  `ImportCalibrationCode` 返回的 `message` 原样显示在文本框下方；返回 `APPLIED` 时对话框关闭，
  同一句话进入 toast。文本为空时不发送请求，提示「请先粘贴完整的校准码。」。
  采集服务的这句话本身区分"命中公开仓库索引"与"未发布"（对应应答中的 `provenance`），
  以及 `reason = REVOKED` 的"已被撤回"，桌面端不改写、不追加；
  只有 `message` 为空时才退回桌面端自己的兜底句。
* **改为本机校准的确认框**（`sharedRejectDialog`，标题「改为本机校准？」）说明将停止使用或取消检查哪份共享校准、
  此后当前游戏版本不再使用共享校准，以及撤销该选择的办法（点「清空校准进度」）；按钮为「取消」与「改为本机校准」
  （`sharedRejectConfirm`），确认后发送 `RejectSharedCalibration`，toast 按应答说明是停止使用了在用的共享校准、
  取消了正在进行的检查，还是只记下了这一选择。共享档案正在记录且有副本进行中（`App.currentRunState` 为
  `MENTOR_MATCHED` 或 `ENTERED_DUTY`）时，「改为本机校准」与确认按钮都停用，已打开的确认框随之关闭，
  展开的「其他方式」中给一行灰字「副本进行中，结束后再试」（`sharedRejectInFlightHint`）：
  停用正在记录的档案会把这一把按停止捕获收尾。共享校准尚未用于记录（核实中、待同意）时不受此限制。
  采集服务对 `RejectSharedCalibration` 执行同一规则：在用的档案来自共享校准、且记录处于 `MENTOR_MATCHED` 或
  `ENTERED_DUTY` 时，以 `ERR_CALIBRATION_NOT_READY` 与上文「重新校准」的同一句说明拒绝；该档案按排本推断匹配、
  保留着一条仍在匹配窗口内的排本申请时同样拒绝，说明为上文排队时的那一句（`calibration.change_while_queued`）。
  排队期间桌面端不停用这两个按钮，拒绝的说明以 toast 显示。
* **分享**：`GetCalibrationShareCode` 成功后先将 `code` 复制到剪贴板。
  仅当 `code_in_url = true` 且地址位于 `https://github.com/` 之下（不含账号、不含端口）时，
  才交给 `QDesktopServices::openUrl`；否则只复制并在 toast 中说明，`code_in_url = false` 时的说明请对方
  「在「其他方式」中点「手动导入校准码」并粘贴」。
  `ERR_SHARE_CODE_UNAVAILABLE` 按 `details.reason`（`NO_PROFILE` / `NOT_LOCAL` / `SHARED` / `SHARED_NOT_PROVEN` / `NOT_SHAREABLE`）
  各给出一句说明，不显示采集服务的原文，原文中可能含有路径。
  为此 `BackendReply` 提供 `errorDetails()`，由 `IpcClient` 传递错误信封中的 `details`。
* 每个请求的结果都以一条 toast 呈现。改变共享状态的请求之后，
  `AppController::rereadCaptureStatus()` 读取一次 `GetCaptureStatus`，与 `calibration_changed` 事件走同一条路径。
* 桌面端自身不联网（`NET-006` 禁止使用 `QNetworkAccessManager`），网页一律交由系统浏览器打开。
* Mock：`--mock-shared fetching|verifying|consent|verified|verified-auditing|imported-published|imported-unpublished|rejected|unavailable|user-rejected|none-for-build|share`，
  截图目标为 `MentorRecorderQmlSharedCalibration_<state>`。
  其中 `verified-auditing`、`imported-published`、`imported-unpublished` 三个状态对应核实门槛按来源分级：
  分别为登录时已核实、排本与进本仍在核对；导入后命中索引；导入后未命中任何索引。
  带 `provenance` 的候选同时带判据的 `gate`（`PUBLISHED` 为 `AUDIT`，`IMPORTED` 为 `REQUIRED`）。
  其余状态不带 `provenance`、`gate` 与 `audit_pending`，用于覆盖旧采集服务的"未报告"分支。
  `verified-proven` 为共享档案已完整记录过一次（首个候选 `PROVEN`，校准 `DONE`），校准码以 `basis = VERIFIED` 给出，截图另有 `--mock-calibration idle` 的 `MentorRecorderQmlSharedCalibration_verified_proven_after_restart`。
  重启之后的分享入口使用 `--mock-calibration idle --mock-shared share`，
  截图目标为 `MentorRecorderQmlSharedCalibration_share_after_restart`。
  QtTest 包括 `MentorRecorderSharedCalibration`（控制器、接线与 Mock）与
  `MentorRecorderSharedCalibrationCard`（真实 QML 的卡片、两个对话框，以及捕获页协议档案卡上的分享入口）。

### 4.5 设置（page 6）

本页自界面改版 P4a（2026-09）起按原型分栏：左侧为 **168 px 子导航**，右侧为一列面板。
面板最大宽度 760，紧贴子导航左对齐，单独滚动；页头与子导航不随之滚动。
子导航项复用 `NavItem`，不带序号，标签下增加一行小字 `sub`，对应原型的 `.navi.set-navi`；
选中样式跟随当前界面风格：

| 分页 | `id` | 小字 | 面板 |
|---|---|---|---|
| 通用 | `general` | 语言 · 启动 · 记录 · 外观 | 语言 / 启动与托盘 / 记录 / 校准 / 更新 / 外观 |
| 播报 | `tts` | 语音与模板 | 本地语音播报 / 在线语音（只在选中在线语音时）/ 播报模板 |
| 成就 | `goal` | 目标与基数 | 成就进度 |
| 数据 | `data` | 数据库与备份 | 数据库 |
| 关于 | `about` | 版权 · 隐私边界 | 导随记录器 / 版权与来源 |

所选分页在本次运行内保留（`SettingsPage.currentTab`）。页面只创建一次，切换分页只改变可见性。
五个分页均常驻，因此下文列出的 objectName 始终可以定位。
`--settings-tab general|tts|goal|data|about` 使截图直接打开指定分页，见 §7。

本页使用的组件如下。`SettingsPanel` 对应 `.panel`：行面板 `padding: 8px 24px`，
kicker `padding: 12px 0 4px`；`freeLayout` 面板 `padding: 20px 24px`，间距 12。
`SettingsRow` 对应原型的 `grid-template-columns: 1fr auto; padding: 12px 0`：
左侧为 600 字重标签与小字，右侧为控件，下方有分隔线，面板最后一行不绘制分隔线。
`SettingToggleRow` 是控件为开关的 `SettingsRow`。
`Card` 为此增加了 `horizontalPadding` 与 `verticalPadding`，二者默认均等于 `padding`。

**通用**

* **语言**（`languageSettingsCard`，位于最上方）：kicker 为「语言 · Language」（英文界面为「Language」），
  保留英文单词，使误选了看不懂的语言的玩家也能找回此处。唯一一行「界面语言」，小字「界面文字和播报语音一起切换」，
  右侧为四段分段控件（`uiLanguageSettingControl`）：简体中文 / 繁體中文 / English / 日本語，
  每个选项以其自身语言书写，并以该语言的字形绘制（`Lang.displayFamily`）。
  点选即切换并写入 `desktop.ini` 的 `ui/language`，**不需要重启**，见 §4.5.3。
* **启动与托盘**（`generalSettingsCard`）：开机启动、随 FF14 自动开始捕获
  （`followGameToggle`，仅维护者可见，写入 `UpdateCaptureSettings.follow_game`）、关闭时最小化到托盘。
* **记录**：「结束后询问本次结果」（`Settings.confirmPrompt`，默认开启）、「通关后提醒记笔记」
  （`Settings.reflectPrompt`，默认开启），以及「提醒方式」（`promptPresentationControl`，`Settings.promptPresentation`，
  INI 键 `general/promptPresentation`）：独立小窗（`window`，默认）/ 浮窗提示（`floating`）/ 仅保留待办（`inbox`），
  小字为「独立小窗不抢键盘焦点；浮窗关闭时只保留待办。关闭提醒不会删除草稿。」。三种方式的行为见 §4.5.2。
* **校准**（原型中没有该面板，见改版方案 §3.1）：游戏更新后自动校准（`autoCalibrationToggle`）、
  **游戏更新后获取其他玩家的共享校准**（`sharedCalibrationToggle`，写入
  `UpdateCaptureSettings.shared_calibration_enabled`，由采集服务持有，默认开启）。
  后者的描述写明这是默认开启的那一种联网请求，且不携带任何可识别玩家的信息，见 §4.4.2；
  另一种联网请求是默认关闭的在线语音，见下文的「播报」。
  `App.captureSettingsError` 以橙色显示在该面板底部。
* **更新**：开关「检查新版本并提示」（`updateCheckToggle`，写入
  `UpdateCaptureSettings.update_check_enabled`，由采集服务持有，默认开启）。
  描述写明这是默认开启的联网请求：每天最多一次，只从本项目的 GitHub 发布页读取一个只含版本号的小文件，
  有新版本时在总览页提示；请求不带账号、安装编号或任何可识别信息；只有在用户点「下载安装包」之后，
  后台进程才会从本项目的发布页下载安装程序并与发布时公布的校验值核对，安装还要再点「立即安装」；
  本软件从不自行下载或安装新版本，见 [privacy-boundary.md](privacy-boundary.md) §8.4、§8.6。
  已检查过时，面板下方以小字给出「最近检查：<时间> · 最新版本 <版本号>」（`updateCheckStatusText`）。
  该行右侧是「检查更新」（`checkUpdateNowButton`）：点击发出 `CheckUpdateNow`，
  采集服务随即检查一次，不受每日节流限制，结果以一句提示说明；已有一次检查正在进行（定时的或另一次请求发起的）时，
  采集服务等待那一次的结果，不重复检查。有可下载的新版本时（`App.update.downloadPhase` 非空）
  同一位置改为随下载阶段变化的按钮，文字与作用和总览横幅的主按钮相同（「下载安装包」「取消」「立即安装」「重试」，
  或所连接的采集服务不支持软件内下载时的「下载新版本」，见 §4.1），这时它在请求尚未返回或桌面端正在校验时停用；
  其下是同一个下载状态组件（前缀 `settingsUpdate`）。
  其旁另有透明文字按钮「查看更新说明」（`updateReleaseNotesButton`），只在有新版本时出现，打开发布页。
  作为「检查更新」时，按钮在开关关闭、采集服务未给出更新信息，或上一次检查尚未返回时停用，
  判据是 `App.update.canCheck`（`available && enabled && !checking`）。
  提示共五句，分别对应：有新版本（写明可以点「下载安装包」，不支持软件内下载时写「下载新版本」）、已是最新版本、
  没有检查成功、开关已关闭、本机已通过环境变量禁用；其中失败一句不区分具体原因，也不出现地址或协议标记。
  新版本的横幅提示本身在总览页（§4.1），设置页不重复呈现。
  打开维护者工具时，面板底部另有一行小字「维护者工具：下载已发布的最新正式版的安装程序，即使它不比当前版本新。」
  与透明文字按钮「重新下载最新正式版」（`reinstallLatestButton`），只在采集服务支持软件内下载时出现：
  点击发出 `StartUpdateDownload { reinstall: true }`，下载进行中、请求尚未返回或桌面端正在校验时停用。
* **外观**（`appearanceSettingsCard`）：界面风格（`uiStyleSettingControl`，取值 经典 / 艾欧泽亚，
  副标题「经典：圆角卡片 · 艾欧泽亚：游戏窗口配色 · Harendotes：夜色与橙焰」，绑定 `Settings.uiStyle`；
  分段控件显示的是**屏幕上实际生效的**风格，`--mock-ui-style` 固定风格时同样如此）、
  主题（深色 / 浅色 / 跟随系统）、UI 缩放（100–175，步长 25，小字「重启后生效」）。
  缩放在 `QApplication` 构造之前注入 `QT_SCALE_FACTOR`，不支持运行时切换。
  启动自检要求 `uiStyleSettingControl` 完整落在 `appearanceSettingsCard` 之内。

采集服务不支持捕获设置时，相关开关停用，小字改为「当前采集器不支持该设置，开关已停用。」。
桌面端的全部设置持久化在 `AppSettings`（QSettings INI）中。

* 开机启动写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下名为
  `MentorRecorder` 的值（`QSettings::NativeFormat`）。本软件只写 HKCU，不安装服务，
  也不创建计划任务，因此删除程序目录即完成卸载。该开关默认**关闭**，
  安装即写入注册表属于用户不会预期的副作用。
* 托盘由 `TrayController`（`QSystemTrayIcon`）实现，菜单项自上而下为：「显示主窗口」；
  「待处理（N）」（N 为待处理的条数），显示主窗口并打开待处理列表（`RecoveryController::requestInbox`，§4.5.2）；
  「显示统计浮窗」或「隐藏统计浮窗」（§4.9.1）；「解锁统计浮窗」，只在浮窗锁定时出现；
  捕获项，只在打开维护者工具时出现，文字与捕获诊断页标题行的按钮相同（`App.captureActionLabel`，
  如「开始捕获」「停止捕获」），调用 `AppController::toggleCapture()`，同样经由后端；分隔线之后为「退出」。
  单击或双击托盘图标同样显示主窗口。主窗口隐藏或最小化时出现「无法自动记录」的情况，托盘另弹出一条同名通知。
  开关打开时，窗口的关闭事件被拦截为「隐藏到托盘」，退出菜单项绕过该拦截。
  没有托盘的环境（offscreen、CI）不创建图标，并将 `quitOnLastWindowClosed` 恢复为真。

**播报**：本机语音是**桌面端本地功能**，不经过采集服务。
在线语音（界面改版 P4b，[privacy-boundary.md](privacy-boundary.md) §8.3）由采集服务合成，
桌面端只播放采集服务写入本机的 WAV，自身不联网。

* **本地语音播报**（`speechSettingsCard`）：标题行（`speechHeaderRow`）由 kicker 与小字组成。
  kicker 为「本地语音播报」，选中在线语音时改为「语音播报」。
  小字在本机语音下为「使用系统语音引擎，不联网」；选中在线语音、且采集服务上**同一家服务**已配置完成时，
  为「播报文字会发送到 <`App.speech.targetHost`>」；其余情况为橙色的「在线语音 · 尚未配置」。
  标题行右侧是「试听」（`ttsPreviewButton`，`Tts.preview("completed")`，播放「通关」模板）与总开关（`Settings.ttsEnabled`）。
  **语音引擎**下拉框（`ttsVoiceCombo`）列出 `Tts.voices`：本机语音在前；
  采集服务支持在线语音时，其后接 Azure 与 OpenAI 兼容的音色，
  标签形如「晓晓（女声） · 在线（Azure）」与「alloy · 在线（OpenAI 兼容）」。
  下拉框小字在在线语音下为「在线 · 需配置密钥」；本机语音在存在语音列表时为「已安装 · Windows 自带」，
  否则取 `Tts.statusText`，引擎出错时显示为橙色。语速取值 50–200 %，音量取值 0–100。
* **首次选择在线语音**时先弹出确认框（`OnlineSpeechConfirmDialog`，`DialogFrame`），说明四项内容：
  发送的内容（只发送当前要播报的这一句，含副本名与进度数字，外加音色与语速）；
  接收方（微软 Azure 语音，或用户填写地址的 OpenAI 兼容服务，请求由采集服务发出）；
  密钥的存放位置（只在本机，由 Windows 加密）；以及关闭方式（改回本机语音）。
  点击「改用在线语音」写入 `Settings.ttsOnlineConfirmed`（INI 键 `tts/online_confirmed`），
  **同一台机器只询问一次**。点击「取消」或按 Esc 不记录任何内容，下拉框回到原来的语音。
* **在线语音**（`onlineSpeechSlot` 中的 `OnlineSpeechPanel`，`onlineSpeechCard`）：
  `Loader` 只在选中在线语音、且 `App.speech` 已加载并支持时激活。
  整个面板是一份**草稿**，点击「保存」后才一并发出：
  * kicker「在线语音」、说明与状态标签（已配置 / 尚未配置）；
  * **服务**分段控件 Azure / OpenAI 兼容（`onlineSpeechProvider`），初值为所选音色对应的服务；
  * Azure：**区域**（`onlineSpeechRegionField`，占位「例如 eastasia」）；
    OpenAI 兼容：**地址**（`onlineSpeechUrlField`，`https://…/v1`）、**模型**（`onlineSpeechModelField`）、
    **音色**（`onlineSpeechVoiceField`，自由填写，默认 alloy）；
  * **密钥**（`onlineSpeechKeyField`）：密码回显，**从不预填**，采集服务只返回 `has_key`；
    小字为「已保存密钥」或「未保存密钥」，旁边是「清除」
    （`onlineSpeechClearKeyButton` → `App.speech.clearKey()`，发送 `api_key: ""`）；
  * 说明（`onlineSpeechNote`）为「开启后播报文字会发送到 <主机>；密钥只保存在本机，并由 Windows 加密。」。
    其中的主机取自采集服务返回的 `target_host`。草稿与已保存的设置不一致时，桌面端不自行拼接域名：
    Azure 写作「微软 Azure 语音（<区域> 区域）」，OpenAI 兼容写作所填地址的主机名。
    已保存密钥而草稿更换了服务时，以橙字提醒旧密钥将被删除，这是采集服务的规则；
  * 结果行（`onlineSpeechResultText`）、「测试」（`onlineSpeechTestButton`，所选服务已配置时可用）
    与主按钮「保存」（`onlineSpeechSaveButton`）。保存时**先清空密钥框**，再发送 `UpdateSpeechSettings`，
    内容包括 `provider`、该服务的字段与音色（Azure 取下拉框中的音色）；
    `api_key` 仅在密钥框有输入时携带。成功后将所选语音改为 `azure:<音色>` 或 `openai:<音色>`。
* **播报语言**（`speechLanguageRow`，在语音引擎之上）：小字「跟随界面语言，语音和播报模板都按这个语言来」，
  右侧为当前界面语言的标签（`speechLanguageTag`）与透明按钮「在通用里更改」（`speechLanguageChangeButton`，切到「通用」分页）。
  语音与五条模板**按界面语言各存一套**：简体中文沿用原有的 `tts/voice`、`tts/tpl_*` 键，
  其他语言写在 `tts/<语言>/voice`、`tts/<语言>/tpl_*`；未编辑过的模板读取该语言的默认句（来自界面翻译）。
  语音列表按界面语言排序（同语言同地区、同语言其他地区、简体中文、其余），未保存语音时选列表第一项。
  本机已有语音、但没有界面语言的语音时，语音引擎小字改为橙色的「这台电脑还没有<语言>语音，暂用其他语言的语音播报」，
  并出现「打开 Windows 语音设置」（`openSpeechSettingsButton`，打开本机的 `ms-settings:speech`，不联网）。
  缺少界面语言的语音时，本软件每次重新回到前台都重新读取一次语音列表（`TtsService::rescanVoices`），
  在 Windows 中新装的语音随即出现；没有保存过语音选择时，按上述排序选中它。
  `{duty}` 按界面语言读出副本名（§4.5.3）。
* **播报模板**（`speechTemplatesCard`）：kicker「播报模板 · <语言>」与「可用变量：`{duty} {progress} {remaining}`」（等宽）；
  末尾的透明按钮「恢复默认模板」（`resetTemplatesButton`）只清除当前语言的五条模板，没有编辑过时停用；
  两列排列 匹配成功 / 进入副本 / 通关 / 异常结束；下方整行为 **结束待确认**，
  带说明「副本内收到通关结算时，记录直接记为通关，播报上面的“通关”；离开副本前没有收到结算时，
  记录停在“待确认”，播报这一条。」。试听与在线语音的「测试」播放的是「通关」那一条：
  国服副本内收到通关结算时（[protocol-profile-format.md](protocol-profile-format.md) §12），
  一局通关的导随听到的就是这句话。

`TtsService`（基于 `QTextToSpeech`）：

* `voices` 列出本机已安装的语音，zh-CN 在前、其余在后，各自保持引擎顺序。
  每行为 `{id: "local:<名称>", name, label, locale, provider: "local"}`，
  `label` 形如「Microsoft Huihui Desktop（中文 · 女声）」。
  `findVoices()` 可能切换引擎的语言区域，因此只在引擎处于 `Ready` 时枚举并缓存；
  之后只在 `rescanVoices()` 要求时（见上文「播报语言」）重新枚举，枚举仍等到下一次 `Ready`。
* `setVoice(id)` 将选择存入 `Settings.ttsVoice`（INI 键 `tts/voice`，空值表示默认）。
  `voiceId` 是当前正在使用、或空闲后即将使用的语音。
  语音**只在 `voiceApplicableInState()` 允许（即 `Ready`）时切换**：
  播报过程中选择的语音等到下一次 `Ready` 才生效，不会从中间打断一句话。
* 默认语音（取值为空、未安装、或选中在线语音时）是第一个 zh-CN 语音；
  没有中文语音的机器使用引擎自身语言区域的第一个语音，且不报错。
  在线语音失败时回退使用的也是该本机默认语音。
  `setVoice` 接受 `local:<名称>`、`azure:<音色>`、`openai:<音色>` 与空值，
  其他形式（缺少名称或使用其他前缀）一律忽略且不保存。
* `voices` 的每行带有 `group`（`local` / `online`）。在线行来自 `App.speech`
  （`SpeechController::voiceRows`）；所选音色或采集服务当前音色不在内置清单中时补充一行。
  采集服务支持在线语音时，`voiceId` 即为已保存的在线 id，否则为当前使用的本机语音。
* 语速为 `(percent - 100) / 100`，并限制在 [-1, 1] 区间内；音量为 `percent / 100`。
  本机没有可用引擎时不会崩溃，界面也不禁用：语音引擎一行显示中性说明，
  `spoke()` 信号照常发出，便于测试以及后续将播报接入其他输出。
  Windows 的 SAPI **异步**枚举语音，`QTextToSpeech` 构造完成时往往尚未 `Ready`，
  `availableVoices()` 为空。因此引擎的 `stateChanged` 与 `errorOccurred` 均接入回调，
  每次 `Ready` 重新选择一次语音并重设语速与音量。
  出错时状态行显示引擎原文，`Tts.available` 为假，设置页将该行标为橙色，而不是继续显示「已就绪」。
  一次导随的两句话（匹配与进本）之间只间隔一两秒，因此播报使用 `enqueue()` 排队，
  只有「试听」与自由文本使用 `stop()` 中断当前播放。

**在线语音的播放**由 `TtsServiceOnline.cpp` 负责，
`TtsService` 仅在 `Settings.ttsVoice` 为在线 id 时进入该路径：

* 每次只处理一句，流程为：发送 `SynthesizeSpeech{text, rate_percent = Settings.ttsRate, test}`
  （试听与「测试」取 `test: true`，正常播报取 `false`）；校验返回的 `audio_path`；
  交给 `SpeechPlayer`（`SoundEffectPlayer`，每句一个 `QSoundEffect`，音量为 `ttsVolume / 100`）；
  播放完毕后才发送下一句。
  IPC 为该消息设置 25 秒超时（`IpcClient::kSpeechRequestTimeoutMs`），`TtsService` 自身等待 22 秒。
  「播放完毕」由 `PlaybackEndTracker` 判定：`QSoundEffect` 在缓冲时会短暂报告停止，
  因此只有停止持续 150 ms、或已播放到 WAV 头声明的长度（误差在 100 ms 以内）才视为结束。
  另有看门狗兜底，其时限为声明长度加 4 秒，且不短于 6 秒。
* **路径校验**由 `SpeechAudioFile.cpp` 的 `checkSpeechAudioFile` 执行。
  数据目录取 `GetStatus.database_path` 所在的目录，采集服务正是以
  `Path.GetDirectoryName(database.Path)` 创建其下的 `tts-cache\`。
  `database_path` 为空或不是绝对路径时不予播放。
  出现下列任一情况即拒绝：带 `\?\` 或 `\.\` 前缀；相对路径；含 `..`；
  盘符之后还有冒号（备用数据流）；文件名不是 `<64 位十六进制>.wav`；
  不直接位于 `tts-cache\`（字面写法与解析链接之后都必须满足，因此换盘、目录联接与符号链接均不接受）；
  `tts-cache` 本身是链接；文件本身是链接；文件不存在；不是普通文件；
  不超过 44 字节或大于 5 MB；头部不是 RIFF/WAVE 16 位 PCM。
  被拒绝时日志只记录原因，不记录路径。
* **失败一律回退**。触发回退的情况包括：返回错误码；22 秒内没有响应；路径被拒；播放出错；
  采集服务不支持（`ERR_UNKNOWN_MESSAGE`）；设置尚未读取到；
  所选音色与采集服务当前服务不属于同一家（此时不发送请求，以免使用另一家服务的声音朗读）；
  句子超过 200 字。以上情况均改用本机默认语音朗读**同一句**，
  并按错误码在每次运行中只提示一次 toast「在线语音暂不可用（<原因>），本次改用本机语音」。
  原因取值为：未配置、密钥无效、额度用完、网络不可用、超时、返回的不是可播放的声音、
  在线语音已被禁用（`SpeechController::errorReason`）。
  回退的那句朗读完毕后（引擎回到 `Ready`，或按字数估算的上限到达）才处理下一句，**不丢弃任何一句**。
* 试听与自由文本会打断正在播放的那句，与本机语音的行为一致；
  仍在等待合成、尚未播出任何字的播报句会重新排入队列。
* 关闭语音播报总开关时，排队中与正在合成、播放的在线播报句一并取消，本机引擎中排队的播报同样停止，
  已排队或正在播放的试听照常播放，在线语音失败后改由本机语音朗读的试听也不例外。唯一的例外是本机引擎中
  在试听之后又排入了播报句：引擎无法单独撤下这一句，关闭总开关时试听与它一并停止（`m_engineHoldsOnlyTest`）。
  所选音色从在线语音改为本机语音时，在线队列连同试听全部取消。
  两种情况都是用户自己的选择，不提示「在线语音暂不可用」（`TtsService::dropOnline`）。
* `spokeVia(kind, text, route)` 对每句话发出一次，`route` 取 `online` 或 `local`，
  测试据此断言播报顺序与去向。

`SpeechController` 以 `App.speech` 属性提供给界面，共享校准也以同样方式通过 `App.calibration.shared` 提供：

* 属性包括 `loaded`、`supported`、`provider`、`azureRegion`、`openaiBaseUrl`、`openaiModel`、
  `voice`、`hasKey`、`configured`、`targetHost`、`azureVoices`、`openaiVoices`、`busy`、
  `resultState`、`resultText` 与 `lastError`，**不含密钥属性**。
  旧版采集服务拒绝 `GetSpeechSettings` 时 `supported` 为假：
  发行版采集服务对无法识别的消息返回 `ERR_BAD_REQUEST`（`field = message_type`），
  控制器同时接受 `ERR_UNKNOWN_MESSAGE` 与 `ERR_UNSUPPORTED`。此时在线语音整组设置不出现。
* 方法包括 `save(map)`、`clearKey()` 与 `test()`，后者转交 `TtsService::testOnline()`，
  以在线语音朗读与试听相同的一句（「通关」模板），结果写回 `resultText`。控制器在连接采集服务时与每次保存后刷新。
  `GetSpeechSettings` 没有得到应答（不是被拒绝）时，按 2 秒起、每次加倍、最长 60 秒的间隔重新读取，
  直到读到或连接断开（`kSettingsRetryFirstMs` / `kSettingsRetryMaxMs`）。
* 所选音色与采集服务属于同一家服务、仅音色不同时，自动发送一次只含 `voice` 的更新。
  控制器**从不自动更换服务**：更换服务而不携带新密钥会导致采集服务删除旧密钥。

播报由后端的 live 事件驱动，**过程状态与终局状态分属两条路径**：

| live 事件 | 状态 | 模板 |
|---|---|---|
| `run_state_changed` | `MENTOR_MATCHED` / `ENTERED_DUTY` | 匹配 / 进本 |
| `run_finished` | `COMPLETED`（收到了结算：档案声明的 `DUTY_RESULT` 或国服按内容识别的通关结算） | 完成 |
| `run_finished` | `UNKNOWN_FINAL_STATE` | **结束待确认** |
| `run_finished` | `LEFT_OR_ABANDONED` / `DISCONNECTED` / `INTERRUPTED` / `INTERRUPTED_PENDING_REVIEW` | 异常结束 |
| `run_finished` | `CANCELLED_BEFORE_ENTRY` | 不播报（拒绝排本是正常操作） |

终局状态只采信 `run_finished`。下一次匹配弹窗紧接着到来时，`StateChanged` 会直接跳过终局状态，
而 `run_finished` 必定发出（`src/Collector/Ipc/LiveEventBus.cs`）。
记录结束时，先等待一次 `GetDashboardStats` 返回，再播报结果，因为 `{progress}` 必须是本局之后的数值；
且 `UNKNOWN_FINAL_STATE` 的记录**在用户确认通关之前不计入**成就进度。
映射本身由 `AppController::announcementKind(state)` 实现，是静态映射，可由表驱动测试固定。
MockBackend 在模拟状态切换以及开始或停止捕获时发出这些事件，
并提供 `simulateRunTransitions()` 供测试驱动完整流程，其中包含最后一条 `RunFinished`。

**成就**（`achievementSettingsCard`，kicker「成就进度」）分为「目标值」与「起始次数」两列，修改原因为必填项。
起始次数是开始用本软件记录前已经完成的导随次数，即契约中的 `baseline_completed_count`（下文简称基数）。
总览成就卡片上「含起始次数 N 次 · 修改」中的「修改」直接打开本分页（`SettingsPage.editBaseline()`），
并把光标放进起始次数栏（`baselineField`）、选中其中的数字，此时输入的数字直接替换它；已保存的值尚未读到、
输入框还不可用时，读到之后再放入光标，届时已离开本分页则不再放入。
两个输入框预填的是从采集服务读到的已保存值（`GetDashboardStats` 的 `goal_count` 与 `baseline_completed_count`）。
在当前连接上读到第一份统计之前（`App.achievementSettingsLoaded` 为假，与采集服务断开后重新变为假），
两个输入框与「保存」均不可用，下方以灰字说明「正在读取已保存的目标和起始次数，读取完成后才能修改和保存。」：
此时输入框里只是默认的 2000 与 0（或上一次连接读到的值），只改目标并保存就可能改掉已保存的基数。
读到之后，用户尚未动过的输入框换成已保存的值。
目标不是大于 0 的整数时以「「目标值」请填写大于 0 的整数。」拒绝；起始次数栏为空时不按 0 保存，
而以「「起始次数」请填写 0 或正整数。」拒绝。
采集服务对 `UpdateAchievementBaseline` 强制要求 reason（`ERR_REASON_REQUIRED`），页面先执行同样的检查，
原因为空时提示「请填写「修改原因」后再点「保存」。」；采集服务的拒绝（`App.baselineFailed`）以红字显示在按钮上方。
修改原因输入框的提示是「例如：调整开始记录前已完成的导随次数（按 Tab 填入）」：框内为空时按 Tab 即填入
「调整开始记录前已完成的导随次数」，焦点留在框内（`StyledTextField.exampleText`）；框内已有文字、输入法正在组字，
或同时按着修饰键（如 Shift+Tab）时，照常移动焦点。只有原因与备注这类自由文字栏设有示例，数字与配置类输入框没有。
保存成功后桌面端重新读取一次总览，toast 取重新读取得到的进度，写作「起始次数已设为 N · 目标 M · 总进度 P」；
重新读取失败时只写「起始次数已设为 N · 目标 M · 已保存」，不给进度数字（`UpdateAchievementBaseline` 的应答不含进度）。
内嵌框（`progressFormula`）中「进度 = 起始次数 + 软件记录」之后是数字字体的大号算式 `B + R = P`（`progressFormulaFigures`），
右侧小字为「起始次数：开始用本软件记录前，已经完成的导随次数。可保留或调整，点「保存」后生效；导入时可扣除重复计数。」。
算式预览当前输入保存后对应的进度：B 取起始次数栏中正在填写的数（栏内为空或不是大于等于 0 的整数时取已保存的基数），
R 为采集服务给出的 `achievement_progress` 减去已保存的基数，即软件记录的、计入进度的通关，
不论何时结束都在其中，不计入进度的通关则不在其中；P 为两者之和
（[statistics-definitions.md](statistics-definitions.md) §4）。
总览的进度环、「还差 N 次」与语音播报中的进度数字取自 `achievement_progress`。

导入记录对话框（`importRecordsDialog`）在底部按钮行之上有一块内嵌框（`importBaselineChoice`），只在预览有效、
已保存的基数大于 0，且勾选的记录中有会计入进度的通关（`ImportRecords.contributingSelectedCount`）时出现：
「勾选记录中有 N 条通关会计入进度。它们已包含在起始次数 B（开始用本软件记录前，已经完成的导随次数）中吗？」，
下方两枚单选 `PickChip`：「已包含：保存时扣除 D 次，起始次数改为 B − D」（`importBaselineDeduct`）与
「未包含：照常加在起始次数之上」（`importBaselineKeep`），D 为 N 与 B 中较小者；选「已包含」而 N 大于 B 时另加一行灰字
「起始次数只有 B，只能扣到 0；其余 N − B 次照常加在起始次数之上。」。
默认值按来源：备份为「未包含」；JSON 只在每一条候选都带非空的 `run_id` 与 `source` 时（即本软件自己导出的 JSON）
为「未包含」，填写后的 JSON 模板与其他 JSON 为「已包含」；表格、粘贴与截图为「已包含」。
用户的选择在同一批次的重新校验后保留，换来源后重置。
选「已包含」时 `CommitRunImport` 带 `deduct_from_baseline = true`，采集服务在同一事务内从基数中扣除本次**新写入**的、
计入进度的通关数，最低扣到 0（[statistics-definitions.md](statistics-definitions.md) §4）。保存后的状态行写
「已导入 I 条，跳过重复 X 条；冲突 C 条，保留本地记录。起始次数已从 B 改为 B′，扣除已包含的 D 次通关，避免重复计算。」，
数字取应答中的 `imported_count`、`duplicate_count`、`conflict_count`、`baseline_deducted_count` 与 `baseline_completed_count`。
扣除之后，新写入的计入进度的通关不多于原基数时总进度不变；多于原基数时基数减至 0，总进度增加超出的部分。
勾选了会计入进度的通关、而基数尚未从采集服务读到时（`ImportRecords.waitingForBaseline`），不能导入，
底部写「起始次数尚未读取，读取后才能导入」；「重试确认保存」重发的是已经发出的同一批次，不受此限制。
基数及其是否已读到由 `App.baselineCount` 与 `App.achievementSettingsLoaded` 经 `main.cpp` 传给
`ImportRecords.baselineCount` 与 `baselineLoaded`，对话框不直接读 `App`。

导入记录对话框另有以下保护：

* 已在本批次中的截图不再加入，提示「截图「<名称>」已在本批次中，没有重复添加；原批次已保留。」；
  一次选择中同一张截图选了两次时提示「截图「<名称>」选择了两次，请每张截图只选一次。」。
* 职业按图标推测（`job_candidate_pending`）的行须先在「职业」中确认（选择一个职业，即使与推测相同）：
  「勾选并下一条」遇到这样的行时提示「职业是按图标推测的，请先在「职业」中确认，再勾选。」，
  「全选整批可导入」跳过这样的行；已勾选的行中仍有待确认职业时不能导入，底部写「N 条已勾选记录的职业待确认，确认后才能导入」。
* 首次预览时，可能重复（`possible_duplicate`）的行与截图来源的行都不预先勾选，只由用户勾选；
  重复、冲突与无效的行不能勾选。「全选整批可导入」会勾选可能重复的行。采集服务把两类行标为可能重复：
  与本地记录副本相同、原站日期相同且至少一方没有具体时刻的行（[data-model.md](data-model.md) §9），以及同一批次中
  与前面某一行副本、时间与心得都相同的行（例如重叠的滚动截图读出的同一张卡片）。
* 修改单行、批量设置、更改地区、重新校验或追加截图之前，先按采集服务的口径计算大小：候选记录的 JSON 数组按 .NET 的转义规则
  计出的 UTF-8 字节数，上限为 IPC 单帧上限的四分之三（3 MiB），与服务端拒绝分页预览的预算相同。超过时这次操作不生效并说明原因，
  例如「修改后这批记录太大，无法重新校验，这次修改没有保存。请缩短内容后再改。」、重新校验时的「这批记录太大，无法重新校验，
  当前内容没有改动。请分成几批重新导入。」、追加截图时的「加入新截图后这批记录太大，无法校验。未添加新记录，原批次已保留；
  请把新截图放到下一批导入。」；已通过校验的批次保持原样，仍可导入。
* 已有记录的批次在追加截图或重新校验时，底部按钮写作「取消识别 / 预览」，只取消这次处理并保留原批次，
  对话框不关闭；空闲时该按钮为「关闭」。
* 提交没有得到应答时，这批记录保留为待确认，按钮改为「重试确认保存」，以同一请求编号重发；
  采集服务明确拒绝时（包括它自己答复的内部错误与预览过期），待确认状态解除，可以再次修改、重新校验或关闭。

成就分页底部左侧为「重新打开首次引导」，右侧为主按钮「保存」。

**数据**（`dataSettingsCard`，kicker 为「数据库」）包含以下内容：
数据库位置（只读、等宽，取自 `GetStatus.database_path`）与「打开目录」
（`QDesktopServices::openUrl` 指向本地文件夹）；
自动备份开关，小字为「每天自动备份一次，保留 14 份」，
由 `AppController::runDailyBackupIfDue()` 每天触发一次 `BackupDatabase`：启动后连上采集服务时、
每次读回当前导随时与每小时检查一次日期，当天已备份则跳过，导随进行中（已匹配或已进本）时顺延到这一场结束
5 分钟之后（结果确认、心得窗口与播报在这段时间里不必排在备份之后），其间再次匹配则继续顺延；
诊断日志保留天数（`logRetentionField`，写入 `UpdateCaptureSettings.log_retention_days`，
小字为「1–90 天 · 采集器当前生效值：N 天」），日志及其保留天数均由采集服务持有；
按钮「立即备份」与「完整性校验」，其下是校验结果行与一行说明。
采集服务在启动时和每次备份时也会自动校验，桌面端不直接连接数据库。

「立即备份」发送 `BackupDatabase`，且**不携带** `target_path`，
这样才会选中采集服务自行管理、并按 14 份轮转的备份目录。
toast 中回显后端返回的 `target_path`、`byte_count`、`integrity_check_passed` 与 `pruned_count`。

「完整性校验」（`integrityCheckButton`）发送只读的 `CheckDatabaseIntegrity {}`，
经由 `App.checkDatabaseIntegrity()` 与 `ExportController`。
校验进行中按钮停用并显示「正在校验…」。
结果保存在 `App.integrityCheckResult` 中，`state` 取 `passed` / `failed` / `unsupported` / `error`，
另有 `passed`、`detail`、`checked_at_utc`、`text` 与 `error`。
结果行（`integrityCheckResultText`）的显示规则如下：

| 情况 | 文字 | 颜色 |
|---|---|---|
| `passed: true` | 完整性校验通过 · HH:mm:ss（`checked_at_utc` 的本地时间） | 绿 |
| `passed: false` | 完整性校验未通过：`detail` | 红 |
| `ERR_UNKNOWN_MESSAGE` / `ERR_UNSUPPORTED` / `ERR_BAD_REQUEST`（旧采集服务） | 当前采集器不支持完整性校验 | 橙 |
| 其他错误（如 `ERR_DB_BUSY`、未连接） | 完整性校验没有完成：错误说明 | 橙 |

MockBackend 回 `{passed: true, detail: "ok", checked_at_utc: 现在}`。

**关于**（`aboutSettingsCard`）包含 kicker「导随记录器」与 `v<App.appVersion> · GPL-3.0 或更高版本`。
版本号一行（`aboutVersionText`）在 `GetStatus.update.update_available` 为真时附带
「有新版本 x.y.z」，其右侧另有透明文字按钮「查看更新说明」（`aboutOpenReleasePageButton`，只在有新版本时出现）
与随下载阶段变化的按钮（`aboutDownloadInstallerButton`），文字与作用和总览横幅的主按钮相同（§4.1）；
版本号一行之下是同一个下载状态组件（前缀 `about`）。
没有可下载的新版本时（`App.update.downloadPhase` 为空），该位置是「检查更新」（`aboutCheckUpdateButton`），与设置页「通用」的同名按钮
发出同一条 `CheckUpdateNow`、遵循同一套停用规则，结果同样以一句提示说明。
小标题「隐私与边界」下有两块内嵌信息：Oodle 解压（`GetStatus.oodle_mode` 经
`Fmt.oodleModeLabel` 换成中文说明，不显示模式名与决策编号）、
读取游戏可执行文件（`reads_game_executable`，为真时以橙色显示「是（读取磁盘上的一份副本）」）。
按钮为「查看软件说明」（`aboutExplanationButton`，打开 §4.6 的说明窗口）与「打开捕获诊断」（ghost）。
其下是 **版权与来源**（`copyrightCard`），保留四段完整表述，较原型的缩写更为准确。
早先独立的「隐私与边界」卡片已并入上述两块信息与两个按钮。

### 4.5.1 导随心得（reflections）

「心得」是每条导随记录上的一段自述，结构为 `{mood: good|ok|bad, text}`，
契约见 `contracts/ipc-v1.schema.json` 中的 `SetRunReflection` 与 `GetReflectionSummary`。
桌面端只绑定 C++ 侧的下列成员，且**全部做了成员存在性检查**，
因此后端尚未实现时，界面只显示空状态，而不会报错：

| 界面 | 绑定 |
|---|---|
| 总览「导随笔记」面板 | `App.reflectionSummary`（`reflection_count` / `pending_completed_count` / `recent` / `next_pending`） |
| 「补录笔记」按钮 | `next_pending` 存在则打开对话框，否则 toast「所有通关记录都已写过笔记」 |
| 近期心得卡片（最多 3 张） | `App.openHistoryForRun(run)`，同时把详情浮层切到「笔记」页签 |
| 历史筛选「有笔记」 | `RunFilter.with_reflection` |
| 详情浮层「笔记」页签 | `run.reflection`；有则 心情 tag + 时间 + 正文 +「编辑笔记」，正文非空时另有「生成分享图片」；无则空状态框 +「补录笔记」 |
| `dialogs/ReflectionDialog.qml` | `App.saveReflection(runId, mood, text)`；`App.reflectionSaved` 关闭，`App.reflectionFailed` 在对话框内红字显示 |
| 通关后自动提醒 | `App.reflectionPromptRequested(run)` → `Main.qml` 的 `offerReminder(run, "reflection")`：按提醒方式（§4.5.2）在紧凑弹窗中 `openForPrompt(run, "刚刚完成")`，或只记入待处理；弹窗正忙时请求留在队列中，弹窗关闭后再弹出（`reflectionPromptShown` / `reflectionPromptClosed`），期间关闭「通关后提醒记笔记」则清空队列；已在结果问句中弹出过的记录不再单独提示 |
| 设置「通关后提醒记笔记」 | `Settings.reflectPrompt` |
| 结束后询问结果 | `App.resultConfirmationRequested(run)` → `offerReminder(run, "result")` → 同一个对话框的 `openForResult(run)` |
| 设置「结束后询问本次结果」 | `Settings.confirmPrompt` |

主窗口中的对话框（由总览「导随笔记」面板与历史记录详情的「笔记」页签打开）宽 560，自上而下依次为：
kicker（补录笔记 / 编辑笔记，或打开方给出的文字）与右侧的 run_id；衬线标题「导随笔记」；
四列内嵌摘要（副本 / 职业 / 结果 / 耗时）；「这次感觉」分段控件（顺利 / 一般 / 糟心）；六行文本域；文本域之下是备注图片条
（`NoteImageStrip`，与详情浮层同一组件：「添加图片」与移除都即时生效，图片属于记录而非心得，
不随「保存笔记」或「稍后补录」改变；提交请求在途时不可编辑）。
页脚为小号开关「通关后提醒记笔记」、「稍后补录」与主按钮「保存笔记」（清空了原有笔记时为「清空笔记」，补选了职业时为「保存记录」）。

清空正文后保存等同于删除该条心得，契约规定 `text` 为空即删除，
因此主按钮在文本为空时不会被禁用。

### 4.5.2 本次导随结果（confirmation）

国服副本内收到通关结算时（[protocol-profile-format.md](protocol-profile-format.md) §12），
记录直接收尾为 `COMPLETED`，计入成就进度，不再询问结果。离开副本前没有收到通关结算时，
一局导随停在 `UNKNOWN_FINAL_STATE`（`pending_review = true`，`result = UNKNOWN`），
而成就进度只统计 `COMPLETED`，因此这一场的**次数在用户确认之后才会增加**。
本软件不代替用户判定这类结果，也不将该判定完全交给事后的「待复核」列表；
0.2.2 中「完成一局而次数未变」的问题即源于后者。

结果问句由同一个 `ReflectionDialog` 的问句模式（`askingResult`）承担，本软件自己发出的问句总以浮窗旁的紧凑弹窗呈现
（§4.9.2）：先问「通关了吗？」，提供三个答案 **通关 +1**、**退出**、**稍后**；`Settings.reflectPrompt` 关闭时弹窗不含心得一行。
选择「通关 +1」调用 `App.resolveRunResult(runId, revision, "COMPLETED", "用户确认通关")`（未识别职业时一并带上补选的职业）；
选择「退出」走同一路径写入 `LEFT_OR_ABANDONED`（原因「用户确认退出或放弃」）；
选择「稍后」不写入任何内容，弹窗收起，记录留在待处理与待复核列表中。
答案被 Collector 接受（`mutationSucceeded`）后，弹窗改为显示已记下的结果，心得在「完成」时保存；
被拒绝时（例如版本号过期）在弹窗内以红字显示。非独立形态的对话框保留同样的问句（「通关」「退出 / 放弃」「稍后再说」，
已填写的心得随答案先行保存），只在不经 `Main.qml` 调度的场合（测试）使用。

`resolveRunResult` 使用**与手动修正完全相同的 `CorrectRun`**：
一条修正中同时写入 `result` 与 `pending_review = false`。
因此确认本身也是一条可审计的新 revision，且不引入任何新的 IPC 消息。

**待处理**：尚未处理完的结果问句与心得提醒汇入一份按数据库区分的待处理列表（`RecoveryController`，`App.recovery`）。
一条记录在以下任一情况下列入：采集服务报告它待复核（`QueryRuns` 的 `pending_review = true`，只取自动识别且确认为导随的记录
与手动记录）；结果问句或心得提醒已经发给它而尚未处理；它有未保存的心得草稿。提醒与草稿写在设置文件旁的
`recovery\<数据库路径的 SHA-256>.json` 中，程序崩溃或关闭后仍在；这份文件只记提醒与草稿，从不修改记录。
列表按匹配时间由新到旧排列，每条写副本名、职业与结束时间，并以「待复核」「待补笔记」「有草稿」标明原因。
「继续处理」先重新读取该记录，再在紧凑弹窗中打开：结果仍未知的待复核记录先问结果，其余打开心得；
读取失败时以保存的副本打开，草稿可以查看、复制，但不能提交。记录已不存在或已删除时，它的结果与心得提醒随之撤下，
只保留草稿供复制；无法打开的条目另有「移除提醒」，带草稿时第一次点击变为「确认移除（草稿一并删除）」，
再点一次才连同草稿一起移除，记录本身不受影响。
列表出现在三处：总览的「待处理」卡片（§4.1）；托盘菜单「待处理（N）」与卡片上「查看全部待处理」打开的
「导随记录器 · 待处理」窗口（`RecoveryInbox`，另有「刷新」与「关闭」）；统计浮窗的待处理模块（§4.9.1）。
待复核的记录可能已是通关（校准被撤下的记录保留原结果），这类记录已经计入进度，在弹窗中以「在历史记录中复核」处理（§4.9.2）。

**提醒方式**（§4.5「记录」）决定一条新的结果问句或心得提醒是否立刻弹出：「独立小窗」总是在浮窗旁弹出紧凑弹窗；
「浮窗提示」只在统计浮窗显示时弹出，浮窗关闭时只记入待处理；「仅保留待办」从不自动弹出，只记入待处理。
无论哪种方式，提醒都先写入待处理，再决定是否弹出；弹窗正在显示另一条记录时，新的提醒排队，弹窗关闭后再弹出。
每条记录只询问一次；`ended_at_utc` 早于本次启动的记录（重连时被重放的旧事件）不再询问。

### 4.5.3 界面语言

界面提供简体中文、繁體中文、English、日本語四种语言，默认简体中文。
简体中文是所有 `qsTr()` / `tr()` 的源文本；其余三种是 Qt Linguist 译文
（`src/Desktop/i18n/mentor_recorder_<语言>.ts`），构建时编译为 `:/i18n/*.qm` 并嵌入程序。
语言由 `LanguageManager`（QML 运行时入口 `Runtime.lang`）管理：

* **切换即生效**：安装对应的 `QTranslator`，设置 `QLocale::setDefault`，调用 `QQmlEngine::retranslate()`。
  只在 C++ 中生成的文字（`Fmt.*`、控制器的状态句、模型的显示列）不经过 `qsTr()`，
  其绑定以 `Theme.live(...)` 包裹，依赖 `Lang.revision`；C++ 侧暂存的文字以 `LiveText` 保存生成方式，
  切换后重新生成并发出 NOTIFY（`cpp/LiveText.h`）。
* **字体随语言切换**：应用字体只登记一个别名 `MentorRecorder Interface`，
  切换时更换别名所替代的字体：繁體中文以 Microsoft JhengHei UI、日本語以 Yu Gothic UI / Meiryo UI 居首，
  其后是 §3 的正文字体栈；简体中文与 English 与 §3 相同。每个文本项创建时复制一次应用字体，
  因此只有别名能让已经显示的文字换用新字形；切换后清空字体缓存，Qt Quick 文本随之重新排版。
* **游戏名称**：记录、统计与导出始终保存简体中文名称。界面显示时，副本、职业、随机任务、副本分类、
  资料片与职能组经 `GameNames`（QML 运行时入口 `Runtime.names`）换成界面语言的官方游戏文本
  （`data/i18n/game-names.<date>.json`，[data/i18n/README.md](../data/i18n/README.md)）；
  选择器以简体中文名称为值、以译名为标签，搜索同时匹配两者。
* **程序写入的修订原因**（「用户确认通关」等）以简体中文原文保存，显示时按当前语言翻译
  （上下文 `SystemReason`，`Theme.storedReason()`）；用户自己填写的原因原样显示。
* **采集服务发来的句子**：采集服务（C#）的错误说明、捕获提示、校准阻塞原因与时间线、
  导入行的错误与警告、在线语音的音色名等，除原有字段中的简体中文原句外，另以配套的 `*_ref` 字段
  给出文字目录中的键与参数（[contracts/collector-texts.md](../contracts/collector-texts.md)）。
  桌面端据此按当前界面语言生成句子（`cpp/CollectorText.*`，QML 中为 `Fmt.collectorText()`），
  暂存的句子以 `LiveText` 保存生成方式，切换语言后随之重新生成；拒绝启动时标准错误中的说明
  同样按其前一行的 `MR-TEXT` 显示。没有配套字段（旧版采集服务）、键不在本版本的目录中或参数不全时，
  显示采集服务的中文原句。界面需要辨认某一句话时（例如校准卡片对「打完这把副本，离开后再回来。」的补充说明），
  比较的是目录键而不是文字，因此在各种语言下判断一致。
* **仍为简体中文的文字**：协议档案中的说明、随程序嵌入的更新记录与导入说明；
  历史记录搜索框的文字（`QueryRuns.filter.text`）由采集服务在记录所存的简体中文副本名、职业名与备注中匹配。
  界面语言不是简体中文、且搜索文字与某个副本或职业的界面译名相符时，桌面端另把它换成记录所存的简体中文名称
  （`Names.recordedSearchText`），作为 `filter.name_text` 一并发送；采集服务只在副本名与职业名中匹配它，
  结果与 `filter.text` 的匹配合并。
* 截图与测试可用 `--ui-language zh-CN|zh-TW|en|ja` 固定本次运行的语言，不改写 `desktop.ini`（§7）。

### 4.6 首次启动与软件说明（DEC-OODLE-01）

首次启动时，`Main.qml` 在 `Component.onCompleted` 中直接打开基数引导 `BaselineDialog`（见 §5 中「首次启动的
baseline 对话框」一段），此前不显示任何说明，也不要求任何确认。是否为首次启动取自 `AppSettings` 的
`ui/first_run_completed`，基数引导保存成功后记为完成。

说明窗口 `DisclosureDialog`（kicker「隐私说明」，标题「这个软件做什么、不做什么」）是一个普通的说明窗口，
只由设置页「关于」的「查看软件说明」打开：没有勾选项，不需要确认，底部只有主按钮「关闭」（`disclosureCloseButton`），
Esc 同样关闭，左侧小字为「此说明可随时在「设置 → 关于」中点「查看软件说明」重新打开。」；窗口不记录任何内容。
只有正文滚动，「关闭」固定在底部：720 像素高的窗口容纳不下整页。

正文分为「注意事项」与「运行顺序」两组。「注意事项」各条如下，其中第 2 条句末写出本机当前的 Oodle 解压方式与
是否读取游戏可执行文件，取自 `GetStatus` 的 `oodle_mode` 与 `reads_game_executable`，而非硬编码的默认值：

1. 只被动监听网卡流量
2. **会读取磁盘上的游戏程序副本**
3. 不注入、不读取游戏进程内存
4. 游戏记录保存在本机：没有遥测或云同步，下列联网请求不会上传数据库
5. **后台服务仅为以下三类用途联网**，分四段叙述：总述（界面本身不直接联网）；
   **联网一：游戏更新后获取共享校准（默认开启，可关）**，说明发送时机、GitHub 读取失败时依次改从 GitCode 镜像
   与 CDN 读取、不携带的内容、GitHub、GitCode 与 CDN 能看到请求来自哪个 IP 地址，
   以及在「设置 → 通用」中的关闭方式（docs/privacy-boundary.md §8.2）；
   **联网二：在线语音（默认关闭）**，仅在选择在线语音并填写自有密钥后才发送，
   发送内容为当前这一句播报，选回本机语音即停止（§8.3）；
   **联网三：检查更新，以及按你的操作下载新版本**：从本项目公开发布页读取版本信息，有新版本时在总览提示，
   也可点「检查更新」主动检查；点「下载安装包」只下载安装包并核对发布时公布的校验值，
   再点「立即安装」才启动安装程序、本软件随即关闭；软件不会自行下载或安装，
   可在「设置 → 通用 → 更新」中关闭「检查新版本并提示」（§8.4、§8.6）

「运行顺序」三条：先打开本软件，再启动游戏；已经登录的，登出到标题画面再登录一次；
正常排指导者任务，未能自动判定的结果到"待处理"里填写（副本内收到通关结算时自动记为通关）。

此前的版本在首次启动时先显示该窗口，须打开「我已阅读并理解」开关再点「我已了解」，确认按说明的版本号
（`AppSettings::kDisclosureVersion`）记在 `desktop.ini` 的 `ui/disclosure_acknowledged_version` 与
`ui/disclosure_acknowledged_at` 中，设置页「关于」另显示确认状态。确认步骤、版本号与这两个设置项现已全部取消；
旧版本写下的这两项可能仍留在 `desktop.ini` 中，读取时不使用（`LifecycleTests::aSettingsFileWithTheRetiredConfirmationStillLoads`）。

`--show-disclosure` 命令行开关在启动时打开该窗口，与「查看软件说明」打开的相同，两种后端均可使用，用于截图与复查。

## 4.7 真实后端行为对照表（Phase 4）

交互式运行默认使用 `--backend ipc`，并以子进程方式启动 `MentorRecorder.Collector.exe --serve`。
下表列出每个页面实际发出的消息。
在环境变量中设置 `MR_IPC_TRACE=1` 后，每一条出站请求都会打印到 stderr，只打印请求，不打印响应。

| 页面 / 控件 | 消息 | 说明 |
|---|---|---|
| 总览：统计卡、进度环、结果分布 | `GetDashboardStats` | `result_breakdown` 直接驱动结果分布条 |
| 总览：完成趋势 | `GetDashboardStats`（`trend_granularity` = day / week / month）| 桶由 Collector 按 UTC 的 `matched_at_utc` 聚合；桌面端只按本地时区标注 |
| 总览：当前导随卡 | `GetCurrentRun` | `state` / `run` / `elapsed_ms` |
| 侧栏状态点 · 顶栏 Collector 行 | `GetStatus` + `CollectorProcess` | FF14 / Npcap / 捕获 / Collector 生命周期 |
| 历史记录：列表、分页、排序、搜索、筛选 | `QueryRuns`（`filter` / `page` / `page_size` / `sort`）| 默认 `sort = {history_date, desc}`（「日期」列），`page_size` 按窗口高度计算（最少 5，见 §2） |
| 历史记录：导出 CSV / JSON | `ExportCsv` / `ExportJson` | 路径由 `QFileDialog` 选（默认「文档」）；`--export-target DIR` 可跳过对话框 |
| 历史记录：详情面板 | 行数据 + `GetRunRevisions` | 选中即读取修改记录 |
| 副本统计 | `GetDungeonStats` | 含 `content_id = null` 的「未知副本」行 |
| 职业统计 | `GetJobStats` | 含 `job_id = null` 的「未知」行；职能占比由 `job_id` 经职业目录推导 |
| 副本 / 职业柱状图点击 | `QueryRuns`（`filter.content_id` / `filter.job_id`）| 跳到历史页并预置筛选；`content_id` / `job_id` 为空的行不跳转，只提示原因 |
| 捕获诊断 | `GetStatus` + `GetCaptureStatus` 数据结构 + `ListCaptureAdapters` + `GetProtocolProfileStatus` | 计数与 `recent_parser_errors` 是 `CaptureStatus` 的可选字段；缺失即 `—`。见 4.4 |
| 捕获诊断：开始 / 停止捕获 | `StartCapture` / `StopCapture` | 本机无 Npcap ⇒ `ERR_NPCAP_MISSING` |
| 捕获诊断：导出脱敏诊断报告 | `ExportDiagnosticsReport`（`target_path` 可省略）| 见 4.4 |
| 设置：成就进度保存 | `UpdateAchievementBaseline`（reason 必填）| 成功后重算总览与趋势 |
| 总览横幅 · 设置「通用」与「关于」的更新按钮 | `CheckUpdateNow` / `StartUpdateDownload` / `CancelUpdateDownload` | 下载或校验进行中约每秒重读一次 `GetStatus` 取进度；「立即安装」不发消息，见 4.1 |
| 设置：立即备份数据库 | `BackupDatabase`（不带 `target_path`）| 见 4.5 |
| 新增记录 | `CreateManualRun`（`source = MANUAL`）| |
| 手动修正 | `CorrectRun`（带 `expected_revision`）| 成功后刷新 `GetRunRevisions` |
| 软删除 / 恢复 | `SoftDeleteRun` / `RestoreRun` | 成功后立即 `refreshAll()` |
| 连接建立时 | `SubscribeLiveEvents` | 断线重连后自动重订 |

### 4.7.1 只在契约里存在的字段才会被发送

`$defs/CreateManualRunRequest` 与 `CorrectRunRequest.changes` 均声明 `additionalProperties: false`。
修正对话框另外跟踪 `duty_level`、`duty_expansion`、`job_name` 与 `role` 用于本地显示，
原样转发会被真实 Collector 判为 `ERR_BAD_REQUEST`，返回「请求包含契约未声明的字段 duty_level。」。
因此 `AppController` 在发送前按契约字段白名单过滤（`contractRunFields`），
对话框则保留更丰富的本地状态。

### 4.7.2 live 事件路由

`SubscribeLiveEvents` 之后，事件按 `kind`（比 `event_type` 更细）分派：

| `kind` | 行为 |
|---|---|
| `run_state_changed` | 先 `GetCurrentRun`，再播报 匹配 / 进本，最后刷新总览；发出时间早于本次桌面端启动的事件（订阅时补发的旧事件）不播报 |
| `run_finished` | 重新加载列表，刷新总览，**在总览响应返回后**播报终局那句并弹出「本次导随结果」 |
| `run_created` / `run_updated` | 重新加载历史列表与当前导随卡 |
| `stats_invalidated` | 刷新总览、趋势、副本统计、职业统计 |
| `collector_status` | 直接采用事件里的 `capture` 对象（发出时间早于最近一次读到的捕获状态时不采用），再补一次 `GetStatus` |
| `heartbeat` | 直接返回：该事件只表明管道存活，落入下一行的处理会每 5 秒产生一串 IPC |
| 其他 | 一次开销较小的 `GetStatus`，绝不静默丢弃 |

每条事件都带有单调递增的 `sequence`。每次连接或重连事件总线时，
总线会**不加标记地重放**最近 64 条事件，因此序号不高于已采用水位的事件一律丢弃，
以避免重连后重复播报旧导随并重复刷新。
断线时水位复位，因为重启后的 Collector 从头计数；
但「该记录的该状态已播报或已询问」这一守卫会持续保留。

### 4.7.3 错误信封 → 界面

所有 `ErrorEnvelope` 都带有 Collector 自身的中文 `message`。
界面**始终显示后端的原文**，不改写为自拟的语句。机器码不对普通用户显示：只有在维护者工具开启时，
才在句末的括号中附上（`AppController::errorText`，各对话框与卡片统一经由它取文字）；后端没有给出说明时显示
「操作没有完成，原因不明。」。

| 场景 | 出现位置 |
|---|---|
| `ERR_REASON_REQUIRED` / `ERR_TIME_ORDER` / `ERR_NEGATIVE_DURATION` / `ERR_NO_CHANGES` / `ERR_REVISION_CONFLICT` / `ERR_BAD_REQUEST`（新增 / 修正）| **修正对话框内的红色错误条** + toast |
| 同上（软删除 / 恢复 / 撤销修正 / 确认复核）| **原因对话框内的红色错误行** + toast |
| `ERR_REASON_REQUIRED` 等（成就进度）| **成就进度卡内、字段下方的红色说明行** + toast |
| `ERR_NPCAP_MISSING` / `ERR_FFXIV_NOT_RUNNING` / `ERR_CAPTURE_*` | toast（页面本身已经在解释同一件事） |
| `ERR_EXPORT_FAILED` / `ERR_DB_*` | toast |
| 连接中断 / 超时（新增 / 修正）| **修正对话框内的红色错误条**：请求已发出、却没有收到能说明是否保存成功的回应时（客户端等待超时、管道断开，或采集服务答复内部错误），说明记录可能已经保存，表单冻结，只能「原样重试」或关闭窗口；请求根本没有发出时（未连上采集服务、写入被拒、请求超过帧上限），说明「这次提交未能发给后台服务，记录尚未保存。连接恢复后可重试。」，表单保持可编辑 |
| 连接中断 / 超时（其他）| toast + 顶栏 Collector 行 |

为使上表第一、二行成立，两个对话框在提交后**不立即关闭**：
按钮变为「提交中…」并禁用，直到 `AppController::mutationSucceeded` 到达后才关闭。
否则后端的拒绝理由只能显示在一个已经取代表单的 toast 上。
`mutationSucceeded` 与 `mutationFailed` 都带有请求的种类与记录编号，每个窗口只处理自己那次请求的
回应：别处（「本次导随结果」、设置页的成就进度）同时在途的请求成功或被拒，都不会关闭或写入另一个窗口。
原因对话框在请求在途期间不能经 Esc 或「取消」关闭（点击窗口之外从不关闭它）。

新增或修正的提交发出之后没有收到能说明是否保存成功的回应时（客户端等待超时或管道断开，桌面端按 `ERR_INTERNAL` 处理；
或采集服务自己答复 `ERR_INTERNAL`），
采集服务可能已经保存了这条记录。向导因此冻结表单，「保存」改为「原样重试」：它以同一请求编号重发
内容完全相同的请求，由采集服务的幂等机制保证只生效一次（`HistoryController` 对内容未变的重新提交沿用
原请求编号）。改动内容后再提交会得到新的请求编号，可能多出一条记录，因此在收到回应之前不允许编辑。

请求在写入管道之前就被拒绝时（未连上采集服务、写入被拒，或请求超过 4 MiB 帧上限），桌面端以
`BackendReply::neverSent()` 标明它从未发出，`mutationFailed` 随之带上这一标志。采集服务不可能保存过这样的请求，
因此可编辑的表单保持可编辑，错误条在原因之后写「这次提交未能发给后台服务，记录尚未保存。连接恢复后可重试。」。
表单已因此前一次发出而未得到回应的提交冻结、「原样重试」又未能发出时，表单仍保持冻结，错误条写
「这次重试未能发给后台服务，上次提交可能已经保存。为避免重复，只能原样重试或关闭窗口；关闭后请先在历史记录中确认，再决定是否重新填写。」

`GetRunRevisions` 是只读接口；撤销经 `UndoRevision` 实现。详情浮层「修改记录」页签只在最新一条修订
（不含第 1 条创建修订）上提供「撤销」按钮（`undoRevisionButton`），点击后经原因对话框提交；
被撤销的修订保留在链上，撤销本身追加一条新修订，规则见 [manual-correction.md](manual-correction.md) §1。
采集服务拒绝撤销时（例如程序为未完结记录写下的系统修订），拒绝原因显示在原因对话框内。

## 4.8 Collector 生命周期在界面上的呈现

`CollectorProcess` 的状态直接映射到标题栏的圆点与文案（`App.collectorStatusText`）。界面一律称采集服务为「后台服务」，
不出现组件名 Collector；侧栏状态面板没有后台服务一行（§1）：

| 状态 | 文案 | 触发 |
|---|---|---|
| `missing` | 缺少后台服务，软件可能没有安装完整。请重新安装后重试。 | exe 同目录与其 `collector\` 子目录中都没有 `MentorRecorder.Collector.exe`。以 `MR_DEV_COLLECTOR_DISCOVERY` 编译的开发构建改为「未找到 Collector（期望位置：…）」并给出构建提示 |
| `idle` | 后台服务尚未启动 | 尚未启动 |
| `starting` | 后台服务正在启动… | `QProcess::start()` 已发出 |
| `running` | 后台服务已启动 | 本软件启动的子进程仍在运行 |
| `reused` | 检测到已有后台服务；占用租约的旧实例没有应答时为「检测到已有后台服务（占用者无响应）」 | 子进程以退出码 4 退出（另一个实例已持有单实例租约）或退出码 6（持有租约的实例没有应答），旧版采集服务则是短时退出而管道已连上；子进程未在运行而管道连上时同样进入此状态 |
| `exited` | 后台服务已停止（代码 N），将在 x.x 秒后重试启动（第 N 次）；崩溃时括号内为「崩溃」，没有排定重启时只写前半句 | 非预期退出，指数退避 0.8 s → 30 s；连续运行满 60 秒后退出时退避才重新从 0.8 s 开始 |
| `exited`（拒绝启动） | 后台服务无法启动：<原因> | 退出码 2（无法识别启动参数）、尚未开始服务就以退出码 3 退出（数据库或数据目录不可用，原因取自采集服务写到标准错误的最后一行），或 Windows 连续两次拒绝运行该程序。不进入重启循环，同一原因只提示一次；退出码 3 每 5 分钟静默重试一次 |
| `stopped` | 后台服务已停止 | 由本软件主动停止 |

管道已连上而本软件自己的子进程不在运行时（即沿用了此前启动的后台服务），标题栏不显示上表的句子，
而写「后台服务运行中（沿用之前启动的）」；使用模拟数据时写「模拟数据」。

`reused` 是必须单独区分的一档。Collector 以一个具名事件实现每用户单实例租约，
第二个实例会立即带错误退出。若将其判定为崩溃，将导致无意义的重启循环。
判据首先是退出码 4（不依赖管道是否已先连上）；不带该退出码的旧版采集服务按「短时退出且管道已连接」判断，
并在重启定时器触发时再判定一次，因为子进程可能先于 socket 的 connected 信号退出。

桌面端每个用户只运行一个（`SingleInstanceGuard`）：第一个实例持有一个以当前用户管道名命名的
具名互斥量；再次启动时，新进程置位第一个实例等待的具名事件，使其把窗口调到前台，随后自行退出，
不会出现第二个托盘图标、第二路播报或第二个 Collector 管理实例。截图运行与模拟后端的运行不受此限制。

退出路径为：`AppController` 析构，调用 `CollectorProcess::stop()`。子进程由本软件启动、
且 `serve.pid` 尚未写出或记录的正是它时，先置位 Collector 的停止事件（`Local\<管道名>.stop`），
最多等待 10 秒让它正常收尾；没有停止事件可用或到时仍未退出，才执行 `terminate()`，0.5 秒后执行 `kill()`。
托盘的「退出」菜单项，以及在「关闭时最小化到托盘」**关闭**的情况下关闭窗口，均走这条路径；
该开关开启时，关闭窗口只把窗口隐藏到托盘。
进程被 `Stop-Process` 或任务管理器强制结束时不会执行析构。子 Collector 以
`--serve --parent-pid <桌面端 pid> --parent-start-time <启动时间>` 启动，自行监视桌面端进程，
发现其结束后走与 Ctrl+C 相同的停止流程，10 秒内未完成则强制退出，因此不会留下孤儿进程。

## 4.9 游戏内浮窗与结果确认弹窗

设计稿为 Design 画布 `Hj9eFWAHmSJETMTfcNf86n` 中「游戏内浮窗 + 本次结果确认弹窗」一页：图形为主、少文字，
两者都跟随界面风格、深浅主题与界面语言。浮窗与弹窗都是无边框、始终置顶的工具窗口
（`Qt.Tool | FramelessWindowHint | WindowStaysOnTopHint`，不设 `transientParent`），
只有底板带透明度，文字与图形始终不透明。

### 4.9.1 导随统计浮窗

入口为主窗口标题栏的「统计浮窗」按钮与托盘菜单；状态由 `StatisticsOverlayController`（`App.statisticsOverlay`）持有，
窗口为 `overlay/StatisticsFloatingWindow.qml`，模块数据集中在 `overlay/OverlayData.qml`。

* **两种形态**：完整浮窗（默认 300 × 560，可拖动右下角缩放，宽 240–520）与迷你条（沿用完整浮窗当前宽度，高度随内容）。
  两者共用位置；迷你条不改变记忆中的完整尺寸，展开恢复完整高度。切换按钮均位于右侧按钮组首位（24 px 点击区）：完整窗依次为缩小、自定义、锁定、关闭，mini 依次为展开、锁定。两组右端对齐，形态持久保存。
  迷你条按自己的尺寸放进屏幕（`StatisticsOverlayController::setPosition`）：贴在屏幕底边时停在原处，
  内容多出第二行时向上长高而不越出屏幕，第二行消失后保持上移后的位置；切回完整浮窗时，完整尺寸按迷你条所在位置放进屏幕。
* **标题栏**（30 px）：左侧为拖动点、徽记、「导随统计」，右侧依次为收成迷你条、自定义、锁定、关闭；空白处可拖动移动。
* **形态切换**：使用 `Theme.motionControl`（200 ms）同步收展高度、必要的纵向移动和分段淡入淡出；出场完全淡出后才显示入场内容，标题、数字不叠影。职能柱图按有界比例变化并裁剪在轨道内。
  切换中可反向，出场内容不接收输入；锁定、自定义、关闭及内容操作暂时禁用，防止连续点击原展开位置时误触透明的锁定按钮。
  动画帧不保存为完整尺寸；关闭停止，重开直接恢复稳定目标。减少动态效果时立即切换，过渡途中开启该设置也立即结束动画。
  迷你条在最窄宽度优先保留切换、锁定、计数与百分比，进度条可收缩；小于 300 宽时省略瞬时「+N」。
* **模块**（`components/Overlay*Module.qml`，顺序与开关由自定义决定）：累计进度（大号数字、目标、百分比、带四分刻度的进度条，
  新增通关后显示「+N」6 秒；修改起始次数或目标、以及这类保存进行中时进度的变化不计为新增）、当前副本（职业徽章、副本类型角标、名称与状态标记：进行中绿点、通关对勾、结果未知「?」、
  已匹配时为红色圆环里的红色铃铛（各风格都用 `Theme.red`），每次红色脉动开始扩张时铃铛快速摇动几下；标记的盒子与脉动最大时一样宽，排在行尾也不被浮窗边缘裁掉）、
  副本用时（大号时间、与该副本平均用时的差值、以平均用时为刻度的进度条——该副本还没有平均用时时以同类副本的平均用时为刻度，
  右侧注「同类均」，两者都没有时不显示进度条；已匹配时计时旁是沙漏——沙子流完、稍停后翻转再流，进度条处为滚动条纹）、
  副本统计（按类型通关的堆叠条、角标图例与出现次数排行）、职业统计（按职能的柱状列与职能图标）、
  待处理记录（数量、每条一个按类型着色的圆点，点击从最新的一条起在结果弹窗中逐条处理；条数增加时闪动并短暂显示「+1」）。
  当前副本与副本用时只在有当前导随（已匹配、副本中、刚通关或结果待确认）时显示，待处理记录只在有记录时显示。
  状态标记的脉动与铃铛摇动、计时沙漏的流沙与翻转、已匹配时的滚动条纹这类循环动画只在浮窗显示（且未最小化）时运行。
* **文字标签**默认关闭：模块名、图例名与职能名不显示，鼠标悬停在模块、分段或按钮上时以提示框给出名称与数字；打开后随内容显示。
  提示框是独立的小窗（`overlay/OverlayToolTip.qml`，`popupType: Popup.Window`），不受浮窗边界约束：显示在所悬停项目的上方，
  贴近屏幕顶端没有空间时改到下方；迷你条上的提示框以整条为基准，不会盖住条上的数字或按钮。
  按钮落在可悬停的条上（标题栏的拖动区、迷你条的进度行）时只显示按钮自己的提示，不同时显示条的提示。
* **副本类型色**与副本统计页的类型图例一致（`DungeonStatsModel::categoryBreakdown` 的 token，§4.3），角标为单字（迷 / 讨 / 大 / 团 / 令；
  极神只作为图例分段出现，角标为「极」）；
  职能色取自 `JobCatalog::tokenForRoleGroup`。累计进度来自 `dashboard.achievement_progress` 与 `goalCount`，
  当前副本来自 `App.currentRun`，进本后的用时来自 `liveElapsedMs`（由 `GetCurrentRun.elapsed_ms` 外推，自进入副本起计，
  进本之前该字段为空），已匹配阶段由 `matched_at_utc` 起算，平均用时取副本统计行的 `avg_duration_ms`
  （同类副本的平均用时为同类各行 `avg_duration_ms` 按 `completed_count` 加权），
  待处理记录来自 `App.recovery.items`。
* **自定义**面板替换浮窗内容：模块列表可拖动握把排序（也可在握把上按 ↑ / ↓，或用悬停时出现的上下箭头），开关控制显示，
  关闭的模块保留原位；其下为副本排行（关 / 前 3 / 前 5 / 前 10 / 全部）、背景不透明度（30–100 %）与文字标签开关；
  「恢复默认」还原模块、排行、不透明度与标签，「完成」返回内容。
* **锁定**：隐藏标题栏与缩放角，窗口对鼠标穿透（`WindowTransparentForInput | WindowDoesNotAcceptFocus`），
  在主窗口标题栏按钮或托盘菜单解锁；锁定不持久，重新打开浮窗时总是解锁状态。
* 设置项位于 `statistics_overlay/*`：`geometry`、`opacity`、`modules`（显示中的模块）、`module_order`（全部模块的顺序）、
  `row_limit`、`form`、`labels`；可见与锁定不保存。无效值由控制器收敛到默认（不透明度 0.85，排行前 3，全部模块开启）。

### 4.9.2 本次结果待确认弹窗

结果问句与心得提醒（§4.5.2）的弹窗由同一个 `ReflectionDialog` 承担：`standalone` 时切换为
`dialogs/ResultPopupContent.qml` 的 320 px 紧凑布局，由 `RecoveryReminderWindow` 承载，放在浮窗左侧 12 px、顶边对齐
（左侧放不下则放右侧；浮窗未显示时靠屏幕右上角）。自动提醒按提醒方式弹出（§4.5.2），从待处理打开的记录同样用这个弹窗。
收起按钮、「本次结果待确认」/「本次笔记待补录」一类标题、「关闭只会收起提醒……」的说明与「已收起」提示条
只属于这个弹窗（`reminderChrome`）；主窗口内的补录 / 编辑心得对话框保持自己的样式，但 Esc 同样经由收起逻辑保存草稿，草稿保存失败时窗口保持打开并显示原因。
主窗口对话框中输入的内容同样随时存为草稿，在保存之前列入待处理（标为「有草稿」），因此不会丢失；
主窗口隐藏到托盘时，未在提交中的该对话框按「稍后补录」收起，草稿保留。

* **标题栏**：徽记、结束时间（自动弹出时为「刚刚结束」，从待处理打开时为结束时刻）、从待处理打开且尚未回答时、待处理多于一条的翻页（上一条 / 下一条，
  经 `pageQueue` → `RecoveryController.requestOpen` → `Main.qml` 的 `onOpenRequested` 识别 `pendingSwitchId` 后在原窗口内 `switchTo`，不关窗）、
  提醒开关（铃铛：结果弹窗控制「结束后自动询问」，心得提醒控制「通关后提醒记笔记」）、ⓘ 说明与「收起」。
* **内容**：职业徽章、类型角标、副本名与用时；结果未知时显示「通关了吗？」与 通关 +1 / 退出 / 稍后 三个答案。
  与主窗口对话框不同，回答后窗口不关闭：结果改以结果框呈现，下方是三个心情表情与一行心得输入框（输入时增高），
  按「完成」才保存心得并关闭；没有写心得时「完成」直接关闭并视作已处理。
  结果框只在采集服务认定该记录计入进度（`contributes_to_goal === true`）的通关上写「已计入导随次数」；
  其后的「N → N+1」两个数字都取自采集服务的 `achievement_progress`：前者是弹窗为自动记为通关的记录自动弹出时、
  或回答「通关 +1」时持有的值，后者是随后再读一次总览（`App.readGoalProgress`）得到的值（回答「通关 +1」时在采集服务
  接受答案之后读取），两者恰好相差 1 时才显示，桌面端从不自行加一。退出写「已记为退出 / 放弃，不计入」，其他结果写结果名称。
  记录仍待复核而结果已知时（崩溃恢复记为中断，或校准被撤下而保留原结果），「完成」旁另有「在历史记录中复核」：
  保存草稿并关闭弹窗（不出现「已收起」提示条），把主窗口带到前台，在历史记录中选中该记录并打开「确认待复核记录」的原因对话框（与详情浮层的「确认已复核」相同）。
  因此写入顺序为先 `CorrectRun` 后 `SetRunReflection`，而主窗口对话框仍是先心得后结果。
  已自动记为通关的记录（心得提醒）直接显示结果框。未识别职业时在副本行下补一行职业选择，随答案一并写入。
* **收起 / 稍后 / Esc**：草稿保留，记录进入待处理；浮窗的待处理条目在条数增加时闪动并显示「+1」，
  弹窗原位出现「已收起到待处理，记录和草稿已保留」的提示条（6 秒，可「重新打开」；待处理列表出错时提示条改写出错原因）；提示条只在该记录仍在待处理中时出现，记录已被删除且没有草稿时直接收起、不显示提示条。
  与采集服务的连接断开、无法确认数据库身份时，弹窗同样按收起处理：草稿保留，出现同一提示条；
  切换到另一个数据库时，弹窗关闭而不出现提示条，草稿留在原数据库的待处理中。回答之后再收起等同于「完成」；若那次保存被拒绝（错误显示在窗内），再次收起按普通方式关闭并保留草稿，
  因此窗口总能被收起。清空已保存的心得后按「完成」会删除该心得（契约规定空正文即删除）；未识别职业时补选的职业随答案写入，
  之后的「完成」不再重复提交。
* 备注图片不在弹窗中提供，仍在历史记录详情中添加。

## 5. 手动修正对话框与校验

校验规则全部位于 C++ 的 `RunFormValidator` 中，QML 对话框与单元测试共用同一份实现。
本地拒绝时返回的错误码与 Collector 返回的错误码一致：

| 情况 | 错误码 |
|------|--------|
| 未填写修正/新增原因 | `ERR_REASON_REQUIRED` |
| 原因超过 500 字、备注超过 1000 字（表单内说明上限与当前字数） | `ERR_BAD_REQUEST` |
| 进本时间早于匹配时间 | `ERR_TIME_ORDER` |
| 无进本时间且结束时间早于匹配时间 | `ERR_TIME_ORDER` |
| 结束时间早于进本时间（耗时为负） | `ERR_NEGATIVE_DURATION` |
| 修正模式下没有任何字段变化 | `ERR_NO_CHANGES`（`RunFormValidator` 的说明为「内容还没有变化，无需保存。」；向导提交前自行发现没有改动时，错误条写「还没有修改记录内容。」） |
| 日期/时间格式非法 | `ERR_BAD_REQUEST` |

进本时间与结束时间都可以留空，与结果无关（2026-10-21 起；此前除「进本前取消」外都必须填进本时间，
通关必须填结束时间，只有历史导入豁免）。留空时耗时未知、不计入平均耗时，记录仍按结果计入次数、完成率与进度
（[statistics-definitions.md](statistics-definitions.md) §2）；填写了的时间照常接受顺序检查。
向导第三步的提示按结果说明这一点，「估算进本」仍可按匹配时间补上进本时间。

对话框在修正模式下显示原型的「修改前后对比」三列表格，
三列分别为 字段 / 修改前（删除线）/ 修改后（强调色）。
表格的行由 `RunForm.diff()` 生成，顺序固定为
副本 / 职业 / 匹配时间 / 进本时间 / 结束时间 / 结果 / 计入进度 / 进入方式 / 备注。
「进入方式」在第 1 步（`RunWizardResultStep`）以三枚 `PickChip` 选择：不清楚（`null`）/ 开局进入（`false`）/
中途加入（`true`），对应 `$defs/Run.joined_in_progress`；修正时预填记录的值，补录时默认「不清楚」。
修正提交只在值改变时带上该字段（`null` 与采集服务未给出的字段视为同一个值）。
没有差异时显示「还没有修改记录内容。」（`noChangesText`）。

首次启动的 baseline 对话框（`dialogs/BaselineDialog.qml`）按原型排版，自上而下为：
`首次启动 · 1 / 1` kicker、大号标题「开始记录前，你已完成多少次导随？」、一段说明（起始次数是开始用本软件记录前
已经完成的导随次数，总进度 = 起始次数 + 软件中计入进度的通关次数；导入旧记录时，若确认它们已包含在起始次数中，
软件会相应扣减）、两个大字号数字输入（「起始次数」与「目标」，起始次数即下文的基数）、
进度预览，以及 `从 0 开始` 与 `保存并开始` 两个按钮。首次启动时该对话框不能经 Esc 关闭。
从设置页「重新打开首次引导」打开时（`openDialog(true)`），kicker 改为「起始次数」，两个输入预填当前的
基数与目标，并多出「取消」按钮，Esc 同样关闭对话框而不做任何修改；保存时一并提交基数与目标。

两个输入预填的同样是从采集服务读到的已保存值。读到之前（`settingsLoaded` 为假）两个输入框、`从 0 开始` 与
`保存并开始` 均不可用，说明行写「正在读取已保存的目标和起始次数，读取完成后才能修改和保存。」；
首次启动时此时另有「稍后填写」按钮，关闭对话框而不保存任何内容，下次启动时再次询问。
值在对话框打开之后才读到时，用户尚未动过的输入换成已保存的值。

`从 0 开始` 提交的是对话框中显示的目标（与 `保存并开始` 一样先检查是否为大于 0 的整数，否则提示「目标值必须是大于 0 的整数。」）
与基数 0。`保存并开始` 不把空的起始次数栏当作 0，而以「起始次数必须是大于等于 0 的整数。」拒绝；以 0 为基数应使用 `从 0 开始`。
按下任一保存按钮后对话框保持打开，`保存并开始` 改写为「保存中…」；应答到达之前，两个输入框、两个保存按钮与
「取消」/「稍后填写」均不可用，Esc 与点击窗外也不关闭对话框。采集服务接受后对话框才关闭，首次启动随之记为完成；
被拒绝或连接断开时，对话框保留已填写的内容，并以红字显示采集服务给出的原因。首次启动时，保存失败之后同样出现
「稍后填写」，可以不保存即关闭对话框，下次启动时再次询问，因此即使采集服务一再拒绝保存，也总能离开对话框。
另有一次成就设置的保存（例如来自设置页）尚未得到应答时，两个保存按钮同样不可用。

进度预览（`baselinePreviewText`）只在读到已保存的值、且软件已记录计入进度的通关时出现，写作
「软件中已有 N 次通关计入进度，总进度为：B + N = P。如果按游戏内成就面板的总次数填写起始次数，请先减去这 N 次，避免重复计算。」
其中 N 为采集服务给出的 `achievement_progress` 减去已保存的基数，B 为起始次数栏中正在填写的数
（栏内为空或不是大于等于 0 的整数时取已保存的基数），P 为两者之和
（[statistics-definitions.md](statistics-definitions.md) §4）。

新增与修正采用三步向导。状态与逻辑位于 `dialogs/EditRunDialog.qml`，
三步的界面分别为 `RunWizardResultStep.qml`、`RunWizardDutyStep.qml` 与 `RunWizardTimeStep.qml`。
向导宽 `min(720, 窗口宽 − 40)`、高 `min(680, 窗口高 − 32)`；
标题、步骤条（可点击，显示每步当前值）与底部按钮栏固定，只有正文滚动。

* 第 1 步「结果」包含六张结果卡（`PickSurface`，颜色取自 `Fmt.resultColorToken`，
  结果代码只在维护者模式下显示）、开关「计入导随成就进度」（`goalToggle`，小字「这里只补录指导者任务。
  关闭后不增加成就进度，仍计入次数、通关率等统计。」）与进度预览（`progressPreview`）。
* 第 2 步「副本与职业」包含：人数分段（全部 / 4 人本 / 8 人本 / 24 人本 / 其他，
  取自 `DutyCatalog` 的 `party_size`）；搜索（支持名称、等级、`7.2` 一类版本号按资料片匹配、
  资料片中文名与种类）；等级区间与难度标签（`PickChip`，难度只在 全部 / 8 人本 / 24 人本 时出现）；
  「最近打过」（`App.recentDuties`，搜索时隐藏）；最多 210 项的列表与「共 N 个副本」。
  缺少人数、难度或等级的行，只在对应筛选为「全部」时列出。
  职业按 `Roles.roleGroups()` 分组，存在图标时使用 `JobIcon`，否则显示职能色的缩写方块。
* 第 3 步「时间与原因」包含：日期（`DateField`，可用日历选择或手动输入）；
  匹配时间（必填）、进本时间与结束时间（两者都可留空；`TimeField`，只需输入数字，
  2130 转为 21:30、930 转为 09:30、213045 转为 21:30:45，离开字段时 9:5 转为 09:05；
  右侧时钟图标打开时 / 分滚轮，另有「现在」）；
  跨天时另有进本日期与结束日期，日期不同时自动出现，修改「日期」只带动仍处于原日期的那两个日期；
  随结果变化的提示、按匹配时间估算进本时间、原因（必填）、备注、备注图片、修改前后对比与错误条。
  原因与备注的提示均以「（按 Tab 填入）」结尾，栏内为空时按 Tab 即填入示例「程序未运行时手动补录」
  （规则同设置页「成就」的修改原因）。
  任何拒绝（本地校验或采集服务）都会跳转到这一步。
* 备注图片（`NoteImageStrip`，`editable`）：已有图片与本次新选的缩略图（后者标「待保存」）、
  每张右上角的移除、「添加图片」（系统文件选择框）以及一行说明保存位置的提示。
  添加与移除只在对话框内暂存（`pendingImageAdds` / `removedImagePaths`），
  记录保存成功后才由 `NoteImages.commit()` 写入安装目录，取消即放弃。
  只改图片时不发送修正、不产生修订，修正模式的主按钮「保存修正」在仅有图片改动时也可用；
  记录已保存而图片写入失败时对话框保持打开并给出原因（`savedRunId` / `recordSaved`）：
  前两步、时间、原因与备注字段锁定，按钮改为「重试图片」与「关闭」，再次保存只重试图片，
  不会把记录再提交一次。不是图片、超过 20 MB、分辨率超过 5000 万像素（先看文件头）、
  截断或无法解码（选择时完整解码一次）、超过 20 张的选择在第三步的错误条里说明。复制到一半失败时已复制的文件一并回滚，
  且不计入「已添加」，暂存列表原样保留，再次保存整批重试。对话框打开期间别处（心得弹窗）
  添加或移除了图片，向导监听 `NoteImages.imagesChanged` 重新读取已有图片，
  已排队删除、但文件已不存在的项随之作废。
* `dutyIndex` 与 `jobIndex` 仍是 `currentDutyOptions()` 与 `currentJobOptions()` 中的下标，
  0 表示未知。筛选不改变这两张表，因此切换筛选不会丢失已选的副本。

## 6. 主题与界面风格

`Theme.qml`（单例）是唯一的调色板来源，包含**两个正交的开关**：

| 开关 | 取值 | 来源 |
|---|---|---|
| `Theme.dark` | 深 / 浅 | `App.dark`（`themeMode` = dark / light / system） |
| `Theme.uiStyle` | `classic` / `eorzea` / `harendotes` | `Settings.uiStyle`（`--mock-ui-style` 优先），未知值按 `classic` |
| `Theme.eorzea` | 镀金系（艾欧泽亚、Harendotes）/ 经典 | `Theme.uiStyle !== "classic"`：布局、字体、圆角、动效按此分支 |
| `Theme.harendotes` | Harendotes / 其他 | `Theme.uiStyle === "harendotes"`：只影响色板，经 `Theme.gilded()` 选值 |

`Theme.eorzea` 的读取方式**具有防御性**：`Settings` 不存在、`uiStyle` 未定义或为空串时，
一律按经典风格（默认风格）渲染。因此旧配置文件或旧版 `AppSettings` 缺少该键时，界面仍可正常显示。
页面代码从不判断风格，只绑定 token；四种组合（艾欧泽亚与经典，各配深色与浅色）
由 `Theme.qml` 一处决定。

所有 `Canvas`（成就进度环、职业占比环、下拉框箭头）都监听 `Theme.dark` 与
`Theme.eorzea` 的变化并重绘。

### 6.1 token 映射（`ff14.css` / `workbench.css` → `Theme.qml`）

镀金系的每个颜色 token 写作 `eorzea ? gilded(艾深, 艾浅, H深, H浅) : 经典`，
`Theme.gilded()` 按 `harendotes` 与 `dark` 取其一。Harendotes 的取值（深 / 浅）：

| 规格名 | Theme 属性 | 深色 | 浅色 |
|---|---|---|---|
| 背景 | `contentBackground` | `#0a0d16` | `#e4e8f1` |
| 面板 / 面板 2 | `surface` / `surfaceRaised` | `#111726` / `#182034` | `#f3f5fa` / `#ffffff` |
| 主毛 · 状态面板 | `statusPanelBackground` | `#263961` | `#ffffff` |
| 主文字 / 次文字 | `textPrimary` / `textSecondary` | `#eef0f6` / `#97a0b8` | `#141a2b` / `#4f5a75` |
| 分割线 | `border` / `gold3` | 蓝灰 22 % | 海军蓝 20 % |
| 橙焰 · 主色 | `accent` / `gold` | `#e85018` | `#c8420f` |
| 亮焰（深）/ 深焰（浅）· 悬停、链接 | `accentStrong` / `gold2` | `#ff7a3d` | `#a83408` |
| 暗焰（深）/ 亮焰（浅）· 边框 | `buttonBorder` | `#9a330c` | `#e85018` |
| 古金 · 仅点缀 | `oldGold` / `yellow` | `#c8a45a` | `#8a6a24` |
| 蓝焰 · 强调 | `teal` | `#1ab8cc` | `#0f8d9e` |
| 埃及青 · 信息 | `blue` | `#5fa8d3` | `#2f6f9e` |
| 成功 / 黄焰 · 警告 / 危险 | `green` / `orange` / `red` | `#6fbf8a` / `#c8a020` / `#e74c3c` | `#35804e` / `#8f7010` / `#b8322a` |

在 Harendotes 中 `gold` 系即"焰"：原先绑定金色的标记、小标题、导航选中条都随之变为橙焰，真正的古金只经 `oldGold`
出现在面板外框与标题栏底线的尾段。成就进度环在此风格下沿弧线渐变（components/ProgressRing.qml 的锥形渐变）：12 点方向的起点为黄焰（`ringTail`，深 `#ffd84a` / 浅 `#f7bc1f`），末端为深橙焰（`ringStart`，深 `#e8401a` / 浅 `#c8380f`），两端在色相与明度上都拉开，全程不透明：半透明或低饱和的色段叠在轨道上会发灰；弧头为圆头，起点仍为平头（只在末端补一枚 `ringStart` 色的圆片——`lineCap: round` 会连 12 点方向的起点一起圆掉，而锥形渐变在那里由 `ringTail` 直接折回 `ringStart`，起点的圆头会显出末端的深橙）；艾欧泽亚仍为对角直线渐变，弧头同样补圆片，圆片直接取同一直线渐变填充。标题栏经 `titlebarTop/Bottom/Brand/Text/TextSecondary/Mark/Fill` 取色，
各风格下都与侧栏同底；规格原定的浅色海军蓝顶栏实机观感过重，已改为浅色。「深色 / 浅色」切换按钮在三种风格下
一律按"对方主题的底色与文字"反色绘制（`inverseBackground` / `inverseText`）；
导航选中底与开关不再写死金色字面量，改由 `navActiveStart/End`、`switchTrackOn`、`switchKnobTop/Bottom` 给出。

经典一列是 `workbench.css` 的深色 / 浅色取值。

| CSS 变量 | Theme 属性 | 说明 |
|---|---|---|
| `--color-bg` | `contentBackground` | 艾欧泽亚 深 `#222222` / 浅 `#c6b387`；经典 `#141922` / `#f5f7fa` |
| `--color-surface` | `surface` | 经典 `#1b212c` / `#ffffff` |
| `--color-surface-2` | `surfaceRaised` / `surfaceMuted` | 经典分别等于 `surface` / `fill` |
| `--color-text` | `textPrimary` | 经典 `#e8ecf2` / `#1c2430` |
| `--color-text-2` | `textSecondary` | 经典 `#a4adbb` / `#5d6675` |
| `--color-text-3` | `textMuted` | 经典 `#7b8494` / `#8a93a1` |
| `--color-divider` | `border` | 经典 `#2b3342` / `#e4e8ee` |
| `--color-fill` / `--color-fill-2` | `fill` / `fillStrong` | 经典 `#252d3a` / `#eceff3`、`#2f384a` / `#e2e6ec` |
| `--color-neutral-300` | `borderStrong` / `neutral300` | 经典按钮、输入框、关着的开关 |
| `--color-gold` / `-2` / `-3` | `gold` / `gold2` / `gold3` | 艾欧泽亚 `gold` / `gold2` 只用于标记、侧栏标题、进度环与柱条；`gold3` 是面板边线，取暖灰 `#7a7260` / `#a8946c`。经典按 workbench 退化为 `textSecondary` / `textPrimary` / `border` |
| `--color-accent` | `accent` | 艾欧泽亚 = 金色；经典 = `#5b8ff0` / `#2f6bd8` |
| `--color-accent-600` | `accentStrong` | 主按钮悬停；经典 `#7aa4f5` / `#2559b8` |
| `--color-accent-700` | `accent700` | 浅色标题色；经典 `#9dbcf8` / `#1e4a9a` |
| `--color-accent-100` | `accentMuted` | 选中行底色、导航选中底、输入框 focus ring；经典 `rgba(91,143,240,.18)` / `#e8effc` |
| `--color-green/orange/red/yellow/purple/teal/blue` | 同名属性 | 经典 `#31a24c` / `#dfa937` / `#d92b2b` / `#e9b949` / `#7b5cd6` / `#2b8fb8` / = accent |
| `--color-green-bg` / `--color-orange-bg` / `--color-orange-fg` / `--color-red-bg` | `greenBackground` / `orangeBackground` / `orangeForeground` / `redBackground` | 经典标签的底色与橙色字 |
| `--color-neutral-100…800` | `neutral100` / `neutral300` / `neutral400` / `neutral500` / `neutral600` / `neutral800` | |
| `--inset-bg` / `--inset-bg-2` | `insetBackground` / `insetBackgroundStrong` | `components/InsetBox.qml`；经典 `#212936` / `#f3f7fe`、`#2a3341` / `fill` |
| `--inset-border` | `insetBorder` | 艾欧泽亚 = `border`；经典透明 |
| `.status-panel` | `statusPanelBackground` / `statusPanelBorder` | 经典 = `--color-bg` + 1 px divider |
| `.dialog-backdrop` | `blackScrim` | 经典 `rgba(28,36,48,.4)`；艾欧泽亚对话框沿用 Qt Basic 的半透明黑 |
| `--badge-fg` | `badgeForeground` | 职业色块上的文字 |
| `--panel-bg` 的两个 stop | `panelTop` / `panelBottom` | `components/Card.qml` 的渐变 |
| `--chrome-bg` 的两个 stop | `chromeTop` / `chromeBottom` | 标题栏 + 侧边栏 |
| `--bar-fill` | `barFillStart`（gold-3）/ `barFillEnd`（gold-2） | 趋势柱、统计条 |
| `--rule-accent` | `ruleAccent` | 页头金线的亮段 |
| `<linearGradient id="ffgold">` | `ringStart` / `ringEnd` | 进度环描边 |
| `.ring-track` | `ringTrack` | |
| `.btn-primary` 的三个 stop | `buttonPrimaryTop/Mid/Bottom` + `buttonPrimaryText` / `buttonPrimaryBorder` | 艾欧泽亚 = 游戏按钮：比窗口更深的灰 / 棕渐变，浅色 1 px 边与浅色文字 |
| `.btn` | `buttonBorder` / `buttonText` | 艾欧泽亚次级按钮：`surfaceRaised` 底、`gold3` 边、`textPrimary` 字 |
| 页签选中态 | `tabActiveTop/Bottom/Border/Text` | 艾欧泽亚 = 游戏页签：深灰 / 深棕底、浅字（`components/SegmentedControl.qml`）；经典 = accent |
| `--radius-xxs/xs/sm/md/lg` = 1/2/3/4/6 | `radiusXxs/Xs/S/M/L` | 经典风格 = 3/4/6/8/10 |
| body 的两层 radial wash | `washTop` / `washBottom`（+ `washBottomRadiusX/Y`） | `Main.qml` 的 `backdropWash` Canvas 用 `createRadialGradient` 画真正的两层径向渐变，位置、半径、颜色同原型；右下角一层在经典浅色改为浅金 `rgba(255,226,160,.30)`，在 Harendotes（深浅均是）改为亮橙 `#ff7a3d` 16%，两者范围都放大到 760×520、70% 处淡出 |
| `h1..h4` 的颜色 | `headingColor` | 艾欧泽亚 深 `#f6f1e4` / 浅 `#3a2c17`（游戏窗口标题不是金色）；经典 = `textPrimary` |

下列辅助函数把 CSS 的类名收敛到一处：

* `Theme.tagBackground/tagForeground/tagBorder(variant)` 对应 `.tag` 及其变体
  `tag-accent`（橙）、`tag-neutral`（灰）、`tag-outline`（黄）、`tag-ink`（绿）与 `tag-blue`。
  艾欧泽亚风格为 1 px currentColor 描边加半透明底；经典风格为 workbench 的浅底药丸：
  accent 与 outline 为橙底橙字，neutral 为 fill 底加次要字色，ink 为绿底绿字，
  blue 为 accent-100 底加 accent 字色，字号 11 px 常规。
* `Theme.fs(px)`、`pageTitleSize` 与 `dialogTitleSize(px)` 见 §3 的字号表。
* `Theme.figureWeight(bold)`、`dimOpacity(v)` 与 `dimColor(c)`：经典风格将数字统一为 600 字重，
  并把艾欧泽亚风格中以透明度压暗的文字改为不透明的 `textSecondary`。
* `Theme.moodLabel/moodVariant(mood)` 将心得心情 `good/ok/bad` 映射为
  顺利（tag-ink）、一般（tag-blue）与 糟心（tag-accent）。

### 6.2 组件与 CSS 类的对应

| QML | CSS |
|---|---|
| `components/Card.qml` | `.panel`（渐变底 + divider 边框 + 顶部 1 px 高光） |
| `components/PanelDecoration.qml` | `.panel::before` / `::after`（左上、右下金角） |
| `components/CardKicker.qml` | `.card-kicker`（`◆` + 字距） |
| `components/PageHeader.qml` | 页头 `h2` + `border-bottom` + `linear-gradient(90deg,rule-accent,transparent 45%)` |
| `components/HeadingLabel.qml` | `h1..h4` / `.dialog-title` |
| `components/AppButton.qml` | `.btn` / `.btn-primary` / `.btn-secondary` / `.btn-ghost`（`compact` = 11 px 小号） |
| `components/NavItem.qml` | 侧栏 `.navi` / `.navi[data-active]`（经典 = accent-100 底 + accent 字 600、无左条；艾欧泽亚 = 渐变底 + 2 px 金条）。第一列是页面的 Lucide 图标（`iconName`，16 px，颜色随标签：总览 layout-dashboard、历史 history、副本 swords、职业 users、捕获 activity、设置 settings、对照核对 clipboard-check）；只给 `number` 时仍显示序号 |
| `components/Tag.qml` | `.tag` |
| `components/Chip.qml` | `.chip`（筛选开关） |
| `components/SegmentedControl.qml` | `.seg` / `.seg-opt` |
| `components/ToggleSwitch.qml` / `PillSwitch.qml` | `.sw`（艾欧泽亚 40×22 方形；经典 36×20 药丸，关 = neutral-300，开 = accent，16 px 白色圆钮） |
| `components/StyledTextField/TextArea/ComboBox.qml` | `.input` / `textarea.input` / `select.input` |
| `components/InsetBox.qml` | `background:var(--inset-bg);border:1px solid var(--inset-border)` |
| `components/StatBar.qml` | 副本 / 职业统计里的 8 px `.barfill` |
| `components/StackedResultBar.qml` | 结果分布条（12 px、2 px 间隙） |
| `components/DialogFrame.qml` | `.dialog` + `.dialog::before` 的 2 px 渐变顶线 |
| `components/ReflectionCard.qml` | 总览「导随心得」面板里的近期卡片 |
| `components/ProgressRing.qml` | 成就环：`ffgold` 渐变 + r76/r96 两圈细金线；起点平头、弧头圆头（末端补一枚同渐变填充的圆片，原型为 butt 端点，2026-10-20 按产品决定改圆头） |

经典风格的按钮按 workbench 定义：默认与 secondary 为 `surface` 底加 1 px neutral-300 边，悬停时为 `fill`；
primary 为 accent 底加 600 字重白字，悬停时为 accent-600；
ghost 为透明底加 accent 字色，悬停时为 accent-100；
禁用态为 `fill` 底加 `textMuted` 字色，不再整体降低透明度。按钮圆角 6，高 30。
经典输入框高 30，边框为 1 px neutral-300，悬停时为 neutral-400，
聚焦时为 accent 边框加 3 px accent-100 外圈。
分段控件为 `fill` 轨道加 2 px 内边距，选中项为 `surface`（深色下为 `fill-2`）、
divider 细边与 600 字重 accent 字色。
筛选 chip 的内边距为 4×8，圆角 4。
历史表行悬停时为 `insetBackground`，选中时为 accent-100 底加左侧 2 px accent 条。

带 `Behavior on color` 的颜色，静止态不得写作 `"transparent"`。
`"transparent"` 等于 `#00000000`，而 `ColorAnimation` 按 RGBA 直线插值，
从该值渐变到经典浅色的 `#f2f2f7` 时中间态是半透明灰，鼠标划过侧栏时每一项都会闪出深灰块。
静止态应写作 `Theme.clear(目标色)`，即同一颜色、透明度为 0，渐变便只改变透明度。
`UiWorkflowRegressionTests::hoverFadeRestsOnTheHoverColour` 在四种风格与主题的组合下
检查导航项与透明文字按钮各自的 `hoverColor`，经典风格下透明文字按钮的悬停色为 accent-100；
该测试同时检查主按钮的底色从不停留在透明黑上。
艾欧泽亚风格下主按钮底色藏在金色渐变之下，切换到经典风格时渐变立即消失，
底色若从透明黑开始渐变就会闪黑。

### 6.3 标题栏

标题栏为 `titlebarTop` → `titlebarBottom` 的竖直渐变（对应 `--chrome-bg`）加 1 px 下边框：艾欧泽亚系风格为 `gold3`，
经典风格为 `border`；Harendotes 另在底边叠一条由橙焰过渡到古金的细线。
左侧在艾欧泽亚系风格下是一枚旋转 45° 的镀金菱形，其后是品牌名「导随记录器」（字体经 `Theme.headingFamilyFor` 选择）与版本号。
右侧依次是后台服务状态圆点与文案（§4.8）、小号的主题切换按钮（`AppButton { variant: "inverse" }`）、
「统计浮窗」按钮与三个窗口按钮。

窗口没有系统边框，最小化、最大化与关闭三个按钮由标题栏自行绘制（§1）；标题栏不绘制 macOS 交通灯圆点。

### 6.4 动效

全部动画均读取 `Theme.qml` 的 `motion*` 时长。`Theme.motion` 为假时这些值全为 0，
动画在同一帧落到终态。
`Runtime.reduceMotion` 是 `main.cpp` 为当前 QML 引擎设置的运行参数，在两种情况下为真：
截图模式，此时帧必须确定；以及 Windows 的「在 Windows 中显示动画」处于关闭状态
（`SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` 返回假，该值只在启动时读取一次）。
缓动曲线取 workbench.css 的 `--ease`，即 `cubic-bezier(.2,.7,.2,1)`（`Theme.curveStandard`），
时长为 200–220 ms（`motionControl` 与 `motionMedium`）。

两种风格都有的微交互：

| 组件 | 规则 |
|---|---|
| `AppButton` | 按下时缩至 0.96；减少动效时不缩放 |
| `NavItem` | 悬停时右移 2 px，按下时缩至 0.97；按下时不右移，减少动效时两者均不生效 |
| `EditRunDialog` 换步 | 新一步淡入，并按切换方向滑入 16 px，下一步自右、上一步自左，时长 `motionMedium`；两种风格相同，减少动效时直接落位（`stepProgress` / `stepDirection`） |
| `SegmentedControl` | 选中项左右内边距各 +3 px，其余项各 −2 px，以宽度区分而不缩放、不降低不透明度（缩放会让原生渲染的文字发虚）；未选项悬停时文字由 `textSecondary` 加深为 `textPrimary`；按下时缩至 0.92 |
| `ToggleSwitch` | 按下时圆钮从 16 px 伸长至 20 px，贴所在一侧伸长；悬停时叠加一层 4 % 黑（`brightness(.96)`） |
| `StyledTextField` / `TextArea` / `ComboBox` | 未聚焦时悬停描边 neutral-400，描边颜色渐变 |
| 历史表行、`PickSurface` 行 | 按下时 accent-100 底（`.row-c:active`） |

仅经典风格具有的进场动画如下；艾欧泽亚风格保持淡入并上移 8 px：

* **页面**（`PageHost`）：切换到该页时从右侧 12 px 滑入并淡入，时长 300 ms（`wbSlide`）。
* **面板**（`Card`，含 `SettingsPanel` 与 `StatCard`）：所在页面成为当前页时，
  从 0.97 倍、全透明弹出，时长 200 ms；按可见兄弟项的次序逐个延迟 30 ms，
  第 5 个起使用相同延迟（`wbPop`）。不在页面内的面板（侧栏状态板、对话框）不执行该动画。
* **行**（历史记录表）：行重新载入（模型重置）时淡入并上移 4 px，时长 200 ms，
  逐行延迟 22 ms，第 12 行起使用相同延迟（`wbRow`）；历史记录页成为当前页时同样重放一次。
  单行的字段更新不重放。共用的进场逻辑位于 `components/Entrance.qml`。

总览的「完成趋势」由 `charts/GraphsTrendChart.qml` 与 `charts/TrendMotionLayer.qml` 实现，
对应原型的 `switchTrend()`。静止时由 Qt Graphs 绘制柱体；
变化期间 Qt Graphs 的柱体隐藏，改由运动层按 Qt Graphs 自身的几何参数绘制，
即每格 2 px 间隙、剩余宽度的 `barWidth`、居中对齐、圆角 4；
动画结束时在同一帧交还绘制权，因此不会出现跳变。

* 柱数不变（数据刷新）：柱高以 300 ms 过渡到新值；数据完全相同的一次刷新不产生动画。
* 柱数减少（日切换为周或月，即合并）：每根旧柱按起始时间落入新的格位
  （`slotOf`，即最后一个起点不晚于它的格），滑动到该格中心，
  同时横向缩至 0.6、不透明度降至 0.35，时长 380 ms，曲线 `cubic-bezier(.4,0,.2,1)`；
  随后新柱从纵向 0.6、不透明度 0.5 生长出来，时长 300 ms，逐根延迟 22 ms。
* 柱数增加（周或月切换为日、7 天切换为 30 天，即拆分）：每根新柱从其来源旧格的中心出发，
  初始为纵向 0.85、不透明度 0.4，移动回自身位置，时长 440 ms，逐根延迟 6 ms，
  第 13 根起使用相同延迟。
* 变化过程中再次发生变化：正在执行的过渡立即取消，新过渡从被取消过渡的目标状态开始。
  每根柱的起始时间取自数据桶的 `start_utc`。

深色与浅色的切换（经由标题栏按钮或 设置 → 通用 → 主题）以点击处为圆心展开，
由 `components/ThemeReveal.qml` 实现，对应原型的 `switchTheme()`。
流程为：先用 `grabToImage` 截取整个窗口，再切换主题，并把截图覆盖在上层；
随后用 `QtQuick.Effects` 的 `MultiEffect` 圆形遮罩（`maskEnabled`）执行 520 ms、
曲线为 `cubic-bezier(.05,.75,.2,1)` 的动画。
切换到深色时，新主题从半径 0 展开至最远的角，即旧图上的洞逐渐变大；
切换到浅色时，旧图从最远角的半径收缩至 0。
覆盖层不接收鼠标事件，动画结束即销毁。动画过程中再次点击时，前一次动画立即结束，随后开始新的一次。
`Theme.motion` 为假时，或使用无法绘制着色器效果的软件渲染器时，不创建覆盖层，直接切换。

动效的证据由隐藏参数 `--motion-probe <theme|trend-week|trend-month|trend-day|history-rows>` 产生，
使用时必须同时给出 `--screenshot out.png`。
探测过程中动画保持开启，改用手动推进的动画时钟；页面稳定后触发场景，
在 t = 0 / 80 / 160 / 260 / 400 / 600 ms 写出 `out-t<ms>.png`，`out.png` 为触发前的一帧；
2.1 s 后仍有动画在运行则以退出码 12 结束。
探测运行使用 GPU 渲染（`QT_QUICK_BACKEND=rhi`、`QSG_RENDER_LOOP=basic`）。
离屏窗口没有交换链，日志中的 `Failed to present` 属于预期输出；帧通过条目截图获取，而非 `grabWindow()`。
`MotionTests` 覆盖以下内容：减少动效时所有时长为 0 且不创建圆形覆盖层；覆盖层只在动画期间存在；
日 → 周 → 日（含中途打断）之后每根柱的缩放为 1、不透明度为 1，几何与 Qt Graphs 绘制的柱体一致，
误差小于 0.01 px；经典风格的面板执行弹出动画，艾欧泽亚风格不执行。

## 7. 截图与验证

```
build/src/Desktop/MentorRecorder.Desktop.exe --screenshot <png> --page N
    --theme dark|light
    [--backend mock|ipc]     # --screenshot 默认 mock；交互运行默认 ipc
    [--screenshot-delay MS]  # 真实后端需要更长的等待（2500 ms 起）
    [--screenshot-size WxH]  # 默认 1280x800

  仅 mock 后端：
    [--mock-live entered|matched|none]
    [--mock-npcap-missing]
    [--mock-first-run]       # 首次启动 baseline 对话框
    [--mock-open-detail]     # 选中第一条记录，打开详情浮层
    [--mock-open-edit]       # 在详情浮层之上打开手动修正对话框
    [--mock-open-reflection] # 对第一条记录打开导随心得对话框
    [--mock-calibration observing|ready|blocked|done|idle]  # 本机校准卡片（§4.4.1）
                             # idle = 校准早已完成、重启之后的常态，卡片不出现
    [--mock-shared fetching|verifying|consent|verified|verified-auditing|imported-published|
                   imported-unpublished|rejected|unavailable|user-rejected|none-for-build|share]
                             # 校准卡片的共享校准一节（§4.4.2）
    [--mock-speech azure|openai|unconfigured|fail]
                             # 在线语音（§4.5 播报）：azure / openai 已配置，unconfigured 什么都没选，
                             # fail 已配置但每句 SynthesizeSpeech 都回 ERR_SPEECH_NETWORK。只在本次运行里
                             # 选中对应的在线音色（并视为确认过），退出时还原；模拟后端的设置文件
                             # 本就位于测试目录，真实的 desktop.ini 不受影响
    [--mock-open-speech-confirm]
                             # 以本机语音起步，打开在线语音确认框（与 --mock-speech 同用时问的是该状态的音色）
    [--mock-speech-preview]  # 启动 0.4 秒后播一次「试听」（「通关」那一条），配 --mock-speech fail 可截到回退提示
    [--mock-update-available]
                             # 采集服务的更新检查报告有新版本（版本号与地址均为虚构，不联网）
    [--mock-update-download downloading|verifying|ready|failed]
                             # 软件内下载停在指定阶段（§4.1），隐含 --mock-update-available；不联网，
                             # 模拟的“安装程序”是写在模拟数据目录 updates\ 下的文本文件，模拟运行从不启动安装程序

  两种后端都可用：
    [--open-detail]          # 同上，但不要求 mock 数据
    [--open-edit]
    [--show-disclosure]      # 启动时打开软件说明窗口（与「设置 → 关于」的「查看软件说明」相同）
    [--mock-open-create]     # 打开「新增遗漏记录」三步向导
    [--mock-wizard-step 1|2|3]
                             # 向导停在第几步（配合 --mock-open-create / --mock-open-edit，默认 1）
    [--mock-detail-tab refl] # 详情浮层默认页签 info / events / revs / refl（会一并打开浮层）
    [--mock-ui-style classic]# 固定界面风格 classic / eorzea / harendotes，不改写 desktop.ini
    [--ui-language en]       # 固定界面语言 zh-CN / zh-TW / en / ja，不改写 desktop.ini（两种后端均可）
    [--settings-tab tts]     # 设置页（--page 6）打开的分页：general / tts / goal / data / about
    [--export-target DIR]    # 用固定目录替代 QFileDialog（测试 / 无人值守）

  隐藏（不在 --help 里）：
    --speech-selftest <wav>  # 用在线语音同一条 QSoundEffect 路径播放一个 16 位 PCM WAV 后退出：
                             # 0 播完；3 文件不存在或不是 16 位 PCM；4 播放失败；5 没有回应；6 本机没有音频输出设备。
                             # scripts/package.ps1 -Verify 在解包目录里用它证明多媒体运行时齐全
```

MockBackend 的在线语音：设置保存在内存中，只记录 `has_key`，从不保留密钥；
校验规则与采集服务相同，涵盖区域、地址、模型、音色，以及更换服务或地址而不携带新密钥时删除密钥。
Azure 的 `target_host` 为 `<区域>.azure-speech.invalid`；按 `NET-007`，
真实的语音主机名只能出现在采集服务的 `OnlineSpeechClient.cs` 中。
`SynthesizeSpeech` 将 0.3 秒静音的 16 位 PCM WAV 写入 `<模拟数据目录>\tts-cache\<sha256>.wav`。
模拟数据目录默认为 `%TEMP%\MentorRecorder-mock`，`GetStatus.database_path` 同样指向该目录，
因此离线环境下也能完整执行真实的校验与播放流程。
截图目标为 `MentorRecorderQmlSpeech_<state>` 与 `MentorRecorderQmlSpeechConfirm`。

启动时若未设置 `QT_MEDIA_BACKEND`，`main.cpp` 将其设为 `windows`。
发行包只包含 Qt Multimedia 的 Windows 后端，不包含 FFmpeg
（[third-party-licenses.md](third-party-licenses.md) §3）。
Qt 6.11 的 `QSoundEffect` 并不依赖后端插件，缺少插件时只输出一条警告；
随包提供 Windows 后端是为了避免该警告，并使 Qt TextToSpeech 在需要时有可用的后端。

默认使用 offscreen 平台；设置 `QT_QPA_PLATFORM=windows` 可渲染真实窗口。
截图要求 `D:/APPS/Qt/6.11.2/mingw_64/bin` 位于 `PATH` 中。

`--mock-*` 与 `--backend ipc` 同时给出时直接拒绝，退出码为 2，以免测试专用开关被静默忽略。
截图运行，以及使用模拟后端的任何运行（无论是否截图），都启用 `QStandardPaths` 的测试模式
（`AppSettings::isHarnessRun`）：设置、自动备份日期与语音选择都写入测试目录，不改动用户真实的
`desktop.ini`；模拟后端的备注图片写入 `%TEMP%\MentorRecorder-mock-note-images`，不进入安装目录。
截图运行以 `--backend ipc` 自行拉起采集服务时，`serve.pid` 仍按真实的 `%LOCALAPPDATA%` 查找
（`CollectorProcess::collectorDataDirectory`），退出时经停止事件请求它正常收尾，而不会将其强行结束。
截图运行期间出现任何 QML / JavaScript 运行时警告时，画面照常写出，进程以退出码 10 结束
（`QmlWarningCounter`），便于查看出问题的页面。
`--open-detail`、`--open-edit`、`--show-disclosure` 与 `--export-target` 只驱动导航与落盘位置，
不注入任何假数据，因此两种后端都接受；`--settings-tab` 同理，取值不在五个分页之内时退出码为 2。
截图模式下 Qt 无法发现系统字体，因此除一个 CJK 字体外，还会登记 `Consolas`（存在时登记），
使艾欧泽亚风格的等宽文字（路径、变量名）不会回落到只有大写字形的 Cinzel。

`MR_IPC_TRACE=1` 将每一条出站请求打印到 stderr，格式为 `--> QueryRuns {...}`，
用于核对某个页面实际发出的筛选条件。

Phase 4 的真实后端评审截图位于 `build/screenshots/phase4/`，包括
`p1..p6-{dark,light}.png`、`detail-dark.png`、`edit-dark.png`、
`disclosure-{dark,light}.png` 与 `lifecycle-own-child.png`。
更早一轮的 mock 截图位于 `build/screenshots/roles/`。

艾欧泽亚改版的复核截图位于 `build-theme/screens/`，包括
`p1..p6-{dark,light}.png`、`dashboard-tall-{dark,light}.png`（将「导随心得」面板一并纳入画面）、
`detail-{dark,light}.png`、`edit-dark.png`、`reflection-dark.png`、
`refl-tab-dark.png`、`npcap-dark.png`、`npcap-cap-dark.png`、
`live-entered-dark.png`、`disclosure-dark.png`、`baseline-light.png`，
以及经典风格的 `classic-p{1,2,6}-dark.png` 与 `classic-p1-light.png`。

其中 `classic-*.png` 拍摄时界面风格尚无命令行开关，是临时修改 `Theme.eorzea` 默认值得到的；
`reflection-dark.png` 与 `refl-tab-dark.png` 同样是临时在 `Main.qml` 中加入
自动打开对话框的 `Timer` 得到的。
当前可用 `--mock-ui-style eorzea|classic` 固定风格，且不改写 `desktop.ini`。

## 8. 已知与原型的差异

1. **数据库文件名**：界面显示后端上报的 `database_path`，本仓库中为
   `%LOCALAPPDATA%\MentorRecorder\mentor_recorder.db`（见 `docs/data-model.md`），
   原型写作 `mentor.db`。实现以真实路径为准，不显示不存在的路径。
2. **事件摘要页签**：内容来自只读消息 `GetRunEvents`。普通用户每行只看到中文事件名称
   （`Fmt.runEventLabel`）与时间，来源不明（`parser_status = UNKNOWN`）的行另带「来源不明」标记；
   方向、opcode、哈希前缀、解析出的字段与原始事件令牌只在维护者工具打开时显示。
3. **完整性校验**：`CheckDatabaseIntegrity` 自 2026-09-16 起纳入契约，执行只读的 `PRAGMA integrity_check`，
   由数据页的按钮调用。原型中的结果仅为一个 toast，实现改为按钮下方的结果行，见 4.5。
   更早版本的采集服务返回 `ERR_UNKNOWN_MESSAGE`，界面显示「当前采集器不支持完整性校验」，不会谎称通过。
4. **撤销修正**：原型的修正历史中有「撤销此修正（生成新 revision）」按钮。实现经 `UndoRevision`
   提供同一功能，但按钮只出现在最新一条修订上，且须在原因对话框中填写原因，见 4.7.3。
5. **开机启动默认值**：原型默认开启，实现默认关闭（见 4.5）。
6. **窗口背景的径向光晕**：原型使用两层 `radial-gradient`，而 `Rectangle.gradient` 只支持线性渐变。
   实现改用一块 `Canvas`（`createRadialGradient`，椭圆通过 `scale` 得到），
   按原型的位置、半径与颜色绘制，两种风格一致（2026-09-17 起；此前为竖直三段渐变的近似）。
   标题栏的深浅色切换按钮使用 `AppButton { variant: "inverse" }`，即另一主题的页面底色与文字色。
7. **副本筛选**：原型删除了历史页的「副本」下拉框；实现保留下钻能力，
   改用一枚以副本名命名的可移除 chip，见 4.2。

## 9. 图标清单与许可

图标是从 FINAL FANTASY XIV 客户端提取的美术素材，**版权归 SQUARE ENIX 所有**，
依据 FINAL FANTASY XIV Materials Usage License 作非商业用途随包分发，
版权声明显示在「设置 → 版权与来源」。条款与来源见 `src/Desktop/resources/icons/LICENSE-NOTE.md`。
`JobIcon`、`RoleIcon` 与 `CategoryIcon` 在图片不可用时自动回退为文字徽章，
分别为职业缩写、「坦 / 治 / 近 / 远 / 魔 / 全」配色徽章与副本类型首字。
`JobCatalog` 与 `RoleCatalog` 优先读取用户放置在 `%LOCALAPPDATA%\MentorRecorder\icons\` 中的自备 PNG。
使用 `-DMR_BUNDLE_GAME_ICONS=OFF` 可构建不含图标的版本。

### 9.1 清单

| 目录 | 内容 | 尺寸 | 使用位置 |
|------|------|------|----------|
| `resources/icons/jobs/` | 职业纯字形 | 56×56 | `JobIcon { framed: false }` |
| `resources/icons/jobs_framed/` | 职业 + 职能边框 | 64×64 | `JobIcon`（默认） |
| `resources/icons/content_types/` | 副本类型 | — | `CategoryIcon` |
| `resources/icons/roles/` | **职能图标** | 64×64 RGBA | `RoleIcon` |

职能图标共六枚，映射由 C++ `mr::RoleCatalog`（QML 运行时入口 `Runtime.roles`）负责：

| 职能（`Jobs.roleGroup()`） | key | 文件 | 字形 | icon id |
|---------------------------|-----|------|------|---------|
| 坦克 | `tank` | `roles/tank.png` | 蓝底 + 盾牌 | 062581 |
| 治疗 | `healer` | `roles/healer.png` | 绿底 + 十字 | 062582 |
| 近战 | `melee` | `roles/melee.png` | 红底 + 拳头 | 062584 |
| 远程物理 | `ranged` | `roles/ranged.png` | 红底 + 箭矢 | 062586 |
| 魔法 | `magic` | `roles/magic.png` | 红底 + 闪电 | 062587 |
| 未知 / 其他 / 空 | `allrounder` | `roles/allrounder.png` | 三色底板 + 人物剪影 | 062576 + 剪影 |

`Roles.roleKey(roleZh)` 不会失败：任何未映射的取值（`null`、空串、契约中的粗粒度 `DPS`、
未收录的职业）都归入 All-Rounder。
`Roles.roleIconSource()` 在没有可用 PNG 时返回空串，此时 `RoleIcon` 绘制徽章，
因此界面上不会出现空方块。
`TANK` 与 `HEALER` 这类契约英文取值同样可以解析。
逐文件的 `sha256` 与来源记录在 `resources/icons/roles/manifest.json` 中。

### 9.2 `allrounder.png` 的来源

xivapi 的 062576 只有三色空底板，人物剪影由游戏 UI 在运行时叠加。
因此该图取自 Gamer Escape「Dictionary of Icons」收录的成品图
（<https://ffxiv.gamerescape.com/w/images/f/fc/All-Rounder_Icon_1.png>）的 RGB 通道，
并套用 xivapi 062576 底板的 alpha 通道恢复圆角遮罩。
Gamer Escape 同样不拥有该素材的版权，SQUARE ENIX 的许可条款一并适用。

### 9.3 资源挂载点

图标资源挂载在 **`MentorRecorderDesktopLib`** 上，而非可执行文件上，
见 `src/Desktop/CMakeLists.txt` 的 `qt_add_resources(MentorRecorderDesktopLib "mr_icons" ...)`。
原因是 `JobCatalog` 与 `RoleCatalog` 从 `:/resources/icons/...` 读取，而单元测试只链接该库。
`tests/Desktop.Tests` 中的 `roleCatalog_mapsEveryRoleToAnExistingIcon` 断言六个职能
都能解析到真实存在的 `:/` 资源，且未知职能回落到 All-Rounder。

### 9.4 公开发布前必做

见 `LICENSE-NOTE.md` 的检查清单：从可分发产物中移除 `jobs/`、`jobs_framed/`、
`content_types/`、`roles/` 下的所有 PNG，或改为首次运行时从用户本地的 FFXIV
安装目录提取；并在「关于」页保留版权与商标声明。
