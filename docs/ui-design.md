# 桌面端 UI 设计说明 / Desktop UI Design

本文件描述 `src/Desktop`（Qt 6 Quick 桌面壳）的界面结构，以及界面与 HTML 原型之间的对应关系。
读者是桌面端的开发者与评审者。

绑定的视觉规范为 **`DOC/表单提交后设计/mentor-recorder-ff14.dc.html` 与 `ff14.css`**，
下称「原型」，对应「艾欧泽亚 / eorzea」界面风格。原型是唯一的视觉基准。
本文件只说明实现如何映射到原型，不另行定义布局。
布局与结构以原型为准；艾欧泽亚风格的**色值**自 1.2.0 起改为对照游戏自身窗口配色（深色 = 暖灰石板配浅字，浅色 = 羊皮纸配深棕字，金色只留给标记、进度环与柱条），见 6.1 节，不再照搬 `ff14.css` 的金边深蓝。
第三种风格 **Harendotes** 与艾欧泽亚共用布局与圆角，色板取自角色毛色：
深色「夜色狼身」以海军蓝黑为底、橙焰为主色、古金仅作点缀；浅色「白胸冷光」以纯白为面板、冷调蓝灰为背景，
顶栏与侧栏同为浅色，底边一条焰→金细线。标题用冷白宋体（Noto Serif SC）而非金色 Cinzel。面板不画金色四角，外框为 1 px 对角渐变：自左上角的橙焰经古金过渡，到右下角完全透明并露出面板自身的分割线色边框（components/PanelDecoration.qml，四条直线渐变加三个圆角弧拼成，不依赖 Canvas 或 Qt Quick Shapes）；侧栏状态面板在此风格下使用同一外框。

**「经典 / classic」界面风格**自 2026-09 改版起为**默认风格**，取值以
**`DOC/表单提交后设计/workbench.css`** 为准，取代早先的 `apple.css`。
`Settings.uiStyle` 不为 `"eorzea"` 时，`Theme.qml` 整套切换为 workbench 的取值，
包括色板、圆角、六级字号与 IBM Plex Mono 数字；页面代码只绑定 token，见 §6。
默认值仅影响从未手动选择过风格的用户。`setUiStyle()` 不写入与当前值相同的风格，
因此配置文件中存在的 `eorzea` 必定来自用户的主动选择。
执行 `diff mentor-recorder-v2.dc.html mentor-recorder-ff14.dc.html` 可列出两版原型之间的全部改动：
视觉主题与新功能「导随心得」。

---

## 1. 壳与导航

| 区域 | 尺寸 | 说明 |
|------|------|------|
| 窗口 | 1280 × 800（最小 1100 × 720） | 原生 Windows 边框 |
| 标题栏 | 高 40 | 菱形徽标 + `MENTOR ROULETTE 导随记录器` + Collector 状态 + 主题切换按钮 |
| 侧边栏 | 宽 200 | `Achievement` kicker + 衬线金色品牌块 + 渐变分隔线 + 6 个页面 + 底部状态面板（`.status-panel`：自带底色和边框的扁平 `Card`，`Theme.statusPanelBackground/Border`）。状态面板只有原型的三行 FF14 / Npcap / 捕获；行首是按状态色（绿 / 灰 / 橙）绘制的 Lucide 图标（gamepad-2 / network / radio），取代原型的 8 px 圆点 |
| 内容区 | 余下空间 | 左右内边距 28，上 22 下 32 |

标题栏**不绘制** macOS 交通灯圆点，也不绘制原型中的 `— ▢ ✕`。
本软件是 Windows 应用，窗口按钮由系统边框提供。原型中的这几个字符属于原型自身的应用截图外观，
复制到真实窗口上会出现两套关闭按钮。

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
* 历史记录固定每页 10 行，10 行与分页条在 800 px 高度内完整可见。
* 捕获诊断页在降级模式面板或提示条出现时、以及打开维护者工具时会变高，此时页面可滚动。

该模型的代价是：很长的列表（例如「全部」副本）会使页面变长，而不是在表格内部滚动。
按本软件的数据量衡量（副本数十个、每页 10 条记录），这是恰当的取舍。
分页由后端负责，不在视图层实现。

## 3. 字体与数字

正文字体栈（`QFont::setFamilies`，`main.cpp`）：

```
"Noto Sans SC", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"
```

**标题字体（仅 eorzea 风格）** 对应 `ff14.css` 的
`--font-heading: "Cinzel","Noto Serif SC","Songti SC","SimSun",serif`：

| Theme 属性 | 值 | 用途 |
|---|---|---|
| `Theme.headingFamily` | `Cinzel` | 拉丁字母与数字（`MENTOR ROULETTE`、统计数字、进度环） |
| `Theme.headingFamilyCjk` | `Noto Serif SC` | 中文标题 |
| `Theme.numFamily` | 同 `headingFamily` | 大号数字 |
| `Theme.headingFamilyFor(text)` | 二选一 | 见下 |

QML 的 `font` 值类型**没有** `families` 列表，只有 `family`，因此无法像 CSS 那样声明字体栈。
`Theme.headingFamilyFor(text)` 以一个 CJK 区间正则判断文本本身是中文还是拉丁，
再返回对应的字体家族，`components/HeadingLabel.qml` 即按此实现。
标题中中英混排的位置（标题栏的 `MENTOR ROULETTE 导随记录器`）本身由两个 `Text` 组成。

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

因此未安装思源宋体的机器会回落到系统自带的宋体，而不是静默退回无衬线正文字体。

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

**字号（仅经典风格）**：页面中书写的仍是艾欧泽亚的像素值，统一经过 `Theme.fs(px)` 换算。
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
捕获诊断页「最近失败」的错误码，以及维护者工具底部的状态块。

## 4. 各页面要点

### 4.1 总览（page 1）

* 成就进度环、8 张统计卡、当前导随卡、完成趋势与结果分布，布局与原型一致。
* 趋势图：`charts/TrendChart.qml` 在 `GraphsAvailable` 为真时加载
  `charts/GraphsTrendChart.qml`，即 Qt Graphs 的 `GraphsView` 与 `BarSeries`；
  否则回落到手绘的 `components/TrendBars.qml`。
  `main()` 中的 `probeGraphs()` 以一个最小 QML 片段实例化 `GraphsView` 进行判定，
  失败时将原因写入 stderr。两条路径都提供 `MM-dd · N 次` 的悬停提示。
  数据桶来自 `GetDashboardStats.trend`，由 Collector 在 SQL 中聚合。
  桌面端只把每个桶的 `start_utc` 按**本地时区**渲染成标签，不自行分桶。
  统计口径见 [statistics-definitions.md](statistics-definitions.md) §12.1。
* `components/UpdateNotice.qml`（`updateNotice`）位于页头之下，仅在 `App.update.updateAvailable`
  为真且本机未忽略该版本时可见，内容全部来自 `GetStatus` 应答中的可选对象 `update`。
  卡片上是「打开下载页」（`openReleasePageButton`）与「忽略此版本」（`dismissUpdateButton`）。
  前者把固定的公开发布页交给系统浏览器，后者只对该版本有效，升到更高的版本后会再次提示。
  本软件不下载、不安装任何内容，见 [privacy-boundary.md](privacy-boundary.md) §8.4。
* 「当前导随」卡片的**当前职业**一栏始终显示：存在职业编号时显示职业图标、职业名与职能图标，
  职业尚未识别时显示「未知」（职业报文在登录与换职业时出现，识别后该栏随记录更新）。
  结束后的结果与心得对话框提供可选的职业补录，与确认结果一并保存；
  用户不选择时留待后续补录。

### 4.2 历史记录（page 2）

* 筛选栏按原型压缩为**一行**（`Flow`，间距 6），自左至右依次为：
  搜索框（占据剩余宽度）；起始日期与结束日期（`components/DateField.qml`，各 118 px，
  占位文字「年/月/日」，可手动输入 yyyy-MM-dd，右侧日历图标打开月历 `MonthGrid` 选择日期，
  对应原型的 `<input type="date">`）；四个 86 px 的窄下拉框 类型 / 职业 / 结果 / 来源
  （首项即占位文字，不再使用「全部…」）；`.chip` 开关 已修正 / **有心得** / 含已删除；
  以及 `清除`（ghost 纯文字，40 px）。
  原型没有「待复核」开关。总览的「去复核」下钻到本页时，该筛选与副本下钻一样，
  以一枚可移除 chip 出现在日期之后，移除该 chip 即清除此筛选（`pendingReviewFilter`）。
  窗口宽 1280 时筛选栏恰为一行；窗口宽度不足时（例如 1100 的最小宽度），
  搜索框与两个日期占第一行，其余控件整体换到第二行，不会被裁切。
* 原型**删除了「副本」下拉框**，但副本统计页的下钻（`filter.content_id`）必须继续可用。
  实现的做法是：`App.historyFilter.content_id` 非空时，在日期之后增加一枚以副本名命名的
  可移除 chip（`removable`，点击时发出 `removed()`）；点击该 chip 即清除 `content_id` 并重新查询。
  下钻仍经由 `App.showHistoryForContent()`，不存在第二套筛选状态。
