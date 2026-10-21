# tools/shared-calibration — 共享校准公开仓库的工具

共享校准阶段 D。本文面向公开校准仓库的维护者。
本目录是公开数据仓库 `Harendotes-Tang/MentorRecorder-Calibrations` 中 `tools/`、Issue 表单与工作流的**唯一源头**。
该仓库名与客户端 `SharedCalibrationClient.Owner` / `Repository` 一致。
公开仓库中的副本由 `sync_public_repo.py` 生成，不在公开仓库内手工修改。

> 在本机运行的 `sync_public_repo.py`、`generate_index_sample.py` 与 `publish.py` 只读写本机文件，不创建仓库、不推送，也不调用 GitHub API。
> 推送 `main` 并通过 `gh` 回复、打标签、关闭 Issue 的入口是 `publish_issue.sh`、`sweep_issues.sh` 和 `evidence_workflow.py`；`report_issue.sh` 仅保留旧接口兼容，
> 它们只在公开仓库的 GitHub Action 中运行。建仓与向公开仓库推送属于下文「需要用户确认后才能执行的手工步骤」，须经用户确认后执行。

本目录只使用 Python 标准库。Action 使用 3.12，本机 3.11 亦可运行。

## 文件

| 文件 | 作用 | 复制到公开仓库 |
|---|---|---|
| `sharecode.py` | `MRC1.` 校准码的解码、编码、规范化、`code_sha256` 与载荷规则；逐条移植 `ShareCode.cs` | 是 |
| `rebuild.py` | 按随包模板结构重建校准码并拒绝无效内容；文件开头列明已移植的 C# 规则与留给客户端本机核实的部分 | 是 |
| `index.py` | `index.json`（客户端读取格式的精确移植）与 `submissions.json` 台账；`add_submission`、`revoke`、`lift_hold`、`prune`；两个标记的 `update_marks` / `refresh_marks` 与字段级比对 `disputes` | 是 |
| `issue.py` | 读两个 Issue 表单的正文（提交表单、报告表单）；标题只用来发现不一致；回显内容的转义 | 是 |
| `publish.py` | Action 的命令行：`check`、`update-index`、`push-failed`、`field`、`event-field`、`pending`、`wrap-event`、`report`；维护者命令 `revoke`、`lift`、`prune`、`remark`、`evidence lift`、`evidence compact` | 是 |
| `publish_issue.sh` | 处理一个提交 Issue：实时查询状态与标签 → 查账号 → 校验、提交、推送（推送被拒时从新的 main 重新开始）→ 回复、打标签、关闭；有不一致的校准码时通知对方 Issue | 是 |
| `sweep_issues.sh` | 定时补处理：逐个处理尚未回复的提交 | 是 |
| `report_issue.sh` | 旧接口：处理一个「报告校准有误」Issue（校验栏目、回复一次、打两个标签）。现已没有工作流调用它，报告由 `evidence_workflow.py` 处理 | 是 |
| `evidence.py` | 读取唯一 canonical schema，按账号/会话防重放与三乘二门槛重算可逆隔离、恢复；新区服输出 CANDIDATE 缺口草案；维护者的 `lift` 与 `compact` | 是 |
| `evidence_workflow.py` | 证据、候选与「报告校准有误」三种 Issue 的处理：fresh REST 作者身份与正文、固定 Git/gh 参数、原子台账/索引写入、冲突重算、单机器人评论幂等、已结案 Issue 的关闭和有界持久游标 sweep | 是 |
| `public-repo/` | 公开仓库的四语 README、LICENSE、Issue 表单、工作流、`.gitattributes`（`dot-` 前缀的文件同步时改名为点文件） | 是（改名后） |
| `sync_public_repo.py` | 把上面这些与随包模板、空索引、许可全文组装成公开仓库的完整文件集 | 否 |
| `generate_index_sample.py` | 生成 C# 回归测试读的 `tests/Fixtures/shared-calibration/index-sample*` | 否 |
| `testsupport.py`、`test_*.py` | 自测 | 否 |

## 与已发布客户端保持一致

- **校准码**：`test_sharecode.py` 让 Python 解码器逐条处理 `tests/Fixtures/shared-calibration/vectors.json` 的全部向量，
  该文件与 `ShareCodeTests.cs` 所用的是同一份，拒绝码逐条相同。测试还覆盖 .NET 运行时带来的行为：`string.Trim()` 的空白集合、
  UTF-16 计长、截断或带尾部数据的 DEFLATE 流、BOM、JSON 嵌套深度 8、`TryGetInt64` 的整数判定。
  规范化 JSON 复制自 `tools/protocol-profile-validator/validate.py`，以使公开仓库能够独立运行；测试比对两份输出一致。
- **索引**：`index.read_index` / `select` / `revoked_codes` 移植 `SharedCalibrationIndex`，`test_index.py` 复用 C# 测试的输入。
  可选字段 `conflicting`（§18.6，§20.3 起为字段级）与 `shape_prior`（§20.3）两端一致：读到非布尔值分别记
  `INVALID:conflicting`、`INVALID:shape_prior` 并跳过该条目；挑选顺序（`index.pick_key`）依次为 `submitters` 从多到少、
  不带 `conflicting` 的在前、`shape_prior` 为 `true` 的在前、首次发布时间从早到晚、编号，即两个标记都只在提交人数相同时起作用。
  标记只在 `update-index`、`revoke` 与维护者的 `remark` 写文件前重算，字段缺省即「无冲突」「无可比的上一版本」，旧索引因此逐字节不变。
  `generate_index_sample.py` 只通过 `index.add_submission` 与 `index.revoke` 生成样本索引与校准码文件，
  并像 `update-index`、`revoke` 一样在每一步之后用 `index.refresh_marks` 重算标记，因此样本含两种标记的各种取值，
  预期文件也逐条记下每个挑选结果的两个标记；
  `SharedCalibrationPublicRepoSampleTests.cs` 用已发布的 `SharedCalibrationIndex` 与 `SharedCalibrationClient` 读取它，
  要求挑选顺序与 Python 完全一致；`test_index_sample.py` 保证测试样本即生成器的输出，不得手工修改。重新生成：

  ```powershell
  python tools/shared-calibration/generate_index_sample.py          # 写入
  python tools/shared-calibration/generate_index_sample.py --check  # 只比对
  ```

