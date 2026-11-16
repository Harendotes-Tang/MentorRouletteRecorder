pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// ③ 轮流登录两个角色 (角色识别采证): the five things to do, one under the other, the
// next one highlighted. Each row says what to do in the game and has one button to
// press once it is done; the last two are optional. The steps are taken in turn: a
// button waits until the logins before it are confirmed and read. Nothing is typed,
// and a step names what was done, never a character. The Collector judges the three
// logins and the verdict is shown as soon as it has one.
EvidenceStep {
    id: step

    required property EvidenceSession session

    number: 3
    nameKey: "3_identity"
    title: step.session.stepTitles[2]

    EvidenceHint {
        iconName: "activity"
        text: qsTr("按顺序一步一步来：先在游戏里做完这一步，进入游戏后等十秒左右，再点这一步右边的按钮。换角色时不用关游戏，返回角色选择即可。")
    }

    ColumnLayout {
        objectName: "evidenceIdentitySteps"
        Layout.fillWidth: true
        spacing: 6
        Accessible.role: Accessible.Grouping
        Accessible.name: qsTr("角色识别步骤")

        Repeater {
            model: step.session.identityDefs
            delegate: Rectangle {
                id: row
                required property var modelData
                required property int index
                readonly property var mark: step.session.marks[index]
                // Confirmed, but too early for the Collector to tell who it was.
                readonly property bool retry: step.session.stepNeedsRetry(index)
                readonly property bool done: !!mark && mark !== "skip" && !retry
                readonly property bool skipped: mark === "skip"
                readonly property bool next: step.session.recording && index === step.session.nextMarkIndex
                                             && step.session.stepReachable(index)

                Layout.fillWidth: true
                implicitHeight: rowLayout.implicitHeight + 16
                radius: Theme.radiusM
                color: row.next ? Theme.accentMuted : "transparent"
                border.width: 1
                border.color: row.next ? Theme.accent : Theme.border
                Behavior on color { ColorAnimation { duration: Theme.motionControl } }

                RowLayout {
                    id: rowLayout
                    anchors.fill: parent
                    anchors.margins: 8
                    anchors.leftMargin: 10
                    spacing: 10

                    Rectangle {
                        implicitWidth: 26
                        implicitHeight: 26
                        radius: 13
                        color: row.done ? Theme.green : row.next ? Theme.accent : Theme.surface
                        border.width: 2
                        border.color: row.done ? Theme.green : row.next ? Theme.accent
                                      : row.skipped || row.retry ? Theme.orange : Theme.neutral300
                        Text {
                            anchors.centerIn: parent
                            visible: !row.done
                            textFormat: Text.PlainText
                            text: String(row.index + 1)
                            color: row.next ? Theme.badgeForeground
                                   : row.skipped || row.retry ? Theme.orangeText : Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                            font.family: Theme.figureFamily
                            font.weight: Font.Bold
                        }
                        Image {
                            visible: row.done
                            anchors.centerIn: parent
                            width: 12; height: 12
                            source: Lucide.source("check", Theme.badgeForeground)
                            sourceSize: Qt.size(24, 24)
                            Accessible.ignored: true
                        }
                    }

                    ColumnLayout {
                        Layout.fillWidth: true
                        spacing: 2
                        Text {
                            Layout.fillWidth: true
                            textFormat: Text.PlainText
                            text: row.modelData.optional ? qsTr("%1（可选）").arg(row.modelData.trigger) : row.modelData.trigger
                            color: row.done || row.next ? Theme.textPrimary : Theme.textSecondary
                            font.pixelSize: Theme.fs(13)
                            font.weight: Font.Bold
                            wrapMode: Text.Wrap
                        }
                        Text {
                            Layout.fillWidth: true
                            textFormat: Text.PlainText
                            text: row.retry ? qsTr("没读到数据：确认得太早。在游戏里等十秒左右，再点「重新确认」")
                                  : row.done ? qsTr("已确认 · %1").arg(Runtime.fmt.localTime(row.mark.at))
                                  : row.skipped ? qsTr("已跳过") : row.modelData.detail
                            color: row.skipped || row.retry ? Theme.orangeText : Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                            wrapMode: Text.Wrap
                        }
                    }

                    AppButton {
                        objectName: "templateObservationMarker_" + row.modelData.code
                        visible: !row.done && !row.skipped
                        compact: true
                        variant: row.next || row.retry ? "primary" : "secondary"
                        text: row.retry ? qsTr("重新确认") : row.modelData.label
                        enabled: step.session.markerEnabled(row.index)
                        onClicked: step.session.mark(row.index)
                        Accessible.name: row.modelData.trigger + " · " + row.modelData.label
                    }
                }
            }
        }
    }

    // The verdict, as soon as the three logins are confirmed.
    Rectangle {
        objectName: "evidenceIdentityVerdict"
        Layout.fillWidth: true
        visible: step.session.identityShown
        implicitHeight: verdictRow.implicitHeight + 20
        radius: Theme.radiusM
        color: Theme.insetBackground
        border.width: 1
        border.color: Theme.toneColor(step.session.identityTone)
        RowLayout {
            id: verdictRow
            anchors.fill: parent
            anchors.margins: 10
            spacing: 8
            StatusLed { tone: step.session.identityTone }
            Text {
                objectName: "evidenceIdentityVerdictText"
                Layout.fillWidth: true
                textFormat: Text.PlainText
                text: step.session.identityVerdict
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                font.weight: Font.DemiBold
                wrapMode: Text.Wrap
            }
        }
    }

    // The Collector's acknowledgement of the last step.
    Text {
        Layout.fillWidth: true
        visible: Runtime.app.validationFeedback.length > 0
        textFormat: Text.PlainText
        text: Runtime.app.validationFeedback
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }
    Text {
        objectName: "evidenceIdentityError"
        Layout.fillWidth: true
        visible: Runtime.app.validationError.length > 0
        textFormat: Text.PlainText
        text: qsTr("采证操作失败：") + Runtime.app.validationError
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }

    // `.ev-acts`
    RowLayout {
        Layout.fillWidth: true
        spacing: 12
        AppButton {
            objectName: step.session.recording && step.session.identity ? "templateObservationActionButton" : "evidenceIdentityStopButton"
            text: Runtime.app.captureCommandBusy ? qsTr("处理中…") : qsTr("停止采证")
            enabled: step.session.active && Runtime.app.templateObservationActionEnabled && !Runtime.app.captureCommandBusy
            onClicked: step.session.stop()
        }
        EvidenceHint { text: qsTr("前三步确认后就可以停止；后两步做了更好") }
    }
}
