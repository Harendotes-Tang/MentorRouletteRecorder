# 状态机与结果判定 / State Machine

本文件定义 Collector 判定"一次导随尝试"的**唯一**规则，面向实现者与评审者。
实现必须与本文件一致，测试（`tools/fixture-replay` 与 `tests/Collector.UnitTests`）以本文件为准。

## 0. 前置条件：fail-closed

在进入任何状态迁移之前：

- 协议档案状态必须为 `VERIFIED`。若为 `NONE` / `UNVERIFIED` / `UNSUPPORTED_BUILD`，
  状态机**不启动**，不解析任何报文，不写入任何记录，`StartCapture` 返回
  `ERR_PROFILE_UNSUPPORTED`。本机校准写出的 VERIFIED 档案（protocol-profile-format.md §11）
  与随包档案同等对待。校准期间的抓包按"无档案"运行，状态机同样不启动；用户确认后
  在同一会话内绑定一次，且只绑定这一次，绑定对象是磁盘上重新加载的档案。
- 所有触发迁移的事件必须是**已验证事件（verified event）**，
  即由 `VERIFIED` 档案中显式声明的 opcode 与结构解析得出，且必需字段解析成功。
  部分解析、猜测与启发式推断**均不构成**已验证事件。

## 1. 状态集合

| 状态 | 含义 | 是否终态 |
|---|---|---|
| `IDLE` | 无进行中的导随 | 否 |
| `MENTOR_MATCHED` | 已匹配到导随，尚未进入副本 | 否 |
| `ENTERED_DUTY` | 已进入副本 | 否 |
| `COMPLETED` | 副本完成 | 是 |
| `CANCELLED_BEFORE_ENTRY` | 匹配后未进入即取消 | 是 |
| `LEFT_OR_ABANDONED` | 进入后离开/放弃 | 是 |
| `DISCONNECTED` | 进入后掉线 | 是 |
| `INTERRUPTED` | 进入后被中断（非玩家主动离开，也非掉线） | 是 |
| `UNKNOWN_FINAL_STATE` | 无法归类的结束 | 是 |
| `INTERRUPTED_PENDING_REVIEW` | 程序重启时发现的未完结记录，待人工确认 | 是（待复核） |

终态映射到 `mentor_runs.result`：

| 终态 | `result` |
|---|---|
| `COMPLETED` | `COMPLETED` |
| `CANCELLED_BEFORE_ENTRY` | `CANCELLED_BEFORE_ENTRY` |
| `LEFT_OR_ABANDONED` | `LEFT_OR_ABANDONED` |
| `DISCONNECTED` | `DISCONNECTED` |
| `INTERRUPTED` | `INTERRUPTED` |
| `UNKNOWN_FINAL_STATE` | `UNKNOWN` |
| `INTERRUPTED_PENDING_REVIEW` | `INTERRUPTED` |

## 2. 状态图

```
                          ┌──────────────────────────────────────────────┐
                          │                    IDLE                      │
                          └───────────────────┬──────────────────────────┘
                                              │
              verified CONTENT_FINDER_POP     │
              且 roulette_id == mentor_roulette_id
                                              ▼
                          ┌──────────────────────────────────────────────┐
             ┌────────────│               MENTOR_MATCHED                 │
             │            └───────────────────┬──────────────────────────┘
             │                                │
    取消 / 超时 / 未进入                        │  verified ZONE_INITIALIZATION
             │                                ▼
             │            ┌──────────────────────────────────────────────┐
             │            │                ENTERED_DUTY                  │
             │            └──┬────────────┬──────────────┬───────────┬───┘
             │               │            │              │           │
             │   verified    │   离开副本  │   连接中断    │  其他中断  │
             │  DUTY_RESULT  │            │              │           │
             │   (victory)   │            │              │           │
             ▼               ▼            ▼              ▼           ▼
  CANCELLED_BEFORE_ENTRY  COMPLETED  LEFT_OR_ABANDONED DISCONNECTED INTERRUPTED
                                              │
                                              └──► 任意无法归类 ──► UNKNOWN_FINAL_STATE

  进程重启时发现 capture_sessions.ended_at_utc IS NULL 且存在未完结记录
                          ──► INTERRUPTED_PENDING_REVIEW（绝不自动判为 COMPLETED）
```

## 3. 迁移规则（规范）

### 3.1 `IDLE` → `MENTOR_MATCHED`

**唯一触发条件**：收到一条来自服务器的 **verified** `CONTENT_FINDER_POP` 事件，且其
`roulette_id` 等于当前协议档案声明的 `mentor_roulette_id`。

- `roulette_id` 不等于导随 id：忽略该事件，保持 `IDLE`。该弹窗属于其他随机任务。
- `roulette_id` 解析失败：**忽略**该事件，保持 `IDLE`。不猜测，不记录。
- 迁移时创建 `mentor_runs` 行，其中 `matched_at_utc` 取事件时间，`result` 暂记为
  `UNKNOWN`，`revision = 1`，`source = AUTO_NETWORK`，`change_kind = CREATE_AUTO`。
  同时启动该记录的单调计时器。