* `有心得` chip 打开时，在 `RunFilter` 中加入 `with_reflection: true`。
* 「标记」列除 已修正 / 已确认 / 已删除 / 手动创建 之外，还会显示 **有心得**（`run.reflection` 非空）。
  已修正 取自 `run.manually_corrected`（人改过软件记下的内容）；已确认 表示该记录有过修订、已不在待复核，
  但没有任何一次改动推翻软件的记录——通常是用户在「本次导随结果」里回答了是否通关。
* **筛选实时生效**，带 300 ms 去抖。先前实现中的「应用筛选」按钮已移除。
* 日期输入接受 `yyyy-MM-dd`，由 C++ `RunForm.isValidDate()` 校验。
  值非法时输入框文字转为红色，该值不会进入筛选条件，也不会以空值静默查询。
* 点击表头排序，同一排序键只在一列上显示箭头。
* 点击行打开右侧 380 px 的详情浮层（`dialogs/RunDetailPanel.qml`），
  含四个页签 详情 / 事件摘要 / 修正历史 / **心得**，底部为 手动修正 / 软删除 / 恢复记录。
  详情页签采用原型的两列键值网格。
* 详情页签末尾的「备注」区块在文字下方显示该记录的图片缩略图
  （`components/NoteImageStrip.qml`，64 px，点击以浮层查看原图）；没有备注文字但有图片时该区块同样显示。
  图片来自安装目录的 `note-images\<run_id>\`（C++ `NoteImageStore`，QML 上下文属性 `NoteImages`），
  采集服务与数据库不参与；面板监听 `NoteImages.imagesChanged` 重新读取。
* 选中行为 `--color-accent-100` 底色，加左侧 2 px 金色内嵌条，
  对应原型的 `box-shadow: inset 2px 0 0 var(--color-gold)`。
* **详情浮层是绝对定位的覆盖层**，与原型的
  `position:absolute; right:12px; top:12px; bottom:12px` 一致。
  浮层位于页面内容*之上*，不占据布局宽度，表格因此始终保持满宽。
  早先的实现为 `Flickable` 设置 `rightMargin: 392`，浮层打开时「副本」列被压缩为「伊…」。
  页面仍可在浮层下方滚动。关闭方式为 ✕ 按钮，或点击浮层以外的任意位置；
  后者由一层透明 `MouseArea` 实现，仅在浮层打开时启用，且不拦截滚轮事件。
* 详情页签的**职业**字段横跨两列，显示职业图标、职业名、职能图标与职能名。
  职业未识别时显示 All-Rounder 图标与 `未知 · job_detection=UNKNOWN`。
* 表格「职业」列宽仅 112 px，不足以容纳职能图标，因此只保留职业图标与名称，
  悬停时显示 `职业 · 角色` 的 tooltip。

### 4.3 副本统计（page 3）/ 职业统计（page 4）

* 左侧为柱状列表，右侧为表格；点击任意一行跳转到按该副本或职业筛选的历史记录。
* **职业统计的「角色」列使用 `Jobs.roleGroup()`**，即图例的六个分组
  坦克 / 治疗 / 近战 / 远程物理 / 魔法 / 未知，不出现契约中的粗粒度 `DPS`（「输出」）。
  `Fmt.roleLabel()` 仍然保留，但只用于确实需要契约 `Role` 三分类的位置。
* 职业图标使用带职能边框的图标。
* **「角色」列与圆环图例均使用真实的游戏内职能图标**（`components/RoleIcon.qml`，
  默认 20 px，启用 `smooth` 与 `mipmap`，`sourceSize` 固定 64×64，只降采样一次）。
  图例保留一条 3 px 的调色板色条，使图例与圆环的配色一一对应；
  名称 / 占比 / 次数三列不变。

### 4.4 捕获诊断（page 5）

本页自 2026-09 改版起按设计稿的「链路 / 解析 / 工具」排布：单列，最大宽度 1040，靠左对齐，
块与块之间间距 20 px。页面拆分为下列文件，均位于 `qml/pages/`，单个文件不超过约 400 行：

| 块 | 文件 | 何时出现 |
|---|---|---|
| 标题行 | `CapturePage.qml` | 始终。标题「捕获诊断」+「被动监听 · 不发包 · 不注入」；右侧普通用户是状态文字（`captureHeaderStatus`：自动监听中 / 等待游戏启动 / 校准中 / 校准待核对 / 监听中 · 收不到游戏数据 / 无法自动记录 / 检查中，取自 `App.recording`，与侧栏同一套判断，点的颜色也相同），维护者是「开始 / 停止捕获」按钮（`captureHeaderAction`，`App.captureActionLabel` / `toggleCapture()`） |
| 降级模式 | `CaptureNpcapPanel.qml` | 未安装 Npcap，或已安装但 `GetStatus.npcap.status` 不是 `READY` |
| 提示条 | `CaptureNotices.qml` | 各自按需；全部不出现时整块不占位 |
| 链路 | `CaptureChainPanel.qml` | 始终 |
| 解析 ｜ 工具 | `CaptureParsePanel.qml` ｜ `CaptureToolsPanel.qml` | 始终；并排 1.3 : 1，顶端对齐 |
| 维护者工具 | `CaptureMaintainerSection.qml`（+ `CaptureValidationEvidenceCard.qml`） | 仅 `App.maintainerToolsVisible` |

页面只读取 `App`、`Fmt`、`Theme`，不读取 `Settings`。
因此 `SharedCalibrationCardTests` 与 `CapturePageTests` 只需提供这三个上下文便可加载整页。
启动自检的禁词不变：普通页面不得出现「开始捕获」「停止捕获」「开始验证」「维护者工具」「对照核对」。

**降级模式**是一块橙色描边面板，分为两栏。
左栏包含橙色小标题「降级模式」、标题「未安装 Npcap」（已安装但不可用时为「Npcap 暂时用不了」）、
说明「被动抓包依赖 Npcap 驱动，出于许可证限制需自行安装。安装前仍可手动记录、统计、导出与备份。」，
以及贴在底边的按钮「打开 npcap.com」与「重新检测」（`ListCaptureAdapters`）。
已安装但不可用时（`NOT_WINPCAP_COMPATIBLE` / `NPCAP_ADMIN_ONLY` / `LOAD_FAILED`），
说明改为一句中文原因，加上采集服务自身的 `npcap.install_hint`。
该提示给出的正是具体的修复方式，界面与 `--capture-doctor` 因此不会给出两种说法。
未安装时 `install_hint` 只是重复安装步骤，故不显示。
右栏是四步安装说明，采用设计稿的短句。

**提示条**位于链路上方，顺序固定，左右内边距与下方的面板一致：

* 校准卡片（`calibrationCard`，含共享校准一节，见 4.4.1、4.4.2）：`calibration.state != IDLE` 时出现。
* 协议档案（`protocolProfileCard`，紧凑面板）：正在使用本机校准或共享校准的档案、
  可以「分享给其他玩家」（`protocolShareHint` / `protocolShareButton`）、
  「重新校准」（`protocolRecalibrateButton`，仅本机校准）或
  「恢复上一份本机校准」（`protocolRestoreButton`，仅在有被停用的档案待恢复时），
  或游戏正在运行而档案不是 `VERIFIED`（校准卡片存在时由校准卡片解释）时出现。
  普通用户看到的标题是「正在使用本机校准出来的档案」「当前游戏版本没有可用档案」一类语句，
  **不出现档案编号与状态令牌**；维护者在 `VERIFIED` 时看到「已就绪：`<profile_id>`」。
  游戏未运行时，措辞中的「当前游戏版本」改为「已安装的游戏版本」；档案不可用的提示此时不出现，
  相关信息已由链路总结按已安装的版本给出。
* 中途开抓 / 无流量（`captureSilentNotice`，标题 `midstreamBannerTitle`）：
  `captureMidstreamSuspected`、`captureSilent` 或 `recording.silent` 时出现。
  正文取采集服务的 `hint`，缺失时取自动记录控制器的提示，下面一行为「已解码 N · 解码失败 N」。
* 记住的网卡没有游戏流量（`adapterPreferenceStaleNotice`）：`preference_stale` 与 `recommended`
  两个适配器都已知时出现。「使用推荐适配器」（`useRecommendedAdapterButton`）写入 `CaptureSettings.adapter_id`。
* 自动跟随（`captureReadinessNotice`，仅维护者可见，且仅在未捕获时出现）：关闭时显示橙框与「开启自动跟随」。

**链路**的标题行包含小标题「链路」、一句总结，以及两个 ghost 按钮：
「重扫 FF14」（`GetStatus` 与档案状态）与「重扫适配器」（`ListCaptureAdapters`）。
总结（`captureChainSummary`）按链路顺序给出**第一个**阻碍记录的原因，依次为：
Npcap 未安装或不可用；游戏未运行（见下）；
校准中 / 待核对 / 校准受阻；档案核对中；档案不可用（与协议档案提示使用同一套说法）；
记住的网卡没有流量；`last_error_code`（`Fmt.captureErrorLabel`）；监听尚未启动；无流量；
其他受阻情况（`recording.message`）。以上均不成立时，总结为「FF14 → Npcap → 适配器 → 协议档案 全部就绪」。
问题用橙字，等待与就绪用灰字。

游戏未运行时的总结取决于采集服务是否已从本机记住的安装目录读到客户端版本
（`capture.ffxiv_running` 为假而 `capture.game_build` 与 `region` 已知）：

* 版本未知（首次使用，尚未见过游戏）：「等待游戏启动 · 档案按版本匹配，游戏启动后才知道能否记录」，灰字。
* 版本已知且档案为 `VERIFIED`：「等待游戏启动 · 已安装版本的档案已就绪，启动游戏后会自动记录」，灰字。
* 版本已知而没有可用档案：以「等待游戏启动 · 已安装的游戏版本还没有可用档案」开头，橙字，
  其后按情况说明「启动游戏后需要重新校准」、待核对、校准受阻、档案冲突或暂时不会自动记录；
  共享校准正在获取、等待核实或等待同意时，以「见上方校准卡片」结尾，不与校准卡片的说法冲突。

游戏未运行时一律称「已安装的游戏版本」而不称「当前」或「最新」：补丁日启动器完成更新之前，
磁盘上仍是旧版本，游戏启动后以运行中的客户端为准。此时界面不给出「去打一把副本」一类的操作指示。

总结下方是四列等宽、竖线分隔的状态列（`captureChain_<key>`，值为 `captureChainValue_<key>`，
副行为 `captureChainSub_<key>`）。每列包含一个状态点（绿为就绪、灰为等待、橙为有问题）与名称、
16 px 数字字体的值，以及小字副行。

| 列 | 值 | 副行 | 来源 |
|---|---|---|---|
| FF14 进程 | `ffxiv_dx11.exe`（固定）/ 未运行 | PID n / 已安装版本 2026.09.01（游戏未运行而版本已知，`Fmt.gameVersionLabel`）/ 启动游戏后自动检测 | `capture.ffxiv_running` / `ffxiv_process_id` / `game_build` |
| Npcap | v版本 / 未安装 | WinPcap 兼容模式 / 驱动缺失 / 未启用 WinPcap 兼容模式 / 仅限管理员使用 / 驱动文件无法加载 | `GetStatus.npcap.version`、`status`，`capture.npcap_*` |
| 适配器 | 正在用的网卡友好名 / 未选择 | 自动选择（有 FF14 连接）/ 手动指定 / 记住的网卡上没有游戏流量 | `capture.adapter_id` 对上 `App.captureAdapters[].friendly_name`（找不到时用 `capture.adapter_description`；普通用户从不看到不透明的 adapter_id）；`captureSettings.adapter_id` 为空即自动 |
| 协议档案 | 普通用户：档案匹配 / 本机校准 / 共享校准 / 待游戏启动（仅版本未知时）/ 校准中 / 待校准（游戏未运行）/ 待核对 / 版本不支持 / 档案冲突 / 未匹配；维护者：`profile_id` | 与游戏版本匹配 / 与已安装的游戏版本匹配 / 启动游戏后重新校准 / 已安装版本暂时不会自动记录 / 待游戏启动后校验（仅版本未知时）/ …；维护者：状态令牌 · build | `capture.profile_status` / `profile_origin`、`App.calibration.state`、`App.protocolProfile` |

适配器只有在监听运行时才显示绿点；游戏未运行时，即使采集服务已记住网卡也显示灰点。

**解析**包含小标题「解析」与说明「失败即忽略，不改变导随状态、不创建记录」，下方是一行指标：
「成功率」使用 34 px 数字字体（艾欧泽亚风格为金色，经典风格为主色）；「消息速率」为 `n / s`；
「失败 · 去重」为 `a · b`；「最近有效事件」为本地时间加 `Fmt.eventKindLabel(last_valid_event_kind)`，
例如「21:38:04 副本结算」。未知类型只显示时间，契约要求将未知令牌视为「某个事件」。
`last_valid_event_kind` 的取值含义如下：`CONTENT_FINDER_POP` 匹配成功、
`ZONE_INITIALIZATION` 进入区域（每次换区都会出现，不限于进入副本）、`ZONE_TERRITORY` 识别所在区域、
`DUTY_RESULT` 副本结算、`PLAYER_JOB` 识别职业、`ZONE_LEFT` 离开副本区域、`INSTANCE_LEFT` 退出副本、
`MATCH_CANCELLED` 匹配取消。契约另有 `MATCH_ANNOUNCED`（按出现时机认出的匹配通知，
见 [protocol-profile-format.md](protocol-profile-format.md) §11.5）：桌面端不为它准备文案，
按上面的约定只显示时间。

**采集服务未发送的计数一律渲染为 `—`**。`AppController::captureCounters` 只复制实际到达的键，
`last_valid_event_kind` 同样如此，因此界面上的 `0` 必定是实测得到的 0。
`解析成功率` 由 `parse_ok_count / (parse_ok_count + parse_fail_count)` 推导，两者均为 0 时显示 `—` 而非 100%。
采集服务完全没有报告解析计数时（`AppController::parserStatsAvailable` 为假），整行指标不出现，
只显示一句「还没有拿到解析统计……」。

「最近失败」位于一条分隔线之下，每行依次为 64 px 时间、140 px 红色 11 px 等宽错误码与说明，
行间有分隔线，最新的一行在上。
普通用户看到的说明来自 `Fmt.parserErrorLabel(code)`，例如「报文长度与档案不符，已忽略」；
六个 `ParserErrorEntry.code` 各对应一句，未知码有通用句。
普通用户视图**不显示 opcode、方向与采集服务原文**，原文中含有十六进制数值；
最多显示 5 行，超出部分写为「另有 N 条更早的失败未列出」。
维护者可以看到全部行，说明为「方向 opcode · 采集服务原文」。
模拟后端的示例行下方有橙色提示，说明该行为模拟数据。

**工具**面板的行与行之间有分隔线，复用 `SettingsRow`。

* 「导出脱敏诊断报告」一行包含说明「不含游戏内容、网络地址与账号」与按钮「导出」
  （`exportDiagnosticsButton`），发送 `ExportDiagnosticsReport`（contracts/CHANGELOG.md 第 16 条）。
  报告由**当前正在运行的**采集服务写成本地 JSON，成功后 toast 给出路径与字节数。
  目标路径必须位于 `%USERPROFILE%` 或 `%LOCALAPPDATA%` 之内，否则采集服务返回 `ERR_EXPORT_FAILED`，
  桌面端原样显示其文案。在文件对话框中取消时不发送任何请求。
* 「离线回放测试样本」（`offlineReplayRow`，仅维护者可见，只有说明没有按钮）显示
  「用采集器的 --replay 命令行，幂等、不产生新记录」，因为 `ipc-v1` 没有回放消息。
* 本页**没有「诊断模式」开关**（决策 3：`Settings.diagnosticsMode` 不产生任何行为）。
  每条导随的脱敏事件行仍在「历史记录 → 事件摘要」中读取（`GetRunEvents`）。
* 「采集器告警」（`collectorWarningsList`）：`GetStatus.warnings` 非空时，在面板底部显示一列橙色小字。

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
   | Npcap | 版本 / 未安装 | `status` · WinPcap 兼容 · 仅管理员 | `capture.npcap_*` + `GetStatus.npcap` |
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
   LIVE_CAPTURE_STATUS = UNVERIFIED
   PROTOCOL_PROFILE_STATUS = <后端上报，mock = SYNTHETIC_ONLY>
   OODLE_MODE = <GetStatus.oodle_mode，本机实测 FfxivTcp>
   READS_GAME_EXECUTABLE = <GetStatus.reads_game_executable>
   PUBLIC_DISTRIBUTION_READY = false
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
标题行的两种身份、链路的三种状态、最近有效事件的已知与未知类型、没有解析计数时的说明，
以及普通用户页面不出现 opcode、`0x`、`ERR_` 与维护者文案。

#### 4.4.1 本机校准（0.3.0）

游戏更新后没有匹配档案时，Collector 进入本机校准。
此时界面只增加三处内容，全部面向玩家，且不置于「维护者工具」之后：

* **校准卡片**（`calibrationCard`，位于捕获页协议档案卡之上，`capture.calibration.state != IDLE` 时显示）：
  四行进度为 已看到排本 / 已看到匹配弹窗 / 已看到进本 / 已看到出本，以 ✓ 或 – 表示，
  取自 `calibration.progress`。`blockers[]` 逐句原样显示，Collector 写入的即是面向玩家的完整句子。
  卡片有两个按钮：**核对并启用**（`calibrationConfirmButton`，仅在 `READY` 时可用）与
  **重新观察**（`calibrationDiscardButton`，发送 `DiscardCalibration`）。
* **核对对话框**（`calibrationDialog`）按时间列出 `calibration.events`，每行为 `HH:mm` 与 `label`，
  登录与换区行灰显且没有按钮。每条 `requires_confirmation` 的事件带一对 **对 / 错** 按钮
  （`calibrationVerdictCorrect_<event_id>` 与 `calibrationVerdictWrong_<event_id>`），
  全部选择完毕后方可点击确认（`calibrationDialogConfirm`），发送 `ConfirmCalibration`。
  成功后对话框关闭并刷新。返回 `ERR_CALIBRATION_REJECTED` 时显示
  「有事件被标为不对，这次校准作废；再打一把随机任务后会重新核对。」并关闭，
  该句同时保留在卡片上（`App.calibration.error`）。
* **横幅与侧栏**：`AutomaticRecordingController` 提供 `calibrating`、`calibration_ready` 与
  `calibration_blocked` 三种投影，均只点亮橙色横幅，**不**弹出「无法自动记录」对话框，
  因为补丁日的档案不匹配属于预期情况，而非故障。横幅文案在观察中为
  「游戏更新到了新版本，本软件正在重新校准：正常打一把随机任务（进本、打完出本）就好，期间不会生成记录。」；
  就绪时为「校准完成，核对 N 件事就能开始自动记录。」并附「核对并启用」按钮；
  阻塞时原样显示第一条 blocker，并附「查看诊断」。
  侧栏「协议」一行显示 校准中 / 待核对 / 需诊断；`profile_origin = LOCAL_CALIBRATION`
  时显示 **本机校准**，状态仍为「监听中」。
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

**校准卡片的「共享校准」一节**（`SharedCalibrationSection.qml`）位于卡片最下方。
`calibration.shared` 缺失时（旧版采集服务）整节不显示。
显示内容先按 `user_rejected` 判定，再按 `phase` 判定：

| 情况 | 卡片上的话 | 按钮 |
|---|---|---|
| `FETCHING` | 正在获取其他玩家的共享校准，本机校准照常进行。 | 导入校准码 |
| `VERIFYING`，候选中有 `provenance = PUBLISHED` 的 | 找到共享校准，登录时自动核实，通过就开始记录。 | 导入校准码、不用共享的，我自己校准 |
| `VERIFYING`，未被拒绝的候选全为 `provenance = IMPORTED` | 已导入校准码，登录并排一次本、核实通过后启用。 | 导入校准码、不用共享的，我自己校准 |
| `VERIFYING`，候选未报告 `provenance`（1.1.0 之前的采集服务） | 找到共享校准，登录或排本时自动核实。（候选全是手动导入的：已导入校准码，……） | 导入校准码、不用共享的，我自己校准 |
| `VERIFYING`，且 `profile_status = VERIFIED`（已有档案在记录，1.4.0 起会出现） | 找到更准的共享校准，正在本机核实；当前记录照常生成。（候选全为 `IMPORTED`：已导入校准码，正在本机核实；当前记录照常生成。）另加一行灰字：现在的记录不受影响；核实通过后会自动换用更准的那一份，之前生成的记录不会改动。 | 导入校准码、不用共享的，我自己校准 |
| `AWAITING_CONSENT` | 共享校准核实通过了，还需要你同意一次才能开始记录。橙框内说明代价，末句另给第三条出路：「手上有其他玩家发来的校准码的话，也可以先点「导入校准码」。」 | 同意，开始记录（橙框内，与 `sharedConsentImportButton` 并排）、导入校准码、不用共享的，我自己校准 |
| `VERIFIED`，或 `profile_origin = SHARED_CALIBRATION` | 已使用其他玩家分享的校准（本机已核实）。 | 不用共享的，我自己校准 |
| 同上，且 `audit_pending = true` | 已使用其他玩家分享的校准（登录时已在本机核实）。另加一行灰字：排本和进本还在核对中，照常游戏即可；万一对不上，会自动改回本机校准，这期间生成的记录会标记待复核。 | 不用共享的，我自己校准 |
| `REJECTED` | 共享校准与本机流量对不上，已改为本机校准。 | 立即检查、导入校准码 |
| `UNAVAILABLE` | 没取到共享校准（网络不通），继续本机校准。 | 立即检查、导入校准码 |
| `NONE` 且 `last_fetch_status = NONE_FOR_BUILD` | 还没有人分享这个版本的校准，继续本机校准。 | 立即检查、导入校准码 |
| `NONE` 且 `last_fetch_status = DISABLED` | 获取共享校准已关闭，继续本机校准。 | 立即检查、导入校准码 |
| `user_rejected = true` | 已按你的选择改为本机校准，这个游戏版本不再使用共享校准。并提示可用「清空进度并重新观察」撤销 | 无 |
| `profile_origin = LOCAL_CALIBRATION`（校准卡片仍显示时，即刚完成校准的那一次） | 一行分享说明（浏览器打开 GitHub 分享页、复制校准码、软件不上传） | 分享给其他玩家 |

**「分享给其他玩家」同时出现在协议档案卡上**（`protocolProfileCard`，即捕获页链路上方的提示条），
只要具备分享条件即会出现。校准卡片仅在 `calibration.state != IDLE` 时出现，
而重启软件之后本机档案直接生效、校准不再布防；在 0.7.12 中，最需要分享的用户因此没有入口。
当前的行为是：正在使用的档案由本机校准产生（`profile_origin = LOCAL_CALIBRATION`），
且采集服务支持共享校准时，协议档案卡上增加一行分享说明与一个 `protocolShareButton`。
该按钮与校准卡片上的 `sharedShareButton` 调用同一个 `App.calibration.shared.share()`，
说明句也取自同一处（C++ 的 `shareHint`）。
**校准卡片存在时该按钮不出现**：校准卡片正在解释刚完成的校准，分享入口置于其上更为连贯，
同一屏内不应出现两个功能相同的按钮。
「导入校准码」不随之迁移，因为采集服务只在校准进行中才接受导入。

**「重新校准」也出现在协议档案卡上**（`protocolRecalibrateButton`，次要按钮），
条件是 `profile_status = VERIFIED` 且 `profile_origin = LOCAL_CALIBRATION`——
本机档案生效后校准卡片消失，「清空进度并重新观察」与「导入校准码」随之不可达，
怀疑本机认错了报文、或拿到了更好的校准码的玩家此前无路可走。
点击先打开确认框（`protocolRecalibrateDialog`，沿用 `DialogFrame`）：
标题「重新校准这一版游戏？」，正文「现在这份本机校准会停用（文件会保留，不会删除），
软件回到观察状态：期间不会生成记录，直到重新校准完成，或导入了其他玩家的校准码。之前的记录不受影响。」，
按钮 **取消** 与 **停用并重新校准**（`protocolRecalibrateConfirm`）。
确认后发送 `DiscardCalibration` 并带上 `retire_local_profile = true`，随即重读一次捕获状态。
`App.currentRunState` 为 `MENTOR_MATCHED` 或 `ENTERED_DUTY` 时按钮禁用，
旁边给一行灰字「副本进行中，结束后再试」——停用档案会把这一把按停止捕获收尾。
共享档案由「不用共享的，我自己校准」停用，随包档案与「没有档案」都不出现此按钮。
行为见 [protocol-profile-format.md](protocol-profile-format.md) §11.6。

**「恢复上一份本机校准」**（`protocolRestoreButton`，同一行、同一张卡）是它的撤销：
`calibration.retired_local_profile_available` 为真时出现，即被停用的那份档案还在磁盘上、
且当前没有本机档案生效。它与「重新校准」互斥——一个要求有本机档案生效，另一个要求没有——
同屏只会出现其中之一。确认框（`protocolRestoreDialog`）标题「恢复上一份本机校准？」，
正文「软件会停用现在这份校准，换回你上次停用的那一份本机校准，并立刻用它记录。之前的记录不受影响。」，
按钮 **取消** 与 **恢复**（`protocolRestoreConfirm`），确认后发送
`DiscardCalibration` 并带上 `restore_local_profile = true`，随即重读一次捕获状态。
副本进行中时同样禁用，并复用「重新校准」那一行灰字。
停用之后校准卡片会重新出现，若不把这条退路算进协议档案卡的可见条件，整张卡会被隐藏、
按钮也就不可达，因此 `profileCardVisible` 把它一并计入。
采集服务拒绝时（没有可恢复的、同名档案已存在、恢复后无法通过校验），它给的中文句子
原样显示在卡片的 `protocolCalibrationError` 一行——此时校准卡片未必在场，这是唯一的说明位置。

* 「立即检查」只在本机仍处于校准（`WAITING` / `OBSERVING`）、
  且没有共享档案正在记录时出现。「导入校准码」条件相同，但**包括**征求同意（`AWAITING_CONSENT`）
  这一状态：采集服务从未在该阶段拒绝导入，此前只是卡片把按钮藏了起来，
  于是「同意这份按排本推断的校准」与「这个版本不再用共享校准」成了仅有的两个答案，
  手里拿着更好的校准码的玩家反而无从导入。该状态下按钮由橙框内的
  `sharedConsentImportButton` 承担，`sharedImportButton` 让位，同屏不出现两个同名按钮。`last_refusal`、候选的 `sha12`、`criteria` 与 `last_index_attempts`
  都不显示在卡片上，它们属于脱敏诊断报告的内容。
* **共享档案正在记录**：在共享档案完整记录一次进出副本之前，采集服务保持校准布防，
  `calibration.state` 仍为 `OBSERVING`。此时卡片小标题改为「共享校准」，
  标题为「已使用其他玩家分享的校准（本机已核实），正在自动记录。」，
  隐藏进度行与「清空进度并重新观察」，采集服务的说明句改用灰色。
  侧栏「协议」显示 **共享校准**，横幅使用普通的「自动监听中」。
* **绑定后仍在核对（`audit_pending = true`）**：核实门槛按来源分级之后
  （plans/shared-calibration.md §18），仓库来源的校准在登录簇核实通过即开始记录，
  排本与进本改为绑定后审计。此时卡片标题改为
  「已使用其他玩家分享的校准（登录时已在本机核实），正在自动记录。」，
  不再说「本机已核实」；共享校准一节的灰字说明照常显示（这是唯一说明该状态的地方），
  内容为「排本和进本还在核对中，照常游戏即可；万一对不上，会自动改回本机校准，这期间生成的记录会标记待复核。」。
  按钮、进度行与侧栏与上一条相同，审计期间不弹任何对话框。
  旧采集服务不报 `audit_pending`，桌面端按"未报告"处理，措辞与上一条一致。
* **同意提示**：代价说明与本机临时档案的说明共用 `CalibrationController.queueInferenceText`。
  `AWAITING_CONSENT` 期间横幅改为
  「找到了其他玩家分享的校准，同意一次就能开始自动记录：请到捕获诊断页的校准卡片上查看。」，
  仍然只点亮橙色横幅，不弹出对话框。
* **导入校准码对话框**（`sharedImportDialog`）包含一个文本框与「导入」按钮。
  `ImportCalibrationCode` 返回的 `message` 原样显示在文本框下方；返回 `APPLIED` 时对话框关闭，
  同一句话进入 toast。文本为空时不发送请求。
  采集服务的这句话本身区分"命中公开仓库索引"与"未发布"（对应应答中的 `provenance`），
  以及 `reason = REVOKED` 的"已被撤回"，桌面端不改写、不追加；
  只有 `message` 为空时才退回桌面端自己的兜底句。
* **不用共享的确认框**（`sharedRejectDialog`）说明将撤下哪些内容、此后该游戏版本不再获取或导入共享校准，
  以及如何撤销该选择；确认后发送 `RejectSharedCalibration`。
* **分享**：`GetCalibrationShareCode` 成功后先将 `code` 复制到剪贴板。
  仅当 `code_in_url = true` 且地址位于 `https://github.com/` 之下（不含账号、不含端口）时，
  才交给 `QDesktopServices::openUrl`；否则只复制并在 toast 中说明。
  `ERR_SHARE_CODE_UNAVAILABLE` 按 `details.reason`（`NO_PROFILE` / `NOT_LOCAL` / `SHARED` / `NOT_SHAREABLE`）
  各给出一句说明，不显示采集服务的原文，原文中可能含有路径。
  为此 `BackendReply` 提供 `errorDetails()`，由 `IpcClient` 传递错误信封中的 `details`。
