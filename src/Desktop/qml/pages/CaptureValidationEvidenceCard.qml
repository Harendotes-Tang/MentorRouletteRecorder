import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 桌面验证取证 (maintainer tools): adapter and region overrides, the five
// validation markers (validationMarker_<code>, driven by main.cpp's keyboard
// check), the evidence counters and the saved trace.
Card {
    id: card
    objectName: guidedMode ? "templateObservationGuideCard" : "validationEvidenceCard"

    readonly property var validation: App.validationStatus
    property bool guidedMode: false
    readonly property var evidence: typeof CalibrationEvidence !== "undefined" ? CalibrationEvidence : null
    // 玩家从「帮助适配其他区服」打开的这张卡片不显示错误码、网卡的内部编号和文件校验值；
    // 这些只给维护者看（审查 K2-4，2026-10-03 §四 第 5 条）。
    readonly property bool technical: !card.guidedMode || App.maintainerToolsVisible

    readonly property var adapterChoices: {
        const choices = [{ adapter_id: "", label: qsTr("自动选择（唯一推荐网卡）") }]
        for (let index = 0; index < App.captureAdapters.length; ++index) {
            const row = App.captureAdapters[index]
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
        return (value === undefined || value === null) ? Fmt.dash() : Fmt.count(value)
    }

    function validationStateLabel() {
        if (card.validation.reason === "CANCELLED")
            return qsTr("已取消等待")
        switch (App.validationState) {
        case "IDLE": return qsTr("尚未开始")
        case "WAITING": return qsTr("等待安全候选")
        case "RECORDING": return qsTr("正在取证")
        case "STOPPING": return qsTr("正在停止并排空")
        case "COMPLETED": return App.validationSaved ? qsTr("已保存") : qsTr("已结束，文件不完整")
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
            variant: App.validationState === "FAILED" ? "danger" : "ink"
        }
    }

    Text {
        Layout.fillWidth: true
        visible: card.guidedMode
        text: qsTr("1. 先回到游戏标题画面，确认下方区服和网卡，点击开始采证，再重新登录。\n2. 排一次随机任务；排队、匹配弹窗出现、进本、通关、离本时，依次点击对应标记。\n3. 离本后停止采证，等到“已保存”再预览摘要。仅提交候选线索，不代表此区服已经适配。")
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
            text: App.validationState === "WAITING" ? qsTr("取消等待")
                  : App.validationActive ? qsTr("停止采证")
                  : App.capturing ? qsTr("切换为采证") : qsTr("开始采证")
            variant: "primary"
            enabled: App.templateObservationActionEnabled
            onClicked: App.toggleTemplateObservation()
        }
        AppButton {
            objectName: "previewTemplateObservationButton"
            text: qsTr("预览候选摘要")
            enabled: !!card.evidence && !card.evidence.busy && App.validationSaved && !App.validationActive
            onClicked: card.evidence.previewTemplateObservation()
        }
    }

    Text {
        Layout.fillWidth: true
        visible: card.guidedMode && App.capturing && !App.validationActive
        text: App.protocolProfileStatus === "VERIFIED"
              ? qsTr("当前正在正式记录，不能切换为候选采证。已有模板的校准核实请使用上方“共享校准证据”。")
              : qsTr("点击“切换为采证”会停止当前被动监听，改为只保存本机元数据；停止采证后自动监听可恢复。")
        color: Theme.orangeText
        wrapMode: Text.WordWrap
        font.pixelSize: Theme.fs(12)
    }

    Text {
        Layout.fillWidth: true
        text: App.captureStatus.candidate_validation_enabled
              ? qsTr("候选验证仅写入独立观测账本；以下是单独的桌面验证取证工具。")
              // Listening on a build without a usable profile records nothing (review K2-4).
              : App.capturing && !App.validationActive && App.protocolProfileStatus === "VERIFIED"
              ? qsTr("当前正在正式记录；以下为最近一次验证取证证据。")
              : App.validationNotice
        color: Theme.orangeText
        font.pixelSize: Theme.fs(13)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    Text {
        Layout.fillWidth: true
        // message_ref renders the Collector's sentence in the interface language.
        text: (card.validation.message
               ? Theme.live(Fmt.collectorText(card.validation.message_ref, card.validation.message))
               : "") || (App.validationStatusLoaded
              ? card.guidedMode ? qsTr("点击上方按钮开始或停止采证。")
                               : qsTr("点击标题行的按钮开始或停止。")
              : qsTr("正在读取采集器验证能力…"))
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(13)
        wrapMode: Text.WordWrap
    }

    Text {
        Layout.fillWidth: true
        visible: App.validationState === "WAITING"
        text: qsTr("等待期间可点击“取消等待”；如需换网卡，请先取消，重新选择后再开始。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    Text {
        Layout.fillWidth: true
        visible: App.validationError.length > 0
        text: qsTr("验证操作失败：") + App.validationError
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
                             : App.errorText("", card.validation.error_code)
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
                value: card.adapterIndex(App.captureAdapterId)
                restoreMode: Binding.RestoreNone
            }
            enabled: !App.validationActive && !App.capturing && !App.captureCommandBusy
            onActivated: function(index) {
                App.captureAdapterId = card.adapterChoices[index].adapter_id
            }
            Accessible.name: qsTr("验证取证网络适配器")
            Accessible.description: qsTr("选择会把不透明 adapter_id 原样发给采集器；等待或取证中不可修改。")
        }

        FieldLabel { text: qsTr("区服") }
        CaptureRegionSelector {
            id: regionOverride
            objectName: "validationRegionSettingControl"
            Layout.preferredWidth: 150
            interactionAllowed: !App.validationActive && !App.capturing && !App.captureCommandBusy
        }
    }

    Text {
        Layout.fillWidth: true
        text: qsTr("区服“自动”由采集器从安装路径推断；装在非默认目录的国服客户端可能识别为未知，"
                   + "此时取证与验证会被拒绝，请在此显式指定。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        wrapMode: Text.WordWrap
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
                enabled: App.validationMarkerEnabled
                onClicked: card.guidedMode ? App.addTemplateObservationMarker(modelData.code)
                                          : App.addCaptureValidationMarker(modelData.code)
                Accessible.name: qsTr("添加验证标记：%1").arg(modelData.label)
            }
        }
    }

    Text {
        Layout.fillWidth: true
        visible: App.validationFeedback.length > 0
        text: App.validationFeedback
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
              ? qsTr("采证文件：%1").arg(Fmt.foldUserPath(card.validation.trace_path || "") || Fmt.dash())
              : qsTr("Trace：%1\nSHA256 文件：%2\nSHA256：%3")
                .arg(Fmt.foldUserPath(card.validation.trace_path || "") || Fmt.dash())
                .arg(Fmt.foldUserPath(card.validation.sha256_path || "") || Fmt.dash())
                .arg(card.validation.sha256 || Fmt.dash())
        color: App.validationSaved ? Theme.textPrimary : Theme.orangeText
        font.pixelSize: Theme.fs(11)
        font.family: Theme.monoFamily
        wrapMode: Text.WrapAnywhere
    }

    RowLayout {
        Layout.fillWidth: true
        visible: !!card.validation.trace_path
        AppButton {
            text: qsTr("打开所在目录")
            enabled: !App.usingMockData
            onClicked: App.openCaptureValidationFolder()
        }
        Text {
            Layout.fillWidth: true
            visible: App.usingMockData
            text: qsTr("当前是截图用模拟路径，不会当作可打开的真实取证。")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }
}
