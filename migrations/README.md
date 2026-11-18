# 数据库迁移 / Database Migrations

本目录存放本软件的数据库迁移脚本。本文面向需要新增或审阅迁移的维护者，说明迁移的命名、执行规则与版本记录方式。

> 当前 schema 版本：**16**。`DATABASE_STATUS = READY`。
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
> | 9 | `0009_personal_record_import.sql` | 导入的本人记录的来源元数据 |
> | 10 | `0010_history_retention.sql` | 回收站保留期、永久清理的事务内授权与墓碑 |
> | 11 | `0011_run_joined_in_progress.sql` | 记录是否中途加入进行中的副本，只增一列 |
> | 12 | `0012_regional_records.sql` | 区服白名单加入 `TC`（重建 `capture_sessions` 与 `mentor_runs`），成就设置按区服分行，原设置归入国服 |
> | 13 | `0013_legacy_region_cn.sql` | schema 12 之前创建、没有采集会话的 `UNKNOWN` 记录（1.8.0 及更早的补录与无区服列的导入）归入国服 |
> | 14 | `0014_character_profiles.sql` | 角色档案 `character_profiles` 取代按区服一行的成就设置；`mentor_runs.profile_id` 记录所属档案，各区服原有设置、基数历史与全部记录归入一个档案 |
> | 15 | `0015_character_identities.sql` | 角色识别 `character_identities`（见过的角色的带密钥摘要及其绑定的档案）；`mentor_runs.identity_id` 记录匹配时登录的角色，既有记录一律为 NULL |
> | 16 | `0016_duty_community.sql` | 副本攻略及社区内容 |

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
3. 迁移在**单个事务**中执行；失败则整体回滚，且不写入 `schema_migrations`。重建表所需的
   `PRAGMA foreign_keys` 与 `PRAGMA legacy_alter_table` 只能在事务外切换，由采集服务在事务前设置、
   事务后恢复（见下文 schema 11 → 12）。
4. 需要迁移时，采集服务先把整个数据库复制为 `backups/pre_schema_<旧版本>_upgrade_<UTC 时间>_<编号>.db`
   并核对副本的完整性，复制或核对失败时不执行迁移，并以 `startup.pre_upgrade_copy_failed` 拒绝启动；
   这类快照不参加常规备份的滚动清理。迁移随后失败并整体回滚时，数据库仍是升级前的样子，本次快照随即删除，
   因此每次启动都失败的迁移不会每次多留一份副本；成功的升级恰好留下一份。回到旧版程序时，
   应以这份快照（或其他早于升级的备份）替换数据库，不得以删除迁移历史的方式降级。
5. 若数据库中的版本**高于**本程序支持的最高版本 → `ERR_DB_INTEGRITY`，且采集服务拒绝启动，
   以免旧版程序破坏新版数据。本软件没有只读模式：错误信息会给出数据库文件位置，
   提示先把该文件复制一份留底，再改用能识别该版本的程序打开。
   开发构建默认使用独立的数据目录与管道（[`../docs/architecture.md`](../docs/architecture.md) §2.3），
   在开发机上运行带新迁移的构建不会把正式安装版的数据库升到它不认识的版本。
6. 迁移脚本是纯 SQL，不含任何数据获取逻辑；**不得**在迁移里写入用户数据。例外有两处：0013 只改写既有记录的
   区服一列，使 schema 12 引入的区服划分与旧版程序写入时的含义一致；0014 只填写既有记录新增的归属一列，
   使每个区服的全部记录归入接收该区服原有设置的档案（均见下文）。
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
反查出来的 `content_id` 写进统计聚合列，偏移错误可能导致副本统计失真。0008 仅为
`mentor_runs` 增加一列 `duty_source TEXT NULL`，取值受
`CHECK (duty_source IS NULL OR duty_source IN ('CONTENT_ID', 'TERRITORY', 'MANUAL'))` 约束。

| 值 | 含义 |
|---|---|
| `CONTENT_ID` | 副本编号来自报文里明确的 `content_id` |
| `TERRITORY` | 由观察到的区域编号经本地副本表推断 |
| `MANUAL` | 人工新建或更正时填写 |
| `NULL` | 这条记录尚无任何副本身份，或为 0008 之前写入的历史行 |

该迁移只增一列，不改动 0001–0007 及其 checksum，也不重建任何表。新增列**不出现在 IPC
传输格式中**，`$defs/Run` 未变，仅作为本机数据库中的来源标注供维护者核对。字段语义见
[`../docs/data-model.md`](../docs/data-model.md) §1。

## schema 10 → 11

国服客户端进本后，副本的导演报文先宣布开始，之后才报告剩余时间；中途加入的玩家收不到这一场的开始宣布，
先到的是剩余时间（[`../docs/protocol-profile-format.md`](../docs/protocol-profile-format.md) §13）。
0011 仅为 `mentor_runs` 增加一列 `joined_in_progress INTEGER NULL`，取值受
`CHECK (joined_in_progress IS NULL OR joined_in_progress IN (0, 1))` 约束：`1` 中途加入，`0` 开局进入，
`NULL` 软件没有看到（包括 0011 之前写入的历史行）。该迁移只增一列，不改动 0001–0010 及其 checksum，
也不回填任何值。该列进入 IPC（`$defs/Run.joined_in_progress`）并可人工更正，语义见
[`../docs/data-model.md`](../docs/data-model.md) §1.5。

