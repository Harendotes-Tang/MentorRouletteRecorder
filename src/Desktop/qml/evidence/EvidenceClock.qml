import QtQuick
import MentorRecorder 1.0

// How long the current 采证 round has run: from its start to now while it is
// active, to ended_at_utc once it has stopped. The tools row, the title bar and
// the page each keep one.
//
// The Collector's 2 h limit counts from Start, the wait for the game included,
// but its status names no start time until recording begins (started_at_utc).
// The clock therefore remembers when it first saw the round (its session id)
// running and counts from the earlier of that and started_at_utc (review K4-6).
// A round first seen after a restart of this app counts from started_at_utc.
QtObject {
    id: clock

    readonly property var status: Runtime.app.validationStatus
    readonly property bool active: Runtime.app.validationActive
    readonly property var startedAt: status.started_at_utc || null
    readonly property var endedAt: status.ended_at_utc || null
    readonly property string roundId: status.session_id ? String(status.session_id) : ""
    // The round first seen running, and when (Date.now()).
    property string seenRound: ""
    property real seenAt: NaN
    property int elapsedSeconds: 0
    readonly property string elapsedText: clock.durationText(elapsedSeconds)
    // Date.now() at the last tick, for what else counts seconds against it.
    property real now: Date.now()

    readonly property Timer ticker: Timer {
        interval: 1000
        repeat: true
        running: clock.active
        triggeredOnStart: true
        onTriggered: clock.recompute()
    }
    onStartedAtChanged: clock.recompute()
    onEndedAtChanged: clock.recompute()
    onActiveChanged: {
        clock.noteRound()
        clock.recompute()
    }
    onRoundIdChanged: {
        clock.noteRound()
        clock.recompute()
    }
    onSeenAtChanged: clock.recompute()
    Component.onCompleted: {
        clock.noteRound()
        clock.recompute()
    }

    function noteRound() {
        if (clock.active && clock.roundId.length > 0 && clock.roundId !== clock.seenRound) {
            clock.seenRound = clock.roundId
            clock.seenAt = Date.now()
        }
    }

    function recompute() {
        clock.now = Date.now()
        let start = clock.startedAt ? Date.parse(String(clock.startedAt)) : NaN
        const seen = clock.roundId.length > 0 && clock.roundId === clock.seenRound ? clock.seenAt : NaN
        if (!isNaN(seen) && (isNaN(start) || seen < start))
            start = seen
        if (isNaN(start)) {
            clock.elapsedSeconds = 0
            return
        }
        const end = clock.endedAt ? Date.parse(String(clock.endedAt)) : NaN
        const until = (!isNaN(end) && !clock.active) ? end : clock.now
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