* 每个请求的结果都以一条 toast 呈现。改变共享状态的请求之后，
  `AppController::rereadCaptureStatus()` 读取一次 `GetCaptureStatus`，与 `calibration_changed` 事件走同一条路径。
* 桌面端自身不联网（`NET-006` 禁止使用 `QNetworkAccessManager`），网页一律交由系统浏览器打开。
* Mock：`--mock-shared fetching|verifying|consent|verified|verified-auditing|imported-published|imported-unpublished|rejected|unavailable|user-rejected|none-for-build|share`，
  截图目标为 `MentorRecorderQmlSharedCalibration_<state>`。
  其中 `verified-auditing`、`imported-published`、`imported-unpublished` 三个状态对应核实门槛按来源分级：
  分别为登录时已核实、排本与进本仍在核对；导入后命中索引；导入后任何索引都不认识。
  其余状态不带 `provenance` 与 `audit_pending`，用于覆盖旧采集服务的"未报告"分支。
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
| 通用 | `general` | 启动 · 记录 · 外观 | 启动与托盘 / 记录 / 校准 / 更新 / 外观 |
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

* **启动与托盘**（`generalSettingsCard`）：开机启动、随 FF14 自动开始捕获
  （`followGameToggle`，仅维护者可见，写入 `UpdateCaptureSettings.follow_game`）、关闭时最小化到托盘。
