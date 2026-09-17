# tools/shared-calibration — 共享校准公开仓库的工具

共享校准阶段 D。本文面向维护公开校准仓库的维护者。
本目录是公开数据仓库 `Harendotes-Tang/MentorRecorder-Calibrations` 中 `tools/`、Issue 表单与工作流的**唯一源头**。
该仓库名与客户端 `SharedCalibrationClient.Owner` / `Repository` 一致。
公开仓库中的副本由 `sync_public_repo.py` 生成，不在公开仓库内手工修改。

> 本目录中没有任何脚本会创建仓库、推送或调用 GitHub API。
> 建仓与推送属于下文「需要用户确认后才能执行的手工步骤」，须经用户确认后执行。

本目录只使用 Python 标准库。Action 使用 3.12，本机 3.11 亦可运行。

## 文件

| 文件 | 作用 | 复制到公开仓库 |
|---|---|---|
| `sharecode.py` | `MRC1.` 校准码的解码、编码、规范化、`code_sha256` 与载荷规则；逐条移植 `ShareCode.cs` | 是 |
| `rebuild.py` | 按随包模板结构重建校准码并拒绝无效内容；文件开头列明已移植的 C# 规则与留给客户端本机核实的部分 | 是 |
| `index.py` | `index.json`（客户端读取格式的精确移植）与 `submissions.json` 台账；`add_submission`、`revoke`、`update_conflicts` | 是 |
| `issue.py` | 读 Issue 表单正文；标题只用来发现不一致；回显内容的转义 | 是 |
| `publish.py` | Action 的命令行：`check`、`update-index`、`push-failed`、`field`、`event-field`、`pending`、`wrap-event`、`revoke` | 是 |
| `publish_issue.sh` | 处理一个 Issue：查状态 → 查账号 → 校验、提交、推送（推送被拒时从新的 main 重新开始）→ 回复、打标签、关闭 | 是 |
| `sweep_issues.sh` | 定时补处理：逐个处理尚未回复的提交 | 是 |
| `public-repo/` | 公开仓库的 README、LICENSE、Issue 表单、工作流、`.gitattributes`（`dot-` 前缀的文件同步时改名为点文件） | 是（改名后） |
| `sync_public_repo.py` | 把上面这些与随包模板、空索引、许可全文组装成公开仓库的完整文件集 | 否 |
| `generate_index_sample.py` | 生成 C# 回归测试读的 `tests/Fixtures/shared-calibration/index-sample*` | 否 |
| `testsupport.py`、`test_*.py` | 自测 | 否 |

## 与已发布客户端保持一致

- **校准码**：`test_sharecode.py` 让 Python 解码器逐条处理 `tests/Fixtures/shared-calibration/vectors.json` 的全部向量，
  该文件与 `ShareCodeTests.cs` 所用的是同一份，拒绝码逐条相同。测试还覆盖 .NET 运行时带来的行为：`string.Trim()` 的空白集合、
  UTF-16 计长、截断或带尾部数据的 DEFLATE 流、BOM、JSON 嵌套深度 8、`TryGetInt64` 的整数判定。
  规范化 JSON 复制自 `tools/protocol-profile-validator/validate.py`，以使公开仓库能够独立运行；测试比对两份输出一致。
- **索引**：`index.read_index` / `select` / `revoked_codes` 移植 `SharedCalibrationIndex`，`test_index.py` 复用 C# 测试的输入。
  可选字段 `conflicting`（§18.6）两端一致：读到非布尔值都记 `INVALID:conflicting`，挑选顺序都把带标记的条目排到末尾；
  `index.update_conflicts` 只在 `update-index` 与 `revoke` 写文件前重算，字段缺省即「无冲突」，旧索引因此逐字节不变。
  `generate_index_sample.py` 只通过 `index.add_submission` 生成样本索引与校准码文件，
  `SharedCalibrationPublicRepoSampleTests.cs` 用已发布的 `SharedCalibrationIndex` 与 `SharedCalibrationClient` 读取它，
  要求挑选顺序与 Python 完全一致；`test_index_sample.py` 保证夹具即生成器的输出，不得手工修改。重新生成：

  ```powershell
  python tools/shared-calibration/generate_index_sample.py          # 写入
  python tools/shared-calibration/generate_index_sample.py --check  # 只比对
  ```

