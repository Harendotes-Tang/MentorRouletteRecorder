import QtQuick
import MentorRecorder

// `.plus`: "+1" after a completion, popping in beside the figure it raised.
Rectangle {
    id: root

    property int count: 1
    property color tone: Theme.green

    implicitWidth: label.implicitWidth + 12
    implicitHeight: 16
    radius: 8
    color: tone
    scale: 1
    onVisibleChanged: if (visible && Theme.motion) pop.restart()

    SequentialAnimation {
        id: pop
        NumberAnimation { target: root; property: "scale"; from: 0.4; to: 1.15; duration: 270; easing.type: Easing.OutCubic }
        NumberAnimation { target: root; property: "scale"; to: 1; duration: 180 }
    }

    Text {
        id: label
        anchors.centerIn: parent
        text: "+" + root.count
        color: Theme.badgeForeground
        font.pixelSize: 10
        font.weight: Font.ExtraBold
    }
}
