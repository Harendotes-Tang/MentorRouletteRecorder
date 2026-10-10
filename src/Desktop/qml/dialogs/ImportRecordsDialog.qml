import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
import "../components/Lucide.js" as Lucide

Dialog {
    id: dialog
    objectName: "importRecordsDialog"
    property var controller: typeof ImportRecords !== "undefined" ? ImportRecords : null
    readonly property bool working: controller ? controller.busy : false
    readonly property bool inputLocked: working || (controller && controller.pendingCommitConfirmation)
    readonly property var candidate: controller ? controller.currentCandidate : ({})
    readonly property var evidence: controller ? controller.currentEvidence : ({})
    readonly property string iconEvidenceText: evidence.icon_evidence_type === "classifier"
        ? qsTr("图标识别得分 %1/100").arg(Math.round(Number(evidence.icon_confidence || 0) * 100))
        : qsTr("图形相似度 %1%").arg(Math.round(Number(evidence.icon_confidence || 0) * 100))
    readonly property var rowData: controller && controller.currentRow >= 0
                                   ? controller.rows[controller.currentRow] : ({})
    readonly property int jobChoiceIndex: jobOptionIndex()
    readonly property bool unsupportedSourceJob: jobChoiceIndex < 0
    property bool showMapping: false

    component PlainToolTip: ToolTip {
        id: tip
        contentItem: Text {
            text: tip.text
            textFormat: Text.PlainText
            font: tip.font
            wrapMode: Text.Wrap
            color: tip.palette.toolTipText
        }
    }
    property bool showGameFacts: false
    property bool wholeImage: false
    property bool customTimeZoneSelected: false
    readonly property var timeZoneOptions: [
        { text: qsTr("北京时间（中国）"), value: "+08:00" },
        { text: qsTr("日本时间"), value: "+09:00" },
        { text: qsTr("世界标准时间（UTC）"), value: "+00:00" },
        { text: qsTr("其他地区（自定义）"), value: "" }
    ]

    component ImportCheckBox: CheckBox {
        id: control
        font.pixelSize: Theme.fs(12)
        indicator: Rectangle {
            implicitWidth: 22
            implicitHeight: 22
            x: control.text ? (control.mirrored ? control.width - width - control.rightPadding : control.leftPadding)
                            : control.leftPadding + (control.availableWidth - width) / 2
            y: control.topPadding + (control.availableHeight - height) / 2
            radius: Theme.radiusS
            color: control.down ? Theme.fill : Theme.insetBackground
            border.width: control.visualFocus ? 2 : 1
            border.color: control.visualFocus || control.checked ? Theme.accent : Theme.textSecondary
            opacity: control.enabled ? 1 : 0.6
            Image {
                anchors.centerIn: parent
                width: 18
                height: 14
                visible: control.checked
                // Existing Lucide clipboard-check.svg supplies this tick path;
                // the bundled ISC licence covers the cropped control glyph.
                source: "data:image/svg+xml;utf8," + encodeURIComponent(
                    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="8 11 8 6" fill="none" stroke="'
                    + String(control.enabled ? Theme.textPrimary : Theme.textMuted)
                    + '" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m9 14 2 2 4-4"/></svg>')
            }
        }
        contentItem: Text {
            textFormat: Text.PlainText
            text: control.text
            font: control.font
            color: control.enabled ? Theme.textPrimary : Theme.textMuted
            leftPadding: control.indicator && !control.mirrored ? control.indicator.width + control.spacing : 0
            rightPadding: control.indicator && control.mirrored ? control.indicator.width + control.spacing : 0
            verticalAlignment: Text.AlignVCenter
        }
    }

    readonly property bool hasRows: controller && controller.rows.length > 0
    readonly property bool canMapColumns: controller && ["CSV", "XLS", "XLSX", "PASTE"].indexOf(controller.sourceKind) >= 0
    property string activeFilter: "all"
    property string navigationHint: ""
    property bool showRecognitionHints: false
    readonly property var recognitionHints: uniqueHints()
    readonly property var resultOptions: [
        {text: qsTr("未知 / 待补充"), value: "UNKNOWN"},
        {text: qsTr("通关"), value: "COMPLETED"},
        {text: qsTr("离开 / 放弃"), value: "LEFT_OR_ABANDONED"},
        {text: qsTr("进本前取消"), value: "CANCELLED_BEFORE_ENTRY"},
        {text: qsTr("断线"), value: "DISCONNECTED"},
        {text: qsTr("中断"), value: "INTERRUPTED"}
    ]
    readonly property var moodOptions: [
        {text: qsTr("未记录心情"), value: "unknown"},
        {text: qsTr("顺利"), value: "good"},
        {text: qsTr("一般"), value: "ok"},
        {text: qsTr("糟心"), value: "bad"}
    ]
    readonly property var visibleRows: projectRows(activeFilter)
    readonly property var listItems: groupedRows()
    readonly property int attentionCount: projectRows("attention").length
    readonly property int unknownJobCount: projectRows("unknown").length
    readonly property int unselectedCount: projectRows("unselected").length
    readonly property bool allImportableSelected: allRowsSelected()

    component FieldLabel: Text {
        textFormat: Text.PlainText
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        Layout.preferredWidth: 82
        Layout.maximumWidth: 82
        wrapMode: Text.WordWrap
    }
    component SectionHeading: Text {
        textFormat: Text.PlainText
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(13)
        font.bold: true
    }
    component FilterButton: Button {
        id: filterControl
        property string filterKey
        property int count: 0
        property string label: ""
        focusPolicy: Qt.TabFocus
        hoverEnabled: true
        implicitHeight: 27
        implicitWidth: filterText.implicitWidth + 14
        padding: 0
        contentItem: Text {
            textFormat: Text.PlainText
            id: filterText
            text: filterControl.label + " " + filterControl.count
            font.pixelSize: Theme.fs(11)
            color: dialog.activeFilter === filterControl.filterKey ? Theme.accent : Theme.textSecondary
            horizontalAlignment: Text.AlignHCenter
            verticalAlignment: Text.AlignVCenter
        }
        background: Rectangle {
            radius: Theme.radiusS
            color: dialog.activeFilter === filterControl.filterKey ? Theme.accentMuted
                   : filterControl.hovered ? Theme.fill : Theme.surface
            border.color: dialog.activeFilter === filterControl.filterKey || filterControl.visualFocus ? Theme.accent : Theme.border
            border.width: filterControl.visualFocus ? 2 : 1
        }
        onClicked: dialog.chooseFilter(filterKey)
    }

    function rowNeedsAttention(row) {
        const e = row.evidence || {}
        return e.duty_candidate_pending === true || e.job_candidate_pending === true
                || (row.errors || []).length > 0 || row.status === "conflict"
                || row.status === "possible_duplicate" || row.status === "invalid"
                || row.status === "error"
    }
    function uniqueHints() {
        const messages = (rowData.warnings || []).concat(evidence.warnings || [])
        let hints = []
        for (let message of messages)
            if (hints.indexOf(message) < 0) hints.push(message)
        return hints
    }
    function projectRows(filter) {
        const rows = controller ? controller.rows : []
        let result = []
        for (let i = 0; i < rows.length; ++i) {
            const row = rows[i]
            const c = row.candidate || {}
            if (filter === "attention" && !rowNeedsAttention(row)) continue
            if (filter === "unknown" && c.job_id !== null && c.job_id !== undefined) continue
            if (filter === "unselected" && row.selected === true) continue
            result.push({originalIndex: i, row: row})
        }
        return result
    }
    function sourceName(path) {
        return String(path || "").replace(/\\/g, "/").split("/").pop()
    }
    function groupedRows() {
        let result = []
        let previousSource = null
        let sourceNumbers = {}
        let sourceCount = 0
        const all = controller ? controller.rows : []
        for (let i = 0; i < all.length; ++i) {
            const source = (all[i].evidence || {}).source_image || ""
            if (source && sourceNumbers[source] === undefined) sourceNumbers[source] = ++sourceCount
        }
        for (let item of visibleRows) {
            const source = (item.row.evidence || {}).source_image || ""
            if (source !== previousSource) {
                let count = 0
                for (let entry of visibleRows)
                    if (((entry.row.evidence || {}).source_image || "") === source) ++count
                result.push({originalIndex: -1, source: source,
                    label: source ? qsTr("截图 %1 · %2").arg(sourceNumbers[source]).arg(sourceName(source))
                                  : (controller ? controller.sourceLabel : qsTr("导入记录")), count: count})
                previousSource = source
            }
            result.push(item)
        }
        return result
    }
    function allRowsSelected() {
        const rows = controller ? controller.rows : []
        let importable = 0
        for (let row of rows) {
            if (row.can_import === true) {
                ++importable
                if (row.selected !== true) return false
            }
        }
        return importable > 0
    }
    function chooseFilter(filter) {
        flushEditors()
        activeFilter = filter
        navigationHint = ""
        showRecognitionHints = false
        const rows = projectRows(filter)
        let found = false
        for (let entry of rows)
            if (entry.originalIndex === controller.currentRow) found = true
        if (!found && rows.length) controller.currentRow = rows[0].originalIndex
        candidateList.forceActiveFocus(Qt.MouseFocusReason)
    }
    function visiblePosition() {
        for (let i = 0; i < visibleRows.length; ++i)
            if (visibleRows[i].originalIndex === controller.currentRow) return i
        return -1
    }
    function navigateRow(delta) {
        const rows = visibleRows.slice()
        let position = visiblePosition()
        const next = position < 0 ? 0 : position + delta
        if (next >= 0 && next < rows.length) selectRow(rows[next].originalIndex)
        candidateList.forceActiveFocus(Qt.MouseFocusReason)
    }
    function checkAndNext() {
        // Snapshot the filtered original indices before selecting hides a row.
        const rows = visibleRows.slice()
        const position = visiblePosition()
        const row = controller.currentRow
        flushEditors()
        if (!controller.previewValid || row < 0 || !controller.rows[row].can_import) {
            navigationHint = qsTr("内容有修改，请先重新校验再勾选。")
            return
        }
        controller.setRowSelected(row, true)
        navigationHint = ""
        if (position >= 0 && position + 1 < rows.length)
            controller.currentRow = rows[position + 1].originalIndex
        else if (visibleRows.length && activeFilter === "unselected")
            controller.currentRow = visibleRows[0].originalIndex
        candidateList.forceActiveFocus(Qt.MouseFocusReason)
    }
    function toggleCurrentSelection() {
        const row = controller.currentRow
        flushEditors()
        if (controller.previewValid && row >= 0 && controller.rows[row].can_import)
            controller.setRowSelected(row, controller.rows[row].selected !== true)
        candidateList.forceActiveFocus(Qt.MouseFocusReason)
    }
    function editorOwnsPaste() {
        const window = importContent.Window.window
        const item = window ? window.activeFocusItem : null
        return !!item && (item.selectByMouse !== undefined || item.preeditText !== undefined
                         || item.editable !== undefined)
    }
    function openBatch() {
        flushEditors()
        if (!controller.previewValid || controller.selectedCount === 0) return
        batchJob.currentIndex = 0
        batchResult.currentIndex = 0
        batchMood.currentIndex = 0
        batchDialog.open()
    }

    modal: true
    width: Math.min(hasRows ? 1240 : 760, parent ? parent.width - 32 : 1240)
    x: parent ? Math.round((parent.width - width) / 2) : 0
    y: parent ? Math.round((parent.height - height) / 2) : 0
    height: Math.min(hasRows ? 800 : 500, parent ? parent.height - 40 : 800)
    padding: 18
    closePolicy: controller && (controller.committing || controller.pendingCommitConfirmation)
                 ? Popup.NoAutoClose : Popup.CloseOnEscape
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }

    function openDialog() {
        if (controller && !controller.committing && !controller.pendingCommitConfirmation)
            controller.reset()
        activeFilter = "all"
        navigationHint = ""
        showRecognitionHints = false
        showMapping = false
        wholeImage = false
        customTimeZoneSelected = false
        mapDuty.text = ""
        mapJob.text = ""
        mapDate.text = ""
        mapReflection.text = ""
        pastedText.text = ""
        open()
    }
    function flushReflection() {
        if (reflectionEditor.editingRow >= 0 && reflectionEditor.text !== reflectionEditor.originalText) {
            const row = reflectionEditor.editingRow
            const text = reflectionEditor.text
            reflectionEditor.originalText = text
            controller.updateCandidate(row, {reflection_text: text})
        }
    }
    function selectRow(row) {
        flushEditors()
        controller.currentRow = row
    }
    function flushEditors() {
        // AppButton accepts Tab focus, so clicking an action may leave a text
        // field focused. Blur it first to commit onEditingFinished values.
        importContent.forceActiveFocus(Qt.MouseFocusReason)
        flushReflection()
    }
    function edit(key, value) {
        if (!controller || controller.currentRow < 0)
            return
        flushReflection()
        let changes = {}
        changes[key] = value
        controller.updateCandidate(controller.currentRow, changes)
    }
    function value(key) { return candidate && candidate[key] !== null && candidate[key] !== undefined ? String(candidate[key]) : "" }
    function optionIndex(options, field, current) {
        for (let i = 0; i < options.length; ++i)
            if (options[i][field] === current)
                return i
        return 0
    }
    function jobOptionIndex() {
        if (!controller || candidate.job_id === null || candidate.job_id === undefined)
            return 0
        const options = controller.jobChoices
        for (let i = 0; i < options.length; ++i)
            if (options[i].job_id === candidate.job_id)
                return i
        return -1
    }
    function timeZoneChoiceIndex() {
        if (!customTimeZoneSelected) {
            const zone = controller ? controller.timeZone : "+08:00"
            for (let i = 0; i < timeZoneOptions.length - 1; ++i)
                if (timeZoneOptions[i].value === zone)
                    return i
        }
        return timeZoneOptions.length - 1
    }
    function statusLabel(row) {
        if (!controller.previewValid)
            return qsTr("需重新校验")
        switch (row.status) {
        case "new": return row.incomplete ? qsTr("待补充事实") : qsTr("可导入")
        case "duplicate": return qsTr("重复，跳过")
        case "conflict": return qsTr("冲突，保留本地")
        case "possible_duplicate": return qsTr("可能重复，请核对")
        default: return qsTr("需要修正")
        }
    }
    function applyMapping() {
        let mapping = {}
        const fields = [ [mapDuty.text, "duty_name"], [mapJob.text, "job_name"],
                         [mapDate.text, "source_recorded_at"], [mapReflection.text, "reflection_text"] ]
        for (let field of fields)
            if (field[0].trim().length > 0)
                mapping[field[0].trim()] = field[1]
        controller.columnMapping = mapping
        controller.revalidate()
    }

    onClosed: if (controller && !controller.committing && !controller.pendingCommitConfirmation) controller.cancel()
    // Opening and mouse row changes blur editors without highlighting a toolbar
    // action. Tab still enters the first button and retains its keyboard cue.
    onOpened: importContent.forceActiveFocus(Qt.PopupFocusReason)


    contentItem: ColumnLayout {
        id: importContent
        objectName: "importContent"
        KeyNavigation.tab: chooseInput
        spacing: 12
        ColumnLayout {
            Layout.fillWidth: true; spacing: 6
            RowLayout {
                Layout.fillWidth: true
                Text { textFormat: Text.PlainText; text: qsTr("导入本人记录"); color: Theme.textPrimary; font.pixelSize: Theme.fs(18); font.bold: true }
                Item { Layout.fillWidth: true }
                AppButton { objectName: "importTemplatesOpen"; text: qsTr("模板与填写说明"); iconName: "book-open-text"; variant: "ghost"; compact: true; enabled: !dialog.inputLocked; onClicked: templatesDialog.open() }
                AppButton { text: ""; iconName: "x"; variant: "ghost"; compact: true; Layout.preferredWidth: 26; focusPolicy: Qt.NoFocus; enabled: !dialog.controller || (!dialog.controller.committing && !dialog.controller.pendingCommitConfirmation); Accessible.name: qsTr("关闭导入记录"); onClicked: { dialog.controller.cancel(); dialog.close() } }
            }
            Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: dialog.hasRows ? qsTr("对照原图逐条核对后再导入。缺失结果按通关预填；没有实际游戏时间的记录会保留为待补充历史。") : qsTr("把以前记在别处的导随记录搬进来。导入前可以逐条核对，确认后才写入历史记录。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
        }
        Rectangle {
            Layout.fillWidth: true; Layout.preferredHeight: timeZonePreset.currentIndex === dialog.timeZoneOptions.length - 1 ? 78 : 48
            visible: dialog.hasRows; color: Theme.contentBackground; radius: Theme.radiusS
            RowLayout {
                anchors.fill: parent; anchors.margins: 9; spacing: 12
                Text {
                    textFormat: Text.PlainText
                    Layout.fillWidth: true; Layout.minimumWidth: 70
                    text: dialog.controller && dialog.controller.sourceKind === "SCREENSHOT" ? qsTr("%1 张截图 · %2 条记录").arg(dialog.controller.sourceImageCount).arg(dialog.controller.rows.length) : dialog.controller ? dialog.controller.sourceLabel : ""
                    elide: Text.ElideMiddle; color: Theme.textPrimary; font.pixelSize: Theme.fs(12)
                    PlainToolTip { visible: toolbarSourceHover.hovered; text: dialog.controller ? dialog.controller.sourceLabel : "" }
                    HoverHandler { id: toolbarSourceHover }
                }
                Item { id: reviewActions; Layout.preferredWidth: sourceActions.implicitWidth; Layout.preferredHeight: 30 }
                Item { id: reviewZone; Layout.preferredWidth: sourceZone.implicitWidth; Layout.fillHeight: true }
            }
        }
        Text { textFormat: Text.PlainText; Layout.fillWidth: true; visible: dialog.working || (dialog.hasRows && !dialog.controller.previewValid); text: dialog.controller ? dialog.controller.statusText : ""; color: Theme.textSecondary; font.pixelSize: Theme.fs(12); maximumLineCount: 2; elide: Text.ElideRight; wrapMode: Text.WordWrap }
        Text {
            textFormat: Text.PlainText
            id: importErrorLabel
            objectName: "importError"; Layout.fillWidth: true; visible: text.length > 0
            text: dialog.controller ? dialog.controller.errorText : qsTr("导入控制器不可用")
            color: Theme.red; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap; maximumLineCount: 2; elide: Text.ElideRight
            PlainToolTip { objectName: "importErrorToolTip"; visible: importErrorHover.hovered; text: importErrorLabel.text }
            HoverHandler { id: importErrorHover }
        }
        Item {
            Layout.fillWidth: true; Layout.fillHeight: true; Layout.minimumHeight: 150
            DropArea {
                anchors.fill: parent; enabled: !dialog.inputLocked
                onDropped: function(drop) { dialog.flushEditors(); if (drop.hasUrls) { dialog.controller.importFiles(drop.urls); drop.acceptProposedAction() } else if (drop.hasText) { dialog.controller.importText(drop.text); drop.acceptProposedAction() } }
            }
            ColumnLayout {
                anchors.fill: parent; visible: !dialog.hasRows; spacing: 14
                Rectangle {
                    id: dropSurface
                    Layout.fillWidth: true; Layout.fillHeight: true; Layout.minimumHeight: 190
                    color: Theme.insetBackground; radius: Theme.radiusM
                    border.color: Theme.neutral300; border.width: 1
                    ColumnLayout {
                        anchors.centerIn: parent; width: parent.width - 32; spacing: 12
                        Image { Layout.alignment: Qt.AlignHCenter; width: 30; height: 30; source: Lucide.source("folder-open", Theme.accent) }
                        Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("把截图或表格文件拖到这里"); color: Theme.textPrimary; font.pixelSize: Theme.fs(16); font.bold: true; horizontalAlignment: Text.AlignHCenter }
                        Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("截图可以一次选多张；剪贴板里的截图或表格也能直接粘贴。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap; horizontalAlignment: Text.AlignHCenter }
                        Item { id: startActions; Layout.alignment: Qt.AlignHCenter; Layout.preferredWidth: sourceActions.implicitWidth; Layout.preferredHeight: 32 }
                    }
                }
                RowLayout {
                    Layout.fillWidth: true; spacing: 24
                    ColumnLayout {
                        Layout.fillWidth: true; Layout.preferredWidth: 1; spacing: 4
                        SectionHeading { text: qsTr("截图") }
                        Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("同批最多 20 张。只在本机识别，原图和候选会一起保留供你核对。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
                    }
                    ColumnLayout {
                        Layout.fillWidth: true; Layout.preferredWidth: 1; spacing: 4
                        SectionHeading { text: qsTr("表格与备份") }
                        Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("支持 CSV、Excel（.xls / .xlsx）、JSON 和数据库备份。可先保存模板填写；表头不一致时可以指定列映射。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
                    }
                }
                AppButton { objectName: "importManualPasteOpen"; text: qsTr("手动粘贴表格文字"); variant: "ghost"; compact: true; Layout.alignment: Qt.AlignLeft; enabled: !dialog.inputLocked; onClicked: pasteDialog.open() }
            }
            RowLayout {
                id: reviewColumns
                anchors.fill: parent; visible: dialog.hasRows; spacing: 12
                readonly property real leftWidth: Math.max(224, Math.min(350, width * 0.29))
                readonly property real remainder: width - leftWidth - 24
                readonly property real centerWidth: Math.max(270, Math.min(remainder * 0.51, remainder - 310))
                ColumnLayout {
                    Layout.preferredWidth: reviewColumns.leftWidth; Layout.minimumWidth: 224; Layout.maximumWidth: reviewColumns.leftWidth; Layout.fillHeight: true; spacing: 8
                    Flow {
                        Layout.fillWidth: true; Layout.preferredHeight: childrenRect.height; spacing: 5
                        FilterButton { objectName: "importFilterAll"; filterKey: "all"; label: qsTr("全部"); count: dialog.controller ? dialog.controller.rows.length : 0; enabled: !dialog.inputLocked }
                        FilterButton { objectName: "importFilterAttention"; filterKey: "attention"; label: qsTr("需处理"); count: dialog.attentionCount; enabled: !dialog.inputLocked }
                        FilterButton { objectName: "importFilterUnknownJob"; filterKey: "unknown"; label: qsTr("职业未知"); count: dialog.unknownJobCount; enabled: !dialog.inputLocked }
                        FilterButton { objectName: "importFilterUnselected"; filterKey: "unselected"; label: qsTr("未勾选"); count: dialog.unselectedCount; enabled: !dialog.inputLocked }
                    }
                    RowLayout {
                        Layout.fillWidth: true; spacing: 5
                        ImportCheckBox { objectName: "importSelectAll"; text: qsTr("全选可导入"); checked: dialog.allImportableSelected; enabled: !dialog.inputLocked && dialog.controller && dialog.controller.previewValid; onClicked: { dialog.flushEditors(); dialog.controller.setAllImportableSelected(checked) } }
                        Item { Layout.fillWidth: true }
                        AppButton { objectName: "importBatchOpen"; text: qsTr("批量设置"); compact: true; Layout.preferredWidth: 72; enabled: !dialog.inputLocked && dialog.controller && dialog.controller.previewValid && dialog.controller.selectedCount > 0; onClicked: dialog.openBatch() }
                    }
                    ListView {
                        id: candidateList
                        objectName: "importCandidates"
                        Layout.fillWidth: true; Layout.fillHeight: true; clip: true; spacing: 3; model: dialog.listItems; activeFocusOnTab: true
                        currentIndex: { if (!dialog.controller) return -1; for (let i = 0; i < dialog.listItems.length; ++i) if (dialog.listItems[i].originalIndex === dialog.controller.currentRow) return i; return -1 }
                        highlightFollowsCurrentItem: false
                        onCurrentIndexChanged: Qt.callLater(function() {
                            const row = candidateList.currentIndex
                            if (row < 0) return
                            const precedingGroup = row > 0 && dialog.listItems[row - 1].originalIndex < 0
                            candidateList.positionViewAtIndex(precedingGroup ? row - 1 : row, ListView.Contain)
                        })
                        ScrollBar.vertical: ScrollBar {}
                        Keys.onPressed: function(event) {
                            if (!activeFocus || dialog.inputLocked) return
                            if (event.key === Qt.Key_Up) { dialog.navigateRow(-1); event.accepted = true }
                            else if (event.key === Qt.Key_Down) { dialog.navigateRow(1); event.accepted = true }
                            else if (event.key === Qt.Key_Space) { dialog.toggleCurrentSelection(); event.accepted = true }
                            else if (event.key === Qt.Key_Return || event.key === Qt.Key_Enter) { dialog.checkAndNext(); event.accepted = true }
                        }
                        delegate: Item {
                            required property var modelData
                            required property int index
                            readonly property bool groupHeader: modelData.originalIndex < 0
                            readonly property var importRow: modelData.row || ({})
                            readonly property var importCandidate: importRow.candidate || ({})
                            width: candidateList.width; height: groupHeader ? 27 : 67
                            Text {
                                textFormat: Text.PlainText
                                anchors.fill: parent; anchors.leftMargin: 2; anchors.rightMargin: 3; visible: parent.groupHeader
                                text: (modelData.label || "") + qsTr("  %1 条").arg(modelData.count || 0)
                                color: Theme.textSecondary; font.pixelSize: Theme.fs(11); verticalAlignment: Text.AlignVCenter; elide: Text.ElideMiddle
                                PlainToolTip { visible: groupHover.hovered; text: modelData.label || "" }
                                HoverHandler { id: groupHover }
                            }
                            Rectangle {
                                objectName: "importRecordRow" + modelData.originalIndex
                                anchors.fill: parent; visible: !parent.groupHeader; radius: Theme.radiusS
                                color: dialog.controller && modelData.originalIndex === dialog.controller.currentRow ? Theme.accentMuted : rowHover.hovered ? Theme.fill : Theme.surface
                                border.color: dialog.controller && modelData.originalIndex === dialog.controller.currentRow ? Theme.accent : Theme.border
                                HoverHandler { id: rowHover }
                                TapHandler { enabled: !dialog.inputLocked; onTapped: { dialog.selectRow(modelData.originalIndex); candidateList.forceActiveFocus(Qt.MouseFocusReason) } }
                                RowLayout {
                                    anchors.fill: parent; anchors.margins: 6; spacing: 5
                                    ImportCheckBox { objectName: "importRowCheck" + modelData.originalIndex; checked: importRow.selected === true; enabled: !dialog.inputLocked && dialog.controller && dialog.controller.previewValid && importRow.can_import === true; onClicked: { dialog.flushEditors(); dialog.controller.setRowSelected(modelData.originalIndex, checked) } Accessible.name: qsTr("选择导入第 %1 行").arg(modelData.originalIndex + 1) }
                                    ColumnLayout {
                                        Layout.fillWidth: true; spacing: 3
                                        Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("%1. %2").arg(modelData.originalIndex + 1).arg(importCandidate.duty_name || qsTr("副本待识别")); color: importRow.can_import === false ? Theme.orangeText : Theme.textPrimary; font.pixelSize: Theme.fs(12); elide: Text.ElideRight }
                                        Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: (importCandidate.job_name || qsTr("职业未知")) + " · " + (importCandidate.reflection_text || dialog.statusLabel(importRow)).replace(/\s+/g, " "); color: Theme.textSecondary; font.pixelSize: Theme.fs(11); elide: Text.ElideRight }
                                        Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: importCandidate.source_recorded_at || dialog.statusLabel(importRow); color: Theme.textMuted; font.pixelSize: Theme.fs(10); elide: Text.ElideRight }
                                    }
                                }
                                PlainToolTip { visible: rowHover.hovered; delay: 700; text: (importCandidate.duty_name || qsTr("副本待识别")) + "\n" + dialog.statusLabel(importRow) }
                            }
                        }
                        Text { textFormat: Text.PlainText; anchors.centerIn: parent; width: parent.width - 12; visible: dialog.visibleRows.length === 0; text: qsTr("没有符合此筛选的记录"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap; horizontalAlignment: Text.AlignHCenter }
                    }
                    Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("列表聚焦后：↑↓ 切换 · 空格勾选 · Enter 下一条"); color: Theme.textMuted; font.pixelSize: Theme.fs(10); wrapMode: Text.WordWrap }
                }
                ColumnLayout {
                    Layout.fillWidth: true; Layout.fillHeight: true; spacing: 12
                    RowLayout {
                        Layout.fillWidth: true; spacing: 6
                        ColumnLayout {
                            Layout.fillWidth: true; spacing: 2
                            SectionHeading { text: qsTr("第 %1 条").arg(dialog.controller ? dialog.controller.currentRow + 1 : 0) }
                            Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: dialog.sourceName(dialog.evidence.source_image) || (dialog.controller ? dialog.controller.sourceLabel : ""); color: Theme.textSecondary; font.pixelSize: Theme.fs(11); elide: Text.ElideMiddle }
                        }
                        AppButton { objectName: "importPrevious"; text: qsTr("上一条"); iconName: "chevron-left"; compact: true; enabled: !dialog.inputLocked && dialog.visiblePosition() > 0; onClicked: dialog.navigateRow(-1) }
                        AppButton { objectName: "importNext"; text: qsTr("下一条"); iconName: "chevron-right"; compact: true; enabled: !dialog.inputLocked && dialog.visiblePosition() + 1 < dialog.visibleRows.length; onClicked: dialog.navigateRow(1) }
                        AppButton { objectName: "importSelectNext"; text: qsTr("勾选并下一条"); variant: "primary"; compact: true; enabled: !dialog.inputLocked && dialog.controller && dialog.controller.previewValid && dialog.rowData.can_import === true; onClicked: dialog.checkAndNext(); ToolTip.visible: hovered; ToolTip.text: qsTr("列表聚焦时也可按 Enter") }
                    }
                    Text { textFormat: Text.PlainText; Layout.fillWidth: true; visible: dialog.navigationHint.length > 0; text: dialog.navigationHint; color: Theme.orangeText; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
                    RowLayout {
                        Layout.fillWidth: true; Layout.fillHeight: true; spacing: 12
                        ScrollView {
                            id: sourcePane
                            objectName: "importSourcePane"
                            Layout.preferredWidth: reviewColumns.centerWidth; Layout.minimumWidth: 270; Layout.maximumWidth: reviewColumns.centerWidth; Layout.fillHeight: true; clip: true
                            contentWidth: availableWidth; ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
                            ColumnLayout {
                                width: sourcePane.availableWidth; spacing: 10
                                RowLayout {
                                    Layout.fillWidth: true
                                    SectionHeading { text: qsTr("原图"); visible: !!dialog.evidence.source_url }
                                    Item { Layout.fillWidth: true }
                                    AppButton { objectName: "importViewWholeImage"; text: qsTr("查看整张"); compact: true; visible: !!dialog.evidence.source_url; enabled: !dialog.inputLocked; onClicked: { dialog.flushEditors(); imageDialog.zoom = 1; imageDialog.open() } }
                                }
                                Text { textFormat: Text.PlainText; Layout.fillWidth: true; visible: !!dialog.evidence.source_url; text: qsTr("文字参考分 %1 · %2").arg(Math.round(Number(dialog.evidence.ocr_confidence || 0) * 100)).arg(dialog.iconEvidenceText); color: Theme.textSecondary; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
                                Rectangle {
                                    Layout.fillWidth: true
                                    Layout.preferredHeight: Math.max(100, Math.min(220,
                                        dialog.evidence.source_rect && dialog.evidence.source_rect.width > 0
                                        ? sourcePane.availableWidth * dialog.evidence.source_rect.height / dialog.evidence.source_rect.width + 8 : 160))
                                    visible: !!dialog.evidence.source_url
                                    color: Theme.insetBackground; radius: Theme.radiusS; clip: true
                                    // OCR coordinates refer to the EXIF-oriented image. Qt's
                                    // sourceClipRect clips before that transform, so crop the
                                    // oriented visual here instead of clipping encoded pixels.
                                    Item {
                                        id: sourceCrop
                                        objectName: "importSourceCrop"
                                        property rect sourceRect: dialog.evidence.source_rect
                                            ? Qt.rect(dialog.evidence.source_rect.x || 0, dialog.evidence.source_rect.y || 0,
                                                      dialog.evidence.source_rect.width || 0, dialog.evidence.source_rect.height || 0)
                                            : Qt.rect(0, 0, 0, 0)
                                        readonly property rect cropRect: {
                                            const w = Math.max(0, sourceImage.sourceSize.width)
                                            const h = Math.max(0, sourceImage.sourceSize.height)
                                            if (sourceRect.width <= 0 || sourceRect.height <= 0)
                                                return Qt.rect(0, 0, w, h)
                                            const x = Math.max(0, Math.min(w, sourceRect.x))
                                            const y = Math.max(0, Math.min(h, sourceRect.y))
                                            const right = Math.max(x, Math.min(w, sourceRect.x + sourceRect.width))
                                            const bottom = Math.max(y, Math.min(h, sourceRect.y + sourceRect.height))
                                            return Qt.rect(x, y, right - x, bottom - y)
                                        }
                                        readonly property real imageScale: cropRect.width > 0 && cropRect.height > 0
                                            ? Math.max(0, Math.min((parent.width - 8) / cropRect.width,
                                                                 (parent.height - 8) / cropRect.height)) : 0
                                        anchors.centerIn: parent
                                        width: cropRect.width * imageScale
                                        height: cropRect.height * imageScale
                                        clip: true
                                        Image {
                                            id: sourceImage
                                            objectName: "importSourceImage"
                                            autoTransform: true
                                            // Leave sourceSize unset: its default is the oriented
                                            // physical pixel size; @2x only shrinks implicit size.
                                            source: dialog.evidence.source_url || ""
                                            x: -sourceCrop.cropRect.x * sourceCrop.imageScale
                                            y: -sourceCrop.cropRect.y * sourceCrop.imageScale
                                            width: Math.max(0, sourceSize.width) * sourceCrop.imageScale
                                            height: Math.max(0, sourceSize.height) * sourceCrop.imageScale
                                            asynchronous: true
                                            cache: false
                                        }
                                    }
                                }
                                Text { textFormat: Text.PlainText; Layout.fillWidth: true; visible: !dialog.evidence.source_url; text: qsTr("表格记录没有原图，可对照原文件核对右侧字段和心得。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
                                SectionHeading { text: qsTr("心得全文") }
                                Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("识别出的文字和原图不一致，直接在这里修改。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
                                ScrollView {
                                    id: reflectionScroll
                                    objectName: "importReflectionScroll"
                                    Layout.fillWidth: true
                                    Layout.preferredHeight: 190
                                    clip: true
                                    contentWidth: availableWidth
                                    ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
                                TextArea {
                                    id: reflectionEditor
                                    objectName: "importReflectionText"
                                    property int editingRow: -1
                                    property string originalText: ""
                                    width: reflectionScroll.availableWidth
                                    height: Math.max(190, contentHeight + topPadding + bottomPadding)
                                    text: dialog.value("reflection_text")
                                    color: Theme.textPrimary; placeholderTextColor: Theme.textMuted; palette.placeholderText: Theme.textMuted
                                    font.pixelSize: Theme.fs(13); wrapMode: TextEdit.Wrap; selectByMouse: true; enabled: !dialog.inputLocked
                                    leftPadding: 10; rightPadding: 10; topPadding: 10; bottomPadding: 10
                                    Accessible.name: qsTr("导入记录心得全文")
                                    onActiveFocusChanged: { if (activeFocus) { editingRow = dialog.controller.currentRow; originalText = text } else { dialog.flushReflection(); editingRow = -1 } }
                                    background: Rectangle { color: Theme.surface; border.color: Theme.neutral300; radius: Theme.radiusS }
                                }
                                }
                            }
                        }
                        Rectangle { Layout.preferredWidth: 1; Layout.fillHeight: true; color: Theme.border }
                        ScrollView {
                            id: fieldsPane
                            objectName: "importFieldsPane"
                            Layout.fillWidth: true; Layout.minimumWidth: 297; Layout.fillHeight: true; clip: true
                            contentWidth: availableWidth; ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
                            ColumnLayout {
                                width: fieldsPane.availableWidth; spacing: 10; enabled: !dialog.inputLocked
                                SectionHeading { text: qsTr("识别结果") }
                                Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: (dialog.rowData.errors || []).join("\n"); visible: text.length > 0; color: Theme.red; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
                                AppButton {
                                    Layout.alignment: Qt.AlignLeft
                                    text: dialog.showRecognitionHints ? qsTr("收起识别提示") : qsTr("查看 %1 条识别提示").arg(dialog.recognitionHints.length)
                                    variant: "ghost"
                                    compact: true
                                    visible: dialog.recognitionHints.length > 0
                                    onClicked: dialog.showRecognitionHints = !dialog.showRecognitionHints
                                }
                                Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: dialog.recognitionHints.join("\n"); visible: dialog.showRecognitionHints && text.length > 0; color: Theme.orangeText; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
                                GridLayout {
                                    Layout.fillWidth: true; columns: 2; columnSpacing: 8; rowSpacing: 9
                                    Text { textFormat: Text.PlainText; objectName: "importDutyCandidateWarning"; Layout.columnSpan: 2; Layout.fillWidth: true; visible: dialog.evidence.duty_candidate_pending === true; text: qsTr("副本候选待复核：%1\n原始识别：%2，请对照原图确认。").arg(dialog.evidence.duty_candidate_name || "").arg(dialog.evidence.ocr_duty_name || ""); color: Theme.orangeText; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
                                    FieldLabel { text: qsTr("副本") }
                                    StyledTextField {
                                        id: dutyNameField
                                        objectName: "importDutyName"
                                        Layout.fillWidth: true
                                        text: dialog.value("duty_name")
                                        Accessible.description: qsTr("输入副本名称联想，方向键选择，Tab 填入候选。")
                                        property string suggestionQuery: ""
                                        property int suggestionIndex: 0
                                        readonly property var suggestions: {
                                            const query = suggestionQuery.trim().toLowerCase()
                                            if (!query || !dialog.controller) return []
                                            const choices = dialog.controller.dutyChoices || []
                                            const found = []
                                            for (let i = 0; i < choices.length && found.length < 8; ++i) {
                                                if (String(choices[i].duty_name || "").toLowerCase().indexOf(query) >= 0)
                                                    found.push(choices[i])
                                            }
                                            return found
                                        }
                                        function acceptSuggestion(index) {
                                            if (index < 0 || index >= suggestions.length || preeditText.length > 0) return
                                            const duty = suggestions[index]
                                            dialog.flushReflection()
                                            // Selecting a catalog candidate writes name and identity together.
                                            // Free-text edits instead clear stale identity in the controller.
                                            dialog.controller.updateCandidate(dialog.controller.currentRow, {
                                                duty_name: duty.duty_name, content_id: duty.content_id,
                                                territory_id: duty.territory_id, duty_category: duty.duty_category,
                                                duty_source: "manual"
                                            })
                                            dutySuggestions.close()
                                            suggestionQuery = ""
                                            cursorPosition = length
                                        }
                                        onTextEdited: {
                                            suggestionQuery = text
                                            suggestionIndex = 0
                                            if (suggestions.length > 0 && preeditText.length === 0) dutySuggestions.open()
                                            else dutySuggestions.close()
                                        }
                                        onPreeditTextChanged: if (preeditText.length > 0) dutySuggestions.close()
                                        onActiveFocusChanged: if (!activeFocus) dutySuggestions.close()
                                        onEditingFinished: dialog.edit("duty_name", text.trim())
                                        Keys.onPressed: function(event) {
                                            if (preeditText.length > 0 || event.modifiers !== Qt.NoModifier) return
                                            if ((event.key === Qt.Key_Down || event.key === Qt.Key_Up) && suggestions.length > 0) {
                                                if (!dutySuggestions.visible) dutySuggestions.open()
                                                else suggestionIndex = (suggestionIndex + (event.key === Qt.Key_Down ? 1 : -1) + suggestions.length) % suggestions.length
                                                event.accepted = true
                                            } else if (event.key === Qt.Key_Escape && dutySuggestions.visible) {
                                                dutySuggestions.close()
                                                event.accepted = true
                                            }
                                        }
                                        Keys.onTabPressed: function(event) {
                                            event.accepted = dutySuggestions.visible && suggestions.length > 0
                                                && preeditText.length === 0 && event.modifiers === Qt.NoModifier
                                            if (event.accepted) acceptSuggestion(suggestionIndex)
                                        }
                                        Popup {
                                            id: dutySuggestions
                                            objectName: "importDutySuggestions"
                                            parent: dutyNameField
                                            x: 0; y: dutyNameField.height + 2
                                            width: dutyNameField.width
                                            height: suggestionsList.contentHeight + 8
                                            padding: 4
                                            focus: false
                                            closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutsideParent
                                            background: Rectangle { color: Theme.surface; radius: Theme.radiusS; border.color: Theme.border }
                                            contentItem: ListView {
                                                id: suggestionsList
                                                objectName: "importDutySuggestionList"
                                                implicitHeight: contentHeight
                                                clip: true
                                                model: dutyNameField.suggestions
                                                currentIndex: dutyNameField.suggestionIndex
                                                delegate: ItemDelegate {
                                                    required property var modelData
                                                    required property int index
                                                    width: suggestionsList.width
                                                    height: 30
                                                    text: modelData.duty_name
                                                    focusPolicy: Qt.NoFocus
                                                    highlighted: index === dutyNameField.suggestionIndex
                                                    contentItem: Text { text: modelData.duty_name; textFormat: Text.PlainText; color: Theme.textPrimary; font.pixelSize: Theme.fs(12); verticalAlignment: Text.AlignVCenter; elide: Text.ElideRight }
                                                    background: Rectangle { color: parent.highlighted || parent.hovered ? Theme.accentMuted : "transparent"; radius: Theme.radiusS }
                                                    onClicked: dutyNameField.acceptSuggestion(index)
                                                }
                                            }
                                        }
                                    }
                                    FieldLabel { text: qsTr("副本类别") }
                                    StyledTextField { objectName: "importDutyCategory"; Layout.fillWidth: true; text: dialog.value("duty_category"); placeholderText: qsTr("根据副本自动填写"); Accessible.description: qsTr("根据匹配的副本资料自动填写类别"); onEditingFinished: { const category = text.trim(); if (category !== dialog.value("duty_category")) dialog.edit("duty_category", category || null) } }
                                    Text { textFormat: Text.PlainText; objectName: "importJobCandidateWarning"; Layout.columnSpan: 2; Layout.fillWidth: true; visible: dialog.evidence.job_candidate_pending === true || dialog.unsupportedSourceJob; text: dialog.unsupportedSourceJob ? qsTr("来源职业不适用于导随，请核对并选择职业。") : qsTr("候选待复核：%1（%2），请对照原图确认职业。").arg(dialog.evidence.job_candidate_name || "").arg(dialog.iconEvidenceText); color: Theme.orangeText; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
                                    FieldLabel { text: qsTr("职业") }
                                    StyledComboBox {
                                        objectName: "importJob"; Layout.fillWidth: true; model: dialog.controller ? dialog.controller.jobChoices : []; textRole: "job_name"; currentIndex: dialog.jobChoiceIndex
                                        displayText: dialog.unsupportedSourceJob ? qsTr("%1（来源职业）").arg(dialog.value("job_name") || qsTr("职业 %1").arg(dialog.value("job_id"))) : (model[currentIndex] ? model[currentIndex].job_name : "")
                                        onActivated: function(index) { dialog.flushReflection(); dialog.controller.updateCandidate(dialog.controller.currentRow, {job_id: model[index].job_id, job_name: index ? model[index].job_name : null}) }
                                    }
                                    FieldLabel { text: qsTr("原站记录时间") }
                                    StyledTextField { objectName: "importSourceRecordedAt"; Layout.fillWidth: true; text: dialog.value("source_recorded_at"); placeholderText: "yyyy-MM-dd HH:mm:ss"; onEditingFinished: dialog.edit("source_recorded_at", text.trim() || null) }
                                    Text { textFormat: Text.PlainText; Layout.columnSpan: 2; Layout.fillWidth: true; text: qsTr("按所选地区解释原站时间，不代表实际游戏时间。"); color: Theme.textMuted; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
                                    FieldLabel { text: qsTr("结果") }
                                    StyledComboBox { objectName: "importResult"; Layout.fillWidth: true; model: dialog.resultOptions; textRole: "text"; currentIndex: dialog.optionIndex(model, "value", dialog.candidate.result || "COMPLETED"); onActivated: function(index) { dialog.edit("result", model[index].value) } }
                                    Text { textFormat: Text.PlainText; Layout.columnSpan: 2; Layout.fillWidth: true; text: qsTr("来源未提供结果时按通关预填，可以修改。"); color: Theme.textMuted; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
                                    FieldLabel { text: qsTr("打本心情") }
                                    StyledComboBox { objectName: "importMood"; Layout.fillWidth: true; model: dialog.moodOptions; textRole: "text"; currentIndex: dialog.optionIndex(model, "value", dialog.candidate.reflection_mood || "unknown"); onActivated: function(index) { dialog.edit("reflection_mood", model[index].value) } }
                                }
                                ImportCheckBox { objectName: "importShowGameFacts"; text: qsTr("补充已知的实际游戏时间"); checked: dialog.showGameFacts; onToggled: dialog.showGameFacts = checked }
                                GridLayout {
                                    Layout.fillWidth: true; visible: dialog.showGameFacts; columns: 2; columnSpacing: 8; rowSpacing: 8
                                    FieldLabel { text: qsTr("匹配时间 UTC") }
                                    StyledTextField { Layout.fillWidth: true; text: dialog.value("matched_at_utc"); placeholderText: "2026-10-08T10:00:00.000Z"; onEditingFinished: dialog.edit("matched_at_utc", text.trim() || null) }
                                    FieldLabel { text: qsTr("进入时间 UTC") }
                                    StyledTextField { Layout.fillWidth: true; text: dialog.value("entered_at_utc"); placeholderText: qsTr("未知可留空"); onEditingFinished: dialog.edit("entered_at_utc", text.trim() || null) }
                                    FieldLabel { text: qsTr("结束时间 UTC") }
                                    StyledTextField { Layout.fillWidth: true; text: dialog.value("ended_at_utc"); placeholderText: qsTr("未知可留空"); onEditingFinished: dialog.edit("ended_at_utc", text.trim() || null) }
                                    FieldLabel { text: qsTr("耗时（毫秒）") }
                                    StyledTextField { Layout.fillWidth: true; text: dialog.value("duration_ms"); placeholderText: qsTr("未知可留空"); onEditingFinished: dialog.edit("duration_ms", text.trim().length ? Number(text) : null) }
                                }
                                Rectangle { Layout.fillWidth: true; Layout.preferredHeight: incompleteHint.implicitHeight + 18; color: Theme.insetBackground; radius: Theme.radiusS
                                    Text { textFormat: Text.PlainText; id: incompleteHint; anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 9; text: qsTr("核对本人导随及通关结果后即可计数。未知游戏时间和耗时可留空，保留待补充提示，不影响已确认通关与成就。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
                                }
                            }
                        }
                    }
                }
            }
        }
        // 这些记录是否已包含在基数中: the baseline is the count the game showed before this
        // software was installed, so imported history of that time is already inside it. The
        // Collector deducts the contributing completions in the same commit when told so.
        Rectangle {
            objectName: "importBaselineChoice"
            visible: dialog.hasRows && dialog.controller && dialog.controller.baselineChoiceOffered
            Layout.fillWidth: true; Layout.preferredHeight: baselineChoice.implicitHeight + 18
            color: Theme.insetBackground; radius: Theme.radiusS
            ColumnLayout {
                id: baselineChoice
                anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 9; spacing: 6
                Text {
                    textFormat: Text.PlainText; Layout.fillWidth: true; wrapMode: Text.WordWrap
                    text: qsTr("勾选的记录中有 %1 条计入进度的通关。它们是否已经包含在成就基数 %2 中？").arg(dialog.controller ? dialog.controller.contributingSelectedCount : 0).arg(dialog.controller ? dialog.controller.baselineCount : 0)
                    color: Theme.textPrimary; font.pixelSize: Theme.fs(12)
                }
                Flow {
                    Layout.fillWidth: true; spacing: 8
                    PickChip {
                        objectName: "importBaselineDeduct"
                        checked: dialog.controller ? dialog.controller.deductFromBaseline : false
                        enabled: !dialog.inputLocked
                        text: qsTr("已包含：保存时从基数中扣除 %1 次，基数改为 %2").arg(dialog.controller ? dialog.controller.baselineDeductionPreview : 0).arg(dialog.controller ? dialog.controller.baselineCount - dialog.controller.baselineDeductionPreview : 0)
                        onPicked: if (dialog.controller) dialog.controller.deductFromBaseline = true
                    }
                    PickChip {
                        objectName: "importBaselineKeep"
                        checked: dialog.controller ? !dialog.controller.deductFromBaseline : false
                        enabled: !dialog.inputLocked
                        text: qsTr("未包含：照常加在基数之上")
                        onPicked: if (dialog.controller) dialog.controller.deductFromBaseline = false
                    }
                }
                Text {
                    textFormat: Text.PlainText; Layout.fillWidth: true; wrapMode: Text.WordWrap
                    visible: dialog.controller && dialog.controller.deductFromBaseline && dialog.controller.contributingSelectedCount > dialog.controller.baselineCount
                    text: qsTr("基数只有 %1，只能扣到 0；其余 %2 次照常加在基数之上。").arg(dialog.controller ? dialog.controller.baselineCount : 0).arg(dialog.controller ? dialog.controller.contributingSelectedCount - dialog.controller.baselineCount : 0)
                    color: Theme.textSecondary; font.pixelSize: Theme.fs(11)
                }
            }
        }
        Rectangle { Layout.fillWidth: true; Layout.preferredHeight: 1; color: Theme.border }
        RowLayout {
            Layout.fillWidth: true; spacing: 10
            Item { id: startZone; visible: !dialog.hasRows; Layout.fillWidth: true; Layout.preferredHeight: sourceZone.implicitHeight }
            ColumnLayout {
                visible: dialog.hasRows; Layout.fillWidth: true; spacing: 2
                Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: dialog.controller && dialog.controller.selectedCount > 0 ? qsTr("已勾选 %1 条").arg(dialog.controller.selectedCount) : qsTr("还没有勾选记录"); color: Theme.textSecondary; font.pixelSize: Theme.fs(11); elide: Text.ElideRight }
                Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: dialog.controller && dialog.controller.previewValid ? qsTr("核对后勾选需要导入的记录") : qsTr("修改后请重新校验"); color: Theme.textMuted; font.pixelSize: Theme.fs(10); elide: Text.ElideRight }
            }
            ImportCheckBox { objectName: "importOwnRecordsConfirmed"; visible: dialog.hasRows; text: qsTr("我确认这些是自己的记录"); checked: dialog.controller ? dialog.controller.ownRecordsConfirmed : false; enabled: !dialog.inputLocked; onToggled: if (dialog.controller) dialog.controller.ownRecordsConfirmed = checked }
            AppButton { objectName: "importCancel"; text: dialog.working ? qsTr("取消识别 / 预览") : qsTr("关闭"); enabled: !dialog.controller || (!dialog.controller.committing && !dialog.controller.pendingCommitConfirmation); onClicked: { if (dialog.controller) dialog.controller.cancel(); dialog.close() } }
            AppButton { objectName: "importRevalidate"; text: qsTr("重新校验"); visible: dialog.hasRows; enabled: !dialog.inputLocked && dialog.controller && dialog.controller.sourceKind.length > 0; onClicked: { dialog.flushEditors(); dialog.controller.revalidate() } }
            AppButton { objectName: "importCommit"; visible: dialog.hasRows; text: dialog.controller && dialog.controller.pendingCommitConfirmation ? qsTr("重试确认保存") : qsTr("导入选中的 %1 条").arg(dialog.controller ? dialog.controller.selectedCount : 0); variant: "primary"; enabled: dialog.controller && dialog.controller.canCommit && !(reflectionEditor.activeFocus && reflectionEditor.text !== reflectionEditor.originalText); onClicked: { dialog.flushEditors(); dialog.controller.commit() } }
        }
    }

    // Keep one identity for the source controls as they move between stages.
    RowLayout {
        id: sourceActions
        parent: dialog.hasRows ? reviewActions : startActions
        anchors.fill: parent; spacing: 6
        AppButton { id: chooseInput; objectName: "importChooseFiles"; text: dialog.hasRows ? qsTr("添加文件") : qsTr("选择文件 / 多张截图"); iconName: dialog.hasRows ? "plus" : "folder-open"; variant: dialog.hasRows ? "secondary" : "primary"; enabled: !dialog.inputLocked; onClicked: { dialog.flushEditors(); dialog.controller.chooseFiles() } }
        AppButton { objectName: "importPasteClipboard"; text: qsTr("粘贴截图或表格"); iconName: "clipboard-paste"; enabled: !dialog.inputLocked; onClicked: { dialog.flushEditors(); dialog.controller.pasteClipboard() } ToolTip.visible: hovered; ToolTip.text: qsTr("Ctrl+V（文本编辑框以外）") }
        AppButton { objectName: "importMappingOpen"; text: qsTr("列映射"); visible: dialog.canMapColumns; enabled: !dialog.inputLocked; onClicked: { dialog.flushEditors(); mappingDialog.open() } }
    }
    ColumnLayout {
        id: sourceZone
        parent: dialog.hasRows ? reviewZone : startZone
        anchors.fill: parent; spacing: 5
        RowLayout {
            Text { textFormat: Text.PlainText; text: qsTr("时间按"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
            StyledComboBox {
                id: timeZonePreset
                objectName: "importTimeZonePreset"; Layout.preferredWidth: dialog.hasRows ? 174 : 200; enabled: !dialog.inputLocked
                model: dialog.timeZoneOptions; textRole: "text"; currentIndex: dialog.timeZoneChoiceIndex()
                onActivated: function(index) { const zone = model[index].value; dialog.customTimeZoneSelected = zone === ""; if (zone !== "" && dialog.controller) dialog.controller.timeZone = zone }
                Accessible.name: qsTr("截图或表格上的时间所在地区")
                ToolTip.visible: hovered; ToolTip.delay: 600; ToolTip.text: qsTr("中国大陆来源通常使用北京时间，不会填补游戏进出时间。")
            }
        }
        RowLayout {
            visible: timeZonePreset.currentIndex === dialog.timeZoneOptions.length - 1
            Text { textFormat: Text.PlainText; text: qsTr("自定义时区"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
            StyledTextField { objectName: "importTimeZone"; Layout.fillWidth: true; Layout.preferredWidth: 130; enabled: !dialog.inputLocked; text: dialog.controller ? dialog.controller.timeZone : "+08:00"; placeholderText: qsTr("例如 +05:30"); onEditingFinished: if (dialog.controller) dialog.controller.timeZone = text.trim(); Accessible.name: qsTr("自定义原记录时区") }
        }
    }
    Shortcut {
        sequences: [StandardKey.Paste]
        enabled: dialog.opened && !dialog.inputLocked && !pasteDialog.opened && !batchDialog.opened
                 && !imageDialog.opened && !mappingDialog.opened && !templatesDialog.opened && !dialog.editorOwnsPaste()
        onActivated: {
            dialog.flushEditors(); dialog.controller.pasteClipboard()
        }
    }
    Dialog {
        id: templatesDialog
        objectName: "importTemplatesDialog"
        modal: true
        width: Math.min(640, dialog.width - 24)
        height: Math.min(540, dialog.height - 24)
        x: Math.round((dialog.width - width) / 2)
        y: Math.round((dialog.height - height) / 2)
        padding: 18
        background: DialogFrame {}
        contentItem: ColumnLayout {
            spacing: 12
            Text { textFormat: Text.PlainText; text: qsTr("保存导入模板"); color: Theme.textPrimary; font.pixelSize: Theme.fs(16); font.bold: true }
            Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("保存到本地后填写，再回到这里导入。模板为空白，不包含示例记录。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12); wrapMode: Text.WordWrap }
            RowLayout {
                Layout.fillWidth: true; spacing: 8
                AppButton { objectName: "importSaveExcelTemplate"; text: qsTr("保存 Excel 模板"); iconName: "file-down"; enabled: !dialog.inputLocked; onClicked: dialog.controller.saveTemplate("XLSX") }
                AppButton { objectName: "importSaveJsonTemplate"; text: qsTr("保存 JSON 模板"); iconName: "file-json"; enabled: !dialog.inputLocked; onClicked: dialog.controller.saveTemplate("JSON") }
                Item { Layout.fillWidth: true }
            }
            Text {
                objectName: "importTemplateSaveStatus"
                textFormat: Text.PlainText; Layout.fillWidth: true; wrapMode: Text.WordWrap
                text: dialog.controller ? (dialog.controller.templateErrorText || dialog.controller.templateStatusText) : ""
                visible: text.length > 0
                color: dialog.controller && dialog.controller.templateErrorText.length > 0 ? Theme.red : Theme.textSecondary
                font.pixelSize: Theme.fs(12); maximumLineCount: 3; elide: Text.ElideMiddle
                PlainToolTip { visible: templateStatusHover.hovered; text: parent.text }
                HoverHandler { id: templateStatusHover }
            }
            ScrollView {
                id: templateHelpScroll
                objectName: "importTemplateHelpScroll"
                Layout.fillWidth: true; Layout.fillHeight: true; clip: true
                Text {
                    objectName: "importTemplateHelp"
                    width: templateHelpScroll.availableWidth
                    textFormat: Text.PlainText; wrapMode: Text.WordWrap
                    text: dialog.controller ? dialog.controller.templateInstructions : ""
                    color: Theme.textPrimary; font.pixelSize: Theme.fs(12)
                }
            }
            AppButton { objectName: "importTemplatesClose"; text: qsTr("返回导入"); Layout.alignment: Qt.AlignRight; onClicked: templatesDialog.close() }
        }
    }

    Dialog {
        id: mappingDialog
        objectName: "importMappingDialog"
        modal: true
        width: Math.min(640, dialog.width - 24)
        x: Math.round((dialog.width - width) / 2)
        y: Math.round((dialog.height - height) / 2)
        padding: 18
        background: DialogFrame {}
        contentItem: ColumnLayout {
            spacing: 12
            SectionHeading { text: qsTr("对应表格列名") }
            Text {
                textFormat: Text.PlainText
                Layout.fillWidth: true
                text: qsTr("填写原表头名称。即使暂时没有识别到记录，也可以在这里调整列映射后重试。")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
            GridLayout {
                Layout.fillWidth: true
                columns: 2
                columnSpacing: 12
                rowSpacing: 10
                FieldLabel { text: qsTr("副本列名") }
                StyledTextField { id: mapDuty; objectName: "importMapDuty"; Layout.fillWidth: true; placeholderText: qsTr("如 副本"); Accessible.name: qsTr("原表副本列名") }
                FieldLabel { text: qsTr("职业列名") }
                StyledTextField { id: mapJob; objectName: "importMapJob"; Layout.fillWidth: true; placeholderText: qsTr("如 职业"); Accessible.name: qsTr("原表职业列名") }
                FieldLabel { text: qsTr("记录日期列名") }
                StyledTextField { id: mapDate; objectName: "importMapDate"; Layout.fillWidth: true; placeholderText: qsTr("原站记录时间"); Accessible.name: qsTr("原表记录日期列名") }
                FieldLabel { text: qsTr("心得列名") }
                StyledTextField { id: mapReflection; objectName: "importMapReflection"; Layout.fillWidth: true; placeholderText: qsTr("如 评论"); Accessible.name: qsTr("原表心得列名") }
            }
            Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("记录日期保留来源语义，不会填补实际游戏时间。"); color: Theme.textMuted; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
            RowLayout {
                Layout.fillWidth: true
                Item { Layout.fillWidth: true }
                AppButton { text: qsTr("取消"); onClicked: mappingDialog.close() }
                AppButton {
                    objectName: "importMappingApply"
                    text: qsTr("应用并重新读取")
                    variant: "primary"
                    enabled: !dialog.inputLocked && dialog.canMapColumns
                    onClicked: { dialog.flushEditors(); mappingDialog.close(); dialog.applyMapping() }
                }
            }
        }
    }
    Dialog {
        id: pasteDialog
        objectName: "importManualPasteDialog"
        modal: true; width: Math.min(640, dialog.width - 24); height: Math.min(370, dialog.height - 24)
        x: Math.round((dialog.width - width) / 2); y: Math.round((dialog.height - height) / 2); padding: 18; background: DialogFrame {}
        contentItem: ColumnLayout {
            spacing: 12
            SectionHeading { text: qsTr("粘贴带表头的表格文字") }
            TextArea {
                id: pastedText
                objectName: "importPastedText"
                property string importPlaceholderHint: qsTr("也可以在这里粘贴带表头的表格文字")
                Layout.fillWidth: true; Layout.fillHeight: true; Accessible.description: importPlaceholderHint
                color: Theme.textPrimary; placeholderTextColor: Theme.textMuted; palette.placeholderText: Theme.textMuted
                font.pixelSize: Theme.fs(12); selectByMouse: true; wrapMode: TextEdit.Wrap; enabled: !dialog.inputLocked
                background: Rectangle { color: Theme.surface; radius: Theme.radiusS; border.color: Theme.border }
                Text { textFormat: Text.PlainText; objectName: "importPastePlaceholder"; x: pastedText.leftPadding; y: pastedText.topPadding; width: Math.max(0, pastedText.width - pastedText.leftPadding - pastedText.rightPadding); text: pastedText.importPlaceholderHint; font: pastedText.font; color: Theme.textMuted; visible: pastedText.length === 0 && pastedText.preeditText.length === 0; wrapMode: Text.WordWrap }
            }
            RowLayout { Layout.fillWidth: true; Item { Layout.fillWidth: true } AppButton { text: qsTr("取消"); onClicked: pasteDialog.close() } AppButton { text: qsTr("预览粘贴内容"); variant: "primary"; enabled: !dialog.inputLocked && pastedText.text.trim().length > 0; onClicked: { pasteDialog.close(); dialog.controller.importText(pastedText.text) } } }
        }
    }
    Dialog {
        id: batchDialog
        objectName: "importBatchDialog"
        modal: true; width: Math.min(430, dialog.width - 24); padding: 18
        x: Math.round((dialog.width - width) / 2); y: Math.round((dialog.height - height) / 2); background: DialogFrame {}
        contentItem: ColumnLayout {
            spacing: 12
            SectionHeading { text: qsTr("批量设置") }
            Text { textFormat: Text.PlainText; text: qsTr("只应用到已勾选的 %1 条记录").arg(dialog.controller ? dialog.controller.selectedCount : 0); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
            GridLayout {
                columns: 2; Layout.fillWidth: true; columnSpacing: 10; rowSpacing: 10
                FieldLabel { text: qsTr("职业") }
                StyledComboBox { id: batchJob; objectName: "importBatchJob"; Layout.fillWidth: true; model: [{job_name: qsTr("不修改"), noChange: true}].concat(dialog.controller ? dialog.controller.jobChoices : []); textRole: "job_name" }
                FieldLabel { text: qsTr("结果") }
                StyledComboBox { id: batchResult; objectName: "importBatchResult"; Layout.fillWidth: true; model: [{text: qsTr("不修改"), noChange: true}].concat(dialog.resultOptions); textRole: "text" }
                FieldLabel { text: qsTr("打本心情") }
                StyledComboBox { id: batchMood; objectName: "importBatchMood"; Layout.fillWidth: true; model: [{text: qsTr("不修改"), noChange: true}].concat(dialog.moodOptions); textRole: "text" }
            }
            Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("应用后会重新校验，仍可导入的记录会保留勾选。"); color: Theme.textMuted; font.pixelSize: Theme.fs(11); wrapMode: Text.WordWrap }
            RowLayout {
                Layout.fillWidth: true; Item { Layout.fillWidth: true }
                AppButton { text: qsTr("取消"); onClicked: batchDialog.close() }
                AppButton {
                    objectName: "importBatchApply"; text: qsTr("应用到已勾选记录"); variant: "primary"
                    enabled: !dialog.inputLocked && dialog.controller && dialog.controller.previewValid && dialog.controller.selectedCount > 0 && (batchJob.currentIndex > 0 || batchResult.currentIndex > 0 || batchMood.currentIndex > 0)
                    onClicked: {
                        let changes = {}
                        if (batchJob.currentIndex > 0) { const job = batchJob.model[batchJob.currentIndex]; changes.job_id = job.job_id; changes.job_name = job.job_id === null ? null : job.job_name }
                        if (batchResult.currentIndex > 0) changes.result = batchResult.model[batchResult.currentIndex].value
                        if (batchMood.currentIndex > 0) changes.reflection_mood = batchMood.model[batchMood.currentIndex].value
                        batchDialog.close(); dialog.controller.batchUpdateSelected(changes)
                    }
                }
            }
        }
    }
    Dialog {
        id: imageDialog
        objectName: "importWholeImageDialog"
        property real zoom: 1
        modal: true; width: Math.min(1000, dialog.parent ? dialog.parent.width - 40 : 1000); height: Math.min(820, dialog.parent ? dialog.parent.height - 40 : 820)
        x: Math.round((dialog.width - width) / 2); y: Math.round((dialog.height - height) / 2); padding: 14; background: DialogFrame {}
        contentItem: ColumnLayout {
            spacing: 10
            RowLayout {
                Layout.fillWidth: true
                SectionHeading { Layout.fillWidth: true; text: qsTr("完整截图 · %1").arg(dialog.sourceName(dialog.evidence.source_image)); elide: Text.ElideMiddle }
                AppButton { objectName: "importImageZoomOut"; text: qsTr("缩小"); compact: true; enabled: imageDialog.zoom > 0.25; onClicked: imageDialog.zoom = Math.max(0.25, imageDialog.zoom - 0.25) }
                Text { textFormat: Text.PlainText; text: Math.round(imageDialog.zoom * 100) + "%"; color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                AppButton { objectName: "importImageZoomIn"; text: qsTr("放大"); compact: true; enabled: imageDialog.zoom < 3; onClicked: imageDialog.zoom = Math.min(3, imageDialog.zoom + 0.25) }
                AppButton { text: qsTr("100%"); compact: true; onClicked: imageDialog.zoom = 1 }
                AppButton { text: qsTr("关闭"); compact: true; onClicked: imageDialog.close() }
            }
            Flickable {
                id: fullImageViewport
                objectName: "importWholeImageViewport"
                Layout.fillWidth: true; Layout.fillHeight: true; clip: true
                contentWidth: Math.max(width, fullImage.width); contentHeight: Math.max(height, fullImage.height)
                ScrollBar.vertical: ScrollBar {} ScrollBar.horizontal: ScrollBar {}
                Image { id: fullImage; objectName: "importWholeImage"; autoTransform: true; source: dialog.evidence.source_url || ""; width: implicitWidth * imageDialog.zoom; height: implicitHeight * imageDialog.zoom; x: Math.max(0, (fullImageViewport.width - width) / 2); asynchronous: true; cache: false }
            }
            Text { textFormat: Text.PlainText; Layout.fillWidth: true; text: qsTr("拖动或滚动查看截图，关闭后继续核对当前记录。"); color: Theme.textSecondary; font.pixelSize: Theme.fs(11) }
        }
    }
}
