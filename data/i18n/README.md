# 多语言游戏名称 / Game Name Translations

为桌面端的界面语言切换（简体中文、繁體中文、English、日本語）提供游戏文本的译名：
副本、职业、随机任务的名称，以及副本分类与资料片的名称。译名按与简体中文数据相同的编号索引。

> **纯展示数据。** 本目录的数据不参与导随识别，不构成协议证据，
> 也不会使任何协议档案变为可用。统计一律按编号聚合，名称仅用于展示。

## 文件

```
data/i18n/
  game-names.<data_version>.json    繁体中文（zh-TW）、英文（en）、日文（ja）译名
```

`<data_version>` 为**数据版本**，即抓取当天的 UTC 日期（形如 `2026-10-15`），并非客户端版本。
简体中文名不在本文件中重复收录，仍以以下文件为准：

| 映射 | 简体中文来源（基准文件） | 本文件中的键 |
|---|---|---|
| `duties` | `data/duties/cn.<data_version>.json` 的 `content_id` | `"<content_id>"` |
| `jobs` | `data/jobs/jobs.json` 的 `job_id` | `"<job_id>"` |
| `roulettes` | `data/roulettes/cn.json` 的 `roulette_id` | `"<roulette_id>"` |
| `duty_categories` | 副本文件中的 `duty_category` 标签 | 标签原文，如 `"四人迷宫"` |
| `expansions` | 副本文件中的 `expansion` 标签（英文） | 标签原文，如 `"A Realm Reborn"` |

所用基准文件记录在 `provenance.base_files` 中。

当前随包提供：

| 文件 | 基准副本文件 | 副本 | 职业 | 随机任务 | 分类 | 资料片 |
|---|---|---|---|---|---|---|
| `game-names.2026-10-15.json` | `cn.2026-09-04.json` | 857 | 43 | 10 | 19 | 6 |

其中繁体中文译名的构成：

| 映射 | 繁中客户端原文 | OpenCC 转换 | 项目自定 |
|---|---|---|---|
| `duties` | 781 | 76 | 0 |
| `jobs` | 42 | 1（43 驯兽师） | 0 |
| `roulettes` | 10 | 0 | 0 |
| `duty_categories` | 18 | 0 | 1（`其他`） |
| `expansions` | 6 | 0 | 0 |

英文与日文译名全部取自游戏文本，不存在转换或自定的条目。

## 数据结构

```jsonc
{
  "schema_version": 1,
  "data_version": "2026-10-15",            // 与文件名中的一段一致
  "source_language": "zh-Hans",
  "languages": ["zh-TW", "en", "ja"],
  "updated_at_utc": "…",
  "source": "…",                           // 来源与授权说明
  "provenance": {
    "fetched_at_utc": "…",
    "xivapi": {
      "game_version": "284bb7f44b9c0976",  // XIVAPI 的版本键
      "game_version_names": ["7.55x2"],
      "fields": { "ContentFinderCondition": "Name,TerritoryType@as(raw)", "…": "…" },
      "requests": [{ "url": "…", "sha256": "…" }]
    },
    "datamining_tc": {
      "repository": "https://github.com/thewakingsands/ffxiv-datamining-tc",
      "commit": "…", "client_version": "2026.09.14.0000.0000",
      "requests": [{ "url": "…", "sha256": "…" }]
    },
    "datamining_cn": { "…": "…" },         // 仅 ExVersion.csv
    "zh_tw_fallback": { "method": "OpenCC", "config": "s2twp", "package": "opencc", "package_version": "1.4.2" },
    "base_files": { "duties": "data/duties/cn.2026-09-04.json", "jobs": "…", "roulettes": "…" },
    "row_counts": { "duties": { "total": 857, "zh_tw_official": 781, "zh_tw_converted": 76 } },
    "territory_checked": { "zh-TW": 781, "en": 857, "ja": 857 },
    "territory_mismatches": { "zh-TW": [5, 13, 36, 235], "en": [], "ja": [] }
  },
  "duties":          { "4":  { "zh-TW": "天然要害沙斯塔夏溶洞", "en": "Sastasha", "ja": "天然要害 サスタシャ浸食洞" } },
  "jobs":            { "19": { "zh-TW": "騎士", "en": "Paladin", "ja": "ナイト" } },
  "roulettes":       { "9":  { "zh-TW": "指導者", "en": "Mentor", "ja": "メンター" } },
  "duty_categories": { "四人迷宫": { "zh-TW": "迷宮挑戰", "en": "Dungeons", "ja": "ダンジョン" } },
  "expansions":      { "A Realm Reborn": { "zh-CN": "重生之境", "zh-TW": "新生艾奧傑亞", "en": "A Realm Reborn", "ja": "新生エオルゼア" } },
  "zh-TW_converted": { "duties": [1011, 1028], "jobs": [43], "roulettes": [], "duty_categories": [], "expansions": [] },  // duties 为节选
  "project_maintained": { "duty_categories": ["其他"] }
}
```

