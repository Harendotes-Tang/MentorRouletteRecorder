# 随机任务名称映射 / Roulette Name Mapping

将 `ContentRoulette` 编号（副本查找器状态更新 S2C 0x0323 第 16 字节）映射为可读的
随机任务名称，用于界面展示。

> **纯展示数据。** 本目录的数据不参与导随识别，不构成协议证据，
> 也不会使任何协议档案变为可用。判定随机任务种类的证据
> 是本机流量中的 0x0323 字节 16，见
> [`../../protocol-profiles/cn/cn.2026.08.05.json`](../../protocol-profiles/cn/cn.2026.08.05.json)
> 的 `provenance.evidence`。本目录的文件仅为该数值提供可读名称。

## 文件

```
data/roulettes/
  cn.json       国服，简体中文名，10 个编号
  global.json   国际服，英文名，最佳努力（BEST EFFORT），未经权威来源核对
```

与 `data/duties/` 不同，这两份文件**不按 `data_version` 分版本**：随机任务的
编号表远比副本表稳定，目前随包仅提供一份。`RouletteCatalog` 因此不具备
`DutyCatalog` 的版本请求与回退逻辑，仅按区服
（`Region.Cn` / `Region.Global` / `Region.Unknown`）取名。

## 当前收录的 10 个编号

| 编号 | 国服名称 | 备注 |
|---|---|---|
| 1 | 练级迷宫 | |
| 2 | 拾级迷宫 | |
| 3 | 主线任务 | |
| 4 | 公会令 | |
| 5 | 高难度任务 | |
| 6 | 纷争前线 | |
| 8 | 大型任务 | |
| 9 | 指导者任务 | 编号来自数据表，尚未在本机流量中以弹窗形式观察到 |
| 15 | 团队任务 | |
| 17 | 主线任务（新版） | |

## 加载与回退规则（`src/Collector/Reference/RouletteCatalog.cs`）

1. 按区服筛选；
2. 所请求区服查不到某个编号时，返回 `null`（`NameOf`）或 `false`
   （`IsKnown`），**不作推测**，**不回退到其他区服**。原则是：
   另一区服的映射恰好共用某个编号，不能作为本区服的证据；
3. 唯一的例外是 `Region.Unknown`。该取值表示用户手工填写且未指明客户端，
   并非某个真实区服，因此任何已装载的映射均为当前可给出的最佳答案；
4. 所有文件缺失时本软件必须正常运行（`RouletteCatalog.Default` 退化为空表，
   全部查询回落到 `RouletteCatalog.UnknownRouletteName`），不得崩溃。

## 字段

```jsonc
{
  "schema_version": 1,
  "region": "CN",                 // CN | GLOBAL | UNKNOWN
  "language": "zh-Hans",
  "source": "…",                  // 来源与授权说明
  "updated_at_utc": "…",
  "provenance": {
    "source_url": "…",
    "accessed_utc": "…",
    "note": "…"
  },
  "roulettes": [
    { "roulette_id": 1, "localized_name": "练级迷宫", "enabled": true }
  ]
}
```

两份文件均由 `src/Collector/MentorRecorder.Collector.csproj` 以**内嵌资源**形式
编入程序集（`data/roulettes/*.json` →
`MentorRecorder.Collector.Data.Roulettes.<file>`）。因此运行时既不依赖工作目录，
也不依赖可执行文件同级目录中是否存在这些文件。

## 来源与授权

- **`cn.json`**：数据取自
  [thewakingsands/ffxiv-datamining-cn 的 `ContentRoulette.csv`](https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master/ContentRoulette.csv)
  （2026-09-07 访问。该仓库没有 LICENSE 文件，此处按未决许可问题记录，另见
  `data/duties/README.md` 与
  [`../../docs/third-party-licenses.md`](../../docs/third-party-licenses.md)）。
  除编号 9（指导者任务）外，其余九个编号均已在
  `cn.2026.08.05.json` 的本机流量证据中逐一核对。
- **`global.json`**：**最佳努力（BEST EFFORT）**。英文名称为社区通用称法的
  整理，**并非**来自权威的国际服 `ContentRoulette` 导出，亦未经国际服
  本机流量核对。在找到权威来源之前，该文件中的每个名称仅供阅读，
  不构成证据。
- **游戏数据本身 © SQUARE ENIX CO., LTD.**
  FINAL FANTASY 是史克威尔艾尼克斯的注册商标。本项目与 SE 无关联。
  本目录保存的是**随机任务名称的展示映射**，并非游戏资源文件；本项目不复制、
  不再分发游戏的任何数据文件。

## 规则

- **统计一律按 `roulette_id` 聚合**，名称仅用于展示。
- 查不到的 `roulette_id` 显示为 `RouletteCatalog.UnknownRouletteName`
  （"未知随机任务"），**不作推测**。
- **不得复制游戏的数据文件或资源**；仅收录自行整理、来源可说明的公开信息，
  并在 `provenance` 中注明来源、URL 与访问时间。
- 缺失这些文件时本软件必须正常运行，不得崩溃。
