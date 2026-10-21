import QtQuick
import MentorRecorder

// A short, input-transparent burst around the achievement ring. No particles
// or animation frames remain active after it finishes or its view is hidden.
Item {
    id: root

    property real phase: 0
    readonly property bool running: flight.running
    readonly property bool presentable: visible && (!Window.window
        || (Window.window.visible && Window.window.visibility !== Window.Minimized))

    function burst() {
        if (Theme.motion && presentable)
            flight.restart()
    }

    onPhaseChanged: particles.requestPaint()
    onPresentableChanged: {
        if (!presentable)
            flight.stop()
    }
    Connections {
        target: Theme
        function onMotionChanged() {
            if (!Theme.motion)
                flight.stop()
        }
    }

    // The trajectories below are drawn on a 1.8 s clock (`age`); playing that
    // clock over 3 s keeps their shape and makes the pieces drift rather than
    // drop.
    readonly property int flightDuration: 3000

    NumberAnimation {
        id: flight
        target: root
        property: "phase"
        from: 0
        to: 1
        duration: root.flightDuration
    }

    Canvas {
        id: particles
        anchors.fill: parent
        visible: root.running

        onPaint: {
            const ctx = getContext("2d")
            ctx.reset()
            const colors = [Theme.accent, Theme.gold2, Theme.green, Theme.orange]
            for (let index = 0; index < 64; ++index) {
                // Stable variation keeps screenshots reproducible, without a
                // global RNG or work that survives the burst.
                const seed = (index * 37 % 101) / 101
                const age = root.phase * 1.8 - (index % 8) * 0.018
                if (age < 0)
                    continue
                const left = index % 2 === 0
                const vx = width * (0.14 + seed * 0.30) * (left ? 1 : -1)
                const vy = -height * (0.85 + (index * 17 % 71) / 100)
                const x = width * (left ? 0.18 : 0.82) + vx * age
                const y = height * 0.82 + vy * age + height * 0.72 * age * age
                const size = 3 + seed * 3
                ctx.save()
                ctx.globalAlpha = Math.max(0, Math.min(1, (1 - root.phase) * 4))
                ctx.fillStyle = colors[index % colors.length]
                ctx.translate(x, y)
                ctx.rotate(index + age * (left ? 5 : -5))
                ctx.fillRect(-size / 2, -size, size, size * (0.7 + Math.abs(Math.cos(age * 9 + index))))
                ctx.restore()
            }
        }
    }
}