- **常量**：`test_public_repo_files.py` 从 C# 源码里读仓库所有者与名称、索引与校准码的大小上限、条数上限、前缀、
  拒绝码与中文提示，与 Python 一一比对；Issue 表单文件名、字段 `id: code`、标题前缀与 `SharedCalibrationIssueLink.cs` 比对。

## Action 执行的规则

1. 只处理带 `share-calibration` 标签、仍处于打开状态、尚无结果标签（`published` / `rejected` / `needs-maintainer`）的 Issue。
   **状态与标签一律实时查询**（`gh issue view --json state,labels`，交给 `publish.py` 的 `--live`），不取事件里的那一份：
   同一个 Issue 的 `labeled` 事件、编辑、重投的事件都可能排在已经作答的那次运行之后，事件里记着的却是它触发那一刻的标签。
   转交维护者的 Issue 保持打开、带着 `needs-maintainer`，因此只有现查的标签能挡住第二次回复（结果为 `skipped`，原因 `ALREADY_ANSWERED`）。
2. 提交者必须是个人账号，`gh api users/<login>` 返回的数字编号必须等于 Issue 作者的编号，账号注册满 **30 天**（正好 30 天算满）。
3. 正文必须恰好有一个「校准码」栏目、一个「确认」栏目，并勾选「上面的来源属实，我已在软件里核对过」
   （旧版表单的「我在软件里逐条核对过校准时间线」同样有效）；栏目缺失或重复一律拒绝，不作推测。
   「这份校准码的来源」是可选的单行输入框：GitHub 只能用查询参数预填文本框与输入框，不能预选下拉框，因此软件在表单地址里带上
   `basis`，填入「本机校准得到，并逐条核对过校准时间线」或「其他玩家分享，已在我的软件里核实通过并正常记录」。
   该栏缺失（旧版表单）、为空或为 `_No response_`（旧版软件不带 `basis`）时按本机校准处理；填了这两种文字以外的内容拒绝（`BASIS_INVALID`），
   出现两次拒绝（`BASIS_AMBIGUOUS`）。
4. 校准码按客户端规则解码；模板必须是 `templates/` 里的某一个；按结构重建必须成立。
   客户端版本号必须是真实客户端的格式 `YYYY.MM.DD.NNNN.NNNN`，且日期是真实的日历日期（`index.is_game_build`）；
   日期也不得晚于提交日（UTC）的次日（`BUILD_IN_FUTURE`），多出的一天留给版本号所用的时区。
   客户端接受的版本号格式更宽松，读取时（`read_index`、台账）因此仍按客户端的规则，只有受理新提交时从严。
5. **每个 GitHub 账号，每个区服与客户端版本，同时只有一份在用的校准码**（按数字编号判定，改名无效）。
   同一份重复提交不重复计数；**另一份则替换先前那一份**，见下表。
6. **每个账号的提交量有上限**，防止单个账号把客户端下载的索引写满：同一区服与版本最多先后提交
   `MAX_CODES_PER_ACCOUNT_BUILD`（3）份不同的码（加入别人已发布的码也算一份）；每个 UTC 日最多为
   `MAX_BUILDS_PER_ACCOUNT_DAY`（2）个区服与版本提交。超出时拒绝（`ACCOUNT_BUILD_LIMIT`、`ACCOUNT_DAILY_LIMIT`），不转交维护者。
   两条合起来，一个账号一天最多新增 6 个条目；没有这两条时，一个账号一小时即可新增约 60 条，约占 64 KB 上限的三分之一。
   条目只能由维护者用 `publish.py prune --region <CN|GLOBAL> --build <版本> [--build <版本> …]` 成批移除：
   只移除逐个写明的客户端版本，连同其条目、台账行与 `.mrc` 文件，结果仍能被 `index.load` 读回；
   加 `--apply` 时还会重算该区服日期晚于被移除版本的各版本的标记，使 `shape_prior` 不再以已移除的版本为依据。
   不加 `--apply` 时只打印将要移除的版本、条目数、台账行数与文件，不改动任何文件；核对无误后加 `--apply` 执行。
   写明的版本在该区服下不存在时，退出码为 2，什么也不删除。工具不按日期自行挑选保留哪些版本：
   版本号由提交者给出，当天日期的版本照常受理，按日期排序会让编造的当天版本排在真实版本之前。
7. 不设名额上限；同一份码（`code_sha256` 相同）由不同账号提交时 `submitters` 加一，`commit` 与 `first_published_at` 不变。
   选择「其他玩家分享，已在我的软件里核实通过」的提交与本机校准的提交适用同一套规则、同样计数，
   只是台账行多一个 `basis: "verified"`（缺省即本机校准），回复的措辞也相应不同。
8. **已撤销的码不再发布**，无论由谁提交，也无论它是被维护者撤销还是被替换撤下。
   **被维护者撤销的码会暂停它的每个提交账号**在该区服与版本下的提交（`ACCOUNT_HELD`，拒绝并请提交者留言申请复核），
   直到维护者执行 `publish.py lift`。暂停不需要台账新增字段：账号那一行仍指向被撤销的码，就是暂停；
   被替换撤下的码不会留下这样的行，因为它最后一个提交者的那一行在同一步里被新的一行取代。
