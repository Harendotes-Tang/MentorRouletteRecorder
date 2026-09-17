# 职业映射 / Job Mapping

将 `job_id` 映射为职业名称与定位（role），用于界面展示与 `GetJobStats`。

> **当前为空。** 自 Phase 1 起按需补充。

## 格式（计划）

```jsonc
{
  "schema_version": 1,
  "language": "zh-Hans",
  "source": "说明这份数据是怎么整理出来的，附 URL 与访问日期",
  "updated_at_utc": "2026-01-01T00:00:00.000Z",
  "jobs": [
    {
      "job_id": 0,
      "name": "示例职业",
      "abbreviation": "XXX",
      "role": "TANK"
    }
  ]
}
```

`role` 取值：`TANK` / `HEALER` / `DPS` / `UNKNOWN`。

## 规则

- **统计一律按 `job_id` 聚合**。
- `job_id IS NULL` **自成一类**，显示为 `未知`，
  **绝不并入任何具体职业，也绝不在图表中被隐藏**
  （见 [`../../docs/statistics-definitions.md`](../../docs/statistics-definitions.md) §11）。
- 映射表中查不到的 `job_id` 同样归入 `未知`，并在诊断日志中记录一条（仅记录 id）。
- **不得复制游戏的数据文件或资源**；仅收录自行整理、来源可说明的公开信息。
