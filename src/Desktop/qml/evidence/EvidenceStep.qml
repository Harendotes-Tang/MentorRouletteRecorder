pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// `.ev-step`: one of the five steps. The header carries the number (a tick once
// done), the title, a one-line summary of what the step settled, and a 当前 /
// 完成 chip. Only the current step is open, with a 3 px accent bar down its left
// edge; a done step reopens from its header; a step still to come is locked.
Card {
    id: step

    required property int number
    required property string title
    property string summary: ""
    // "cur" | "done" | "todo"
    property string status: "todo"
    property bool open: false
    default property alias body: bodyColumn.data

    signal toggleRequested()

    readonly property bool current: status === "cur"
    readonly property bool done: status === "done"

    objectName: "evidenceStep_" + number
    Layout.fillWidth: true
    padding: 0
    spacing: 0
    decorated: false
    borderColor: current ? Theme.accent : Theme.border

    // `inset 3px 0 0 var(--color-accent)`: an accent plate under the surface and
    // the surface itself set 3 px to the right, both with the frame's inner
    // radius, so the bar bends into the rounded corners the way the prototype's
    // inset shadow does instead of poking out of them.
    Rectangle {
        parent: step
        z: 0
        visible: step.current
        anchors.fill: parent
        anchors.margins: step.borderWidth
        radius: Math.max(0, step.radius - step.borderWidth)
        color: Theme.accent
    }
    Rectangle {
        parent: step
        // Above the plate whatever order the two were reparented in, below the body.
        z: 0.5
        visible: step.current
        anchors.fill: parent
        anchors.margins: step.borderWidth
        anchors.leftMargin: step.borderWidth + 3
        radius: Math.max(0, step.radius - step.borderWidth)
        gradient: Gradient {
            GradientStop { position: 0.0; color: Theme.eorzea ? Theme.panelTop : step.fillColor }
            GradientStop { position: 1.0; color: Theme.eorzea ? Theme.panelBottom : step.fillColor }
        }
    }

    Item {
        id: header
        Layout.fillWidth: true
        implicitHeight: headerRow.implicitHeight + 24
        Accessible.role: step.done ? Accessible.Button : Accessible.StaticText
        Accessible.name: step.number + ". " + step.title
        Accessible.description: step.summary

        Rectangle {
            anchors.fill: parent
            radius: step.radius
            color: Theme.fill
            opacity: step.done && headerHover.hovered ? 1 : 0
            Behavior on opacity { NumberAnimation { duration: Theme.motionFast } }
        }
        HoverHandler { id: headerHover; enabled: step.done }
        TapHandler {
            enabled: step.done
            onTapped: step.toggleRequested()
        }

        RowLayout {
            id: headerRow
            anchors.fill: parent
            anchors.leftMargin: 16
            anchors.rightMargin: 16
            anchors.topMargin: 12
            anchors.bottomMargin: 12
            spacing: 12

            // `.ev-n`
            Rectangle {
                Layout.alignment: Qt.AlignVCenter
                implicitWidth: 24
                implicitHeight: 24
                radius: 12
                color: step.current ? Theme.accent : step.done ? Theme.green : "transparent"
                border.width: 2
                border.color: step.current ? Theme.accent : step.done ? Theme.green : Theme.neutral300
                Behavior on color { ColorAnimation { duration: Theme.motionControl } }
                Text {
                    anchors.centerIn: parent
                    textFormat: Text.PlainText
                    text: step.done ? "✓" : String(step.number)
                    color: step.current || step.done ? Theme.badgeForeground : Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    font.family: step.done ? Theme.bodyFamily : Theme.figureFamily
                    font.weight: Font.Bold
                }
            }

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 1
                Text {
                    objectName: "evidenceStepTitle_" + step.number
                    Layout.fillWidth: true
                    textFormat: Text.PlainText
                    text: step.title
                    color: step.status === "todo" ? Theme.textSecondary : Theme.textPrimary
                    font.pixelSize: Theme.fs(14)
                    font.weight: step.status === "todo" ? Font.DemiBold : Font.Bold
                    elide: Text.ElideRight
                }
                Text {
                    objectName: "evidenceStepSummary_" + step.number
                    Layout.fillWidth: true
                    visible: step.done && step.summary.length > 0
                    textFormat: Text.PlainText
                    text: step.summary
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    elide: Text.ElideRight
                }
            }

            Tag {
                visible: step.status !== "todo"
                text: step.current ? qsTr("当前") : qsTr("完成")
                variant: step.current ? "blue" : "ink"
            }

            // `.ev-tw`: the chevron of a reopenable step, pointing down when closed.
            EvidenceIcon {
                visible: step.done
                Layout.alignment: Qt.AlignVCenter
                name: "chevron-right"
                color: Theme.textSecondary
                size: 14
                rotation: step.open ? -90 : 90
                Behavior on rotation { NumberAnimation { duration: Theme.motionFast } }
            }
        }
    }

    // `.ev-sb`: padding 0 18px 16px 52px, plus 8 px of air under the header so
    // the body does not sit on the title row.
    ColumnLayout {
        id: bodyColumn
        Layout.fillWidth: true
        Layout.topMargin: 8
        Layout.leftMargin: 52
        Layout.rightMargin: 18
        Layout.bottomMargin: 16
        visible: step.open
        spacing: 12
    }
}
