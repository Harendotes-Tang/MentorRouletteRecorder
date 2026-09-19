# 统计口径定义 / Statistics Definitions

> 本文件中的口径是**固定的**。任何实现、UI 文案与导出结果都必须与此一致。
> 若将来需要调整，必须先修改本文件并同步修改契约与测试，不允许在实现中就地变通。

本文件定义本软件全部统计指标的计算口径，包括候选集合的过滤前提、各项计数与比率的公式，
以及必须覆盖的边界用例。桌面端、采集服务与导出功能均以本文件为唯一依据，
供实现者与审阅者查阅。

## 0. 通用过滤前提

除非另有说明，所有统计的候选集合都先满足：

```sql
soft_deleted = 0
```

软删除的记录**永远不参与任何统计**，无论查询是否带 `include_deleted`。
`include_deleted` 只影响列表展示，不影响统计。

**进行中的记录同样不参与任何统计：**

```sql
NOT (source = 'AUTO_NETWORK' AND result = 'UNKNOWN' AND ended_at_utc IS NULL AND pending_review = 0)
```

自动记录在副本尚未结束时以 `result = 'UNKNOWN'`、`ended_at_utc IS NULL`、`pending_review = 0` 保存。
它还没有结果：计入尝试次数会使完成率在整场副本期间被拉低，并在「未知结果」中多出一条。
副本结束后（`ended_at_utc` 已写入）即正常计入，结果未知的记录仍计入尝试次数与「未知」。
崩溃恢复交给用户复核的未结束记录（`pending_review = 1`，见 state-machine.md §3.9）**不属于**进行中：
该次游玩已经结束，只是结果无人见证，照常计入，并继续计入 `unfinished_pending_review`。

用户提供的 `RunFilter`（时间范围、副本、职业、结果、来源、文本等）在此基础上叠加。

## 1. 确认为导随（confirmed mentor）

一条记录被视为"确认为导随"，当且仅当满足以下任一条件：

- `source = 'AUTO_NETWORK'` 且 `mentor_roulette_id IS NOT NULL`，
  即状态机因 `roulette_id == mentor_roulette_id` 成立而创建该记录；
- `source = 'MANUAL'`，即用户手工补录的记录，按定义即为导随；
- `source = 'IMPORT'` 且导入源显式标注为导随。

不满足上述条件的记录不进入任何统计。

## 2. 尝试次数 `attempt_count`

```
attempt_count = COUNT(*) WHERE
      entered_at_utc IS NOT NULL
  AND soft_deleted = 0
  AND confirmed_mentor
```

说明：

- 记录**必须真正进入过副本**。因此 `CANCELLED_BEFORE_ENTRY` 的记录
  （`entered_at_utc IS NULL`）**不计入** `attempt_count`，也不出现在完成率与离开率的分母中。
- `attempt_count` 是所有比率指标的**唯一分母**。

## 3. 完成次数 `completed_count`

```
completed_count = COUNT(*) WHERE
      result = 'COMPLETED'
  AND soft_deleted = 0
  AND confirmed_mentor
```

`result = 'COMPLETED'` 蕴含 `entered_at_utc IS NOT NULL`，因为只有 `ENTERED_DUTY`
才能迁移到 `COMPLETED`。

## 4. 成就进度 `achievement_progress`

```
achievement_progress =
      achievement_settings.baseline_completed_count
    + COUNT(*) WHERE
          contributes_to_goal = 1
      AND result = 'COMPLETED'
      AND soft_deleted = 0
      AND confirmed_mentor
```

- `baseline_completed_count` 是用户自行申报的、开始使用本软件之前已完成的次数，
  经 `UpdateAchievementBaseline` 设置。该消息必须携带 `reason`，并写入审计。
- 记录被用户取消勾选 `contributes_to_goal` 后**不计入进度**，但**仍计入**
  `attempt_count` 与 `completed_count`，因为它仍然是一次真实的完成。
- 成就进度**不受 `RunFilter` 的时间范围影响**，始终采用全量口径。
  仪表盘上的「本周」与「本月」卡片使用 `completed_count`，而非 `achievement_progress`。

