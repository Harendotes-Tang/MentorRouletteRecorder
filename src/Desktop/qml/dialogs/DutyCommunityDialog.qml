pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide
import "DutyCommunityMatch.js" as Match
import "DutyCommunityScope.js" as Scope

// 副本提醒与导随宏: tabs, one row of search and filters, then three columns - the duty directory
// that sets the range, the list grouped inside it, and the chosen entry (Design artifact,
// 条目很多时). Editing takes the whole dialog and submitting the right pane; no second dialog.
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
    // What the public library lists. A withdrawn entry cannot be imported any more, so it is not
    // shown there; publicRows keeps it for the status of local copies and for links.
    readonly property var listedPublicRows: publicRows.filter(row => row.content.status !== "withdrawn")
    property var basket: []
    property var callbacks: ({})
    property int serial: 0
    property bool configured: false
    property bool online: false
    property int localRequest: 0
    property int publicRequest: 0
    property string publicAction: ""
    readonly property bool refreshing: publicAction.length > 0
    property string libraryStatus: ""
    property string note: ""
    property string selectedId: ""
    property string copiedKey: ""
    property bool confirmingDelete: false

    property string query: ""
    readonly property string term: query.trim().toLowerCase()
    // 类型 / 职业 / 区服 / 语言, kept per tab. 副本 is chosen in the directory on the left and
    // BOSS / 阶段 became the list's groups (Design: 筛选只留四个).
    property var filters: ({ local: { type: [], job: [], region: [], language: [] },
                             "public": { type: [], job: [], region: [], language: [] } })
    // The public library opens on the player's server and the interface language; once the
    // player chooses a region or language there, that choice is what is remembered.
    property bool publicPreset: true
    readonly property string myRegion: defaultRegion() === "ALL" ? "" : defaultRegion()
    readonly property var presetFilter: {
        const region = myRegion, language = defaultLanguage()
        // No entry of this language on the server: by server alone, so it never opens empty.
        const any = listedPublicRows.some(row => (!region || row.content.region === region || row.content.region === "ALL")
                                           && row.content.language === language)
        return { region: region ? [region] : [], language: any ? [language] : [] }
    }
    readonly property bool preset: remote && publicPreset
    readonly property var typeFilter: filters[tab].type
    readonly property var jobFilter: filters[tab].job
    readonly property var regionFilter: preset ? presetFilter.region : filters[tab].region
    readonly property var languageFilter: preset ? presetFilter.language : filters[tab].language
    function setFilter(key, value) {
        const place = remote && (key === "region" || key === "language")
        const own = Object.assign({}, filters[tab])
        if (place) {
            own.region = regionFilter
            own.language = languageFilter
            publicPreset = false
        }
        own[key] = value
        const next = Object.assign({}, filters)
        next[tab] = own
        filters = next
        basket = []
        if (place) saveView()
    }

    /// What the directory limits the list to: "all", "cur" (the duty the player is in, and
    /// 通用), "fav", "recent", "d:<content id>" (d:0 is 通用) or "t:<category>". Empty follows
    /// the game: 当前副本 inside a duty, 全部 outside.
    property string scope: ""
    readonly property string activeScope: {
        let key = scope || (inDuty ? "cur" : "all")
        if (key === "cur" && !inDuty) key = "all"
        if (remote && (key === "fav" || key === "recent")) key = "all"
        return key
    }
    property string railQuery: ""
    property var railOpen: ({})
    /// Folded groups and groups shown in full, by "<scope>/<group>".
    property var collapsed: ({})
    property var uncapped: ({})
    // Preferences that outlive the dialog (Design: 排序、行高、公共库的区服和语言算偏好).
    property string sortLocal: "updated"
    property string sortPublic: "updated"
    property bool dense: false
    /// When a local entry was last copied, { id: ISO time }; this computer only.
    property var recent: ({})
    readonly property string sortKey: activeScope === "recent" ? "used" : remote ? sortPublic : sortLocal
    readonly property var sortOptions: remote
        ? [{ value: "updated", label: qsTr("最近更新") }, { value: "verified", label: qsTr("已核验的在前") },
           { value: "title", label: qsTr("按标题") }]
        : [{ value: "updated", label: qsTr("最近更新") }, { value: "used", label: qsTr("最近用过") },
           { value: "title", label: qsTr("按标题") }]
    function setSort(value) {
        if (remote) sortPublic = value
        else sortLocal = value
        saveView()
    }
    function setDense(value) { dense = value; saveView() }
    readonly property var settings: typeof Runtime.settings !== "undefined" ? Runtime.settings : null
    function loadView() {
        const view = settings ? settings.dutyCommunityView : ({})
        const list = (value, allowed) => {
            const saved = value ? Array.from(value) : []
            return allowed.filter(entry => saved.includes(entry))
        }
        sortLocal = ["updated", "used", "title"].includes(view.sortLocal) ? view.sortLocal : "updated"
        sortPublic = ["updated", "verified", "title"].includes(view.sortPublic) ? view.sortPublic : "updated"
        dense = view.dense === true
        recent = view.recent && typeof view.recent === "object" ? view.recent : ({})
        publicPreset = view.publicPreset !== false
        const next = Object.assign({}, filters)
        next["public"] = Object.assign({}, filters["public"], { region: list(view.publicRegion, regionKeys),
                                                              language: list(view.publicLanguage, languageKeys) })
        filters = next
    }
    function saveView() {
        if (!settings) return
        settings.dutyCommunityView = { sortLocal: sortLocal, sortPublic: sortPublic, dense: dense, recent: recent,
            publicPreset: publicPreset, publicRegion: filters["public"].region, publicLanguage: filters["public"].language }
    }
    /// Copying a local entry is what 最近用过 remembers: the newest 30.
    function markUsed(id) {
        const next = Object.assign({}, recent)
        next[id] = new Date().toISOString()
        const keep = Object.keys(next).sort((a, b) => next[a] < next[b] ? 1 : -1).slice(0, 30)
        const trimmed = ({})
        for (const key of keep) trimmed[key] = next[key]
        recent = trimmed
        saveView()
    }

    property string issueText: ""
    property string issueState: ""
    readonly property int issueNumber: /^[0-9]{1,9}$/.test(issueText) ? Number(issueText) : 0

    readonly property var rows: remote ? listedPublicRows : localRows
    /// The tab's entries after search and filters, before the directory narrows them.
    readonly property var filtered: rows.filter(row => matches(row, ""))
    // content id -> { category, level, name } for the duties the entries may name.
    readonly property var dutyIndex: {
        const map = ({})
        if (!visible) return map
        const regions = myRegion ? [myRegion] : []
        for (const key of ["CN"].concat(localRows.concat(listedPublicRows).map(row => row.content.region)))
            if (regionKeys.includes(key) && !regions.includes(key)) regions.push(key)
        for (const region of regions)
            for (const entry of Runtime.app.dutyChoicesForRegion(region)) {
                const id = Number(entry.content_id)
                if (!map[id]) map[id] = { category: entry.duty_category || "", level: entry.duty_level || 0,
                                          name: String(entry.duty_name || "") }
            }
        return map
    }
    readonly property var categoryOrder: ["四人迷宫", "讨伐歼灭战", "大型任务", "绝境战", "深层迷宫", "行会令", "任务战斗", "禁地探索"]
    function categoryOf(id) { return id ? ((dutyIndex[id] || {}).category || "other") : "" }
    function categoryLabel(category) { return category === "other" ? qsTr("其他副本") : Theme.categoryName(category) }
    function fitsCurrent(item) {
        return inDuty && (item.content_id === duty.content_id || item.content_id === 0)
            && (item.region === "ALL" || item.region === duty.region)
    }
    function inScope(row, key) {
        const item = row.content
        switch (key) {
        case "all": return true
        case "cur": return fitsCurrent(item)
        case "fav": return !!row.favorite
        case "recent": return !!recent[item.id]
        }
        return key[0] === "d" ? item.content_id === Number(key.slice(2)) : categoryOf(item.content_id) === key.slice(2)
    }
    /// How many of `filtered` each directory entry holds: the numbers follow the search and the
    /// filters, not the directory's own choice, so a search shows where its hits are.
    readonly property var scopeCounts: {
        const n = { all: filtered.length, cur: 0, fav: 0, recent: 0, duty: ({}), kind: ({}) }
        for (const row of filtered) {
            const id = row.content.content_id
            n.duty[id] = (n.duty[id] || 0) + 1
            if (id) { const kind = categoryOf(id); n.kind[kind] = (n.kind[kind] || 0) + 1 }
            if (row.favorite) ++n.fav
            if (recent[row.content.id]) ++n.recent
            if (fitsCurrent(row.content)) ++n.cur
        }
        return n
    }
    function stageOf(item) { return item.boss || item.phase || "" }
    function phaseLabel(phase) {
        switch (phase.code) {
        case "none": return qsTr("未标阶段")
        case "open": return qsTr("开场")
        case "all": return qsTr("全程")
        case "trash": return qsTr("道中")
        case "last": return qsTr("尾王")
        case "boss": return qsTr("BOSS %1").arg(phase.n)
        case "phase": return qsTr("阶段 %1").arg(phase.n)
        case "trans": return qsTr("转场")
        case "end": return qsTr("结束")
        }
        return phase.raw
    }
    /// The entries of the chosen range, in the chosen order. 最近更新 keeps a local list as the
    /// Collector returns it: entries written here carry no time of their own.
    readonly property var scopeRows: {
        const key = activeScope, order = sortKey
        let list = filtered.filter(row => inScope(row, key))
        const at = new Map(list.map((row, index) => [row, index]))
        const updated = row => String(row.content.updated_at || "")
        const used = row => recent[row.content.id] || ""
        const later = (text, a, b) => text(a) < text(b) ? 1 : text(a) > text(b) ? -1 : 0
        const stable = (a, b) => at.get(a) - at.get(b)
        const byUpdated = (a, b) => (remote ? later(updated, a, b) : 0) || stable(a, b)
        const verified = row => row.content.status === "verified" ? 1 : 0
        const compare = order === "used" ? (a, b) => later(used, a, b) || byUpdated(a, b)
                      : order === "title" ? (a, b) => String(a.content.title).localeCompare(String(b.content.title)) || byUpdated(a, b)
                      : order === "verified" ? (a, b) => verified(b) - verified(a) || byUpdated(a, b)
                      : byUpdated
        list = list.slice().sort(compare)
        return key === "recent" ? list.slice(0, 30) : list
    }
    /// The size of the range without search and filters, for 「n / m 项」.
    readonly property int scopeTotal: {
        const n = rows.filter(row => inScope(row, activeScope)).length
        return activeScope === "recent" ? Math.min(30, n) : n
    }
    /// Public entries of the range that only 区服 and 语言 hide (Design: 另有 n 项是其它区服或语言的).
    readonly property int hiddenByPlace: {
        if (!remote || (regionFilter.length === 0 && languageFilter.length === 0)) return 0
        return rows.filter(row => inScope(row, activeScope) && matches(row, "place")).length - scopeRows.length
    }
    function showEveryPlace() {
        const next = Object.assign({}, filters)
        next["public"] = Object.assign({}, filters["public"], { region: [], language: [] })
        filters = next
        publicPreset = false
        basket = []
        saveView()
    }
    // The open entry and the ticked ones are rows of the range: an entry folded in a group of
    // similar ones is reached by opening the group (select does), so nothing hidden is acted on.
    readonly property var shownRows: listRows.map(entry => entry.row)
    readonly property var firstListed: (listEntries.find(entry => entry.kind === "row") || {}).row || null
    readonly property var current: mode === "edit" ? null
        : (shownRows.find(row => row.content.id === selectedId) || firstListed)
    readonly property string currentId: current ? current.content.id : ""
    readonly property var source: current && current.upstream
        ? (publicRows.find(row => row.content.id === current.upstream.id) || {}).content : null
    readonly property var shownIds: new Set(shownRows.map(row => row.content.id))
    readonly property var checkedIds: basket.filter(id => shownIds.has(id))
    readonly property bool filterActive: term.length > 0 || typeFilter.length > 0 || jobFilter.length > 0
        || regionFilter.length > 0 || languageFilter.length > 0
    /// Whether 清除筛选 would change anything.
    readonly property bool resettable: term.length > 0 || typeFilter.length > 0 || jobFilter.length > 0
        || (!remote && (regionFilter.length > 0 || languageFilter.length > 0))
    // Public entries that read alike (Match.groups): { id: the id that stands for its group }.
    readonly property var similarGroups: Match.groups(publicRows)
    property var openGroups: ({})
    /// The rows the list draws, as { row, folded, member, group }. On the public tab an entry
    /// that reads like another follows its group's first row, hidden until 「另有 N 条相似」
    /// opens the group (owner, 2026-11-23).
    readonly property var listRows: {
        if (!remote) return scopeRows.map(row => ({ row: row, folded: 0, member: false, group: "" }))
        const members = ({}), order = []
        for (const row of scopeRows) {
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
    /// What the list draws, top to bottom: { kind: "head" | "row" | "more" | "hint" | "also" }.
    /// Inside a duty the rows are grouped by BOSS / 阶段, inside a category by duty (eight each,
    /// then 查看全部); other ranges are one flat list. An opened group of similar entries stays
    /// under its first entry, whatever stage the others name.
    readonly property var listEntries: {
        const key = activeScope, out = []
        const grouped = (entries, describe) => {
            const map = ({}), list = []
            let last = null
            for (const entry of entries) {
                let group = entry.member ? last : null
                if (!group) {
                    const made = describe(entry.row.content)
                    group = map[made.key]
                    if (!group) {
                        group = map[made.key] = Object.assign({ items: [], cap: 0, go: "", stage: true }, made)
                        list.push(group)
                    }
                }
                group.items.push(entry)
                last = group
            }
            return list.sort((a, b) => a.order - b.order || a.label.localeCompare(b.label))
        }
        const byPhase = entries => grouped(entries, item => {
            const phase = Scope.phase(stageOf(item))
            return { key: "p:" + phase.key, order: phase.order, label: phaseLabel(phase), stage: false }
        })
        const byDuty = entries => grouped(entries, item => ({
            key: "d:" + item.content_id, order: ((dutyIndex[item.content_id] || {}).level || 0) * 100000 + item.content_id,
            label: dutyLabel(item), cap: 8, go: "d:" + item.content_id }))
        let groups = null
        if (key === "cur") {
            const own = [], general = []
            let side = own
            for (const entry of listRows) {
                if (!entry.member) side = entry.row.content.content_id === 0 ? general : own
                side.push(entry)
            }
            // 通用 last, the starred and the lately used first; with a duty's own entries above
            // it shows five until asked for more.
            const used = entry => recent[entry.row.content.id] || ""
            if (!remote)
                general.sort((a, b) => (b.row.favorite ? 1 : 0) - (a.row.favorite ? 1 : 0)
                                       || (used(a) < used(b) ? 1 : used(a) > used(b) ? -1 : 0))
            groups = byPhase(own)
            if (general.length)
                groups.push({ key: "gen", label: qsTr("通用"), items: general, go: "", stage: true,
                              cap: !term && general.length > 8 && own.length > 0 ? 5 : 0 })
        } else if (key[0] === "d") {
            groups = byPhase(listRows)
        } else if (key[0] === "t") {
            groups = byDuty(listRows)
        }
        const heads = !!groups && (groups.length > 1 || (groups.length === 1 && groups[0].key !== "p:none"))
        const flat = !groups || groups.length === 0
        if (flat) groups = [{ key: "flat", label: "", items: listRows, cap: 0, go: "", stage: true }]
        for (const group of groups) {
            const id = key + "/" + group.key
            const open = !heads || !collapsed[id]
            if (heads) out.push({ kind: "head", key: id, label: group.label, count: group.items.length, open: open })
            if (!open) continue
            const limit = group.cap && !uncapped[id] ? group.cap : group.items.length
            for (const entry of group.items.slice(0, limit))
                out.push({ kind: "row", key: id, row: entry.row, folded: entry.folded, member: entry.member,
                           group: entry.group, showDuty: flat, showStage: group.stage })
            if (group.items.length > limit)
                out.push({ kind: "more", key: id, go: group.go, rest: group.items.length - limit, count: group.items.length })
        }
        if (hiddenByPlace > 0) out.push({ kind: "hint", key: "hint" })
        if (term && key !== "all" && scopeCounts.all > scopeRows.length && scopeRows.length > 0)
            out.push({ kind: "also", key: "also" })
        return out
    }
    readonly property bool listGrouped: listEntries.some(entry => entry.kind === "head")
    readonly property bool anyGroupOpen: listEntries.some(entry => entry.kind === "head" && entry.open)
    function toggleListGroup(id) {
        const next = Object.assign({}, collapsed)
        next[id] = !next[id]
        collapsed = next
    }
    function foldAll() {
        const fold = anyGroupOpen, next = Object.assign({}, collapsed)
        for (const entry of listEntries)
            if (entry.kind === "head") next[entry.key] = fold
        collapsed = next
    }
    function showMore(entry) {
        if (entry.go) {
            toggleRailGroup(categoryOf(Number(entry.go.slice(2))), true)
            pickScope(entry.go)
            return
        }
        const next = Object.assign({}, uncapped)
        next[entry.key] = true
        uncapped = next
    }
    function pickScope(key) {
        scope = key
        basket = []
        selectedId = ""
        if (mode !== "edit") mode = "view"
    }
    /// The directory: the fixed ranges, then the duties that have entries, by category and
    /// level. { kind: "item" | "head" | "group" | "none" }.
    readonly property var railEntries: {
        const n = scopeCounts, chosen = activeScope, out = [], find = railQuery.trim().toLowerCase()
        const item = (key, label, icon, count, more) => Object.assign(
            { kind: "item", key: key, label: label, icon: icon, count: count, sub: "", off: false, tip: "",
              level: 1, here: false, on: chosen === key }, more || ({}))
        if (!find) {
            out.push(item("all", qsTr("全部"), "layers", n.all))
            out.push(item("cur", qsTr("当前副本"), "target", inDuty ? n.cur : 0, {
                sub: inDuty ? currentDutyName : qsTr("不在副本中"), off: !inDuty,
                tip: inDuty ? qsTr("只看%1和通用的内容").arg(currentDutyName) : "" }))
            if (!remote) {
                out.push(item("fav", qsTr("收藏"), "star", n.fav))
                out.push(item("recent", qsTr("最近用过"), "history", Math.min(30, n.recent),
                              { tip: qsTr("最近复制过的 30 条") }))
            }
            out.push(item("d:0", qsTr("通用"), "asterisk", n.duty[0] || 0,
                          { tip: qsTr("不分副本都能用的内容，比如开场问候、通关道别") }))
        }
        const selected = chosen[0] === "d" ? Number(chosen.slice(2)) : 0
        const ids = Object.keys(n.duty).map(Number).filter(id => id !== 0)
        if (selected && !ids.includes(selected)) ids.push(selected)
        const groups = ({})
        let shown = 0
        for (const id of ids) {
            const category = categoryOf(id), label = dutyLabel({ content_id: id })
            const names = [label, (dutyIndex[id] || {}).name || "", categoryLabel(category), category].join(" ").toLowerCase()
            if (find && !names.includes(find) && String(id) !== find) continue
            if (!groups[category]) groups[category] = []
            groups[category].push({ id: id, label: label, level: (dutyIndex[id] || {}).level || 0 })
            ++shown
        }
        const rank = category => categoryOrder.indexOf(category) < 0 ? categoryOrder.length : categoryOrder.indexOf(category)
        const categories = Object.keys(groups).sort((a, b) => rank(a) - rank(b) || a.localeCompare(b))
        if (find && categories.length === 0) { out.push({ kind: "none", key: "none" }); return out }
        if (!find && categories.length > 0) out.push({ kind: "head", key: "head", label: qsTr("按副本") })
        const marked = selected || (chosen[0] !== "t" && inDuty ? duty.content_id : 0)
        for (const category of categories) {
            const duties = groups[category].sort((a, b) => a.level - b.level || a.id - b.id)
            const on = chosen === "t:" + category
            const open = railOpen[category] !== undefined ? !!railOpen[category]
                       : (!!find || shown <= 10 || on || duties.some(entry => entry.id === marked))
            out.push({ kind: "group", key: "t:" + category, category: category, label: categoryLabel(category),
                       count: n.kind[category] || 0, open: open, on: on })
            if (open)
                for (const entry of duties)
                    out.push(item("d:" + entry.id, entry.label, "", n.duty[entry.id] || 0,
                                  { level: 2, here: inDuty && entry.id === duty.content_id }))
        }
        return out
    }
    function toggleRailGroup(category, open) {
        const next = Object.assign({}, railOpen)
        next[category] = open
        railOpen = next
    }
    /// What the empty list says, by range (Design).
    readonly property string emptyText: scopeRows.length > 0 ? ""
        : filterActive ? qsTr("没有匹配的内容")
        : activeScope === "fav" ? qsTr("还没有收藏。点列表里的星标，把常用的收进来。")
        : activeScope === "recent" ? qsTr("复制过的内容会出现在这里。")
        : activeScope === "cur" ? qsTr("%1和通用都还没有内容").arg(currentDutyName)
        : qsTr("这里还没有内容")
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
    /// 不限 and the 职能 first (the panel shows a dozen rows before a search), then the single
    /// jobs the public entries name.
    readonly property var jobOptions: {
        const parsed = rows.filter(entry => inScope(entry, activeScope) && matches(entry, "job"))
                           .map(row => jobCodes(row.content.role))
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
    /// The detail shows the code alone (Design: 许可：CC BY 4.0) and says the rest on hover.
    function licenseCode(code) {
        return String(code || "").replace(/^CC-BY-SA-/, "CC BY-SA ").replace(/^CC-BY-/, "CC BY ")
    }
    function licenseDetail(code) {
        const option = licenseOptions.find(entry => entry.value === code)
        return option ? option.detail : ""
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
    width: Math.min(1320, parent ? parent.width - 24 : 1320)
    height: Math.min(860, parent ? parent.height - 24 : 860)
    padding: 0
    // The title is drawn in the content, beside the tabs.
    header: null
    // Escape must not throw away a form that is being filled in.
    closePolicy: mode === "view" ? Popup.CloseOnEscape : Popup.NoAutoClose
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(root.palette.shadow) }
    // Ctrl + F goes to the search box (Design: 搜索的范围).
    Shortcut {
        sequences: [StandardKey.Find]
        enabled: root.opened && !root.editing
        onActivated: toolbar.focusSearch()
    }
    // Ctrl + S saves the form (Design: Ctrl + S 保存), wherever in the dialog the focus is.
    Shortcut {
        objectName: "dutyCommunitySaveShortcut"
        sequences: [StandardKey.Save]
        enabled: root.opened && root.editing
        onActivated: editor.save()
    }

    // ------------------------------------------------------------ filters --
    // The text a search looks through, built once per entry and load.
    property var haystacks: ({})
    onLocalRowsChanged: haystacks = ({})
    onPublicRowsChanged: haystacks = ({})
    function haystack(row) {
        const item = row.content, key = tab + "/" + item.id
        let text = haystacks[key]
        if (text === undefined) {
            const author = item.author || (row.upstream && row.upstream.author) || ({})
            text = haystacks[key] = [item.title, item.body, item.boss, item.phase, item.role, jobsLabel(item.role),
                item.content_id, item.content_id ? Theme.dutyName(item) : qsTr("通用"),
                (dutyIndex[item.content_id] || {}).name || "", author.login].concat(item.tags).join(" ").toLowerCase()
        }
        return text
    }
    /// `skip` leaves one filter out: its own key, or "place" for 区服 and 语言 together.
    function matches(row, skip) {
        const item = row.content, place = skip === "place"
        if (skip !== "type" && typeFilter.length && !typeFilter.includes(item.type)) return false
        if (!place && skip !== "region" && regionFilter.length && item.region !== "ALL"
                && !regionFilter.includes(item.region)) return false
        if (!place && skip !== "language" && languageFilter.length && !languageFilter.includes(item.language)) return false
        if (skip !== "job" && jobFilter.length) {
            const codes = jobCodes(item.role)
            if (!jobFilter.some(choice => codesMatch(codes, choice))) return false
        }
        return !term || haystack(row).includes(term)
    }
    /// How many entries of the range a choice would leave, the other filters unchanged.
    function facet(key, accepts) {
        return rows.filter(row => inScope(row, activeScope) && matches(row, key) && accepts(row.content)).length
    }
    /// 清除筛选: the search, 类型 and 职业. On the public tab 区服 and 语言 stay as they are
    /// (owner, 2026-11-25): they say whose content is shown and are changed on their own capsules
    /// or by 「也显示出来」, so clearing a search never drops them.
    function resetFilters() {
        query = ""
        const next = Object.assign({}, filters)
        next[tab] = remote ? Object.assign({}, filters[tab], { type: [], job: [] })
                           : { type: [], job: [], region: [], language: [] }
        filters = next
        basket = []
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
    /// The refreshed public library by entry ID.
    readonly property var publicById: {
        const out = ({})
        for (const row of publicRows) out[row.content.id] = row.content
        return out
    }
    /// The public entry this library published under the given ID, or null: a local entry
    /// written or adapted here (not an installed copy of someone's entry) whose ID the public
    /// library holds. Only the GitHub account that published it can withdraw it; the workflow
    /// checks that, the app cannot.
    function ownPublication(id) {
        const entry = publicById[id]
        const local = entry ? localRows.find(row => row.content.id === id) : null
        return local && !(local.upstream && local.upstream.id === id) ? entry : null
    }
    function withdrawable(id) {
        const entry = ownPublication(id)
        return !!entry && entry.status !== "withdrawn"
    }
    function defaultLanguage() {
        const code = Runtime.lang ? Runtime.lang.code : ""
        return code === "ja" || code === "en" ? code : code === "zh-TW" ? "zh-Hant" : "zh-Hans"
    }

    // ------------------------------------------------------------ requests --
    function call(action, args, callback, isCurrent) {
        const id = ++serial
        callbacks[id] = { done: callback || report, isCurrent: isCurrent }
        service.request(id, action, args)
    }

    Connections {
        target: root.service
        function onAnswered(ticket, result, error) {
            const request = root.callbacks[ticket]
            delete root.callbacks[ticket]
            if (!request || (request.isCurrent && !request.isCurrent())) return
            if (error && root.visible) root.note = error
            if (result.app_configured !== undefined) root.configured = result.app_configured
            // A build without a GitHub App offers the browser route alone.
            if (result.status === "app_not_configured") root.configured = false
            request.done(error ? { status: "transport_error" } : result)
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
    Component.onCompleted: { loadView(); if (Runtime.app.backendConnected) loadLocal() }

    function pages(action, args, done, failed, isCurrent) {
        const all = []
        function fetchPage(action, args, first) {
            call(action, args, result => {
                const batchRows = result.items || result.entries
                if (!batchRows) { failed(result); return }
                if (first && result.generation !== first.generation) { failed({ status: "source_changed" }); return }
                const start = first || result
                all.push(...batchRows)
                if (result.next === null || result.next === undefined) done(all, start)
                else fetchPage(result.items ? "list" : "browse", { offset: result.next }, start)
            }, isCurrent)
        }
        fetchPage(action, args, null)
    }
    function loadPublic(action, args) {
        const request = ++publicRequest
        publicAction = action
        pages(action, args || ({}), (entries, result) => {
            publicRows = entries.map(content => ({ content: content }))
            online = result.online
            libraryStatus = result.status
            if (action === "online" && online) loadPublic("refresh")
            else publicAction = ""
        }, result => {
            publicAction = ""
            report(result)
        }, () => request === publicRequest)
    }
    property string revealId: ""
    property bool favoriteBusy: false
    function loadLocal() {
        const request = ++localRequest
        pages("list", {}, entries => {
            localRows = entries
            favoriteBusy = false
            // An entry just saved stays in view: filters that would hide it are dropped. A form
            // opened meanwhile is left as it is.
            const wanted = revealId
            revealId = ""
            if (wanted && !editing) reveal(wanted)
        }, result => { favoriteBusy = false; report(result) }, () => request === localRequest)
    }
    function reload() { loadLocal(); loadPublic("browse") }
    /// The one click the offline state offers: the player allows the public library and
    /// asks for it in the same gesture.
    function goOnline() { loadPublic("online", { enabled: true }) }
    /// 公共共享库 refreshes each time it is opened once the player allowed it online (owner,
    /// 2026-11-23). The Collector answers from its cache within 30 s of its last check (2 min after
    /// a successful one), so switching back and forth does not reach GitHub every time.
    function refreshPublic() { if (online && publicAction !== "refresh" && publicAction !== "online") loadPublic("refresh") }
    onOpened: reload()
    onDutyKeyChanged: if (visible && activeScope === "cur") { selectedId = ""; compare.close() }
    onTabChanged: { selectedId = ""; basket = []; mode = "view"; confirmingDelete = false; if (tab === "public") refreshPublic() }
    onCurrentIdChanged: confirmingDelete = false

    /// Every opening starts clean (Design: 每次打开): search, 类型 and 职业 are cleared and the
    /// directory follows the game again; sort, row height and the public library's 区服 and 语言
    /// are preferences and stay.
    function openFor() {
        tab = "local"
        loadView()
        query = ""
        railQuery = ""
        filters = { local: { type: [], job: [], region: [], language: [] },
                    "public": Object.assign({}, filters["public"], { type: [], job: [] }) }
        scope = ""
        railOpen = ({})
        collapsed = ({})
        uncapped = ({})
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
    property int revealTick: 0
    /// Brings a local entry into view (after saving it, or 在本地查看): the directory moves to
    /// its duty - unless 当前副本 is showing and holds it - and its group opens.
    function reveal(id) {
        const row = localRows.find(entry => entry.content.id === id)
        if (!row) return
        tab = "local"
        if (!matches(row, "")) {
            query = ""
            const cleared = Object.assign({}, filters)
            cleared["local"] = { type: [], job: [], region: [], language: [] }
            filters = cleared
        }
        const item = row.content
        const stay = activeScope === "cur" && fitsCurrent(item)
        const key = stay ? "cur" : "d:" + item.content_id
        const group = key + "/" + (stay && item.content_id === 0 ? "gen" : "p:" + Scope.phase(stageOf(item)).key)
        const open = Object.assign({}, collapsed), full = Object.assign({}, uncapped)
        open[group] = false
        full[group] = true
        collapsed = open
        uncapped = full
        if (item.content_id) toggleRailGroup(categoryOf(item.content_id), true)
        scope = key
        basket = []
        selectedId = id
        mode = "view"
        ++revealTick
    }
    function showLocal(id) { reveal(id) }
    /// 关联项: opens the linked entry wherever it is. A local one is revealed as a saved entry is;
    /// on the public tab the search and filters are cleared, 区服 and 语言 too when only they hide
    /// it, and the directory goes to 全部 when its range does not hold it.
    function openLinked(id) {
        if (!remote) { reveal(id); return }
        const row = rows.find(entry => entry.content.id === id)
        if (!row) return
        resetFilters()
        if (!matches(row, "")) showEveryPlace()
        if (!inScope(row, activeScope)) pickScope("all")
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
            // The duty the directory has chosen, else the one the player is in.
            content_id: activeScope[0] === "d" ? Number(activeScope.slice(2)) : inDuty ? duty.content_id : 0,
            region: defaultRegion(),
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
                // A refused save comes back into the form with the reason in it. When the player
                // has opened another form meanwhile, it is offered back instead of being dropped.
                if (action === "save") {
                    const reason = result.status === "transport_error" ? (note || statusText(result.status))
                                                                       : statusText(result.status)
                    if (editing) {
                        refusedSave = { args: args, reason: reason }
                    } else {
                        startEdit(args)
                        editor.refusal = reason
                    }
                }
                return
            }
            // A form opened before the answer came stays open.
            if (nextId !== undefined && !editing) {
                tab = "local"
                select(nextId)
                revealId = nextId
            }
            loadLocal()
        })
    }
    /// A save refused while another form was open, { args, reason }: kept until the player
    /// restores or drops it (audit 2026-11-26 RE-7).
    property var refusedSave: null
    function restoreRefused() {
        const refused = refusedSave
        if (!refused || editing) return
        refusedSave = null
        startEdit(refused.args)
        editor.refusal = refused.reason
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
            if (localRows.includes(row)) markUsed(row.content.id)
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
        if (!picked.length) { note = statusText("invalid_submission"); return }
        mode = "submit"
        submission.start(picked)
    }
    /// 撤回发布: the given entries this library published, without what they link to (a
    /// withdrawn reminder leaves its macros published).
    function withdrawRows(ids) {
        const picked = ids.filter(withdrawable).map(id => ({ content: publicById[id] }))
        if (!picked.length) { note = statusText("invalid_withdrawal"); return }
        mode = "submit"
        submission.start(picked, "withdraw")
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
        const asked = issueNumber
        issueState = "checking"
        // The answer belongs to the number it was asked for: once the field says another, it is dropped.
        call("issue_status", { issue: asked }, result => {
            issueState = result.status === "transport_error" ? "" : result.status
        }, () => asked === issueNumber)
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
        // Only the chosen entry itself: a withdrawn linked entry is left out, not refused (RC-5).
        case "source_unavailable": return qsTr("这条内容已从公共共享库撤回或不再提供，请刷新公共共享库后再试。")
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
        // Failed before anything was sent (audit 2026-11-26 RC-4).
        case "submit_not_sent": return qsTr("投稿没有发出，什么都没有提交，请稍后再试。")
        case "closed": return qsTr("投稿已关闭。")
        case "prepared": return qsTr("投稿正文已复制，请粘贴到浏览器表单并提交。")
        case "withdrawal_prepared": return qsTr("撤回请求已复制，请粘贴到浏览器表单并提交。")
        case "invalid_withdrawal": return qsTr("请选择 1 至 20 条自己发布、还没有撤回的内容，并先刷新公共共享库。")
        case "verified": return qsTr("社区投稿 · 已核验")
        case "outdated": return qsTr("过时")
        case "withdrawn": return qsTr("已撤回")
        case "community": return qsTr("社区投稿 · 未核验")
        default: return qsTr("输入无效或操作失败。") + " (" + status + ")"
        }
    }
    function issueTone(status) {
        // withdrawn: a withdrawal Issue went through (or every entry of a submission was withdrawn).
        return status === "published" || status === "duplicate" || status === "withdrawn" ? "ok"
             : status === "processing_failed" || status === "closed" ? "bad"
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
    readonly property bool publicBlank: remote && listedPublicRows.length === 0

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
                    { value: "public", label: root.listedPublicRows.length > 0
                        ? qsTr("公共共享库") + "  " + root.listedPublicRows.length : qsTr("公共共享库") }
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
            id: toolbar
            Layout.fillWidth: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            Layout.bottomMargin: 12
            visible: !root.editing
            store: root
        }

        Divider {}

        // A save the Collector refused after another form was opened: what was not saved, why,
        // and the way back to it once the open form is saved or left.
        RowLayout {
            objectName: "dutyCommunityRefusedSave"
            Layout.fillWidth: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            Layout.topMargin: 8
            Layout.bottomMargin: 8
            visible: !!root.refusedSave
            spacing: 10
            Text {
                objectName: "dutyCommunityRefusedSaveText"
                renderType: Theme.bodyRenderType
                Layout.fillWidth: true
                textFormat: Text.PlainText
                wrapMode: Text.Wrap
                text: !root.refusedSave ? ""
                    : qsTr("「%1」没有保存：").arg(root.refusedSave.args.content.title) + root.refusedSave.reason
                      + (root.editing ? " " + qsTr("保存或取消当前表单后可以恢复。") : "")
                color: Theme.red
                font.pixelSize: Theme.fs(12)
            }
            AppButton {
                objectName: "dutyCommunityRestoreRefused"
                compact: true
                enabled: !root.editing
                text: qsTr("恢复")
                onClicked: root.restoreRefused()
            }
            AppButton {
                objectName: "dutyCommunityDropRefused"
                compact: true
                variant: "ghost"
                text: qsTr("放弃")
                onClicked: root.refusedSave = null
            }
        }

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

                DutyCommunityRail {
                    Layout.fillWidth: false
                    Layout.preferredWidth: 232
                    Layout.maximumWidth: 232
                    Layout.fillHeight: true
                    store: root
                }
                Rectangle {
                    Layout.fillHeight: true
                    implicitWidth: 1
                    color: Theme.border
                }
                DutyCommunityList {
                    Layout.fillWidth: false
                    Layout.preferredWidth: 384
                    Layout.maximumWidth: 384
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
                        objectName: "dutyCommunityDetail"
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
                    wrapMode: TextEdit.Wrap
                    text: root.sourceChanges
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
    /// 来源更新对比: the content fields and the licence that changed between the version this
    /// entry came from and the newer public one, named in words as the submission names them.
    readonly property string sourceChanges: !source || !current || !current.upstream ? ""
        : submission.comparisonText(Match.differences(current.upstream, source, source.license))
}
