import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

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
    readonly property int checkedCount: App.checkedHistoryRunCount
    function isChecked(runId) { return App.checkedHistoryRunIds.indexOf(runId) >= 0 }
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
        batchReason.text = action === "purge" ? qsTr("永久删除回收站记录")
                         : action === "restore" ? qsTr("恢复回收站记录") : qsTr("批量移入回收站")
        batchDialog.open()
    }
    function dateLabel(run) {
        const actual = run.matched_at_utc || run.entered_at_utc || run.ended_at_utc
        if (actual) return Fmt.localDate(actual)
        const source = run.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站\n%1").arg(Fmt.localDate(source.source_recorded_at_utc))
        if (source.source_recorded_at) {
            const date = /^(\d{4})[-\/](\d{1,2})[-\/](\d{1,2})$/.exec(source.source_recorded_at)
            if (date) {
                const normalized = date[1] + "-" + String(date[2]).padStart(2, "0") + "-" + String(date[3]).padStart(2, "0")
                if (RunForm.isValidDate(normalized)) return qsTr("原站\n%1").arg(normalized)
            }
        }
        return Fmt.dash()
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
        const rows = App.dutyOptions
        for (let index = 0; index < rows.length; ++index) {
            if (rows[index].content_id === contentId)
                return rows[index].duty_name || qsTr("未知副本")
        }
        return qsTr("指定副本")
    }

    // 副本 is the only column that stretches to fill the table width; the matched
    // time-of-day appears in the detail panel only.
    readonly property int dutyColumnWidth: 228
    // Classic sets times in IBM Plex Mono, whose figures are wider than the body
    // face's: 8 of them at 12 px need 70 px to keep a gap before the next column.
    readonly property int timeColumnWidth: Theme.eorzea ? 64 : 70
    // The same wider mono figures: classic needs 96 px, at 86 the date touches 进本.
    readonly property int dateColumnWidth: Theme.eorzea ? 86 : 96
    // Column titles sit centred over their column; the cells of the narrow
    // columns centre too, so a title and its figures line up. 副本 is the one
    // exception: an icon, a name and a second line read from the left.
    readonly property var columns: [
        { key: "", label: "", width: 32 },
        { key: "history_date", label: qsTr("日期"), width: page.dateColumnWidth },
        { key: "entered_at_utc", label: qsTr("进本"), width: page.timeColumnWidth },
        { key: "ended_at_utc", label: qsTr("结束"), width: page.timeColumnWidth },
        { key: "duty_name", label: qsTr("副本"), width: page.dutyColumnWidth, fill: true },
        { key: "", label: qsTr("类型"), width: 84 },
        { key: "", label: qsTr("职业"), width: 112 },
        { key: "", label: qsTr("结果"), width: 92 },
        { key: "duration_ms", label: qsTr("耗时"), width: 66 },
        { key: "", label: qsTr("来源"), width: 60 },
        { key: "", label: qsTr("标记"), width: 92 }
    ]

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
        const fixed = pageHeader.height + filterRow.height + (activeFilterRow.visible ? activeFilterRow.height : 0) + batchBar.height + (batchFeedback.visible ? batchFeedback.height : 0) + filterSeparator.height
                    + tableHeader.height + pagerRow.height + contentColumn.spacing * (6 + (activeFilterRow.visible ? 1 : 0) + (batchFeedback.visible ? 1 : 0))
                    + Math.max(0, page.reservedBottom)
        return Math.max(page.minimumRowsPerPage, Math.floor((page.height - fixed) / page.rowHeight))
    }
    onRowsThatFitChanged: pageSizeDebounce.restart()

    Timer {
        id: pageSizeDebounce
        interval: 150
        onTriggered: App.runs.pageSize = page.rowsThatFit
    }

    function toUtcRange(dateText, endOfDay) {
        if (!dateText || dateText.length === 0)
            return null
        if (!RunForm.isValidDate(dateText))
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

    function scheduleFilter() {
        if (page.suspendFilter)
            return
        filterDebounce.restart()
    }

    // True while applyFilter hands its own filter to App. The change it causes
    // comes straight back through onHistoryFilterChanged, and mirroring it would
    // rewrite the fields under the user's cursor (review OK-5).
    property bool applying: false

    // 开始 later than 结束: the Collector refuses the query, so it is not sent.
    readonly property bool rangeInverted: {
        const from = toUtcRange(fromField.text.trim(), false)
        const to = toUtcRange(toField.text.trim(), true)
        return from !== null && to !== null && from > to
    }

    function applyFilter() {
        // A half-typed or invalid date is neither queried nor silently left out of
        // the query (docs/ui-design.md §4.2): its field turns red and the list keeps
        // its last answer until the date is whole. An inverted range waits the same way.
        if (!fromField.valid || !toField.valid || page.rangeInverted)
            return
        const filter = { date_field: "history_date" }
        if (searchField.text.trim().length > 0)
            filter.text = searchField.text.trim()
        const from = toUtcRange(fromField.text.trim(), false)
        const to = toUtcRange(toField.text.trim(), true)
        if (from)
            filter.from_utc = from
        if (to)
            filter.to_utc = to
        if (from) filter.history_from_day = fromField.text.trim()
        if (to) filter.history_to_day = toField.text.trim()
        if (page.contentFilter !== null && page.contentFilter !== undefined)
            filter.content_id = [page.contentFilter]
        if (categoryBox.currentIndex > 0)
            filter.duty_category = [categoryBox.model[categoryBox.currentIndex]]
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
        if (includeDeleted.checked)
            filter.include_deleted = true
        page.applying = true
        App.setHistoryFilter(filter)
        page.applying = false
    }

    function resetFilter() {
        page.suspendFilter = true
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
        includeDeleted.checked = false
        page.suspendFilter = false
        filterDebounce.stop()
        App.resetHistoryFilter()
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
        const filter = App.historyFilter
        page.suspendFilter = true
        searchField.text = filter.text || ""
        fromField.text = filter.from_utc ? Fmt.localDate(filter.from_utc) : ""
        toField.text = filter.to_utc ? Fmt.localDate(filter.to_utc) : ""
        categoryBox.currentIndex = filterIndex(categoryBox.model, "", filter.duty_category)
        jobBox.currentIndex = filterIndex(jobBox.model, "job_id", filter.job_id)
        page.retainedJobFilter = jobBox.currentIndex === 0 ? firstFilterValue(filter.job_id) : null
        resultBox.currentIndex = filterIndex(resultBox.model, "value", filter.result)
        sourceBox.currentIndex = filterIndex(sourceBox.model, "value", filter.source)
        correctedOnly.checked = !!filter.corrected_only
        page.pendingReviewFilter = !!filter.pending_review
        withReflection.checked = !!filter.with_reflection
        includeDeleted.checked = !!filter.include_deleted
        page.contentFilter = firstFilterValue(filter.content_id)
        page.suspendFilter = false
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
        if (App.historyFilterConfirmationPending) filterConfirmation.open()
    }

    Connections {
        target: App

        function onHistoryFilterChanged() {
            page.syncFilter()
        }
        function onHistoryFilterConfirmationChanged() {
            if (App.historyFilterConfirmationPending && !filterConfirmation.visible)
                filterConfirmation.open()
        }
        function onHistoryBatchChanged() {
            if (batchDialog.submitted && !App.historyBatchRunning && page.checkedCount === 0)
                batchDialog.close()
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
                subtitle: App.runs.loadError.length > 0
                          ? qsTr("读取失败")
                          : qsTr("%1 条匹配 · 第 %2 / %3 页").arg(App.runs.total)
                                                             .arg(App.runs.page)
                                                             .arg(App.runs.pageCount)

                AppButton { text: page.checkedCount > 0 ? qsTr("导出选中 CSV (%1)").arg(page.checkedCount) : qsTr("导出 CSV"); iconName: "file-down"; enabled: !App.historyBatchRunning; onClicked: App.exportCsv() }
                AppButton { text: page.checkedCount > 0 ? qsTr("导出选中 JSON (%1)").arg(page.checkedCount) : qsTr("导出 JSON"); iconName: "file-json"; enabled: !App.historyBatchRunning; onClicked: App.exportJson() }
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
            // narrow combo boxes, the toggle chips and 清除. A Flow rather than
            // a RowLayout so that on a narrow window the trailing controls wrap
            // to a second line instead of being clipped off the right edge.
            Flow {
                id: filterRow
                Layout.fillWidth: true
                spacing: 6
                enabled: !App.historyBatchRunning

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
                // Narrow fallback: search + the two dates share the first line,
                // the combo boxes, chips and 清除 wrap to the second.
                readonly property real dateWidth: fromField.width + toField.width + 2 * spacing

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

                // 年/月/日 with a calendar behind the icon; a typed yyyy-MM-dd
                // is accepted as well.
                DateField {
                    id: fromField
                    objectName: "fromDateField"
                    width: 118
                    onTextChanged: page.scheduleFilter()
                    Accessible.name: qsTr("开始日期")
                }

                DateField {
                    id: toField
                    objectName: "toDateField"
                    width: 118
                    onTextChanged: page.scheduleFilter()
                    Accessible.name: qsTr("结束日期")
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

                Chip {
                    id: pendingChip
                    objectName: "pendingReviewChip"
                    visible: true
                    height: filterRow.controlHeight
                    text: qsTr("待复核")
                    checked: page.pendingReviewFilter
                    activeFocusOnTab: true
                    Accessible.role: Accessible.CheckBox
                    Accessible.checked: checked
                    Accessible.name: qsTr("待复核筛选")
                    onToggled: function(checked) {
                        page.pendingReviewFilter = checked
                        // Chip's TapHandler assigns checked; restore the mirror so
                        // cancelling a filter confirmation restores its visual state.
                        pendingChip.checked = Qt.binding(function() { return page.pendingReviewFilter })
                        page.scheduleFilter()
                    }
                    Keys.onSpacePressed: function(event) { page.pendingReviewFilter = !page.pendingReviewFilter; page.scheduleFilter(); event.accepted = true }
                    border.width: activeFocus ? 1 : 0
                    border.color: Theme.accent
                }

                StyledComboBox {
                    id: categoryBox
                    width: 78
                    model: [qsTr("全部类型")].concat(App.categoryOptions)
                    displayText: currentIndex === 0 ? qsTr("类型") : currentText
                    Accessible.name: qsTr("类型筛选")
                    onActivated: page.scheduleFilter()
                }

                StyledComboBox {
                    id: jobBox
                    objectName: "historyJobFilter"
                    width: 78
                    // The first row clears this filter; the closed field keeps its short label.
                    model: [{ job_id: null, job_name: qsTr("全部职业") }].concat(App.battleJobOptions)
                    textRole: "job_name"
                    displayText: currentIndex === 0 ? qsTr("职业") : currentText
                    Accessible.name: qsTr("职业筛选")
                    onActivated: {
                        page.retainedJobFilter = null
                        page.scheduleFilter()
                    }
                }

                StyledComboBox {
                    id: resultBox
                    width: 80
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
                    width: 78
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

                Chip {
                    id: correctedOnly
                    visible: !page.narrowFilters
                    height: filterRow.controlHeight
                    text: qsTr("已修正")
                    onToggled: page.scheduleFilter()
                }

                Chip {
                    id: withReflection
                    visible: !page.narrowFilters
                    height: filterRow.controlHeight
                    text: qsTr("有笔记")
                    onToggled: page.scheduleFilter()
                }

                Chip {
                    id: includeDeleted
                    visible: !page.narrowFilters
                    height: filterRow.controlHeight
                    text: qsTr("含已删除")
                    onToggled: page.scheduleFilter()
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
                    width: 40
                    height: filterRow.controlHeight
                    text: qsTr("清除")
                    onClicked: page.resetFilter()
                }
            }

            Flow {
                id: activeFilterRow
                Layout.fillWidth: true
                visible: (page.contentFilter !== null && page.contentFilter !== undefined) || page.hasRetainedJobFilter
                         || (page.narrowFilters && (sourceBox.currentIndex > 0 || correctedOnly.checked || withReflection.checked || includeDeleted.checked))
                spacing: 6
                Chip { visible: page.contentFilter !== null && page.contentFilter !== undefined; text: page.dutyLabel(page.contentFilter); checked: true; removable: true; onRemoved: { page.contentFilter = null; page.scheduleFilter() } }
                Chip { objectName: "retainedJobFilterChip"; visible: page.hasRetainedJobFilter; text: qsTr("职业：%1").arg(Jobs.jobName(page.retainedJobFilter)); checked: true; removable: true; Accessible.name: text; onRemoved: { page.retainedJobFilter = null; page.scheduleFilter() } }
                Chip { visible: page.narrowFilters && sourceBox.currentIndex > 0; text: sourceBox.currentText; checked: true; removable: true; onRemoved: { sourceBox.currentIndex = 0; page.scheduleFilter() } }
                Chip { visible: page.narrowFilters && correctedOnly.checked; text: qsTr("已修正"); checked: true; removable: true; onRemoved: { correctedOnly.checked = false; page.scheduleFilter() } }
                Chip { visible: page.narrowFilters && withReflection.checked; text: qsTr("有笔记"); checked: true; removable: true; onRemoved: { withReflection.checked = false; page.scheduleFilter() } }
                Chip { visible: page.narrowFilters && includeDeleted.checked; text: qsTr("含已删除"); checked: true; removable: true; onRemoved: { includeDeleted.checked = false; page.scheduleFilter() } }
            }

            Flow {
                id: batchBar
                objectName: "historyBatchBar"
                Layout.fillWidth: true
                spacing: 8
                enabled: !App.historyBatchRunning
                SelectionCheckBox {
                    objectName: "selectCurrentHistoryPage"
                    text: qsTr("全选当前页")
                    checked: App.allCurrentHistoryPageChecked
                    enabled: !App.runs.loading && App.runs.loadError.length === 0 && App.runs.total > 0
                    onClicked: App.setCurrentHistoryPageChecked(checked)
                }
                Text { height: 32; verticalAlignment: Text.AlignVCenter; text: qsTr("已勾选 %1 条 · 翻页保留").arg(page.checkedCount); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                AppButton { text: qsTr("取消勾选"); visible: page.checkedCount > 0; variant: "ghost"; onClicked: App.clearCheckedHistoryRuns() }
                AppButton { objectName: "batchSoftDeleteButton"; text: qsTr("移入回收站"); visible: page.checkedCount > 0; enabled: App.checkedHistoryDeletedCount === 0; onClicked: page.requestBatch("soft_delete") }
                AppButton { objectName: "batchRestoreButton"; text: qsTr("恢复选中"); visible: page.checkedCount > 0 && App.checkedHistoryDeletedCount > 0; enabled: App.checkedHistoryDeletedCount === page.checkedCount; onClicked: page.requestBatch("restore") }
                AppButton { objectName: "batchPurgeButton"; text: qsTr("永久删除"); visible: page.checkedCount > 0 && App.checkedHistoryDeletedCount > 0; enabled: App.checkedHistoryDeletedCount === page.checkedCount; onClicked: page.requestBatch("purge") }
            }

            Text {
                id: batchFeedback
                Layout.fillWidth: true
                visible: text.length > 0
                text: App.historyBatchRunning ? qsTr("正在处理选中的记录…") : App.historyBatchFeedback
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
            Rectangle {
                id: tableHeader
                Layout.fillWidth: true
                Layout.preferredHeight: 32
                color: Theme.eorzea
                       ? (Theme.dark ? "#2e000000" : "#14785c28")
                       : "transparent"

                Rectangle {
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: 1
                    color: Theme.eorzea ? Theme.gold3 : Theme.border
                }

                RowLayout {
                    anchors.fill: parent
                    anchors.leftMargin: 4
                    anchors.rightMargin: 4
                    spacing: 0

                    Repeater {
                        model: page.columns

                        delegate: Text {
                            required property var modelData

                            Layout.preferredWidth: modelData.width
                            Layout.fillWidth: modelData.fill === true
                            text: modelData.label
                                  + (modelData.key.length > 0 && App.runs.sortField === modelData.key
                                     ? (App.runs.sortAscending ? " ↑" : " ↓") : "")
                            horizontalAlignment: Text.AlignHCenter
                            // workbench `.table th`: t5, regular, text-3.
                            color: Theme.eorzea
                                   ? Theme.gold
                                   : (App.runs.sortField === modelData.key && modelData.key.length > 0
                                      ? Theme.accent : Theme.textMuted)
                            font.pixelSize: Theme.fs(12)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                            elide: Text.ElideRight

                            TapHandler {
                                enabled: modelData.key.length > 0
                                onTapped: App.runs.sortBy(modelData.key)
                            }
                        }
                    }
                }
            }

            // The table sizes to its content (a full page of rows always fits),
            // so the page - not the table - is what scrolls.
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 0

                Repeater {
                    id: rowRepeater
                    model: App.runs

                    delegate: Rectangle {
                        id: runRow

                        required property var run
                        required property int index

                        readonly property bool selected: App.selectedRun.run_id === run.run_id

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
                                App.selectRun(run)
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

                        RowLayout {
                            anchors.fill: parent
                            anchors.leftMargin: 4
                            anchors.rightMargin: 4
                            spacing: 0

                            SelectionCheckBox {
                                objectName: "historyRunCheck_" + run.run_id
                                Layout.preferredWidth: 32
                                checked: page.isChecked(run.run_id)
                                enabled: !App.historyBatchRunning
                                Accessible.name: qsTr("勾选记录 %1 %2").arg(run.duty_name || qsTr("未知副本")).arg(page.dateLabel(run))
                                onClicked: App.setHistoryRunChecked(run, checked)
                            }

                            Text {
                                Layout.preferredWidth: page.dateColumnWidth
                                textFormat: Text.PlainText
                                text: page.dateLabel(run)
                                horizontalAlignment: Text.AlignHCenter
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                font.weight: Theme.figureWeight(true)
                                font.family: Theme.figureFamily
                                font.features: ({ "tnum": 1 })
                            }
                            Text {
                                Layout.preferredWidth: page.timeColumnWidth
                                text: Fmt.localTime(run.entered_at_utc)
                                horizontalAlignment: Text.AlignHCenter
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                font.family: Theme.figureFamily
                                font.weight: Theme.figureWeight(false)
                                font.features: ({ "tnum": 1 })
                            }
                            Text {
                                Layout.preferredWidth: page.timeColumnWidth
                                text: Fmt.localTime(run.ended_at_utc)
                                horizontalAlignment: Text.AlignHCenter
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                font.family: Theme.figureFamily
                                font.weight: Theme.figureWeight(false)
                                font.features: ({ "tnum": 1 })
                            }

                            RowLayout {
                                Layout.fillWidth: true
                                Layout.preferredWidth: page.dutyColumnWidth
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
                                        text: run.duty_name || qsTr("未知副本")
                                        textFormat: Text.PlainText
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(12)
                                        font.bold: true
                                        elide: Text.ElideRight
                                    }
                                    Text {
                                        Layout.fillWidth: true
                                        text: run.duty_expansion
                                              ? qsTr("%1 · %2级").arg(run.duty_expansion)
                                                                  .arg(run.duty_level || 0)
                                              : Fmt.dash()
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

                            Text {
                                Layout.preferredWidth: 84
                                text: run.duty_category || qsTr("未识别")
                                textFormat: Text.PlainText
                                horizontalAlignment: Text.AlignHCenter
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(12)
                                elide: Text.ElideRight
                            }

                            Item {
                                // A nested layout fills by default; only 副本
                                // stretches, as in the header row.
                                Layout.fillWidth: false
                                Layout.preferredWidth: 112
                                Layout.fillHeight: true

                                // No room for the role glyph in 112 px, so the
                                // role only shows up on hover.
                                HoverHandler { id: jobHover }
                                ToolTip {
                                    id: jobTip
                                    visible: jobHover.hovered
                                    delay: 400
                                    text: (run.job_name || qsTr("未知")) + " · " + Jobs.roleGroup(run.job_id)
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
                                        Layout.maximumWidth: 112 - 22 - 7
                                        text: run.job_name || qsTr("未知")
                                        textFormat: Text.PlainText
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(12)
                                        elide: Text.ElideRight
                                    }
                                }
                            }

                            Item {
                                Layout.preferredWidth: 92
                                Layout.preferredHeight: 28
                                Tag {
                                    anchors.centerIn: parent
                                    text: Fmt.runResultLabel(run)
                                    variant: Fmt.runInProgress(run)
                                             ? "outline" : Fmt.resultTagVariant(run.result || "UNKNOWN")
                                }
                            }

                            Text {
                                Layout.preferredWidth: 66
                                text: Fmt.duration(run.duration_ms)
                                horizontalAlignment: Text.AlignHCenter
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                font.family: Theme.figureFamily
                                font.weight: Theme.figureWeight(false)
                                font.features: ({ "tnum": 1 })
                            }
                            Text {
                                Layout.preferredWidth: 60
                                text: Fmt.sourceLabel(run.source || "AUTO_NETWORK")
                                horizontalAlignment: Text.AlignHCenter
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(12)
                                elide: Text.ElideRight
                            }
                            Text {
                                Layout.preferredWidth: 92
                                horizontalAlignment: Text.AlignHCenter
                                text: [run.source !== "IMPORT" && run.pending_review ? qsTr("待复核") : "",
                                       page.importIncomplete(run) ? qsTr("待补充") : "",
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
                visible: App.runs.total === 0
                // An emptied list after a refused or lost query is not "no matches".
                text: App.runs.loadError.length > 0
                      ? qsTr("历史记录读取失败：%1").arg(App.runs.loadError)
                      : qsTr("没有符合筛选条件的记录。")
                textFormat: Text.PlainText
                wrapMode: Text.WordWrap
                color: App.runs.loadError.length > 0 ? Theme.orangeText : Theme.textSecondary
                font.pixelSize: Theme.fs(13)
            }

            RowLayout {
                id: pagerRow
                Layout.fillWidth: true
                Layout.preferredHeight: 34
                spacing: 8

                Item { Layout.fillWidth: true }

                AppButton {
                    text: qsTr("上一页")
                    iconName: "chevron-left"
                    enabled: App.runs.page > 1
                    onClicked: App.runs.previousPage()
                }

                Text {
                    text: qsTr("%1 / %2").arg(App.runs.page).arg(App.runs.pageCount)
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12)
                    font.family: Theme.figureFamily
                    font.weight: Theme.figureWeight(false)
                    font.features: ({ "tnum": 1 })
                }

                AppButton {
                    text: qsTr("下一页")
                    iconName: "chevron-right"
                    iconAfterText: true
                    enabled: App.runs.page < App.runs.pageCount
                    onClicked: App.runs.nextPage()
                }
            }
        }
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
                textRole: "label"
                currentIndex: sourceBox.currentIndex
                Accessible.name: qsTr("来源筛选")
                onActivated: { sourceBox.currentIndex = currentIndex; page.scheduleFilter() }
            }
            SelectionCheckBox { text: qsTr("只看已修正"); checked: correctedOnly.checked; onClicked: { correctedOnly.checked = checked; page.scheduleFilter() } }
            SelectionCheckBox { text: qsTr("只看有笔记"); checked: withReflection.checked; onClicked: { withReflection.checked = checked; page.scheduleFilter() } }
            SelectionCheckBox { objectName: "historyMoreIncludeDeleted"; text: qsTr("包含回收站记录"); checked: includeDeleted.checked; onClicked: { includeDeleted.checked = checked; page.scheduleFilter() } }
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
        onClosed: { if (!answered) { answered = true; App.confirmHistoryFilterChange(false) } }
        contentItem: ColumnLayout {
            spacing: 14
            HeadingLabel { Layout.fillWidth: true; text: qsTr("更改筛选并取消勾选？"); wrapMode: Text.Wrap }
            Text { Layout.fillWidth: true; text: qsTr("已经勾选 %1 条记录（含其他页）。更改筛选将清空这些勾选，不会删除记录。").arg(page.checkedCount); textFormat: Text.PlainText; wrapMode: Text.Wrap; color: Theme.textPrimary; font.pixelSize: Theme.fs(13) }
            RowLayout {
                Layout.fillWidth: true
                Item { Layout.fillWidth: true }
                AppButton { id: keepFilterButton; objectName: "historyKeepSelection"; text: qsTr("保留勾选和筛选"); onClicked: { filterConfirmation.answered = true; App.confirmHistoryFilterChange(false); filterConfirmation.close() } }
                AppButton { objectName: "historyConfirmFilter"; text: qsTr("取消勾选并更改"); variant: "primary"; onClicked: { filterConfirmation.answered = true; App.confirmHistoryFilterChange(true); filterConfirmation.close() } }
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
        closePolicy: App.historyBatchRunning ? Popup.NoAutoClose : Popup.CloseOnEscape
        background: DialogFrame {}
        Overlay.modal: Rectangle { color: Theme.modalScrim(batchDialog.palette.shadow) }
        onOpened: batchCancelButton.forceActiveFocus()
        contentItem: ColumnLayout {
            spacing: 14
            HeadingLabel { Layout.fillWidth: true; text: batchDialog.purge ? qsTr("永久删除 %1 条记录？").arg(page.checkedCount) : batchDialog.action === "restore" ? qsTr("恢复 %1 条记录？").arg(page.checkedCount) : qsTr("将 %1 条记录移入回收站？").arg(page.checkedCount); wrapMode: Text.Wrap }
            Text {
                Layout.fillWidth: true
                text: batchDialog.purge
                    ? qsTr("当前记录、心得、事件与修订正文会被永久移除，关联备注图片会清理；该操作不能撤销。已有备份、导出文件和分享图片保留。图片清理失败会保留任务并重试。")
                    : batchDialog.action === "restore" ? qsTr("选中的回收站记录将恢复到历史，自动清理计时取消，统计会重新计算。")
                    : App.historyRetentionDays === 0 ? qsTr("记录会从统计中移除，可以在回收站恢复。当前设为永不自动清理。")
                    : App.historyRetentionDays < 0 ? qsTr("记录会从统计中移除，可在保留期内恢复。保留期尚未读回，可到设置·数据确认采集器的生效值。")
                    : qsTr("记录会从统计中移除，可在保留期内恢复。软删除后按设置的 %1 天保留期自动永久清理；关闭软件时顺延到下次启动。").arg(App.historyRetentionDays)
                textFormat: Text.PlainText
                wrapMode: Text.Wrap
                color: batchDialog.purge ? Theme.orangeText : Theme.textPrimary
                font.pixelSize: Theme.fs(13)
            }
            StyledTextField { id: batchReason; objectName: "historyBatchReason"; Layout.fillWidth: true; maximumLength: 500; enabled: !App.historyBatchRunning; placeholderText: qsTr("操作原因（必填）"); Accessible.name: qsTr("批量操作原因") }
            Text { Layout.fillWidth: true; visible: batchDialog.submitted && App.historyBatchFeedback.length > 0; text: App.historyBatchFeedback; textFormat: Text.PlainText; wrapMode: Text.Wrap; color: Theme.orangeText; font.pixelSize: Theme.fs(12) }
            RowLayout {
                Layout.fillWidth: true
                Item { Layout.fillWidth: true }
                AppButton { id: batchCancelButton; text: qsTr("取消"); enabled: !App.historyBatchRunning; onClicked: batchDialog.close() }
                AppButton {
                    objectName: "historyConfirmBatch"
                    text: App.historyBatchRunning ? qsTr("正在处理…") : batchDialog.purge ? qsTr("确认永久删除") : batchDialog.action === "restore" ? qsTr("确认恢复") : qsTr("确认移入回收站")
                    variant: "primary"
                    enabled: !App.historyBatchRunning && page.checkedCount > 0 && batchReason.text.trim().length > 0
                    onClicked: { batchDialog.submitted = true; App.mutateCheckedHistoryRuns(batchDialog.action, batchReason.text) }
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
        visible: App.hasSelection
        enabled: App.hasSelection
        acceptedButtons: Qt.LeftButton | Qt.RightButton
        onClicked: App.clearSelection()
    }

    RunDetailPanel {
        visible: App.hasSelection
        anchors.top: parent.top
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.margins: 12
        width: 380
        z: 10
        runData: App.selectedRun
        revisions: App.selectedRunRevisions
        activeTab: page.detailTab
        onTabChanged: function(tab) { page.detailTab = tab }
        onCloseRequested: App.clearSelection()
        onCorrectRequested: page.openCorrectRequested()
        onDeleteRequested: page.openDeleteRequested()
        onRestoreRequested: page.openRestoreRequested()
        onUndoRequested: page.openUndoRequested()
        onReviewRequested: page.openReviewRequested()
        onReflectRequested: page.reflectRequested(App.selectedRun)
        onShareRequested: page.shareRequested(App.selectedRun)
    }
}