9. 文件名（编号前 12 位）被另一份**在用的**码占用、或写入后索引会超过客户端上限（64 KB、512 条）时，
   转交维护者（标签 `needs-maintainer`，Issue 保持打开）。按当前的条目长度，64 KB 约在 190 条左右先达到上限。
   只被已撤销的码占用的文件名可以重新使用：新码覆盖该文件，旧条目仍按自己的提交号指向原来的文件内容。
   客户端读取这样的索引没有问题（撤销按编号判定，下载按提交号）；本机此前已存下同名旧码文件的客户端，
   在所读索引撤销了该旧码时用新码覆盖这个文件；同名文件存着未被撤销的另一份码时仍不覆盖（`NAME_TAKEN`）。
10. 新码先单独提交 `.mrc` 文件，再把这次提交的 sha 写进索引条目的 `commit` 并提交 `index.json` 与 `submissions.json`；
    写文件前重算该区服、该版本以及该区服更晚各版本的 `conflicting` 与 `shape_prior`（见下文「校准码之间不一致时」）；
    不调用 jsDelivr 清缓存。
11. 回复中文说明；结果打标签 `published`（已发布、已计入、重复）或 `rejected`（拒绝，关闭为 not planned）；仓库侧问题打 `needs-maintainer`。
    新码与已发布的码不一致时，另给本 Issue 与对方 Issue 打 `conflicting`，并在对方 Issue 下评论一次；再有账号计入同一份码时只在本 Issue 的回复里提示并打标签，不再评论对方 Issue。

### 校准码之间不一致时

`index.update_marks` 按 §20.3 为一个区服与版本重算两个可选标记，只读该版本已发布、未撤销、码文件可读的条目：

- **`conflicting`（字段级）。** 一份校准码按四个字段比对：换区报文（`zone`）、区域报文（`territory`）、职业报文（`job`）
  与匹配报文（`match`，即匹配方式加 `pop` 对象）。只有模板哈希相同的码互相比较；换区、区域、职业三个字段跨匹配方式比较，
  匹配报文只在同一匹配方式内比较；码里缺省的字段（`null`）不投票，也不构成分歧。某份码在任一字段上与另一份不同即标
  `conflicting: true`，否则不写该字段。§18.6 的旧规则只比较同一匹配方式下的整份码：匹配方式不同、换区报文不同的两份码
  原先不会被标记，现在会；两份码只因一方缺省某个字段而不同时，原先会被标记，现在不会。
- **`shape_prior`（结构先验）。** 码的「形状」按匹配方式取：独立报文为长度，marker 为长度与轮盘偏移，回执状态为状态值列表，
  按排本推断没有形状（从不写该字段）。比较对象是同一区服、同一模板、同一匹配方式下，版本日期**严格早于**本版本的未撤销码中
  **最近的那一个版本**（同日的多个版本按版本号取较大者；没有日期的版本不参与比较）。该版本有形状相同的码为 `true`，
  有码但形状都不同为 `false`，没有可比的版本或没有形状时不写。
  **在本版本有提交记录的账号提交的更早版本的码一律不作比较**（安全评审 2026-10-15）：版本号的日期由提交者给出，
  否则一个账号可以先为一个日期介于真实上一版本与本版本之间的虚构版本发布自己的形状，再在本版本发布对立的码，
  让对立码独得 `true`、诚实的码变成 `false`。排除的是在本版本有台账行的所有账号（含被撤销、被替换的码的提交者），
  而不只是这份码自己的提交者，因此撤销对立码或在真实上一版本补交一份码都不会把先验交还给攻击者。
  代价是同一个账号连续两个版本都提交时，它在上一版本的码不再为本版本任何一份码提供先验；两个独立账号的情形不受影响。
  两个不同的账号仍可配合完成上述攻击，这只能靠票数与玩家本机的核实挡住。
  `shape_prior` 只决定客户端下载与核实的先后：客户端的启用门槛比较的是玩家本机自己在上一版本核实过的档案，而不是这个字段，
  它也从不作为拒绝的依据。
- 已撤销的条目不参与比较，也不带任何标记；码文件读不出来的条目同样不带标记，并在 stderr 记名。
- `index.refresh_marks` 在重算本版本之后，再重算同一区服所有更晚日期的版本，因为它们的 `shape_prior` 可能以本版本为准。
  `update-index`（已发布、已计入）与 `revoke` 都通过它写文件；`index.update_conflicts` 保留为 `update_marks` 的别名。
- 标记会让索引变大。重算后的索引超过客户端字节上限时分级退让，并在 stderr 说明：先去掉重算版本的 `shape_prior`、保留新的
  `conflicting`；仍然放不下时保留索引原有的标记，不写这次重算的结果。放得下的 `conflicting` 从不为腾出空间而删去，
  因为已发布的 1.7.2 客户端仍按它决定启用门槛；而被拒绝的索引会让所有版本的码一起失效。

`index.disputes` 给出逐字段的分歧（只供回复使用，不写进索引）。`update-index` 据此在结果中列出 `conflicts`：
每份对方码的编号前 12 位、对方的 Issue 编号（取自 `submissions.json`，排除本 Issue，升序，每份码最多 8 个）与分歧字段的中文名
（换区报文、区域报文、职业报文、匹配报文）。对方码按提交人数从多到少、再按发布先后排列，一次发布合计最多列出 8 个 Issue，
排在后面的码分不到名额时只以校准码编号出现。随后：

