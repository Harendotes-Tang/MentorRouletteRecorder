pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// ① 选择区服和网卡: the game must be closed, the region and the adapter are the
// Collector's own override and adapter choice, 开始采证 starts the round. The
// optional checks of the interface fold out beneath.
EvidenceStep {
    id: step

    required property EvidenceSession session
    signal openDataSettingsRequested()

    property bool preChecksOpen: false

    readonly property string gameTone: !step.session.npcapInstalled ? "bad"
                                       : step.session.gameRunning && step.session.idle ? "warn" : "ok"
    readonly property string gameText: !step.session.npcapInstalled ? qsTr("未安装 Npcap，暂时不能采证")
                                       : step.session.gameRunning && step.session.idle ? qsTr("游戏正在运行：先完全退出游戏，再开始采证")
                                       : qsTr("游戏未运行，可以开始")
    readonly property bool lockFields: step.session.active || Runtime.app.capturing || Runtime.app.captureCommandBusy

    number: 1
    title: step.session.stepTitles[0]

    // `.ev-check`
    RowLayout {
        Layout.fillWidth: true
        spacing: 8
        StatusLed { tone: step.gameTone }
        Text {
            objectName: "evidenceGameCheck"
            textFormat: Text.PlainText
            text: step.gameText
            color: Theme.toneColor(step.gameTone)
            font.pixelSize: Theme.fs(13)
            font.weight: Font.DemiBold
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }
        AppButton {
            objectName: "evidenceRecheckGameButton"
            visible: step.gameTone === "warn"
            variant: "ghost"
            compact: true
            text: qsTr("已经退出了，重新检测")
            enabled: !Runtime.app.captureCommandBusy
            onClicked: Runtime.app.rescanGame()
        }
    }

    // A round that did not complete: the Collector's sentence, a failed command,
    // and the code - the code itself only for a maintainer, or when there is no
    // sentence to read instead (K2-4).
    Text {
        Layout.fillWidth: true
        visible: step.session.phase === "failed" && !!step.session.status.message
        textFormat: Text.PlainText
        text: step.session.status.message
              ? Theme.live(Runtime.fmt.collectorText(step.session.status.message_ref, step.session.status.message)) : ""
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }
    Text {
        Layout.fillWidth: true
        visible: Runtime.app.validationError.length > 0
        textFormat: Text.PlainText
        text: qsTr("采证操作失败：") + Runtime.app.validationError
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }
    Text {
        objectName: "evidenceFailureCode"
        Layout.fillWidth: true
        visible: step.session.phase === "failed" && !!step.session.status.error_code
                 && (step.session.technical || !step.session.status.message)
        textFormat: Text.PlainText
        text: step.session.technical ? qsTr("错误码：") + step.session.status.error_code
                                     : Runtime.app.errorText("", step.session.status.error_code)
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }
    Text {
        Layout.fillWidth: true
        visible: step.session.formalRecording
        textFormat: Text.PlainText
        text: qsTr("当前正在正式记录，不能切换为采证。已有版本的校准核实请用「工具 › 共享校准证据」。")
        color: Theme.orangeText
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.Wrap
    }
    // The maintainer's view keeps the trace of a failed round here (K2-4).
    Text {
        Layout.fillWidth: true
        visible: step.session.technical && step.session.phase === "failed" && !!step.session.status.trace_path
        textFormat: Text.PlainText
        text: qsTr("Trace：%1\nSHA256 文件：%2\nSHA256：%3")
              .arg(Runtime.fmt.foldUserPath(step.session.status.trace_path || "") || Runtime.fmt.dash())
              .arg(Runtime.fmt.foldUserPath(step.session.status.sha256_path || "") || Runtime.fmt.dash())
              .arg(step.session.status.sha256 || Runtime.fmt.dash())
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        font.family: Theme.monoFamily
        wrapMode: Text.WrapAnywhere
    }

    // `.ev-form`: label column, field column at most 360 px
    GridLayout {
        Layout.fillWidth: true
        columns: 2
        columnSpacing: 16
        rowSpacing: 10

        FieldLabel { text: qsTr("区服"); Layout.alignment: Qt.AlignTop; Layout.topMargin: 8 }
        ColumnLayout {
            Layout.fillWidth: true
            Layout.maximumWidth: 360
            spacing: 4
            CaptureRegionSelector {
                objectName: "evidenceRegionControl"
                Layout.fillWidth: true
                interactionAllowed: !step.lockFields
            }
            EvidenceHint { text: qsTr("和界面语言分开设置；监听或采证期间不能修改") }
        }

        FieldLabel { text: qsTr("网络适配器"); Layout.alignment: Qt.AlignTop; Layout.topMargin: 8 }
        StyledComboBox {
            id: adapterBox
            objectName: "evidenceAdapterControl"
            Layout.fillWidth: true
            Layout.maximumWidth: 360
            model: step.session.adapterChoices
            textRole: "label"
            valueRole: "adapter_id"
            // Qt Quick Controls writes currentIndex before it emits activated, which
            // destroys an inline binding; the Binding keeps the box on the adapter
            // StartCaptureValidation will actually be sent for.
            Binding {
                target: adapterBox
                property: "currentIndex"
                value: step.session.adapterIndex(Runtime.app.captureAdapterId)
                restoreMode: Binding.RestoreNone
            }
            enabled: !step.lockFields
            onActivated: function(index) {
                Runtime.app.captureAdapterId = step.session.adapterChoices[index].adapter_id
            }
            Accessible.name: qsTr("采证网络适配器")
        }
    }

    // `.ev-acts`
    Flow {
        Layout.fillWidth: true
        spacing: 12
        AppButton {
            // The one action button of the round carries the test name while the
            // round has not started; 停止采证 takes it over once it has.
            objectName: step.session.idle ? "templateObservationActionButton" : "evidenceStartButton"
            text: Runtime.app.captureCommandBusy ? qsTr("处理中…") : qsTr("开始采证")
            variant: "primary"
            iconName: "radio"
            enabled: step.session.canStart
            onClicked: step.session.start()
        }
        AppButton {
            objectName: "evidencePreChecksButton"
            variant: "ghost"
            text: qsTr("采证前可选检查") + "  " + step.session.preChecksDone + " / " + step.session.preCheckDefs.length
            iconName: "chevron-right"
            iconAfterText: true
            onClicked: step.preChecksOpen = !step.preChecksOpen
            Accessible.description: step.preChecksOpen ? qsTr("收起") : qsTr("展开")
        }
    }

    // `.ev-prel`
    InsetBox {
        Layout.fillWidth: true
        visible: step.preChecksOpen
        implicitHeight: preChecks.implicitHeight + 24
        ColumnLayout {
            id: preChecks
            anchors.fill: parent
            anchors.margins: 12
            spacing: 6
            Repeater {
                model: step.session.preCheckDefs
                delegate: SelectionCheckBox {
                    required property var modelData
                    Layout.fillWidth: true
                    text: modelData.label
                    checked: !!step.session.preChecks[modelData.key]
                    onToggled: step.session.togglePreCheck(modelData.key)
                }
            }
            EvidenceHint { text: qsTr("测成就设置前先记下原值，测完恢复；不方便就跳过，回传时注明。") }
            AppButton {
                objectName: "evidenceBackupButton"
                text: qsTr("去「设置 › 数据」备份")
                compact: true
                onClicked: step.openDataSettingsRequested()
            }
        }
    }
}