* **记录**：结束后询问本次结果（`Settings.confirmPrompt`，默认开启）、
  通关后弹出笔记窗口（`Settings.reflectPrompt`）。
* **校准**（原型中没有该面板，见改版方案 §3.1）：游戏更新后自动校准（`autoCalibrationToggle`）、
  **游戏更新后获取其他玩家的共享校准**（`sharedCalibrationToggle`，写入
  `UpdateCaptureSettings.shared_calibration_enabled`，由采集服务持有，默认开启）。
  后者的描述写明这是默认开启的那一种联网请求，且不携带任何可识别玩家的信息，见 §4.4.2；
  另一种联网请求是默认关闭的在线语音，见下文的「播报」。
  `App.captureSettingsError` 以橙色显示在该面板底部。
* **更新**：开关「检查新版本并提示」（`updateCheckToggle`，写入
  `UpdateCaptureSettings.update_check_enabled`，由采集服务持有，默认开启）。
  描述写明这是第三类联网请求：每天最多一次，只从本项目的发布页读取一个仅含版本号的小文件，
  与当前版本比较；请求不带账号、安装编号或任何可识别信息，本软件也从不自动下载或安装任何东西，
  见 [privacy-boundary.md](privacy-boundary.md) §8.4。已检查过时，面板下方以小字给出
  「最近检查：<时间> · 最新版本 <版本号>」（`updateCheckStatusText`）。
  该行右侧是「检查更新」（`checkUpdateNowButton`）：点击发出 `CheckUpdateNow`，
  采集服务随即检查一次，不受每日节流限制，结果以一句提示说明。有新版本时同一位置改为
  「打开下载页」，点击把发布页交给系统浏览器，不在本软件内下载任何东西。
  按钮在开关关闭、采集服务未给出更新信息，或上一次检查尚未返回时停用，
  判据是 `App.update.canCheck`（`available && enabled && !checking`）。
  提示共五句，分别对应：有新版本、已是最新版本、没有检查成功、开关已关闭、
  本机已通过环境变量禁用；其中失败一句不区分具体原因，也不出现地址或协议标记。
  新版本的横幅提示本身在总览页（§4.1），设置页不重复呈现。
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
* 托盘由 `TrayController`（`QSystemTrayIcon`）实现，菜单项为 显示 / 开始捕获 / 退出。
  捕获项调用 `AppController::toggleCapture()`，同样经由后端。
  开关打开时，窗口的关闭事件被拦截为「隐藏到托盘」，退出菜单项绕过该拦截。
  没有托盘的环境（offscreen、CI）不创建图标，并将 `quitOnLastWindowClosed` 恢复为真。

