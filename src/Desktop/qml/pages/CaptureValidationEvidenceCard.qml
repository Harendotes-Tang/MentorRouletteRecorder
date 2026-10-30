pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 桌面验证取证 (maintainer tools): adapter and region overrides, the five
// validation markers (validationMarker_<code>, driven by main.cpp's keyboard
// check), the evidence counters and the saved trace.
Card {
    id: card
    objectName: guidedMode ? "templateObservationGuideCard" : "validationEvidenceCard"

    readonly property var validation: Runtime.app.validationStatus
    property bool guidedMode: false
    readonly property var evidence: typeof Runtime.calibrationEvidence !== "undefined" ? Runtime.calibrationEvidence : null
    // 玩家从「帮助适配其他区服」打开的这张卡片不显示错误码、网卡的内部编号和文件校验值；
    // 这些只给维护者看（审查 K2-4，2026-10-03 §四 第 5 条）。
    readonly property bool technical: !card.guidedMode || Runtime.app.maintainerToolsVisible

    readonly property var adapterChoices: {
        const choices = [{ adapter_id: "", label: qsTr("自动选择（唯一推荐网卡）") }]
        for (let index = 0; index < Runtime.app.captureAdapters.length; ++index) {
            const row = Runtime.app.captureAdapters[index]
            const name = row.friendly_name || row.description || qsTr("未命名适配器")
            choices.push({
                adapter_id: row.adapter_id,
                label: !card.technical
                       ? (row.recommended ? qsTr("%1 · 推荐").arg(name) : name)
                       : (row.recommended ? qsTr("%1 · %2 · 推荐") : qsTr("%1 · %2"))
                         .arg(name)
                         .arg(row.adapter_id)
            })
        }
        return choices
    }

    function adapterIndex(adapterId) {
        for (let index = 0; index < card.adapterChoices.length; ++index) {
            if (card.adapterChoices[index].adapter_id === adapterId)
                return index
        }
        return 0
    }

    function validationCounter(key) {
        const value = card.validation[key]
        return (value === undefined || value === null) ? Runtime.fmt.dash() : Runtime.fmt.count(value)
    }

    function validationStateLabel() {
        if (card.validation.reason === "CANCELLED")
            return qsTr("已取消等待")
        switch (Runtime.app.validationState) {
        case "IDLE": return qsTr("尚未开始")
        case "WAITING": return qsTr("等待安全候选")
        case "RECORDING": return qsTr("正在取证")
        case "STOPPING": return qsTr("正在停止并排空")
        case "COMPLETED": return Runtime.app.validationSaved ? qsTr("已保存") : qsTr("已结束，文件不完整")
        case "FAILED": return qsTr("失败")
        default: return qsTr("状态未知")
        }
    }

    padding: 20
    spacing: 10

    RowLayout {
        Layout.fillWidth: true
        CardKicker { text: card.guidedMode ? qsTr("新区服采证步骤") : qsTr("桌面验证取证") }
        Item { Layout.fillWidth: true }
        Tag {
            text: card.validationStateLabel()
            variant: Runtime.app.validationState === "FAILED" ? "danger" : "ink"
        }
    }

    Text {
        Layout.fillWidth: true
        visible: card.guidedMode
        text: qsTr("1. 先退出游戏，选好实际区服和网卡（繁中服选 TC），再点“开始采证”。\n2. 启动游戏，等这里显示“正在取证”后再登录；若提示重启，须完全退出游戏后重开。\n3. 离本后停止采证，等到“已保存”再取文件。本轮只收集元数据，不会新增正式记录。")
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(13)
        wrapMode: Text.WordWrap
    }

    Flow {
        Layout.fillWidth: true
        visible: card.guidedMode
        spacing: 10
        AppButton {
            objectName: "templateObservationActionButton"
            text: Runtime.app.validationState === "WAITING" ? qsTr("取消等待")
                  : Runtime.app.validationActive ? qsTr("停止采证")
                  : Runtime.app.capturing ? qsTr("切换为采证") : qsTr("开始采证")
            variant: "primary"
            enabled: Runtime.app.templateObservationActionEnabled
            onClicked: Runtime.app.toggleTemplateObservation()
        }
        AppButton {
            objectName: "previewTemplateObservationButton"
            text: qsTr("预览候选摘要")
            enabled: !!card.evidence && !card.evidence.busy && Runtime.app.validationSaved && !Runtime.app.validationActive
            onClicked: card.evidence.previewTemplateObservation()
        }
    }

    Text {
        Layout.fillWidth: true
        visible: card.guidedMode && Runtime.app.capturing && !Runtime.app.validationActive
        text: Runtime.app.protocolProfileStatus === "VERIFIED"
              ? qsTr("当前正在正式记录，不能切换为候选采证。已有模板的校准核实请使用上方“共享校准证据”。")
              : qsTr("点击“切换为采证”会停止当前被动监听，改为只保存本机元数据；停止采证后自动监听可恢复。")
        color: Theme.orangeText
        wrapMode: Text.WordWrap
        font.pixelSize: Theme.fs(12)
    }

    Text {
        Layout.fillWidth: true
        text: Runtime.app.captureStatus.candidate_validation_enabled
              ? qsTr("候选验证仅写入独立观测账本；以下是单独的桌面验证取证工具。")
              // Listening on a build without a usable profile records nothing (review K2-4).
              : Runtime.app.capturing && !Runtime.app.validationActive && Runtime.app.protocolProfileStatus === "VERIFIED"
              ? qsTr("当前正在正式记录；以下为最近一次验证取证证据。")
              : Runtime.app.validationNotice
        color: Theme.orangeText
        font.pixelSize: Theme.fs(13)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    Text {
        Layout.fillWidth: true
        // message_ref renders the Collector's sentence in the interface language.
        text: (card.validation.message
               ? Theme.live(Runtime.fmt.collectorText(card.validation.message_ref, card.validation.message))
               : "") || (Runtime.app.validationStatusLoaded
              ? card.guidedMode ? qsTr("点击上方按钮开始或停止采证。")
                               : qsTr("点击标题行的按钮开始或停止。")
              : qsTr("正在读取采集器验证能力…"))
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(13)
        wrapMode: Text.WordWrap
    }

    Text {
        Layout.fillWidth: true
        visible: Runtime.app.validationState === "WAITING"
        text: qsTr("等待期间可点击“取消等待”；如需换网卡，请先取消，重新选择后再开始。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    Text {
        Layout.fillWidth: true
        visible: Runtime.app.validationError.length > 0
        text: qsTr("验证操作失败：") + Runtime.app.validationError
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    // A player reads the Collector's sentence above; the code is a maintainer's. Without a
    // sentence the player still learns that it failed, never the token.
    Text {
        Layout.fillWidth: true
        visible: !!card.validation.error_code && (card.technical || !card.validation.message)
        text: card.technical ? qsTr("错误码：") + card.validation.error_code
                             : Runtime.app.errorText("", card.validation.error_code)
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 10

        FieldLabel { text: qsTr("网络适配器") }
        StyledComboBox {
            id: validationAdapter
            Layout.fillWidth: true
            Layout.maximumWidth: 620
            model: card.adapterChoices
            textRole: "label"
            valueRole: "adapter_id"
            // Qt Quick Controls writes currentIndex before it emits activated,
            // which permanently destroys an inline binding; a rescan that
            // reorders the list would then leave the box naming one adapter
            // while StartCaptureValidation is sent for another.
            Binding {
                target: validationAdapter
                property: "currentIndex"
                value: card.adapterIndex(Runtime.app.captureAdapterId)
                restoreMode: Binding.RestoreNone
            }
            enabled: !Runtime.app.validationActive && !Runtime.app.capturing && !Runtime.app.captureCommandBusy
            onActivated: function(index) {
                Runtime.app.captureAdapterId = card.adapterChoices[index].adapter_id
            }
            Accessible.name: qsTr("验证取证网络适配器")
            Accessible.description: qsTr("选择会把不透明 adapter_id 原样发给采集器；等待或取证中不可修改。")
        }

        FieldLabel { text: qsTr("区服") }
        CaptureRegionSelector {
            id: regionOverride
            objectName: "validationRegionSettingControl"
            Layout.preferredWidth: 150
            interactionAllowed: !Runtime.app.validationActive && !Runtime.app.capturing && !Runtime.app.captureCommandBusy
        }
    }

    Text {
        Layout.fillWidth: true
        text: qsTr("区服与界面语言分别设置。请选择实际区服：繁中服为 TC，国际服为 GLOBAL；监听或采证期间不可修改。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        wrapMode: Text.WordWrap
    }

    Text {
        objectName: "templateObservationMarkerGuide"
        Layout.fillWidth: true
        visible: card.guidedMode
        text: qsTr("开始排队 → 已排队；匹配弹窗出现 → 已匹配；读条结束进本 → 已进入；出现通关提示 → 已胜利；离本回到外面 → 已离开。尽量在事件发生后 3 秒内点击；漏按、点晚或未通关要如实记录，不要事后补点。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.Wrap
    }

    Flow {
        Layout.fillWidth: true
        spacing: 8
        Repeater {
            model: [
                { code: "queued", label: qsTr("已排队") },
                { code: "pop", label: qsTr("已匹配") },
                { code: "entered", label: qsTr("已进入") },
                { code: "victory", label: qsTr("已胜利") },
                { code: "left", label: qsTr("已离开") }
            ]
            delegate: AppButton {
                required property var modelData
                objectName: (card.guidedMode ? "templateObservationMarker_" : "validationMarker_") + modelData.code
                text: modelData.label
                enabled: Runtime.app.validationMarkerEnabled
                onClicked: card.guidedMode ? Runtime.app.addTemplateObservationMarker(modelData.code)
                                          : Runtime.app.addCaptureValidationMarker(modelData.code)
                Accessible.name: qsTr("添加验证标记：%1").arg(modelData.label)
            }
        }
    }

    Text {
        Layout.fillWidth: true
        visible: Runtime.app.validationFeedback.length > 0
        text: Runtime.app.validationFeedback
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(12)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    Flow {
        Layout.fillWidth: true
        spacing: 18
        Repeater {
            model: [
                qsTr("报文 %1").arg(card.validationCounter("message_count")),
                qsTr("已写入标记 %1").arg(card.validationCounter("marker_count")),
                qsTr("解码失败 %1").arg(card.validationCounter("decode_error_count")),
                qsTr("队列丢弃 %1").arg(card.validationCounter("queue_dropped"))
            ]
            delegate: Text {
                required property string modelData
                text: modelData
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                font.family: Theme.figureFamily
                font.weight: Theme.figureWeight(false)
                font.features: ({ "tnum": 1 })
            }
        }
    }

    Text {
        Layout.fillWidth: true
        visible: !!card.validation.trace_path
        // Folded to %USERPROFILE% exactly as the Collector folds it before
        // writing a log line: this is the page users screenshot when filing an
        // issue (docs/privacy-boundary.md section 5). The unfolded value stays
        // with openCaptureValidationFolder().
        text: !card.technical
              ? qsTr("采证文件：%1").arg(Runtime.fmt.foldUserPath(card.validation.trace_path || "") || Runtime.fmt.dash())
              : qsTr("Trace：%1\nSHA256 文件：%2\nSHA256：%3")
                .arg(Runtime.fmt.foldUserPath(card.validation.trace_path || "") || Runtime.fmt.dash())
                .arg(Runtime.fmt.foldUserPath(card.validation.sha256_path || "") || Runtime.fmt.dash())
                .arg(card.validation.sha256 || Runtime.fmt.dash())
        color: Runtime.app.validationSaved ? Theme.textPrimary : Theme.orangeText
        font.pixelSize: Theme.fs(11)
        font.family: Theme.monoFamily
        wrapMode: Text.WrapAnywhere
    }

    RowLayout {
        Layout.fillWidth: true
        visible: !!card.validation.trace_path
        AppButton {
            text: qsTr("打开所在目录")
            enabled: !Runtime.app.usingMockData
            onClicked: Runtime.app.openCaptureValidationFolder()
        }
        Text {
            Layout.fillWidth: true
            visible: Runtime.app.usingMockData
            text: qsTr("当前是截图用模拟路径，不会当作可打开的真实取证。")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }

    ColumnLayout {
        objectName: "templateObservationDetailedGuide"
        Layout.fillWidth: true
        visible: card.guidedMode
        spacing: 10

        CardKicker { text: qsTr("先确认数据可读") }
        Text {
            objectName: "templateObservationReadinessGuide"
            Layout.fillWidth: true
            text: qsTr("登录后正常活动 30～60 秒，查看上方采证“报文”是否增加。一直为 0 或“解码失败”快速增加时，先截图、停止并导出诊断，不必继续排本。\n优先采指导者随机；没有资格可采普通随机作对照，并注明类型。先回传一轮，确认可用后再补一轮。单次最长 2 小时，含等待时间。")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.Wrap
        }

        CardKicker { text: qsTr("停止后保存这些文件") }
        Text {
            objectName: "templateObservationFilesGuide"
            Layout.fillWidth: true
            text: qsTr("1. 点“停止采证”，等“已保存”，截图保存状态和四项计数。\n2. 点“打开所在目录”，复制本轮 trace.jsonl 与 trace.jsonl.sha256；每轮分开保存，不改原文件和文件名。\n3. 在上方“工具”点“导出诊断报告”并保存 JSON。\n失败时有多少文件就回传多少，注明缺失项，不自行补造。采证目录只保留最近 10 份且不超过 7 天，请及时复制到外部文件夹。")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.Wrap
        }

        CardKicker { text: qsTr("摘要和附加文件（可选）") }
        Text {
            objectName: "templateObservationOptionalGuide"
            Layout.fillWidth: true
            text: qsTr("可先“预览候选摘要”；摘要不足或预览失败，不影响回传上述文件。预览只针对当前最近一轮，需在下一轮或重开软件前处理。\n“复制摘要并打开提交页”会复制完整 JSON 并打开 GitHub，页面可能预填摘要。可将复制内容原样保存为 JSON 回传，无需发布 Issue；不想打开 GitHub 可跳过摘要。\n无法识别游戏版本时，可附 game 目录内的 ffxivgame.ver；启动或采证异常时，可附数据目录 logs 内对应时间的 collector-*.log。")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.Wrap
        }

        CardKicker { text: qsTr("回传时一起说明") }
        Text {
            objectName: "templateObservationFeedbackGuide"
            Layout.fillWidth: true
            text: qsTr("将每轮采证文件、诊断 JSON、状态或错误截图及文字回报放在同一 ZIP，私下交给维护者。请写明：软件、游戏和 Windows 版本，测试时间及时区，加速器或 VPN，随机类型、副本、职业，是否中途加入、通关，以及漏按、延迟、断线等情况。\n不需要数据库、游戏程序或 DLL、账号密码和原始抓包；完整 trace 不要贴到公开 Issue。采证完成只代表取得候选线索，仍需维护者验证。")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.Wrap
        }

        CardKicker { text: qsTr("采证前也可检查界面") }
        Text {
            objectName: "templateObservationInterfaceGuide"
            Layout.fillWidth: true
            text: qsTr("已有正式记录可先在“设置 → 数据”备份，备份留本机。可先检查：切换界面语言后区服仍正确；各区成就分开显示且设置互不影响；手动补录能找到对应区服的副本。测试成就设置前记下原值，测试后恢复；不方便修改时可跳过并注明。")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.Wrap
        }
    }
}