**本机校准的排队推断模式**：当档案以客户端排本请求代替匹配通知
（`MatchFromQueue = true`）时，请求只暂存在内存中，状态保持 `IDLE`，没有当前场次，
也不写入历史记录。随后在档案窗口内收到进本标记，且区域为副本表中的已知副本时，
在同一事务中创建记录并进入 `ENTERED_DUTY`，不对外发布 `MENTOR_MATCHED`。
`matched_at_utc` 沿用排队申请时间，`entered_at_utc` 使用实际进本标记时间，
耗时仍从进本开始计算。

普通传送不创建记录，也不能证明取消，因此保留暂存请求。新的排本请求替换旧请求。
其他轮盘、明确取消、连接丢失、抓包停止、丢包或窗口过期会清除暂存请求。
暂存状态参与事务失败回滚，但不跨抓包会话或程序重启恢复。若取消报文尚未识别，
取消后的普通换区不会留下“未知副本”；取消后在窗口内自行进入已知副本的歧义仍然存在，
消除该歧义需要识别服务器匹配通知或取消报文。

### 3.2 `MENTOR_MATCHED` → `ENTERED_DUTY`

**唯一触发条件**：收到一条 **verified** `ZONE_INITIALIZATION` 事件，且其
`territory_id` / `content_id` 与本次匹配一致（或档案未提供交叉校验字段时，
在匹配后的合理时间窗内首次出现）。

- 设置 `entered_at_utc`，记录 `content_id` / `territory_id` / `job_id`。
  进本标记本身不带区域和副本编号时，改用最近一次区域播报补齐，见 §3.11。
- **`entered_at_utc` 非空是该记录计入 `attempt_count` 的必要条件**
  （见 [statistics-definitions.md](statistics-definitions.md)）。
- 档案不提供 `is_duty_instance` 时，事件中该标志为 null。此时匹配窗口内的第一次
  `ZONE_INITIALIZATION` 视为进入副本；窗口过后才出现的换区按 §3.3 第 4 条处理。

**已知歧义（不猜测、不引入启发式）**：档案没有 `is_duty_instance` 时，窗口内的换区
可能并非进入副本，例如玩家在排队等待期间传送至其他城镇。判定前提已收紧至极限：
必须先有一条 verified 的 `CONTENT_FINDER_POP`（国服档案要求
`finder_state == 3`，即"确认进入"的弹窗本身），窗口从**最近**一次这样的弹窗算起
（见 §3.3 末尾"不触发本转移的一种情况"），且只有窗口内的**第一次**换区才算数。
在此之上进一步区分"进入副本"与"传送"，只能依赖时长、区域号猜测或聊天文本，
这些做法均为 §5 明令拒绝的手段。因此本项目**保留这一歧义**。
误判的后果是多出一条 `entered_at_utc` 非空、随后按 §3.10 收尾为
`UNKNOWN` 待复核的记录，用户可在"待复核"列表中删除或更正。该代价可控，而猜测的代价不可控。
识别出 `is_duty_instance` 或 `DUTY_RESULT` 之后，这一歧义自动消失。

### 3.3 `MENTOR_MATCHED` → `CANCELLED_BEFORE_ENTRY`

触发条件（任一）：

1. 收到 verified 的"匹配取消/放弃"事件。
2. 在档案声明的匹配有效期（默认 45 秒，可由档案覆盖）内未进入副本，且随后
   观察到新的 `CONTENT_FINDER_POP`，或观察到明确的回到 `IDLE` 的信号。
3. 用户显式停止抓包，或进程正常退出时该记录仍处于 `MENTOR_MATCHED`。
4. 档案不能区分副本区域（`is_duty_instance` 为 null）时，匹配窗口过后出现的任何
   `ZONE_INITIALIZATION`。此时匹配已失效，玩家前往了其他区域（`detection_confidence = LOW`）。
5. 窗口**之内**收到一条 verified 的、`roulette_id` **不是**导随的
   `CONTENT_FINDER_POP`（`detection_confidence = LOW` 且 `pending_review = 1`）。
   任务搜索器一次只会给出一个任务，因此该弹窗是"本次导随匹配已经结束"的直接证据，
   即玩家拒绝了导随并改排其他任务。此前这类弹窗被忽略，紧随其后的换区因而被记为一场导随的进本，
   出本后收尾为"未知 · 待复核"，并计入了 `attempt_count`。
   **判定为 LOW 加待复核而非 MEDIUM 的理由。** "该弹窗属于其他随机任务"这一判断，
   完全依赖于按档案声明的偏移解出的 `roulette_id`；而导随本身的真实样本至今未在国服客户端上采集到，
   该偏移没有经过任何核对。在取得样本之前，本转移按 fail-safe 处理，以应对同一条弹窗的重发，
   以及该字段在其他 `finder_state` 下含义不同的可能。其代价是用户需多确认一条记录，
   而不是将一场真实的导随静默改写为"进本前取消"（评审 R-7）。这类记录因此计入
   [statistics-definitions.md](statistics-definitions.md) §12 的 `unfinished_pending_review`。

