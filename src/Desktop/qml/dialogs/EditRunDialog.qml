import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 手动修正 / 新增遗漏记录: a three-step wizard, 结果 → 副本与职业 → 时间与原因.
// The header, the step bar and the action row stay put; only the step body
// scrolls.
//
// Every validation rule lives in the C++ RunFormValidator, so the dialog and
// the unit tests agree by construction and a local refusal carries the same
// error code the Collector would have returned. The three step bodies are
// views only (RunWizardResultStep / RunWizardDutyStep / RunWizardTimeStep);
// the state, the option lists and the step 2 filtering all live here.
Dialog {
    id: dialog

    property var dutyOptions: []
    property var jobOptions: []
    property var runData: null
    property bool editMode: false
    property string externalErrorText: ""
    /// True between "save" and the backend's answer; the buttons disable so a
    /// second click cannot send the same correction twice.
    property bool submitting: false

    /// 1 结果, 2 副本与职业, 3 时间与原因.
    property int currentStep: 1
    // The step switch (docs/ui-design.md 动效): the incoming step fades in and
    // slides 16 px from the side it comes from - the right for 下一步, the left
    // for 上一步. Both styles; with reduced motion it lands at once.
    property real stepProgress: 1
    property int stepDirection: 1
    readonly property bool stepAnimates: Theme.motion
    readonly property NumberAnimation stepSwitch: NumberAnimation {
        target: dialog
        property: "stepProgress"
        from: 0
        to: 1
        duration: Theme.motionMedium
        easing.type: Easing.Bezier
        easing.bezierCurve: Theme.curveStandard
    }

    signal createRequested(var fields, string reason)
    signal correctRequested(var changes, string reason)

    readonly property real overlayWidth: Overlay.overlay && Overlay.overlay.width > 0
                                         ? Overlay.overlay.width : 760
    readonly property real overlayHeight: Overlay.overlay && Overlay.overlay.height > 0
                                          ? Overlay.overlay.height : 712

    modal: true
    width: Math.min(720, overlayWidth - 40)
    height: Math.min(680, overlayHeight - 32)
    closePolicy: Popup.CloseOnEscape
    padding: 0

    /// Kept for callers and tests: set when a result needs an entry time. The
    /// time fields themselves are always on step 3.
    property bool showTimeDetails: false
    property bool estimatedEntry: false
    /// 跨天: shows the separate 进本日期 / 结束日期 fields even while they
    /// equal 日期. They show on their own whenever they differ.
    property bool dayFieldsOpen: false
    readonly property bool dayFieldsVisible: dayFieldsOpen || enteredDate !== matchedDate
                                             || endedDate !== matchedDate
    readonly property bool countsAfterSave: resultCode === "COMPLETED" && contributesToGoal
                                           && !(editMode && runData && runData.soft_deleted)
    readonly property bool countedBefore: editMode && runData && runData.result === "COMPLETED"
                                          && !!runData.contributes_to_goal && !runData.soft_deleted
    readonly property int progressDelta: (countsAfterSave ? 1 : 0) - (countedBefore ? 1 : 0)
    readonly property string progressTitle: progressDelta > 0 ? qsTr("保存后，成就进度 +1")
        : progressDelta < 0 ? qsTr("保存后，成就进度 −1") : qsTr("成就进度不变")
    readonly property string progressExplanation: editMode && runData && runData.soft_deleted
        ? qsTr("这条记录已删除，需要先恢复记录才能计入进度。")
        : resultCode !== "COMPLETED"
        ? qsTr("只有通关的指导者任务才会增加进度；当前结果为「%1」。").arg(resultLabel(resultCode))
        : !contributesToGoal ? qsTr("这条通关记录未选择计入导随成就。")
        : countedBefore ? qsTr("这条导随已经计入进度，修改其他信息不会重复计数。")
        : qsTr("这条已通关的指导者任务将计入 %1 次成就。完成必填信息后保存生效。").arg(App.goalCount)

    property string matchedDate: ""
    property string matchedTime: ""
    property string enteredDate: ""
    property string enteredTime: ""
    property string endedDate: ""
    property string endedTime: ""
    property string resultCode: "COMPLETED"
    property string reasonText: ""
    property string noteText: ""
    property bool contributesToGoal: true
    /// Index into currentDutyOptions(); 0 is 未知副本.
    property int dutyIndex: 0
    /// Index into currentJobOptions(); 0 is 未知.
    property int jobIndex: 0
    property string errorText: ""
    property string errorCode: ""

    // ------------------------------------------------ 第 2 步的筛选状态 --
    /// "" 全部, "4", "8", "24", "0" 其他 - DutyCatalog's party_size group.
    property string partyFilter: ""
    /// "" or "lo-hi".
    property string levelFilter: ""
    /// "" 全部难度, 普通, 极, 零式. Ignored while the difficulty row is hidden.
    property string difficultyFilter: ""
    property string dutyQuery: ""

    readonly property var resultOptions: [
        { value: "COMPLETED", label: qsTr("通关"), desc: qsTr("打完并结算") },
        { value: "LEFT_OR_ABANDONED", label: qsTr("退出/放弃"), desc: qsTr("中途退出或队伍解散") },
        { value: "CANCELLED_BEFORE_ENTRY", label: qsTr("进本前取消"), desc: qsTr("匹配成功但未进本") },
        { value: "DISCONNECTED", label: qsTr("断线"), desc: qsTr("断线导致未完成") },
        { value: "INTERRUPTED", label: qsTr("中断"), desc: qsTr("程序或游戏异常中断") },
        { value: "UNKNOWN", label: qsTr("未知"), desc: qsTr("不确定发生了什么") }
    ]

    readonly property var partyOptions: [
        { value: "", label: qsTr("全部") },
        { value: "4", label: qsTr("4人本") },
        { value: "8", label: qsTr("8人本") },
        { value: "24", label: qsTr("24人本") },
        { value: "0", label: qsTr("其他") }
    ]

    readonly property var levelOptions: [
        { value: "", label: qsTr("全部等级") },
        { value: "1-50", label: "1–50" },
        { value: "51-60", label: "51–60" },
        { value: "61-70", label: "61–70" },
        { value: "71-80", label: "71–80" },
        { value: "81-90", label: "81–90" },
        { value: "91-100", label: "91–100" }
    ]

    readonly property var difficultyOptions: [
        { value: "", label: qsTr("全部难度") },
        { value: "普通", label: qsTr("普通") },
        { value: "极", label: qsTr("极") },
        { value: "零式", label: qsTr("零式") }
    ]

    readonly property bool showDifficultyFilter: partyFilter === "" || partyFilter === "8"
                                                 || partyFilter === "24"

    /// Search by expansion name as well as by "7.x".
    readonly property var versionNames: ({
        "2.x": qsTr("重生之境"), "3.x": qsTr("苍穹之禁城"), "4.x": qsTr("红莲之狂潮"),
        "5.x": qsTr("暗影之逆焰"), "6.x": qsTr("晓月之终途"), "7.x": qsTr("金曦之遗辉")
    })

    /// 随机任务可能派发的副本种类。金碟游乐场、深层迷宫、任务战斗、PVP、寻宝等
    /// 种类随机任务不会派发，列出只会增加翻找成本，故不纳入。
    readonly property var dutyCategories: [
        qsTr("四人迷宫"), qsTr("讨伐歼灭战"), qsTr("大型任务"), qsTr("团队任务"), qsTr("行会令")
    ]

    readonly property string reasonLabel: editMode ? qsTr("修正原因") : qsTr("新增原因")

    // The form as the validator sees it.
    readonly property var formState: ({
        reason: dialog.reasonText,
        reason_label: dialog.reasonLabel,
        date: dialog.matchedDate,
        matched: dialog.matchedTime,
        entered_date: dialog.enteredDate,
        entered: dialog.enteredTime,
        ended_date: dialog.endedDate,
        ended: dialog.endedTime,
        result: dialog.resultCode,
        duty_name: dialog.selectedDutyName(),
        job_name: dialog.selectedJobName(),
        contributes: dialog.contributesToGoal,
        note: dialog.noteText,
        edit_mode: dialog.editMode
    })

    readonly property var beforeState: editMode && runData ? ({
        reason: "",
        date: Fmt.localDate(runData.matched_at_utc),
        matched: dialog.timeOrEmpty(runData.matched_at_utc),
        entered_date: dialog.dateOrEmpty(runData.entered_at_utc),
        entered: dialog.timeOrEmpty(runData.entered_at_utc),
        ended_date: dialog.dateOrEmpty(runData.ended_at_utc),
        ended: dialog.timeOrEmpty(runData.ended_at_utc),
        result: runData.result || "UNKNOWN",
        duty_name: runData.duty_name || qsTr("未知副本"),
        job_name: runData.job_name || qsTr("未知"),
        contributes: !!runData.contributes_to_goal,
        note: runData.note || ""
    }) : ({})

    readonly property var diffRows: editMode ? RunForm.diff(beforeState, formState) : []

    /// Header row plus one entry per changed field. One model entry per row
    /// (rather than per cell) so a row can size itself to its own text.
    readonly property var diffTableRows: {
        const rows = [{
            k: qsTr("字段"),
            a: qsTr("修改前（自动识别原始值保留）"),
            b: qsTr("修改后"),
            head: true
        }]
        for (let i = 0; i < diffRows.length; ++i)
            rows.push({ k: diffRows[i].k, a: diffRows[i].a, b: diffRows[i].b, head: false })
        return rows
    }

    // ------------------------------------------------------ 选项列表 --
    /// 未知副本, then (when correcting a run whose duty the catalogue cannot
    /// name) that run's own duty, then every catalogue row a roulette can hand
    /// out. Each row carries option_index, its own position here, so a
    /// filtered view can still address it. The filters never change this list:
    /// dutyIndex keeps pointing at the same duty whatever is on screen.
    readonly property var dutyOptionList: {
        const unknown = qsTr("未知副本")
        const out = [{ content_id: null, duty_name: unknown, duty_label: unknown, option_index: 0 }]
        const source = dialog.dutyOptions || []
        const run = dialog.editMode ? dialog.runData : null
        // Territory-only and retired catalogue entries still describe a known run.
        // Keep a selectable copy so an unrelated correction cannot erase that duty.
        if (run && ((run.duty_name && run.duty_name !== unknown) || run.territory_id || run.content_id)
                && !source.some(function(row) {
                    return run.content_id != null && row.content_id === run.content_id
                        && dialog.rouletteCategory(row.duty_category)
                })) {
            out.push(Object.assign({}, run, {
                preservesRunDuty: true,
                duty_label: run.duty_name || unknown,
                option_index: out.length
            }))
        }
        for (let i = 0; i < source.length; ++i) {
            const row = source[i]
            // 仅排除确属随机任务不会派发的种类；缺少种类字段的行予以保留，
            // 否则一张缺该字段的副本表会使整个列表为空。
            if (!dialog.rouletteCategory(row.duty_category))
                continue
            out.push(Object.assign({}, row, {
                duty_label: row.duty_name || "",
                option_index: out.length
            }))
        }
        return out
    }

    readonly property var jobOptionList: {
        const out = [{ job_id: null, job_name: qsTr("未知"), option_index: 0 }]
        const source = dialog.jobOptions || []
        for (let i = 0; i < source.length; ++i)
            out.push(Object.assign({}, source[i], { option_index: out.length }))
        return out
    }

    /// Step 2's list: dutyOptionList without 未知副本, filtered and sorted by
    /// level then content id. A filter that is set needs the row to carry the
    /// field it filters on; a row without it (a bare {content_id, duty_name})
    /// still shows while that filter is 全部.
    readonly property var visibleDuties: {
        const list = dialog.dutyOptionList
        const party = dialog.partyFilter
        const difficulty = dialog.showDifficultyFilter ? dialog.difficultyFilter : ""
        const range = dialog.levelRange(dialog.levelFilter)
        const query = dialog.dutyQuery.trim().toLowerCase()
        const rows = []
        for (let i = 1; i < list.length; ++i) {
            const row = list[i]
            if (party !== "" && !(typeof row.party_size === "number" && String(row.party_size) === party))
                continue
            if (difficulty !== "" && row.difficulty !== difficulty)
                continue
            if (range && !(typeof row.duty_level === "number"
                           && row.duty_level >= range[0] && row.duty_level <= range[1]))
                continue
            if (query.length > 0 && !dialog.dutyMatchesQuery(row, query))
                continue
            rows.push(row)
        }
        rows.sort(function(a, b) {
            if (!!a.preservesRunDuty !== !!b.preservesRunDuty)
                return a.preservesRunDuty ? -1 : 1
            const la = typeof a.duty_level === "number" ? a.duty_level : 1000
            const lb = typeof b.duty_level === "number" ? b.duty_level : 1000
            if (la !== lb)
                return la - lb
            const ia = typeof a.content_id === "number" ? a.content_id : 0
            const ib = typeof b.content_id === "number" ? b.content_id : 0
            return ia !== ib ? ia - ib : a.option_index - b.option_index
        })
        return rows
    }

    /// 最近打过: App.recentDuties mapped onto dutyOptionList, at most five.
    /// RunFormValidatorTests' stub App has no such property.
    readonly property var recentDutyOptions: {
        const recent = typeof App !== "undefined" && App && App.recentDuties ? App.recentDuties : []
        const list = dialog.dutyOptionList
        const out = []
        for (let r = 0; r < recent.length && out.length < 5; ++r) {
            const id = recent[r].content_id
            if (id === null || id === undefined)
                continue
            for (let i = 1; i < list.length; ++i) {
                if (!list[i].preservesRunDuty && list[i].content_id === id) {
                    out.push(list[i])
                    break
                }
            }
        }
        return out
    }

    /// Jobs by 职能 in Roles.roleGroups() order; a job whose group is unknown
    /// (a bare {job_id, job_name}) is placed by the bundled job table, and
    /// anything left over goes last under 其他.
    readonly property var jobGroups: {
        const order = typeof Roles !== "undefined" ? Roles.roleGroups() : []
        const buckets = {}
        const named = []
        for (let g = 0; g < order.length; ++g) {
            if (order[g] === "未知")
                continue
            named.push(order[g])
            buckets[order[g]] = []
        }
        const other = []
        const list = dialog.jobOptionList
        for (let i = 1; i < list.length; ++i) {
            const job = list[i]
            const hasCatalog = typeof Jobs !== "undefined"
            const group = job.role_group || (hasCatalog ? Jobs.roleGroup(job.job_id) : "")
            const entry = {
                job_id: job.job_id,
                job_name: job.job_name || "",
                abbreviation: job.abbreviation || (hasCatalog ? Jobs.abbreviation(job.job_id) : "?"),
                option_index: job.option_index
            }
            if (buckets[group] !== undefined)
                buckets[group].push(entry)
            else
                other.push(entry)
        }
        const out = []
        for (let n = 0; n < named.length; ++n) {
            if (buckets[named[n]].length > 0) {
                out.push({
                    role: named[n],
                    token: typeof Jobs !== "undefined" ? Jobs.tokenForRoleGroup(named[n]) : "",
                    jobs: buckets[named[n]]
                })
            }
        }
        if (other.length > 0)
            out.push({ role: qsTr("其他"), token: "neutral400", jobs: other })
        return out
    }

    readonly property var stepItems: [
        { n: 1, label: qsTr("结果"), value: dialog.resultLabel(dialog.resultCode) },
        { n: 2, label: qsTr("副本与职业"),
          value: dialog.selectedDutyName() + " · " + dialog.selectedJobName() },
        { n: 3, label: qsTr("时间与原因"),
          value: dialog.matchedTime.trim().length > 0
                 ? dialog.matchedDate + " " + dialog.matchedTime : dialog.matchedDate }
    ]

    background: DialogFrame { opaque: true }
    Overlay.modal: Rectangle {
        color: Theme.eorzea ? (Theme.dark ? "#99060910" : "#66060910") : Theme.blackScrim
    }

    enter: Transition {
        NumberAnimation { property: "opacity"; from: 0; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
        NumberAnimation { property: "scale"; from: 0.97; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
    }
    exit: Transition {
        NumberAnimation { property: "opacity"; from: 1; to: 0; duration: Theme.motionFast }
    }

    // ------------------------------------------------------------ 逻辑 --
    function rouletteCategory(category) {
        return !category || dialog.dutyCategories.indexOf(category) >= 0
    }

    function levelRange(value) {
        if (!value)
            return null
        const parts = value.split("-")
        return parts.length === 2 ? [Number(parts[0]), Number(parts[1])] : null
    }

    /// Name, category, exact level ("90"), or version: "7", "7.x" and "7.2"
    /// all mean the 7.x expansion (the catalogue knows expansions, not
    /// patches), and so does its Chinese name.
    function dutyMatchesQuery(row, query) {
        if (String(row.duty_name || "").toLowerCase().indexOf(query) >= 0)
            return true
        if (row.duty_category && String(row.duty_category).indexOf(query) >= 0)
            return true
        if (typeof row.duty_level === "number" && String(row.duty_level) === query)
            return true
        const version = row.version || ""
        if (version.length === 0)
            return false
        const major = /^(\d)(?:\.(?:\d+|x)?)?$/i.exec(query)
        if (major && version.charAt(0) === major[1])
            return true
        const name = dialog.versionNames[version] || ""
        return name.length > 0 && name.indexOf(query) >= 0
    }

    function partyIcon(size) {
        switch (size) {
        case 4: return "4"
        case 8: return "8"
        case 24: return "24"
        case 0: return qsTr("令")
        default: return "·"
        }
    }

    function selectResult(code) {
        resultCode = code
        if (code !== "CANCELLED_BEFORE_ENTRY" && !enteredTime.trim())
            showTimeDetails = true
    }

    function pickResult(code) {
        selectResult(code)
    }

    function pickDuty(index) {
        if (index >= 0 && index < dutyOptionList.length)
            dutyIndex = index
    }

    function pickJob(index) {
        if (index >= 0 && index < jobOptionList.length)
            jobIndex = index
    }

    function goToStep(step) {
        const next = Math.max(1, Math.min(3, step))
        if (next === currentStep)
            return
        stepDirection = next > currentStep ? 1 : -1
        currentStep = next
        formScroll.contentY = 0
        stepSwitch.stop()
        if (dialog.stepAnimates) {
            stepProgress = 0
            stepSwitch.start()
        } else {
            stepProgress = 1
        }
        if (next === 2)
            Qt.callLater(dutyStep.showSelection)
    }

    function nextStep() {
        goToStep(currentStep + 1)
    }

    function prevStep() {
        goToStep(currentStep - 1)
    }

    /// Only a user edit of 日期 moves the entry and end dates along, and only
    /// those that were still on the old day.
    function setMatchedDate(value) {
        if (value === matchedDate)
            return
        const previous = matchedDate
        if (enteredDate === previous)
            enteredDate = value
        if (endedDate === previous)
            endedDate = value
        matchedDate = value
    }

    function markCompleted() {
        selectResult("COMPLETED")
        contributesToGoal = true
        reasonText = qsTr("实际已通关，修正识别结果")
    }

    function estimateEntryFromMatch() {
        if (!buildUtc(matchedDate, matchedTime))
            return
        enteredDate = matchedDate
        enteredTime = matchedTime
        estimatedEntry = true
        errorText = ""
        errorCode = ""
        const explanation = qsTr("进本时间按匹配时间估算；实际耗时不详，不计入平均耗时。")
        if (noteText.indexOf(explanation) < 0)
            noteText = noteText.trim() ? noteText.trim() + "\n" + explanation : explanation
    }

    function timeOrEmpty(utc) {
        const value = Fmt.localTime(utc)
        if (value === Fmt.dash())
            return ""
        const millis = new Date(utc).getMilliseconds()
        return millis > 0 ? value + "." + String(millis).padStart(3, "0") : value
    }

    function dateOrEmpty(utc) {
        const value = Fmt.localDate(utc)
        return value === Fmt.dash() ? "" : value
    }

    function optionIndex(list, key, value) {
        if (value === null || value === undefined)
            return 0
        for (let i = 1; i < list.length; ++i) {
            if (list[i][key] === value)
                return i
        }
        return 0
    }

    function resultIndex(code) {
        for (let i = 0; i < resultOptions.length; ++i) {
            if (resultOptions[i].value === code)
                return i
        }
        return 0
    }

    /// The player-facing label for a result code; the raw token is a
    /// maintainer detail and never reaches the normal view.
    function resultLabel(code) {
        return resultOptions[resultIndex(code)].label
    }

    function currentDutyOptions() {
        return dutyOptionList
    }

    function currentJobOptions() {
        return jobOptionList
    }

    function lookupByIndex(list, index) {
        return index >= 0 && index < list.length ? list[index] : null
    }

    function selectedDutyName() {
        const duty = lookupByIndex(dutyOptionList, dutyIndex)
        return duty ? (duty.duty_name || qsTr("未知副本")) : qsTr("未知副本")
    }

    /// 「伊弗利特讨伐战 · 20级 · 2.x」 for the step 2 header.
    function selectedDutySummary() {
        const duty = lookupByIndex(dutyOptionList, dutyIndex)
        if (!duty || dutyIndex === 0)
            return ""
        const parts = [duty.duty_name || qsTr("未知副本")]
        if (typeof duty.duty_level === "number" && duty.duty_level > 0)
            parts.push(qsTr("%1级").arg(duty.duty_level))
        if (duty.version)
            parts.push(duty.version)
        return parts.join(" · ")
    }

    function selectedJobName() {
        const job = lookupByIndex(jobOptionList, jobIndex)
        return job ? (job.job_name || qsTr("未知")) : qsTr("未知")
    }

    function buildUtc(dateText, timeText) {
        if (!timeText || timeText.trim().length === 0)
            return null
        const value = RunForm.toDateTime(dateText, timeText)
        return value ? new Date(value).toISOString() : null
    }

    function fieldUtc(dateText, timeText, key) {
        const original = editMode && runData ? runData[key] : null
        // Leave untouched UTC text intact, including subsecond precision and
        // the original instant if local daylight-saving time is ambiguous.
        if (original && dateText.trim() === dateOrEmpty(original)
                && timeText.trim() === timeOrEmpty(original))
            return original
        return buildUtc(dateText, timeText)
    }

    function refreshRecentDuties() {
        if (typeof App !== "undefined" && App && typeof App.refreshRecentDuties === "function")
            App.refreshRecentDuties()
    }

    function resetWizard() {
        currentStep = 1
        partyFilter = ""
        levelFilter = ""
        difficultyFilter = ""
        dutyQuery = ""
        dayFieldsOpen = false
        formScroll.contentY = 0
    }

    function openForCreate() {
        editMode = false
        runData = null
        matchedDate = Qt.formatDateTime(new Date(), "yyyy-MM-dd")
        enteredDate = matchedDate
        endedDate = matchedDate
        matchedTime = ""
        enteredTime = ""
        endedTime = ""
        resultCode = "COMPLETED"
        reasonText = qsTr("补录遗漏的导随记录")
        noteText = ""
        contributesToGoal = true
        dutyIndex = 0
        jobIndex = 0
        errorText = ""
        errorCode = ""
        externalErrorText = ""
        submitting = false
        estimatedEntry = false
        showTimeDetails = true
        resetWizard()
        refreshRecentDuties()
        open()
    }

    function openForRun(run) {
        editMode = true
        runData = run
        matchedDate = Fmt.localDate(run.matched_at_utc)
        matchedTime = timeOrEmpty(run.matched_at_utc)
        // A missing entry or end starts on the match day, so typing only its
        // time is enough. A date without a time is never sent (buildUtc).
        enteredDate = dateOrEmpty(run.entered_at_utc) || matchedDate
        enteredTime = timeOrEmpty(run.entered_at_utc)
        endedDate = dateOrEmpty(run.ended_at_utc) || matchedDate
        endedTime = timeOrEmpty(run.ended_at_utc)
        resultCode = run.result || "UNKNOWN"
        reasonText = qsTr("手动核对并修正记录")
        noteText = run.note || ""
        contributesToGoal = !!run.contributes_to_goal
        const duties = dutyOptionList
        const retainedIndex = duties.findIndex(function(row) { return row.preservesRunDuty === true })
        dutyIndex = retainedIndex >= 0 ? retainedIndex : optionIndex(duties, "content_id", run.content_id)
        jobIndex = optionIndex(jobOptionList, "job_id", run.job_id)
        errorText = ""
        errorCode = ""
        externalErrorText = ""
        submitting = false
        estimatedEntry = false
        showTimeDetails = false
        resetWizard()
        refreshRecentDuties()
        open()
    }

    function collectFields() {
        const duty = lookupByIndex(dutyOptionList, dutyIndex)
        const preserveDuty = editMode && runData && duty && (duty.preservesRunDuty
                || (runData.content_id != null && duty.content_id === runData.content_id))
        const job = lookupByIndex(jobOptionList, jobIndex)
        const matchedUtc = fieldUtc(matchedDate, matchedTime, "matched_at_utc")
        const enteredUtc = fieldUtc(enteredDate, enteredTime, "entered_at_utc")
        const endedUtc = fieldUtc(endedDate, endedTime, "ended_at_utc")
        const durationUnchanged = editMode && runData
                && enteredUtc === runData.entered_at_utc && endedUtc === runData.ended_at_utc
        // A saved unknown duration stays unknown while its entry time is kept:
        // editing the end of an estimated run must not count queue time. A real
        // entry time restores the calculation. Qt can expose a null QVariant as
        // either null or undefined in QML, hence the loose comparison.
        const unknownDurationRetained = editMode && runData && runData.duration_ms == null
                && enteredUtc && enteredUtc === runData.entered_at_utc
        const durationMs = estimatedEntry || unknownDurationRetained ? null
                : durationUnchanged ? runData.duration_ms : enteredUtc && endedUtc
                ? Math.max(0, new Date(endedUtc) - new Date(enteredUtc))
                : null

        return {
            content_id: preserveDuty ? runData.content_id : duty && duty.content_id !== undefined ? duty.content_id : null,
            territory_id: runData && runData.territory_id !== undefined ? runData.territory_id : null,
            duty_name: preserveDuty ? runData.duty_name : duty && duty.content_id !== null && duty.duty_name !== undefined
                       ? duty.duty_name : null,
            duty_category: preserveDuty ? runData.duty_category : duty && duty.duty_category !== undefined ? duty.duty_category : null,
            duty_level: preserveDuty ? runData.duty_level : duty && duty.duty_level !== undefined ? duty.duty_level : null,
            duty_expansion: preserveDuty ? runData.duty_expansion : duty && duty.duty_expansion !== undefined ? duty.duty_expansion : null,
            job_id: job && job.job_id !== undefined ? job.job_id : null,
            job_name: job && job.job_id !== null && job.job_name !== undefined
                      ? job.job_name : qsTr("未知"),
            role: job && job.role !== undefined ? job.role : "UNKNOWN",
            matched_at_utc: matchedUtc,
            entered_at_utc: enteredUtc,
            ended_at_utc: endedUtc,
            duration_ms: durationMs,
            result: resultCode,
            contributes_to_goal: contributesToGoal,
            note: noteText
        }
    }

    function submit() {
        if (submitting)
            return
        const verdict = RunForm.validate(formState, beforeState)
        errorCode = verdict.ok ? "" : verdict.code
        errorText = verdict.ok ? "" : verdict.message
        if (!verdict.ok)
            return

        const fields = collectFields()
        if (!editMode) {
            // The dialog stays open until AppController reports the outcome:
            // a Collector refusal (ERR_TIME_ORDER, ERR_REASON_REQUIRED …) has
            // to land in this form, not only in a toast that replaces it.
            submitting = true
            createRequested(fields, reasonText.trim())
            return
        }

        const changes = { }
        for (const key in fields) {
            // An untouched empty note is "" here and null in the record: not a change.
            if (key === "note" && !fields.note && !runData.note)
                continue
            if (JSON.stringify(fields[key]) !== JSON.stringify(runData[key]))
                changes[key] = fields[key]
        }
        // A null duration must be explicit when a timestamp changed, otherwise the
        // Collector derives it and counts queue time as if it were measured duty time.
        if (fields.duration_ms === null && (fields.entered_at_utc !== runData.entered_at_utc
                                           || fields.ended_at_utc !== runData.ended_at_utc))
            changes.duration_ms = null
        if (Object.keys(changes).length === 0) {
            errorCode = "ERR_NO_CHANGES"
            errorText = qsTr("没有任何字段被修改。")
            return
        }

        submitting = true
        correctRequested(changes, reasonText.trim())
    }

    /// Called by the shell once the mutation was accepted.
    function acceptSubmission() {
        submitting = false
        close()
    }

    onExternalErrorTextChanged: {
        if (externalErrorText.length > 0) {
            errorText = externalErrorText
            submitting = false
        }
    }

    /// Every field the validator and the Collector can refuse - reason, dates,
    /// times - and the error banner itself are on step 3, so a refusal brings
    /// that step up and scrolls the banner into view.
    onErrorTextChanged: {
        if (errorText.length === 0)
            return
        if (currentStep !== 3)
            goToStep(3)
        Qt.callLater(function() {
            if (formScroll.contentHeight > formScroll.height)
                formScroll.contentY = formScroll.contentHeight - formScroll.height
        })
    }

    // ------------------------------------------------------------ 视图 --
    contentItem: ColumnLayout {
        id: dialogBody

        spacing: 0

        // ------------------------------------------------ 标题与步骤条 --
        ColumnLayout {
            id: headerBlock

            Layout.fillWidth: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            Layout.topMargin: 20
            spacing: 14

            RowLayout {
                Layout.fillWidth: true
                spacing: 12

                HeadingLabel {
                    text: dialog.editMode ? qsTr("手动修正") : qsTr("新增遗漏记录")
                    font.pixelSize: Theme.dialogTitleSize(24)
                }

                Text {
                    Layout.fillWidth: true
                    Layout.alignment: Qt.AlignBaseline
                    // Raw identifiers and column names are maintainer material; a
                    // player sees the same fact in plain Chinese.
                    text: dialog.editMode && dialog.runData
                          ? (App.maintainerToolsVisible
                             ? qsTr("%1 · 当前修订 %2").arg(dialog.runData.run_id)
                                                        .arg(dialog.runData.revision || 1)
                             : qsTr("修正结果与进度，原记录会保留"))
                          : (App.maintainerToolsVisible
                             ? qsTr("来源：手动新增")
                             : qsTr("这条记录由你手动新增"))
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    elide: Text.ElideMiddle
                }

                Text {
                    objectName: "wizardStepLabel"
                    Layout.alignment: Qt.AlignBaseline
                    text: qsTr("第 %1 / 3 步").arg(dialog.currentStep)
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                }
            }

            RowLayout {
                objectName: "wizardStepBar"
                Layout.fillWidth: true
                spacing: 8

                Repeater {
                    model: dialog.stepItems

                    delegate: Item {
                        id: stepSegment

                        required property var modelData
                        readonly property bool active: dialog.currentStep === modelData.n
                        readonly property bool done: dialog.currentStep > modelData.n
                        readonly property color activeColor: Theme.eorzea ? Theme.gold : Theme.accent

                        objectName: "wizardStep_" + modelData.n
                        Layout.fillWidth: true
                        Layout.preferredWidth: 1
                        implicitHeight: 40

                        Accessible.role: Accessible.PageTab
                        Accessible.name: modelData.label
                        Accessible.selected: active
                        Accessible.onPressAction: dialog.goToStep(modelData.n)

                        Rectangle {
                            anchors.left: parent.left
                            anchors.right: parent.right
                            anchors.bottom: parent.bottom
                            height: 2
                            color: stepSegment.active ? stepSegment.activeColor : Theme.border
                            Behavior on color { ColorAnimation { duration: Theme.motionFast } }
                        }

                        RowLayout {
                            anchors.fill: parent
                            anchors.bottomMargin: 4
                            spacing: 8

                            Rectangle {
                                implicitWidth: 20
                                implicitHeight: 20
                                radius: 10
                                color: stepSegment.active ? stepSegment.activeColor
                                       : stepSegment.done ? Theme.accentMuted : "transparent"
                                border.width: 1
                                border.color: stepSegment.active ? stepSegment.activeColor
                                              : (Theme.eorzea ? Theme.gold3 : Theme.neutral300)

                                Text {
                                    anchors.centerIn: parent
                                    text: stepSegment.modelData.n
                                    color: stepSegment.active
                                           ? (Theme.eorzea ? Theme.badgeForeground : "#ffffff")
                                           : stepSegment.done
                                             ? (Theme.eorzea ? Theme.gold2 : Theme.accent)
                                             : Theme.textSecondary
                                    font.pixelSize: 11
                                    font.bold: true
                                }
                            }

                            Text {
                                text: stepSegment.modelData.label
                                color: stepSegment.active ? Theme.textPrimary : Theme.textSecondary
                                font.pixelSize: Theme.fs(13)
                                font.weight: Font.DemiBold
                            }

                            Text {
                                Layout.fillWidth: true
                                horizontalAlignment: Text.AlignRight
                                text: stepSegment.modelData.value
                                color: Theme.textMuted
                                font.pixelSize: Theme.fs(11)
                                elide: Text.ElideRight
                            }
                        }

                        HoverHandler { cursorShape: Qt.PointingHandCursor }
                        TapHandler { onTapped: dialog.goToStep(stepSegment.modelData.n) }
                    }
                }
            }
        }

        // ----------------------------------------------------------- 正文 --
        Flickable {
            id: formScroll

            objectName: "wizardBody"

            /// Width kept free on the right so the scroll bar never sits on
            /// top of a field or of the last diff column.
            readonly property real gutter: 12

            Layout.fillWidth: true
            Layout.fillHeight: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24 - gutter
            clip: true
            contentWidth: width
            contentHeight: stepColumn.implicitHeight + 36
            boundsBehavior: Flickable.StopAtBounds
            // AlwaysOn rather than the AsNeeded used by the pages: a dialog
            // body clipped inside its own frame is not obviously scrollable,
            // and the required reason field can sit below the fold.
            ScrollBar.vertical: ScrollBar {
                policy: formScroll.contentHeight > formScroll.height + 0.5
                        ? ScrollBar.AlwaysOn : ScrollBar.AlwaysOff
            }

            ColumnLayout {
                id: stepColumn

                y: 18
                width: formScroll.width - formScroll.gutter
                spacing: 16
                enabled: !dialog.submitting

                // ------------------------------------------ 第 1 步：结果 --
                RunWizardResultStep {
                    objectName: "wizardStep1"
                    Layout.fillWidth: true
                    visible: dialog.currentStep === 1
                    opacity: dialog.stepProgress
                    transform: Translate { x: (1 - dialog.stepProgress) * 16 * dialog.stepDirection }
                    wizard: dialog
                }

                // ------------------------------------ 第 2 步：副本与职业 --
                RunWizardDutyStep {
                    id: dutyStep

                    objectName: "wizardStep2"
                    Layout.fillWidth: true
                    visible: dialog.currentStep === 2
                    opacity: dialog.stepProgress
                    transform: Translate { x: (1 - dialog.stepProgress) * 16 * dialog.stepDirection }
                    wizard: dialog
                }

                // -------------------------------------- 第 3 步：时间与原因 --
                RunWizardTimeStep {
                    objectName: "wizardStep3"
                    Layout.fillWidth: true
                    visible: dialog.currentStep === 3
                    opacity: dialog.stepProgress
                    transform: Translate { x: (1 - dialog.stepProgress) * 16 * dialog.stepDirection }
                    wizard: dialog
                }
            }
        }

        // ------------------------------------------------------- 按钮栏 --
        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            color: Theme.border
        }

        RowLayout {
            id: actionsRow

            Layout.fillWidth: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            Layout.topMargin: 14
            Layout.bottomMargin: 14
            spacing: 10

            Text {
                Layout.fillWidth: true
                text: dialog.progressTitle
                color: dialog.progressDelta > 0 ? Theme.green : Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                font.weight: Font.DemiBold
                elide: Text.ElideRight
            }

            AppButton {
                text: qsTr("取消")
                enabled: !dialog.submitting
                onClicked: dialog.close()
            }

            AppButton {
                objectName: "prevStepButton"
                visible: dialog.currentStep > 1
                text: qsTr("上一步")
                enabled: !dialog.submitting
                onClicked: dialog.prevStep()
            }

            AppButton {
                objectName: "nextStepButton"
                visible: dialog.currentStep < 3
                variant: "primary"
                text: qsTr("下一步")
                onClicked: dialog.nextStep()
            }

            AppButton {
                variant: "primary"
                objectName: "saveRunButton"
                visible: dialog.currentStep === 3
                enabled: !dialog.submitting && (!dialog.editMode || dialog.diffRows.length > 0)
                text: dialog.submitting
                      ? qsTr("提交中…")
                      : (dialog.editMode ? qsTr("保存为新修订") : qsTr("添加记录"))
                onClicked: dialog.submit()
            }
        }
    }
}