## 5. 剩余次数 `remaining`

```
remaining = MAX(goal_count - achievement_progress, 0)
```

`goal_count` 默认 **2000**。永不为负。

## 6. 完成率 `completion_rate`

```
completion_rate = completed_count / attempt_count        (attempt_count > 0)
completion_rate = null                                   (attempt_count = 0)
```

`attempt_count = 0` 时返回 `null`，UI 显示 `—`。**绝不显示 0%**，也绝不以 0 代替未定义值。

## 7. 离开率 `leave_rate`

```
leave_rate = COUNT(result = 'LEFT_OR_ABANDONED') / attempt_count   (attempt_count > 0)
leave_rate = null                                                  (attempt_count = 0)
```

> **`DISCONNECTED`、`INTERRUPTED`、`UNKNOWN` 单独展示，永远不并入离开率。**
>
> 理由：掉线与程序中断并非用户主动放弃的行为，将其计入离开率会给出错误且不公平的
> 自我评价。UI 上这三类必须各自成行，并可单独查看。
>
> `DISCONNECTED` 一行由真实链路产生：已投递过解码消息的游戏连接收到 FIN/RST，
> 或从系统连接表中消失时，即产生该结果（见 [state-machine.md](state-machine.md) §3.6）。
> 此前该结果没有任何生产者，该行恒为 0，掉线只会归入 `INTERRUPTED` 或 `UNKNOWN`。

## 8. 平均时长 `avg_duration_ms`

```
avg_duration_ms = AVG(duration_ms) WHERE
      result = 'COMPLETED'
  AND soft_deleted = 0
  AND confirmed_mentor
  AND duration_ms IS NOT NULL
  AND duration_ms >= 0
  AND entered_at_utc IS NOT NULL
  AND ended_at_utc IS NOT NULL
  AND entered_at_utc <= ended_at_utc
```

没有记录满足条件时返回 `null`。

说明：

- **只统计 `COMPLETED`**，未完成的副本时长不具可比性。
- 时间必须合法，即非空且顺序正确，同时 `duration_ms >= 0`。
- `duration_ms` 来自单调时钟。统计时不允许以 `ended - entered` 现算来补齐缺失值，
  缺失的记录一律排除。

## 9. 结果分布 `GetResultStats`

对 `RunResult` 的**全部六个取值**各输出一个桶，即使计数为 0：

`COMPLETED`、`LEFT_OR_ABANDONED`、`CANCELLED_BEFORE_ENTRY`、`DISCONNECTED`、
`INTERRUPTED`、`UNKNOWN`。

- 桶**永不合并**。
- `share = count / attempt_count`；`attempt_count = 0` 时 `share = null`。
- `CANCELLED_BEFORE_ENTRY` 的 `count` 可以大于 0，而 `attempt_count` 并不包含它。
  因此 `CANCELLED_BEFORE_ENTRY` 桶的 `share` 恒为 `null`，UI 需单独说明
  "未进入副本，不计入尝试"。

## 10. 副本统计 `GetDungeonStats`

- **按 `content_id ?? territory_id` 聚合**，不按 `duty_name` 聚合，因为名称可能随版本或
  语言变化。报文中带有 `content_id` 时按其聚合，仅观察到区域时按 `territory_id` 聚合。
  两者位于**各自的键空间**内，因此一个 territory id 不可能与一个 content id 归入同一组。
  该规则的必要性在于：自动记录不再将由 `territory_id` 反推出的 `content_id` 写入记录
  （见 [state-machine.md](state-machine.md) §3.11 与 [data-model.md](data-model.md) §1.4）。
  若仍只按 `content_id` 聚合，仅按区域识别出的副本会全部并入"未知副本"一行。
- 仅按区域识别出的那一组在 IPC 上仍回报 **`content_id: null`**，名称取自本地副本表
  `DutyCatalog.FindByTerritory`，且只查询该记录所属区服。