- 此终态的 `entered_at_utc` 为 `NULL`，因此**不计入 attempt**，也不计入完成率分母。

**不触发本转移的一种情况：窗口从最近一次导随弹窗算起。** 仍处于 `MENTOR_MATCHED`
时又收到一条**窗口之内**的导随 `CONTENT_FINDER_POP`，例如有人拒绝、队伍重组而再次弹出。
此时既**不**收尾也**不**开启新记录，而是把匹配锚点刷新到这条新弹窗，并在
`run_events` 中补一条 `MENTOR_MATCHED → MENTOR_MATCHED` 的同状态事件，
以便审计时可以看出本次进入为何被算作有效。若不刷新锚点，"t=0 弹窗、t=97s 重弹、t=130s 进入"
将因超出从第一次弹窗算起的窗口而被判为本节的 `CANCELLED_BEFORE_ENTRY`，整场副本随之丢失。
超过窗口才到达的新弹窗不在此列，仍按上述第 2 条收尾旧记录并开启新记录。

### 3.4 `ENTERED_DUTY` → `COMPLETED`

**唯一触发条件**：收到一条 **verified** `DUTY_RESULT` 事件，且其结果字段表示
**胜利 / 完成（victory）**。

- 设置 `ended_at_utc`、`duration_ms`（取自单调时钟），`result = COMPLETED`。
- **不存在**任何其他进入 `COMPLETED` 的路径。超时、推测与形似完成的迹象均不成立。

### 3.5 `ENTERED_DUTY` → `LEFT_OR_ABANDONED`

触发条件：观察到本方角色离开副本区域（verified 的离开/退出副本事件，
或 `ZONE_INITIALIZATION` 切换到非副本区域），且此前**没有**收到 victory 的 `DUTY_RESULT`。

该条件亦涵盖副本因队伍解散而结束的情形，在没有更精确证据时归入此项。

档案不能区分副本区域时，进入副本之后的**任何** `ZONE_INITIALIZATION` 都视为离开。
若档案同时没有 `DUTY_RESULT`，则按 §3.10 收尾为 `UNKNOWN`，而不是 `LEFT_OR_ABANDONED`。

### 3.6 `ENTERED_DUTY` → `DISCONNECTED`

触发条件：TCP 连接被观察到中断，且未收到 victory 的 `DUTY_RESULT`。

**该终态的产生路径是明确的**，由以下环节构成：

1. `FirstPacketBuffer` 只对**已经取得解码器**的那条流报告结束，条件是重组时消费到 FIN/RST，
   或该四元组从操作系统的连接表中消失。报告发生在重组锁释放之后。
2. `FirstPacketMonitor` 只在该连接是**最后一条**仍在投递解码消息的连接时才上报。
   投递过消息的连接记录在一个有界集合中，最多 256 条。仅以"投递过消息"作为过滤条件远远不够：
   国服客户端同时保持大厅、区域、聊天三条被解码的连接（见
   [`../protocol-profiles/oodle-signatures/README.md`](../protocol-profiles/oodle-signatures/README.md)），
   聊天服务器按自身节奏掉线重连；若将其报为"连接中断"，会使一场区域连接完好的正常
   通关被永久记为掉线（评审 R-2）。整个游戏真正停止通信，只有"一条连接都不剩"这一种读法，
   也只有该读法与本节标题中"且游戏进程仍存在"的条件相符。
3. `MachinaCaptureSource` 记录一条 `capture/game_connection_closed` 并通知观察者；
   `CaptureController` 在解码线程之外将其转为 `ICaptureLifecycleListener.OnConnectionLost`；
   `LiveProtocolPipeline.OnConnectionLost` 经 `ApplyAndPublish` 落库。
4. 转发之前先确认**游戏进程仍然存活**。正在关闭的客户端会先发送 FIN，连接也先从系统连接表
   中消失，早于 1 秒一轮的跟随轮询发现进程退出。缺少该确认时，在副本中关闭游戏会被记为
   掉线而非 `INTERRUPTED`。进程列表读取失败时一律判定为进程存活，因此一次诊断失败不可能
   凭空产生一个终态；该情况记录一条 `capture/connection_lost_game_exited`。
5. **顺序**：连接结束不绕过解码队列直达状态机，而是作为一枚标记排入**同一条**有界队列，
   由解析线程取到时才回调。数据库繁忙时，仍滞留在队列中的 `DUTY_RESULT` 因而会先被解析，
   不会出现先按掉线收尾、随后到达的通关结果撞上终态而被忽略的情况（评审 R-4）。队列已在关闭、
   标记无法排入时静默丢弃，且**不**计入丢失观测。

因此，进入副本之后断网或断流会收尾为 `DISCONNECTED`，而不是等到关闭游戏才变为
`INTERRUPTED`，也不会在重新登录后被当作换区收尾为 `UNKNOWN` 待复核。在副本中关闭游戏
仍按 §3.7 收尾为 `INTERRUPTED`。