**播报**：本机语音是**桌面端本地功能**，不经过采集服务。
在线语音（界面改版 P4b，[privacy-boundary.md](privacy-boundary.md) §8.3）由采集服务合成，
桌面端只播放采集服务写入本机的 WAV，自身不联网。

* **本地语音播报**（`speechSettingsCard`）：标题行（`speechHeaderRow`）由 kicker 与小字组成。
  kicker 为「本地语音播报」，选中在线语音时改为「语音播报」。
  小字在本机语音下为「使用系统语音引擎，不联网」；选中在线语音、且采集服务上**同一家服务**已配置完成时，
  为「播报文字会发送到 <`App.speech.targetHost`>」；其余情况为橙色的「在线语音 · 尚未配置」。
  标题行右侧是「试听」（`ttsPreviewButton`，`Tts.preview("finished")`）与总开关（`Settings.ttsEnabled`）。
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
* **播报模板**（`speechTemplatesCard`）：kicker 与「可用变量：`{duty} {progress} {remaining}`」（等宽）；
  两列排列 匹配成功 / 进入副本 / 通关 / 异常结束；下方整行为 **结束待确认**，
  带说明「国服目前看不到通关判定……」。试听播放的即是这一条：
  国服协议档案无法识别通关判定，因此这是一局导随实际会听到的话。

`TtsService`（基于 `QTextToSpeech`）：

* `voices` 列出本机已安装的语音，zh-CN 在前、其余在后，各自保持引擎顺序。
  每行为 `{id: "local:<名称>", name, label, locale, provider: "local"}`，
  `label` 形如「Microsoft Huihui Desktop（中文 · 女声）」。
  `findVoices()` 可能切换引擎的语言区域，因此只在引擎处于 `Ready` 时枚举一次并缓存。
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
* 语速为 `(percent - 100) / 100`，并夹取到 [-1, 1]；音量为 `percent / 100`。
  本机没有可用引擎时不会崩溃，界面也不禁用：语音引擎一行显示中性说明，
  `spoke()` 信号照常发出，便于测试以及后续将播报接入其他输出。
  Windows 的 SAPI **异步**枚举语音，`QTextToSpeech` 构造完成时往往尚未 `Ready`，
  `availableVoices()` 为空。因此引擎的 `stateChanged` 与 `errorOccurred` 均接入回调，
  每次 `Ready` 重新选择一次语音并重设语速与音量。
  出错时状态行显示引擎原文，`Tts.available` 为假，设置页将该行标为橙色，而不是继续显示「已就绪」。
  一次导随的两句话（匹配与进本）之间只间隔一两秒，因此播报使用 `enqueue()` 排队，
  只有「试听」与自由文本使用 `stop()` 抢断。

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
* `spokeVia(kind, text, route)` 对每句话发出一次，`route` 取 `online` 或 `local`，
  测试据此断言播报顺序与去向。

`SpeechController` 挂在 `App.speech` 上，方式与共享校准挂在 `App.calibration.shared` 相同：

