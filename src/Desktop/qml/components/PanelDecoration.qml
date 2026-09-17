import QtQuick
import MentorRecorder

// `.panel::before` / `.panel::after` of ff14.css: two gilded corner ticks plus
// the 1 px inner highlight of `--shadow-sm`. Anchor it over any panel-like
// surface; it draws nothing at all in the classic style.
Item {
    id: root

    property int tickSize: 10
    property real tickWidth: 1.5

    anchors.fill: parent
    visible: Theme.eorzea
    z: 2

    // inset 0 1px 0 rgba(255,235,190,.06)
    Rectangle {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.margins: 1
        height: 1
        color: Theme.dark ? "#0fffebbe" : "#b3ffffff"
    }

    Repeater {
        model: [
            { x: 0, y: 0, w: root.tickSize, h: root.tickWidth },
            { x: 0, y: 0, w: root.tickWidth, h: root.tickSize }
        ]

        delegate: Rectangle {
            required property var modelData

            x: modelData.x
            y: modelData.y
            width: modelData.w
            height: modelData.h
            color: Theme.gold
            opacity: 0.85
        }
    }

    Repeater {
        model: [
            { w: root.tickSize, h: root.tickWidth },
            { w: root.tickWidth, h: root.tickSize }
        ]

        delegate: Rectangle {
            required property var modelData

            x: root.width - modelData.w
            y: root.height - modelData.h
            width: modelData.w
            height: modelData.h
            color: Theme.gold
            opacity: 0.85
        }
    }
}
