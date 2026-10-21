import QtQuick
import MentorRecorder

// The state mark beside the current duty: a pulsing green dot while the duty
// is in progress, a green check once cleared, an amber "?" when it ended
// without a result, a pulsing hourglass while the match waits for entry.
Item {
    id: root

    required property var ov
    /// "live" | "done" | "ended" | "matched"
    property string kind: "live"

    readonly property bool dot: kind === "live"
    // The float's modules live on while its window is hidden or minimized; the
    // pulse runs only while someone can see it, not through every duty.
    readonly property bool windowShown: !Window.window
        || (Window.window.visible && Window.window.visibility !== Window.Minimized)
    implicitWidth: dot ? 12 : 18
    implicitHeight: implicitWidth

    // The pulse: a ring that grows from the mark and fades (`ovPulse`).
    Rectangle {
        id: pulse
        visible: Theme.motion && (root.kind === "live" || root.kind === "matched")
        anchors.centerIn: parent
        width: root.dot ? 8 : 18
        height: width
        radius: width / 2
        color: "transparent"
        border.width: 2
        border.color: root.kind === "matched" ? root.ov.accent : root.ov.ok
        SequentialAnimation on scale {
            running: pulse.visible && root.windowShown
            loops: Animation.Infinite
            NumberAnimation { from: 1; to: 1.9; duration: 1260; easing.type: Easing.OutCubic }
            PauseAnimation { duration: 540 }
        }
        SequentialAnimation on opacity {
            running: pulse.visible && root.windowShown
            loops: Animation.Infinite
            NumberAnimation { from: 0.7; to: 0; duration: 1260 }
            PauseAnimation { duration: 540 }
        }
    }

    Rectangle {
        anchors.centerIn: parent
        width: root.dot ? 8 : 18
        height: width
        radius: width / 2
        color: root.kind === "live" ? root.ov.ok
             : root.kind === "done" ? root.ov.ok
             : root.kind === "ended" ? root.ov.warn : "transparent"
        border.width: root.kind === "matched" ? 1.5 : 0
        border.color: root.ov.accent

        OverlayIcon {
            anchors.centerIn: parent
            visible: root.kind === "done" || root.kind === "matched"
            name: root.kind === "done" ? "check" : "wait"
            color: root.kind === "done" ? Theme.badgeForeground : root.ov.accent
            size: 12
        }
        Text {
            anchors.centerIn: parent
            visible: root.kind === "ended"
            text: "?"
            color: Theme.badgeForeground
            font.pixelSize: 11
            font.weight: Font.ExtraBold
        }
    }
}
