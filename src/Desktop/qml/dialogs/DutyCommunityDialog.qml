pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide
import "DutyCommunityMatch.js" as Match

// 副本提醒与导随宏: tabs, one row of filters, the list on the left and the chosen entry on the
// right. Creating, editing and submitting happen in the right pane, with no second dialog.
// This file holds the state and the Collector calls; the panes read them through `store`.
MotionDialog {
    id: root
    objectName: "dutyCommunityDialog"

    required property var run
    readonly property var service: Runtime.app.dutyCommunity
    readonly property var duty: service.dutyIdentity(run)
    readonly property bool inDuty: !!duty.content_id
    // `run` is a new object on every refresh of the current run; only this tells duties apart.
    readonly property string dutyKey: (duty.content_id || 0) + "/" + (duty.region || "")
    readonly property string currentDutyName: inDuty ? dutyLabel(duty) : ""

    property string tab: "local"
    readonly property bool remote: tab === "public"
    /// "view" | "edit" | "submit"
    property string mode: "view"
    property var localRows: []
    property var publicRows: []
    property var basket: []
    property var callbacks: ({})
    property int serial: 0
    property bool configured: false
    property bool online: false
    property bool refreshing: false
    property string libraryStatus: ""
    property string note: ""
    property string selectedId: ""
    property string copiedKey: ""
    property bool confirmingDelete: false

    property string query: ""
    property var typeFilter: []
    property var regionFilter: []
    property var languageFilter: []
    property var stageFilter: []
    // 副本 (content ids) and 职业 (职能 codes, job abbreviations, "ANY" for 不限) narrow the public
    // library only; the local list has 当前副本与通用 instead.
    property var dutyFilter: []
    property var jobFilter: []
    property bool dutyOnly: false
    property bool favoriteOnly: false

    property string issueText: ""
    property string issueState: ""
    readonly property int issueNumber: /^[0-9]{1,9}$/.test(issueText) ? Number(issueText) : 0

    readonly property var rows: remote ? publicRows : localRows
    readonly property var filtered: {
        const shown = rows.filter(row => matches(row, ""))
        if (!dutyOnly || !inDuty)
            return shown
        const here = row => row.content.content_id === duty.content_id
        return shown.filter(here).concat(shown.filter(row => !here(row)))
    }
    // The open entry and the ticked ones are rows the list shows: an entry folded in a group is
    // reached by opening the group (select does), so nothing hidden is open or acted on.
    readonly property var shownRows: listRows.map(entry => entry.row)
    readonly property var current: mode === "edit" ? null
        : (shownRows.find(row => row.content.id === selectedId) || shownRows[0] || null)
    readonly property string currentId: current ? current.content.id : ""
    readonly property var source: current && current.upstream
        ? (publicRows.find(row => row.content.id === current.upstream.id) || {}).content : null
    readonly property var checkedIds: basket.filter(id => shownRows.some(row => row.content.id === id))
    readonly property bool filterActive: query.trim().length > 0 || typeFilter.length > 0 || regionFilter.length > 0
        || languageFilter.length > 0 || stageFilter.length > 0 || dutyOnly || (!remote && favoriteOnly)
        || (remote && (dutyFilter.length > 0 || jobFilter.length > 0))
    // Public entries that read alike (Match.groups): { id: the id that stands for its group }.
    readonly property var similarGroups: Match.groups(publicRows)
    property var openGroups: ({})
    /// The rows the list draws, as { row, folded, member, group }. On the public tab an entry
    /// that reads like another follows its group's first row, hidden until 「另有 N 条相似」
    /// opens the group (owner, 2026-11-23).
    readonly property var listRows: {
        if (!remote) return filtered.map(row => ({ row: row, folded: 0, member: false, group: "" }))
        const members = ({}), order = []
        for (const row of filtered) {
            const group = similarGroups[row.content.id] || row.content.id
            if (!members[group]) { members[group] = []; order.push(group) }
            members[group].push(row)
        }
        const out = []
        for (const group of order) {
            const head = members[group].find(row => row.content.id === group) || members[group][0]
            out.push({ row: head, folded: members[group].length - 1, member: false, group: group })
            if (openGroups[group])
                for (const row of members[group])
                    if (row !== head) out.push({ row: row, folded: 0, member: true, group: group })
        }
        return out
    }
    function toggleGroup(group) {
        const opening = !openGroups[group]
        const next = Object.assign({}, openGroups)
        next[group] = opening
        // Closing a group whose other entry is open moves to the entry that stays in view.
        const head = listRows.find(entry => entry.group === group && !entry.member)
        if (!opening && head && similarGroups[selectedId] === group)
            selectedId = head.row.content.id
        openGroups = next
    }
    /// Local entries written for the duty the player is in; the dashboard entry shows it.
    readonly property int currentDutyCount: inDuty
        ? localRows.filter(row => row.content.content_id === duty.content_id
                                  && (row.content.region === "ALL" || row.content.region === duty.region)).length
        : 0

    readonly property var regionKeys: ["CN", "TC", "GLOBAL"]
    readonly property var languageKeys: ["zh-Hans", "zh-Hant", "en", "ja"]
    readonly property var typeOptions: [
        { value: "macro", label: qsTr("宏"), sub: qsTr("一行一条指令，复制到游戏里用"),
          count: facet("type", item => item.type === "macro") },
        { value: "reminder", label: qsTr("提醒"), sub: qsTr("记下这个副本要注意的事"),
          count: facet("type", item => item.type === "reminder") }
    ]
    readonly property var regionOptions: regionKeys.map(key => ({ value: key, label: regionLabel(key), sub: "",
        count: facet("region", item => item.region === key || item.region === "ALL") }))
    readonly property var languageOptions: languageKeys.map(key => ({ value: key, label: languageLabel(key), sub: "",
        count: facet("language", item => item.language === key) }))
    readonly property var stageOptions: {
        const counts = ({})
        for (const row of rows.filter(entry => matches(entry, "stage")))
            for (const value of [row.content.boss, row.content.phase])
                if (value) counts[value] = (counts[value] || 0) + 1
        for (const value of stageFilter)
            if (counts[value] === undefined) counts[value] = 0
        return Object.keys(counts).sort((a, b) => counts[b] - counts[a] || a.localeCompare(b))
                     .map(value => ({ value: value, label: value, sub: "", count: counts[value] }))
    }
    /// The duties the public entries are for, 通用 first, then the most entries first.
    readonly property var dutyOptions: {
        if (!remote) return []
        const counts = ({}), sample = ({})
        for (const row of rows.filter(entry => matches(entry, "duty"))) {
            const id = row.content.content_id
            counts[id] = (counts[id] || 0) + 1
            sample[id] = row.content
        }
        for (const id of dutyFilter)
            if (counts[id] === undefined) { counts[id] = 0; sample[id] = { content_id: id, region: "ALL" } }
        return Object.keys(counts).map(Number)
            .map(id => ({ value: id, label: dutyLabel(sample[id]), sub: "", count: counts[id] }))
            .sort((a, b) => (a.value === 0 ? -1 : b.value === 0 ? 1 : b.count - a.count || a.label.localeCompare(b.label)))
    }
    /// 不限 and the 职能 first (the panel shows a dozen rows before a search), then the single
    /// jobs the public entries name.
    readonly property var jobOptions: {
        if (!remote) return []
        const parsed = rows.filter(entry => matches(entry, "job")).map(row => jobCodes(row.content.role))
        const count = choice => parsed.filter(codes => codesMatch(codes, choice)).length
        const out = [{ value: "ANY", label: qsTr("不限职业"), sub: "", count: count("ANY") }]
            .concat(jobGroups.map(group => ({ value: group.code, label: group.label, sub: "", count: count(group.code) })))
        const named = ({})
        for (const codes of parsed)
            for (const code of (codes || [])) named[code] = true
        for (const code of jobFilter) named[code] = true
        for (const group of jobGroups)
            for (const job of group.jobs)
                if (named[job.abbreviation])
                    out.push({ value: job.abbreviation, label: Theme.jobName(job), sub: job.abbreviation,
                               count: count(job.abbreviation) })
        return out
    }
    function jobGroupOf(code) {
        if (jobGroups.some(group => group.code === code)) return code
        const job = jobsByCode[code]
        return job ? roleGroupCodes[job.role_group] || "" : ""
    }
    /// Whether an entry's 职业 (parsed by jobCodes) fits one choice of the filter: a 职能 covers
    /// its jobs and a job its 职能; "ANY" is the entries for every job. Hand-written text fits none.
    function codesMatch(codes, choice) {
        if (codes === null) return false
        if (choice === "ANY") return codes.length === 0
        return codes.some(code => code === choice || jobGroupOf(code) === choice || jobGroupOf(choice) === code)
    }
    // The licence as what others may do with the text (owner: the bare code means nothing to most
    // players); the code follows in brackets for those who know it.
    readonly property var licenseOptions: [
        { value: "CC-BY-4.0", label: qsTr("注明作者即可转载、改编（CC BY 4.0）"),
          detail: qsTr("别人可以复制、修改并再次分享这段内容，只要写明原作者是你。") },
        { value: "CC-BY-SA-4.0", label: qsTr("注明作者，改编后也须同样共享（CC BY-SA 4.0）"),
          detail: qsTr("别人可以复制、修改并再次分享，要写明原作者是你，改过的版本也必须按同样的规则公开。") }
    ]
    function licenseLabel(code) {
        const option = licenseOptions.find(entry => entry.value === code)
        return option ? option.label : String(code || "")
    }
    // The owner's macros (2026-11-22), as written for the CN client. The macro text is not
    // translated: /micon and /marking take names in the client's own language. The Collector
    // refuses more than 15 lines or a line over 180 UTF-8 bytes; a test holds every example to it.
    readonly property var examples: [
        { title: qsTr("实盾姿"), body: [
            "/micon 守护",
            "/p 请开启盾姿：钢铁信念（剑术师/骑士）守护（斧术师/战士）深恶痛绝（暗黑骑士）王室亲卫（绝枪战士）<se.2>"
        ].join("\n") },
        { title: qsTr("单点攻击"), body: [
            "/marking 攻击1 <t>",
            "/p 请各位集中攻击<attack1>！！<se.1>"
        ].join("\n") },
        { title: qsTr("无敌提醒宏"), body: [
            "/micon 神圣领域",
            "/p 我将使用无敌，接下来10s内不需要进行过多治疗。<se.2><se.2>"
        ].join("\n") },
        { title: qsTr("OT宏"), body: [
            "/p ｡ 　　 ∧＿∧｡ﾟ  <se.1>",
            "/p  ﾟ 　(ﾟ ´Д｀ﾟ)っﾟ<se.2>",
            "/p  　　(つ　　/",
            "/p  　 　| （⌒）  <se.3>",
            "/p  　　 し ⌒  <se.4>",
            "/p 有怪在扒拉我！好痛！！ <se.1>",
            "/p 有怪在扒拉我！！  <se.2>",
            "/p 有怪在扒拉我！！  <se.3>"
        ].join("\n") },
        { title: qsTr("禁止攻击宏"), body: [
            "/marking 禁止1 <t> ",
            "/party 请不要攻击<禁止1> <se.1>"
        ].join("\n") },
        { title: qsTr("辛苦宏"), body: [
            "/p    ／l、",
            "/p （°、。７    <se.2>",
            "/p    l、 ~ ヽ",
            "/p   じしf_, )ノ",
            "/p 辛苦了嗷♡ <se.2>"
        ].join("\n") }
    ]
    /// The line a card or a menu row shows for a macro of several lines.
    function firstLine(text) { return String(text || "").split("\n")[0] }

    title: qsTr("副本提醒与导随宏")
    modal: true
    focus: true
    anchors.centerIn: parent
    width: Math.min(1080, parent ? parent.width - 24 : 1080)
    height: Math.min(740, parent ? parent.height - 24 : 740)
    padding: 0
    // The title is drawn in the content, beside the tabs.
    header: null
    // Escape must not throw away a form that is being filled in.
    closePolicy: mode === "view" ? Popup.CloseOnEscape : Popup.NoAutoClose
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(root.palette.shadow) }
    // Ctrl + S saves the form (Design: Ctrl + S 保存), wherever in the dialog the focus is.
    Shortcut {
        objectName: "dutyCommunitySaveShortcut"
        sequences: [StandardKey.Save]
        enabled: root.opened && root.editing
        onActivated: editor.save()
    }

    // ------------------------------------------------------------ filters --
    function matches(row, skip) {
        const item = row.content
        if (skip !== "type" && typeFilter.length && !typeFilter.includes(item.type)) return false
        if (skip !== "region" && regionFilter.length && item.region !== "ALL" && !regionFilter.includes(item.region)) return false
        if (skip !== "language" && languageFilter.length && !languageFilter.includes(item.language)) return false
        if (skip !== "stage" && stageFilter.length && !stageFilter.includes(item.boss) && !stageFilter.includes(item.phase)) return false
        if (remote && skip !== "duty" && dutyFilter.length && !dutyFilter.includes(item.content_id)) return false
        if (remote && skip !== "job" && jobFilter.length) {
            const codes = jobCodes(item.role)
            if (!jobFilter.some(choice => codesMatch(codes, choice))) return false
        }
        if (!remote && favoriteOnly && !row.favorite) return false
        if (dutyOnly && ((item.content_id !== 0 && item.content_id !== duty.content_id)
                         || (item.region !== "ALL" && item.region !== duty.region))) return false
        const term = query.trim().toLowerCase()
        if (!term) return true
        const author = item.author || (row.upstream && row.upstream.author) || ({})
        return [item.title, item.body, item.boss, item.phase, item.role, jobsLabel(item.role), item.content_id,
                item.content_id ? Theme.dutyName(item) : "", author.login].concat(item.tags)
            .join(" ").toLowerCase().includes(term)
    }
    function facet(key, accepts) {
        return rows.filter(row => matches(row, key) && accepts(row.content)).length
    }
    function resetFilters() {
        query = ""
        typeFilter = []
        regionFilter = []
        languageFilter = []
        stageFilter = []
        dutyFilter = []
        jobFilter = []
        dutyOnly = false
        favoriteOnly = false
    }

    // -------------------------------------------------------------- labels --
    function regionLabel(key) {
        switch (key) {
        case "CN": return qsTr("国服 CN")
        case "TC": return qsTr("繁中服 TC")
        case "GLOBAL": return qsTr("国际服 GLOBAL")
        default: return qsTr("全部区服")
        }
    }
    // A language is written in its own words, whatever the interface language is.
    function languageLabel(key) {
        return ({ "zh-Hans": "简体中文", "zh-Hant": "繁體中文", "en": "English", "ja": "日本語" })[key] || key
    }
    function dutyLabel(item) {
        return item.content_id ? (Theme.dutyName(item) || String(item.content_id)) : qsTr("通用")
    }
    function stageLabel(item) {
        return [item.boss, item.phase].filter(value => value).join(" · ")
    }
    // 适用游戏版本 is a range kept in the content's one text field: "6.0-7.3", "-6.0" (up to
    // 6.0), "7.3-" (7.3 to the latest); a version alone is that version only. Anything else
    // was written by hand and is shown as it is.
    function versionRange(value) {
        const text = String(value || "").trim()
        const range = /^([0-9][0-9.]*)?\s*-\s*([0-9][0-9.]*)?$/.exec(text)
        if (range) return { from: range[1] || "", to: range[2] || "" }
        return /^[0-9][0-9.]*$/.test(text) ? { from: text, to: text } : null
    }
    function versionValue(from, to) {
        const first = from.trim().replace(/\.$/, ""), last = to.trim().replace(/\.$/, "")
        return first && first === last ? first : (first || last ? first + "-" + last : "")
    }
    function versionLabel(value) {
        const range = versionRange(value)
        if (!range) return String(value || "")
        if (range.from && range.from === range.to) return range.from
        if (range.from && range.to) return range.from + " → " + range.to
        if (range.to) return qsTr("到 %1").arg(range.to)
        return range.from ? qsTr("%1 → 最新").arg(range.from) : ""
    }
    // 职业 of a macro (Design artifact, 职业怎么存): a whole 职能 is kept as the group itself, so
    // jobs the game adds later count too; single jobs as their abbreviations. Both go into the
    // content's free-text `role` field and read the same in every client: "TANK,BRD". Anything
    // else there was written by hand and is shown as it is.
    readonly property var roleGroupCodes: ({ "坦克": "TANK", "治疗": "HEALER", "近战": "MELEE",
                                             "远程物理": "RANGED", "魔法": "CASTER" })
    /// [{ code, label, token, jobs }] in the 职能 order of the history filters.
    readonly property var jobGroups: {
        const order = typeof Runtime.roles !== "undefined" ? Runtime.roles.roleGroups() : []
        const jobs = typeof Runtime.jobs !== "undefined" ? Runtime.jobs.battleJobs() : []
        return Theme.live(order.filter(group => !!roleGroupCodes[group]).map(group => ({
            code: roleGroupCodes[group], label: Theme.roleGroupName(group),
            token: Runtime.jobs.tokenForRoleGroup(group), jobs: jobs.filter(job => job.role_group === group)
        })).filter(group => group.jobs.length > 0))
    }
    // Built once: search calls jobsLabel for every row on every keystroke.
    readonly property var jobsByCode: {
        const map = ({})
        if (typeof Runtime.jobs !== "undefined")
            for (const job of Runtime.jobs.battleJobs()) map[job.abbreviation] = job
        return map
    }
    function jobCodes(value) {
        const text = String(value || "").trim()
        if (!text) return []
        const codes = text.split(",").map(code => code.trim())
        return codes.every(code => !!jobsByCode[code] || jobGroups.some(group => group.code === code)) ? codes : null
    }
    // A 职能 whose jobs are all chosen becomes the group; the catalogue's order throughout, so
    // one choice is always written the same way.
    function normalizeJobs(codes) {
        let out = []
        for (const group of jobGroups) {
            const own = group.jobs.map(job => job.abbreviation)
            if (codes.includes(group.code) || own.every(code => codes.includes(code)))
                out.push(group.code)
            else
                out = out.concat(own.filter(code => codes.includes(code)))
        }
        return out
    }
    /// 不限, or the 职能 and job names, the first `max` of them (all when 0) and "+N".
    function jobsSummary(codes, max) {
        if (!codes || codes.length === 0) return qsTr("不限")
        const names = codes.map(code => {
            const group = jobGroups.find(entry => entry.code === code)
            return group ? group.label : Theme.jobName(jobsByCode[code])
        })
        const separator = qsTr("、")
        return !max || names.length <= max ? names.join(separator)
                                           : names.slice(0, max).join(separator) + " +" + (names.length - max)
    }
    function jobsLabel(value) {
        const codes = jobCodes(value)
        return codes === null ? String(value || "") : codes.length > 0 ? jobsSummary(codes, 0) : ""
    }
    // 游戏版本 offers the patches, x.0 to x.5 of each expansion, newest first (Design: 只能选已有
    // 的版本号). The game data has no patch list, so a new expansion adds a row here.
    readonly property var gameVersions: {
        const out = []
        for (let major = 7; major >= 2; --major)
            for (let minor = 5; minor >= 0; --minor)
                out.push(major + "." + minor)
        return out
    }
    function isInstalled(id) {
        return localRows.some(row => row.content.id === id || (row.upstream && row.upstream.id === id))
    }
    function defaultLanguage() {
        const code = Runtime.lang ? Runtime.lang.code : ""
        return code === "ja" || code === "en" ? code : code === "zh-TW" ? "zh-Hant" : "zh-Hans"
    }

    // ------------------------------------------------------------ requests --
    function call(action, args, callback) {
        const id = ++serial
        callbacks[id] = callback || report
        service.request(id, action, args)
    }

    Connections {
        target: root.service
        function onAnswered(ticket, result, error) {
            const callback = root.callbacks[ticket]
            delete root.callbacks[ticket]
            if (!callback) return
            if (error && root.visible) root.note = error
            if (result.app_configured !== undefined) root.configured = result.app_configured
            callback(error ? { status: "transport_error" } : result)
        }
    }

    Connections {
        target: Runtime.app
        function onBackendChanged() {
            if (!Runtime.app.backendConnected) return
            if (root.visible) root.reload()
            else root.loadLocal()
        }
    }
    Component.onCompleted: if (Runtime.app.backendConnected) loadLocal()

    function pages(action, args, done, prior, first) {
        call(action, args, result => {
            const batchRows = result.items || result.entries
            if (!batchRows) { refreshing = false; report(result); return }
            const start = first || result
            if (first && result.generation !== first.generation) { refreshing = false; note = statusText("source_changed"); return }
            const all = (prior || []).concat(batchRows)
            if (result.next === null || result.next === undefined) done(all, start)
            else pages(result.items ? "list" : "browse", { offset: result.next }, done, all, start)
        })
    }
    function loadPublic(action, args, then) {
        if (action === "refresh") refreshing = true
        pages(action, args || ({}), (entries, result) => {
            publicRows = entries.map(content => ({ content: content }))
            online = result.online
            libraryStatus = result.status
            refreshing = false
            if (then) then()
        })
    }
    property string revealId: ""
    property bool favoriteBusy: false
    function loadLocal() {
        pages("list", {}, entries => {
            localRows = entries
            favoriteBusy = false
            // An entry just saved stays in view: filters that would hide it are dropped.
            const wanted = revealId
            revealId = ""
            if (wanted && entries.some(row => row.content.id === wanted)
                    && !filtered.some(row => row.content.id === wanted)) {
                resetFilters()
                selectedId = wanted
            }
        })
    }
    function reload() { loadLocal(); loadPublic("browse") }
    /// The one click the offline state offers: the player allows the public library and
    /// asks for it in the same gesture.
    function goOnline() { loadPublic("online", { enabled: true }, () => loadPublic("refresh")) }
    /// 公共共享库 refreshes each time it is opened once the player allowed it online (owner,
    /// 2026-11-23). The Collector answers from its cache within 30 s of its last check (2 min after
    /// a successful one), so switching back and forth does not reach GitHub every time.
    function refreshPublic() { if (online && !refreshing) loadPublic("refresh") }
    onOpened: reload()
    onDutyKeyChanged: if (visible && dutyOnly) { selectedId = ""; compare.close() }
    onTabChanged: { selectedId = ""; basket = []; mode = "view"; confirmingDelete = false; if (tab === "public") refreshPublic() }
    onCurrentIdChanged: confirmingDelete = false

    function openFor(currentDuty) {
        tab = "local"
        resetFilters()
        dutyOnly = currentDuty
        mode = "view"
        selectedId = ""
        basket = []
        note = ""
        submission.copiedBody = ""
        open()
    }

    function select(id) {
        // An entry folded in a group of similar ones is shown by opening its group.
        const group = remote ? similarGroups[id] : undefined
        if (group && !openGroups[group] && !shownRows.some(row => row.content.id === id)) {
            const next = Object.assign({}, openGroups)
            next[group] = true
            openGroups = next
        }
        selectedId = id
        mode = "view"
    }
    function showLocal(id) {
        tab = "local"
        resetFilters()
        select(id)
    }
    function toggleChecked(id) {
        basket = basket.includes(id) ? basket.filter(entry => entry !== id) : basket.concat([id])
    }

    function defaultRegion() {
        const selected = Runtime.app && Runtime.app.recordRegion ? Runtime.app.recordRegion : ""
        return regionKeys.includes(selected) ? selected : "ALL"
    }
    function blankItem(type) {
        return { schema_version: 1, id: service.newId(), type: type,
            // The selected record region unless the author chooses another (owner, 2026-11-24);
            // 全部区服 while none is selected. The duty still defaults to the current one.
            content_id: inDuty ? duty.content_id : 0, region: defaultRegion(),
            language: defaultLanguage(), title: "", body: "", boss: "", phase: "", role: "", tags: [],
            game_version: "", links: [], source: "" }
    }
    function editNew(type, related) {
        const item = blankItem(type)
        if (related) {
            for (const key of ["title", "content_id", "region", "language", "boss", "phase", "role", "game_version"])
                item[key] = related[key]
            item.body = "/p " + related.body.replace(/[\r\n]+/g, " ")
            item.links = [related.id]
        }
        startEdit({ content: item, revision: 0, position: 0, favorite: false })
    }
    function editExample(example) {
        const item = Object.assign(blankItem("macro"), { content_id: 0, title: example.title, body: example.body })
        startEdit({ content: item, revision: 0, position: 0, favorite: false })
    }
    function startEdit(row) {
        tab = "local"
        mode = "edit"
        editor.edit(row)
    }

    function mutate(action, args, nextId) {
        call(action, args, result => {
            report(result)
            if (!["saved", "installed", "deleted", "updated"].includes(result.status)) {
                if (action === "save") startEdit(args)
                return
            }
            if (nextId !== undefined) {
                tab = "local"
                select(nextId)
                revealId = nextId
            }
            loadLocal()
        })
    }
    function toggleFavorite(row) {
        if (favoriteBusy) return
        favoriteBusy = true
        call("save", { content: row.content, revision: row.revision, position: row.position, favorite: !row.favorite },
             result => {
                 if (result.status !== "saved") report(result)
                 loadLocal()
             })
    }
    function installAll(ids) {
        const queue = ids.filter(id => !isInstalled(id))
        if (!queue.length) { note = statusText("already_installed"); return }
        const step = done => {
            if (!queue.length) {
                basket = []
                note = qsTr("已导入 %1 项到本地，可以在「本地」里编辑。").arg(done)
                loadLocal()
                return
            }
            call("install", { id: queue.shift() }, result => {
                if (result.status === "installed" || result.status === "already_installed") step(done + 1)
                else { report(result); loadLocal() }
            })
        }
        step(0)
    }
    function removeCurrent() {
        if (current) mutate("delete", { id: currentId, revision: current.revision }, "")
    }
    function copyBody(copyMode) {
        const row = current
        if (!row) return
        call("copy", { mode: copyMode, body: row.content.body }, result => {
            if (result.status !== "copy_ready") { report(result); return }
            if (!service.copy(result.body)) { note = statusText("copy_failed"); return }
            copiedKey = row.content.id + copyMode
            copiedTimer.restart()
        })
    }
    Timer { id: copiedTimer; interval: 1800; onTriggered: root.copiedKey = "" }

    /// The entries an export or a submission takes: the ticked ones (or the chosen one) and
    /// everything they link to.
    function batch(ids) {
        const queue = ids.slice()
        const picked = []
        while (queue.length) {
            const id = queue.shift()
            if (picked.some(row => row.content.id === id)) continue
            const row = localRows.find(entry => entry.content.id === id)
            if (!row) continue
            picked.push(row)
            row.content.links.forEach(link => queue.push(link))
        }
        return picked
    }
    function exportRows(ids) {
        const status = service.exportFile(batch(ids))
        if (status) report({ status: status })
    }
    function submitRows(ids) {
        const picked = batch(ids)
        // The 20-item limit counts what is sent: entries the public library already has are left
        // out (DutyCommunitySubmission.twins). Unchanged is judged here under the licence the entry
        // was published with; the pane holds the exact count against the licence chosen there.
        const publishedLicense = row => (publicRows.find(entry => entry.content.id === row.content.id)
                                         || { content: {} }).content.license
        const sent = picked.filter(row => !Match.sameTextTwin(row.content, publicRows)
                                          && !Match.unchangedEntry(row.content, publicRows, publishedLicense(row)))
        if (!picked.length || sent.length > 20) { note = statusText("invalid_submission"); return }
        mode = "submit"
        submission.start(picked)
    }
    function importFile() {
        const file = service.pickImport()
        if (file.data) mutate("import_file", { file: file.data })
        else if (file.status) report(file)
    }
    function apply(applyMode) {
        mutate("apply", { id: source.id, local_id: currentId, revision: current.revision,
                          source_revision: source.revision, mode: applyMode })
        compare.close()
    }
    function forkCurrent() {
        const id = service.newId()
        mutate("fork", { id: currentId, local_id: id, revision: current.revision }, id)
    }
    function checkIssue() {
        issueState = "checking"
        call("issue_status", { issue: issueNumber }, result => {
            issueState = result.status === "transport_error" ? "" : result.status
        })
    }

    function report(result) { if (result.status !== "transport_error") note = statusText(result.status) }

    function statusText(status) {
        switch (status) {
        case "ready": return ""
        case "checking": return qsTr("正在查询…")
        case "offline": return qsTr("公共库联网已关闭，仍可使用本地内容与缓存。")
        case "not_refreshed": return qsTr("手动刷新时连接公共共享库。")
        case "refreshing": return qsTr("正在刷新公共共享库。")
        case "not_deployed": return qsTr("公共共享库尚未部署。")
        case "empty": return qsTr("公共共享库暂时没有内容。")
        case "temporarily_unavailable": return qsTr("暂时无法联网或请求受限，保留上次有效缓存。")
        case "invalid_index": return qsTr("公共索引或内容校验失败，保留上次有效缓存。")
        case "stale_mirror": return qsTr("镜像尚未同步，继续使用现有版本。")
        case "saved": return qsTr("已保存到本地。")
        case "deleted": return qsTr("已删除本地条目。")
        case "installed": return qsTr("已导入，可离线使用。")
        case "updated": return qsTr("已应用来源更新。")
        case "already_installed": return qsTr("已安装，请在本地查看或检查更新。")
        case "source_changed": return qsTr("来源版本已变化，请重新刷新和对比。")
        case "source_unavailable": return qsTr("来源或关联内容已撤回或不可用。")
        case "local_conflict": return qsTr("本地有同一条目或更新的修改，请重新加载。")
        case "local_limit": return qsTr("本地库已达到 400 条。")
        case "invalid_title": return qsTr("请填写标题，最多 120 字。")
        case "empty_body": return qsTr("请填写正文，最多 1600 字。")
        case "macro_lines": return qsTr("完整宏最多 15 行。")
        case "macro_length": return qsTr("每行宏最多 180 个 UTF-8 字节。")
        case "chat_length": return qsTr("聊天指令最多 500 个 UTF-8 字节。")
        case "single_chat_required": return qsTr("聊天复制只支持带正文的单行 /p 或 /a 指令。")
        case "copy_failed": return qsTr("剪贴板写入失败或内容不一致。")
        case "copied": return qsTr("已复制到剪贴板。")
        case "invalid_file": return qsTr("文件格式不正确，单次最多 20 条。")
        case "file_error": return qsTr("无法读取或写入此文件。")
        case "exported": return qsTr("已导出。")
        case "invalid_links": return qsTr("关联项必须存在，且提醒和宏只能关联另一种类型。")
        case "invalid_submission": return qsTr("请选择 1 至 20 条内容并确认投稿许可。")
        case "submission_size": return qsTr("投稿文字超出大小限制，请分批投稿。")
        case "app_not_configured": return qsTr("原生投稿尚未配置 GitHub App，可使用浏览器投稿。")
        case "authorizing": return qsTr("请在 GitHub 输入设备代码并确认授权。")
        case "device_expired": return qsTr("授权代码已过期。")
        case "authorization_denied": return qsTr("授权未完成。")
        case "cancelled": return qsTr("已取消授权。")
        case "auth_busy": return qsTr("上一项授权操作尚在处理。")
        case "signed_out": return qsTr("未登录 GitHub。")
        case "signed_in": return qsTr("已登录 GitHub。")
        case "account_changed": return qsTr("GitHub 账号已变化，请重新预览提交。")
        case "submitted": return qsTr("已提交投稿，等待工作流处理。")
        case "processing": return qsTr("投稿正在处理。")
        case "published": return qsTr("已公开发布。刷新共享库后可查看。")
        case "duplicate": return qsTr("投稿的内容公共共享库里都已经有了，没有重复发布。")
        case "processing_failed": return qsTr("处理失败，请打开 GitHub Issue 查看原因。")
        case "submit_unconfirmed": return qsTr("提交结果暂时无法确认，请在 GitHub 检查投稿后再重试。")
        case "closed": return qsTr("投稿已关闭。")
        case "prepared": return qsTr("投稿正文已复制，请粘贴到浏览器表单并提交。")
        case "verified": return qsTr("已核验")
        case "outdated": return qsTr("过时")
        case "withdrawn": return qsTr("已撤回")
        case "community": return qsTr("社区投稿／未核验")
        default: return qsTr("输入无效或操作失败。") + " (" + status + ")"
        }
    }
    function issueTone(status) {
        return status === "published" || status === "duplicate" ? "ok" : status === "processing_failed" || status === "closed" ? "bad"
             : status ? "warn" : "off"
    }

    // -------------------------------------------------------------- layout --
    component Divider: Rectangle {
        Layout.fillWidth: true
        implicitHeight: 1
        color: Theme.border
    }
    component BlankState: ColumnLayout {
        id: blank
        property string iconName: ""
        property string heading: ""
        property string body: ""
        spacing: 10
        Image {
            Layout.alignment: Qt.AlignHCenter
            Layout.preferredWidth: 30
            Layout.preferredHeight: 30
            source: Lucide.source(blank.iconName, Theme.textMuted)
            sourceSize: Qt.size(60, 60)
        }
        // Filling the width lets the column stretch the blank state; otherwise it is only as
        // wide as its widest line and sits at the column's left edge.
        Text {
            renderType: Theme.bodyRenderType
            Layout.fillWidth: true
            horizontalAlignment: Text.AlignHCenter
            wrapMode: Text.Wrap
            text: blank.heading
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(16)
            font.bold: true
        }
        Text {
            renderType: Theme.bodyRenderType
            Layout.alignment: Qt.AlignHCenter
            Layout.maximumWidth: 460
            horizontalAlignment: Text.AlignHCenter
            wrapMode: Text.Wrap
            lineHeight: 1.4
            text: blank.body
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(13)
        }
    }

    // Creating or editing takes the whole dialog (Design artifact, 新建宏): the tabs, filters,
    // list and 投稿进度 step aside until the form is saved or left.
    readonly property bool editing: mode === "edit"
    readonly property bool onboarding: !remote && localRows.length === 0 && !editing
    readonly property bool publicBlank: remote && publicRows.length === 0

    contentItem: ColumnLayout {
        spacing: 0

        RowLayout {
            Layout.fillWidth: true
            Layout.leftMargin: 24
            Layout.rightMargin: 12
            Layout.topMargin: 14
            Layout.bottomMargin: 12
            spacing: 16

            Text {
                renderType: Theme.bodyRenderType
                visible: !root.editing
                text: root.title
                color: Theme.textPrimary
                font.pixelSize: Theme.dialogTitleSize(18)
                font.bold: true
            }
            // While editing: 「← 副本提醒与导随宏 / 新建宏」; the first half goes back to the list.
            RowLayout {
                visible: root.editing
                spacing: 6
                AppButton {
                    objectName: "dutyCommunityBack"
                    variant: "ghost"
                    compact: true
                    iconName: "chevron-left"
                    text: root.title
                    ToolTip.visible: hovered
                    ToolTip.text: qsTr("返回列表")
                    onClicked: editor.cancelled()
                }
                Text {
                    renderType: Theme.bodyRenderType
                    text: "/"
                    color: Theme.textSecondary
                    opacity: 0.55
                }
                Text {
                    objectName: "dutyCommunityEditorHeading"
                    renderType: Theme.bodyRenderType
                    text: editor.heading
                    color: Theme.textPrimary
                    font.pixelSize: Theme.dialogTitleSize(18)
                    font.bold: true
                }
            }
            SegmentedControl {
                objectName: "dutyCommunityTabs"
                visible: !root.editing
                implicitWidth: naturalWidth
                options: [
                    { value: "local", label: qsTr("本地") + "  " + root.localRows.length },
                    { value: "public", label: root.publicRows.length > 0
                        ? qsTr("公共共享库") + "  " + root.publicRows.length : qsTr("公共共享库") }
                ]
                currentValue: root.tab
                // Clicking 公共共享库 while it is open refreshes it as well.
                onActivated: value => { if (value === root.tab && root.remote) root.refreshPublic(); root.tab = value }
            }
            Item { Layout.fillWidth: true }
            DutyGuideButton {
                objectName: "dutyCommunityGuide"
                variant: "ghost"
                run: Runtime.app.currentGuideRun || ({})
            }
            AppButton {
                objectName: "dutyCommunityClose"
                variant: "ghost"
                implicitWidth: implicitHeight
                iconName: "x"
                Accessible.name: qsTr("关闭")
                ToolTip.visible: hovered
                ToolTip.text: qsTr("关闭")
                onClicked: root.close()
            }
        }

        DutyCommunityToolbar {
            Layout.fillWidth: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            Layout.bottomMargin: 12
            visible: !root.editing
            store: root
        }

        Divider {}

        Item {
            Layout.fillWidth: true
            Layout.fillHeight: true

            DutyCommunityEditor {
                id: editor
                anchors.fill: parent
                visible: root.editing
                store: root
                onSaved: value => {
                    root.mode = "view"
                    root.mutate("save", value, value.content.id)
                }
                onCancelled: root.mode = "view"
            }

            RowLayout {
                anchors.fill: parent
                spacing: 0
                visible: !root.onboarding && !root.publicBlank && !root.editing

                DutyCommunityList {
                    Layout.fillWidth: false
                    Layout.preferredWidth: 372
                    Layout.maximumWidth: 372
                    Layout.fillHeight: true
                    store: root
                }
                Rectangle {
                    Layout.fillHeight: true
                    implicitWidth: 1
                    color: Theme.border
                }
                Item {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    Layout.minimumWidth: 0
                    clip: true

                    DutyCommunityDetail {
                        anchors.fill: parent
                        visible: root.mode === "view"
                        store: root
                    }
                    DutyCommunitySubmission {
                        id: submission
                        objectName: "dutyCommunitySubmission"
                        anchors.fill: parent
                        visible: root.mode === "submit"
                        store: root
                        onFinished: root.mode = "view"
                    }
                }
            }

            // Nothing local yet: say what this is for and offer the three ways to start.
            ColumnLayout {
                objectName: "dutyCommunityOnboarding"
                anchors.centerIn: parent
                width: Math.min(parent.width - 48, 780)
                visible: root.onboarding
                spacing: 10

                BlankState {
                    Layout.fillWidth: true
                    iconName: "clipboard-copy"
                    heading: qsTr("还没有提醒或宏")
                    body: qsTr("把进本要说的话、要注意的机制存在这里，进了副本一键复制。")
                }
                RowLayout {
                    Layout.alignment: Qt.AlignHCenter
                    Layout.topMargin: 6
                    spacing: 8
                    AppButton { variant: "primary"; iconName: "plus"; text: qsTr("新建宏"); onClicked: root.editNew("macro", null) }
                    AppButton { iconName: "bell"; text: qsTr("新建提醒"); onClicked: root.editNew("reminder", null) }
                    AppButton { variant: "ghost"; iconName: "globe"; text: qsTr("去公共共享库挑选"); onClicked: root.tab = "public" }
                }
                Text {
                    renderType: Theme.bodyRenderType
                    Layout.alignment: Qt.AlignHCenter
                    Layout.topMargin: 14
                    text: qsTr("或者从常用宏示例开始")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    font.weight: Font.DemiBold
                }
                GridLayout {
                    Layout.fillWidth: true
                    // Up to three a row; fewer examples share the whole width.
                    columns: Math.max(1, Math.min(3, root.examples.length))
                    columnSpacing: 10
                    rowSpacing: 10
                    Repeater {
                        model: root.examples
                        delegate: Button {
                            id: example
                            required property var modelData
                            Layout.fillWidth: true
                            Layout.preferredWidth: 1
                            padding: 10
                            focusPolicy: Qt.StrongFocus
                            Accessible.name: modelData.title
                            ToolTip.visible: hovered
                            ToolTip.text: modelData.body
                            onClicked: root.editExample(modelData)
                            background: InsetBox {
                                border.width: 1
                                border.color: example.hovered || example.activeFocus
                                              ? (Theme.eorzea ? Theme.gold3 : Theme.accent) : Theme.insetBorder
                            }
                            contentItem: ColumnLayout {
                                spacing: 4
                                Text {
                                    renderType: Theme.bodyRenderType
                                    Layout.fillWidth: true
                                    elide: Text.ElideRight
                                    textFormat: Text.PlainText
                                    text: example.modelData.title
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(13)
                                    font.weight: Font.DemiBold
                                }
                                // The macro as typed: <se.2> is a sound tag, not markup.
                                Text {
                                    renderType: Theme.bodyRenderType
                                    Layout.fillWidth: true
                                    elide: Text.ElideRight
                                    textFormat: Text.PlainText
                                    text: root.firstLine(example.modelData.body)
                                    color: Theme.textSecondary
                                    font.family: Theme.monoFamily
                                    font.pixelSize: Theme.fs(12)
                                }
                            }
                        }
                    }
                }
            }

            // The public library has nothing to show: offline, never refreshed, or empty.
            ColumnLayout {
                objectName: "dutyCommunityPublicBlank"
                anchors.centerIn: parent
                width: Math.min(parent.width - 48, 520)
                visible: root.publicBlank
                spacing: 10

                BlankState {
                    Layout.fillWidth: true
                    iconName: root.online ? "globe" : "cloud-off"
                    heading: root.refreshing ? qsTr("正在刷新公共库…")
                           : root.online ? qsTr("公共共享库还没有内容") : qsTr("公共库联网已关闭")
                    body: root.refreshing ? qsTr("第一次刷新需要几秒钟。")
                        : root.online ? root.statusText(root.libraryStatus || "not_refreshed")
                        : qsTr("公共库联网已关闭，仍可使用本地内容与缓存。打开「允许公共库联网」后，可以刷新并浏览大家投稿的内容。")
                }
                AppButton {
                    objectName: "dutyCommunityGoOnline"
                    Layout.alignment: Qt.AlignHCenter
                    Layout.topMargin: 6
                    variant: "primary"
                    enabled: !root.refreshing
                    text: root.online ? qsTr("刷新公共库") : qsTr("允许联网并刷新")
                    onClicked: root.online ? root.loadPublic("refresh") : root.goOnline()
                }
            }
        }

        Divider { visible: !root.editing }

        // 投稿进度: the Issue number is typed in; the state is shown in place.
        RowLayout {
            Layout.fillWidth: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            Layout.topMargin: 10
            Layout.bottomMargin: 10
            visible: !root.editing
            spacing: 10

            Text {
                renderType: Theme.bodyRenderType
                text: qsTr("投稿进度")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }
            StyledTextField {
                id: issueField
                objectName: "dutyCommunityIssue"
                Layout.preferredWidth: 112
                leftPadding: 22
                placeholderText: qsTr("编号")
                Accessible.name: qsTr("投稿的 Issue 编号")
                inputMethodHints: Qt.ImhDigitsOnly
                validator: RegularExpressionValidator { regularExpression: /[0-9]{0,9}/ }
                text: root.issueText
                onTextEdited: { root.issueText = text; root.issueState = "" }
                onAccepted: if (root.issueNumber > 0) root.checkIssue()
                Text {
                    renderType: Theme.bodyRenderType
                    x: 10
                    anchors.verticalCenter: parent.verticalCenter
                    text: "#"
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(13)
                }
            }
            AppButton {
                objectName: "dutyCommunityIssueStatus"
                text: qsTr("查看状态")
                enabled: root.issueNumber > 0 && root.issueState !== "checking"
                onClicked: root.checkIssue()
            }
            AppButton {
                variant: "ghost"
                iconName: "external-link"
                iconAfterText: true
                text: qsTr("在 GitHub 打开")
                enabled: root.issueNumber > 0
                onClicked: root.service.openGitHub("issue", root.issueNumber)
            }
            StatusLed {
                visible: root.issueState.length > 0
                tone: root.issueTone(root.issueState === "checking" ? "" : root.issueState)
            }
            Text {
                renderType: Theme.bodyRenderType
                visible: root.issueState.length > 0
                textFormat: Text.PlainText
                text: root.statusText(root.issueState)
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
            }
            Text {
                objectName: "dutyCommunityNote"
                renderType: Theme.bodyRenderType
                Layout.fillWidth: true
                horizontalAlignment: Text.AlignRight
                elide: Text.ElideRight
                maximumLineCount: 2
                wrapMode: Text.Wrap
                textFormat: Text.PlainText
                text: root.note
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }
            // Another entry opened before the submission was pasted: its steps and 再次复制 stay
            // one click away until 完成.
            AppButton {
                objectName: "dutyCommunityBackToPaste"
                visible: submission.copied && root.mode !== "submit"
                text: qsTr("回到粘贴步骤")
                onClicked: { root.tab = "local"; root.mode = "submit" }
            }
        }
    }

    MotionDialog {
        id: compare
        objectName: "dutyCommunityCompare"
        title: qsTr("来源更新对比")
        anchors.centerIn: parent
        modal: true
        padding: 20
        width: Math.min(640, root.width - 40)
        background: DialogFrame {}
        function show() { open() }
        contentItem: ColumnLayout {
            spacing: 12
            HeadingLabel { Layout.fillWidth: true; text: compare.title }
            ScrollView {
                Layout.fillWidth: true
                Layout.preferredHeight: 225
                StyledTextArea {
                    readOnly: true
                    textFormat: TextEdit.PlainText
                    text: {
                        if (!root.source || !root.current) return ""
                        const old = root.current.upstream
                        const keys = Object.keys(root.source).filter(k => JSON.stringify(root.source[k]) !== JSON.stringify(old[k]))
                        return keys.map(k => k + "\n− " + JSON.stringify(old[k]) + "\n+ " + JSON.stringify(root.source[k])).join("\n\n")
                    }
                }
            }
            RowLayout {
                Layout.alignment: Qt.AlignRight
                spacing: 8
                AppButton { variant: "ghost"; text: qsTr("取消"); onClicked: compare.close() }
                AppButton { text: qsTr("使用来源版本"); enabled: !!root.current && !!root.source; onClicked: root.apply("replace") }
                AppButton { variant: "primary"; text: qsTr("应用更新并保留我的修改"); enabled: !!root.current && !!root.source; onClicked: root.apply("keep") }
            }
        }
    }
    function compareSource() { compare.show() }
}
