import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 桌面验证取证 (maintainer tools): adapter and region overrides, the five
// validation markers (validationMarker_<code>, driven by main.cpp's keyboard
// check), the evidence counters and the saved trace.
Card {
    id: card
    objectName: "validationEvidenceCard"

    readonly property var validation: App.validationStatus

    readonly property var adapterChoices: {
        const choices = [{ adapter_id: "", label: qsTr("自动选择（唯一推荐网卡）") }]
        for (let index = 0; index < App.captureAdapters.length; ++index) {
            const row = App.captureAdapters[index]
            const name = row.friendly_name || row.description || qsTr("未命名适配器")
            choices.push({
                adapter_id: row.adapter_id,
                label: qsTr("%1 · %2%3")
                       .arg(name)
                       .arg(row.adapter_id)
                       .arg(row.recommended ? qsTr(" · 推荐") : "")
            })
        }
        return choices
    }

    readonly property var regionChoices: [
        { value: null, label: qsTr("自动（按安装路径）") },
        { value: "CN", label: qsTr("国服 CN") },
        { value: "GLOBAL", label: qsTr("国际服 GLOBAL") }
    ]

    function regionIndex(value) {
        for (let index = 0; index < card.regionChoices.length; ++index) {
            if (card.regionChoices[index].value === (value === undefined ? null : value))
                return index
        }
        return 0
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
        CardKicker { text: qsTr("桌面验证取证") }
        Item { Layout.fillWidth: true }
        Tag {
            text: card.validationStateLabel()
            variant: App.validationState === "FAILED" ? "danger" : "ink"
        }
    }

    Text {
        Layout.fillWidth: true
        text: App.captureStatus.candidate_validation_enabled
              ? qsTr("候选验证仅写入独立观测账本；以下是单独的桌面验证取证工具。")
              : App.capturing && !App.validationActive
              ? qsTr("当前正在正式记录；以下为最近一次验证取证证据。")
              : App.validationNotice
        color: Theme.orangeText
        font.pixelSize: Theme.fs(13)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    Text {
        Layout.fillWidth: true
        text: card.validation.message || (App.validationStatusLoaded
              ? qsTr("点击标题行的按钮开始或停止。")
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
        StyledComboBox {
            id: regionOverride
            Layout.preferredWidth: 150
            model: card.regionChoices
            textRole: "label"
            valueRole: "value"
            enabled: App.captureSettingsSupported && !App.validationActive
                     && !App.capturing && !App.captureCommandBusy
            Binding {
                target: regionOverride
                property: "currentIndex"
                value: card.regionIndex(App.captureSettings.region_override)
                restoreMode: Binding.RestoreNone
            }
            onActivated: function(index) {
                App.updateCaptureSetting("region_override", card.regionChoices[index].value)
            }
            Accessible.name: qsTr("区服覆盖")
            Accessible.description: qsTr("默认自动：由采集器从安装路径判断。装在自定义目录时国服可能识别不出，可在此显式指定。")
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
                objectName: "validationMarker_" + modelData.code
                text: modelData.label
                enabled: App.validationMarkerEnabled
                onClicked: App.addCaptureValidationMarker(modelData.code)
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
        text: qsTr("Trace：%1\nSHA256 文件：%2\nSHA256：%3")
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
