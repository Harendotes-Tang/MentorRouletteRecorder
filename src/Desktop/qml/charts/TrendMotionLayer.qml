pragma ComponentBehavior: Bound

import QtQuick
import QtQml.Models 6.8
import MentorRecorder 1.0

// The completion-trend bars while they move. At rest this layer is hidden and
// Qt Graphs draws the bars. Bar geometry is Qt Graphs' own
// (BarsRenderer::updateVerticalBars: a 2 px margin per slot, `barRatio` of what
// is left, centred, radius 4) and the owner lays this item exactly over
// GraphsView.plotArea, so the hand-over is seamless.
//
// Every change is one motion. Each bar is given the slot and the height it
// starts from and the ones it ends on, and all bars move between the two on
// one clock; no bar is swapped, faded or resized in a step on the way. The
// first frame is therefore the picture that was on screen and the last frame
// the series Qt Graphs shows next.
//
//   height : the same slots - only the heights move
//   merge  : longer slots (日 -> 周 / 月), the time axis zooms out - every bar
//            becomes the new bar its start time falls into, so the days of a
//            week end as that week's one bar; new bars the old window did not
//            reach come in from where they sat on the old axis
//   split  : shorter slots (周 / 月 -> 日), the reverse - every new bar starts
//            as the bar it was part of; bars the new window does not reach
//            leave along the new axis
//   zoom   : the same slot length over another window (7 天 <-> 30 天, a new
//            day) - a bar both series have keeps its place on the time axis,
//            the others come in or leave
//
// A change that arrives mid-way takes the bars up where they stand: the frame
// on screen is the first frame of the next motion.
Item {
    id: trendLayer

    property real barRatio: 0.82
    property color barColor: Theme.accent
    /// Test hook: draw the resting bars even when nothing moves.
    property bool forceVisible: false

    /// "", "height", "merge", "split" or "zoom".
    property string phase: ""
    readonly property bool running: phase !== ""
    /// How far the running motion is, eased: 0 is where it started, 1 the new series.
    property real progress: 1
    /// The series last accepted: [{ t, count }] and its axis maximum.
    property var shown: []
    property real shownMax: 1
    /// What the repeater draws: one entry per bar (see restingEntry()).
    property var bars: []
    /// Whether the spans of the running motion's bars are start times (true) or
    /// shares of an even axis (false, see evenEdges()).
    property bool timed: false
    onBarsChanged: {
        // Keep the delegates a change can reuse. Replacing an array model would
        // destroy every rectangle and create a full new set in the same frame,
        // including for height-only updates.
        if (barRows.count > bars.length)
            barRows.remove(bars.length, barRows.count - bars.length)
        const existing = barRows.count
        for (let i = 0; i < existing; ++i)
            barRows.set(i, { entry: bars[i] })
        for (let appendIndex = existing; appendIndex < bars.length; ++appendIndex)
            barRows.append({ entry: bars[appendIndex] })
    }

    visible: running || forceVisible
    // Bars enter and leave past the edges of the plot.
    clip: true

    // ------------------------------------------------------------- 几何 --
    readonly property real barMargin: 2
    /// Tolerance for positions given as a share of the plot width.
    readonly property real edgeSlack: 0.000001

    // ----------------------------------------------------------- 时间轴 --
    // A series is n slots side by side. `edges` holds their n + 1 boundaries,
    // and a position on that axis is a share of the plot width: slot i spans
    // i / n .. (i + 1) / n, and an instant outside the series lies below 0 or
    // above 1.

    /// The slot boundaries of a series, or [] when its start times cannot say.
    function edgesOf(series) {
        const n = series.length
        if (n < 2)
            return []
        const edges = []
        for (let i = 0; i < n; ++i) {
            const t = series[i].t
            if (isNaN(t) || (i > 0 && t <= edges[i - 1]))
                return []
            edges.push(t)
        }
        edges.push(trendLayer.lastEdge(edges[n - 2], edges[n - 1]))
        return edges
    }

    /// Where the last slot ends: one calendar step after it starts, the step being the
    /// one between the last two starts. On a month axis the last month has its own
    /// length, not the length of the month before it (audit 2026-11-20, K2-3); a day
    /// keeps to midnight across a clock change. Starts at midnight are read in local
    /// time, then in UTC; anything else is taken to be as long as the slot before it.
    function lastEdge(previous, last) {
        for (const utc of [false, true]) {
            const fields = function(t) {
                const d = new Date(t)
                return utc
                    ? { y: d.getUTCFullYear(), m: d.getUTCMonth(), day: d.getUTCDate(),
                        midnight: d.getUTCHours() === 0 && d.getUTCMinutes() === 0
                                  && d.getUTCSeconds() === 0 && d.getUTCMilliseconds() === 0 }
                    : { y: d.getFullYear(), m: d.getMonth(), day: d.getDate(),
                        midnight: d.getHours() === 0 && d.getMinutes() === 0
                                  && d.getSeconds() === 0 && d.getMilliseconds() === 0 }
            }
            const a = fields(previous)
            const b = fields(last)
            if (!a.midnight || !b.midnight)
                continue
            const at = function(y, m, day) { return utc ? Date.UTC(y, m, day) : new Date(y, m, day).getTime() }
            if (a.day === 1 && b.day === 1)
                return at(b.y, b.m + (b.y - a.y) * 12 + b.m - a.m, 1)
            const days = Math.round((Date.UTC(b.y, b.m, b.day) - Date.UTC(a.y, a.m, a.day)) / 86400000)
            return at(b.y, b.m, b.day + days)
        }
        return (2 * last) - previous
    }

    /// Boundaries for a series without usable start times: n equal slots over 0..1.
    function evenEdges(n) {
        const edges = []
        for (let i = 0; i <= n; ++i)
            edges.push(i / Math.max(1, n))
        return edges
    }

    /// The slot that holds instant `t`, or -1 when the series does not reach it.
    function slotAt(t, edges) {
        const n = edges.length - 1
        const slack = (edges[n] - edges[0]) * trendLayer.edgeSlack
        if (t < edges[0] - slack || t >= edges[n] - slack)
            return -1
        let slot = 0
        for (let k = 1; k < n; ++k) {
            if (t >= edges[k] - slack)
                slot = k
        }
        return slot
    }

    /// Where instant `t` lies on the axis, as a share of the plot width.
    function place(t, edges) {
        const n = edges.length - 1
        if (t <= edges[0])
            return (t - edges[0]) / (edges[1] - edges[0]) / n
        if (t >= edges[n])
            return (n + (t - edges[n]) / (edges[n] - edges[n - 1])) / n
        let k = 0
        while (k < n - 1 && t >= edges[k + 1])
            ++k
        return (k + (t - edges[k]) / (edges[k + 1] - edges[k])) / n
    }

    /// Where a bar of `width` stands on an axis that has no slot for the span
    /// `from`..`to`. Outside the plot the edge facing the plot follows the axis,
    /// so the bar is out of sight exactly when its span is.
    function strayPlace(from, to, edges, width) {
        const start = trendLayer.place(from, edges)
        const end = trendLayer.place(to, edges)
        if (end <= trendLayer.edgeSlack)
            return { s: end - width, e: end, outside: true }
        if (start >= 1 - trendLayer.edgeSlack)
            return { s: start, e: start + width, outside: true }
        const centre = (start + end) / 2
        return { s: centre - (width / 2), e: centre + (width / 2), outside: false }
    }

    // ------------------------------------------------------------- 数据 --
    function shareOf(count, max) { return max > 0 ? count / max : 0 }

    /// A bar standing in slot `index` of `n`: s / e are the slot's edges and h
    /// its height, as shares of the plot; 0 marks the start and 1 the end.
    function restingEntry(index, n, count, max) {
        const share = trendLayer.shareOf(count, max)
        return { s0: index / n, e0: (index + 1) / n, h0: share,
                 s1: index / n, e1: (index + 1) / n, h1: share }
    }

    function restingBars(series, max) {
        const out = []
        for (let i = 0; i < series.length; ++i)
            out.push(trendLayer.restingEntry(i, series.length, series[i].count, max))
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

    // ------------------------------------------------------- 画面上的柱 --
    // A bar on screen: s / e / h as it stands now, and t0..t1, the span of the
    // axis it stands for. The span decides where the bar belongs in a new series.

    /// The bars of a series at rest.
    function standingBars(series, max, edges) {
        const n = series.length
        const out = []
        for (let i = 0; i < n; ++i)
            out.push({ s: i / n, e: (i + 1) / n, h: trendLayer.shareOf(series[i].count, max),
                       t0: edges[i], t1: edges[i + 1] })
        return out
    }

    /// The bars of the running motion as this frame shows them.
    function movingBars() {
        const at = trendLayer.progress
        const out = []
        for (let k = 0; k < trendLayer.bars.length; ++k) {
            const entry = trendLayer.bars[k]
            out.push({ s: entry.s0 + (entry.s1 - entry.s0) * at,
                       e: entry.e0 + (entry.e1 - entry.e0) * at,
                       h: entry.h0 + (entry.h1 - entry.h0) * at,
                       t0: entry.t0, t1: entry.t1 })
        }
        return out
    }

    /// An entry that starts as `bar` stands and, until told otherwise, ends there.
    function entryFrom(bar) {
        return { s0: bar.s, e0: bar.e, h0: bar.h, s1: bar.s, e1: bar.e, h1: bar.h,
                 t0: bar.t0, t1: bar.t1 }
    }

    // ------------------------------------------------------------- 过渡 --
    /// Adopt a new series; animates from the frame on screen when motion is on.
    function accept(series, max) {
        const previous = trendLayer.shown
        const previousMax = trendLayer.shownMax
        if (trendLayer.sameSeries(previous, series) && previousMax === max)
            return
        // Read the frame of a running motion before that motion is ended.
        const moving = trendLayer.running ? trendLayer.movingBars() : []
        const movingTimed = trendLayer.timed
        trendLayer.shown = series
        trendLayer.shownMax = max

        // The motion supplies its own entries. Do not build the resting bars
        // only to replace them at once with the moving ones.
        trendLayer.stop(false)
        if (!Theme.motion || previous.length === 0 || series.length === 0
                || trendLayer.width <= 0 || trendLayer.height <= 0) {
            trendLayer.bars = trendLayer.restingBars(series, max)
            return
        }

        let from = trendLayer.edgesOf(previous)
        let to = trendLayer.edgesOf(series)
        const timed = from.length > 0 && to.length > 0
        if (!timed) {
            // No usable start times: both series are taken to cover the same span.
            from = trendLayer.evenEdges(previous.length)
            to = trendLayer.evenEdges(series.length)
        }
        // A motion cut short goes on from where its bars stand, provided their
        // spans are told in the terms this change uses.
        const live = moving.length > 0 && movingTimed === timed
            ? moving : trendLayer.standingBars(previous, previousMax, from)
        trendLayer.timed = timed

        const fromSlot = (from[previous.length] - from[0]) / previous.length
        const toSlot = (to[series.length] - to[0]) / series.length
        const name = toSlot > fromSlot * 1.5 ? "merge" : (toSlot < fromSlot / 1.5 ? "split" : "zoom")
        trendLayer.startMotion(live, from, series, max, to, name)
    }

    /// Gives every bar on screen its place in the new series and starts the clock.
    function startMotion(live, from, series, max, to, name) {
        const m = series.length
        const slotSpan = (to[m] - to[0]) / m
        // A bar that stands for more of the axis than a new slot holds splits
        // into the new bars of its span (-2). Any other bar goes to the slot
        // its start falls into (-1: the new series has none).
        const roles = []
        const carried = []
        for (let k = 0; k < live.length; ++k) {
            const role = (live[k].t1 - live[k].t0) > slotSpan * 1.5
                ? -2 : trendLayer.slotAt(live[k].t0, to)
            roles.push(role)
            if (role >= 0 && live[k].h > 0)
                carried[role] = true
        }
        const plan = { moving: [], leaving: [], reached: [] }
        for (let k = 0; k < live.length; ++k) {
            const role = roles[k]
            if (role === -2) {
                trendLayer.splitBar(plan, live[k], series, max, to)
            } else if (role < 0) {
                trendLayer.sendAway(plan, live[k], to)
            } else {
                plan.reached[role] = true
                // A bar without height adds nothing to a slot that others with
                // height go to: it would only rise inside the bar they form.
                if (live[k].h > 0 || !carried[role])
                    plan.moving.push(trendLayer.settled(live[k], role, series, max))
            }
        }
        for (let j = 0; j < m; ++j) {
            if (!plan.reached[j])
                plan.moving.push(trendLayer.broughtIn(j, series, max, to, from))
        }
        const entries = trendLayer.seenOnly(plan.moving.concat(plan.leaving))
        trendLayer.begin(trendLayer.movesSideways(entries) ? name : "height", entries)
    }

    /// `bar` on its way to slot `slot` of the new series, ending as that slot's bar.
    function settled(bar, slot, series, max) {
        const m = series.length
        const entry = trendLayer.entryFrom(bar)
        entry.s1 = slot / m
        entry.e1 = (slot + 1) / m
        entry.h1 = trendLayer.shareOf(series[slot].count, max)
        return entry
    }

    /// A bar that stands for several new slots: each of their bars starts as it
    /// stands. The empty ones are left out when another has a count, since they
    /// would only sink inside the bar the others still form.
    function splitBar(plan, bar, series, max, to) {
        const m = series.length
        const slack = (to[m] - to[0]) * trendLayer.edgeSlack
        const inside = []
        let filled = false
        for (let j = 0; j < m; ++j) {
            if (to[j] >= bar.t0 - slack && to[j] < bar.t1 - slack) {
                inside.push(j)
                filled = filled || series[j].count > 0
            }
        }
        if (inside.length === 0) {
            trendLayer.sendAway(plan, bar, to)
            return
        }
        for (let k = 0; k < inside.length; ++k) {
            const slot = inside[k]
            plan.reached[slot] = true
            if (filled && !(series[slot].count > 0))
                continue
            const entry = trendLayer.settled(bar, slot, series, max)
            entry.t0 = to[slot]
            entry.t1 = to[slot + 1]
            plan.moving.push(entry)
        }
    }

    /// A bar the new series has nothing for: it leaves along the new axis at the
    /// size it has, or sinks to the baseline when it would end in view.
    function sendAway(plan, bar, to) {
        const entry = trendLayer.entryFrom(bar)
        const spot = trendLayer.strayPlace(bar.t0, bar.t1, to, bar.e - bar.s)
        entry.s1 = spot.s
        entry.e1 = spot.e
        if (!spot.outside)
            entry.h1 = 0
        plan.leaving.push(entry)
    }

    /// A new bar nothing on screen goes to: it comes in along the old axis at
    /// its own size, or rises from the baseline when it would start in view.
    function broughtIn(slot, series, max, to, from) {
        const m = series.length
        const entry = trendLayer.restingEntry(slot, m, series[slot].count, max)
        entry.t0 = to[slot]
        entry.t1 = to[slot + 1]
        const spot = trendLayer.strayPlace(entry.t0, entry.t1, from, 1 / m)
        entry.s0 = spot.s
        entry.e0 = spot.e
        if (!spot.outside)
            entry.h0 = 0
        return entry
    }

    /// Without the bars that have no height at either end, which are never seen.
    function seenOnly(entries) {
        const out = []
        for (let k = 0; k < entries.length; ++k) {
            if (entries[k].h0 > 0 || entries[k].h1 > 0)
                out.push(entries[k])
        }
        return out
    }

    function movesSideways(entries) {
        for (let k = 0; k < entries.length; ++k) {
            if (Math.abs(entries[k].s1 - entries[k].s0) > trendLayer.edgeSlack
                    || Math.abs(entries[k].e1 - entries[k].e0) > trendLayer.edgeSlack)
                return true
        }
        return false
    }

    function begin(name, entries) {
        trendLayer.bars = entries
        trendLayer.progress = 0
        // The clock reads its duration and curve from the phase.
        trendLayer.phase = name
        clock.restart()
    }

    /// Ends any running motion, leaving the bars at rest on the accepted series.
    function stop(rebuildBars) {
        clock.stop()
        trendLayer.phase = ""
        trendLayer.progress = 1
        if (rebuildBars !== false)
            trendLayer.bars = trendLayer.restingBars(trendLayer.shown, trendLayer.shownMax)
    }

    NumberAnimation {
        id: clock
        target: trendLayer
        property: "progress"
        from: 0
        to: 1
        // Heights alone settle quickly; bars that travel take the slower curve
        // that starts and ends gently.
        duration: trendLayer.phase === "height" ? Theme.motionTrendHeight : Theme.motionTrendMorph
        easing.type: Easing.Bezier
        easing.bezierCurve: trendLayer.phase === "height" ? Theme.curveStandard : Theme.curveMorph
        // Emitted when the motion ran to its end, not when stop() replaced it.
        onFinished: trendLayer.stop()
    }

    // Reduced motion switched on mid-way: land at rest.
    Connections {
        target: Theme
        function onMotionChanged() { if (!Theme.motion) trendLayer.stop() }
    }

    ListModel {
        id: barRows
        dynamicRoles: true
    }

    Repeater {
        id: repeater

        model: barRows

        delegate: Rectangle {
            id: bar

            required property var entry
            required property int index

            readonly property real slotStart: bar.entry.s0 + (bar.entry.s1 - bar.entry.s0) * trendLayer.progress
            readonly property real slotEnd: bar.entry.e0 + (bar.entry.e1 - bar.entry.e0) * trendLayer.progress
            // What the slot leaves for the bar once Qt Graphs' margin is taken off.
            readonly property real room: Math.max(0, (bar.slotEnd - bar.slotStart) * trendLayer.width
                                                     - trendLayer.barMargin)
            readonly property real share: bar.entry.h0 + (bar.entry.h1 - bar.entry.h0) * trendLayer.progress

            x: bar.slotStart * trendLayer.width + (bar.room - bar.width) / 2
            y: trendLayer.height - bar.height
            width: bar.room * trendLayer.barRatio
            height: bar.share * trendLayer.height
            radius: 4
            color: trendLayer.barColor
        }
    }
}