- **常量**：`test_public_repo_files.py` 从 C# 源码里读仓库所有者与名称、索引与校准码的大小上限、条数上限、前缀、
  拒绝码与中文提示，与 Python 一一比对；Issue 表单文件名、字段 `id: code`、标题前缀与 `SharedCalibrationIssueLink.cs` 比对。

## Action 执行的规则

1. 只处理带 `share-calibration` 标签、仍然打开的 Issue（事件可能过时，脚本会重新查询状态）。
2. 提交者必须是个人账号，`gh api users/<login>` 返回的数字编号必须等于 Issue 作者的编号，账号注册满 **30 天**（正好 30 天算满）。
3. 正文必须恰好有一个「校准码」栏目、一个「确认」栏目，并勾选「我在软件里逐条核对过校准时间线」；栏目缺失或重复一律拒绝，不作推测。
4. 校准码按客户端规则解码；模板必须是 `templates/` 里的某一个；按结构重建必须成立。
5. **每个 GitHub 账号，每个区服与客户端版本，一份校准码**（按数字编号判定，改名无效）。另一份拒绝，同一份不重复计数。
6. 不设名额上限；同一份码（`code_sha256` 相同）由不同账号提交时 `submitters` 加一，`commit` 与 `first_published_at` 不变。
7. 已撤销的码不再发布；文件名（编号前 12 位）被另一份码占用、或写入后索引会超过客户端上限（64 KB、512 条）时，
   转交维护者（标签 `needs-maintainer`，Issue 保持打开）。按当前的条目长度，64 KB 约在 190 条左右先达到上限。
8. 新码先单独提交 `.mrc` 文件，再把这次提交的 sha 写进索引条目的 `commit` 并提交 `index.json` 与 `submissions.json`；
   不调用 jsDelivr 清缓存。
9. 回复中文说明；结果打标签 `published`（已发布、已计入、重复）或 `rejected`（拒绝，关闭为 not planned）；仓库侧问题打 `needs-maintainer`。

## 工作流的安全措施

- 标题、正文、登录名均视为不可信输入。没有任何 `run:` 脚本含 `${{ }}` 表达式；`env:` 只传令牌、仓库名与 Issue 编号；
  只有 `publish.py` 从事件文件读取 Issue 内容，交回 shell 的每个值（编号、登录名、状态、标签、关闭原因、文件路径、提交说明）都先按白名单校验。
- 登录名只用于 `gh api "users/$login"`，事先校验为 `[A-Za-z0-9-]{1,39}`；账号编号再与事件中的作者编号比对。
- 回复先写入文件，再以 `--body-file` 发送。回显的内容（例如校准码中未知的键名）经 `issue.escape_inline` 处理：去掉换行、控制与零宽字符，
  转义 Markdown/HTML，`@ # : / .` 替换为全角，杜绝提及、链接与自动链接。标题从不回显。
- `publish.py` 每次只打印一行 ASCII JSON，Issue 内容不可能出现在日志的行首，因此无法伪装成工作流命令。
- 只用默认 `GITHUB_TOKEN`，权限仅 `contents: write`、`issues: write`；不用 `pull_request_target`，不读 secrets。
- 两个 job 共用一个 `concurrency` 组，该配置写在 job 上，被 `if` 跳过的事件不占用队列；运行中的任务从不取消。
- 推送被拒绝时**不执行 rebase**。索引按提交 sha 指向码文件，rebase 会改写该提交，因此每次重试都从新的 main 重新判定、
  重新生成两个提交，最多 5 次；仍失败则回复并转交维护者。
- 脚本的全部语句写在函数内，最后一行以 `exit` 结尾，因为 `git reset --hard` 可能在运行中替换脚本文件本身。
- Action 版本与主仓库 CI 一致（`actions/checkout@v4`、`actions/setup-python@v5`）。

