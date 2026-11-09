import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

// Qt owns the popup lifecycle and modal layer. Finite transitions reverse safely;
// the narrow native bridge completes their current target on reduced motion or
// window hiding without reopening the dialog or synthesizing lifecycle signals.
Dialog {
    id: dialog
    property bool exiting: false
    Connections {
        target: dialog
        function onAboutToShow() { dialog.exiting = false }
        function onAboutToHide() { dialog.exiting = true }
    }
    readonly property bool motionAllowed: Theme.motion && (!parent || parent.visible)
        && (!parent || !parent.Window.window || parent.Window.window.visible)
    readonly property bool motionRunning: entrance.running || departure.running
    onMotionAllowedChanged: if (!motionAllowed) PopupMotion.finish(dialog)

    enter: Transition {
        id: entrance
        enabled: dialog.motionAllowed
        NumberAnimation { property: "opacity"; from: 0; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
        NumberAnimation { property: "scale"; from: 0.98; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
    }
    exit: Transition {
        id: departure
        enabled: dialog.motionAllowed
        NumberAnimation { property: "opacity"; to: 0; duration: Theme.motionFast; easing.type: Easing.OutCubic }
    }
}
