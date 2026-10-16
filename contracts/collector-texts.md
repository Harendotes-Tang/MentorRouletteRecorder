# 采集服务文字目录 / Collector text catalogue

本文件说明采集服务（Collector）发给桌面端的、面向玩家的句子如何按桌面端的界面语言显示。
目录本身是 [collector-texts.json](collector-texts.json)，它是这些句子简体中文措辞的**唯一来源**。

## 1. 为什么需要目录

桌面端可在简体中文、繁體中文、English、日本語之间即时切换（docs/ui-design.md §4.5.3）。
采集服务的错误说明、捕获提示、校准阻塞原因、导入行的警告等句子由采集服务生成；
如果采集服务只发送写好的中文句子，切换语言后这些句子仍是中文。

因此采集服务对每一句话同时发送两份：

* **原有字段**中的简体中文句子（`message`、`hint`、`blockers` 等），与此前逐字相同，
  供不认识目录的旧版桌面端显示；
* **配套的 `*_ref` 字段**中的 `$defs/TextRef`：目录中的键与各占位符的取值。
  新版桌面端据此用 `tr()` 生成界面语言的句子。

## 2. `$defs/TextRef`

```json
{ "key": "import.row.missing_duty", "args": { "row": 12 } }
```

* `key`：目录中的键，以点分隔的小写下划线单词，最长 96 个字符。
* `args`：每个占位符一个取值，按名称对应；句子没有占位符时省略。最多 8 个。
  取值的种类由目录声明：

| 种类 | 线上取值 | 简体中文句子中的写法 | 桌面端的显示 |
|---|---|---|---|
| `number` | 整数 | 十进制数字，不分组 | 同左 |
| `text` | 字符串 | 原样 | 原样：文件名、路径、字段路径、版本号、记号、玩家自己写的文字 |
| `duty` | 字符串（国服客户端的副本名） | 原样 | 界面语言的官方副本名（`GameNames`），不认识的名称原样显示 |
| `job` | 字符串（国服客户端的职业名） | 原样 | 界面语言的官方职业名 |
| `roulette` | 字符串（国服客户端的随机任务名） | 原样 | 界面语言的官方随机任务名 |
| `clock` | `UtcTimestamp` | 本机时区的 `HH:mm` | 本机时区的 `HH:mm` |
| `text_ref` | 嵌套的 `TextRef` | 嵌套句子的简体中文 | 嵌套句子的界面语言 |

配套字段的命名：单个句子为 `<字段>_ref`（`message_ref`、`hint_ref`、`label_ref`……），
句子列表为 `<单数>_refs`（`blocker_refs`、`warning_refs`……）。
列表形式的配套字段是 `$defs/TextRefList`，与中文列表**等长、同序**，
某一项没有目录键时该项为 `null`。

### 2.1 带配套字段的位置

| 位置 | 中文字段 | 配套字段 |
|---|---|---|
| `$defs/ErrorPayload` | `message` | `message_ref` |
| `$defs/CaptureStatus` | `hint` | `hint_ref` |
| `$defs/CollectorStatus` | `warnings` | `warning_refs` |
| `$defs/CollectorStatus.npcap` | `install_hint` | `install_hint_ref` |
| `ListCaptureAdapters` 应答 | `install_hint` | `install_hint_ref` |
| `$defs/ProtocolProfileStatus` | `message` | `message_ref` |
| `$defs/CaptureValidationStatus` | `message` | `message_ref` |
| `$defs/CalibrationStatus` | `blockers` | `blocker_refs` |
| `$defs/CalibrationEvent` | `label` | `label_ref` |
| `$defs/UpdateDownload` | `message` | `message_ref` |
| `ImportCalibrationCode` 应答 | `message` | `message_ref` |
| `$defs/RunImportPreview.rows[]` | `errors` / `warnings` | `error_refs` / `warning_refs` |
| `$defs/SpeechVoiceOption` | `label` | `label_ref` |

桌面端不显示的文字不带配套字段：`LiveEvent.message`、`SharedCalibrationCriterion.reason`、
`ParserErrorEntry.message`（英文，仅维护者可见）、`CheckDatabaseIntegrity.detail`（SQLite 原文）。
`CandidateHypothesis.label` 与协议档案中的说明来自档案文件本身，不属于本目录。
脱敏诊断报告只含中文字段，不含配套字段。

### 2.2 标准错误中的拒绝启动说明

采集服务拒绝启动时，标准错误的最后一行仍是 `ERR_XXX: <中文说明>`（`contracts/error-codes.md`）。
说明出自目录时，其**前一行**为

```text
MR-TEXT {"key":"startup.db_locked","args":{"path":"D:\\MentorRecorder\\data\\mentor.db"}}
```

即前缀 `MR-TEXT`、一个空格，再加一个单行的 `$defs/TextRef` JSON。
旧版桌面端只读最后一行，不受影响；新版桌面端取最后一个 `MR-TEXT` 行，能显示时以界面语言显示，否则仍显示最后一行的中文。

## 3. 桌面端的显示规则

`src/Desktop/cpp/CollectorText.*` 负责显示：

1. 有 `*_ref` 且桌面端认识该键、每个取值都能读出时，显示界面语言的句子。
   简体中文时显示的正是采集服务的中文原句。
2. 其余情况一律显示原有字段中的中文：没有 `*_ref`（旧版采集服务）、
   不认识的键（更新的采集服务）、缺少或类型不符的取值、列表长度不一致。
3. 句子以 `LiveText` 或 `Theme.live(...)` 保存生成方式，切换界面语言后立即重新生成。

译文位于 `src/Desktop/i18n/*.ts` 的 `CollectorText` 上下文，以目录键作为消歧注释；
桌面端的表格 `src/Desktop/cpp/CollectorTextCatalog.g.cpp` 由生成器写出，
其中的 `QT_TRANSLATE_NOOP3` 让 `lupdate`（构建目标 `update_translations`）收录每一句的中文原文。

## 4. 目录格式

```json
{
  "version": 1,
  "texts": {
    "ipc.request.not_json": "请求不是合法的 JSON。",
    "import.row.missing_duty": {
      "zh": "第 {row} 行缺少副本名称。",
      "args": { "row": "number" },
      "note": "可选：给译者的说明。"
    }
  }
}
```

* 没有占位符的句子可以直接写成字符串。
* `zh` 中的每个 `{名称}` 都必须是已声明的参数，每个参数恰好出现一次；不允许其他花括号。
* `zh` 为单行，不以空白开头或结尾。

## 5. 新增或修改一句话

1. 在 `collector-texts.json` 中新增或修改条目。**修改已有键的中文会让三种译文失效**，
   措辞变了而含义不变时也要同步译文；含义变了时改用新键。
2. 运行 `python tools/collector-texts/generate.py`，写出
   `src/Collector/Contracts/Texts/CollectorTexts.g.cs`（每个键一个类型化工厂方法）与
   `src/Desktop/cpp/CollectorTextCatalog.g.cpp`。
3. 在采集服务中以 `CollectorTexts.<名称>(...)` 取得 `UserText`，交给
   `CollectorException` 或相应的状态对象；序列化时同时写出中文与 `*_ref`。
4. 构建 `update_translations`，在 `src/Desktop/i18n/*.ts` 中补齐三种语言的译文，
   沿用这些文件中已有的用词。

`tools/collector-texts/test_generate.py`（`scripts/verify.ps1` 的工具自测）检查：
目录合法、两个生成文件是最新的、每个键都被采集服务使用、三种译文齐全且占位符与中文一致。