**定时补处理（`sweep`）的原因。** GitHub 的并发组中**只保留一个排队中的任务**，较新的排队任务会取消较早的任务
（`cancel-in-progress: false` 只保护正在运行的任务）。补丁日大量玩家同时分享时，部分 Issue 的任务会在开始前被取消。
`sweep` 每 30 分钟运行一次，也可手动触发，列出仍打开、带 `share-calibration`、且尚无 `published` / `rejected` / `needs-maintainer`
标签的 Issue，逐个交由同一个 `publish_issue.sh` 处理。

## 公开仓库布局

```text
index.json                                   已发布校准码的索引（客户端下载）
submissions.json                             提交台账：账号数字编号 → 校准码（客户端不下载）
cn/<客户端版本>/<编号前12位>.mrc              校准码文本（客户端按提交 sha 下载）
global/<客户端版本>/<编号前12位>.mrc
templates/cn.2026.08.05.json                 随包模板的逐字节副本
tools/                                       本目录的运行文件
.github/ISSUE_TEMPLATE/share-calibration.yml 提交表单（字段 id: code）
.github/ISSUE_TEMPLATE/config.yml            关闭空白 Issue
.github/workflows/publish-calibration.yml    自动发布与定时补处理
README.md、LICENSE.md、LICENSES/GPL-3.0-or-later.txt、.gitattributes、.gitignore
```

## 自测

```powershell
python -m unittest discover -s tools/shared-calibration
pwsh -NoProfile -File scripts/run-python-tool-tests.ps1   # verify.ps1 的工具自测会自动发现本目录的 test_*.py
```

`test_workflow_script.py` 在本地以裸仓库代替 GitHub，以桩 `gh` 记录调用，真实运行两个 shell 脚本，其中包括「维护者先推送」的并发场景。
该测试需要 bash 与 git；Windows 上使用 Git for Windows 自带的 bash，不使用 WSL。依赖缺失时跳过。

## 需要用户确认后才能执行的手工步骤

> **第 1–8 步已于 2026-09-16 执行完毕。** 公开仓库
> [`Harendotes-Tang/MentorRecorder-Calibrations`](https://github.com/Harendotes-Tang/MentorRecorder-Calibrations)
> 已建立并推送，四个标签、Issues 与 Actions 均已开启，工作流令牌为读写。数据许可为 CC0-1.0（第 2 步）。
> 以下步骤保留为将来重建仓库时的清单，**每一步仍须先向用户确认**。

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

6. 建四个标签：

   ```powershell
   gh label create share-calibration --repo Harendotes-Tang/MentorRecorder-Calibrations --color 1D76DB --description "共享校准码提交"
   gh label create published --repo Harendotes-Tang/MentorRecorder-Calibrations --color 0E8A16 --description "已发布"
   gh label create rejected --repo Harendotes-Tang/MentorRecorder-Calibrations --color B60205 --description "未受理"
   gh label create needs-maintainer --repo Harendotes-Tang/MentorRecorder-Calibrations --color FBCA04 --description "需要维护者处理"
   ```

7. 设置：确认 Issues 已开启（`gh repo edit Harendotes-Tang/MentorRecorder-Calibrations --enable-issues`），Actions 已允许运行。
   Settings → Actions → General → Workflow permissions：工作流文件已自行声明 `contents: write` 与 `issues: write`；
   若仓库或账号策略将令牌限制为只读，选择 "Read and write permissions"。不需要任何 secret。
8. **验收（plan §8 阶段 D）**：在公开宣布前用测试 Issue 走通以下场景：合法码发布；非法码拒绝并回复原因；标题与正文中的注入
   （`$(…)`、反引号、`@提及`、Markdown 链接、`### 校准码` 重复栏目）既不被执行也不被回显；两份并发提交不丢失更新；
   非协作者账号创建的 Issue 可正常发布（plan §9 待实测项）。测试数据建议使用不存在的客户端版本号，
   例如 `9999.12.31.0000.0000`。验收后撤销测试数据，或在公开前重建仓库，由用户决定。
9. 公开仓库 60 天无提交时，GitHub 会停用定时工作流（`sweep`）。发现停用后，在 Actions 页重新启用。
