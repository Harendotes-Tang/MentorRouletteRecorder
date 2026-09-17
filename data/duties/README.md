# 副本名称映射 / Duty Name Mapping

将 `content_id` 映射为可读的副本名称与分类，用于界面展示与
`GetDungeonStats` 的 `duty_name` 回退。

> **纯展示数据。** 本目录的数据不参与导随识别，不构成协议证据，
> 也不会使任何协议档案变为可用。

## 文件

```
data/duties/
  cn.<data_version>.json         国服，简体中文名
  global.<data_version>.json     国际服，英文名
  cn.sample.json                 合成样例（Phase 1 起就在），永远排在真实数据之后
```

`<data_version>` 为**数据版本**，并非客户端版本，形如 `2026-09-04`（UTC 日期）。
文件名中的该字段即 `DutyCatalog` 排序与回退所用的键。

当前随包提供：

| 文件 | 区服 | 行数 | 来源 |
|---|---|---|---|
| `cn.2026-09-04.json` | CN | 857 | XIVAPI v2 + ffxiv-datamining-cn |
| `global.2026-09-04.json` | GLOBAL | 857 | XIVAPI v2 |
| `cn.sample.json` | CN | 4 启用 | 合成样例，`sample: true` |

三份文件均由 `src/Collector/MentorRecorder.Collector.csproj` 以**内嵌资源**形式编入程序集
（`data/duties/*.json` → `MentorRecorder.Collector.Data.Duties.<file>`）。
因此运行时既不依赖工作目录，也不依赖可执行文件同级目录中是否存在这些文件。
`cn.sample.json` 一并纳入仓库：`ReferenceCatalogTests`、`StatisticsTests`、`ExportTests` 与
集成回放测试均依赖其合成名称，缺少该文件将导致测试失败。

## 加载与回退规则（`src/Collector/Reference/DutyCatalog.cs`）

1. 按区服筛选；
2. **合成样例始终排在真实数据之后**，因此新增样例不会覆盖真实名称；
3. 所请求的 `data_version` 已安装时使用该版本，否则使用该区服**最新的**一版；
4. 更旧的版本仍用于**补空**：新表中已删除的 `content_id` 仍可查得名称；
5. 所有版本均查不到的 `content_id` 显示为 `未知副本`，**不作推测**，并自成一类
   （见 [`../../docs/statistics-definitions.md`](../../docs/statistics-definitions.md) §10）。
6. 文件全部缺失时本软件必须正常运行（`duty_name` 保持 `NULL`），不得崩溃。

## 生成

```bash
python tools/duty-data-generator/generate.py --version 2026-09-04
python tools/duty-data-generator/generate.py --dry-run     # 只抓取并统计，不写文件
python tools/duty-data-generator/test_generate.py          # 离线单元测试，不联网
```

省略 `--version` 时采用当天的 UTC 日期。一次运行**同时**写出
`global.<version>.json` 与 `cn.<version>.json`，二者共用同一份 XIVAPI 抓取结果。
另有两个参数：`--out-dir`（默认为本目录）与 `--raw-dir`（原始下载的存放目录，默认为临时目录）。

为已有版本补充人数（仅新增 `party_size`，其余字段不变，按文件中记录的游戏版本抓取）：

```bash
python tools/duty-data-generator/generate.py --add-party-size data/duties/cn.2026-09-04.json data/duties/global.2026-09-04.json
```

生成器仅执行两项操作：从 XIVAPI v2 分页读取 `ContentFinderCondition`（英文名、
`TerritoryType`、`ContentType`、等级需求、`ContentMemberType` 人数）；从 ffxiv-datamining-cn 的 CSV 读取中文名。
两者按 `ContentFinderCondition` 的行号连接。**原始下载不纳入仓库**，仅将其 SHA-256
记入 `provenance`。抓取失败时不写出任何文件，保留现有数据。

缺少中文名的行**不会**写入 `cn.*.json`，亦不回退为英文名。界面因此显示 `未知副本`，
而非形似译名的推测值。

## 字段

```jsonc
{
  "schema_version": 1,
  "region": "CN",                  // CN | GLOBAL | UNKNOWN
  "language": "zh-Hans",
  "data_version": "2026-09-04",    // 必须与文件名中的一段一致
  "sample": false,                 // true 表示合成样例，排序时永远靠后
  "updated_at_utc": "…",
  "source": "…",
  "provenance": {
    "fetched_at_utc": "…",
    "xivapi_urls": ["…"],          // 逐页 URL
    "xivapi_sha256": "…",          // 原始下载的摘要
    "xivapi_game_version": "…",
    "datamining_cn_url": "…",
    "datamining_cn_sha256": "…",
    "row_counts": {                // 当前这一版的实际值
      "xivapi_rows": 1118, "datamining_cn_rows": 857,
      "global_duties": 857, "cn_duties": 857
    },
    "note": "…",                   // 固定文案：原始下载不进仓库，只留摘要
    "party_size": {                // 补抓人数那一次的来源（2026-09-16 起）
      "fields": "ContentMemberType.MembersPerParty * ContentMemberType.PartyCount",
      "fetched_at_utc": "…",
      "xivapi_game_version": "…",  // 与上面的 xivapi_game_version 相同：按原版本补抓
      "xivapi_urls": ["…"],
      "xivapi_sha256": "…",
      "rows_without_party_size": 0
    }
  },
  "duties": [
    {
      "content_id": 1,
      "territory_id": 1039,
      "localized_name": "监狱废墟托托·拉克千狱",
      "duty_category": "四人迷宫",
      "expansion": "A Realm Reborn",
      "level": 24,
      "party_size": 4,             // 队伍人数；null = 数据源没写
      "enabled": true
    }
  ]
}
```