**注意**：`DISCONNECTED` 与 `LEFT_OR_ABANDONED` 在统计中**永远分开展示，绝不合并**。

### 3.7 `ENTERED_DUTY` → `INTERRUPTED`

触发条件：

1. 游戏进程退出（崩溃或用户关闭客户端）；
2. 抓包会话被用户停止，或 Collector 正常退出，而记录仍处于 `ENTERED_DUTY`；
3. 有界队列溢出导致事件序列出现明确空洞，无法继续可靠判定。

此时置 `detection_confidence = LOW`。

### 3.8 → `UNKNOWN_FINAL_STATE`

适用于上述任何条件均不满足、但必须收尾的情况，例如档案在会话中途变为不可用。
此时 `result = UNKNOWN`，`detection_confidence = NONE`。

### 3.9 进程重启：`INTERRUPTED_PENDING_REVIEW`

Collector 启动时：

1. 找出**仍未收尾**（`mentor_runs.ended_at_utc IS NULL`）、`result = UNKNOWN`、
   且属于**其他**抓包会话（`capture_session_id` 非空，且不是本进程刚打开的那个）的记录。
   判据是记录仍处于未收尾状态，而非会话仍处于打开状态：会话行可能已关闭而记录仍未收尾。
   离线重放工具即属此类；进程在关闭会话之后、收尾副本之前终止同样属此类。
   `ended_at_utc` 非空的记录已由状态机正常收尾，**一律不作处理**。§3.10 所述的档案
   （国服现状）本就把每一场都收尾为 `UNKNOWN` 加待复核，若仅按 `result` 挑选，
   将在下一次启动时把每一条正常完成的记录改写为 `INTERRUPTED`。
   `capture_session_id` 为空的是手工创建的记录，同样不作处理。
2. 将它们置为 `result = INTERRUPTED`、`detection_confidence = LOW`，
   并在 `run_events` 追加一条 `PROCESS_RESTART` 事件，`to_state =
   INTERRUPTED_PENDING_REVIEW`；
3. 将所有仍处于打开状态的会话（本进程的会话除外）的 `ended_at_utc` 补为发现时间，
   `end_reason = UNKNOWN`；
4. 通过 `GetDashboardStats.unfinished_pending_review` 与实时事件提示用户复核。

恢复过程沿用 [人工更正字段保护](manual-correction.md)：人工修改过的字段予以保留，其余字段继续收尾，
修订只记录实际变化。人工时间与发现时间冲突时，保留合法时间及未知待复核结果。
即使保护后记录仍为 `UNKNOWN` 且结束时间为空，既有的 `PROCESS_RESTART` 事件也会阻止重复恢复。

> **绝对规则：重启后的未完结记录永远不会被自动判定为 `COMPLETED`。**
> 只有用户通过 `CorrectRun`（携带 `reason`）才能将其改为 `COMPLETED`，
> 且该记录会被标记 `manually_corrected = 1`。

### 3.10 档案没有 `DUTY_RESULT`：`UNKNOWN` + 待复核

`DUTY_RESULT` 自 2026-09-07 起不再是档案的必需消息。缺少该消息的档案能够观察到副本结束，
却观察不到结果。因此 `ENTERED_DUTY` 之后的离开（§3.5 的换区，或新的弹窗）收尾为
`UNKNOWN_FINAL_STATE`，取 `result = UNKNOWN`、`detection_confidence = LOW`、
`mentor_runs.pending_review = 1`。此类记录出现在“待复核”列表中，由用户通过 `CorrectRun`
填入 `COMPLETED` 或 `LEFT_OR_ABANDONED`。该路径**从不**产生 `COMPLETED`，§3.4 的唯一性不变。

国服 `cn.2026.08.05` 即为此类档案。候选模式的副本内采样（`DUTY_WINDOW_SAMPLE`）
用于定位通关报文；识别之后在档案中补入 `DUTY_RESULT`，该路径随之不再出现。

### 3.11 副本名与职业：两项**不参与判定**的记忆

状态机额外记住两项信息。两者都不是状态与结果的判定输入，只负责把记录填写完整。
两者的共同点是：**观察到它们的时刻，往往尚不存在可供写入的记录。**

**职业（`PLAYER_JOB` → `PlayerJob`）**
职业信息在登录时播报一次，之后只在换职业与升级时再次播报。因此在绝大多数导随记录中，
唯一一次职业观察发生在 `IDLE` 状态，此时尚无记录。状态机因而在**每一个状态**（含 `IDLE`）
都记下最近一次职业，并在 §3.1 创建记录时立即写入（`CreateRunCommand` 之后紧跟
`SetJobCommand`）。副本内再次观察到换职业时按原规则覆盖。