- 本 Issue 的回复在摘要之后增加一段提示，逐份说明与哪个 Issue 的码在哪些字段上不一致；对方没有可用的 Issue 编号时改写校准码编号。
  提示同时说明玩家的软件怎样处理：提交人数相同时优先使用匹配报文结构与它自己在上一版本核实过的档案一致的那一份，
  仓库的 `shape_prior` 只决定下载与核实的先后，没有这样的依据时两份都要先完整核实。计入（同一份码多一个账号）的回复把对方
  Issue 写成 `＃N`（全角），GitHub 不会因此在对方 Issue 的时间线上逐次添加引用；发布新码的回复仍写 `#N`。
- 只在发布新码时，`--out` 下为每个对方 Issue 写一份 `conflict-<编号>.md`，内容只由固定文字、两个 Issue 编号与字段名拼成，
  不含任何提交者写的内容；计入时不写，`conflict_issues` 为空，已关闭的对方 Issue 不会因每多一个账号而再收到评论。
- `publish_issue.sh` 在主回复、关闭与结果标签之后读取 `conflict_label` 与 `conflict_issues` 两个经白名单校验的字段：
  建 `conflicting` 标签，打在本 Issue 上，再对每个对方 Issue 评论一次并打同一标签。这一步尽力而为，失败只输出 `::warning::`，
  既不让任务失败，也不会让补处理再次回复同一个提交。维护者按 `conflicting` 标签即可找到所有成对的不一致。

**维护者命令 `remark`。** 发布与撤销只重算它们涉及的版本，§20.3 之前写出的条目在该版本有新提交之前没有 `shape_prior`，
`conflicting` 也仍是旧规则的结果。部署本轮改动后执行一次：

```bash
python tools/publish.py remark --repo .                                         # 索引中的全部区服与版本
python tools/publish.py remark --repo . --region CN --build 2026.10.06.0000.0000  # 只重算点名的版本，可重复
```

它按 `update_marks` 重算后写回 `index.json` 与 `submissions.json`，打印一行 JSON：重算了哪些区服与版本（`recomputed`）、
多少条目发生变化（`changed`）、哪些版本因字节上限没能写入全部标记（`dropped`，每项的 `kept` 为 `conflicting` 表示只写了
`conflicting`、为 `previous` 表示保留了原有标记，同时在 stderr 说明）。点名的版本索引中不存在、
或 `--region` 与 `--build` 没有成对给出时退出码为 2，不写任何文件。重复执行不会再改变什么。

### 同账号再次提交时的判定

设某账号在某区服、某客户端版本下已有台账记录，本次提交的码为**新码**，台账里记着的为**旧码**：

| 情形 | 结果 |
|---|---|
| 新码与旧码相同，且未撤销 | `duplicate`，不重复计数，文件与索引都不变 |
| 新码已被撤销（任何人、任何原因） | `refused`（`REVOKED`），旧码原样保留 |
| 旧码已被维护者撤销 | `refused`（`ACCOUNT_HELD`），台账行原样保留，直到维护者 `lift` |
| 旧码、`replaced` 与新码合计超过 3 份不同的码 | `refused`（`ACCOUNT_BUILD_LIMIT`），旧码原样保留 |
| 新码是一份全新的码 | `published`：旧码按下面两行处理，新码照常发布 |
| 新码已由别的账号发布且未撤销 | `added`：该码 `submitters` 加一，旧码按下面两行处理 |
| ——旧码只有这一个提交者 | 旧码标 `revoked: true`（条目不删除，`submitters` 仍为 1） |
| ——旧码还有别的账号提交过 | 旧码不撤销，只把该账号移出台账，`submitters` 减一 |

旧码**被维护者撤销**时不再按「替换」处理：台账行留在被撤销的码上，该账号在这个区服与版本下暂停受理。
维护者复核后执行 `publish.py lift --repo . --account id:<数字编号> --region <CN|GLOBAL> --build <版本>`：
这一行被删除，被撤销条目的 `submitters` 相应减一（最少为 1），该账号随后的提交按全新提交处理，份数上限也重新计算。
台账行增加可选字段 `replaced`（该账号此前提交过的码，旧在前），仅供审计；`index.json` 的格式**不变**，
被取代的码就是 `revoked: true`，旧版客户端按撤销处理，语义正确。

`replaced` 的两条约束：

- **一行的 `replaced` 里绝不含这一行当前的码。** 旧码还有别的账号提交时并不撤销，该账号因此可以再换回去
  （X 与 Z 都提交 A，X 换成 B，X 又换回 A）。若让 A 出现在 A 自己那一行里，`dump` 写得出去、
  下一次 `parse` 却会判为 `IndexCorrupt`——而每次 Action 都以读取台账开始，整个提交流程会就此停摆。
  换回去时把离开的那一份（B）留在链里，历史不丢。`check_consistent` 与 `_read_ledger` 因此校验同一组规则：
  **读不回来的台账不允许被写出去**。
- **只保留最近 `MAX_REPLACED`（16）项**，并且 `submissions.json` 另有 `MAX_LEDGER_BYTES`（4 MiB）上限，
  超出时按维护者问题（`LEDGER_FULL_BYTES`）处理。两者合起来堵住「两个账号互相把两份码轮流换来换去」把台账撑大的路径。
  这两个上限仅适用于仓库：客户端从不下载台账。

### 「报告校准有误」的规则

1. `report-calibration.yml` 在 `opened`、`edited`、`labeled`、`reopened` 事件中，对带 `calibration-report` 标签、仍处于打开状态的
   Issue 运行 `python -B tools/evidence_workflow.py --event "$GITHUB_EVENT_PATH"`。同时带证据表单标签的 Issue 按证据处理。
2. 正文不作解析：普通文字报告一律不计票，结果为 `report`（原因 `STRUCTURED_EVIDENCE_REQUIRED`），回复固定文字，
   指引玩家用软件的「共享校准证据」生成摘要后新建证据 Issue，并打上 `evidence-waiting` 标签。
