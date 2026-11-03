# 职业映射 / Job Mapping

将 `job_id` 映射为职业名称与定位（role），用于界面展示与 `GetJobStats`。

`jobs.json` 当前收录 43 个职业（`job_id` 1–43）。编号、缩写与英文名取自公开的 xivapi v2 `ClassJob` 表，简体中文名与六个定位分组由本项目维护，来源写在文件的 `source` 与 `aliases_zh_tw_source` 中。

## 格式

```jsonc
{
  "version": 1,
  "schema_version": 1,
  "language": "zh-Hans",
  "source": "…",                 // 来源与访问日期
  "aliases_zh_tw_source": "…",   // aliases_zh_tw 的来源
  "role_values": ["坦克", "治疗", "近战", "远程物理", "魔法", "其他"],
  "role_to_stat_role": { "坦克": "TANK", … },
  "unknown": { "name_zh": "未知", "role": "未知" },
  "updated_at_utc": "2026-09-04T00:00:00.000Z",
  "jobs": [
    {
      "job_id": 24,
      "abbreviation": "WHM",
      "name_en": "white mage",
      "name_zh": "白魔法师",
      "aliases_zh": ["白魔"],          // 可选：经批准的简体中文简称
      "aliases_zh_tw": ["白魔道士"],   // 可选：繁中服客户端的职业名
      "role": "治疗"
    }
  ]
}
```

| 字段 | 用途 |
|---|---|
| `name_zh` | 界面显示的简体中文名；导入时按它识别职业 |
| `name_en`、`abbreviation` | 导入时按英文名或缩写识别职业，不区分大小写 |
| `aliases_zh` | 经批准的完整简体中文简称（当前 19 个），导入与历史搜索都会识别 |
| `aliases_zh_tw` | 繁中服客户端导出的职业名（当前 42 个），只用于导入识别，不参与搜索；仅由简体转换得到的名称不收录 |
| `role` | 六个定位分组之一，经 `role_to_stat_role` 归入统计用的定位 |

`role` 分组与统计定位（下表与 `jobs.json` 一致）：

| 分组 | 统计定位 | 职业数 |
|---|---|---|
| 坦克 | `TANK` | 6 |
| 治疗 | `HEALER` | 5 |
| 近战 | `DPS` | 10 |
| 远程物理 | `DPS` | 4 |
| 魔法 | `DPS` | 7 |
| 其他 | `UNKNOWN` | 11 |

一个名称或简称必须恰好指向一个职业；指向多个职业时不做选择，按未知职业处理。

## 规则

- **统计一律按 `job_id` 聚合**。
- `job_id IS NULL` **自成一类**，显示为 `未知`，
  **绝不并入任何具体职业，也绝不在图表中被隐藏**
  （见 [`../../docs/statistics-definitions.md`](../../docs/statistics-definitions.md) §11）。
- 映射表中查不到的 `job_id` 同样归入 `未知`，并在诊断日志中记录该 id（不记录其他信息）。
- **不得复制游戏的数据文件或资源**；仅收录自行整理、来源可说明的公开信息。
