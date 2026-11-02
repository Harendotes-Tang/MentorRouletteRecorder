import QtQuick
import MentorRecorder 1.0

// How long the current 采证 round has run: from the Collector's started_at_utc to
// now while it is active, to ended_at_utc once it has stopped. The tools row,
// the title bar and the page each keep one.
QtObject {
    id: clock

    readonly property var status: Runtime.app.validationStatus
    readonly property bool active: Runtime.app.validationActive
    readonly property var startedAt: status.started_at_utc || null
    readonly property var endedAt: status.ended_at_utc || null
    property int elapsedSeconds: 0
    readonly property string elapsedText: clock.durationText(elapsedSeconds)

    readonly property Timer ticker: Timer {
        interval: 1000
        repeat: true
        running: clock.active
        triggeredOnStart: true
        onTriggered: clock.recompute()
    }
    onStartedAtChanged: clock.recompute()
    onEndedAtChanged: clock.recompute()
    onActiveChanged: clock.recompute()

    function recompute() {
        const start = clock.startedAt ? Date.parse(String(clock.startedAt)) : NaN
        if (isNaN(start)) {
            clock.elapsedSeconds = 0
            return
        }
        const end = clock.endedAt ? Date.parse(String(clock.endedAt)) : NaN
        const until = (!isNaN(end) && !clock.active) ? end : Date.now()
        clock.elapsedSeconds = Math.max(0, Math.floor((until - start) / 1000))
    }

    // h:mm:ss once past the hour, mm:ss before.
    function durationText(seconds) {
        const total = Math.max(0, Math.floor(seconds || 0))
        const hours = Math.floor(total / 3600)
        const minutes = Math.floor((total % 3600) / 60)
        const rest = total % 60
        const two = function(n) { return (n < 10 ? "0" : "") + n }
        return hours > 0 ? hours + ":" + two(minutes) + ":" + two(rest) : two(minutes) + ":" + two(rest)
    }
}