## schema 11 → 12

繁中服加入后，`capture_sessions.region` 与 `mentor_runs.region` 的 CHECK 白名单需要加入 `TC`。SQLite 不能修改
CHECK，因此采集服务在执行 0012 脚本之前，按 SQLite 文档的“新建、复制、删除、改名”步骤在同一事务中重建这两张表：
表定义取自现有 schema，只替换白名单文本，原有的列、约束、索引与触发器都保留；白名单文本与预期不符时拒绝迁移。
重建前在事务外关闭外键检查并打开 `legacy_alter_table`（使引用这两张表的触发器与视图在改名后仍指向原名），
提交前以 `PRAGMA foreign_key_check` 核对外键，发现违例即整体回滚；两项 PRAGMA 在事务结束后恢复原值。
0012 本身把单行成就设置改为每个区服一行，原有的目标与基数归入国服，并为国服与繁中服补齐默认行。
这两类拒绝与其他迁移失败一样使用 `startup.migration_failed`，并给出数据库文件位置。

## schema 12 → 13

schema 12 之前本软件只服务国服：1.8.0 及更早把所有手动补录与没有区服列的导入都存为 `UNKNOWN`，
它们计入当时唯一的成就进度，按「已包含」导入时还从那份基数中扣除过。0012 把那份基数归入国服，
却让这些记录留在 `UNKNOWN`，国服进度因此在升级后减少。0013 把 `region = 'UNKNOWN'`、没有采集会话
（`capture_session_id IS NULL`）、且创建时间早于 `schema_migrations` 中第 12 版应用时间的记录改为 `CN`；
采集写入的记录带有采集会话，保持会话记录的区服；数据库升到 12 之后才创建的 `UNKNOWN` 记录是在 schema 12 的
程序里有意选的「未知区服」，也保持不变。同一次升级从 11 直接到 13 时，第 12 版的记录先于 0013 写入同一事务。
迁移只改这一列：不追加修订、不改更新时间，也不改导入来源指纹。之后新建的手动补录与导入不受影响，
省略区服时仍按契约存为 `UNKNOWN`。

## schema 13 → 14

为按角色分别统计成就进度，0014 新增 `character_profiles`：一个区服可以有多个档案，每个档案有自己的显示名称
（用户填写，不读取角色名）、目标与基数，其中恰有一个是新记录归入的「当前档案」。迁移把 `achievement_settings`
的每一行迁入一个名称为空的当前档案（只有记录而没有设置行的区服按默认值建立），为 `mentor_runs` 增加可空的
`profile_id` 并把每个区服的全部记录指向该区服的档案，再把按区服保存的基数修改历史键改到档案名下，最后删除旧表。
升级前后各区服的目标、基数与进度逐项相等；迁移不追加修订、不改更新时间。

`profile_id` 用 AUTOINCREMENT，编号删除后不复用。两个触发器保证记录只能归入同区服的档案，一个触发器禁止档案
更换区服；外键使仍有记录引用的档案无法删除。用户在旧表上自建的索引会随旧表一并删除。

## schema 14 → 15

为按登录的角色自动归入档案，0015 新增 `character_identities`：采集服务见过的每个角色一行，保存角色永久标识（登录报文负载前 8 字节）在本机密钥下的
HMAC-SHA256 摘要（`digest`，32 位十六进制）与该密钥的指纹（`key_id`），以及它绑定的档案（`profile_id`）或「不统计」标记（`ignored`），
不保存任何游戏内标识或角色名。`(region, key_id, digest)` 唯一；`CHECK (profile_id IS NULL OR ignored = 0)` 保证一个角色不会既绑定又不统计；
两个触发器保证角色只能绑定到同区服的档案。`identity_id` 用 AUTOINCREMENT，删除后不复用。

`mentor_runs` 增加可空的 `identity_id` 及部分索引 `ix_runs_identity`。迁移不回填：升级前的全部记录为 NULL，归属不变；
之后只有国服在开启角色识别时写入的自动记录才可能带有该值（[docs/data-model.md](../docs/data-model.md) §1.7、§4.3）。迁移不追加修订、不改更新时间。

## `schema_migrations` 表

| 列 | 说明 |
|---|---|
| `version` | 迁移版本号（PK） |
| `name` | 迁移文件名 |
| `checksum` | 脚本内容的 SHA-256 |
| `applied_at_utc` | 应用时间（UTC ISO-8601 带毫秒） |

## schema 15 → 16

`0016_duty_community.sql` 新增 `community_local`、`community_cache` 和 `community_receipts`。旧历史、心得、角色和校准相关数据保持原值。Collector 启动时按现有流程留存 v15 升级前快照。