3. 每份报告在 `evidence-ledger.json` 的 `reports` 中按 Issue 编号记一条无票收据（只含账号数字编号），用于去重与容量控制：
   全局最多 4096 条、每个账号最多 64 条，超出时回复 `LEDGER_CAPACITY_WAIT` 或 `ACCOUNT_CAPACITY_WAIT` 并保持等待。
   收据随台账提交并推送 `main`，因此该工作流需要 `contents: write`，并与发布共用 `publish-calibration` 并发组。
4. **不做任何自动撤回或隔离**，也不按报告数量设阈值：报告只是维护者查看的理由，**Issue 保持打开**。撤回始终由维护者执行 `publish.py revoke`。
5. 状态、标签、作者与正文一律以实时 REST 查询为准，事件只用来定位 Issue 编号；回复前后各核对一次 Issue 未被改动。
   每个 Issue 只有一条机器人评论（按评论开头的标记识别），重投的事件与重试只原地更新它，不重复回复。
6. 回复只由固定文字拼成，不回显正文或标题的任何内容。
7. 任务在开始前被并发组取消的报告，由 `evidence-calibration.yml` 的定时 sweep 补处理（见下文）。

`report_issue.sh` 与 `publish.py report` 仍随公开仓库同步，`test_workflow_script.py` 继续测试它们，但已没有工作流调用。

### 证据台账的维护

**会话别名与候选。** 一个会话别名（按账号计）只归它第一次被受理时所属的候选。1.7.3 客户端在一次抓包会话中给所有候选用同一个别名，
各候选的判据各不相同；另一个候选的摘要重复这个别名时，该会话对这个候选不计，摘要照常受理，不判为无效。
1.7.3 之后的客户端在同一次抓包会话中为每个候选各生成一个别名，该会话因此对每个候选都计入。

**抢先复制。** 公开摘要可以被复制。`evidence_id` 或会话别名已由另一个账号的 Issue 先被受理、而本 Issue 编号更小（创建得更早）、
且工作流此前从未答复过本 Issue 时，判给本 Issue：较晚那个 Issue 的全部收据、由它新建的会话行和它的票一并作废，并重算相关候选；
较晚 Issue 的 Issue 行原样保留。之后再处理较晚的 Issue 时按复制处理（`EVIDENCE_ID_REUSED`，或该会话不计）。
本 Issue 此前已得到过答复（受理、等待、未受理、候选草案或报告，任何结果都算）时，同样按复制处理：Issue 正文可以随时编辑，
先建 Issue、得到答复后再贴入他人已受理的摘要，不能夺走对方的收据与票。
工作流把每个答复过的 Issue 记入台账的 `answered`（Issue 编号 → 首次答复时的台账序号），与处理结果在同一次提交中、并在回复之前推送；
运行在推送之前失败时 Issue 也没有得到回复，下次处理仍算首次。
`answered` 最多 16384 行；写满、或再记一行会使台账超过字节上限时，`answered_floor` 提到已记录的最大编号并清空该表，
编号不超过它的 Issue 一律算已答复：此时尚未处理过的 Issue 不再能收回被抢先的摘要，已答复过的 Issue 也绝不会因此获得这一资格。
`schema_version` 为 1 的旧台账在读取时升级为 2，`answered_floor` 取台账中出现过的最大 Issue 编号。
仍无法区分的情形：Issue 在首次被处理之前（任务被并发组取消、sweep 尚未处理到它）就被编辑贴入他人已受理的摘要，与真正被抢先的 Issue 无从区分，
仍会判给它；这只可能发生在 sweep 处理到它之前，只能由维护者核对。

**解除隔离（`evidence lift`）。** 维护者复核后执行：

```bash
python tools/publish.py evidence lift --repo . <候选编号>
```

候选编号是 `evidence-ledger.json` 中 `candidates` 下的 64 位十六进制键，其 `scope` 写明区服、版本与 `code_sha256`。
命令清除该候选的全部隔离原因，并像自动恢复一样把触发这些原因的负向票记为已消耗；`quarantine_epoch` 不变，
此后只有新收到的证据才能再次隔离它（届时 epoch 加一）。随后按台账重写 `index.json` 的隔离提示，之后的证据处理因此不会把它改回去；
只手改 `index.json` 则会被下一次处理按台账覆盖。打印一行 JSON（`lifted`、`fields` 与该候选的区服、版本、`code_sha256`）。
候选不存在或没有被隔离时退出码为 2，不写任何文件。

**腾出容量（`evidence compact`）。** 台账的全局与每账号上限到顶后，新证据只会等待（`LEDGER_CAPACITY_WAIT`、`ACCOUNT_CAPACITY_WAIT`）。

```bash
python tools/publish.py evidence compact --repo .          # 只打印各类将删除的行数
python tools/publish.py evidence compact --repo . --apply  # 写回 evidence-ledger.json，并按它重写 index.json 的隔离提示
```

只删除以后不会再被任何 Issue 计入的行：

- 已退役候选的全部行：它的校准码在 `index.json` 中已撤销（包括被替换撤下）或已被 `prune` 移除，针对这样的校准码的证据在受理前就被拒绝（`PUBLISHED_CANDIDATE_REQUIRED`）。
  被 `prune` 移除的版本若之后重新发布同一份码，它的证据从零开始累计；
- 不属于任何候选的会话行（模板观察从不计票）；
- 早于所属候选当前 epoch 的会话行：客户端只把会话放进同一 epoch 的摘要；
- 被同一 Issue 后来的收据取代的收据：sweep 只重读 Issue 当前的正文，它对应的收据保留，重读仍判为重复；
- 收据已全部删除的 Issue 行。