- `content_id` 与 `territory_id` **均**为 NULL 的记录聚成**一行**，
  `content_id = null`，`duty_name = "未知副本"`。
- 每行输出 `attempt_count`、`completed_count`、`completion_rate`、`avg_duration_ms`，
  口径与上文完全一致，仅将候选集限制在该组之内。
- `duty_name` 取该组下最近一次非空的名称，查不到时回退到 `data/duties/` 映射表。
- 排序依次按 `attempt_count` 降序、`content_id`，最后以 `duty_name` 做稳定的并列打破，
  否则两组 `content_id = null` 的行顺序不确定。

## 11. 职业统计 `GetJobStats`

- **按 `job_id` 聚合**。
- `job_id IS NULL` 自成一类，`job_name = "未知"`，**绝不并入任何具体职业**，
  也绝不在图表中被隐藏。
- 每行输出与 §10 相同，并附加 `role`。`role` 由 `job_id` 推导，未知职业的 `role = UNKNOWN`。

## 12. 仪表盘 `GetDashboardStats`

一次返回：

| 字段 | 口径 |
|---|---|
| `attempt_count` | §2 |
| `completed_count` | §3 |
| `baseline_completed_count` | `achievement_settings` |
| `achievement_progress` | §4（全量口径，不受时间过滤影响） |
| `goal_count` | `achievement_settings`，默认 2000 |
| `remaining` | §5 |
| `completion_rate` | §6 |
| `leave_rate` | §7 |
| `avg_duration_ms` | §8 |
| `result_breakdown` | §9 |
| `unfinished_pending_review` | 现有正式数据与未删除过滤范围内 `pending_review = 1` 的条数（见下） |
| `trend` | §12.1 |

`unfinished_pending_review` 的口径是 `pending_review` 标志本身，而非
`result = 'INTERRUPTED'`。需要用户复核的记录共有四类，其中第二类是国服的常态：

- 重启后恢复出的未完结记录（`INTERRUPTED` + `LOW`，见
  [state-machine.md](state-machine.md) §3.9）；
- 档案不含 `DUTY_RESULT` 时每一场副本的收尾（`UNKNOWN` + `LOW`，见同一文件 §3.10）；
- 匹配窗口内排到**其他随机任务**而按进本前取消收尾的记录
  （`CANCELLED_BEFORE_ENTRY` + `LOW`，见同一文件 §3.3 第 5 条）。该判断依赖尚无样本核对过的
  `roulette_id` 偏移，因此按 fail-safe 原则交由用户确认，而不是静默改写一场导随。
  此类记录的 `entered_at_utc` 为 `NULL`，因此**不计入 attempt**，也不进入完成率分母，
  只增加一条待复核；
- 纯自动写入时结果为 `COMPLETED`、但进入或结束时刻缺失的记录。此类记录改记为
  `UNKNOWN` + `LOW` 并交付复核，而不是将自相矛盾的行原样写入。

四类记录均由系统置 `pending_review = 1`。用户通过 `CorrectRun` 提交结果，或显式设置
`pending_review=false` 之后，该记录退出计数。仅更正备注、职业或时间的记录仍保留待复核状态，
不因 `manually_corrected = 1` 而被排除。软删除记录依旧不参与统计。

## 12.1 完成趋势 `trend`

`GetDashboardStats` 同时返回一条完成趋势序列。请求可带
`trend_granularity`（`day` / `week` / `month`，默认 `day`）：

| 粒度 | 桶数 | 桶的起点 |
|---|---|---|
| `day` | 30 | 每个 UTC 自然日的 00:00Z，最后一个桶是"今天（UTC）" |
| `week` | 12 | 每个 ISO 周的**周一** 00:00Z |
| `month` | 6 | 每个 UTC 自然月的 1 日 00:00Z |

口径：

- 时间取 **`matched_at_utc`**，而非 `entered_at_utc`。趋势衡量的是一段时间内接到并完成的
  导随数量，因此排队匹配的时刻才是判定记录归属于哪一天的依据。
