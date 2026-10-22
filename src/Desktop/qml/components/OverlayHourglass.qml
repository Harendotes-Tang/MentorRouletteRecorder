import QtQuick
import MentorRecorder 1.0

// An hourglass on the float's 16 x 16 icon grid (OverlayIcons.js). While
// `running`, the sand runs from the upper bulb into the lower one, rests a
// moment, and the glass turns over to run again; upside down, the full lower
// bulb is the full upper one, so the next pour starts where the turn ended.
// Standing still it shows the sand half run. Turning, the glass stays within
// 0.53 x its size of its centre, so its box is 1.06 x its size: at a module's
// edge, where the float's body clips, the corners are never cut.
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

    onSandChanged: glass.requestPaint()
    onColorChanged: glass.requestPaint()
    onRunningChanged: if (!running) { sand = 0.5; turn = 0 }

    // A pour of 3 s, a rest, a turn of 0.6 s: about 3.9 s a round.
    SequentialAnimation {
        running: root.running
        loops: Animation.Infinite
        NumberAnimation { target: root; property: "sand"; from: 0; to: 1; duration: 3000 }
        PauseAnimation { duration: 300 }
        NumberAnimation { target: root; property: "turn"; from: 0; to: 180; duration: 600; easing.type: Easing.InOutCubic }
        ScriptAction { script: { root.sand = 0; root.turn = 0 } }
    }

    Canvas {
        id: glass
        anchors.centerIn: parent
        width: root.size
        height: root.size
        rotation: root.turn

        // The walls run from the caps to the neck at y = 8 and are stroked;
        // the sand fills a path just inside them, so a gap shows between.
        function walls(ctx, x0, xNeck, top, bottom) {
            ctx.moveTo(x0, top)
            ctx.bezierCurveTo(x0, 5.4, xNeck, 6.2, xNeck, 8)
            ctx.bezierCurveTo(xNeck, 9.8, x0, 10.6, x0, bottom)
        }

        onPaint: {
            const ctx = getContext("2d")
            ctx.reset()
            const k = width / 16
            ctx.scale(k, k)
            const fill = root.color

            ctx.save()
            ctx.beginPath()
            walls(ctx, 5.3, 7.6, 3.1, 12.9)
            ctx.lineTo(10.7, 12.9)
            ctx.bezierCurveTo(10.7, 10.6, 8.4, 9.8, 8.4, 8)
            ctx.bezierCurveTo(8.4, 6.2, 10.7, 5.4, 10.7, 3.1)
            ctx.closePath()
            ctx.clip()
            ctx.fillStyle = fill
            // In a bulb narrowing to the neck the sand's area goes with the
            // square of its height, so the levels follow the square root and
            // the sand runs at an even rate.
            const height = 4.9
            const upper = height * Math.sqrt(1 - root.sand)
            const lower = height * (1 - Math.sqrt(1 - root.sand))
            if (upper > 0) ctx.fillRect(0, 8 - upper, 16, upper)
            if (lower > 0) ctx.fillRect(0, 12.9 - lower, 16, lower)
            if (root.running && root.sand > 0 && root.sand < 1)
                ctx.fillRect(7.6, 8, 0.8, 12.9 - lower - 8)
            ctx.restore()

            ctx.strokeStyle = fill
            ctx.lineCap = "round"
            ctx.lineJoin = "round"
            ctx.lineWidth = 1.4
            ctx.beginPath()
            walls(ctx, 4.2, 6.9, 2.2, 13.8)
            walls(ctx, 11.8, 9.1, 2.2, 13.8)
            ctx.stroke()
            ctx.lineWidth = 1.6
            ctx.beginPath()
            ctx.moveTo(3.2, 2.2)
            ctx.lineTo(12.8, 2.2)
            ctx.moveTo(3.2, 13.8)
            ctx.lineTo(12.8, 13.8)
            ctx.stroke()
        }
    }
}
