pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// ② 启动游戏，登录后确认数据可读: what the round is doing right now (waiting for
// the game, recording), how far the 报文 count has come, and the one anomaly a
// player can tell apart - nothing arrives, or decode failures outrun the data.
EvidenceStep {
    id: step

    required property EvidenceSession session

    readonly property bool waiting: step.session.phase === "waiting"
    readonly property string liveTone: step.waiting ? "warn" : "ok"

    number: 2
    title: step.session.stepTitles[1]

    // `.ev-live`
    Rectangle {
        Layout.fillWidth: true
        visible: !(step.session.anomaly && step.session.recording)
        implicitHeight: liveRow.implicitHeight + 24
        radius: Theme.radiusM
        color: step.waiting ? Theme.insetBackground : Theme.greenBackground
        border.width: 1
        border.color: step.waiting ? Theme.insetBorder : Theme.green
        RowLayout {
            id: liveRow
            anchors.fill: parent
            anchors.margins: 12
            spacing: 12
            EvidencePulse {
                Layout.alignment: Qt.AlignTop
                Layout.topMargin: 6
                tone: step.session.active ? step.liveTone : "off"
            }
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 2
                Text {
                    objectName: "evidenceLiveTitle"
                    Layout.fillWidth: true
                    textFormat: Text.PlainText
                    text: step.waiting ? qsTr("等待游戏启动")
                          : step.session.recording ? qsTr("正在取证 · 可以登录了")
                          : step.session.badgeText
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(14)
                    font.weight: Font.Bold
                    wrapMode: Text.Wrap
                }
                EvidenceHint {
                    // The Collector's own sentence when it has one (message_ref).
                    text: step.session.status.message
                          ? Theme.live(Runtime.fmt.collectorText(step.session.status.message_ref, step.session.status.message))
                          : step.waiting ? qsTr("启动游戏后会自动开始取证，看到「正在取证」再登录。")
                                         : qsTr("如果提示重启，完全退出游戏后再重开。")
                }
            }
            AppButton {
                objectName: step.waiting ? "templateObservationActionButton" : "evidenceCancelWaitButton"
                visible: step.waiting
                Layout.alignment: Qt.AlignVCenter
                compact: true
                text: qsTr("取消等待")
                enabled: Runtime.app.templateObservationActionEnabled && !Runtime.app.captureCommandBusy
                onClicked: step.session.stop()
            }
        }
    }

    // `.ev-read`
    ColumnLayout {
        Layout.fillWidth: true
        visible: step.session.recording && !step.session.anomaly
        spacing: 7
        RowLayout {
            Layout.fillWidth: true
            spacing: 12
            FieldLabel { text: qsTr("登录后正常活动 30～60 秒，报文要一直增加"); Layout.fillWidth: true }
            Text {
                objectName: "evidenceMessageCount"
                textFormat: Text.PlainText
                text: qsTr("报文 %1").arg(step.session.countText(step.session.messageCount))
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(15)
                font.family: Theme.figureFamily
                font.weight: Theme.figureWeight(true)
                font.features: ({ "tnum": 1 })
            }
        }
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 6
            radius: 3
            color: Theme.insetBackgroundStrong
            Rectangle {
                width: parent.width * step.session.readProgress
                height: parent.height
                radius: 3
                gradient: Gradient {
                    orientation: Gradient.Horizontal
                    GradientStop { position: 0.0; color: Theme.barFillStart }
                    GradientStop { position: 1.0; color: Theme.barFillEnd }
                }
                Behavior on width { NumberAnimation { duration: Theme.motionSlow; easing.bezierCurve: Theme.curveStandard } }
            }
        }
        RowLayout {
            visible: step.session.readable
            spacing: 6
            EvidenceIcon { name: "clipboard-check"; color: Theme.green; size: 14 }
            Text {
                textFormat: Text.PlainText
                text: qsTr("数据可读，接着排本")
                color: Theme.green
                font.pixelSize: Theme.fs(13)
                font.weight: Font.Bold
            }
        }
    }

    // `.ev-warn`
    Rectangle {
        Layout.fillWidth: true
        visible: step.session.anomaly && step.session.recording
        implicitHeight: warnRow.implicitHeight + 24
        radius: Theme.radiusM
        color: Theme.redBackground
        border.width: 1
        border.color: Theme.red
        RowLayout {
            id: warnRow
            anchors.fill: parent
            anchors.margins: 12
            spacing: 12
            EvidencePulse { Layout.alignment: Qt.AlignTop; Layout.topMargin: 5; tone: "bad" }
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 2
                Text {
                    objectName: "evidenceAnomalyTitle"
                    Layout.fillWidth: true
                    textFormat: Text.PlainText
                    text: step.session.anomalyTitle
                    color: Theme.red
                    font.pixelSize: Theme.fs(13)
                    font.weight: Font.Bold
                    wrapMode: Text.Wrap
                }
                EvidenceHint { text: qsTr("先截图，再停止并导出诊断，不用继续排本。") }
            }
            AppButton {
                objectName: "evidenceStopExportButton"
                Layout.alignment: Qt.AlignVCenter
                text: qsTr("停止并导出诊断")
                enabled: Runtime.app.templateObservationActionEnabled && !Runtime.app.captureCommandBusy
                onClicked: step.session.stopAndExport()
            }
        }
    }
}
