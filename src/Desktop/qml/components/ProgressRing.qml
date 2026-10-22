import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// Achievement progress ring. The Eorzea theme paints the arc with the
// #f0dc9e -> #b08a3a gradient of the prototype's <linearGradient id="ffgold">,
// plus hairline gold rings at r=76 / r=96. Harendotes paints the gradient along
// the arc instead: an amber tail at 12 o'clock running into the flame at the
// head, every stop opaque so nothing mixes with the track underneath. Both
// gilded styles leave the tail flat at 12 o'clock and finish the head with a
// disc, so the arc ends in a round cap.
Item {
    id: root

    property real value: 0
    property real maximum: 1
    property string label: ""
    property string detail: ""
    property color ringColor: Theme.accent

    implicitWidth: 200
    implicitHeight: 200

    readonly property real progress: maximum > 0 ? Math.max(0, value / maximum) : 0
    // Animate the total ratio so 99% -> 101% goes forwards through the top,
    // rather than interpolating backwards from .99 to the next lap's .01.
    property real shown: 0
    property bool initialized: false
    readonly property real lapProgress: shown > 0 && shown % 1 === 0 ? 1 : shown % 1
    readonly property bool animationAllowed: Theme.motion && visible && (!Window.window
        || (Window.window.visible && Window.window.visibility !== Window.Minimized))
    function updateShown() {
        if (!initialized) {
            shown = progress
            return
        }
        advance.stop()
        if (animationAllowed) {
            advance.from = shown
            advance.to = progress
            advance.start()
        } else {
            shown = progress
        }
    }
    onProgressChanged: updateShown()
    onAnimationAllowedChanged: {
        if (!animationAllowed && initialized) {
            advance.stop()
            shown = progress
        }
    }
    Component.onCompleted: {
        shown = progress
        initialized = true
    }
    NumberAnimation {
        id: advance
        target: root
        property: "shown"
        duration: Theme.motionRing
        easing.type: Easing.OutCubic
    }

    onShownChanged: ring.requestPaint()

    Canvas {
        id: ring
        anchors.fill: parent

        Connections {
            target: Theme
            function onDarkChanged() { ring.requestPaint() }
            function onEorzeaChanged() { ring.requestPaint() }
            function onUiStyleChanged() { ring.requestPaint() }
        }

        onPaint: {
            const ctx = getContext("2d")
            ctx.reset()
            const scale = Math.min(width, height) / 200
            const cx = width / 2
            const cy = height / 2
            const radius = 86 * scale
            ctx.lineWidth = 14 * scale
            ctx.strokeStyle = Theme.eorzea ? Theme.ringTrack : Theme.fill
            ctx.beginPath()
            ctx.arc(cx, cy, radius, 0, Math.PI * 2, false)
            ctx.stroke()

            if (root.shown > 1) {
                // A completed circle stays underneath the active lap.
                ctx.strokeStyle = Theme.eorzea ? Theme.ringStart : root.ringColor
                ctx.globalAlpha = 0.24
                ctx.beginPath()
                ctx.arc(cx, cy, radius, 0, Math.PI * 2, false)
                ctx.stroke()
                ctx.globalAlpha = 1
            }

            if (Theme.eorzea) {
                ctx.lineWidth = 0.8 * scale
                ctx.strokeStyle = Theme.gold3
                ctx.globalAlpha = 0.7
                for (const r of [76, 96]) {
                    ctx.beginPath()
                    ctx.arc(cx, cy, r * scale, 0, Math.PI * 2, false)
                    ctx.stroke()
                }
                ctx.globalAlpha = 1
            }

            ctx.lineWidth = 14 * scale
            let headFill = null
            if (Theme.harendotes) {
                // A conical gradient runs counter-clockwise from its start angle
                // and the arc clockwise from the top, so the arc fraction f sits
                // at gradient position 1 - f: the tail (f = 0) at 1, the head
                // (f = shown) at 1 - shown.
                const head = 1 - root.lapProgress
                const gradient = ctx.createConicalGradient(cx, cy, Math.PI / 2)
                gradient.addColorStop(0, Theme.ringStart)
                gradient.addColorStop(head, Theme.ringStart)
                gradient.addColorStop(1, Theme.ringTail)
                ctx.strokeStyle = gradient
                ctx.lineCap = "butt"
                // Only the head is rounded, with a disc of the head colour. A
                // round lineCap would round the tail at 12 o'clock too, where
                // the conical gradient wraps from ringTail straight back to
                // ringStart, so that cap would come out in the head's flame.
                headFill = Theme.ringStart
            } else if (Theme.eorzea) {
                const gradient = ctx.createLinearGradient(0, 0, width, height)
                gradient.addColorStop(0, Theme.ringStart)
                gradient.addColorStop(1, Theme.ringEnd)
                ctx.strokeStyle = gradient
                ctx.lineCap = "butt"
                // The gradient is spatial, so the same fill colours the head
                // disc exactly as the arc is coloured there.
                headFill = gradient
            } else {
                ctx.strokeStyle = root.ringColor
                ctx.lineCap = "round"
            }
            if (root.lapProgress > 0) {
                const end = -Math.PI / 2 + Math.PI * 2 * root.lapProgress
                ctx.beginPath()
                ctx.arc(cx, cy, radius, -Math.PI / 2, end, false)
                ctx.stroke()
                if (headFill) {
                    ctx.fillStyle = headFill
                    ctx.beginPath()
                    ctx.arc(cx + radius * Math.cos(end), cy + radius * Math.sin(end),
                            ctx.lineWidth / 2, 0, Math.PI * 2, false)
                    ctx.fill()
                }
            }
        }
    }

    ColumnLayout {
        anchors.centerIn: parent
        spacing: 4

        Text {
            text: root.label
            color: Theme.eorzea ? Theme.headingColor : Theme.textPrimary
            font.family: Theme.numFamily
            font.pixelSize: Theme.fs(40)
            font.weight: Theme.figureWeight(true)
            font.features: ({ "tnum": 1 })
            horizontalAlignment: Text.AlignHCenter
            Layout.alignment: Qt.AlignHCenter
        }

        Text {
            text: root.detail
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.NoWrap
            horizontalAlignment: Text.AlignHCenter
            Layout.maximumWidth: 160
            Layout.alignment: Qt.AlignHCenter
        }
    }
}
