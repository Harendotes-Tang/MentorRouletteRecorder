# 数据库迁移 / Database Migrations

本目录存放本软件的数据库迁移脚本。本文面向需要新增或审阅迁移的维护者，说明迁移的命名、执行规则与版本记录方式。

> 当前 schema 版本：**8**。`DATABASE_STATUS = READY`。
>
> | 版本 | 文件 | 内容 |
> |---|---|---|
> | 1 | `0001_initial.sql` | 全部表、索引、CHECK 约束与 `run_revisions` 的只追加触发器 |
> | 2 | `0002_pending_review_and_note.sql` | `mentor_runs.pending_review`、`mentor_runs.note`，以及按旧口径回填待复核标记 |
> | 3 | `0003_run_reflections.sql` | `run_reflections` 表（导随心得：`mood` / `text`），及按 `updated_at_utc` 倒序的索引 |
> | 4 | `0004_candidate_observations.sql` | 独立候选观测、只追加核对历史及默认关闭的设置 |
> | 5 | `0005_candidate_research_payload.sql` | 研究负载可空列，历史上限 256 字节 |
> | 6 | `0006_candidate_window_samples.sql` | 候选窗口样本的出现次数 |
> | 7 | `0007_candidate_research_payload_512.sql` | 研究负载上限扩大至 512 字节，事务内保留原负载与核对关联 |
> | 8 | `0008_run_duty_source.sql` | 记录副本身份的来源，只增一列 |

## 命名

```
migrations/
  0001_initial_schema.sql
  0002_<描述>.sql
  ...
```

版本号为四位数字，单调递增且无空洞，其后接小写下划线描述。

## 规则

1. **只向前**。已发布的迁移**永不修改**。`schema_migrations.checksum` 记录每个迁移
   文件内容的 SHA-256，历史迁移一经改动即触发 `ERR_DB_INTEGRITY`。需要变更时应新增
   一个迁移。
2. **只有 Collector 执行迁移**，且在启动时、开放 IPC 之前完成。
3. 迁移在**单个事务**中执行；失败则整体回滚，且不写入 `schema_migrations`。
4. 启动迁移不会自动创建备份，备份由显式 `BackupDatabase` 操作生成。若需保留旧版程序
   可用的数据，应在升级前备份，不得以删除迁移历史的方式降级。
5. 若数据库中的版本**高于**本程序支持的最高版本 → `ERR_DB_INTEGRITY`，并进入只读模式，
   以免旧版程序破坏新版数据。
6. 迁移脚本是纯 SQL，不含任何数据获取逻辑；**不得**在迁移里写入用户数据。
7. 每个迁移均须有对应测试：空库 → 迁移 → 断言表结构与约束。详见
   [`../docs/data-model.md`](../docs/data-model.md)。

## schema 6 → 7

自 2026-09-08 起，研究模式最多保存完整的 512 字节负载，超过上限的负载整段不保存。
0007 暂存既有非空负载，替换该列的 CHECK 约束后回填。该迁移未重建观测父表，因此观测
与核对记录的标识、rowid、外键、索引、触发器和 occurrences 全部保留。整个过程由既有
迁移事务包围，失败时连同版本记录一并回滚。0001–0006 文件及其历史 checksum 保持不变。
默认关闭、显式白名单、混淆条目拒绝与正式记录隔离仍受
[隐私边界](../docs/privacy-boundary.md#51-研究模式被动候选验证的显式例外)约束。

## schema 7 → 8

`ZONE_TERRITORY` 的字段偏移尚未在副本内负载上核实。此前 `SetDuty` 会把由 `territory_id`
反查出来的 `content_id` 写进统计聚合列，一个错误的偏移足以污染副本统计。0008 仅为
`mentor_runs` 增加一列 `duty_source TEXT NULL`，取值受
`CHECK (duty_source IS NULL OR duty_source IN ('CONTENT_ID', 'TERRITORY', 'MANUAL'))` 约束。

| 值 | 含义 |
|---|---|
| `CONTENT_ID` | 副本编号来自报文里明确的 `content_id` |
| `TERRITORY` | 由观察到的区域编号经本地副本表推断 |
| `MANUAL` | 人工新建或更正时填写 |
| `NULL` | 这条记录尚无任何副本身份，或为 0008 之前写入的历史行 |

该迁移只增一列，不改动 0001–0007 及其 checksum，也不重建任何表。新增列**不出现在 IPC
线格式上**，`$defs/Run` 未变，仅作为本机数据库中的来源标注供维护者核对。字段语义见
[`../docs/data-model.md`](../docs/data-model.md) §1。

## `schema_migrations` 表

| 列 | 说明 |
|---|---|
| `version` | 迁移版本号（PK） |
| `name` | 迁移文件名 |
| `checksum` | 脚本内容的 SHA-256 |
| `applied_at_utc` | 应用时间（UTC ISO-8601 带毫秒） |
