import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

Item {
    id: page

    signal openManualRequested()
    signal openImportRequested()
    signal openCorrectRequested()
    signal openDeleteRequested()
    signal openRestoreRequested()
    signal openUndoRequested()
    signal openReviewRequested()
    // Opens ReflectionDialog for the selected run.
    signal reflectRequested(var run)
    signal shareRequested(var run)

    property string detailTab: "info"
    property bool suspendFilter: false
    readonly property bool narrowFilters: page.width < 1020
    readonly property int checkedCount: Runtime.app.checkedHistoryRunCount
    function isChecked(runId) { return Runtime.app.checkedHistoryRunIds.indexOf(runId) >= 0 }
    function importIncomplete(run) {
        if (run.source !== "IMPORT") return false
        if (run.import_metadata) return !!run.import_metadata.incomplete
        const result = run.result || "UNKNOWN"
        return result === "UNKNOWN" || (result !== "CANCELLED_BEFORE_ENTRY"
            && (!run.entered_at_utc || (result === "COMPLETED" && !run.ended_at_utc)))
    }
    function requestBatch(action) {
        batchDialog.action = action
        batchDialog.submitted = false
        batchReason.text = action === "purge" ? qsTranslate("SystemReason", "永久删除已删除的记录")
                         : action === "restore" ? qsTranslate("SystemReason", "恢复已删除的记录")
                         : qsTranslate("SystemReason", "批量删除记录")
        batchDialog.open()
    }
    function dateLabel(run) {
        const actual = run.matched_at_utc || run.entered_at_utc || run.ended_at_utc
        if (actual) return Runtime.fmt.localDate(actual)
        const source = run.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站\n%1").arg(Runtime.fmt.localDate(source.source_recorded_at_utc))
        if (source.source_recorded_at) {
            const date = /^(\d{4})[-\/.](\d{1,2})[-\/.](\d{1,2})$/.exec(source.source_recorded_at)
            if (date) {
                const normalized = date[1] + "-" + String(date[2]).padStart(2, "0") + "-" + String(date[3]).padStart(2, "0")
                if (Runtime.runForm.isValidDate(normalized)) return qsTr("原站\n%1").arg(normalized)
            }
        }
        return Runtime.fmt.dash()
    }
    // A drill-down from 副本统计 arrives as a content_id; the 副本 chip below
    // mirrors it so the two never disagree about what is filtered.
    property var contentFilter: null
    // Statistics can link to an old/imported job outside the current mentor
    // choices. Keep that drill-down visible without adding it to the picker.
    property var retainedJobFilter: null
    readonly property bool hasRetainedJobFilter: retainedJobFilter !== null
                                                && retainedJobFilter !== undefined

    // The chip label comes from App.dutyOptions, the list 副本统计 aggregates by.
    function dutyLabel(contentId) {
        if (contentId === null || contentId === undefined)
            return ""
        const rows = Runtime.app.dutyOptions
        for (let index = 0; index < rows.length; ++index) {
            if (rows[index].content_id === contentId)
                return Theme.dutyChoiceLabel(rows[index]) || qsTr("未知副本")
        }
        return qsTr("指定副本")
    }

    // ------------------------------------------------------------- 列 --
    // 副本 is the one elastic column: without a width the user dragged out it
    // takes whatever the others leave of the table width; the matched
    // time-of-day appears in the detail panel only.
    readonly property int dutyColumnWidth: 228
    // Classic sets times in IBM Plex Mono, whose figures are wider than the body
    // face's: 8 of them at 12 px need 70 px to keep a gap before the next column.
    readonly property int timeColumnWidth: Theme.eorzea ? 64 : 70
    // The same wider mono figures: classic needs 96 px, at 86 the date touches 进本.
    readonly property int dateColumnWidth: Theme.eorzea ? 86 : 96
    // The columns by the stable keys App.historyColumns stores (docs/ui-design.md
    // §4.2): the title in the interface language, the sort key the Collector
    // knows (empty: not sortable), the default width and the narrowest width a
    // drag may leave. Column titles sit centred over their column; the cells of
    // the narrow columns centre too, so a title and its figures line up. 副本 is
    // the one exception: an icon, a name and a second line read from the left.
    readonly property var columnCatalog: ({
        date:     { label: qsTr("日期"), sortKey: "history_date",   width: page.dateColumnWidth, minimum: 80 },
        entered:  { label: qsTr("进本"), sortKey: "entered_at_utc", width: page.timeColumnWidth, minimum: 62 },
        ended:    { label: qsTr("结束"), sortKey: "ended_at_utc",   width: page.timeColumnWidth, minimum: 62 },
        duty:     { label: qsTr("副本"), sortKey: "duty_name",      width: page.dutyColumnWidth, minimum: 120, fill: true },
        category: { label: qsTr("类型"), sortKey: "",               width: 84,  minimum: 56 },
        job:      { label: qsTr("职业"), sortKey: "",               width: 112, minimum: 64 },
        result:   { label: qsTr("结果"), sortKey: "",               width: 92,  minimum: 64 },
        duration: { label: qsTr("耗时"), sortKey: "duration_ms",    width: 66,  minimum: 50 },
        source:   { label: qsTr("来源"), sortKey: "",               width: 60,  minimum: 48 },
        marks:    { label: qsTr("标记"), sortKey: "",               width: 92,  minimum: 60 }
    })
    /// App.historyColumns; a scene whose App is a stand-in without it (some shell
    /// tests) shows the default columns and cannot customise them.
    readonly property var columnsController: Runtime.app.historyColumns ? Runtime.app.historyColumns : defaultColumns
    QtObject {
        id: defaultColumns
        readonly property var order: ["date", "entered", "ended", "duty", "category", "job",
                                      "result", "duration", "source", "marks"]
        readonly property var visible: order
        readonly property var widths: ({})
        readonly property bool customised: false
        readonly property int minimumWidth: 40
        readonly property int maximumWidth: 800
        function isVisible(key) { return order.indexOf(key) >= 0 }
        function canHide(key) { return false }
        function width(key) { return 0 }
        function setVisible(key, visible) {}
        function setWidth(key, width) {}
        function moveTo(key, index) {}
        function reset() {}
    }
    /// Every column in the user's order, hidden ones included: the 自定义列 dialog's list.
    readonly property var allColumns: page.columnsController.order.map(function(key) {
        const column = page.columnCatalog[key]
        return { key: key, label: column ? column.label : key }
    })
    /// Width the columns share: the header less its margins and the checkbox.
    readonly property real columnsWidth: Math.max(0, tableHeader.width - 8 - 32)
    /// Where every shown column sits, for the header and the rows alike:
    /// { keys, available, <key>: { x, width, index, maximum } }. A width being
    /// dragged on the header is previewed here, so the rows follow the pointer.
    readonly property var columnLayout: page.computeLayout()

    /// What each shown column asks for: the width being dragged, the stored
    /// width (never below the column's narrowest) or the default; `fill` names
    /// the elastic column, if one is still elastic.
    function requestedWidths(keys, stored) {
        const request = { widths: {}, minimums: {}, fill: "" }
        for (const key of keys) {
            const column = page.columnCatalog[key] || { width: 80, minimum: 40 }
            const resizing = tableHeader.resizeKey === key
            const custom = stored[key] > 0
            request.minimums[key] = column.minimum
            request.widths[key] = resizing ? tableHeader.resizeWidth
                                : (custom ? Math.max(column.minimum, stored[key]) : column.width)
            if (column.fill === true && !custom && !resizing)
                request.fill = key
        }
        return request
    }
    /// Too wide for the window: every column but the one being dragged gives
    /// up its share of what it can, down to its narrowest; beyond that the row
    /// overflows. The dragged one keeps following the pointer.
    function squeeze(keys, widths, minimums, available) {
        let total = 0
        for (const summedKey of keys)
            total += widths[summedKey]
        if (total <= available)
            return
        let give = 0
        for (const shrinkableKey of keys)
            if (shrinkableKey !== tableHeader.resizeKey)
                give += Math.max(0, widths[shrinkableKey] - minimums[shrinkableKey])
        const excess = Math.min(total - available, give)
        if (give <= 0 || excess <= 0)
            return
        for (const shrinkingKey of keys) {
            if (shrinkingKey === tableHeader.resizeKey)
                continue
            const room = Math.max(0, widths[shrinkingKey] - minimums[shrinkingKey])
            widths[shrinkingKey] -= Math.round(excess * room / give)
        }
    }
    function computeLayout() {
        const keys = page.columnsController.visible
        const available = page.columnsWidth
        const request = page.requestedWidths(keys, page.columnsController.widths)
        const widths = request.widths
        const minimums = request.minimums
        const fillKey = request.fill
        let fixed = 0
        for (const fixedKey of keys)
            if (fixedKey !== fillKey)
                fixed += widths[fixedKey]
        // The elastic column takes what the others leave, down to its narrowest
        // (whole pixels, so the columns after it sit on whole pixels too).
        if (fillKey.length > 0)
            widths[fillKey] = Math.max(minimums[fillKey], Math.floor(available - fixed))
        // What another column may still take: beyond the widths asked for and
        // the elastic column's narrowest. Negative when the window is too narrow.
        const room = available - fixed - (fillKey.length > 0 ? minimums[fillKey] : 0)
        const layout = { keys: keys, available: available }
        for (const boundedKey of keys) {
            const widest = boundedKey === fillKey ? available - fixed : widths[boundedKey] + room
            layout[boundedKey] = { maximum: Math.min(page.columnsController.maximumWidth, Math.max(minimums[boundedKey], widest)) }
        }
        page.squeeze(keys, widths, minimums, available)
        let x = 0
        for (let index = 0; index < keys.length; ++index) {
            const positionedKey = keys[index]
            layout[positionedKey].x = x
            layout[positionedKey].width = widths[positionedKey]
            layout[positionedKey].index = index
            x += widths[positionedKey]
        }
        return layout
    }
    function sortByColumn(key) {
        const column = page.columnCatalog[key]
        if (column && column.sortKey.length > 0)
            Runtime.app.runs.sortBy(column.sortKey)
    }

    // ---------------------------------------------------- 每页行数 --
    // Rows per page follow the window: as many as fit between the table
    // header and the pagination row, never fewer than a handful, so a tall
    // window is not left three-quarters empty and a short one still pages.
    // Pixels come from the items themselves, not from constants that would
    // drift; the debounce keeps a window drag from issuing a QueryRuns per
    // pixel. The model keeps the top row in view when the size changes.
    readonly property int rowHeight: 44
    readonly property int minimumRowsPerPage: 5
    /// Pixels at the bottom of the page that something else is drawn over
    /// (the shell's 无法自动记录 notice), so the pagination row is not placed
    /// underneath it. The shell binds this; alone, the page reserves nothing.
    property real reservedBottom: 0
    readonly property int rowsThatFit: {
        const fixed = pageHeader.height + filterRow.height + (activeFilterRow.visible ? activeFilterRow.height : 0) + (batchBar.visible ? batchBar.height : 0) + (batchFeedback.visible ? batchFeedback.height : 0) + filterSeparator.height
                    + tableHeader.height + pagerRow.height + contentColumn.spacing * (5 + (batchBar.visible ? 1 : 0) + (activeFilterRow.visible ? 1 : 0) + (batchFeedback.visible ? 1 : 0))
                    + Math.max(0, page.reservedBottom)
        return Math.max(page.minimumRowsPerPage, Math.floor((page.height - fixed) / page.rowHeight))
    }
    onRowsThatFitChanged: pageSizeDebounce.restart()

    Timer {
        id: pageSizeDebounce
        interval: 150
        onTriggered: Runtime.app.runs.pageSize = page.rowsThatFit
    }

    function toUtcRange(dateText, endOfDay) {
        if (!dateText || dateText.length === 0)
            return null
        if (!Runtime.runForm.isValidDate(dateText))
            return null
        const parts = dateText.split("-").map(Number)
        return new Date(parts[0], parts[1] - 1, parts[2],
                        endOfDay ? 23 : 0, endOfDay ? 59 : 0,
                        endOfDay ? 59 : 0, endOfDay ? 999 : 0).toISOString()
    }

    // Filters apply live; the debounce keeps every keystroke from firing its own
    // QueryRuns.
    Timer {
        id: filterDebounce
        interval: 300
        onTriggered: page.applyFilter()
    }

    // The dashboard's 去复核 drill-down; like the duty drill-down it is shown as a
    // removable chip only while it is on.
    property bool pendingReviewFilter: false

    property bool calendarReady: false
    function scheduleFilter() {
        if (page.suspendFilter) return
        if (calendarReady) {
            Runtime.app.runs.setCalendarFilter(page.buildFilter(true))
            Runtime.app.runs.ensureCalendarDay(fromField.text.trim())
            Runtime.app.runs.ensureCalendarDay(toField.text.trim())
        }
        filterDebounce.restart()
    }

    // True while applyFilter hands its own filter to App. The change it causes
    // comes straight back through onHistoryFilterChanged, and mirroring it would
    // rewrite the fields under the user's cursor (review OK-5).
    property bool applying: false
    readonly property var fromField: dateRange.startField
    readonly property var toField: dateRange.endField

    // 开始 later than 结束: the Collector refuses the query, so it is not sent.
    readonly property bool rangeInverted: {
        const from = toUtcRange(fromField.text.trim(), false)
        const to = toUtcRange(toField.text.trim(), true)
        return from !== null && to !== null && from > to
    }

    function buildFilter(withoutDates) {
        const filter = { date_field: "history_date" }
        // Keep the original words for notes; translated names add the runs
        // they name within the same query, so totals, paging and the calendar agree.
        const text = searchField.text.trim()
        if (text.length > 0) {
            filter.text = text
            const names = typeof Runtime.names !== "undefined" && Runtime.names
                          ? Runtime.names.recordedSearchNames(text) : []
            if (names.length > 0)
                filter.name_exact = names
        }
        const from = toUtcRange(fromField.text.trim(), false)
        const to = toUtcRange(toField.text.trim(), true)
        if (!withoutDates && from)
            filter.from_utc = from
        if (!withoutDates && to)
            filter.to_utc = to
        if (!withoutDates && from) filter.history_from_day = fromField.text.trim()
        if (!withoutDates && to) filter.history_to_day = toField.text.trim()
        if (page.contentFilter !== null && page.contentFilter !== undefined)
            filter.content_id = [page.contentFilter]
        if (categoryBox.currentIndex > 0)
            filter.duty_category = [categoryBox.model[categoryBox.currentIndex].value]
        if (jobBox.currentIndex > 0)
            filter.job_id = [jobBox.model[jobBox.currentIndex].job_id]
        else if (page.hasRetainedJobFilter)
            filter.job_id = [page.retainedJobFilter]
        if (resultBox.currentIndex > 0)
            filter.result = [resultBox.model[resultBox.currentIndex].value]
        if (sourceBox.currentIndex > 0)
            filter.source = [sourceBox.model[sourceBox.currentIndex].value]
        if (correctedOnly.checked)
            filter.corrected_only = true
        if (page.pendingReviewFilter)
            filter.pending_review = true
        if (withReflection.checked)
            filter.with_reflection = true
        if (deletedOnly.checked)
            filter.deleted_only = true
        if (joinedInProgressOnly.checked)
            filter.joined_in_progress = true
        return filter
    }

    function applyFilter() {
        // A half-typed or invalid date is neither queried nor silently left out of
        // the query (docs/ui-design.md §4.2): its field turns red and the list keeps
        // its last answer until the date is whole. An inverted range waits the same way.
        if (dateRange.selectionPending || !fromField.valid || !toField.valid || page.rangeInverted)
            return
        const filter = page.buildFilter(false)
        page.applying = true
        Runtime.app.setHistoryFilter(filter)
        page.applying = false
    }

    function resetFilter() {
        page.suspendFilter = true
        dateRange.selectionPending = false
        searchField.text = ""
        fromField.text = ""
        toField.text = ""
        page.contentFilter = null
        page.retainedJobFilter = null
        categoryBox.currentIndex = 0
        jobBox.currentIndex = 0
        resultBox.currentIndex = 0
        sourceBox.currentIndex = 0
        correctedOnly.checked = false
        page.pendingReviewFilter = false
        withReflection.checked = false
        deletedOnly.checked = false
        joinedInProgressOnly.checked = false
        page.suspendFilter = false
        filterDebounce.stop()
        Runtime.app.resetHistoryFilter()
    }

    function firstFilterValue(value) {
        if (value === undefined || value === null)
            return null
        // QVariantList reaches QML as a sequence wrapper, which need not pass
        // Array.isArray(). Unwrap it before comparing ids or rebuilding arrays.
        if (Array.isArray(value) || (typeof value === "object" && typeof value.length === "number"))
            return value.length > 0 ? value[0] : null
        return value
    }

    function filterIndex(model, key, value) {
        const selected = firstFilterValue(value)
        if (selected === null)
            return 0
        for (let index = 1; index < model.length; ++index) {
            if ((key ? model[index][key] : model[index]) === selected)
                return index
        }
        return 0
    }

    function syncFilter() {
        // The controls already say what this page just sent; only a filter from
        // elsewhere (a drill-down, 清除) is mirrored back into them.
        if (page.applying)
            return
        // A drill-down replaces the complete filter. Cancel a pending edit
        // debounce and mirror every control without issuing another request.
        filterDebounce.stop()
        const filter = Runtime.app.historyFilter
        page.suspendFilter = true
        dateRange.selectionPending = false
        searchField.text = filter.text || ""
        fromField.text = filter.from_utc ? Runtime.fmt.localDate(filter.from_utc) : ""
        toField.text = filter.to_utc ? Runtime.fmt.localDate(filter.to_utc) : ""
        categoryBox.currentIndex = filterIndex(categoryBox.model, "value", filter.duty_category)
        jobBox.currentIndex = filterIndex(jobBox.model, "job_id", filter.job_id)
        page.retainedJobFilter = jobBox.currentIndex === 0 ? firstFilterValue(filter.job_id) : null
        resultBox.currentIndex = filterIndex(resultBox.model, "value", filter.result)
        sourceBox.currentIndex = filterIndex(sourceBox.model, "value", filter.source)
        correctedOnly.checked = !!filter.corrected_only
        page.pendingReviewFilter = !!filter.pending_review
        withReflection.checked = !!filter.with_reflection
        deletedOnly.checked = !!filter.deleted_only
        joinedInProgressOnly.checked = filter.joined_in_progress === true
        page.contentFilter = firstFilterValue(filter.content_id)
        page.suspendFilter = false
        if (page.calendarReady) {
            Runtime.app.runs.setCalendarFilter(page.buildFilter(true))
            Runtime.app.runs.ensureCalendarDay(fromField.text.trim())
            Runtime.app.runs.ensureCalendarDay(toField.text.trim())
        }
    }

    // The rows replay their entrance when the page becomes current
    // (PageHost.entered).
    signal rowsEntering()
    property Item pageHost: null

    function findPageHost() {
        for (let item = page.parent; item; item = item.parent) {
            if (item.isPageHost === true)
                return item
        }
        return null
    }

    Connections {
        target: page.pageHost
        enabled: page.pageHost !== null
        function onEntered() { page.rowsEntering() }
    }

    Component.onCompleted: {
        page.pageHost = page.findPageHost()
        page.syncFilter()
        page.calendarReady = true
        Runtime.app.runs.setCalendarFilter(page.buildFilter(true))
        if (Runtime.app.historyFilterConfirmationPending) filterConfirmation.open()
    }

    Connections {
        target: Runtime.app

        function onHistoryFilterChanged() {
            page.syncFilter()
        }
        function onHistoryFilterConfirmationChanged() {
            if (Runtime.app.historyFilterConfirmationPending && !filterConfirmation.visible)
                filterConfirmation.open()
        }
        function onHistoryBatchChanged() {
            if (batchDialog.submitted && !Runtime.app.historyBatchRunning && page.checkedCount === 0)
                batchDialog.close()
        }
    }

    Connections {
        target: Runtime.app.runs
        function onCalendarChanged() {
            if (!page.calendarReady) return
            Runtime.app.runs.ensureCalendarDay(fromField.text.trim())
            Runtime.app.runs.ensureCalendarDay(toField.text.trim())
            filterDebounce.restart()
        }
    }

    Flickable {
        id: scroll
        anchors.fill: parent
        contentWidth: width
        contentHeight: contentColumn.implicitHeight
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        ScrollBar.vertical: ScrollBar {
            policy: scroll.contentHeight > scroll.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff
        }

        ColumnLayout {
            id: contentColumn
            width: scroll.width - Theme.scrollGutter
            spacing: 16

            PageHeader {
                id: pageHeader
                objectName: "historyHeader"
                title: qsTr("历史记录")
                // A failed query is not "0 条匹配".
                subtitle: Runtime.app.runs.loadError.length > 0
                          ? qsTr("读取失败")
                          : qsTr("%1 条匹配 · 第 %2 / %3 页").arg(Runtime.app.runs.total)
                                                             .arg(Runtime.app.runs.page)
                                                             .arg(Runtime.app.runs.pageCount)

                AppButton { text: page.checkedCount > 0 ? qsTr("导出选中 CSV (%1)").arg(page.checkedCount) : qsTr("导出 CSV"); iconName: "file-down"; enabled: !Runtime.app.historyBatchRunning; onClicked: Runtime.app.exportCsv() }
                AppButton { text: page.checkedCount > 0 ? qsTr("导出选中 JSON (%1)").arg(page.checkedCount) : qsTr("导出 JSON"); iconName: "file-json"; enabled: !Runtime.app.historyBatchRunning; onClicked: Runtime.app.exportJson() }
                AppButton { text: qsTr("导入记录"); iconName: "file-input"; onClicked: page.openImportRequested() }
                AppButton {
                    text: qsTr("新增记录")
                    iconName: "plus"
                    variant: "primary"
                    onClicked: page.openManualRequested()
                }
            }

            // -------------------------------------------------- 筛选栏 --
            // One compact row, as in the prototype: search, date range, four
            // narrow combo boxes, mark flags, page selection and 清除. A Flow rather than
            // a RowLayout so that on a narrow window the trailing controls wrap
            // to a second line instead of being clipped off the right edge.
            Flow {
                id: filterRow
                Layout.fillWidth: true
                spacing: 6
                enabled: !Runtime.app.historyBatchRunning

                readonly property int controlHeight: Theme.eorzea ? 32 : 30
                // Width of everything after the search field, gaps included.
                readonly property real restWidth: {
                    let total = 0
                    for (let index = 1; index < children.length; ++index) {
                        const child = children[index]
                        if (!child.visible)
                            continue
                        total += child.width + spacing
                    }
                    return total
                }
                readonly property bool singleLine: restWidth + 120 <= width
                // Narrow fallback: search + the date range share the first line,
                // the combo boxes, chips and 清除 wrap to the second.
                readonly property real dateWidth: dateRange.width + spacing

                StyledTextField {
                    id: searchField
                    objectName: "historySearchField"
                    // -1 keeps a sub-pixel rounding from pushing the last
                    // control of a line onto the next one.
                    width: filterRow.singleLine ? filterRow.width - filterRow.restWidth - 1
                                                : filterRow.width - filterRow.dateWidth - 1
                    placeholderText: qsTr("搜索 副本 / 职业 / 备注")
                    // RunFilter.text maxLength: a longer search is refused outright.
                    maximumLength: 200
                    onTextChanged: page.scheduleFilter()
                }

                DateField {
                    id: dateRange
                    objectName: "historyDateRange"
                    width: 238
                    rangeMode: true
                    restrictToRecords: true
                    availableDays: Runtime.app.runs.calendarAvailableDays
                    knownMonths: Runtime.app.runs.calendarKnownMonths
                    availabilityLoading: Runtime.app.runs.calendarLoading
                    availabilityError: Theme.live(Runtime.app.runs.calendarError || Runtime.app.runs.calendarDayError(startText.trim()) || Runtime.app.runs.calendarDayError(endText.trim()))
                    onCalendarRequested: function(month) {
                        // Retry failed typed endpoints as well as the visible month.
                        for (const date of [startText.trim(), endText.trim()]) {
                            if (Runtime.app.runs.calendarDayError(date).length > 0)
                                Runtime.app.runs.requestCalendarDays(date.slice(0, 7))
                        }
                        Runtime.app.runs.requestCalendarDays(month)
                    }
                    onEndpointRequested: date => Runtime.app.runs.ensureCalendarDay(date)
                    Accessible.name: qsTr("历史日期范围")
                    onStartTextChanged: page.scheduleFilter()
                    onEndTextChanged: page.scheduleFilter()
                    onSelectionPendingChanged: page.scheduleFilter()
                }

                // A removable chip rather than a fifth combo box, which would not
                // fit the single row.
                Chip {
                    id: dutyChip
                    visible: false
                    height: filterRow.controlHeight
                    text: page.dutyLabel(page.contentFilter)
                    checked: true
                    removable: true
                    Accessible.name: qsTr("副本筛选")
                    onRemoved: {
                        page.contentFilter = null
                        page.scheduleFilter()
                    }
                }

                // The closed filters show a short label (类型 / 职业 / 结果 / 来源). In
                // Chinese it fits the fixed widths below; a translation may be
                // longer, so each box grows to its own label: the text's padding
                // (10 + 24) plus the box's own, and a pixel against rounding.
                TextMetrics { id: typeLabelMetrics; font.pixelSize: Theme.fs(13); text: qsTr("类型") }
                TextMetrics { id: jobLabelMetrics; font.pixelSize: Theme.fs(13); text: qsTr("职业") }
                TextMetrics { id: resultLabelMetrics; font.pixelSize: Theme.fs(13); text: qsTr("结果") }
                TextMetrics { id: sourceLabelMetrics; font.pixelSize: Theme.fs(13); text: qsTr("来源") }

                StyledComboBox {
                    id: categoryBox
                    width: Math.max(78, Math.ceil(typeLabelMetrics.advanceWidth) + 36
                                     + leftPadding + rightPadding)
                    // value: the Chinese category RunFilter.duty_category carries; label: as shown.
                    model: [{ value: null, label: qsTr("全部类型") }].concat(Theme.labelledCategories(Runtime.app.categoryOptions))
                    textRole: "label"
                    displayText: currentIndex === 0 ? qsTr("类型") : currentText
                    Accessible.name: qsTr("类型筛选")
                    onActivated: page.scheduleFilter()
                }

                StyledComboBox {
                    id: jobBox
                    objectName: "historyJobFilter"
                    width: Math.max(78, Math.ceil(jobLabelMetrics.advanceWidth) + 36
                                     + leftPadding + rightPadding)
                    // The first row clears this filter; the closed field keeps its short label.
                    // The label is the job in the interface language; the filter sends job_id.
                    model: [{ job_id: null, job_name: qsTr("全部职业"), label: qsTr("全部职业") }]
                           .concat(Theme.labelledJobs(Runtime.app.battleJobOptions))
                    textRole: "label"
                    displayText: currentIndex === 0 ? qsTr("职业") : currentText
                    Accessible.name: qsTr("职业筛选")
                    onActivated: {
                        page.retainedJobFilter = null
                        page.scheduleFilter()
                    }
                }

                StyledComboBox {
                    id: resultBox
                    width: Math.max(80, Math.ceil(resultLabelMetrics.advanceWidth) + 36
                                     + leftPadding + rightPadding)
                    model: [
                        { value: null, label: qsTr("全部结果") },
                        { value: "COMPLETED", label: qsTr("通关") },
                        { value: "LEFT_OR_ABANDONED", label: qsTr("退出/放弃") },
                        { value: "CANCELLED_BEFORE_ENTRY", label: qsTr("进本前取消") },
                        { value: "DISCONNECTED", label: qsTr("断线") },
                        { value: "INTERRUPTED", label: qsTr("中断") },
                        { value: "UNKNOWN", label: qsTr("未知") }
                    ]
                    textRole: "label"
                    displayText: currentIndex === 0 ? qsTr("结果") : currentText
                    Accessible.name: qsTr("结果筛选")
                    onActivated: page.scheduleFilter()
                }

                StyledComboBox {
                    id: sourceBox
                    visible: !page.narrowFilters
                    width: Math.max(78, Math.ceil(sourceLabelMetrics.advanceWidth) + 36
                                     + leftPadding + rightPadding)
                    model: [
                        { value: null, label: qsTr("全部来源") },
                        { value: "AUTO_NETWORK", label: qsTr("自动识别") },
                        { value: "MANUAL", label: qsTr("手动") },
                        { value: "IMPORT", label: qsTr("导入") }
                    ]
                    textRole: "label"
                    displayText: currentIndex === 0 ? qsTr("来源") : currentText
                    Accessible.name: qsTr("来源筛选")
                    onActivated: page.scheduleFilter()
                }

                // Four independent flags retain their existing combined filter semantics.
                StyledComboBox {
                    id: markBox
                    objectName: "historyMarkFilter"
                    width: 118
                    model: [qsTr("标记类型")]
                    readonly property int selectedCount: (page.pendingReviewFilter ? 1 : 0)
                                                       + (correctedOnly.checked ? 1 : 0)
                                                       + (withReflection.checked ? 1 : 0)
                                                       + (joinedInProgressOnly.checked ? 1 : 0)
                                                       + (deletedOnly.checked ? 1 : 0)
                    displayText: selectedCount > 0 ? qsTr("标记类型 · %1").arg(selectedCount) : qsTr("标记类型")
                    Accessible.name: qsTr("标记类型筛选，可多选")
                    popup: Popup {
                        id: markPopup
                        y: markBox.height + 4
                        width: 180
                        padding: 8
                        implicitHeight: contentItem.implicitHeight + topPadding + bottomPadding
                        background: Rectangle {
                            radius: Theme.radiusS
                            color: Theme.surface
                            border.width: 1
                            border.color: Theme.eorzea ? Theme.gold3 : Theme.border
                        }
                        contentItem: ColumnLayout {
                            spacing: 2
                            SelectionCheckBox {
                                id: pendingChip
                                objectName: "pendingReviewChip"
                                Layout.fillWidth: true
                                text: qsTr("待复核")
                                checked: page.pendingReviewFilter
                                onClicked: { page.pendingReviewFilter = checked; markPopup.close(); page.scheduleFilter() }
                            }
                            SelectionCheckBox {
                                id: correctedOnly
                                objectName: "historyCorrectedFilter"
                                Layout.fillWidth: true
                                text: qsTr("已修正")
                                onClicked: { markPopup.close(); page.scheduleFilter() }
                            }
                            SelectionCheckBox {
                                id: withReflection
                                objectName: "historyReflectionFilter"
                                Layout.fillWidth: true
                                text: qsTr("有笔记")
                                onClicked: { markPopup.close(); page.scheduleFilter() }
                            }
                            SelectionCheckBox {
                                id: joinedInProgressOnly
                                objectName: "historyJoinedInProgressFilter"
                                Layout.fillWidth: true
                                text: qsTr("中途加入")
                                onClicked: { markPopup.close(); page.scheduleFilter() }
                            }
                            SelectionCheckBox {
                                id: deletedOnly
                                objectName: "historyDeletedFilter"
                                Layout.fillWidth: true
                                text: qsTr("已删除")
                                onClicked: { markPopup.close(); page.scheduleFilter() }
                            }
                        }
                    }
                }

                // Fixed 40 px: the Flow sums its children into restWidth and the
                // search field takes whatever is left.
                AppButton {
                    id: moreFiltersButton
                    objectName: "historyMoreFiltersButton"
                    visible: page.narrowFilters
                    text: qsTr("更多筛选")
                    height: filterRow.controlHeight
                    onClicked: moreFilters.open()
                }
                AppButton {
                    variant: "ghost"
                    // Its label's own width when a translation is longer than 清除.
                    width: Math.max(40, Math.ceil(contentItem.implicitWidth) + 4)
                    height: filterRow.controlHeight
                    text: qsTr("清除")
                    onClicked: page.resetFilter()
                }
            }

            Flow {
                id: activeFilterRow
                Layout.fillWidth: true
                visible: (page.contentFilter !== null && page.contentFilter !== undefined) || page.hasRetainedJobFilter
                         || (page.narrowFilters && (sourceBox.currentIndex > 0))
                spacing: 6
                Chip { visible: page.contentFilter !== null && page.contentFilter !== undefined; text: page.dutyLabel(page.contentFilter); checked: true; removable: true; onRemoved: { page.contentFilter = null; page.scheduleFilter() } }
                Chip { objectName: "retainedJobFilterChip"; visible: page.hasRetainedJobFilter; text: qsTr("职业：%1").arg(Theme.catalogJobName(page.retainedJobFilter)); checked: true; removable: true; Accessible.name: text; onRemoved: { page.retainedJobFilter = null; page.scheduleFilter() } }
                Chip { visible: page.narrowFilters && sourceBox.currentIndex > 0; text: sourceBox.currentText; checked: true; removable: true; onRemoved: { sourceBox.currentIndex = 0; page.scheduleFilter() } }
            }

            Flow {
                id: batchBar
                visible: page.checkedCount > 0
                objectName: "historyBatchBar"
                Layout.fillWidth: true
                spacing: 8
                enabled: !Runtime.app.historyBatchRunning
                AppButton { text: qsTr("取消勾选"); visible: page.checkedCount > 0; variant: "ghost"; onClicked: Runtime.app.clearCheckedHistoryRuns() }
                AppButton { objectName: "batchSoftDeleteButton"; text: qsTr("删除记录"); visible: page.checkedCount > 0; enabled: Runtime.app.checkedHistoryDeletedCount === 0; onClicked: page.requestBatch("soft_delete") }
                AppButton { objectName: "batchRestoreButton"; text: qsTr("恢复选中"); visible: page.checkedCount > 0 && Runtime.app.checkedHistoryDeletedCount > 0; enabled: Runtime.app.checkedHistoryDeletedCount === page.checkedCount; onClicked: page.requestBatch("restore") }
                AppButton { objectName: "batchPurgeButton"; text: qsTr("永久删除"); visible: page.checkedCount > 0 && Runtime.app.checkedHistoryDeletedCount > 0; enabled: Runtime.app.checkedHistoryDeletedCount === page.checkedCount; onClicked: page.requestBatch("purge") }
            }

            Text {
                id: batchFeedback
                Layout.fillWidth: true
                visible: text.length > 0
                text: Runtime.app.historyBatchRunning ? qsTr("正在处理选中的记录…") : Runtime.app.historyBatchFeedback
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.Wrap
            }

            Text {
                objectName: "historyRangeHint"
                Layout.fillWidth: true
                visible: page.rangeInverted
                text: qsTr("开始日期晚于结束日期，筛选未更新。")
                textFormat: Text.PlainText
                color: Theme.orangeText
                font.pixelSize: Theme.fs(12)
            }

            Rectangle {
                id: filterSeparator
                Layout.fillWidth: true
                Layout.preferredHeight: 1
                color: Theme.border
            }

            // ---------------------------------------------------- 表头 --
            // Titles, sorting, drag-to-reorder and drag-to-resize live in the
            // component; the page owns the layout both it and the rows read.
            HistoryTableHeader {
                id: tableHeader
                objectName: "historyTableHeader"
                Layout.fillWidth: true
                Layout.preferredHeight: 32
                // Its drop and width guides reach down over the rows.
                z: 1
                layout: page.columnLayout
                catalog: page.columnCatalog
                controller: page.columnsController
                sortField: Runtime.app.runs.sortField
                sortAscending: Runtime.app.runs.sortAscending
                bodyHeight: rowsColumn.height
                onSortRequested: function(key) { page.sortByColumn(key) }
                onCustomizeRequested: columnsDialog.open()
            }

            // The table sizes to its content (a full page of rows always fits),
            // so the page - not the table - is what scrolls.
            ColumnLayout {
                id: rowsColumn
                Layout.fillWidth: true
                spacing: 0

                Repeater {
                    id: rowRepeater
                    model: Runtime.app.runs

                    delegate: Rectangle {
                        id: runRow

                        required property var run
                        required property int index

                        readonly property bool selected: Runtime.app.selectedRun.run_id === run.run_id

                        Layout.fillWidth: true
                        Layout.preferredHeight: page.rowHeight
                        // Classic: hover is --inset-bg, the selection accent-100;
                        // `.row-c:active` shows accent-100 while pressed.
                        color: selected || rowTap.pressed
                               ? Theme.accentMuted
                               : (rowHover.hovered
                                  ? (Theme.eorzea ? Theme.fill : Theme.insetBackground)
                                  : "transparent")
                        opacity: (run.soft_deleted ? 0.45 : 1) * rowEntrance.opacity
                        transform: Translate { y: rowEntrance.dy }

                        // The model resets on every reload, so a fresh delegate is
                        // a reloaded row; a changed field of a surviving row never
                        // replays the entrance.
                        readonly property Entrance rowEntrance: Entrance { distanceY: 4 }
                        function playEntrance() {
                            runRow.rowEntrance.play(Math.min(runRow.index, 11) * Theme.motionRowStagger)
                        }
                        Component.onCompleted: runRow.playEntrance()
                        Connections {
                            target: page
                            function onRowsEntering() { runRow.playEntrance() }
                        }

                        HoverHandler { id: rowHover }
                        // A row click opens the panel on 详情; a dashboard jump
                        // sets the tab before selecting.
                        TapHandler {
                            id: rowTap
                            onTapped: function(eventPoint) {
                                if (eventPoint.position.x < 36) return
                                page.detailTab = "info"
                                Runtime.app.selectRun(run)
                            }
                        }

                        // box-shadow: inset 2px 0 0 var(--color-gold) (accent in classic)
                        Rectangle {
                            anchors.left: parent.left
                            anchors.top: parent.top
                            anchors.bottom: parent.bottom
                            width: 2
                            visible: parent.selected
                            color: Theme.eorzea ? Theme.gold : Theme.accent
                        }

                        // The cells sit where page.columnLayout puts their
                        // columns, exactly under the header's titles.
                        Item {
                            anchors.fill: parent
                            anchors.leftMargin: 4
                            anchors.rightMargin: 4

                            SelectionCheckBox {
                                objectName: "historyRunCheck_" + run.run_id
                                x: 0
                                width: 32
                                anchors.verticalCenter: parent.verticalCenter
                                checked: page.isChecked(run.run_id)
                                enabled: !Runtime.app.historyBatchRunning
                                Accessible.name: qsTr("勾选记录 %1 %2").arg(Theme.dutyName(run) || qsTr("未知副本")).arg(page.dateLabel(run))
                                onClicked: Runtime.app.setHistoryRunChecked(run, checked)
                            }

                            HistoryCell {
                                columnKey: "date"
                                layout: page.columnLayout
                                Text {
                                    anchors.fill: parent
                                    textFormat: Text.PlainText
                                    text: page.dateLabel(run)
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                    // Figures elide rather than spill into the
                                    // next column when a column is narrowed.
                                    elide: Text.ElideRight
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.weight: Theme.figureWeight(true)
                                    font.family: Theme.figureFamily
                                    font.features: ({ "tnum": 1 })
                                }
                            }
                            HistoryCell {
                                columnKey: "entered"
                                layout: page.columnLayout
                                Text {
                                    anchors.fill: parent
                                    text: Runtime.fmt.localTime(run.entered_at_utc)
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideRight
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }
                            }
                            HistoryCell {
                                columnKey: "ended"
                                layout: page.columnLayout
                                Text {
                                    anchors.fill: parent
                                    text: Runtime.fmt.localTime(run.ended_at_utc)
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideRight
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }
                            }

                            HistoryCell {
                                columnKey: "duty"
                                layout: page.columnLayout
                                RowLayout {
                                    anchors.fill: parent
                                    spacing: 7

                                    CategoryIcon {
                                        category: run.duty_category || ""
                                        size: 22
                                    }
                                    ColumnLayout {
                                        Layout.fillWidth: true
                                        spacing: 0
                                        Text {
                                            Layout.fillWidth: true
                                            text: Theme.dutyName(run) || qsTr("未知副本")
                                            textFormat: Text.PlainText
                                            color: Theme.textPrimary
                                            font.pixelSize: Theme.fs(12)
                                            font.bold: true
                                            elide: Text.ElideRight
                                        }
                                        Text {
                                            Layout.fillWidth: true
                                            text: run.duty_expansion
                                                  ? qsTr("%1 · %2级").arg(Theme.expansionName(run.duty_expansion))
                                                                      .arg(run.duty_level || 0)
                                                  : Runtime.fmt.dash()
                                            textFormat: Text.PlainText
                                            color: Theme.textSecondary
                                            font.pixelSize: Theme.fs(11)
                                            font.family: Theme.figureFamily
                                            font.weight: Theme.figureWeight(false)
                                            font.features: ({ "tnum": 1 })
                                            elide: Text.ElideRight
                                        }
                                    }
                                }
                            }

                            HistoryCell {
                                columnKey: "category"
                                layout: page.columnLayout
                                Text {
                                    anchors.fill: parent
                                    text: Theme.categoryName(run.duty_category) || qsTr("未识别")
                                    textFormat: Text.PlainText
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(12)
                                    elide: Text.ElideRight
                                }
                            }

                            HistoryCell {
                                id: jobCell
                                columnKey: "job"
                                layout: page.columnLayout

                                // No room for the role glyph at the default 112 px,
                                // so the role only shows up on hover.
                                HoverHandler { id: jobHover }
                                ToolTip {
                                    id: jobTip
                                    visible: jobHover.hovered
                                    delay: 400
                                    text: (Theme.jobName(run) || qsTr("未知")) + " · "
                                          + Theme.roleGroupName(Runtime.jobs.roleGroup(run.job_id))
                                    contentItem: Text {
                                        text: jobTip.text
                                        textFormat: Text.PlainText
                                        font: jobTip.font
                                        wrapMode: Text.Wrap
                                        color: jobTip.palette.toolTipText
                                    }
                                }

                                RowLayout {
                                    anchors.centerIn: parent
                                    width: Math.min(implicitWidth, parent.width)
                                    spacing: 7

                                    JobIcon { jobId: run.job_id; size: 22 }
                                    Text {
                                        Layout.maximumWidth: Math.max(0, jobCell.width - 22 - 7)
                                        text: Theme.jobName(run) || qsTr("未知")
                                        textFormat: Text.PlainText
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(12)
                                        elide: Text.ElideRight
                                    }
                                }
                            }

                            HistoryCell {
                                columnKey: "result"
                                layout: page.columnLayout
                                Tag {
                                    anchors.centerIn: parent
                                    text: Theme.live(Runtime.fmt.runResultLabel(run))
                                    variant: Runtime.fmt.runInProgress(run)
                                             ? "outline" : Runtime.fmt.resultTagVariant(run.result || "UNKNOWN")
                                }
                            }

                            HistoryCell {
                                columnKey: "duration"
                                layout: page.columnLayout
                                Text {
                                    anchors.fill: parent
                                    text: Runtime.fmt.duration(run.duration_ms)
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                    elide: Text.ElideRight
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }
                            }
                            HistoryCell {
                                columnKey: "source"
                                layout: page.columnLayout
                                Text {
                                    anchors.fill: parent
                                    text: Theme.live(Runtime.fmt.sourceLabel(run.source || "AUTO_NETWORK"))
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(12)
                                    elide: Text.ElideRight
                                }
                            }
                            HistoryCell {
                                columnKey: "marks"
                                layout: page.columnLayout
                                Text {
                                    anchors.fill: parent
                                    horizontalAlignment: Text.AlignHCenter
                                    verticalAlignment: Text.AlignVCenter
                                    text: [run.source !== "IMPORT" && run.pending_review ? qsTr("待复核") : "",
                                           page.importIncomplete(run) ? qsTr("待补充") : "",
                                           // 中途加入: the duty was already under way at the entry
                                           // ($defs/Run.joined_in_progress); null and false say nothing here.
                                           run.joined_in_progress === true ? qsTr("中途加入") : "",
                                           // 已修正：人改过软件记下的内容。已确认：人只回答了软件
                                           // 留待复核的结局，或补上了它没认出的职业、副本。
                                           run.manually_corrected ? qsTr("已修正")
                                               : (Number(run.revision || 1) > 1 && !run.pending_review
                                                  && !run.manually_created && !run.soft_deleted
                                                  ? qsTr("已确认") : ""),
                                           run.soft_deleted ? qsTr("已删除") : "",
                                           run.manually_created ? qsTr("手动创建") : "",
                                           run.reflection ? qsTr("有笔记") : ""]
                                          .filter(function(part) { return part.length > 0 })
                                          .join(" · ")
                                    color: Theme.orangeText
                                    font.pixelSize: Theme.fs(11)
                                    elide: Text.ElideRight
                                }
                            }
                        }

                        Rectangle {
                            anchors.left: parent.left
                            anchors.right: parent.right
                            anchors.bottom: parent.bottom
                            height: 1
                            color: Theme.border
                        }
                    }
                }
            }

            Text {
                objectName: "historyEmptyText"
                Layout.fillWidth: true
                Layout.topMargin: 24
                Layout.bottomMargin: 24
                visible: Runtime.app.runs.total === 0
                // An emptied list after a refused or lost query is not "no matches".
                text: Runtime.app.runs.loadError.length > 0
                      ? qsTr("历史记录读取失败：%1").arg(Runtime.app.runs.loadError)
                      : qsTr("没有符合筛选条件的记录。")
                textFormat: Text.PlainText
                wrapMode: Text.WordWrap
                color: Runtime.app.runs.loadError.length > 0 ? Theme.orangeText : Theme.textSecondary
                font.pixelSize: Theme.fs(13)
            }

            RowLayout {
                id: pagerRow
                Layout.fillWidth: true
                Layout.preferredHeight: 34
                spacing: 8

                // 自定义列 sits at the table's foot, where it takes no width from
                // the rows; a right click on the header opens the same dialog.
                AppButton {
                    objectName: "historyCustomizeColumns"
                    text: qsTr("自定义列")
                    onClicked: columnsDialog.open()
                }

                Item { Layout.fillWidth: true }

                Text {
                    objectName: "historyCheckedCount"
                    text: qsTr("已勾选 %1 条 · 翻页保留").arg(page.checkedCount)
                    textFormat: Text.PlainText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                }

                AppButton {
                    objectName: "historyPreviousPage"
                    text: qsTr("上一页")
                    iconName: "chevron-left"
                    enabled: !Runtime.app.runs.loading && Runtime.app.runs.page > 1
                    onClicked: Runtime.app.runs.previousPage()
                }

                PageNumberInput {
                    objectName: "historyPageJump"
                    paginationModel: Runtime.app.runs
                }

                AppButton {
                    text: qsTr("下一页")
                    iconName: "chevron-right"
                    iconAfterText: true
                    enabled: !Runtime.app.runs.loading && Runtime.app.runs.page < Runtime.app.runs.pageCount
                    onClicked: Runtime.app.runs.nextPage()
                }
            }
        }
    }

    HistoryColumnsDialog {
        id: columnsDialog
        objectName: "historyColumnsDialog"
        columns: page.allColumns
        controller: page.columnsController
    }

    Popup {
        id: moreFilters
        objectName: "historyMoreFilters"
        parent: Overlay.overlay
        modal: true
        focus: true
        width: Math.min(370, page.width - 24)
        x: parent ? (parent.width - width) / 2 : 0
        y: parent ? (parent.height - height) / 2 : 0
        padding: 20
        background: DialogFrame {}
        Overlay.modal: Rectangle { color: Theme.modalScrim(moreFilters.palette.shadow) }
        contentItem: ColumnLayout {
            spacing: 12
            HeadingLabel { text: qsTr("更多筛选") }
            StyledComboBox {
                objectName: "historyMoreSource"
                Layout.fillWidth: true
                model: sourceBox.model
                // Mirrors sourceBox through its binding, which a language switch must not replace.
                keepsRowAcrossLanguages: false
                textRole: "label"
                currentIndex: sourceBox.currentIndex
                Accessible.name: qsTr("来源筛选")
                onActivated: { sourceBox.currentIndex = currentIndex; page.scheduleFilter() }
            }
            AppButton { Layout.alignment: Qt.AlignRight; text: qsTr("完成"); onClicked: moreFilters.close() }
        }
    }

    Dialog {
        id: filterConfirmation
        objectName: "historyFilterConfirmation"
        parent: Overlay.overlay
        modal: true
        focus: true
        width: Math.min(490, page.width - 24)
        x: parent ? (parent.width - width) / 2 : 0
        y: parent ? (parent.height - height) / 2 : 0
        padding: 20
        closePolicy: Popup.CloseOnEscape
        property bool answered: false
        background: DialogFrame {}
        Overlay.modal: Rectangle { color: Theme.modalScrim(filterConfirmation.palette.shadow) }
        onOpened: { answered = false; keepFilterButton.forceActiveFocus() }
        onClosed: { if (!answered) { answered = true; Runtime.app.confirmHistoryFilterChange(false) } }
        contentItem: ColumnLayout {
            spacing: 14
            HeadingLabel { Layout.fillWidth: true; text: qsTr("更改筛选并取消勾选？"); wrapMode: Text.Wrap }
            Text { Layout.fillWidth: true; text: qsTr("已经勾选 %1 条记录（含其他页）。更改筛选将清空这些勾选，不会删除记录。").arg(page.checkedCount); textFormat: Text.PlainText; wrapMode: Text.Wrap; color: Theme.textPrimary; font.pixelSize: Theme.fs(13) }
            RowLayout {
                Layout.fillWidth: true
                Item { Layout.fillWidth: true }
                AppButton { id: keepFilterButton; objectName: "historyKeepSelection"; text: qsTr("保留勾选和筛选"); onClicked: { filterConfirmation.answered = true; Runtime.app.confirmHistoryFilterChange(false); filterConfirmation.close() } }
                AppButton { objectName: "historyConfirmFilter"; text: qsTr("取消勾选并更改"); variant: "primary"; onClicked: { filterConfirmation.answered = true; Runtime.app.confirmHistoryFilterChange(true); filterConfirmation.close() } }
            }
        }
    }

    Dialog {
        id: batchDialog
        objectName: "historyBatchConfirmation"
        parent: Overlay.overlay
        modal: true
        focus: true
        width: Math.min(560, page.width - 24)
        x: parent ? (parent.width - width) / 2 : 0
        y: parent ? (parent.height - height) / 2 : 0
        padding: 20
        property string action: "soft_delete"
        property bool submitted: false
        readonly property bool purge: action === "purge"
        closePolicy: Runtime.app.historyBatchRunning ? Popup.NoAutoClose : Popup.CloseOnEscape
        background: DialogFrame {}
        Overlay.modal: Rectangle { color: Theme.modalScrim(batchDialog.palette.shadow) }
        onOpened: batchCancelButton.forceActiveFocus()
        contentItem: ColumnLayout {
            spacing: 14
            HeadingLabel { Layout.fillWidth: true; text: batchDialog.purge ? qsTr("永久删除 %1 条记录？").arg(page.checkedCount) : batchDialog.action === "restore" ? qsTr("恢复 %1 条记录？").arg(page.checkedCount) : qsTr("删除 %1 条记录？").arg(page.checkedCount); wrapMode: Text.Wrap }
            Text {
                Layout.fillWidth: true
                text: batchDialog.purge
                    ? qsTr("记录、心得、事件和修改内容会被永久删除，关联图片也会清理，无法恢复。已有备份、导出文件和分享图片会保留。图片清理失败时会保留清理任务，之后重试。")
                    : batchDialog.action === "restore" ? qsTr("选中的记录将恢复到历史记录中，取消自动永久删除，统计会重新计算。")
                    : Runtime.app.historyRetentionDays === 0 ? qsTr("记录会移到“已删除”，不再计入统计，可以恢复。当前设置为不自动永久删除。")
                    : Runtime.app.historyRetentionDays < 0 ? qsTr("记录会移到“已删除”，不再计入统计，永久删除前可以恢复。尚未读到保留期，请到“设置”的“数据”页确认后台服务当前使用的天数。")
                    : qsTr("记录会移到“已删除”，不再计入统计，%1 天内可以恢复；到期后会自动永久删除。软件关闭期间到期的记录，会在下次启动时清理。").arg(Runtime.app.historyRetentionDays)
                textFormat: Text.PlainText
                wrapMode: Text.Wrap
                color: batchDialog.purge ? Theme.orangeText : Theme.textPrimary
                font.pixelSize: Theme.fs(13)
            }
            StyledTextField { id: batchReason; objectName: "historyBatchReason"; Layout.fillWidth: true; maximumLength: 500; enabled: !Runtime.app.historyBatchRunning; placeholderText: qsTr("操作原因（必填）"); Accessible.name: qsTr("批量操作原因") }
            Text { Layout.fillWidth: true; visible: batchDialog.submitted && Runtime.app.historyBatchFeedback.length > 0; text: Runtime.app.historyBatchFeedback; textFormat: Text.PlainText; wrapMode: Text.Wrap; color: Theme.orangeText; font.pixelSize: Theme.fs(12) }
            RowLayout {
                Layout.fillWidth: true
                Item { Layout.fillWidth: true }
                AppButton { id: batchCancelButton; text: qsTr("取消"); enabled: !Runtime.app.historyBatchRunning; onClicked: batchDialog.close() }
                AppButton {
                    objectName: "historyConfirmBatch"
                    text: Runtime.app.historyBatchRunning ? qsTr("正在处理…") : batchDialog.purge ? qsTr("确认永久删除") : batchDialog.action === "restore" ? qsTr("确认恢复") : qsTr("确认删除")
                    variant: "primary"
                    enabled: !Runtime.app.historyBatchRunning && page.checkedCount > 0 && batchReason.text.trim().length > 0
                    onClicked: { batchDialog.submitted = true; Runtime.app.mutateCheckedHistoryRuns(batchDialog.action, Theme.storedReason(batchReason.text)) }
                }
            }
        }
    }

    // Click-outside-to-close. Alive only while the panel is open, so the table
    // underneath keeps its full width and stays scrollable (MouseArea does not
    // consume wheel events).
    MouseArea {
        anchors.fill: parent
        z: 5
        visible: Runtime.app.hasSelection
        enabled: Runtime.app.hasSelection
        acceptedButtons: Qt.LeftButton | Qt.RightButton
        onClicked: Runtime.app.clearSelection()
    }

    RunDetailPanel {
        visible: Runtime.app.hasSelection
        anchors.top: parent.top
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.margins: 12
        width: 380
        z: 10
        runData: Runtime.app.selectedRun
        revisions: Runtime.app.selectedRunRevisions
        activeTab: page.detailTab
        onTabChanged: function(tab) { page.detailTab = tab }
        onCloseRequested: Runtime.app.clearSelection()
        onCorrectRequested: page.openCorrectRequested()
        onDeleteRequested: page.openDeleteRequested()
        onRestoreRequested: page.openRestoreRequested()
        onUndoRequested: page.openUndoRequested()
        onReviewRequested: page.openReviewRequested()
        onReflectRequested: page.reflectRequested(Runtime.app.selectedRun)
        onShareRequested: page.shareRequested(Runtime.app.selectedRun)
    }
}