- 只统计 `result = 'COMPLETED'` 的记录。`matched_at_utc IS NULL` 的记录不进入任何桶。
- §0 的通用前提继续适用：`soft_deleted = 0`，且只含 §1 定义的"确认为导随"。
- 调用方提供的 `RunFilter` 在此基础上叠加，再与最近 N 个桶的窗口取交集。
- **空桶必须显式出现**，其 `completed_count = 0`。序列长度恒等于上表的桶数，不允许压缩。

### 时区：桶在服务端按 UTC 划，标签在客户端按本地时区显示

这是一项**刻意的**设计选择，在此记录是因为它会被反复质疑。

- **桶边界按 UTC 划分。** 同一个采集服务在回答两个不同时区客户端的同一问题时，
  必须给出同一条序列。若按本地时区分桶，序列会随提问方而变化，导出的 CSV 与截图
  也就无法相互印证。
- **标签按本地时区显示。** 每个桶只携带 `start_utc`，桌面端将其转换为本地时间后
  取日期作为标签。客户端**不重新分桶**。
- 该选择的代价如下。一条本地时间 09-04 23:30（UTC+8）匹配到的记录落在 UTC 的 09-04 桶中，
  而该桶的标签在 UTC+8 下显示为 09-04 08:00 所在的那一天，同样是 09-04。
  绝大多数时区与绝大多数时刻两者一致，仅在 UTC 日界附近的一两个小时内可能相差一天。
  这是换取全局一致性的代价，不是缺陷。

在 2026-09-04 之前，桌面端使用 `QueryRuns(page_size = 200)` 自行拉取一页已完成记录，
再按**本地日期**分桶。该做法在记录超过 200 条时会静默截断，趋势图开始少计，
而界面上没有任何提示。服务端聚合已取代该做法（contracts/CHANGELOG.md 第 17 条）。

## 13. 边界用例（必须有测试覆盖）

| 用例 | 期望 |
|---|---|
| 库为空 | 所有计数 0；`completion_rate` / `leave_rate` / `avg_duration_ms` 均为 `null`；`remaining = goal_count` |
| 只有一条 `CANCELLED_BEFORE_ENTRY` | `attempt_count = 0`，率均为 `null`；结果分布中该桶 `count = 1`、`share = null` |
| 一条 `COMPLETED` 但 `contributes_to_goal = 0` | `completed_count = 1`，`achievement_progress` 不增加 |
| 一条 `COMPLETED` 被软删除 | 完全不出现在任何统计中 |
| `baseline = 1500`，新增 3 条 `COMPLETED` | `achievement_progress = 1503`，`remaining = 497` |
| `baseline` 大于 `goal_count` | `remaining = 0`（不为负） |
| `COMPLETED` 但 `duration_ms IS NULL` | 计入 `completed_count`，**不**计入 `avg_duration_ms` |
| 5 条尝试：3 完成 / 1 离开 / 1 掉线 | `completion_rate = 0.6`，`leave_rate = 0.2`，掉线单独成行为 0.2，两者不相加为 0.4 |
| `job_id` 为 NULL 的 4 条 | 职业统计出现 `未知` 一行，`attempt_count = 4` |
| 趋势：库为空 | `day` 返回 30 个桶，每个 `completed_count = 0`；`week` 12 个；`month` 6 个 |
| 趋势：一条 `COMPLETED` 但 `matched_at_utc IS NULL` | 不进任何桶，但仍计入 `completed_count` |
| 趋势：一条 `COMPLETED` 被软删除 | 不进任何桶 |
| 趋势：一条 31 天前的 `COMPLETED` | 不在 `day` 序列里，但在 `month` 序列里 |

## 14. 数值与展示

- 比率以 `[0, 1]` 区间内的浮点数经 IPC 传输，由 UI 负责格式化为百分比，默认保留一位小数。
- 平均时长以毫秒数传输，由 UI 格式化为 `mm:ss` 或 `hh:mm:ss`。
- 计数为整数，永不为负。
- `null` 一律显示为 `—`，并在 tooltip 中说明"样本不足"。
