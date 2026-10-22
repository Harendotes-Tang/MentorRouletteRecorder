import QtQuick
import MentorRecorder 1.0

// The state mark beside the current duty: a pulsing green dot while the duty
// is in progress, a green check once cleared, an amber "?" when it ended
// without a result, a pulsing ring with a bell while the match waits for
// entry: the bell shakes each time the pulse sets off.
Item {
    id: root

    required property var ov
    /// "live" | "done" | "ended" | "matched"
    property string kind: "live"

    readonly property bool dot: kind === "live"
    readonly property bool pulses: Theme.motion && (kind === "live" || kind === "matched")
    readonly property real markSize: dot ? 8 : 18
    /// 已匹配 rings in red in every theme: the pop calls the player.
    readonly property color callColor: Theme.red
    // The float's modules live on while its window is hidden or minimized; the
    // pulse and the bell run only while someone can see them, not through
    // every duty.
    readonly property bool windowShown: !Window.window
        || (Window.window.visible && Window.window.visibility !== Window.Minimized)
    // The mark ends the row, where the float's body clips: its box is as wide
    // as the pulse at its widest (1.9 x the mark), so the ring is never cut.
    // The height stays the mark's, so a pulse does not make its row taller.
    implicitWidth: pulses ? Math.ceil(markSize * 1.9) : markSize
    implicitHeight: dot ? 12 : markSize

    // The pulse: a ring that grows from the mark and fades (`ovPulse`).
    Rectangle {
        id: pulse
        objectName: "statusPulse"
        visible: root.pulses
        anchors.centerIn: parent
        width: root.markSize
        height: width
        radius: width / 2
        color: "transparent"
        border.width: 2
        border.color: root.kind === "matched" ? root.callColor : root.ov.ok
    }

    // One beat: the ring grows and fades while, at 已匹配, the bell shakes as
    // it sets off. One animation drives both, so they never drift apart.
    SequentialAnimation {
        running: pulse.visible && root.windowShown
        loops: Animation.Infinite
        ParallelAnimation {
            NumberAnimation { target: pulse; property: "scale"; from: 1; to: 1.9; duration: 1260; easing.type: Easing.OutCubic }
            NumberAnimation { target: pulse; property: "opacity"; from: 0.7; to: 0; duration: 1260 }
            // At most 14° either way: the swung bell stays inside the ring.
            SequentialAnimation {
                NumberAnimation { target: bell; property: "rotation"; to: 14; duration: 45 }
                NumberAnimation { target: bell; property: "rotation"; to: -12; duration: 70 }
                NumberAnimation { target: bell; property: "rotation"; to: 10; duration: 70 }
                NumberAnimation { target: bell; property: "rotation"; to: -7; duration: 65 }
                NumberAnimation { target: bell; property: "rotation"; to: 4; duration: 60 }
                NumberAnimation { target: bell; property: "rotation"; to: 0; duration: 50 }
            }
        }
        PauseAnimation { duration: 540 }
        // Stopped part-way, the bell hangs straight again.
        onRunningChanged: if (!running) bell.rotation = 0
    }

    Rectangle {
        anchors.centerIn: parent
        width: root.markSize
        height: width
        radius: width / 2
        color: root.kind === "live" ? root.ov.ok
             : root.kind === "done" ? root.ov.ok
             : root.kind === "ended" ? root.ov.warn : "transparent"
        border.width: root.kind === "matched" ? 1.5 : 0
        border.color: root.callColor

        OverlayIcon {
            anchors.centerIn: parent
            visible: root.kind === "done"
            name: "check"
            color: Theme.badgeForeground
            size: 12
        }
        OverlayIcon {
            id: bell
            objectName: "floatingMatchedBell"
            anchors.centerIn: parent
            visible: root.kind === "matched"
            name: "bell"
            color: root.callColor
            size: 12
            // A bell swings from where it hangs.
            transformOrigin: Item.Top
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
