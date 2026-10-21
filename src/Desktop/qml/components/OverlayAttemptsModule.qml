import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// The independent snapshot distinguishes a known zero from waiting or failed
// reads. The achievement module below continues to use its all-time progress.
ColumnLayout {
    id: root
    required property var ov
    readonly property var controller: ov.controller
    spacing: 5

    RowLayout {
        Layout.fillWidth: true
        spacing: 8

        Text {
            Layout.fillWidth: true
            Layout.minimumWidth: 0
            text: qsTr("排本次数")
            textFormat: Text.PlainText
            wrapMode: Text.WordWrap
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
        Text {
            objectName: "floatingAttemptCount"
            text: root.controller.attemptsKnown ? String(root.controller.attemptCount) : Fmt.dash()
            color: root.ov.figure
            font.family: Theme.numFamily
            font.pixelSize: Theme.fs(26)
            font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
            font.features: ({ "tnum": 1 })
            Accessible.name: qsTr("排本次数") + " " + text
        }
    }
    Text {
        objectName: "floatingAttemptsLoading"
        Layout.fillWidth: true
        visible: root.controller.attemptsLoading
        text: qsTr("读取中…")
        textFormat: Text.PlainText
        wrapMode: Text.WordWrap
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
    }
    Text {
        objectName: "floatingAttemptsError"
        Layout.fillWidth: true
        visible: root.controller.attemptsError.length > 0
        text: root.controller.attemptsError
        textFormat: Text.PlainText
        wrapMode: Text.WordWrap
        color: Theme.orangeText
        font.pixelSize: Theme.fs(11)
        Accessible.role: Accessible.AlertMessage
    }
    Button {
        id: retry
        objectName: "floatingAttemptsRetry"
        visible: root.controller.attemptsError.length > 0
        text: qsTr("重试")
        focusPolicy: Qt.TabFocus
        implicitHeight: 28
        leftPadding: 10
        rightPadding: 10
        onClicked: root.controller.refreshAttempts()
        background: Rectangle {
            radius: Theme.radiusS
            color: retry.hovered || retry.down ? Theme.fillStrong : Theme.fill
            border.width: retry.activeFocus ? 2 : 1
            border.color: retry.activeFocus ? (Theme.eorzea ? Theme.gold2 : Theme.accent) : Theme.border
        }
        contentItem: Text {
            text: retry.text
            horizontalAlignment: Text.AlignHCenter
            verticalAlignment: Text.AlignVCenter
            font.pixelSize: Theme.fs(11)
            color: Theme.textPrimary
        }
    }
}