**职业未发生变化的播报被忽略。** 国服档案把 `PLAYER_JOB` 绑定在一条客户端每次获得经验都会发送的
状态报文上，因此一场副本中同一职业会被播报数十至上百次。观察值与记录当前职业**相同**时，
该事件标记为 `Ignored`：不写入数据库，不追加 `run_events`，不发送实时事件。记忆本身照常更新，
因此下一条记录仍会写入职业；真正的换职业不受影响。

**副本（`ZONE_TERRITORY` → `TerritoryObserved`）**
国服将区域编号放在**另一条**报文中，该报文比本项目用作进本标记的 `0x014a` 早约 50 毫秒，
而 `0x014a` 本身没有任何可读字段。规则如下：

1. 状态机在每一个状态记下最近一次区域播报，连同其单调读数。
2. §3.2 接受进本、且本次进本**既无 `territory_id` 也无 `content_id`** 时，
   取**最近 10 秒内**的那条播报作为本次记录的区域。该窗口是 50 毫秒的两百倍，
   但仍远小于两次换区的间隔。
3. 已经进本、且记录**尚无区域**时又收到播报，只有**落在进本前后 10 秒之内**的那一条
   才被采纳。该界限是必需的：若进本时的那条播报恰好丢失，在没有界限的规则下，
   出本前 50 毫秒的"目的地"播报会被当作本场副本写入记录。记录一旦有了区域，后续播报一律忽略。
4. 带 `territory_id` 的进本标记始终更可信，存在该字段时不借用播报。

宿主（`SemanticEventProcessor`）取得 `SetDutyCommand` 后，用
`DutyCatalog.FindByTerritory` 将区域编号转换为副本名。唯一命中时取其**名称与分类**；
同一区域上存在多个副本时取 `content_id` 最小的一条；本地映射表中没有对应项时只保留区域编号。
查表只在**记录所属区服自身的**映射中进行，不跨区服回退。因此国服记录不会因国服映射表滞后
而取得国际服的英文副本名。`Region.Unknown` 表示"没有区服"，而非某个具体区服，手工记录仍查询全部映射。

**反查得到的 `content_id` 不写入记录。** `ZONE_TERRITORY` 的字段偏移尚未在副本内负载上
核实，而 `content_id` 是副本统计的聚合列。一个错误的偏移会造成难以察觉的统计污染。
因此自动写入只落 `territory_id` 与显示用的名称和分类，并在
`mentor_runs.duty_source` 上标注来源（`CONTENT_ID` / `TERRITORY` / `MANUAL`，见
[data-model.md](data-model.md) §1.4）。统计相应改为按 `content_id ?? territory_id` 聚合。

**该步骤是本地展示映射，而非协议证据**，因此它只填补空值，从不覆盖已有值，也从不提高 §4 的置信度。
人工更正过的字段及其关联显示值保持不变，其余字段仍可补齐；
`manually_corrected` 仅表示历史来源，不冻结整行（见 [manual-correction.md](manual-correction.md) §7）。

**只有职业跨会话传递，区域不传递。** 每个抓包会话都会新建一个状态机。抓包故障自动重试
之后，新状态机若从"职业未知"重新开始，重试后的第一条记录将一直到玩家换职业为止都没有
职业。为此，`LiveProtocolPipeline.OnCaptureStarted` 将上一个状态机的
`Memory`（`StateMachineMemory(LastKnownJobId)`）通过 `Seed()` 传给新状态机。
已在跟踪一条记录的状态机**拒绝**被 seed：记忆是尚无记录时观察到的信息，
不能用于改写一条正在进行的记录。

**区域记忆刻意不跨会话传递**（评审 R-6）。上述第 2、3 条中的 10 秒均以抓包源的单调计时器计量，
而每个会话都会新建抓包源，计时从零开始。将上一段会话的区域连同其旧读数一并传递，
等同于对两个不同的时钟求差：会话 1 在 3 秒处观察到的城内区域，
在会话 2 的 8 秒处会被算作"5 秒前刚播报过"，从而被当作本次进入的副本写入记录，
`duty_source` 还会标为 `TERRITORY`。

### 3.12 按出现时机认出的「匹配成功」报文：`MATCH_ANNOUNCED`

按排本申请推断匹配的档案（`CONTENT_FINDER_POP` 方向为 CLIENT_TO_SERVER）在进本之前
拿不出任何可播报的时刻：它知道玩家排了本，不知道匹配是什么时候来的。国服 2026.09.15
客户端的匹配通知在任何字节位置都不带轮盘编号，只能按出现时机认出，档案里记为可选消息
`MATCH_ANNOUNCED`（[protocol-profile-format.md](protocol-profile-format.md) §11.5）。
本节只适用于同时带有这两者的档案。

