import QtQuick
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// The bundled Lucide hourglass with its sand drawn inside the outline: while
// `running`, the sand runs from the upper bulb into the lower one, rests a moment,
// and the glass turns over to run again; upside down, the full lower bulb is the
// full upper one (docs/ui-design.md §4.9.1, audit 2026-11-10 L3-1). Standing still
// it shows the sand half run.
Item {
    id: root

    property color color: Theme.textSecondary
    property real size: 14
    /// The caller decides when someone can see the sand run.
    property bool running: false

    /// 0: all the sand in the upper bulb, 1: all of it in the lower one.
    property real sand: 0.5
    /// The glass's turn in degrees, 0 to 180.
    property real turn: 0

    implicitWidth: Math.ceil(size * 1.06)
    implicitHeight: implicitWidth

    onRunningChanged: if (!running) { sand = 0.5; turn = 0 }
    onSandChanged: sandLayer.requestPaint()
    onColorChanged: sandLayer.requestPaint()

    // Keep the 3 s phase, short rest and 0.6 s turn: about 3.9 s a round.
    SequentialAnimation {
        running: root.running
        loops: Animation.Infinite
        NumberAnimation { target: root; property: "sand"; from: 0; to: 1; duration: 3000 }
        PauseAnimation { duration: 300 }
        NumberAnimation { target: root; property: "turn"; from: 0; to: 180; duration: 600; easing.type: Easing.InOutCubic }
        ScriptAction { script: { root.sand = 0; root.turn = 0 } }
    }

    // The sand, in the icon's own 24-unit grid, inside the two bulbs the outline
    // draws (walls at x 7 and 17 down to y 6.2, the neck at y 12).
    Canvas {
        id: sandLayer
        objectName: "hourglassSand"
        anchors.centerIn: parent
        width: root.size
        height: root.size
        rotation: root.turn
        onWidthChanged: requestPaint()
        onPaint: {
            const ctx = getContext("2d")
            ctx.reset()
            const k = width / 24
            ctx.scale(k, k)
            ctx.beginPath()
            ctx.moveTo(8.4, 3.4); ctx.lineTo(15.6, 3.4); ctx.lineTo(15.6, 6.4); ctx.lineTo(12, 10.4)
            ctx.lineTo(8.4, 6.4); ctx.closePath()
            ctx.moveTo(8.4, 20.6); ctx.lineTo(15.6, 20.6); ctx.lineTo(15.6, 17.6); ctx.lineTo(12, 13.6)
            ctx.lineTo(8.4, 17.6); ctx.closePath()
            ctx.clip()
            ctx.fillStyle = root.color
            // A bulb narrows to the neck, so the sand's area goes roughly with the
            // square of its height: the levels follow the square root and the sand
            // runs at an even rate.
            const depth = 7
            const sand = Math.max(0, Math.min(1, root.sand))
            const upper = depth * Math.sqrt(1 - sand)
            const lower = depth * (1 - Math.sqrt(1 - sand))
            if (upper > 0) ctx.fillRect(0, 10.4 - upper, 24, upper)
            if (lower > 0) ctx.fillRect(0, 20.6 - lower, 24, lower)
            if (root.running && sand > 0 && sand < 1)
                ctx.fillRect(11.6, 10.4, 0.8, Math.max(0, 20.6 - lower - 10.4))
        }
    }

    Image {
        anchors.centerIn: parent
        width: root.size
        height: root.size
        rotation: root.turn
        source: Lucide.source("hourglass", root.color)
        sourceSize: Qt.size(Math.ceil(width * Screen.devicePixelRatio),
                            Math.ceil(height * Screen.devicePixelRatio))
        fillMode: Image.PreserveAspectFit
        Accessible.ignored: true
    }
}
