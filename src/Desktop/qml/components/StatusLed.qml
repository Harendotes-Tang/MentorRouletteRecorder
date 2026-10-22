import QtQuick
import MentorRecorder 1.0

// `.dg-led` of the capture page: an 8 px dot in the tone's colour with a soft
// glow (box-shadow: 0 0 6px). The glow is a wider, translucent disc underneath
// rather than a blur effect, so it costs nothing and renders the same offscreen.
Item {
    id: root

    // "ok" | "warn" | "bad" | "off"
    property string tone: "off"
    property int size: 8
    readonly property color toneColor: Theme.toneColor(tone)

    implicitWidth: size
    implicitHeight: size

    Rectangle {
        anchors.centerIn: parent
        width: root.size + 6
        height: width
        radius: width / 2
        visible: root.tone !== "off"
        color: Qt.rgba(root.toneColor.r, root.toneColor.g, root.toneColor.b, 0.28)
    }

    Rectangle {
        anchors.centerIn: parent
        width: root.size
        height: width
        radius: width / 2
        color: root.toneColor
        Behavior on color { ColorAnimation { duration: Theme.motionControl } }
    }
}