`party_size` 取自游戏自身的队伍构成：`ContentMemberType.MembersPerParty × PartyCount`
（XIVAPI v2，`ContentFinderCondition.ContentMemberType`）。4 为轻锐小队，8 为满编小队，
24 为三支小队组成的团队；PVP、探索类等另有 1、10、48、72 等取值，均原样保留。
该字段是区分 `大型任务` 中 8 人副本与 24 人团队副本的依据，用于桌面端向导的「人数」筛选，
见 `src/Desktop/cpp/DutyCatalog.cpp`。`2026-09-04` 版的人数于 2026-09-16 通过
`--add-party-size` 补充：请求固定在该版记录的 `xivapi_game_version`，因此其余字段与行顺序
逐字不变；上游中文表此后新增的 6 行未纳入。

2026-09-04 版各任务行的人数分布：

| duty_category | party_size | 行数 |
|---|---|---|
| 四人迷宫 | 4 | 103 |
| 讨伐歼灭战 | 8 / 4 | 102 / 5（旧版剧情讨伐战等轻锐小队讨伐） |
| 大型任务 | 8 / 24 | 137 / 18（24 人为团队任务：水晶塔、伊瓦利斯、尼尔、神域、巡行等） |
| 团队任务 | 24 | 1 |
| 行会令 | 4 / 8 | 13 / 1 |

难度（普通 / 极 / 零式）不写入数据文件，由桌面端按名称判定（`DutyCatalog::difficultyForName`）。

`duty_category` 由 `ContentType.Name` 映射而来，映射表定义于
`tools/duty-data-generator/generate.py` 的 `CATEGORY_BY_CONTENT_TYPE`：

| ContentType | duty_category |
|---|---|
| Dungeons | 四人迷宫 |
| Trials | 讨伐歼灭战 |
| Raids | 大型任务 |
| Guildhests | 行会令 |
| Chaotic Alliance Raid | 团队任务 |
| Ultimate Raids | 绝境战 |
| Deep Dungeons | 深层迷宫 |
| V&C Dungeon Finder | 多变迷宫 |
| PvP | PVP |
| Quest Battles | 任务战斗 |
| Treasure Hunt | 寻宝 |
| Adventuring Forays / Eureka / Save the Queen / Occult Crescent | 探索性任务 / 禁地探索 / 南方战线 / 新月岛 |
| Gold Saucer / The Masked Carnivale / Disciples of the Land | 金碟游乐场 / 假面狂欢 / 采集活动 |
| 其余 | 其他 |

> **团队任务（24 人副本）没有独立的 `ContentType`**：游戏的
> `ContentFinderCondition.ContentType` 将其与 8 人大型任务归为一类。
> `duty_category` 不按队伍人数改写，因此除 `Chaotic Alliance Raid` 外的团队任务
> 仍归入 `大型任务`。区分 8 人与 24 人需参照同一行的 `party_size`。

## 许可与来源

- **游戏数据本身 © SQUARE ENIX CO., LTD.**
  FINAL FANTASY 是史克威尔艾尼克斯的注册商标。本项目与 SE 无关联。
  本目录保存的是**副本名称与分类的展示映射**，并非游戏资源文件；
  本项目不复制、不再分发游戏的任何数据文件。
- **XIVAPI v2**（<https://v2.xivapi.com>）：公开的只读 API，用于获取英文名、
  `TerritoryType`、`ContentType`、等级需求与队伍人数（`ContentMemberType`）。生成器逐页请求，最多 10 次。
- **thewakingsands/ffxiv-datamining-cn**
  （<https://github.com/thewakingsands/ffxiv-datamining-cn>）：国服客户端的
  SaintCoinach 导出，用于获取中文名。生成器仅请求
  `ContentFinderCondition.csv` 一次。

  > **该仓库没有 LICENSE 文件**（`GET /repos/thewakingsands/ffxiv-datamining-cn`
  > 返回 `license: null`，`raw.../master/LICENSE` 返回 404，2026-09-04 核对）。
  > 该仓库**未给出任何明示的再分发许可**。本目录仅保存
  > 由其派生的 `content_id → 中文名` 映射，且映射的原始内容为
  > © SQUARE ENIX 的游戏文本。
  >
  > **这是一个未决的许可问题**，见
  > [`../../docs/third-party-licenses.md`](../../docs/third-party-licenses.md)。
  > 在该问题解决之前，`cn.<version>.json` 应视为可随时移出仓库、
  > 改为安装时在本地生成的数据。

## 规则

- **统计一律按 `content_id` 聚合**，名称仅用于展示。
- 查不到的 `content_id` 显示为 `未知副本`，并单独聚为一行，**不作推测**。
- **不得复制游戏的数据文件或资源**；仅收录自行整理、来源可说明的公开信息，
  并在 `provenance` 中注明来源、URL、抓取时间与原始下载的 SHA-256。
- 缺失这些文件时本软件必须正常运行（`duty_name` 保持 `NULL`），不得崩溃。