报告收据、已答复 Issue 的记录（`answered` 与 `answered_floor`）与序号不删除。打印一行 JSON（`applied` 与各类删除行数 `removed`），不加 `--apply` 时不改动任何文件。
删除较旧 epoch 的会话行之后，仍停留在旧 epoch 的客户端（索引长期没能刷新）若再提交含这些会话的新摘要，它们会被当作新会话；
这与一个账号自行编造会话的效果相同，仍受「至少 3 个账号」的门槛约束。
两条命令都只改本地文件，核对差异后由维护者提交并推送 `main`。

## 工作流的安全措施

- 标题、正文、登录名均视为不可信输入。没有任何 `run:` 脚本含 `${{ }}` 表达式；`env:` 只传令牌、仓库名与 Issue 编号；
  只有 `publish.py` 从事件文件读取 Issue 内容，交回 shell 的每个值（编号、登录名、状态、标签、关闭原因、文件路径、提交说明）都先按白名单校验。
  `evidence_workflow.py` 只从事件取 Issue 编号，内容与作者一律重新经 REST 读取，全程在 Python 内处理，不交回 shell。
- 登录名只用于 `gh api "users/$login"`，事先校验为 `[A-Za-z0-9-]{1,39}`；账号编号再与事件中的作者编号比对。
- 回复先写入文件，再以 `--body-file` 发送。回显的内容（例如校准码中未知的键名）经 `issue.escape_inline` 处理：去掉换行、控制与零宽字符，
  转义 Markdown/HTML，`@ # : / .` 替换为全角，杜绝提及、链接与自动链接。标题从不回显。
  给对方 Issue 的通知（`conflict-<编号>.md`）不回显任何内容；对方 Issue 编号由 `publish.py` 校验为 1–10 位、不以 0 开头的数字，
  以单个空格分隔交给 shell，标签只能是 `conflicting`。
- `publish.py` 每次只打印一行 ASCII JSON，Issue 内容不可能出现在日志的行首，因此无法伪装成工作流命令。
- `publish-calibration.yml`、`evidence-calibration.yml` 与 `report-calibration.yml` 只用默认 `GITHUB_TOKEN`，权限均为
  `contents: write`、`issues: write`：三者都会推送 `main`（证据台账、索引的隔离提示、候选草案与报告收据也要提交）。
  只有 `mirror-repository.yml` 读取 secret `GITCODE_MIRROR_TOKEN`；它只检出可信的 `main`，`persist-credentials: false`，权限仅 `contents: read`。
  所有工作流都不用 `pull_request_target`。
- 发布、证据与报告三个工作流的 job 共用同一个 `publish-calibration` 并发组，该配置写在 job 上，被 `if` 跳过的事件不占用队列；
  运行中的任务从不取消。三者都写 `main`，证据与报告还写同一份 `evidence-ledger.json`，因此不能并行。
- `mirror-repository.yml` 在推送、每 30 分钟的定时任务，以及上述三个工作流任意一次运行结束后（不论结论，因为一次运行可能先推送再失败）
  把公开仓库的分支与标签同步到 GitCode 上的同名镜像。
- 推送被拒绝时**不执行 rebase**。索引按提交 sha 指向码文件，rebase 会改写该提交，因此每次重试都从最新的 main 重新判定、
  重新生成两个提交，最多 5 次；仍失败则回复并转交维护者。
- 脚本的全部语句写在函数内，最后一行以 `exit` 结尾，因为 `git reset --hard` 可能在运行中替换脚本文件本身。
- 每个 `uses:` 都按提交号固定，版本写在行尾注释里；标签可以被改指向别的提交，而这里的任务持有可写本仓库的令牌。
  所用 Action 与主仓库 CI 取自同一组提交号（`test_public_repo_files.py` 比对 `.github/workflows/ci.yml` 中的 `uses:`），
  目前为 `actions/checkout`（v7.0.1，按提交号固定）与 `actions/setup-python`（v7.0.0，按提交号固定）。

**定时补处理（`sweep`）的原因。** GitHub 的并发组中**只保留一个排队中的任务**，较新的排队任务会取消较早的任务
（`cancel-in-progress: false` 只保护正在运行的任务）。补丁日大量玩家同时分享时，部分 Issue 的任务会在开始前被取消。
`sweep` 每 30 分钟运行一次，也可手动触发，列出仍打开、带 `share-calibration`、且尚无 `published` / `rejected` / `needs-maintainer`
标签的 Issue，逐个交由同一个 `publish_issue.sh` 处理。

`evidence-calibration.yml` 的 `sweep` 同样每 30 分钟运行一次，按持久游标 `evidence-sweep.json` 每次至多处理 25 个仍打开、
带 `calibration-evidence`、`template-observation` 或 `calibration-report` 的 Issue，到达末尾后从头开始。
已计入立场的证据 Issue 与已归档候选草案的 Issue 在回复后关闭（`completed`）：重读同一份正文只会得到同样的答复，关闭后 sweep 不再重复处理它们。
仍在等待、未受理的证据 Issue 与报告保持打开，玩家可以编辑原 Issue 补充或修正；重新打开已关闭的 Issue 会再处理一次。

## 公开仓库布局

