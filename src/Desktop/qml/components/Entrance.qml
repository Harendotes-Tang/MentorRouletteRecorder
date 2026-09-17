import QtQuick
import MentorRecorder

// A classic-style entrance (workbench.css wbRow / wbPop): the owner binds its
// opacity, scale and a Translate to this object and calls play(delay).
//
//   Entrance { id: enter; distanceY: 4 }          // a row: fade + rise 4 px
//   Entrance { id: enter; fromScale: 0.97 }       // a panel: fade + pop
//   opacity: enter.opacity; scale: enter.scale
//   transform: Translate { y: enter.dy }
//
// Eorzea keeps its own entrances, and with Theme.motion off play() lands on
// the end state at once, so a screenshot never catches a half-drawn item.
// A QtObject rather than an Item so it never takes a slot in a layout.
QtObject {
    id: entrance

    property real distanceX: 0
    property real distanceY: 0
    property real fromScale: 1
    property int duration: Theme.motionEnter

    /// 0 at the start of the entrance, 1 at rest.
    property real progress: 1
    readonly property real opacity: progress
    readonly property real dx: (1 - progress) * distanceX
    readonly property real dy: (1 - progress) * distanceY
    readonly property real scale: fromScale + (1 - fromScale) * progress
    readonly property bool enabled: Theme.motion && !Theme.eorzea
    readonly property bool running: animation.running

    function play(delay) {
        animation.stop()
        if (!entrance.enabled) {
            entrance.progress = 1
            return
        }
        pause.duration = Math.max(0, delay || 0)
        entrance.progress = 0
        animation.start()
    }

    function finish() {
        animation.stop()
        entrance.progress = 1
    }

    // Switching to eorzea or to reduced motion mid-way must not leave an item
    // half transparent.
    onEnabledChanged: if (!entrance.enabled) entrance.finish()

    readonly property SequentialAnimation animation: SequentialAnimation {
        PauseAnimation { id: pause; duration: 0 }
        NumberAnimation {
            target: entrance
            property: "progress"
            to: 1
            duration: entrance.duration
            easing.type: Easing.Bezier
            easing.bezierCurve: Theme.curveStandard
        }
    }
}
