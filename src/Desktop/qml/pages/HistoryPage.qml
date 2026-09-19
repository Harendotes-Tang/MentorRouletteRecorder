import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Item {
    id: page

    signal openManualRequested()
    signal openCorrectRequested()
    signal openDeleteRequested()
    signal openRestoreRequested()
    signal openUndoRequested()
    signal openReviewRequested()
    // Opens ReflectionDialog for the selected run.
    signal reflectRequested(var run)

    property string detailTab: "info"
    property bool suspendFilter: false
    // A drill-down from 副本统计 arrives as a content_id; the 副本 chip below
    // mirrors it so the two never disagree about what is filtered.
    property var contentFilter: null

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
    // face's: 8 of them need 64 px to keep a gap before the next column.
    readonly property int timeColumnWidth: Theme.eorzea ? 58 : 64
    // The same wider mono figures: classic needs 88 px, at 78 the date touches 进本.
    readonly property int dateColumnWidth: Theme.eorzea ? 78 : 88
    readonly property var columns: [
        { key: "matched_at_utc", label: qsTr("日期"), width: page.dateColumnWidth },
        { key: "entered_at_utc", label: qsTr("进本"), width: page.timeColumnWidth },
        { key: "ended_at_utc", label: qsTr("结束"), width: page.timeColumnWidth },
        { key: "duty_name", label: qsTr("副本"), width: page.dutyColumnWidth, fill: true },
        { key: "", label: qsTr("类型"), width: 76 },
        { key: "", label: qsTr("职业"), width: 112 },
        { key: "", label: qsTr("结果"), width: 92 },
        { key: "duration_ms", label: qsTr("耗时"), width: 62 },
        { key: "", label: qsTr("来源"), width: 60 },
        { key: "", label: qsTr("标记"), width: 74 }
    ]

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

    function applyFilter() {
        const filter = { date_field: "matched_at_utc" }
        if (searchField.text.trim().length > 0)
            filter.text = searchField.text.trim()
        const from = toUtcRange(fromField.text.trim(), false)
        const to = toUtcRange(toField.text.trim(), true)
        if (from)
            filter.from_utc = from
        if (to)
            filter.to_utc = to
        if (page.contentFilter !== null && page.contentFilter !== undefined)
            filter.content_id = [page.contentFilter]
        if (categoryBox.currentIndex > 0)
            filter.duty_category = [categoryBox.model[categoryBox.currentIndex]]
        if (jobBox.currentIndex > 0)
            filter.job_id = [jobBox.model[jobBox.currentIndex].job_id]
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
        App.setHistoryFilter(filter)
    }

    function resetFilter() {
        page.suspendFilter = true
        searchField.text = ""
        fromField.text = ""
        toField.text = ""
        page.contentFilter = null
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
    }

    Connections {
        target: App

        function onHistoryFilterChanged() {
            page.syncFilter()
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
                title: qsTr("历史记录")
                subtitle: qsTr("%1 条匹配 · 第 %2 / %3 页").arg(App.runs.total)
                                                           .arg(App.runs.page)
                                                           .arg(App.runs.pageCount)

                AppButton { text: qsTr("导出 CSV"); iconName: "file-down"; onClicked: App.exportCsv() }
                AppButton { text: qsTr("导出 JSON"); iconName: "file-json"; onClicked: App.exportJson() }
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
                    // -1 keeps a sub-pixel rounding from pushing the last
                    // control of a line onto the next one.
                    width: filterRow.singleLine ? filterRow.width - filterRow.restWidth - 1
                                                : filterRow.width - filterRow.dateWidth - 1
                    placeholderText: qsTr("搜索 副本 / 职业 / 备注")
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
                    visible: page.contentFilter !== null && page.contentFilter !== undefined
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
                    visible: page.pendingReviewFilter
                    height: filterRow.controlHeight
                    text: qsTr("待复核")
                    checked: true
                    removable: true
                    Accessible.name: qsTr("待复核筛选")
                    onRemoved: {
                        page.pendingReviewFilter = false
                        page.scheduleFilter()
                    }
                }

                StyledComboBox {
                    id: categoryBox
                    width: 86
                    model: [qsTr("类型")].concat(App.categoryOptions)
                    onActivated: page.scheduleFilter()
                }

                StyledComboBox {
                    id: jobBox
                    width: 86
                    // Battle jobs only: a roulette is never run as 刻木匠 or 剑术师.
                    model: [{ job_id: null, job_name: qsTr("职业") }].concat(App.battleJobOptions)
                    textRole: "job_name"
                    onActivated: page.scheduleFilter()
                }

                StyledComboBox {
                    id: resultBox
                    width: 86
                    model: [
                        { value: null, label: qsTr("结果") },
                        { value: "COMPLETED", label: qsTr("通关") },
                        { value: "LEFT_OR_ABANDONED", label: qsTr("退出/放弃") },
                        { value: "CANCELLED_BEFORE_ENTRY", label: qsTr("进本前取消") },
                        { value: "DISCONNECTED", label: qsTr("断线") },
                        { value: "INTERRUPTED", label: qsTr("中断") },
                        { value: "UNKNOWN", label: qsTr("未知") }
                    ]
                    textRole: "label"
                    onActivated: page.scheduleFilter()
                }

                StyledComboBox {
                    id: sourceBox
                    width: 86
                    model: [
                        { value: null, label: qsTr("来源") },
                        { value: "AUTO_NETWORK", label: qsTr("自动识别") },
                        { value: "MANUAL", label: qsTr("手动") },
                        { value: "IMPORT", label: qsTr("导入") }
                    ]
                    textRole: "label"
                    onActivated: page.scheduleFilter()
                }

                Chip {
                    id: correctedOnly
                    height: filterRow.controlHeight
                    text: qsTr("已修正")
                    onToggled: page.scheduleFilter()
                }

                Chip {
                    id: withReflection
                    height: filterRow.controlHeight
                    text: qsTr("有笔记")
                    onToggled: page.scheduleFilter()
                }

                Chip {
                    id: includeDeleted
                    height: filterRow.controlHeight
                    text: qsTr("含已删除")
                    onToggled: page.scheduleFilter()
                }

                // Fixed 40 px: the Flow sums its children into restWidth and the
                // search field takes whatever is left.
                AppButton {
                    variant: "ghost"
                    width: 40
                    height: filterRow.controlHeight
                    text: qsTr("清除")
                    onClicked: page.resetFilter()
                }
            }

            Rectangle {
                Layout.fillWidth: true
                Layout.preferredHeight: 1
                color: Theme.border
            }

            // ---------------------------------------------------- 表头 --
            Rectangle {
                Layout.fillWidth: true
                Layout.preferredHeight: 30
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
                            // workbench `.table th`: t6, regular, text-3.
                            color: Theme.eorzea
                                   ? Theme.gold
                                   : (App.runs.sortField === modelData.key && modelData.key.length > 0
                                      ? Theme.accent : Theme.textMuted)
                            font.pixelSize: Theme.fs(11)
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
                        Layout.preferredHeight: 40
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
                            onTapped: {
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

                            Text {
                                Layout.preferredWidth: page.dateColumnWidth
                                text: Fmt.localDate(run.matched_at_utc)
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(11)
                                font.weight: Theme.figureWeight(true)
                                font.family: Theme.figureFamily
                                font.features: ({ "tnum": 1 })
                            }
                            Text {
                                Layout.preferredWidth: page.timeColumnWidth
                                text: Fmt.localTime(run.entered_at_utc)
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(11)
                                font.family: Theme.figureFamily
                                font.weight: Theme.figureWeight(false)
                                font.features: ({ "tnum": 1 })
                            }
                            Text {
                                Layout.preferredWidth: page.timeColumnWidth
                                text: Fmt.localTime(run.ended_at_utc)
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(11)
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
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(11)
                                        font.bold: true
                                        elide: Text.ElideRight
                                    }
                                    Text {
                                        Layout.fillWidth: true
                                        text: run.duty_expansion
                                              ? qsTr("%1 · %2级").arg(run.duty_expansion)
                                                                  .arg(run.duty_level || 0)
                                              : Fmt.dash()
                                        color: Theme.textSecondary
                                        font.pixelSize: Theme.fs(10)
                                        font.family: Theme.figureFamily
                                        font.weight: Theme.figureWeight(false)
                                        font.features: ({ "tnum": 1 })
                                        elide: Text.ElideRight
                                    }
                                }
                            }

                            Text {
                                Layout.preferredWidth: 76
                                text: run.duty_category || qsTr("未识别")
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(11)
                                elide: Text.ElideRight
                            }

                            RowLayout {
                                // A nested layout fills by default; only 副本
                                // stretches, as in the header row.
                                Layout.fillWidth: false
                                Layout.preferredWidth: 112
                                Layout.fillHeight: true
                                spacing: 7

                                // No room for the role glyph in 112 px, so the
                                // role only shows up on hover.
                                HoverHandler { id: jobHover }
                                ToolTip.visible: jobHover.hovered
                                ToolTip.delay: 400
                                ToolTip.text: (run.job_name || qsTr("未知"))
                                              + " · " + Jobs.roleGroup(run.job_id)

                                JobIcon { jobId: run.job_id; size: 22 }
                                Text {
                                    Layout.fillWidth: true
                                    text: run.job_name || qsTr("未知")
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(11)
                                    elide: Text.ElideRight
                                }
                            }

                            Item {
                                Layout.preferredWidth: 92
                                Layout.preferredHeight: 28
                                Tag {
                                    anchors.left: parent.left
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: Fmt.runResultLabel(run)
                                    variant: Fmt.runInProgress(run)
                                             ? "outline" : Fmt.resultTagVariant(run.result || "UNKNOWN")
                                }
                            }

                            Text {
                                Layout.preferredWidth: 62
                                text: Fmt.duration(run.duration_ms)
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(11)
                                font.family: Theme.figureFamily
                                font.weight: Theme.figureWeight(false)
                                font.features: ({ "tnum": 1 })
                            }
                            Text {
                                Layout.preferredWidth: 60
                                text: Fmt.sourceLabel(run.source || "AUTO_NETWORK")
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(11)
                                elide: Text.ElideRight
                            }
                            Text {
                                Layout.preferredWidth: 74
                                text: [run.pending_review ? qsTr("待复核") : "",
                                       Number(run.revision || 1) > 1 ? qsTr("已修正") : "",
                                       run.soft_deleted ? qsTr("已删除") : "",
                                       run.manually_created ? qsTr("手动创建") : "",
                                       run.reflection ? qsTr("有笔记") : ""]
                                      .filter(function(part) { return part.length > 0 })
                                      .join(" · ")
                                color: Theme.orangeText
                                font.pixelSize: Theme.fs(10)
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
                Layout.fillWidth: true
                Layout.topMargin: 24
                Layout.bottomMargin: 24
                visible: App.runs.total === 0
                text: qsTr("没有符合筛选条件的记录。")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(13)
            }

            RowLayout {
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
    }
}