```text
index.json                                   已发布校准码的索引（客户端下载）
submissions.json                             提交台账：账号数字编号 → 校准码（客户端不下载）
cn/<客户端版本>/<编号前12位>.mrc              校准码文本（客户端按提交 sha 下载）
global/<客户端版本>/<编号前12位>.mrc
templates/cn.2026.08.05.json                 随包模板的逐字节副本
tools/                                       本目录的运行文件
.github/ISSUE_TEMPLATE/share-calibration.yml  提交表单（字段 id: code）
.github/ISSUE_TEMPLATE/report-calibration.yml 报告校准有误的表单（区服、游戏版本、现象、说明）
.github/ISSUE_TEMPLATE/evidence-calibration.yml 最小公开证据表单
.github/ISSUE_TEMPLATE/template-observation.yml 无模板区服候选线索表单
.github/ISSUE_TEMPLATE/config.yml             关闭空白 Issue
.github/workflows/publish-calibration.yml     自动发布与定时补处理
.github/workflows/report-calibration.yml      普通报告自动等待、无投票收据与幂等回复
.github/workflows/evidence-calibration.yml    证据受理、可逆隔离恢复、候选归档与补处理
.github/workflows/mirror-repository.yml       同步到 GitCode 镜像（tools/sync_refs.py，需要 secret GITCODE_MIRROR_TOKEN）
schemas/calibration-evidence-v1.schema.json   canonical 证据格式的逐字节副本
evidence-ledger.json、evidence-sweep.json     有界证据台账与补处理游标
candidates/<哈希>.json                       无模板区服的 CANDIDATE 草案（证据工作流写入）
README.md、README.zh-TW.md、README.en.md、README.ja.md
LICENSE.md、LICENSES/GPL-3.0-or-later.txt、.gitattributes、.gitignore
```

四语文档均由 `public-repo/` 随生成器复制，规则变更时同步更新四份 README。
字段名、命令、表单文件名与协议 token 保留原拼写；README 翻译不会改变现有中文 Issue 栏目或解析契约。

## 自测

```powershell
python -m unittest discover -s tools/shared-calibration
pwsh -NoProfile -File scripts/run-python-tool-tests.ps1   # verify.ps1 的工具自测会自动发现本目录的 test_*.py
```

`test_workflow_script.py` 在本地以裸仓库代替 GitHub，以桩 `gh` 记录调用，真实运行三个 shell 脚本，
其中包括「维护者先推送」的并发场景，以及旧接口 `report_issue.sh` 只回复一次、不关闭 Issue、不改动任何文件。
`test_evidence.py` 用同一套桩真实运行 `evidence_workflow.py`：证据的受理、隔离与恢复，已结案 Issue 的关闭，
以及报告只回复一次、保持打开、不改动索引。
该测试需要 bash 与 git；Windows 上使用 Git for Windows 自带的 bash，不使用 WSL。依赖缺失时跳过。

## 需要用户确认后才能执行的手工步骤

