import QtQuick
import QtQuick.Effects
import MentorRecorder

// The circular 深色 / 浅色 switch (the prototype's switchTheme(), which uses a
// view transition). run(change, x, y) captures the window content, calls
// `change()` to switch the theme underneath, then lays the captured frame on
// top behind a circular mask centred on (x, y):
//   * to dark  - the new theme opens from radius 0 to the farthest corner
//                (a growing hole in the old frame);
//   * to light - the old frame shrinks from the farthest corner to radius 0.
// 520 ms on cubic-bezier(.05,.75,.2,1). The overlay never takes input, is
// destroyed when the circle completes, and is not created when Theme.motion is
// off. A run() during another reveal finishes that one first.
Item {
    id: reveal

    /// What gets captured; the overlay covers this item's area.
    property Item source: null
    /// The overlay while a reveal is on screen, otherwise null.
    property Item overlay: null
    /// Number of overlays created so far (tests).
    property int createdCount: 0
    readonly property bool running: reveal.overlay !== null || reveal.pendingChange !== null

    // A capture has been requested and the theme has not switched yet.
    property var pendingChange: null
    property int generation: 0

    /// Tests: run the reveal on the software renderer too (its mask draws nothing).
    property bool softwareFallback: false
    /// The software renderer draws no shader effects, so it gets no reveal.
    readonly property bool supported: reveal.softwareFallback
                                      || (reveal.GraphicsInfo.api !== GraphicsInfo.Software
                                          && reveal.GraphicsInfo.api !== GraphicsInfo.Unknown)

    function run(change, x, y) {
        reveal.finish()
        if (!Theme.motion || !reveal.supported || !reveal.source || !reveal.source.visible
                || reveal.source.width <= 0 || reveal.source.height <= 0) {
            change()
            return
        }
        const generation = ++reveal.generation
        const wasDark = Theme.dark
        reveal.pendingChange = change
        // Device pixels, so the frame stays sharp on a scaled display.
        const ratio = Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1
        const size = Qt.size(Math.ceil(reveal.source.width * ratio),
                             Math.ceil(reveal.source.height * ratio))
        const requested = reveal.source.grabToImage(function(result) {
            if (generation !== reveal.generation)
                return
            captureTimeout.stop()
            reveal.pendingChange = null
            change()
            if (Theme.dark === wasDark || !Theme.motion)
                return
            reveal.show(result, x, y, Theme.dark)
        }, size)
        if (!requested) {
            reveal.pendingChange = null
            change()
            return
        }
        captureTimeout.restart()
    }

    /// Ends a reveal now: a pending theme change is applied, the overlay goes.
    function finish() {
        captureTimeout.stop()
        reveal.generation += 1
        const change = reveal.pendingChange
        reveal.pendingChange = null
        if (change)
            change()
        reveal.dropOverlay()
    }

    function dropOverlay() {
        if (!reveal.overlay)
            return
        const item = reveal.overlay
        reveal.overlay = null
        item.stopAndHide()
        item.destroy()
    }

    function farthestCorner(x, y) {
        const w = reveal.width
        const h = reveal.height
        return Math.hypot(Math.max(x, w - x), Math.max(y, h - y))
    }

    function show(result, x, y, toDark) {
        const far = reveal.farthestCorner(x, y)
        const generation = reveal.generation
        const item = overlayComponent.createObject(reveal, {
            grab: result,
            centerX: x,
            centerY: y,
            expand: toDark,
            radius: toDark ? 0 : far,
            targetRadius: toDark ? far : 0
        })
        if (!item)
            return
        reveal.createdCount += 1
        reveal.overlay = item
        item.done.connect(function() {
            if (generation === reveal.generation && reveal.overlay === item)
                reveal.dropOverlay()
        })
        item.start()
    }

    // A window that is not being drawn (minimised, covered by the lock screen)
    // never delivers the capture; the switch must not wait for it.
    Timer {
        id: captureTimeout
        interval: 1000
        onTriggered: reveal.finish()
    }

    // Reduced motion switched on mid-way: no half-revealed window.
    Connections {
        target: Theme
        function onMotionChanged() { if (!Theme.motion) reveal.finish() }
    }

    Component {
        id: overlayComponent

        Item {
            id: frame

            property var grab: null
            property real centerX: 0
            property real centerY: 0
            property real radius: 0
            property real targetRadius: 0
            /// true: the new theme opens as a hole in the old frame.
            property bool expand: true

            signal done()

            function start() {
                grow.from = frame.radius
                grow.to = frame.targetRadius
                grow.start()
            }

            function stopAndHide() {
                grow.stop()
                frame.visible = false
            }

            anchors.fill: parent

            Image {
                id: oldFrame
                anchors.fill: parent
                source: frame.grab ? frame.grab.url : ""
                visible: false
                cache: false
                asynchronous: false
                smooth: true
            }

            // The mask: a disc on a transparent field, the size of the frame.
            Item {
                id: mask
                anchors.fill: parent
                visible: false
                layer.enabled: true

                Rectangle {
                    x: frame.centerX - frame.radius
                    y: frame.centerY - frame.radius
                    width: frame.radius * 2
                    height: frame.radius * 2
                    radius: frame.radius
                    color: "#ffffffff"
                }
            }

            MultiEffect {
                anchors.fill: parent
                source: oldFrame
                maskEnabled: true
                maskSource: mask
                maskInverted: frame.expand
                // A soft one-pixel edge instead of a stair-stepped circle.
                maskThresholdMin: 0.5
                maskSpreadAtMin: 1.0
            }

            NumberAnimation {
                id: grow
                target: frame
                property: "radius"
                duration: Theme.motionReveal
                easing.type: Easing.Bezier
                easing.bezierCurve: Theme.curveReveal
                onFinished: frame.done()
            }
        }
    }
}