| 当前状态 | 事件 | 结果 |
|---|---|---|
| `IDLE`，持有未过期的指导者排本申请 | `MATCH_ANNOUNCED` | 开启记录进入 `MENTOR_MATCHED`。匹配时刻取通知的时刻，轮盘取申请；待定申请清空 |
| `IDLE`，没有指导者申请 | `MATCH_ANNOUNCED` | **静默忽略**。可能是别的轮盘，也可能玩家是队员：没有申请就不知道排的是什么。不计解析错误 |
| `MENTOR_MATCHED`（由通知开启） | `MATCH_ANNOUNCED` | `RefreshMatch`：有人拒绝后重新匹配，或一次匹配连发数条（该客户端连发 3–4 条），都是同一条记录，窗口随之前移。同一条记录最多刷新 16 次（`MaxAnnouncedRefreshes`），此后忽略，避免认错的多话报文无限写入事件行 |
| `MENTOR_MATCHED`（由通知开启） | `CONTENT_FINDER_POP`（即新的排本申请） | 上一条按「进本前取消」收尾（MEDIUM，不待复核）；新申请若为指导者轮盘则成为新的待定申请 |
| `MENTOR_MATCHED`（由通知开启） | 进入已知副本 | 与 §3.2 相同，但窗口用下面的 `AnnouncedWindow` |

**进本窗口。** 由通知开启的匹配使用 `StateMachineOptions.AnnouncedWindow`（默认 120 秒），
而不是排队用的 `MatchWindow`。按排本推断的档案把自己的匹配窗口写成了格式上限 3600 秒，
因为它计量的是排队；通知一到，排队就结束了，剩下的只有玩家确认与读条。仍用 3600 秒会让
一次被拒绝的匹配一直挂到玩家下一次换区为止。这个数字不由档案携带：它就是各随包模板声明的
那两分钟，而推断档案的同名字段已被排队上限覆盖，文件里没有别的地方可读。

**播报。** 由通知开启的那一次 `MENTOR_MATCHED` 状态事件发 `match_from_queue = false`，
桌面端现有逻辑据此播报「匹配成功」；同一台机器上其余状态（含随后的 `ENTERED_DUTY`）
仍为 `true`，因为它们确实仍是推断。该标志属于这一次迁移，不属于档案。

**只增不减。** 通知漏抓（丢包、客户端没发）时，`IDLE` → `ENTERED_DUTY` 一步完成的原路径
不变（§3.1），记录内容完全相同，只是少了进本之前的那次播报。

通知来得过早或根本认错时同样如此。通知只决定匹配的时刻，玩家排了指导者任务这件事仍由排本申请证明，
而申请有它自己的窗口（`MatchWindow`，推断档案为 3600 秒）：

- `AnnouncedWindow` 过后进入**已知副本**，只要仍在申请的窗口内，照常进入 `ENTERED_DUTY`；
- `AnnouncedWindow` 过后发生非副本换区，该记录按「进本前取消」（LOW）收尾，但申请若未过期则**恢复为待定申请**，
  之后的进本仍会生成记录。

因此一条认错的通知最多造成一次过早的播报和一条「进本前取消」，不会吞掉没有它时本可生成的记录。

### 3.13 终态的归一：收尾之后立刻回到 `IDLE`

终态描述的是一条记录的结局，而非状态机的停留位置。收尾之后状态机立即归一到 `IDLE`
（`NormalizeIfTerminal()`，幂等），`GetCurrentRun` 也会在返回之前调用它。

**读路径上的归一同样发送事件。** `GetCurrentRun` 在 `ApplyAndPublish` 中执行
`NormalizeIfTerminal`，因此从终态回落到 `IDLE` 会发出**恰好一次**
`run_state_changed(IDLE)`；第二次轮询看到的已是 `IDLE`，不再发送事件。此前该状态变更是
静默的，只订阅事件而不轮询的客户端无法观察到回到空闲（评审 R-9）。存储侧已处于闩死状态
时，读取不会因此抛出异常，而是退回静默归一，故障仍从抓包状态中报出。

因此，**终态不能通过 `GetCurrentRun` 观察到**。副本结束后在主城停留时，`GetCurrentRun`
报告的是空闲，而不是一小时前的那条 `UNKNOWN_FINAL_STATE`。查询结局应读取已存储的行
（`QueryRuns`），或订阅 `run_finished` 事件。该事件本就由行的 `ended_at_utc` 计算得出，
而非取自状态机的当前状态。

## 4. 置信度 `detection_confidence`

| 值 | 条件 |
|---|---|
| `HIGH` | 匹配、进入、结束三个关键事件全部为 verified，且 `content_id` 与 `job_id` 均解析成功 |
| `MEDIUM` | 关键事件齐全但存在字段缺失（如 `content_id` 为 NULL） |
| `LOW` | 关键事件缺失（重启恢复、队列溢出、连接异常收尾） |
| `NONE` | 手工创建的记录，或 `UNKNOWN_FINAL_STATE` |

## 5. 不允许的行为（反例）

以下写法在评审中一律拒绝：

- 以"进入副本后超过 N 分钟"推断完成。
- 以聊天或系统消息的文本匹配判定结果。
- 在 `roulette_id` 未知时假设其为导随。
- 在档案未验证时先行记录、事后修正。
- 将 `DISCONNECTED` / `INTERRUPTED` / `UNKNOWN` 并入 `LEFT_OR_ABANDONED` 以简化离开率。
- 重启后按上次心跳时间补写一条 `COMPLETED`。
- 将 §3.10 的 `UNKNOWN` 待复核记录按时长、离开方式或任何启发式自动改为 `COMPLETED`。