说明：

- JSON 对象的键只能是字符串，因此编号以十进制字符串作键（`"4"`），与基准文件中的整数编号一一对应。
- `zh-TW_converted` 列出繁体中文名为 OpenCC 转换结果的条目：`duties` / `jobs` / `roulettes`
  为**整数**编号，`duty_categories` / `expansions` 为标签字符串。未列出的繁体中文名均为繁中客户端原文。
- `project_maintained` 列出译名由本项目给定、并非游戏文本的条目。目前仅有兜底分类 `其他`
  （`其他` / `Other` / `その他`）。
- `jobs` 的条目仅在某语言的职业缩写与 `jobs.json` 的 `abbreviation` 不同时才带 `abbr`
  （形如 `"abbr": { "ja": "…" }`）。本版本中三种语言的缩写均与英文三字母缩写一致，因此没有 `abbr`。
- `expansions` 的条目额外带 `zh-CN`：副本文件中的 `expansion` 为英文标签，简体中文界面同样需要译名。
- 名称一律为单行文本，保留游戏原文中的全角符号、空格与特殊连字符（如 `Toto–Rak` 中的 `–`）。
- 每个编号的译名在文件中各占一行，便于审阅差异。

## 来源与字段

| 语言 | 来源 | 版本 |
|---|---|---|
| `en` / `ja` | XIVAPI v2，`language=en` / `language=ja` | 固定为基准副本文件 `provenance.xivapi_game_version` 记录的版本（`284bb7f44b9c0976`，XIVAPI 标注为 `7.55x2`） |
| `zh-TW` | thewakingsands/ffxiv-datamining-tc，繁體中文版客户端的 SaintCoinach 导出 | 提交 `1cf1f9fb735bad0748848dbd3f4de05721fe619a`，客户端版本 `2026.09.14.0000.0000` |
| `zh-CN`（仅 `expansions`） | thewakingsands/ffxiv-datamining-cn 的 `ExVersion.csv` | 提交 `2c6741cffe3cfe70f093c54c463791364e515d8d`，客户端版本 `2026.10.06.0000.0000` |

英文、日文与繁中客户端的数据表共用同一套行号，各映射取自以下数据表与列：

| 映射 | 数据表 | `en` | `ja` / `zh-TW` |
|---|---|---|---|
| `duties` | `ContentFinderCondition` | `Name`（首字母大写，见下文） | `Name` |
| `jobs` | `ClassJob` | `NameEnglish` | `Name` |
| `roulettes` | `ContentRoulette` | `Category` | `Category` |
| `duty_categories` | `ContentType` | `Name` | `Name` |
| `expansions` | `ExVersion` | `Name` | `Name` |

- **副本**按 `content_id`（`ContentFinderCondition` 的行号）关联。生成器同时比对各来源的
  `TerritoryType` 与基准文件的 `territory_id`：已比对的行数记入 `provenance.territory_checked`，
  不一致的编号记入 `provenance.territory_mismatches`。
  本版本中繁中客户端有 4 行不一致（5、13、36、235），均为国际服已重制、繁中客户端尚未更新的迷宫，
  名称不受影响。不一致的行数超过已比对行数的 5%（且多于 5 行）时，说明行号已错位；
  可读的领地编号不足应比对行数的 90% 时，说明该列已变动。两种情况下生成器均报错退出。
- **职业**的英文名取 `NameEnglish` 列，即游戏自身的首字母大写形式（`White Mage`），
  而非用于句中的小写 `Name` 列（`white mage`）。
