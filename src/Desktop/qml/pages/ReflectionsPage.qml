import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

ColumnLayout {
    id: page
    objectName: "reflectionsPage"
    property var controller: typeof Reflections !== "undefined" ? Reflections : null
    property real reservedBottom: 0
    readonly property var runs: controller ? controller.runs : null
    signal reflectRequested(var run)
    signal shareRequested(var run)
    signal shareBatchRequested(var runs)
    signal openRunRequested(var run)
    spacing: 16

    function applyFilter() {
        if (!controller) return
        const filter = { text: search.text }
        if (job.currentIndex > 0)
            filter.job_id = [job.model[job.currentIndex].job_id]
        if (category.currentIndex > 0)
            filter.duty_category = [category.model[category.currentIndex]]
        controller.setFilter(filter)
    }
    function dateLabel(run) {
        if (run.matched_at_utc)
            return Fmt.localDateTime(run.matched_at_utc)
        const source = run.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站记录时间：%1").arg(Fmt.localDateTime(source.source_recorded_at_utc))
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
            if (categories.indexOf(category.model[j]) >= 0) category.currentIndex = j
    }
    Connections {
        target: page.controller
        function onFilterChanged() { page.syncFilters() }
        function onFilterChangeNeedsConfirmation() { debounce.stop(); filterConfirmation.open() }
    }

    PageHeader {
        title: qsTr("全部心得")
        subtitle: !page.runs ? qsTr("心得服务未就绪")
                  : page.runs.loadError.length > 0 ? qsTr("读取失败")
                  : page.runs.loading ? qsTr("正在读取…")
                  : qsTr("%1 条心得 · 第 %2 / %3 页").arg(page.runs.total).arg(page.runs.page).arg(page.runs.pageCount)
        AppButton { text: qsTr("刷新"); enabled: !!page.runs && !page.runs.loading; onClicked: page.controller.reload() }
    }

    Flow {
        Layout.fillWidth: true
        spacing: 8
        StyledTextField {
            id: search
            objectName: "reflectionSearchField"
            width: Math.max(150, Math.min(300, parent.width))
            placeholderText: qsTr("搜索副本 / 职业 / 记录备注")
            onTextEdited: debounce.restart()
            onAccepted: { debounce.stop(); page.applyFilter() }
        }
        StyledComboBox {
            id: job
            width: 150
            model: [{ job_id: null, job_name: qsTr("全部职业") }].concat(typeof App !== "undefined" ? App.battleJobOptions : [])
            textRole: "job_name"
            onActivated: page.applyFilter()
        }
        StyledComboBox {
            id: category
            width: 130
            model: [qsTr("全部类型")].concat(typeof App !== "undefined" ? App.categoryOptions : [])
            onActivated: page.applyFilter()
        }
        AppButton {
            text: qsTr("清除筛选")
            iconName: "filter-x"
            onClicked: { debounce.stop(); search.text = ""; job.currentIndex = 0; category.currentIndex = 0; page.applyFilter() }
        }
    }
    Timer { id: debounce; interval: 300; onTriggered: page.applyFilter() }

    RowLayout {
        Layout.fillWidth: true
        spacing: 8
        SelectionCheckBox {
            objectName: "selectCurrentReflectionPage"
            text: qsTr("全选当前页")
            checked: !!page.controller && page.controller.currentPageSelected
            enabled: !!page.runs && !page.runs.loading && page.runs.loadError.length === 0 && page.runs.total > 0
            onToggled: page.controller.setPageSelected(checked)
        }
        Text {
            Layout.fillWidth: true
            text: page.controller && page.controller.selectedCount > 0
                ? qsTr("已选 %1 条（翻页保留）").arg(page.controller.selectedCount) : qsTr("勾选心得可批量生成图片")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        AppButton {
            objectName: "clearReflectionSelection"
            text: qsTr("清空选择")
            enabled: !!page.controller && page.controller.selectedCount > 0
            onClicked: page.controller.clearSelection()
        }
        AppButton {
            objectName: "shareSelectedReflections"
            text: qsTr("生成选中图片")
            iconName: "file-down"
            variant: "primary"
            enabled: !!page.controller && page.controller.selectedCount > 0
            onClicked: page.shareBatchRequested(page.controller.selectedRuns)
        }
    }

    Dialog {
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
                            Accessible.name: qsTr("选择心得：%1").arg(entry.run.duty_name || qsTr("未知副本"))
                            checked: !!page.controller && page.controller.selectedIds.indexOf(entry.run.run_id) >= 0
                            onToggled: page.controller.toggleSelected(entry.run)
                        }
                        JobIcon { jobId: entry.run.job_id; size: 28 }
                        Text {
                            Layout.fillWidth: true
                            text: entry.run.duty_name || qsTr("未知副本")
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
                        text: (entry.run.job_name || qsTr("未知职业")) + " · " + page.dateLabel(entry.run)
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
                text: !page.runs ? qsTr("心得服务未就绪")
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
        Text { text: page.runs ? qsTr("第 %1 / %2 页").arg(page.runs.page).arg(page.runs.pageCount) : ""; color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
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
