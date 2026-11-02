pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 桌面验证取证 (maintainer tools): adapter and region overrides, the five
// validation markers (validationMarker_<code>, driven by main.cpp's keyboard
// check), the evidence counters and the saved trace. The player's guided
// version of the same round is evidence/EvidencePage.qml (新区服采证).
Card {
    id: card
    objectName: "validationEvidenceCard"

    readonly property var validation: Runtime.app.validationStatus

    readonly property var adapterChoices: {
        const choices = [{ adapter_id: "", label: qsTr("自动选择（唯一推荐网卡）") }]
        for (let index = 0; index < Runtime.app.captureAdapters.length; ++index) {
            const row = Runtime.app.captureAdapters[index]
            const name = row.friendly_name || row.description || qsTr("未命名适配器")
            choices.push({
                adapter_id: row.adapter_id,
                label: (row.recommended ? qsTr("%1 · %2 · 推荐") : qsTr("%1 · %2")).arg(name).arg(row.adapter_id)
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
        CardKicker { text: qsTr("桌面验证取证") }
        Item { Layout.fillWidth: true }
        Tag {
            text: card.validationStateLabel()
            variant: Runtime.app.validationState === "FAILED" ? "danger" : "ink"
        }
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
              ? qsTr("点击标题行的按钮开始或停止。")
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

    Text {
        Layout.fillWidth: true
        visible: !!card.validation.error_code
        text: qsTr("错误码：") + card.validation.error_code
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
                objectName: "validationMarker_" + modelData.code
                text: modelData.label
                enabled: Runtime.app.validationMarkerEnabled
                onClicked: Runtime.app.addCaptureValidationMarker(modelData.code)
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
        text: qsTr("Trace：%1\nSHA256 文件：%2\nSHA256：%3")
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
}