* 属性包括 `loaded`、`supported`、`provider`、`azureRegion`、`openaiBaseUrl`、`openaiModel`、
  `voice`、`hasKey`、`configured`、`targetHost`、`azureVoices`、`openaiVoices`、`busy`、
  `resultState`、`resultText` 与 `lastError`，**不含密钥属性**。
  旧版采集服务拒绝 `GetSpeechSettings` 时 `supported` 为假：
  发行版采集服务对无法识别的消息返回 `ERR_BAD_REQUEST`（`field = message_type`），
  控制器同时接受 `ERR_UNKNOWN_MESSAGE` 与 `ERR_UNSUPPORTED`。此时在线语音整组设置不出现。
* 方法包括 `save(map)`、`clearKey()` 与 `test()`，后者转交 `TtsService::testOnline()`，
  结果写回 `resultText`。控制器在连接采集服务时与每次保存后刷新。
* 所选音色与采集服务属于同一家服务、仅音色不同时，自动发送一次只含 `voice` 的更新。
  控制器**从不自动更换服务**：更换服务而不携带新密钥会导致采集服务删除旧密钥。

播报由后端的 live 事件驱动，**过程状态与终局状态分属两条路径**：

| live 事件 | 状态 | 模板 |
|---|---|---|
| `run_state_changed` | `MENTOR_MATCHED` / `ENTERED_DUTY` | 匹配 / 进本 |
| `run_finished` | `COMPLETED`（真的看到了 DUTY_RESULT） | 完成 |
| `run_finished` | `UNKNOWN_FINAL_STATE` | **结束待确认** |
| `run_finished` | `LEFT_OR_ABANDONED` / `DISCONNECTED` / `INTERRUPTED` / `INTERRUPTED_PENDING_REVIEW` | 异常结束 |
| `run_finished` | `CANCELLED_BEFORE_ENTRY` | 不播报（拒绝排本是正常操作） |

终局状态只采信 `run_finished`。下一次匹配弹窗紧接着到来时，`StateChanged` 会直接跳过终局状态，
而 `run_finished` 必定发出（`src/Collector/Ipc/LiveEventBus.cs`）。
终局的那一句先等待一次 `GetDashboardStats` 返回后再播报，因为 `{progress}` 必须是本局之后的数值；
且 `UNKNOWN_FINAL_STATE` 的记录**在用户确认通关之前不计入**成就进度。
映射本身由 `AppController::announcementKind(state)` 实现，是静态映射，可由表驱动测试固定。
MockBackend 在模拟状态切换以及开始或停止捕获时发出这些事件，
并提供 `simulateRunTransitions()` 供测试驱动完整流程，其中包含最后一条 `RunFinished`。

**成就**（`achievementSettingsCard`）分为目标值与安装前已完成次数（基数）两列，修改原因为必填项。
采集服务对 `UpdateAchievementBaseline` 强制要求 reason（`ERR_REASON_REQUIRED`），页面先执行同样的检查；
采集服务的拒绝（`App.baselineFailed`）以红字显示在按钮上方。
内嵌框「进度 = 基数 + 软件记录」之后是数字字体的大号算式 `基数 + 记录 = 进度`（`progressFormula`），
右侧小字为「修改后立即重算 · 导入按 run_id 去重」。
左下为「重新打开首次引导」，右下为主按钮「保存」。

**数据**（`dataSettingsCard`，kicker 为「数据库」）包含以下内容：
数据库位置（只读、等宽，取自 `GetStatus.database_path`）与「打开目录」
（`QDesktopServices::openUrl` 指向本地文件夹）；
自动备份开关，小字为「每日启动时备份，保留 14 份」，
由 `AppController::runDailyBackupIfDue()` 在启动时按日触发一次 `BackupDatabase`；
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
「有新版本 x.y.z」，其右侧另有「打开下载页」（`aboutOpenReleasePageButton`），同样只在有新版本时出现。
没有新版本时，该位置是「检查更新」（`aboutCheckUpdateButton`），与设置页「通用」的同名按钮
发出同一条 `CheckUpdateNow`、遵循同一套停用规则，结果同样以一句提示说明。
小标题「隐私与边界」下有三块内嵌信息：Oodle 解压（`GetStatus.oodle_mode`）、
读取游戏可执行文件（`reads_game_executable`，为真时以橙色显示「是（DEC-OODLE-01）」）、
首次运行说明（「已确认 · 时间」或「未确认」）。
按钮为「重新查看首次运行说明」与「打开捕获诊断」（ghost）。
其下是 **版权与来源**（`copyrightCard`），保留四段完整表述，较原型的缩写更为准确。
早先独立的「隐私与边界」卡片，其内容已全部并入上述三块信息与两个按钮。

### 4.5.1 导随心得（reflections）

「心得」是每条导随记录上的一段自述，结构为 `{mood: good|ok|bad, text}`，
契约见 `contracts/ipc-v1.schema.json` 中的 `SetRunReflection` 与 `GetReflectionSummary`。
桌面端只绑定 C++ 侧的下列成员，且**全部做了存在性保护**，
因此后端尚未实现时，界面只显示空状态，而不会报错：

| 界面 | 绑定 |
|---|---|
| 总览「导随心得」面板 | `App.reflectionSummary`（`reflection_count` / `pending_completed_count` / `recent` / `next_pending`） |
| `补录心得` 按钮 | `next_pending` 存在则打开对话框，否则 toast「所有通关记录都已写过心得」 |
| 近期心得卡片（最多 3 张） | `App.openHistoryForRun(run)`，同时把详情浮层切到 `心得` 页签 |
| 历史筛选 `有心得` | `RunFilter.with_reflection` |
| 详情浮层「心得」页签 | `run.reflection`；有则 心情 tag + 时间 + 正文 + `编辑心得`，无则空状态框 + `补录心得` |
| `dialogs/ReflectionDialog.qml` | `App.saveReflection(runId, mood, text)`；`App.reflectionSaved` 关闭，`App.reflectionFailed` 在对话框内红字显示 |
| 通关后自动弹出 | `App.reflectionPromptRequested(run)` → `Main.qml` 的 `openReflection(run, "刚刚完成")` |
| 设置「通关后弹出心得窗口」 | `Settings.reflectPrompt` |
| 结束后询问结果 | `App.resultConfirmationRequested(run)` → 同一个对话框的 `openForResult(run)` |
| 设置「结束后询问本次结果」 | `Settings.confirmPrompt` |

对话框宽 560，自上而下依次为：kicker（刚刚完成 / 补录心得 / 编辑心得）与右侧的 run_id；
衬线标题「导随心得」；四列内嵌摘要（副本 / 职业 / 结果 / 耗时）；
`这次感觉` 分段控件（顺利 / 一般 / 糟心）；六行文本域。
页脚为小号开关「通关后自动弹出」、`稍后补录` 与 `保存心得`。

清空正文后保存等同于删除该条心得，契约规定 `text` 为空即删除，
因此 `保存心得` 在文本为空时不会被禁用。

### 4.5.2 本次导随结果（confirmation）

国服协议档案中没有通关判定的报文，一局导随结束后只会停在 `UNKNOWN_FINAL_STATE`
（`pending_review = true`，`result = UNKNOWN`），而成就进度只统计 `COMPLETED`，
因此**次数在用户确认之后才会增加**。
本软件不代替用户判定结果，也不将该判定完全交给事后的「待复核」列表；
0.2.2 中「完成一局而次数未变」的问题即源于后者。

同一个 `ReflectionDialog` 增加了问句模式（`askingResult`）。此时标题变为「本次导随结果」，
先询问《副本名》是否已完成，提供三个答案：**通关**、**未通关 / 中途离开**、**稍后再说**，
其下接原有的心得字段；`Settings.reflectPrompt` 关闭时只保留问句。
选择 `通关` 调用 `App.resolveRunResult(runId, revision, "COMPLETED", "用户确认通关")`；
选择 `未通关 / 中途离开` 走同一路径写入 `LEFT_OR_ABANDONED`；
选择 `稍后再说` 不写入任何内容，记录保留在待复核列表中。
已填写的心得随答案一并保存。答案被 Collector 接受（`mutationSucceeded`）后窗口才关闭；
被拒绝时（例如版本号过期）在对话框内以红字显示。

`resolveRunResult` 使用**与手动修正完全相同的 `CorrectRun`**：
一条修正中同时写入 `result` 与 `pending_review = false`。
因此确认本身也是一条可审计的新 revision，且不引入任何新的 IPC 消息。

总览的「待复核」提示条上提供同样的一对按钮，供当时仍在游戏中、未及回答的用户补充确认。
`App.pendingReviewRun` 是最近一条待确认的记录，仅在 `pendingReviewCount > 0` 时才查询。
每条记录只询问一次；`ended_at_utc` 早于本次启动的记录（重连时被重放的旧事件）不再询问。

### 4.6 首次运行说明（DEC-OODLE-01）

首次启动的顺序是**先显示说明、后显示 baseline**：`Main.qml` 在 `Component.onCompleted` 中
先打开 `DisclosureDialog`，用户确认后才打开 `BaselineDialog`。

说明页共六条，取值全部来自 `GetStatus`，而非硬编码的默认值：

