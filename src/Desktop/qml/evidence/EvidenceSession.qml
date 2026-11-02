pragma ComponentBehavior: Bound

import QtQuick
import MentorRecorder 1.0

// ---------------------------------------------------------------------------
// 新区服采证 · one round as the page tells it.
//
// Everything the Collector knows comes from App.validationStatus
// ($defs/CaptureValidationStatus); this object only derives the five-step story
// from it (which step is current, is the data readable, is it anomalous) and
// keeps what the page itself remembers for the round: the moment each marker
// was pressed, the 这一轮的情况 chips, which files were copied, the few words the
// 回传说明 needs. None of that is sent anywhere: the report is a text the player
// copies and hands over privately.
//
// Every stored value is replaced, never mutated, so bindings re-evaluate.
// ---------------------------------------------------------------------------
QtObject {
    id: session

    // -------------------------------------------------- the Collector's facts --
    readonly property var status: Runtime.app.validationStatus
    readonly property string state: Runtime.app.validationState
    readonly property bool active: Runtime.app.validationActive
    readonly property bool recording: state === "RECORDING"
    readonly property bool loaded: Runtime.app.validationStatusLoaded
    readonly property bool fileSaved: Runtime.app.validationSaved
    readonly property var startedAt: status.started_at_utc || null
    readonly property var endedAt: status.ended_at_utc || null
    // One round = one Collector session. The id, else the start time, else "".
    readonly property string roundKey: status.session_id ? String(status.session_id)
                                     : (startedAt ? String(startedAt) : "")

    function counter(key) {
        const value = session.status[key]
        return (value === undefined || value === null) ? null : Number(value)
    }
    readonly property var messageCount: session.counter("message_count")
    readonly property var markerCount: session.counter("marker_count")
    readonly property var decodeErrors: session.counter("decode_error_count")
    readonly property var queueDropped: session.counter("queue_dropped")

    // -------------------------------------------------------- the round --
    // 「开始下一轮」: the Collector still reports the finished round, the page
    // is back at step 1 until a new session starts.
    property string dismissedRound: ""
    readonly property bool dismissed: dismissedRound.length > 0 && dismissedRound === roundKey && !active

    // "idle" | "failed" | "cancelled" | "waiting" | "cancelling" | "capturing"
    // | "stopping" | "saved"
    // A wait that is given up (取消等待, or the time limit ran out) passes through
    // STOPPING and ends COMPLETED with reason CANCELLED and no trace. The
    // Collector only counts once recording has started, so a null message_count
    // tells that STOPPING apart from one that is saving a file: the page stays at
    // step 2 meanwhile and is back at step 1 with the Collector's sentence after.
    readonly property string phase: {
        if (session.active)
            return session.state === "WAITING" ? "waiting"
                 : session.state === "STOPPING" ? (session.messageCount === null ? "cancelling" : "stopping")
                 : "capturing"
        if (session.dismissed)
            return "idle"
        if (session.state === "COMPLETED")
            return session.status.reason === "CANCELLED" ? "cancelled" : "saved"
        if (session.state === "FAILED")
            return "failed"
        return "idle"
    }
    readonly property bool idle: phase === "idle" || phase === "failed" || phase === "cancelled"
    // Nothing of a round to show: the clock, the counters and the summary rest.
    readonly property bool blank: phase === "idle" || phase === "cancelled"
    readonly property bool saved: phase === "saved"

    // The game, as the Collector last saw it (GetStatus.game).
    readonly property bool gameRunning: Runtime.app.ffxivRunning
    readonly property bool npcapInstalled: Runtime.app.npcapInstalled
    // Formal recording of a verified build cannot be turned into 采证.
    readonly property bool formalRecording: Runtime.app.capturing
                                            && Runtime.app.protocolProfileStatus === "VERIFIED"
    readonly property bool canStart: idle && npcapInstalled && !gameRunning && !formalRecording
                                     && Runtime.app.templateObservationActionEnabled
                                     && !Runtime.app.captureCommandBusy

    // ------------------------------------------------------ the clock --
    readonly property EvidenceClock clock: EvidenceClock {}
    readonly property int elapsedSeconds: blank ? 0 : clock.elapsedSeconds
    readonly property string elapsedText: clock.durationText(elapsedSeconds)
    readonly property int maxSeconds: 2 * 3600
    function durationText(seconds) { return session.clock.durationText(seconds) }

    // 报文 per second, from one status to the next.
    property real messageRate: 0
    property var lastRateSample: null
    onMessageCountChanged: {
        const now = Date.now()
        const sample = session.lastRateSample
        if (session.messageCount === null || !session.recording) {
            session.messageRate = 0
            session.lastRateSample = null
            return
        }
        if (sample && now > sample.at && session.messageCount >= sample.count)
            session.messageRate = (session.messageCount - sample.count) / ((now - sample.at) / 1000)
        session.lastRateSample = { at: now, count: session.messageCount }
    }

    // ----------------------------------------------------- readability --
    // 报文 never arriving, or decode failures outrunning them: stop and export.
    readonly property bool stalled: recording && messageCount === 0 && elapsedSeconds >= 60
    readonly property bool decodeStorm: recording && decodeErrors !== null && decodeErrors > 0
                                        && decodeErrors >= Math.max(10, messageCount || 0)
    readonly property bool anomaly: stalled || decodeStorm
    readonly property string anomalyTitle: messageCount === 0
                                           ? (decodeErrors > 0 ? qsTr("报文一直为 0，解码失败在快速增加") : qsTr("报文一直为 0"))
                                           : qsTr("解码失败在快速增加")
    readonly property int readableAt: 100
    readonly property real readProgress: messageCount === null ? 0 : Math.min(1, messageCount / readableAt)
    readonly property bool readable: recording && !anomaly && (messageCount || 0) >= readableAt

    // ---------------------------------------------------------- steps --
    readonly property var stepTitles: [
        qsTr("选择区服和网卡"),
        qsTr("启动游戏，登录后确认数据可读"),
        qsTr("排一把随机，按事件标记"),
        qsTr("停止采证，保存文件"),
        qsTr("打包回传给维护者")
    ]
    // Set by 下一步：生成回传说明.
    property bool reportOpen: false
    readonly property int step: {
        switch (session.phase) {
        case "waiting": return 2
        case "cancelling": return 2
        case "capturing": return (session.readable || session.markedAny) && !session.anomaly ? 3 : 2
        case "stopping": return 4
        case "saved": return session.reportOpen ? 5 : 4
        default: return 1
        }
    }
    // Done steps the player reopened, by number.
    property var openedSteps: ({})
    function stepStatus(n) { return n < session.step ? "done" : n === session.step ? "cur" : "todo" }
    function stepOpen(n) { return n === session.step || (n < session.step && !!session.openedSteps[n]) }
    function toggleStep(n) {
        const copy = Object.assign({}, session.openedSteps)
        copy[n] = !copy[n]
        session.openedSteps = copy
    }

    // --------------------------------------------------- region / adapter --
    readonly property string regionLabel: {
        const code = Runtime.app.captureSettings.region_override
        switch (code) {
        case "CN": return qsTr("国服 CN")
        case "TC": return qsTr("繁中服 TC")
        case "GLOBAL": return qsTr("国际服 GLOBAL")
        default: return qsTr("自动")
        }
    }
    // The maintainer's view keeps the adapter id beside the name (K2-4).
    readonly property bool technical: Runtime.app.maintainerToolsVisible
    readonly property var adapterChoices: {
        const choices = [{ adapter_id: "", label: qsTr("自动选择（唯一推荐网卡）") }]
        for (let index = 0; index < Runtime.app.captureAdapters.length; ++index) {
            const row = Runtime.app.captureAdapters[index]
            const name = row.friendly_name || row.description || qsTr("未命名适配器")
            choices.push({
                adapter_id: row.adapter_id,
                label: !session.technical
                       ? (row.recommended ? qsTr("%1 · 推荐").arg(name) : name)
                       : (row.recommended ? qsTr("%1 · %2 · 推荐") : qsTr("%1 · %2"))
                         .arg(name).arg(row.adapter_id)
            })
        }
        return choices
    }
    function adapterIndex(adapterId) {
        for (let index = 0; index < session.adapterChoices.length; ++index) {
            if (session.adapterChoices[index].adapter_id === adapterId)
                return index
        }
        return 0
    }
    readonly property string adapterLabel: {
        const id = Runtime.app.captureAdapterId
        for (let index = 0; index < Runtime.app.captureAdapters.length; ++index) {
            const row = Runtime.app.captureAdapters[index]
            if (row.adapter_id === id)
                return row.friendly_name || row.description || qsTr("未命名适配器")
        }
        return qsTr("自动选择")
    }

    // ---------------------------------------------------------- markers --
    readonly property var markerDefs: [
        { code: "queued", label: qsTr("已排队"), trigger: qsTr("开始排队") },
        { code: "pop", label: qsTr("已匹配"), trigger: qsTr("匹配弹窗出现") },
        { code: "entered", label: qsTr("已进入"), trigger: qsTr("读条结束进本") },
        { code: "victory", label: qsTr("已胜利"), trigger: qsTr("出现通关提示") },
        { code: "left", label: qsTr("已离开"), trigger: qsTr("离本回到外面") }
    ]
    // Per marker: null, "skip", or { at: <ISO 8601 UTC> }.
    property var marks: [null, null, null, null, null]
    readonly property bool markedAny: marks.some(function(mark) { return mark && mark !== "skip" })
    readonly property int markedCount: marks.filter(function(mark) { return mark && mark !== "skip" }).length
    readonly property int nextMarkIndex: marks.indexOf(null)
    function markerEnabled(index) {
        return session.recording && Runtime.app.validationMarkerEnabled && session.marks[index] === null
    }
    // Pressing a later marker records the earlier ones as 漏按 (never back-dated).
    function mark(index) {
        if (!session.markerEnabled(index))
            return
        const copy = session.marks.slice()
        let skipped = false
        for (let earlier = 0; earlier < index; ++earlier) {
            if (copy[earlier] === null) {
                copy[earlier] = "skip"
                skipped = true
            }
        }
        copy[index] = { at: new Date().toISOString() }
        session.marks = copy
        if (skipped)
            session.chips = Object.assign({}, session.chips, { miss: true })
        Runtime.app.addTemplateObservationMarker(session.markerDefs[index].code)
    }

    // --------------------------------------------- roulette and the round --
    property string rouletteType: "mentor"
    property string rouletteOther: ""
    readonly property string rouletteLabel: rouletteType === "mentor" ? qsTr("指导者随机")
                                            : rouletteOther.length > 0 ? qsTr("其他随机（%1）").arg(rouletteOther)
                                            : qsTr("其他随机")
    readonly property var chipDefs: [
        { key: "mid", label: qsTr("中途加入") },
        { key: "fail", label: qsTr("未通关") },
        { key: "miss", label: qsTr("漏按") },
        { key: "late", label: qsTr("点晚") },
        { key: "dc", label: qsTr("断线") }
    ]
    property var chips: ({})
    function toggleChip(key) {
        const copy = Object.assign({}, session.chips)
        copy[key] = !copy[key]
        session.chips = copy
    }
    readonly property var chipsOn: chipDefs.filter(function(chip) { return !!session.chips[chip.key] })
                                           .map(function(chip) { return chip.label })

    // ----------------------------------------------------------- files --
    // The real products of a round (docs/ui-design.md §4.4); the four required
    // ones first, then the two a maintainer asks for only when needed.
    readonly property var fileDefs: [
        { key: "shot", name: qsTr("状态截图"), desc: qsTr("含状态和四项计数"), mono: false, required: true, action: "", hint: "Win + Shift + S" },
        { key: "trace", name: "trace.jsonl", desc: qsTr("本轮取证数据"), mono: true, required: true, action: "folder", hint: "" },
        { key: "sha", name: "trace.jsonl.sha256", desc: qsTr("校验文件，和上面同一目录"), mono: true, required: true, action: "", hint: "" },
        { key: "diag", name: qsTr("诊断报告 JSON"), desc: qsTr("不含游戏内容、网络地址与账号"), mono: false, required: true, action: "diagnostics", hint: "" },
        { key: "ver", name: "ffxivgame.ver", desc: qsTr("无法识别游戏版本时，在游戏目录"), mono: true, required: false, action: "", hint: "" },
        { key: "log", name: "collector-*.log", desc: qsTr("启动或采证出错时，在数据目录 logs"), mono: true, required: false, action: "logs", hint: "" }
    ]
    property var files: ({})
    function toggleFile(key) {
        const copy = Object.assign({}, session.files)
        copy[key] = !copy[key]
        session.files = copy
    }
    readonly property var requiredFiles: fileDefs.filter(function(file) { return file.required })
    readonly property var missingFiles: requiredFiles.filter(function(file) { return !session.files[file.key] })
    readonly property int copiedRequired: requiredFiles.length - missingFiles.length

    // ------------------------------------------------ 采证前可选检查 --
    readonly property var preCheckDefs: [
        { key: "backup", label: qsTr("已在「设置 › 数据」备份正式记录（备份只留在本机）") },
        { key: "lang", label: qsTr("切换界面语言后，区服仍然正确") },
        { key: "split", label: qsTr("各区服的成就分开显示，设置互不影响") },
        { key: "manual", label: qsTr("手动补录能找到对应区服的副本") }
    ]
    property var preChecks: ({})
    function togglePreCheck(key) {
        const copy = Object.assign({}, session.preChecks)
        copy[key] = !copy[key]
        session.preChecks = copy
    }
    readonly property int preChecksDone: preCheckDefs.filter(function(check) { return !!session.preChecks[check.key] }).length

    // ------------------------------------------------------- the report --
    property string vpn: "none"
    property string vpnName: ""
    property string duty: ""
    property string job: ""

    function utcOffsetLabel() {
        const minutes = -new Date().getTimezoneOffset()
        const sign = minutes >= 0 ? "+" : "-"
        const abs = Math.abs(minutes)
        const two = function(n) { return (n < 10 ? "0" : "") + n }
        return "UTC" + sign + two(Math.floor(abs / 60)) + ":" + two(abs % 60)
    }
    function countText(value) { return value === null ? Runtime.fmt.dash() : Runtime.fmt.count(value) }
    readonly property string markerLine: {
        const parts = []
        for (let index = 0; index < session.markerDefs.length; ++index) {
            const mark = session.marks[index]
            if (mark === "skip")
                parts.push(session.markerDefs[index].label + " " + qsTr("漏按"))
            else if (mark)
                parts.push(session.markerDefs[index].label + " " + Runtime.fmt.localTime(mark.at))
        }
        return parts.length > 0 ? parts.join(" · ") : qsTr("无")
    }
    readonly property string report: {
        const unfilled = qsTr("（未填）")
        const vpnText = session.vpn === "none" ? qsTr("未使用")
                      : (session.vpn === "acc" ? qsTr("加速器") : "VPN") + (session.vpnName ? " · " + session.vpnName : "")
        const attached = session.fileDefs.filter(function(file) { return !!session.files[file.key] })
                                         .map(function(file) { return file.name })
        const lines = [
            qsTr("【导随记录器 · 新区服采证回报】"),
            qsTr("软件版本：%1").arg("v" + Runtime.app.appVersion),
            qsTr("游戏版本：%1").arg(Theme.live(Runtime.fmt.gameVersionLabel(Runtime.app.captureStatus.game_build))),
            qsTr("系统：%1").arg(Runtime.app.systemVersion),
            qsTr("测试时间：%1 – %2（%3）")
                .arg(session.startedAt ? Runtime.fmt.localDateTime(session.startedAt) : unfilled)
                .arg(session.endedAt ? Runtime.fmt.localTime(session.endedAt) : (session.active ? qsTr("进行中") : unfilled))
                .arg(session.utcOffsetLabel()),
            qsTr("区服 / 网卡：%1 · %2").arg(session.regionLabel).arg(session.adapterLabel),
            qsTr("加速器或 VPN：%1").arg(vpnText),
            qsTr("随机类型：%1").arg(session.rouletteLabel),
            qsTr("副本 / 职业：%1 · %2").arg(session.duty || unfilled).arg(session.job || unfilled),
            qsTr("标记：%1").arg(session.markerLine),
            qsTr("情况：%1").arg(session.chipsOn.length > 0 ? session.chipsOn.join(qsTr("、")) : qsTr("无")),
            qsTr("计数：报文 %1 · 已写入标记 %2 · 解码失败 %3 · 队列丢弃 %4")
                .arg(session.countText(session.messageCount)).arg(session.countText(session.markerCount))
                .arg(session.countText(session.decodeErrors)).arg(session.countText(session.queueDropped)),
            qsTr("附件：%1").arg(attached.length > 0 ? attached.join(qsTr("、")) : qsTr("无"))
        ]
        if (session.missingFiles.length > 0)
            lines.push(qsTr("缺失：%1").arg(session.missingFiles.map(function(file) { return file.name }).join(qsTr("、"))))
        return lines.join("\n")
    }

    // -------------------------------------------------------- summaries --
    function summary(n) {
        switch (n) {
        case 1:
            return session.regionLabel + " · " + session.adapterLabel
                   + (session.startedAt ? " · " + qsTr("开始于 %1").arg(Runtime.fmt.localTime(session.startedAt)) : "")
        case 2:
            return session.anomaly ? qsTr("数据异常，已停止")
                                   : qsTr("数据可读 · 报文 %1").arg(session.countText(session.messageCount))
        case 3:
            // The Collector's count of written markers outlives this page; the
            // page's own clicks stand in until it has reported one.
            return qsTr("%1 · 标记 %2 / 5").arg(session.rouletteLabel)
                   .arg(session.markerCount !== null ? Math.max(session.markerCount, session.markedCount) : session.markedCount)
        case 4:
            return qsTr("已保存 · 文件 %1 / %2").arg(session.copiedRequired).arg(session.requiredFiles.length)
        default:
            return ""
        }
    }

    // ------------------------------------------------------- the badge --
    readonly property string badgeTone: {
        switch (session.phase) {
        case "waiting": return "warn"
        case "cancelling": return "warn"
        case "capturing": return session.anomaly ? "bad" : "ok"
        case "stopping": return "warn"
        case "saved": return session.fileSaved ? "ok" : "warn"
        case "failed": return "bad"
        default: return "off"
        }
    }
    readonly property string badgeText: {
        switch (session.phase) {
        case "waiting": return qsTr("等待游戏启动")
        case "cancelling": return qsTr("正在取消等待")
        case "capturing": return session.anomaly ? qsTr("数据异常") : qsTr("正在取证")
        case "stopping": return qsTr("正在保存")
        case "saved": return session.fileSaved ? qsTr("已保存") : qsTr("已结束，文件不完整")
        case "failed": return qsTr("失败")
        default: return qsTr("尚未开始")
        }
    }

    // --------------------------------------------------------- actions --
    function start() {
        if (session.canStart)
            Runtime.app.toggleTemplateObservation()
    }
    function stop() {
        if (session.active)
            Runtime.app.toggleTemplateObservation()
    }
    function stopAndExport() {
        session.stop()
        Runtime.app.exportDiagnosticsReport()
    }
    // A new Collector session clears what the page remembered of the last one.
    onRoundKeyChanged: if (session.active) session.resetRound()
    function resetRound() {
        session.marks = [null, null, null, null, null]
        session.chips = {}
        session.files = {}
        session.openedSteps = {}
        session.reportOpen = false
        session.rouletteType = "mentor"
        session.rouletteOther = ""
        session.vpn = "none"
        session.vpnName = ""
        session.duty = ""
        session.job = ""
        session.messageRate = 0
        session.lastRateSample = null
    }
    function startNextRound() {
        session.dismissedRound = session.roundKey
        session.resetRound()
    }
}
