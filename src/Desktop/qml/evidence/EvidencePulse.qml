import QtQuick
import MentorRecorder 1.0

// `.ev-pulse`: an 8 px dot in the tone's colour; while the round is live a
// ring breathes out of it every 1.8 s (1.2 s when the tone is bad).
Item {
    id: root

    // "ok" | "warn" | "bad" | "off"
    property string tone: "off"
    property int size: 8
    readonly property color toneColor: Theme.toneColor(tone)
    readonly property bool live: tone !== "off" && Theme.motion
    // A round can run for two hours while the page is elsewhere or the window
    // is minimized: the ring breathes only while someone can see it (review K4-9,
    // as OverlayStatusIcon does).
    readonly property bool windowShown: !Window.window
        || (Window.window.visible && Window.window.visibility !== Window.Minimized)
    readonly property bool animating: live && visible && windowShown

    implicitWidth: size
    implicitHeight: size

    Rectangle {
        id: ring
        anchors.centerIn: parent
        width: root.size
        height: width
        radius: width / 2
        color: "transparent"
        border.width: 2
        border.color: root.toneColor
        visible: root.live
        SequentialAnimation on scale {
            running: root.animating
            loops: Animation.Infinite
            NumberAnimation { from: 1; to: 2.4; duration: root.tone === "bad" ? 840 : 1260; easing.type: Easing.OutCubic }
            PauseAnimation { duration: root.tone === "bad" ? 360 : 540 }
        }
        SequentialAnimation on opacity {
            running: root.animating
            loops: Animation.Infinite
            NumberAnimation { from: 0.55; to: 0; duration: root.tone === "bad" ? 840 : 1260 }
            PauseAnimation { duration: root.tone === "bad" ? 360 : 540 }
        }
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