1. 只被动监听网卡流量 — `capture.monitor_type`
2. **会读取游戏可执行文件的一份副本** — `oodle_mode` / `reads_game_executable`
3. 不注入、不读取游戏进程内存 — `capture.injected_hook_enabled`
4. 数据只留在本机 — 无遥测、无云同步、从不上传
5. **只有三种联网，且均由后台进程发出**，分四段叙述：总述；
   **联网一：游戏更新后获取共享校准**（默认开启），说明发送时机、不携带的内容，
   以及在「设置 → 通用」中的关闭方式（docs/privacy-boundary.md §8.2）；
   **联网二：在线语音**（默认关闭），仅在选择在线语音并填写自有密钥后才发送，
   发送内容为当前这一句播报，改回本机语音即停止（§8.3）；
   **联网三：检查新版本**（默认开启，可关），每 24 小时最多一次，只读取发布页上的版本号并与当前版本比较，
   不下载安装包、不自动安装，可在「设置 → 通用 → 更新」中关闭（§8.4）
6. 尚未在真实游戏流量上验证 — `LIVE_CAPTURE_STATUS`

说明正文可以滚动，确认开关与「我已了解」固定在底部；
加入第 5 条之后，720 像素高的窗口无法容纳整页。
第 5 条使 `AppSettings::kDisclosureVersion` 提升至 3，
此前的确认针对的是「没有任何出站网络请求」的版本。
在线语音并入第 5 条时**未**再次提升版本号：该功能默认关闭，选用时另有确认框（§4.5「播报」），
已确认版本 3 的用户不会因此被再次拦截。
更新检查并入第 5 条时**再次**提升了版本号：它默认开启，且没有单独的确认框，
因此每一位既有用户在升级之后都会再看到一次该页。
「联网一」写明在用的共享校准或按排本推断的校准会被再次核对时，版本号提升至 5：
此前的文案写的是「已经有可用档案时不会联网」，与新的行为不符（§8.2）。

必须先打开「我已阅读并理解」开关，「我已了解」按钮才可用。
确认结果写入 `AppSettings` 的 `ui/disclosure_acknowledged_version` 与 `ui/disclosure_acknowledged_at`。
存储的是**版本号**而非布尔值，因此 `AppSettings::kDisclosureVersion` 一旦提高，
旧的确认自动失效、说明页重新出现，对旧文案的同意不会被用于替代新文案。

设置页的「重新查看首次运行说明」先清除确认，再打开对话框。
`--show-disclosure` 命令行开关在两种后端下均可强制打开该页，用于截图与复查。

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
| 历史记录：列表、分页、排序、搜索、筛选 | `QueryRuns`（`filter` / `page` / `page_size` / `sort`）| 默认 `sort = {matched_at_utc, desc}`，每页 10 条 |
| 历史记录：导出 CSV / JSON | `ExportCsv` / `ExportJson` | 路径由 `QFileDialog` 选（默认「文档」）；`--export-target DIR` 可跳过对话框 |
| 历史记录：详情面板 | 行数据 + `GetRunRevisions` | 选中即拉修正历史 |
| 副本统计 | `GetDungeonStats` | 含 `content_id = null` 的「未知副本」行 |
| 职业统计 | `GetJobStats` | 含 `job_id = null` 的「未知」行；职能占比由 `job_id` 经职业目录推导 |
| 副本 / 职业柱状图点击 | `QueryRuns`（`filter.content_id` / `filter.job_id`）| 跳到历史页并预置筛选 |
| 捕获诊断 | `GetStatus` + `GetCaptureStatus` 形状 + `ListCaptureAdapters` + `GetProtocolProfileStatus` | 计数与 `recent_parser_errors` 是 `CaptureStatus` 的可选字段；缺失即 `—`。见 4.4 |
| 捕获诊断：开始 / 停止捕获 | `StartCapture` / `StopCapture` | 本机无 Npcap ⇒ `ERR_NPCAP_MISSING` |
| 捕获诊断：导出脱敏诊断报告 | `ExportDiagnosticsReport`（`target_path` 可省略）| 见 4.4 |
| 设置：成就进度保存 | `UpdateAchievementBaseline`（reason 必填）| 成功后重算总览与趋势 |
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
| `run_state_changed` | 先 `GetCurrentRun`，再播报 匹配 / 进本，最后刷新总览 |
| `run_finished` | 重新加载列表，刷新总览，**在总览回包里**播报终局那句并弹出「本次导随结果」 |
| `run_created` / `run_updated` | 重新加载历史列表与当前导随卡 |
| `stats_invalidated` | 刷新总览、趋势、副本统计、职业统计 |
| `collector_status` | 直接采用事件里的 `capture` 对象，再补一次 `GetStatus` |
| `heartbeat` | 直接返回：该事件只表明管道存活，落入下一行的处理会每 5 秒产生一串 IPC |
| 其他 | 一次廉价的 `GetStatus`，绝不静默丢弃 |

每条事件都带有单调递增的 `sequence`。每次连接或重连事件总线时，
总线会**不加标记地重放**最近 64 条事件，因此序号不高于已采用水位的事件一律丢弃，
以避免重连后重复播报旧导随并重复刷新。
断线时水位复位，因为重启后的 Collector 从头计数；
但「该记录的该状态已播报或已询问」这一守卫会持续保留。

### 4.7.3 错误信封 → 界面

所有 `ErrorEnvelope` 都带有 Collector 自身的中文 `message`。
界面**始终显示后端的原文**，并在括号中附上机器码，不改写为自拟的语句。

| 场景 | 出现位置 |
|---|---|
| `ERR_REASON_REQUIRED` / `ERR_TIME_ORDER` / `ERR_NEGATIVE_DURATION` / `ERR_NO_CHANGES` / `ERR_REVISION_CONFLICT` / `ERR_BAD_REQUEST`（新增 / 修正）| **修正对话框内的红色错误条** + toast |
| 同上（软删除 / 恢复）| **原因对话框内的红色错误行** + toast |
| `ERR_REASON_REQUIRED` 等（成就进度）| **成就进度卡内、字段下方的红色说明行** + toast |
| `ERR_NPCAP_MISSING` / `ERR_FFXIV_NOT_RUNNING` / `ERR_CAPTURE_*` | toast（页面本身已经在解释同一件事） |
| `ERR_EXPORT_FAILED` / `ERR_DB_*` | toast |
| 连接中断 / 超时 | toast + 顶栏 Collector 行 |

为使上表第一、二行成立，两个对话框在提交后**不立即关闭**：
按钮变为「提交中…」并禁用，直到 `AppController::mutationSucceeded` 到达后才关闭。
否则后端的拒绝理由只能显示在一个已经取代表单的 toast 上。

`GetRunRevisions` 是只读接口。`ipc-v1` 没有「撤销」消息，
`MessageDispatcher.KnownMessageTypes` 中同样没有，因此界面**不提供**撤销按钮。
`run_revisions` 为 append-only，撤销需再发一次 `CorrectRun` 实现。

## 4.8 Collector 生命周期在界面上的呈现

`CollectorProcess` 的状态直接映射到顶栏的圆点与文案，以及侧栏的 Collector 行：

| 状态 | 文案 | 触发 |
|---|---|---|
| `missing` | 未找到 Collector，请重新构建或检查安装目录 | 同目录下没有 exe |
| `idle` | Collector 可用，未启动 | 还没拉起 |
| `starting` | Collector 启动中… | `QProcess::start()` 已发出 |
| `running` | Collector 运行中 | 本软件启动的子进程仍在运行 |
| `reused` | Collector 运行中（复用已有实例）| 子进程因单实例租约立刻退出，但管道已连上 |
| `exited` | Collector 已退出（代码 N），x.x 秒后重启（第 N 次）| 非预期退出，指数退避 0.8 s → 30 s |
| `stopped` | Collector 已停止 | 由本软件主动停止 |

`reused` 是必须单独区分的一档。Collector 以一个具名事件实现每用户单实例租约，
第二个实例会立即带错误退出。若将其判定为崩溃，将导致无意义的重启循环。
判据是「短时退出且管道已连接」，并在重启定时器触发时再判定一次，
因为子进程可能先于 socket 的 connected 信号退出。

退出路径为：`AppController` 析构，调用 `CollectorProcess::stop()`，执行 `terminate()`，
3 秒后执行 `kill()`。托盘的「退出」菜单项，以及在「关闭时最小化到托盘」开启状态下关闭窗口，
均走这条路径。
进程被 `Stop-Process` 或任务管理器强制结束时不会执行析构，子 Collector 会残留。
下一次启动会以 `reused` 复用该进程，不会出错，但 `tasklist` 中会多出一个进程。

## 5. 手动修正对话框与校验

校验规则全部位于 C++ 的 `RunFormValidator` 中，QML 对话框与单元测试共用同一份实现。
本地拒绝时返回的错误码与 Collector 返回的错误码一致：

| 情况 | 错误码 |
|------|--------|
| 未填写修正/新增原因 | `ERR_REASON_REQUIRED` |
| 进本时间早于匹配时间 | `ERR_TIME_ORDER` |
| 无进本时间且结束时间早于匹配时间 | `ERR_TIME_ORDER` |
| 结束时间早于进本时间（耗时为负） | `ERR_NEGATIVE_DURATION` |
| 非 `CANCELLED_BEFORE_ENTRY` 却没有进本时间 | `ERR_BAD_REQUEST` |
| `COMPLETED` 却没有结束时间 | `ERR_BAD_REQUEST` |
| 修正模式下没有任何字段变化 | `ERR_NO_CHANGES`（"没有任何字段被修改。"） |
| 日期/时间格式非法 | `ERR_BAD_REQUEST` |