## 6. 可测试性

状态机是**单线程、纯函数式**的：输入为有序的 `DomainEvent` 序列，输出为状态迁移与
待持久化的变更。它不直接访问数据库，也不访问时钟；时间由事件携带，时长由注入的单调
时钟接口提供。因此 `tools/fixture-replay` 可以用离线固件确定性地重放全部迁移，
无需游戏、无需 Npcap、无需真实 opcode。

两种重放模式从不同层次切入同一条链路：`--replay` 直接输入语义事件，只测试判定规则；
`--replay-decoded` 输入解码报文的字节，一并测试解析规则。
`--replay-decoded` 之后的全部环节与活体抓包使用同一份生产代码，
因此固件通过即构成活体行为一致的证据。见
[`../tools/fixture-replay/README.md`](../tools/fixture-replay/README.md)。

## 7. 事件的来源：解析器与管线

上述每一条规则的输入都是**已验证事件**。本节说明该事件的产生方式，
以及在哪些情形下它**不会**被产生。

### 7.1 链路

```
Machina.FFXIV (抓包回调线程)
      │  DecodedMessage —— 抓包与解析之间唯一的交接类型
      │  {capture_session_id, direction, observed_at_utc, mono, epoch,
      │   segment_type, opcode, payload(IPC 头之后), connection_key}
      ▼
DecodedMessageQueue —— 有界队列（默认 4096，最小 512，最大 65536）
      │  满则丢**最旧**并计数；绝不阻塞抓包回调
      │  恰好一个消费者线程按观察顺序取出
      ▼
LiveProtocolPipeline.Accept  —— 会话守卫 + 档案选择的持有者
      ▼
ProfileMessageParser —— 档案驱动，零硬编码常量
      │  SemanticEvent（ContentFinderPop / ZoneInitialization / DutyResult /
      │                 PlayerJob / TerritoryObserved / ZoneLeft / InstanceLeft /
      │                 MatchCancelled）
      ▼
SemanticEventProcessor —— 唯一一条"事件 → 数据库行"的路径
      │  ├─► MentorRunStateMachine （本文件第 3 节的规则）
      │  └─► SQLite （mentor_runs / run_events / run_revisions / parser_errors）
      ▼
LiveEventBus —— 每订阅者 256 条有界缓冲
      ▼
命名管道 → MentorRecorder.Desktop.exe
```

**墙钟只有一个来源。** `DecodedMessage.ObservedAtUtc` 取自注入的 `IClock`，生产环境即本机
时钟；服务器 bundle 的 epoch 原样保留在 `DecodedMessage.Epoch` 中，**仅作诊断字段**。
早先的实现中，报文事件使用服务器时间，生命周期事件与崩溃恢复使用本机时间。本机时钟慢于服务器时，
一次正常的收尾会出现 `ended < entered`，并被合并规则改写为"未知 · 永远未完结"
（见 [manual-correction.md](manual-correction.md) §7）。`duration_ms` 始终取自单调时钟，
不受该问题影响。

### 7.2 解析器的四项检查：任一失败即整条报文作废

`ProfileMessageParser.Parse` 按顺序：

1. **检查档案是否可用**：`ProfileBinding.IsUsable`，即 `VERIFIED` 的活体档案或显式的合成档案，
   且 `mentor_roulette_id` 非 `null`。否则每条报文都记 `E_PROFILE_UNSUPPORTED`。
2. **认领**：查找一条 `(opcode, direction)` 相同的档案消息；档案若声明了 `segment_type`，
   还必须相等。**找不到不构成失败**。档案只声明记录所需的少数几条消息，而客户端每分钟要发送数百个
   其他 opcode；将其计为失败会使诊断页显示"成功率 0.0%、失败 4298"，从而掩盖真正需要关注的
   长度、偏移与约束违规。因此认领失败走 `Ignore()`，单独计入"与记录无关"，
   既不进入错误环，也不写入 `parser_errors`。
3. **长度**：报文体长度必须满足档案声明的 `expected_length` 或 `min_length`/`max_length`，
   否则记 `E_LEN_MISMATCH`。
4. **逐字段**：`offset + 大小` 必须落在**实际**报文体内。加载时已按声明长度检查过一遍，
   此处是第二遍检查，不满足时记 `E_OFFSET_OOB`。读出的值必须满足 `constraints`，
   否则记 `E_FIELD_CONSTRAINT`。`bytes` 字段只做越界检查，内容不读入任何值。
   **例外**：档案将该字段声明为 `role: "selector"`（筛选器）时，约束不命中同样走
   `Ignore()`，因为该报文描述的是其他事件，而非档案错误。国服档案的
   `CONTENT_FINDER_POP.finder_state` 即如此声明，因此一次完全正常的导随不再显示
   "失败 2"。见 [protocol-profile-format.md](protocol-profile-format.md)。

