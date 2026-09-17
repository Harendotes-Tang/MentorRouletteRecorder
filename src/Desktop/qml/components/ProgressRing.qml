import QtQuick
import QtQuick.Layouts
import MentorRecorder

// Achievement progress ring. The Eorzea theme paints a butt-capped arc with the
// #f0dc9e -> #b08a3a gradient of the prototype's <linearGradient id="ffgold">,
// plus hairline gold rings at r=76 / r=96.
Item {
    id: root

    property real value: 0
    property real maximum: 1
    property string label: ""
    property string detail: ""
    property color ringColor: Theme.accent

    implicitWidth: 200
    implicitHeight: 200

    readonly property real progress: maximum > 0 ? Math.max(0, Math.min(1, value / maximum)) : 0
    // Animated copy of progress; the arc is painted from this value.
    property real shown: progress
    Behavior on shown { NumberAnimation { duration: Theme.motionSlow; easing.type: Easing.OutCubic } }

    onShownChanged: ring.requestPaint()

    Canvas {
        id: ring
        anchors.fill: parent

        Connections {
            target: Theme
            function onDarkChanged() { ring.requestPaint() }
            function onEorzeaChanged() { ring.requestPaint() }
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
            if (Theme.eorzea) {
                const gradient = ctx.createLinearGradient(0, 0, width, height)
                gradient.addColorStop(0, Theme.ringStart)
                gradient.addColorStop(1, Theme.ringEnd)
                ctx.strokeStyle = gradient
                ctx.lineCap = "butt"
            } else {
                ctx.strokeStyle = root.ringColor
                ctx.lineCap = "round"
            }
            if (root.shown > 0) {
                ctx.beginPath()
                ctx.arc(cx, cy, radius, -Math.PI / 2,
                        -Math.PI / 2 + Math.PI * 2 * root.shown, false)
                ctx.stroke()
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