对话框在修正模式下显示原型的「修改前后对比」三列表格，
三列分别为 字段 / 修改前（删除线）/ 修改后（强调色）。
表格的行由 `RunForm.diff()` 生成，顺序固定为
副本 / 职业 / 匹配时间 / 进本时间 / 结束时间 / 结果 / 计入进度 / 备注。
没有差异时显示「尚未修改任何字段。」。

首次启动的 baseline 对话框（`dialogs/BaselineDialog.qml`）按原型排版，自上而下为：
`首次启动 · 1 / 1` kicker、大号标题、两个大字号数字输入（当前已完成次数与目标）、
生效时间说明，以及 `从 0 开始` 与 `保存并开始` 两个按钮。

新增与修正采用三步向导。状态与逻辑位于 `dialogs/EditRunDialog.qml`，
三步的界面分别为 `RunWizardResultStep.qml`、`RunWizardDutyStep.qml` 与 `RunWizardTimeStep.qml`，
另见计划 §5（内部工作文档，不随仓库分发）。
向导宽 `min(720, 窗口宽 − 40)`、高 `min(680, 窗口高 − 32)`；
标题、步骤条（可点击，显示每步当前值）与底部按钮栏固定，只有正文滚动。

* 第 1 步「结果」包含六张结果卡（`PickSurface`，颜色取自 `Fmt.resultColorToken`，
  结果代码只在维护者模式下显示）、开关「这是指导者任务，计入成就进度」（`goalToggle`）
  与进度预览（`progressPreview`）。
* 第 2 步「副本与职业」包含：人数分段（全部 / 4 人本 / 8 人本 / 24 人本 / 其他，
  取自 `DutyCatalog` 的 `party_size`）；搜索（支持名称、等级、`7.2` 一类版本号按资料片匹配、
  资料片中文名与种类）；等级区间与难度标签（`PickChip`，难度只在 全部 / 8 人本 / 24 人本 时出现）；
  「最近打过」（`App.recentDuties`，搜索时隐藏）；最多 210 项的列表与「共 N 个副本」。
  缺少人数、难度或等级的行，只在对应筛选为「全部」时列出。
  职业按 `Roles.roleGroups()` 分组，存在图标时使用 `JobIcon`，否则显示职能色的缩写方块。
* 第 3 步「时间与原因」包含：日期（`DateField`，可用日历选择或手动输入）；
  匹配时间（必填）、进本时间与结束时间（`TimeField`，只需输入数字，
  2130 转为 21:30、930 转为 09:30、213045 转为 21:30:45，离开字段时 9:5 转为 09:05；
  右侧时钟图标打开时 / 分滚轮，另有「现在」）；
  跨天时另有进本日期与结束日期，日期不同时自动出现，修改「日期」只带动仍处于原日期的那两个日期；
  随结果变化的提示、按匹配时间估算进本时间、原因（必填）、备注、备注图片、修改前后对比与错误条。
  任何拒绝（本地校验或采集服务）都会跳转到这一步。
* 备注图片（`NoteImageStrip`，`editable`）：已有图片与本次新选的缩略图（后者标「待保存」）、
  每张右上角的移除、「添加图片」（系统文件选择框）以及一行说明保存位置的提示。
  添加与移除只在对话框内暂存（`pendingImageAdds` / `removedImagePaths`），
  记录保存成功后才由 `NoteImages.commit()` 写入安装目录，取消即放弃。
  只改图片时不发送修正、不产生修订，「保存为新修订」在仅有图片改动时也可用；
  记录已保存而图片写入失败时对话框保持打开并给出原因，再次保存只重试图片（`savedRunId`），
  不会把记录再提交一次。不是图片、超过 20 MB、超过 20 张的选择在第三步的错误条里说明。
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
出现在面板外框与标题栏底线的尾段。成就进度环在此风格下沿弧线渐变（components/ProgressRing.qml 的锥形渐变）：12 点方向的起点为黄焰（`ringTail`，深 `#ffd84a` / 浅 `#f7bc1f`），末端为深橙焰（`ringStart`，深 `#e8401a` / 浅 `#c8380f`），两端在色相与明度上都拉开，全程不透明：半透明或低饱和的色段叠在轨道上会发灰；艾欧泽亚仍为对角直线渐变。标题栏经 `titlebarTop/Bottom/Brand/Text/TextSecondary/Mark/Fill` 取色，
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
| body 的两层 radial wash | `washTop` / `washBottom` | `Main.qml` 的 `backdropWash` Canvas 用 `createRadialGradient` 画真正的两层径向渐变，位置、半径、颜色同原型，两种风格相同 |
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
| `components/ProgressRing.qml` | 成就环：butt 端点 + `ffgold` 渐变 + r76/r96 两圈细金线 |

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
检查导航项与幽灵按钮各自的 `hoverColor`，经典风格下幽灵按钮的悬停色为 accent-100；
该测试同时检查主按钮的底色从不停留在透明黑上。
艾欧泽亚风格下主按钮底色藏在金色渐变之下，切换到经典风格时渐变立即消失，
底色若从透明黑开始渐变就会闪黑。

### 6.3 标题栏

标题栏为 `--chrome-bg` 渐变加 1 px `gold-3` 下边框。
左侧是一枚旋转 45° 的镀金菱形，其后是 Cinzel 字体的 `MENTOR ROULETTE` 与正文字体的 `导随记录器`。
右侧是 Collector 生命周期圆点与文案，以及 `btn-secondary` 小号的主题切换按钮。

标题栏**不绘制**原型中的 `— ▢ ✕` 三个装饰字符，也不绘制 macOS 交通灯圆点。
本软件是 Windows 应用，窗口按钮由系统边框提供，复制到真实窗口上会出现两套关闭按钮。

### 6.4 动效

全部动画均读取 `Theme.qml` 的 `motion*` 时长。`Theme.motion` 为假时这些值全为 0，
动画在同一帧落到终态。
`ReduceMotion` 是 `main.cpp` 设置的上下文属性，在两种情况下为真：
截图模式，此时帧必须确定；以及 Windows 的「在 Windows 中显示动画」处于关闭状态
（`SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` 返回假，该值只在启动时读取一次）。
缓动曲线取 workbench.css 的 `--ease`，即 `cubic-bezier(.2,.7,.2,1)`（`Theme.curveStandard`），
时长为 200–220 ms（`motionControl` 与 `motionMedium`）。

两种风格都有的微交互：

| 组件 | 规则 |
|---|---|
| `AppButton` | 按下时缩至 0.96；减少动效时不缩放 |
| `NavItem` | 悬停时右移 2 px，按下时缩至 0.97；按下时不右移，减少动效时两者均不生效 |
| `EditRunDialog` 换步 | 新一步淡入并从来向滑入 16 px，下一步自右、上一步自左，时长 `motionMedium`；两种风格相同，减少动效时直接落位（`stepProgress` / `stepDirection`） |
| `SegmentedControl` | 选中项放大至 1.05 倍、左右内边距各 +3 px；其余项缩至 0.94、内边距各 −2 px、不透明度 0.75，悬停时回到 1 与 0.97；按下时为 0.92。这是静止样式，截图中同样如此 |
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
变化期间 Qt Graphs 的柱体隐藏，改由运动层按 Qt Graphs 自身的几何绘制，
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
离屏窗口没有交换链，日志中的 `Failed to present` 属于预期输出；帧采用条目抓取，而非 `grabWindow()`。
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
                             # 选中对应的在线音色（并视为确认过），退出时把 desktop.ini 里的选择还原
    [--mock-open-speech-confirm]
                             # 以本机语音起步，打开在线语音确认框（与 --mock-speech 同用时问的是该状态的音色）
    [--mock-speech-preview]  # 启动 0.4 秒后播一次「试听」，配 --mock-speech fail 可截到回退提示

  两种后端都可用：
    [--open-detail]          # 同上，但不要求 mock 数据
    [--open-edit]
    [--show-disclosure]      # 强制打开首次运行说明页
    [--mock-open-create]     # 打开「新增遗漏记录」三步向导
    [--mock-wizard-step 1|2|3]
                             # 向导停在第几步（配合 --mock-open-create / --mock-open-edit，默认 1）
    [--mock-detail-tab refl] # 详情浮层默认页签 info / events / revs / refl（会一并打开浮层）
    [--mock-ui-style classic]# 固定界面风格 classic / eorzea / harendotes，不改写 desktop.ini
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
2. **事件摘要页签**：Collector 契约目前不返回逐事件摘要，因此该页签显示说明文字
   而不是伪造的 opcode 列表。
3. **完整性校验**：`CheckDatabaseIntegrity` 自 2026-09-16 起纳入契约，执行只读的 `PRAGMA integrity_check`，
   由数据页的按钮调用。原型中的结果仅为一个 toast，实现改为按钮下方的结果行，见 4.5。
   更早版本的采集服务返回 `ERR_UNKNOWN_MESSAGE`，界面显示「当前采集器不支持完整性校验」，不会谎称通过。
4. **撤销修正**：原型的修正历史中有「撤销此修正（生成新 revision）」按钮；
   契约没有对应消息，本软件未实现该功能。
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

职能图标共六枚，映射由 C++ `mr::RoleCatalog`（QML 上下文属性 `Roles`）负责：

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