> **第 1–8 步已于 2026-09-16 执行完毕。** 公开仓库
> [`Harendotes-Tang/MentorRecorder-Calibrations`](https://github.com/Harendotes-Tang/MentorRecorder-Calibrations)
> 已建立并推送，四个标签、Issues 与 Actions 均已开启，工作流令牌为读写。数据许可为 CC0-1.0（第 2 步）。
> 以下步骤保留为将来重建仓库时的清单，**每一步仍须先向用户确认**。
>
> **已于 2026-10-06 同步完毕（随 1.6.0 正式版，公开仓库提交 `c66b7c8`）**：下面两段所列的文件均已推送，`calibration-report` 标签已存在，索引与台账的既有文件未改写。
>
> **本轮（替换与报告表单）需要补做的手工步骤**，同样须先向用户确认：
> 把 `sync_public_repo.py` 生成的
> `tools/index.py`、`tools/issue.py`、`tools/publish.py`、`tools/report_issue.sh`、
> `.github/ISSUE_TEMPLATE/report-calibration.yml`、`.github/workflows/report-calibration.yml` 与 `README.md`
> 同步到公开仓库并推送；补建标签 `calibration-report`（第 6 步）。
> 索引与台账的既有文件不需要改写：格式向后兼容，旧台账没有 `replaced` 字段也能照常读写。
>
> **2026-10-03 审计修复需要补做的同步**，同样须先向用户确认：`tools/index.py`、`tools/publish.py`、`tools/publish_issue.sh`、
> `tools/rebuild.py`、`.github/ISSUE_TEMPLATE/share-calibration.yml` 与 `README.md`。不需要新标签，也不需要改写既有的索引与台账：
> 两者的格式都没有变化。此前被维护者撤销的码，如果它的提交账号之后没有再提交，台账行仍指向它，同步后这些账号即按新规则暂停受理。
>
> **校准码置信度（§20）需要补做的步骤**，同样须先向用户确认：把 `sync_public_repo.py` 生成的 `tools/index.py`、`tools/issue.py`、
> `tools/publish.py`、`tools/publish_issue.sh`、`.github/ISSUE_TEMPLATE/share-calibration.yml` 与 `README.md` 同步到公开仓库并推送；
> 补建标签 `conflicting`（第 6 步）；推送后在公开仓库执行一次 `python tools/publish.py remark --repo .`，
> 核对输出后提交并推送 `index.json`，为既有条目补上 `shape_prior` 并按字段级规则重算 `conflicting`。
> 索引与台账的格式向后兼容：旧客户端忽略 `shape_prior`，旧台账没有 `basis` 也照常读写。
>
> **证据通道与 GitCode 镜像（1.7.3 起）需要补做的步骤**，同样须先向用户确认；执行前先用
> `gh label list --repo Harendotes-Tang/MentorRecorder-Calibrations` 与公开仓库的文件列表核对哪些已经存在：
> 把 `sync_public_repo.py` 生成的 `tools/evidence.py`、`tools/evidence_workflow.py`、`tools/publish.py`、`tools/sync_refs.py`、
> `schemas/calibration-evidence-v1.schema.json`、`.github/ISSUE_TEMPLATE/evidence-calibration.yml`、
> `.github/ISSUE_TEMPLATE/template-observation.yml`、`.github/workflows/evidence-calibration.yml`、
> `.github/workflows/report-calibration.yml`、`.github/workflows/mirror-repository.yml` 与四份 README 同步到公开仓库并推送；
> 补建 `calibration-evidence`、`template-observation` 与四个证据结果标签（第 6 步）；添加 secret `GITCODE_MIRROR_TOKEN`（第 7 步）。
> 证据台账 `evidence-ledger.json` 与补处理游标 `evidence-sweep.json` 由工作流第一次处理时创建并提交，不需要手工准备。

以下每一步都会对外产生效果，**执行前逐项向用户确认**：

1. **确认仓库名与所有者。** 客户端已将 `Harendotes-Tang/MentorRecorder-Calibrations` 写死在 `SharedCalibrationClient.cs`
   与 `SharedCalibrationIssueLink.cs` 中。改名需要发布新版客户端，并同步 `publish.py` 的 `OWNER` / `REPOSITORY`。
2. **数据许可为 CC0-1.0**，由用户于 2026-09-16 选定。`public-repo/LICENSE.md`、`public-repo/README.md` 与 Issue 表单说明均已写明
   「提交即表示同意按 CC0-1.0 公开」。仓库不放置 CC0 全文副本，改为链接官方文本。工具与 `templates/` 仍为 GPL-3.0-or-later。
3. 生成文件集并检查：

   ```powershell
   python tools/shared-calibration/sync_public_repo.py --out <新的空目录>
   ```

4. 创建公开仓库（默认分支 `main`，开启 Issues）：

   ```powershell
   gh repo create Harendotes-Tang/MentorRecorder-Calibrations --public --description "MentorRecorder 共享校准码（自动发布）"
   ```

5. 在生成的目录里首次提交并推送：

   ```powershell
   git init -b main
   git add -A
   git commit -m "chore: initial calibration repository"
   git remote add origin https://github.com/Harendotes-Tang/MentorRecorder-Calibrations.git
   git push -u origin main
   ```

6. 建十二个标签：

   ```powershell
   gh label create share-calibration --repo Harendotes-Tang/MentorRecorder-Calibrations --color 1D76DB --description "共享校准码提交"
   gh label create calibration-report --repo Harendotes-Tang/MentorRecorder-Calibrations --color D93F0B --description "报告校准有误"
   gh label create published --repo Harendotes-Tang/MentorRecorder-Calibrations --color 0E8A16 --description "已发布"
   gh label create rejected --repo Harendotes-Tang/MentorRecorder-Calibrations --color B60205 --description "未受理"
   gh label create needs-maintainer --repo Harendotes-Tang/MentorRecorder-Calibrations --color FBCA04 --description "需要维护者处理"
   gh label create conflicting --repo Harendotes-Tang/MentorRecorder-Calibrations --color C5DEF5 --description "与同版本的另一份校准码不一致"
   gh label create calibration-evidence --repo Harendotes-Tang/MentorRecorder-Calibrations --color 5319E7 --description "共享校准证据摘要"
   gh label create template-observation --repo Harendotes-Tang/MentorRecorder-Calibrations --color 006B75 --description "无模板区服的候选线索"
   gh label create evidence-accepted --repo Harendotes-Tang/MentorRecorder-Calibrations --color 0E8A16 --description "证据已计入该账号的立场"
   gh label create evidence-waiting --repo Harendotes-Tang/MentorRecorder-Calibrations --color C2E0C6 --description "证据或报告自动等待"
   gh label create evidence-invalid --repo Harendotes-Tang/MentorRecorder-Calibrations --color E99695 --description "证据摘要未受理"
   gh label create candidate-waiting --repo Harendotes-Tang/MentorRecorder-Calibrations --color BFD4F2 --description "候选草案已归档，协议语义尚不完整"
   ```

   > **仓库已存在时，先用 `gh label list` 核对，只补建缺少的标签**（`calibration-report` 已于 2026-10-06 同步时建好，`needs-maintainer` 已于 2026-09-16 建好）。
   > `calibration-evidence` 与 `template-observation` 必须在证据表单启用前建好：GitHub 只在标签已存在时才给用表单提交的 Issue 打上它，
   > 而证据工作流只处理带这两个标签之一（或 `calibration-report`）的 Issue，缺少时这些 Issue 永远不会被处理。
   > 其余标签缺失时，脚本在打标签前会先 `gh label create`，不会让 Issue 无人回复，但预先建好才有颜色与说明。

7. 设置：确认 Issues 已开启（`gh repo edit Harendotes-Tang/MentorRecorder-Calibrations --enable-issues`），Actions 已允许运行。
   Settings → Actions → General → Workflow permissions：工作流文件已自行声明 `contents: write` 与 `issues: write`；
   若仓库或账号策略将令牌限制为只读，选择 "Read and write permissions"。
   GitCode 镜像工作流 `mirror-repository.yml` 需要仓库 secret `GITCODE_MIRROR_TOKEN`（对 GitCode 上同名镜像仓库有推送权限的令牌）：
   `gh secret set GITCODE_MIRROR_TOKEN --repo Harendotes-Tang/MentorRecorder-Calibrations`。其余工作流只用默认 `GITHUB_TOKEN`，不读取任何 secret。
8. **验收（plan §8 阶段 D）**：在公开宣布前用测试 Issue 走通以下场景：合法码发布；非法码拒绝并回复原因；标题与正文中的注入
   （`$(…)`、反引号、`@提及`、Markdown 链接、`### 校准码` 重复栏目）既不被执行也不被回显；两份并发提交不丢失更新；
   非协作者账号创建的 Issue 可正常发布（plan §9 待实测项）。测试数据建议使用不存在的过去的客户端版本号，
   例如 `2000.01.01.0000.0000`；日期晚于提交日次日的版本号会被拒绝（`BUILD_IN_FUTURE`）。
   验收后撤销测试数据，或在公开前重建仓库，由用户决定。
9. 公开仓库 60 天无提交时，GitHub 会停用定时工作流（`sweep`）。发现停用后，在 Actions 页重新启用。