- **随机任务**取 `Category` 列，即不带「隨機任務：」「Duty Roulette:」前缀的简称，
  与 `data/roulettes/cn.json` 的写法一致：该文件 10 个编号的名称均与国服 `ContentRoulette.Category` 相同。
  各客户端的称呼可能不同，例如繁中客户端的编号 5 为「高難度」、编号 8 为「100級」。
- **副本分类**的译名取该分类所由来的 `ContentType` 的名称。分类由
  `tools/duty-data-generator/generate.py` 的 `CATEGORY_BY_CONTENT_TYPE` 从 `ContentType` 得出，
  简体中文标签由本项目自定，因此译名的措辞可能与简体中文标签不同。例如：
  `四人迷宫` → `迷宮挑戰` / `Dungeons` / `ダンジョン`；
  `团队任务` → `滅激鬥戰` / `Chaotic Alliance Raid` / `滅アライアンスレイド`
  （该分类仅由 `Chaotic Alliance Raid` 这一 `ContentType` 得出，其余团队任务归入 `大型任务`）。
  映射表中当前数据表已不存在的 `ContentType`（`Adventuring Forays`）不生成译名；
  副本文件实际用到的分类缺少译名时，生成器报错退出。
- **资料片**按 `ExVersion` 的行号关联，键为英文名。

### 英文名的首字母

XIVAPI 返回的英文副本名保留游戏原文的大小写。以冠词开头的名称在游戏中会被嵌入句中，
因此原文首字母为小写，例如 `the Praetorium`。为便于直接显示，生成器**仅将第一个字符改为大写**
（`The Praetorium`），其余字符保持原样；以非字母开头的名称不作改动。本版本中受此规则影响的副本名为 250 条。
除此之外，三种语言的名称均为游戏原文，不作任何改写。

## 繁体中文的回退：OpenCC 转换