失败**只计数**，绝不记录报文内容。最近 100 条保留在有界环中。
解析器**永不抛出异常**：任何意外都转换为 `E_INTERNAL` 计数，因为抓包线程不能承受异常。

**被拒绝的报文不会到达状态机**：不产生事件，不创建记录，不改变状态。
`tests/Fixtures/decoded/synthetic_len_mismatch` 与 `synthetic_offset_oob` 是该规则的
可执行形式：记录分别停在 `MENTOR_MATCHED` 与 `ENTERED_DUTY`，而不是被收尾为某个终态。

### 7.3 去重的两个层次

解析器用一个 512 条的有界集合统计 `duplicates`，但**仍将重复事件向下传递**。
真正的去重规则只有两条，且都位于下游：

- 状态机的规则（第 3 节：重复的弹出、进入与胜利不会产生第二条记录或第二次完成）；
- `run_events.event_key` 上的 UNIQUE 索引，落库时执行 `DO NOTHING`。

观察本身的身份由 `capture_session_id | direction | opcode | epoch | payload_hash |
semantic_key` 拼接而成，状态机内存中的那层去重使用的即是该键。将计数留在解析器、将策略留在
下游，是为了避免出现两套互相竞争的去重规则。

**落库时该键需再按记录限定一次**，写成
`<上述键> | run:<run_id>`。原因是 `run_events.event_key` 上的 UNIQUE 索引是**全局**索引，
而同一次观察合法地属于两条记录：按 §3.4 收尾上一条记录的那次弹出，同时也是开启
下一条记录的那次弹出。若不加限定，第二行会被索引静默丢弃，新记录的事件轨迹自始为空。
按记录限定之后，索引应有的约束仍然有效：同一条记录中的同一次观察仍只能
有一行，重放两次或重启后再次执行都不会写入第二遍；同时两条记录各自保留属于自己的那一份。

`run:` 后缀**附加在键尾**而非键首，因为 `Ipc.EventIdentity` 仍从键的前几段读取方向、opcode
与载荷摘要；附加在尾部可使其无须感知该后缀。

### 7.4 档案选择在一次会话内是冻结的

`LiveProtocolPipeline.OnCaptureStarted` 时按当时的
`GameProcessDetection`（`region` + `game_build`）定下档案，此后
`Refresh` 在 `_active` 期间**不再改动该选择**。
客户端更新或档案目录被修改，都不会使一次进行中的记录在中途改用另一套结构重新解释。

无法选出可用档案时，管线退化为一个**只计数的沉降端**：不创建状态机，不创建解析器，
不写入任何记录，只让 `CaptureStatus` 与诊断如实反映档案不可用。这就是第 0 节的
fail-closed 在活体路径上的具体形态。

### 7.5 丢包与写库失败

- 队列溢出触发 `OnEventsDropped`，按 3.7 第 3 条将当前记录降级为 `INTERRUPTED`
  （`detection_confidence = LOW`），并累计 `CaptureStatus.packets_dropped`。
  **上报发生在消息丢失的时刻**，由队列的 `onDropped` 回调在抓包回调线程之外派发，
  而不是等到停止抓包时一次性结算。否则副本进行中丢失出本那条报文时，状态机会毫无察觉地
  停在 `ENTERED_DUTY`，数小时后被下一次弹窗收尾为时长虚增数小时的 `UNKNOWN`。
- 收尾超时而**放弃**的积压单独计数（`AbandonedCount`），**不**报为 `EVENT_SEQUENCE_GAP`。
  关机时放弃积压与副本进行中丢失消息是两类不同的情况，前者不应将正常收尾的记录改写为中断。
  `CaptureStatus.packets_dropped` 仍为两者之和（`LostCount`），诊断日志的
  `capture/session_closed` 中 `dropped` 与 `abandoned` 分别记录。
- 可重试的写库失败（`ERR_DB_BUSY` / SQLITE_BUSY / SQLITE_LOCKED）先原地重试
  最多 3 次，退避间隔为 20ms 与 40ms；每次重试前状态机都会回滚到该事件之前的检查点。
  丢失一条事件的后果严重：丢失的若是弹窗，整条记录将不存在。
- 重试之后仍然失败时，才会被**闩住**（`LastStorageError`，含错误码与消息）。
  在抓包源被停止之前，队列中后续的报文不再推进状态机。宁可停在一个已知状态，
  也不在缺失一次观察之后继续判定。
- 生命周期事件（`CAPTURE_STOPPED` / `EVENT_SEQUENCE_GAP` / `CONNECTION_LOST`）的
  单调读数取自**最后一条真实报文**的读数，而非管线自身的会话计时器。时长是两次
  单调读数之差，而抓包源的计时起点早于管线的会话计时器；混用两者会使被中断的副本
  时长偏短，甚至被钳制为 0。仅当会话内没有任何报文时才退回会话计时器，
  此时也不可能有记录处于进行中。
