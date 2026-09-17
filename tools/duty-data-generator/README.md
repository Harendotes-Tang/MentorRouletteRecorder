# 副本数据生成器 / Duty Data Generator

本工具从公开来源生成 `data/duties/<region>.<data_version>.json`。本文面向需要更新副本映射数据的维护者。
本工具基于 Python 3，**只使用标准库**。

```bash
python tools/duty-data-generator/generate.py --version 2026-09-04
python tools/duty-data-generator/generate.py --dry-run       # 抓取并统计，不写文件
python tools/duty-data-generator/generate.py --add-party-size data/duties/cn.2026-09-04.json data/duties/global.2026-09-04.json
                                                             # 给已有文件补 party_size，其余不动
python tools/duty-data-generator/test_generate.py            # 离线单元测试，不联网
```

## 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--version <label>` | 当天的 UTC 日期（`YYYY-MM-DD`） | 写进文件名与 `data_version` 的**数据版本**标签 |
| `--out-dir <dir>` | `data/duties` | 输出目录 |
| `--raw-dir <dir>` | 临时目录 | 原始下载的存放位置，不进入仓库 |
| `--dry-run` | 关 | 抓取并打印统计，**不写任何文件** |
| `--game-version <key>` | XIVAPI 当前版本 | 将每一页请求固定到该游戏版本，即 `provenance.xivapi_game_version` 记录的键 |
| `--add-party-size <file>…` | — | 不生成新版本，只为这些已有文件逐行补写 `party_size`（置于 `level` 之后），行顺序与其余字段逐字不变；按各文件记录的 `xivapi_game_version` 抓取，来源记入 `provenance.party_size`。任一步骤失败均不写文件 |

一次运行**同时**写出 `global.<version>.json` 与 `cn.<version>.json`。
两者共用同一次 XIVAPI 抓取，仅取名所用的列不同。

## 数据来源与字段

| 步骤 | 来源 | 请求数 |
|---|---|---|
| 英文名 / `TerritoryType` / `ContentType` / 等级需求 / 队伍人数 | `https://v2.xivapi.com/api/sheet/ContentFinderCondition?fields=…&limit=500&after=…[&version=…]` | ≤ 10（分页，2026-09-04 版本实际为 3） |
| 简体中文名 | `https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master/ContentFinderCondition.csv` | 1 |

XIVAPI 侧只请求这些字段：
`Name,ClassJobLevelRequired,ContentType.Name,TerritoryType.ExVersion.Name,ContentMemberType.MembersPerParty,ContentMemberType.PartyCount`。
`territory_id` 取自 `TerritoryType` 链接自身的 row id。
`expansion` 取自其下一层的 `ExVersion.Name`，取不到时写 `UNKNOWN`。
`party_size` 为 `MembersPerParty × PartyCount`，取值如 4 / 8 / 24；两者缺一或非正整数时写 `null`。
`大型任务` 中 8 人副本与 24 人团队副本共用一个 `ContentType`，由该字段区分。

两个来源按 `ContentFinderCondition` 的**行号**（`content_id`）关联。
TLS 最低版本为 1.2，证书校验开启。两项均定义在 `http_get()` 中，任何降级都会是一次可见的改动。
User-Agent 固定为 `MentorRecorder-duty-data-generator/1.0 (+local, no telemetry)`。

## 游戏版本更新后的处理

副本映射是**纯展示数据**，与协议档案无关，因此无需等待任何证据：

1. 执行一次 `--dry-run`，核对行数与上一版的差异。差异过大通常意味着上游结构变更；
2. `python tools/duty-data-generator/generate.py --version <新的 UTC 日期>`；
3. 新文件与旧文件**并列保留**，不得删除旧文件。`DutyCatalog` 采用最新版本，
   并以更旧的版本**补空**，使新表中已删除的 `content_id` 仍可查到名称；
4. `provenance.xivapi_game_version` 记录 XIVAPI 报告的游戏版本串，用于事后核对。

CSV 的 `Name` 列**按列名定位**。上游表头变更时，生成器**报错退出**，退出码为 1，
不会将各值整体错位一列，现有文件保持原样。
XIVAPI 侧的链接字段为软失败：取不到时写 `null` / `UNKNOWN` / `其他`，不作猜测。
因此更新版本后应检查 `duty_category` 中 `其他` 的数量是否异常增长。

## 不做的事项

- **不复制游戏数据文件**：只取出"行号 → 名称 / 分类 / 领地 / 等级"这一层映射。
- **不把原始下载放进仓库**：原始 JSON/CSV 写入临时目录，仓库中只保留其 SHA-256。
- **不编造中文名**：没有中文名的行不会进入 `cn.*.json`，UI 显示 `未知副本`。
- **不在抓取失败时写文件**：任一来源失败 → 打印原因，退出码 1，现有文件保持不变。
- **不按队伍人数改写分类**：`duty_category` 只由 `ContentType` 决定，人数单独写入
  `party_size`。详见 `data/duties/README.md` 中关于 24 人副本的说明。

## 输出

字段说明与加载/回退规则见 [`../../data/duties/README.md`](../../data/duties/README.md)。
`provenance` 记录逐页 URL、抓取时间、行数与原始下载的 SHA-256，
因此一份结果可以在不再分发上游数据的前提下复现与核对。

## 许可

- 游戏数据 © SQUARE ENIX CO., LTD.；本项目与 SE 无关联。
- XIVAPI v2 是公开只读 API。
- **`thewakingsands/ffxiv-datamining-cn` 没有 LICENSE 文件**
  2026-09-04 核对结果为：GitHub API 返回 `license: null`，`raw.../LICENSE` 返回 404。
  该来源因此没有明示的再分发许可。此情况已记入
  [`../../data/duties/README.md`](../../data/duties/README.md)。
  在此问题解决之前，应将 `cn.*.json` 视为可能需要改为"安装时本地生成"的数据。

## 离线测试

`sample/` 下为一份**人为缩小的**样例，包含 4 行 XIVAPI 数据（其中两行带人数）与 4 行 CSV 数据。
`test_generate.py` 仅使用该样例，因此可以在完全没有外网的 CI 上运行：

```bash
python tools/duty-data-generator/test_generate.py
```

覆盖范围：CSV 表头按列名定位（上游结构变更时报错而非错位）、
英文与中文两种本地化的行筛选、分类映射与未映射回落、
`territory_id` / `expansion` / `level` 的透传、`party_size` 的计算与缺失时的 `null`、
`--add-party-size` 只增加一个字段且不改变行序、排序稳定性、`provenance` 组装。
另有一条测试读取随包的 `data/duties/*.2026-09-04.json`，确认随机任务涉及的各类行均带有人数。