FINAL FANTASY XIV 繁體中文版于 2025 年 12 月正式上市，其客户端版本落后于国服与国际服。
繁中客户端尚无某一编号，或该行名称为空时，生成器使用 [OpenCC](https://github.com/BYVoid/OpenCC)
的 `s2twp` 配置（简体中文 → 台湾正体，含台湾惯用词替换）对简体中文名进行**机械转换**，
并将该编号列入 `zh-TW_converted`。

- 转换结果**不是官方译名**，也不保证与繁中客户端日后的正式译名一致。界面展示时应当能够区分，
  例如注明「译名由简体中文转换」。
- 本版本中转换的 76 个副本均为繁中客户端 `2026.09.14.0000.0000` 尚未收录的较新内容
  （`content_id` 1011 至 1118 之间），职业仅 43（驯兽师 → 馴獸師）一项。
- 英文与日文**从不转换或推测**：基准文件中的任一编号在 XIVAPI 中缺少英文名或日文名时，生成器报错退出，不写出文件。
- 繁中客户端更新后重新生成即可。转换条目会随之减少，`zh-TW_converted` 也会相应缩短。

## 生成

本文件由副本数据生成器的 `--game-names` 模式生成。按静态硬边界检查的规定，
全部网络请求由 `tools/duty-data-generator/generate.py` 发出；不联网的
`tools/duty-data-generator/game_names.py` 负责解析、合并、OpenCC 回退与写出。
两者共用分类映射表与仓库边界检查。

```bash
python tools/duty-data-generator/generate.py --game-names --version 2026-10-15
python tools/duty-data-generator/generate.py --game-names --dry-run   # 抓取并统计，不写任何文件
python tools/duty-data-generator/test_game_names.py                   # 离线单元测试（解析、合并、写出）
python tools/duty-data-generator/test_generate.py                     # 含抓取与命令行的离线测试
```

OpenCC 仅在繁中客户端缺少名称时才需要，且只在生成时使用，不随程序分发：

```bash
python -m pip install opencc        # 官方 Python 绑定；opencc-python-reimplemented 亦可
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `--version <label>` | 当天的 UTC 日期 | 写入文件名与 `data_version` 的数据版本，须为 `YYYY-MM-DD` 形式 |
| `--duties <file>` | `data/duties/` 下日期最新的 `cn.<date>.json` | 需覆盖其全部 `content_id` 的基准副本文件 |
| `--jobs <file>` / `--roulettes <file>` | `data/jobs/jobs.json` / `data/roulettes/cn.json` | 基准职业、随机任务文件 |
| `--game-version <key>` | 基准副本文件记录的 `xivapi_game_version` | XIVAPI 版本键；`latest` 表示采用 XIVAPI 当前标注为 latest 的版本键，所有请求仍固定到该键。基准文件未记录版本且未指定本参数时拒绝运行 |
| `--tc-ref <ref>` / `--cn-ref <ref>` | `main` / `master` | 两个 datamining 仓库的分支或提交；运行时解析为提交号并记入 `provenance` |
| `--out-dir <dir>` | `data/i18n` | 输出目录 |
| `--raw-dir <dir>` | 临时目录 | 原始下载的存放位置，规则与副本数据生成器相同：位于仓库内时拒绝运行，`--dry-run` 时不保存 |

一次运行共发出 23 个请求：XIVAPI 15 个（5 张数据表 × 2 种语言，`ContentFinderCondition`
分页 3 次，另有 1 个版本列表请求），GitHub 8 个（2 次提交解析与 6 个 CSV）。
每个请求的 URL 与响应的 SHA-256 均记入 `provenance`；原始下载**不纳入仓库**。
任一来源失败、缺少必需的名称或基准文件为空时，生成器打印原因并以退出码 1 结束，不写出任何文件，现有数据保持不变。
输出先写入同目录的临时文件，确认可被完整解析后再整体替换目标文件。
在相同的版本键与提交号下重新生成，除时间戳外输出逐字节相同。

更新基准副本文件（`data/duties/cn.<新日期>.json`）后，应重新生成本文件，使其覆盖新增的 `content_id`。
新旧文件可以并列保留。离线测试会核对最新的本文件是否以最新的基准副本文件为基准，
遗漏重新生成时测试失败。

## 许可与来源

- **游戏数据本身 © SQUARE ENIX CO., LTD.**
  FINAL FANTASY 是史克威尔艾尼克斯的注册商标。本项目与 SE 无关联。
  本文件中的名称为 FINAL FANTASY XIV 的游戏文本，依 FINAL FANTASY XIV Materials Usage License
  以非商业方式注明出处后用于展示。本目录保存的是**编号到名称的展示映射**，并非游戏资源文件；
  本项目不复制、不再分发游戏的任何数据文件。
- **XIVAPI v2**（<https://v2.xivapi.com>）：公开的只读 API，用于获取英文名与日文名。
- **thewakingsands/ffxiv-datamining-tc**
  （<https://github.com/thewakingsands/ffxiv-datamining-tc>）：繁體中文版客户端的
  SaintCoinach 导出，用于获取繁体中文名。

  > **该仓库没有 LICENSE 文件**（`GET /repos/thewakingsands/ffxiv-datamining-tc`
  > 返回 `license: null`，`raw.../main/LICENSE` 返回 404，2026-10-15 核对），
  > 与 ffxiv-datamining-cn 的情况相同，属于同一个**未决的许可问题**，见
  > [`../../docs/third-party-licenses.md`](../../docs/third-party-licenses.md)。
  > 在该问题解决之前，本文件中的繁体中文名应视为可随时移出仓库、改为安装时在本地生成的数据。

- **thewakingsands/ffxiv-datamining-cn**：仅用于资料片的简体中文名，许可情况见
  [`../duties/README.md`](../duties/README.md)。
- **OpenCC**（Apache License 2.0）：仅作为生成时的转换工具使用，不随程序分发，不链接进任何产物。

## 规则

- **统计一律按编号聚合**，名称仅用于展示。
- 本文件中查不到某编号或某语言的译名时，界面应回退到简体中文数据中的名称，
  **不得自行翻译或推测**；简体中文数据也查不到时，按各自目录 README 的规则显示为未知。
- **不得复制游戏的数据文件或资源**；仅收录来源可说明的公开信息，
  并在 `provenance` 中注明来源、URL、抓取时间与原始下载的 SHA-256。
- 缺失本目录的文件时本软件必须正常运行（界面仅显示简体中文名），不得崩溃。
