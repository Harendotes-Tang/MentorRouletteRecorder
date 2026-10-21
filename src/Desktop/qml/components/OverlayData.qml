import QtQuick
import MentorRecorder

// What the statistics float shows, derived once from the application's own
// models (docs/ui-design.md §4.9): the goal progress, the run in progress, the
// per-duty average, the pending records, and the colours and single-character
// badges of the duty categories. The modules and the mini bar read it; nothing
// here writes to the backend.
QtObject {
    id: data

    readonly property var controller: App.statisticsOverlay
    readonly property bool labels: controller.labels

    // ------------------------------------------------------------ 累计进度 --
    readonly property bool progressKnown: App.achievementSettingsLoaded
    readonly property int progress: Number(App.dashboard.achievement_progress || 0)
    readonly property int goal: Math.max(1, Number(App.goalCount || 0))
    readonly property int baseline: Number(App.baselineCount || 0)
    readonly property real progressRatio: Math.min(1, progress / goal)
    readonly property int remaining: Math.max(0, goal - progress)
    readonly property string progressTip: qsTr("累计进度 %1 / %2（%3）\n还差 %4 次 · 含起始次数 %5 次")
        .arg(progress).arg(goal).arg(Fmt.percent(progressRatio, 1)).arg(remaining).arg(baseline)

    // "+N": the completions counted while the float was open, shown for a
    // few seconds after each one (the design's plus pill). A new 起始次数 or
    // goal moves the figure too but is no completion: like the lap
    // celebration (StatisticsController::observeAchievement), the read that
    // brings it, or one while a save is in flight, only re-primes.
    property int plus: 0
    property int observedProgress: -1
    property int observedBaseline: -1
    property int observedGoal: -1
    readonly property Timer plusTimer: Timer {
        interval: 6000
        onTriggered: data.plus = 0
    }
    // The settings are read from App, not from the bindings above: those may
    // not have caught up with the same dashboard update yet.
    function primeProgress() {
        observedProgress = progress
        observedBaseline = Number(App.baselineCount || 0)
        observedGoal = Number(App.goalCount || 0)
    }
    onProgressChanged: {
        const sameSettings = Number(App.baselineCount || 0) === observedBaseline
                             && Number(App.goalCount || 0) === observedGoal
        if (observedProgress >= 0 && progressKnown && sameSettings && !App.baselineSaving
                && progress > observedProgress) {
            plus += progress - observedProgress
            plusTimer.restart()
        }
        if (progressKnown) primeProgress()
    }
    onProgressKnownChanged: if (progressKnown) primeProgress()

    // ------------------------------------------------------------ 当前副本 --
    readonly property string runState: App.currentRunState
    readonly property var run: App.currentRun && App.currentRun.run && App.currentRun.run.run_id
                               ? App.currentRun.run : null
    readonly property bool matched: !!run && runState === "MENTOR_MATCHED"
    readonly property bool entered: !!run && runState === "ENTERED_DUTY"
    readonly property bool completed: !!run && runState === "COMPLETED"
    readonly property bool ended: !!run && runState === "UNKNOWN_FINAL_STATE"
    /// 当前副本 / 副本用时 are shown while one of these holds.
    readonly property bool inRun: matched || entered || completed || ended
    readonly property string runName: run ? (Theme.dutyName(run) || qsTr("未识别")) : ""
    readonly property string runCategory: run ? String(run.duty_category || "") : ""
    readonly property string runJobName: run ? (Theme.jobName(run) || qsTr("未知")) : ""
    readonly property string runTip: {
        if (matched) return qsTr("已匹配导随，等待进本")
        if (entered) return qsTr("已进入副本 · %1 · %2").arg(runName).arg(runJobName)
        if (completed) return qsTr("通关 · %1 · %2").arg(runName).arg(runJobName)
        if (ended) return qsTr("结果待确认 · %1 · %2").arg(runName).arg(runJobName)
        return ""
    }

    // ------------------------------------------------------------ 副本用时 --
    // Elapsed since entry comes from the controller (extrapolated from the
    // Collector's figure); the wait since the match is counted here.
    property int clockTick: 0
    readonly property Timer clock: Timer {
        interval: 1000
        repeat: true
        running: data.matched
        onTriggered: data.clockTick += 1
    }
    readonly property real elapsedMs: {
        void clockTick
        if (!run) return 0
        if (matched) {
            const matchedAt = Date.parse(String(run.matched_at_utc || ""))
            return isNaN(matchedAt) ? 0 : Math.max(0, Date.now() - matchedAt)
        }
        if (entered) return Number(App.liveElapsedMs || 0)
        return Number(run.duration_ms || 0)
    }
    /// This duty's average over completed runs, from the statistics page's rows.
    readonly property real averageMs: {
        void App.dungeons.count
        if (!run) return 0
        const rows = App.dungeons.topRows(0)
        const contentId = Number(run.content_id || 0)
        const name = String(run.duty_name || "")
        for (let i = 0; i < rows.length; ++i) {
            const row = rows[i]
            const sameId = contentId > 0 && Number(row.content_id || 0) === contentId
            const sameName = contentId <= 0 && name.length > 0 && String(row.duty_name || "") === name
            if (sameId || sameName) return Number(row.avg_duration_ms || 0)
        }
        return 0
    }
    readonly property bool hasAverage: averageMs > 0
    /// The bar spans 130 % of the average, the mark sits at the average.
    readonly property real paceSpanMs: hasAverage ? averageMs * 1.3 : Math.max(1, elapsedMs * 1.3)
    readonly property real paceRatio: Math.min(1, elapsedMs / paceSpanMs)
    readonly property real averageRatio: hasAverage ? averageMs / paceSpanMs : 0
    readonly property bool overAverage: entered && hasAverage && elapsedMs > averageMs
    readonly property bool underAverage: completed && hasAverage && elapsedMs <= averageMs
    readonly property string elapsedText: Fmt.duration(elapsedMs)
    readonly property string averageText: qsTr("均 %1").arg(Fmt.duration(averageMs))
    readonly property string deltaText: {
        if (!completed || !hasAverage) return ""
        const diff = Math.abs(averageMs - elapsedMs)
        return (elapsedMs <= averageMs ? "−" : "+") + Fmt.duration(diff)
    }
    readonly property string timeTip: {
        if (matched) return qsTr("匹配后已过 %1").arg(elapsedText)
        if (entered) return hasAverage ? qsTr("已进行 %1 · 该副本平均 %2").arg(elapsedText).arg(Fmt.duration(averageMs))
                                       : qsTr("已进行 %1").arg(elapsedText)
        if (completed && hasAverage)
            return elapsedMs <= averageMs
                   ? qsTr("用时 %1 · 比平均快 %2").arg(elapsedText).arg(Fmt.duration(averageMs - elapsedMs))
                   : qsTr("用时 %1 · 比平均慢 %2").arg(elapsedText).arg(Fmt.duration(elapsedMs - averageMs))
        return qsTr("用时 %1").arg(elapsedText)
    }

    // ------------------------------------------------------------ 待处理 --
    readonly property var pending: App.recovery ? App.recovery.items : []
    readonly property int pendingCount: pending.length
    // The count holds runs waiting for their result, for a note, or with an
    // unsaved draft, so the words claim none of those for all of them.
    readonly property string pendingTip: qsTr("%1 条记录待处理，点击逐条处理").arg(pendingCount)
    property bool pendingNew: false
    property int observedPending: -1
    readonly property Timer pendingTimer: Timer {
        interval: 3200
        onTriggered: data.pendingNew = false
    }
    onPendingCountChanged: {
        if (observedPending >= 0 && pendingCount > observedPending) {
            pendingNew = true
            pendingTimer.restart()
        }
        observedPending = pendingCount
    }
    function openPending() {
        if (!App.recovery || pendingCount === 0) return
        const first = pending[0]
        App.recovery.requestOpen(first.runId || first.run_id)
    }

    // ------------------------------------------------------------ 颜色 --
    readonly property color ok: Theme.green
    /// The design's --warn: the yellow of the gilded themes, a readable amber
    /// in the light ones.
    readonly property color warn: Theme.eorzea
        ? (Theme.dark ? Theme.yellow : (Theme.harendotes ? "#a77a06" : Theme.yellow))
        : (Theme.dark ? "#e6c26a" : "#c58a12")
    /// Big figures: the heading face in gold (classic: the mono face in text).
    readonly property color figure: Theme.eorzea ? Theme.gold2 : Theme.textPrimary
    readonly property color accent: Theme.eorzea ? Theme.gold2 : Theme.accent
    readonly property color barFill: Theme.eorzea ? Theme.gold2 : Theme.accent

    // The category colours and single-character badges mirror the statistics
    // page's category legend (DungeonStatsModel::categoryBreakdown); 极神 only
    // exists as a bucket there, so it has a badge but never a run category.
    readonly property var categoryKeys: ["四人迷宫", "讨伐歼灭战", "大型任务", "团队任务", "行会令"]
    function categoryToken(category) {
        switch (String(category || "")) {
        case "四人迷宫": return "blue"
        case "讨伐歼灭战": return "orange"
        case "大型任务": return "teal"
        case "团队任务": return "purple"
        case "行会令": return "green"
        default: return "neutral500"
        }
    }
    function categoryColor(category) { return Theme.token(categoryToken(category)) }
    function categoryBadge(category) {
        switch (String(category || "")) {
        case "四人迷宫": return qsTr("迷")
        case "讨伐歼灭战": return qsTr("讨")
        case "大型任务": return qsTr("大")
        case "团队任务": return qsTr("团")
        case "行会令": return qsTr("令")
        default: return "?"
        }
    }
    /// The badge of a legend bucket, found by its localized label.
    function badgeForLabel(label) {
        void Theme.langRevision
        for (let i = 0; i < categoryKeys.length; ++i)
            if (Theme.categoryName(categoryKeys[i]) === label) return categoryBadge(categoryKeys[i])
        if (label === qsTr("极神")) return qsTr("极")
        return "?"
    }
    function roleColor(group) { return Theme.token(Jobs.tokenForRoleGroup(group)) }
}
