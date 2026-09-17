import QtQuick
import MentorRecorder

// The completion-trend bars while they move (the prototype's switchTrend()).
// At rest this layer is hidden and Qt Graphs draws the bars. Bar geometry is Qt
// Graphs' own (BarsRenderer::updateVerticalBars: a 2 px margin per slot,
// `barRatio` of what is left, centred, radius 4) and the owner lays this item
// exactly over GraphsView.plotArea, so the hand-over is seamless.
//
//   height : same number of bars - each bar's height eases to its new value
//   merge  : fewer bars (日 -> 周 / 月) - every old bar slides to the centre of
//            the new slot its start time falls into, narrowing to .6 and fading
//            to .35; then `regrow`: the new bars rise from .6 / .5, 22 ms apart
//   split  : more bars (周 / 月 -> 日) - every new bar starts at the centre of
//            the old slot it came from, at .85 height and .4 opacity, and
//            moves into place, 6 ms apart (the thirteenth and later together)
//
// A change that arrives mid-way cancels the running transition and starts the
// next one from the series the cancelled one was heading for.
Item {
    id: trendLayer

    property real barRatio: 0.82
    property color barColor: Theme.accent
    /// Test hook: draw the resting bars even when nothing moves.
    property bool forceVisible: false

    /// "", "height", "merge", "regrow" or "split".
    property string phase: ""
    readonly property bool running: phase !== ""
    /// Bumped by every begin() / stop(), so a late phase hand-over is ignored.
    property int generation: 0
    /// Milliseconds into the current phase, driven by `clock`.
    property real elapsed: 0
    /// The series last accepted: [{ t, count }] and its axis maximum.
    property var shown: []
    property real shownMax: 1
    /// What the repeater draws: one entry per bar (see barEntry()).
    property var bars: []

    // The phase after `merge` needs the series it merged from and into.
    property var pendingSeries: []
    property real pendingMax: 1

    visible: running || forceVisible

    // ------------------------------------------------------------- 几何 --
    readonly property real barMargin: 2

    function slotWidth(n) { return n > 0 ? trendLayer.width / n : 0 }
    function barWidthFor(n) { return Math.max(0, (trendLayer.slotWidth(n) - trendLayer.barMargin) * trendLayer.barRatio) }
    function barXFor(i, n) {
        const maxBarWidth = trendLayer.slotWidth(n) - trendLayer.barMargin
        const centering = (maxBarWidth - trendLayer.barWidthFor(n)) * 0.5
        return (i / n) * trendLayer.width + centering
    }
    function lengthFor(count, max) { return max > 0 ? trendLayer.height * count / max : 0 }
    function centerOf(i, n) { return (i + 0.5) * trendLayer.slotWidth(n) }

    // The last slot whose start is at or before `t` (the prototype's slotOf).
    // Without start times the slots are matched by position instead.
    function slotOf(t, index, count, list) {
        if (list.length === 0)
            return 0
        let known = !isNaN(t)
        for (let k = 0; known && k < list.length; ++k)
            known = !isNaN(list[k].t)
        if (!known)
            return Math.min(list.length - 1, Math.floor(index * list.length / Math.max(1, count)))
        let slot = 0
        for (let k = 0; k < list.length; ++k) {
            if (t >= list[k].t)
                slot = k
        }
        return slot
    }

    // ------------------------------------------------------------- 缓动 --
    // CSS cubic-bezier(x1, y1, x2, y2) at x, solved by bisection.
    function bezier(x, curve) {
        if (x <= 0)
            return 0
        if (x >= 1)
            return 1
        const x1 = curve[0], y1 = curve[1], x2 = curve[2], y2 = curve[3]
        const at = function(p1, p2, t) {
            const u = 1 - t
            return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t
        }
        let low = 0
        let high = 1
        let t = x
        for (let step = 0; step < 24; ++step) {
            t = (low + high) / 2
            if (at(x1, x2, t) < x)
                low = t
            else
                high = t
        }
        return at(y1, y2, t)
    }

    function phaseDuration(name) {
        switch (name) {
        case "height": return Theme.motionTrendHeight
        case "merge": return Theme.motionTrendMerge
        case "regrow": return Theme.motionTrendRegrow
        case "split": return Theme.motionTrendSplit
        default: return 0
        }
    }

    function phaseCurve(name) {
        return name === "merge" ? Theme.curveMerge : Theme.curveStandard
    }

    /// Eased progress of one bar in the current phase.
    function progressOf(delay) {
        const duration = trendLayer.phaseDuration(trendLayer.phase)
        if (!trendLayer.running || duration <= 0)
            return 1
        const x = (trendLayer.elapsed - delay) / duration
        return trendLayer.bezier(Math.min(1, Math.max(0, x)), trendLayer.phaseCurve(trendLayer.phase))
    }

    // ------------------------------------------------------------- 数据 --
    function barEntry(index, n, count, max) {
        return { index: index, n: n, count: count, max: max,
                 fromCount: count, fromMax: max, delay: 0,
                 slotX: 0, slotN: n }
    }

    function restingBars(series, max) {
        const out = []
        for (let i = 0; i < series.length; ++i)
            out.push(trendLayer.barEntry(i, series.length, series[i].count, max))
        return out
    }

    function sameSeries(a, b) {
        if (a.length !== b.length)
            return false
        for (let i = 0; i < a.length; ++i) {
            if (a[i].count !== b[i].count)
                return false
            if (a[i].t !== b[i].t && !(isNaN(a[i].t) && isNaN(b[i].t)))
                return false
        }
        return true
    }

    /// Adopt a new series; animates from the previous one when motion is on.
    function accept(series, max) {
        const previous = trendLayer.shown
        const previousMax = trendLayer.shownMax
        if (trendLayer.sameSeries(previous, series) && previousMax === max)
            return
        trendLayer.shown = series
        trendLayer.shownMax = max

        trendLayer.stop()
        if (!Theme.motion || previous.length === 0 || series.length === 0
                || trendLayer.width <= 0 || trendLayer.height <= 0)
            return

        if (series.length === previous.length)
            trendLayer.startHeight(previous, previousMax, series, max)
        else if (series.length < previous.length)
            trendLayer.startMerge(previous, previousMax, series, max)
        else
            trendLayer.startSplit(previous, series, max)
    }

    function startHeight(previous, previousMax, series, max) {
        const out = []
        for (let i = 0; i < series.length; ++i) {
            const entry = trendLayer.barEntry(i, series.length, series[i].count, max)
            entry.fromCount = previous[i].count
            entry.fromMax = previousMax
            out.push(entry)
        }
        trendLayer.begin("height", out, 0)
    }

    function startMerge(previous, previousMax, series, max) {
        const out = []
        for (let i = 0; i < previous.length; ++i) {
            const entry = trendLayer.barEntry(i, previous.length, previous[i].count, previousMax)
            entry.slotX = trendLayer.slotOf(previous[i].t, i, previous.length, series)
            entry.slotN = series.length
            out.push(entry)
        }
        trendLayer.pendingSeries = series
        trendLayer.pendingMax = max
        trendLayer.begin("merge", out, 0)
    }

    function startRegrow() {
        const series = trendLayer.pendingSeries
        const out = trendLayer.restingBars(series, trendLayer.pendingMax)
        for (let i = 0; i < out.length; ++i)
            out[i].delay = i * Theme.motionTrendRegrowStagger
        trendLayer.begin("regrow", out, (out.length - 1) * Theme.motionTrendRegrowStagger)
    }

    function startSplit(previous, series, max) {
        const out = trendLayer.restingBars(series, max)
        for (let i = 0; i < out.length; ++i) {
            out[i].slotX = trendLayer.slotOf(series[i].t, i, series.length, previous)
            out[i].slotN = previous.length
            out[i].delay = Math.min(i, 12) * Theme.motionTrendSplitStagger
        }
        trendLayer.begin("split", out, Math.min(out.length - 1, 12) * Theme.motionTrendSplitStagger)
    }

    function begin(name, entries, longestDelay) {
        trendLayer.generation += 1
        trendLayer.bars = entries
        trendLayer.elapsed = 0
        trendLayer.phase = name
        clock.to = trendLayer.phaseDuration(name) + Math.max(0, longestDelay)
        clock.duration = clock.to
        clock.restart()
    }

    function advance(generation) {
        if (generation !== trendLayer.generation)
            return
        if (trendLayer.phase === "merge") {
            trendLayer.startRegrow()
            return
        }
        trendLayer.stop()
    }

    /// Ends any running phase, leaving the bars at rest on the accepted series.
    function stop() {
        trendLayer.generation += 1
        clock.stop()
        trendLayer.phase = ""
        trendLayer.elapsed = 0
        trendLayer.pendingSeries = []
        trendLayer.bars = trendLayer.restingBars(trendLayer.shown, trendLayer.shownMax)
    }

    NumberAnimation {
        id: clock
        target: trendLayer
        property: "elapsed"
        from: 0
        easing.type: Easing.Linear
        // The next phase starts from the event loop, not from inside the
        // animation's own finished signal.
        onFinished: {
            const generation = trendLayer.generation
            Qt.callLater(function() { trendLayer.advance(generation) })
        }
    }

    // Reduced motion switched on mid-way: land at rest.
    Connections {
        target: Theme
        function onMotionChanged() { if (!Theme.motion) trendLayer.stop() }
    }

    Repeater {
        id: repeater

        model: trendLayer.bars

        delegate: Rectangle {
            id: bar

            required property var modelData
            required property int index

            readonly property real progress: trendLayer.progressOf(bar.modelData.delay)
            readonly property string phase: trendLayer.phase
            readonly property real length: {
                const to = trendLayer.lengthFor(bar.modelData.count, bar.modelData.max)
                if (bar.phase !== "height")
                    return to
                const from = trendLayer.lengthFor(bar.modelData.fromCount, bar.modelData.fromMax)
                return from + (to - from) * bar.progress
            }
            // Horizontal offset (merge: towards the new slot; split: from the old one).
            readonly property real shift: {
                const own = trendLayer.centerOf(bar.modelData.index, bar.modelData.n)
                const other = trendLayer.centerOf(bar.modelData.slotX, bar.modelData.slotN)
                if (bar.phase === "merge")
                    return (other - own) * bar.progress
                if (bar.phase === "split")
                    return (other - own) * (1 - bar.progress)
                return 0
            }
            readonly property real widthScale: bar.phase === "merge" ? 1 - 0.4 * bar.progress : 1
            readonly property real heightScale: {
                if (bar.phase === "regrow")
                    return 0.6 + 0.4 * bar.progress
                if (bar.phase === "split")
                    return 0.85 + 0.15 * bar.progress
                return 1
            }

            x: trendLayer.barXFor(bar.modelData.index, bar.modelData.n)
            y: trendLayer.height - bar.length
            width: trendLayer.barWidthFor(bar.modelData.n)
            height: bar.length
            radius: 4
            color: trendLayer.barColor
            opacity: {
                if (bar.phase === "merge")
                    return 1 - 0.65 * bar.progress
                if (bar.phase === "regrow")
                    return 0.5 + 0.5 * bar.progress
                if (bar.phase === "split")
                    return 0.4 + 0.6 * bar.progress
                return 1
            }
            transform: [
                Scale {
                    origin.x: bar.width / 2
                    origin.y: bar.height
                    xScale: bar.widthScale
                    yScale: bar.heightScale
                },
                Translate { x: bar.shift }
            ]
        }
    }
}
