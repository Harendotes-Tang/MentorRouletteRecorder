import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 待处理记录: how many runs still wait for an answer or a note, one coloured
// dot per run. Clicking opens the first in the result popup, which then pages
// through the rest. Only on screen while something waits.
ColumnLayout {
    id: root

    required property var ov
    spacing: 0

    OverlayModuleLabel { ov: root.ov; text: qsTr("待处理记录"); Layout.bottomMargin: 5 }

    AbstractButton {
        id: button
        objectName: "floatingPendingButton"
        Layout.fillWidth: true
        implicitHeight: 24
        hoverEnabled: true
        focusPolicy: Qt.TabFocus
        Accessible.name: root.ov.pendingTip
        onClicked: root.ov.openPending()

        // `ovFlash`: the row lights up twice when a new record arrives.
        background: Rectangle {
            id: flash
            radius: Theme.radiusXs
            color: Theme.clear(root.ov.warn)
            SequentialAnimation {
                id: flashAnimation
                loops: 2
                ColorAnimation { target: flash; property: "color"; to: Qt.rgba(root.ov.warn.r, root.ov.warn.g, root.ov.warn.b, 0.26); duration: 420 }
                ColorAnimation { target: flash; property: "color"; to: Theme.clear(root.ov.warn); duration: 980 }
            }
            Connections {
                target: root.ov
                function onPendingNewChanged() { if (root.ov.pendingNew && Theme.motion) flashAnimation.restart() }
            }
        }

        contentItem: RowLayout {
            spacing: 8
            OverlayIcon { name: "pending"; color: Theme.textSecondary; Layout.alignment: Qt.AlignVCenter }
            Text {
                objectName: "floatingPendingCount"
                text: String(root.ov.pendingCount)
                color: root.ov.warn
                font.pixelSize: Theme.fs(15)
                font.weight: Font.ExtraBold
                font.family: Theme.figureFamily
                font.features: ({ "tnum": 1 })
            }
            OverlayPlusPill { visible: root.ov.pendingNew; count: 1; tone: root.ov.warn; Layout.alignment: Qt.AlignVCenter }
            Flow {
                Layout.fillWidth: true
                Layout.minimumWidth: 0
                Layout.alignment: Qt.AlignVCenter
                spacing: 3
                Repeater {
                    model: root.ov.pending.slice(0, 14)
                    delegate: Rectangle {
                        required property var modelData
                        width: 7
                        height: 7
                        radius: 3.5
                        color: root.ov.categoryColor(modelData.duty_category)
                        border.width: 1
                        border.color: "#33000000"
                    }
                }
            }
            Text {
                visible: root.ov.labels
                text: qsTr("%1 条待处理").arg(root.ov.pendingCount)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11.5)
            }
            OverlayIcon {
                name: "chevron-right"
                color: button.hovered ? root.ov.accent : Theme.textSecondary
                Layout.alignment: Qt.AlignVCenter
                Layout.rightMargin: button.hovered ? 0 : 2
                Behavior on Layout.rightMargin { NumberAnimation { duration: Theme.motionFast } }
            }
        }

        OverlayToolTip { visible: button.hovered; text: root.ov.pendingTip }
    }
}
