pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

ColumnLayout {
    id: page
    objectName: "reflectionsPage"
    property var controller: typeof Runtime.reflections !== "undefined" ? Runtime.reflections : null
    property real reservedBottom: 0
    readonly property var runs: controller ? controller.runs : null
    signal reflectRequested(var run)
    signal shareRequested(var run)
    signal shareBatchRequested(var runs)
    signal openRunRequested(var run)
    spacing: 16

    function applyFilter() {
        if (!controller) return
        // Keep note words as typed and add the recorded names the text names.
        const text = search.text.trim()
        const filter = { text: text }
        const names = typeof Runtime.names !== "undefined" && Runtime.names
                      ? Runtime.names.recordedSearchNames(text) : []
        if (names.length > 0)
            filter.name_exact = names
        if (job.currentIndex > 0)
            filter.job_id = [job.model[job.currentIndex].job_id]
        if (category.currentIndex > 0)
            filter.duty_category = [category.model[category.currentIndex].value]
        controller.setFilter(filter)
    }
    function dateLabel(run) {
        if (run.matched_at_utc)
            return Runtime.fmt.localDateTime(run.matched_at_utc)
        const source = run.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站记录时间：%1").arg(Runtime.fmt.localDateTime(source.source_recorded_at_utc))
        if (source.source_recorded_at)
            return qsTr("原站记录时间：%1").arg(source.source_recorded_at)
        return qsTr("游玩时间未知")
    }
    function syncFilters() {
        if (!controller) return
        search.text = controller.filter.text || ""
        const jobs = controller.filter.job_id || []
        job.currentIndex = 0
        for (let i = 1; i < job.model.length; ++i)
            if (jobs.indexOf(job.model[i].job_id) >= 0) job.currentIndex = i
        const categories = controller.filter.duty_category || []
        category.currentIndex = 0
        for (let j = 1; j < category.model.length; ++j)
            if (categories.indexOf(category.model[j].value) >= 0) category.currentIndex = j
    }
    Connections {
        target: page.controller
        function onFilterChanged() { page.syncFilters() }
        function onFilterChangeNeedsConfirmation() { debounce.stop(); filterConfirmation.open() }
    }

    PageHeader {
        title: qsTr("全部心得")
        subtitle: !page.runs ? qsTr("暂时无法读取心得")
                  : page.runs.loadError.length > 0 ? qsTr("读取失败")
                  : page.runs.loading ? qsTr("正在读取…")
                  : qsTr("%1 条心得 · 第 %2 / %3 页").arg(page.runs.total).arg(page.runs.page).arg(page.runs.pageCount)
        AppButton { text: qsTr("刷新"); enabled: !!page.runs && !page.runs.loading; onClicked: page.controller.reload() }
    }

    Flow {
        id: toolbar
        objectName: "reflectionToolbar"
        readonly property real fixedWidth: job.width + category.width + clearFilter.implicitWidth
            + selectPage.implicitWidth + clearSelection.implicitWidth + shareSelected.implicitWidth + spacing * 6
        readonly property real flexibleWidth: width - fixedWidth
        Layout.fillWidth: true
        spacing: 8
        SelectionCheckBox {
            id: selectPage
            objectName: "selectCurrentReflectionPage"
            height: search.height
            Accessible.name: qsTr("全选当前页心得")
            checked: !!page.controller && page.controller.currentPageSelected
            enabled: !!page.runs && !page.runs.loading && page.runs.loadError.length === 0 && page.runs.total > 0
            onToggled: page.controller.setPageSelected(checked)
            ToolTip.visible: hovered
            ToolTip.text: qsTr("全选当前页心得")
        }
        StyledTextField {
            id: search
            objectName: "reflectionSearchField"
            width: Math.min(toolbar.width, Math.max(150, Math.min(300, toolbar.flexibleWidth)))
            placeholderText: qsTr("搜索副本 / 职业 / 记录备注")
            onTextEdited: debounce.restart()
            onAccepted: { debounce.stop(); page.applyFilter() }
        }
        StyledComboBox {
            id: job
            width: 120
            // The label is the job in the interface language; the filter sends job_id.
            model: [{ job_id: null, job_name: qsTr("全部职业"), label: qsTr("全部职业") }]
                   .concat(Theme.labelledJobs(typeof Runtime.app !== "undefined" ? Runtime.app.battleJobOptions : []))
            textRole: "label"
            onActivated: page.applyFilter()
        }
        StyledComboBox {
            id: category
            width: 110
            // value: the Chinese category RunFilter.duty_category carries; label: as shown.
            model: [{ value: null, label: qsTr("全部类型") }]
                   .concat(Theme.labelledCategories(typeof Runtime.app !== "undefined" ? Runtime.app.categoryOptions : []))
            textRole: "label"
            onActivated: page.applyFilter()
        }
        AppButton {
            id: clearFilter
            text: qsTr("清除筛选")
            iconName: "filter-x"
            onClicked: { debounce.stop(); search.text = ""; job.currentIndex = 0; category.currentIndex = 0; page.applyFilter() }
        }
        AppButton {
            id: clearSelection
            objectName: "clearReflectionSelection"
            text: qsTr("清空选择")
            enabled: !!page.controller && page.controller.selectedCount > 0
            onClicked: page.controller.clearSelection()
        }
        AppButton {
            id: shareSelected
            objectName: "shareSelectedReflections"
            text: qsTr("生成分享图片")
            iconName: "image"
            variant: "primary"
            enabled: !!page.controller && page.controller.selectedCount > 0
            onClicked: page.shareBatchRequested(page.controller.selectedRuns)
        }
    }
    Timer { id: debounce; interval: 300; onTriggered: page.applyFilter() }

    QtObject {
        readonly property Dialog filterPrompt: Dialog {
            id: filterConfirmation
            objectName: "reflectionFilterConfirmation"
            property bool confirmed: false
            modal: true
            anchors.centerIn: Overlay.overlay
            width: Math.min(440, Overlay.overlay ? Overlay.overlay.width - 48 : 440)
            padding: 20
            closePolicy: Popup.CloseOnEscape
            background: DialogFrame {}
            Overlay.modal: Rectangle { color: Theme.modalScrim(filterConfirmation.palette.shadow) }
            onOpened: { confirmed = false; keepFilter.forceActiveFocus() }
            onClosed: if (!confirmed && page.controller) page.controller.cancelFilterChange()
            contentItem: ColumnLayout {
                spacing: 16
                HeadingLabel { text: qsTr("更改筛选将清空选择"); font.pixelSize: Theme.dialogTitleSize(18) }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("当前已选 %1 条心得。清空选择后再应用新筛选？").arg(page.controller ? page.controller.selectedCount : 0)
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(13)
                    wrapMode: Text.Wrap
                }
                RowLayout {
                    Layout.fillWidth: true
                    AppButton { id: keepFilter; objectName: "keepReflectionFilter"; text: qsTr("保留当前筛选"); onClicked: filterConfirmation.close() }
                    Item { Layout.fillWidth: true }
                    AppButton {
                        objectName: "confirmReflectionFilter"
                        text: qsTr("清空并筛选")
                        variant: "primary"
                        onClicked: {
                            filterConfirmation.confirmed = true
                            page.controller.confirmFilterChange()
                            filterConfirmation.close()
                        }
                    }
                }
            }
        }
    }

    Item {
        Layout.fillWidth: true
        Layout.fillHeight: true
        implicitHeight: 180
        ListView {
            id: list
            objectName: "reflectionLibraryList"
            anchors.fill: parent
            clip: true
            spacing: 12
            model: page.runs
            visible: !!page.runs && !page.runs.loading && page.runs.loadError.length === 0
            ScrollBar.vertical: ScrollBar {}
            delegate: InsetBox {
                id: entry
                required property var run
                readonly property var reflection: run.reflection || ({})
                width: list.width - Theme.scrollGutter
                implicitHeight: contents.implicitHeight + 32
                ColumnLayout {
                    id: contents
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.margins: 16
                    spacing: 10
                    RowLayout {
                        Layout.fillWidth: true
                        SelectionCheckBox {
                            objectName: "reflectionSelectionCheckBox"
                            Accessible.name: qsTr("选择心得：%1").arg(Theme.dutyName(entry.run) || qsTr("未知副本"))
                            checked: !!page.controller && page.controller.selectedIds.indexOf(entry.run.run_id) >= 0
                            onToggled: page.controller.toggleSelected(entry.run)
                        }
                        JobIcon { jobId: entry.run.job_id; size: 28 }
                        Text {
                            Layout.fillWidth: true
                            text: Theme.dutyName(entry.run) || qsTr("未知副本")
                            textFormat: Text.PlainText
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(15)
                            font.bold: true
                            wrapMode: Text.Wrap
                        }
                        Tag {
                            text: Theme.moodLabel(entry.reflection.mood)
                            variant: Theme.moodVariant(entry.reflection.mood)
                        }
                    }
                    Text {
                        Layout.fillWidth: true
                        text: (Theme.jobName(entry.run) || qsTr("未知职业")) + " · " + page.dateLabel(entry.run)
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.Wrap
                    }
                    Text {
                        Layout.fillWidth: true
                        visible: !!(entry.run.import_metadata && entry.run.import_metadata.incomplete)
                        text: qsTr("导入记录 · 待补充")
                        color: Theme.orangeText
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.Wrap
                    }
                    Text {
                        objectName: "reflectionLibraryFullText"
                        Layout.fillWidth: true
                        text: entry.reflection.text || ""
                        textFormat: Text.PlainText
                        color: Theme.textPrimary
                        font.family: Qt.fontFamilies().indexOf("Noto Sans SC") >= 0
                                     ? "Noto Sans SC"
                                     : (typeof Runtime.lang !== "undefined" && Runtime.lang
                                        ? Runtime.lang.displayFamily(Runtime.lang.code) : Theme.bodyFamily)
                        font.pixelSize: Theme.fs(14)
                        wrapMode: Text.Wrap
                        lineHeight: 1.5
                    }
                    Flow {
                        Layout.fillWidth: true
                        spacing: 8
                        AppButton { text: qsTr("编辑心得"); onClicked: page.reflectRequested(entry.run) }
                        AppButton { text: qsTr("生成分享图片"); iconName: "image"; onClicked: page.shareRequested(entry.run) }
                        AppButton { text: qsTr("记录详情"); onClicked: page.openRunRequested(entry.run) }
                    }
                }
            }
        }
        ColumnLayout {
            anchors.centerIn: parent
            width: Math.min(parent.width - 24, 440)
            visible: !page.runs || page.runs.loading || page.runs.loadError.length > 0 || page.runs.total === 0
            spacing: 12
            Text {
                Layout.fillWidth: true
                horizontalAlignment: Text.AlignHCenter
                text: !page.runs ? qsTr("暂时无法读取心得")
                      : page.runs.loading ? qsTr("正在读取心得…")
                      : page.runs.loadError.length > 0 ? qsTr("无法读取心得：%1").arg(page.runs.loadError)
                      : Object.keys(page.controller.filter).length > 1 ? qsTr("没有符合筛选条件的心得，请调整或清除筛选。")
                      : qsTr("还没有心得，可以从历史记录中补录。")
                textFormat: Text.PlainText
                wrapMode: Text.Wrap
                color: page.runs && page.runs.loadError.length > 0 ? Theme.red : Theme.textSecondary
                font.pixelSize: Theme.fs(14)
            }
            AppButton {
                Layout.alignment: Qt.AlignHCenter
                visible: !!page.runs && page.runs.loadError.length > 0
                text: qsTr("重试")
                onClicked: page.controller.reload()
            }
        }
    }
    RowLayout {
        Layout.fillWidth: true
        spacing: 8
        AppButton { objectName: "reflectionPreviousPage"; text: qsTr("上一页"); iconName: "chevron-left"; enabled: !!page.runs && !page.runs.loading && page.runs.page > 1; onClicked: page.runs.previousPage() }
        PageNumberInput { objectName: "reflectionPageJump"; paginationModel: page.runs }
        AppButton { objectName: "reflectionNextPage"; text: qsTr("下一页"); iconName: "chevron-right"; enabled: !!page.runs && !page.runs.loading && page.runs.page < page.runs.pageCount; onClicked: page.runs.nextPage() }
        Item { Layout.fillWidth: true }
        StyledComboBox {
            objectName: "reflectionPageSize"
            Layout.preferredWidth: 110
            model: [qsTr("每页 10 条"), qsTr("每页 20 条"), qsTr("每页 50 条")]
            onActivated: if (page.runs) page.runs.pageSize = [10, 20, 50][currentIndex]
        }
    }
    Item { Layout.fillWidth: true; Layout.preferredHeight: Math.max(0, page.reservedBottom) }
}
