pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

MotionDialog {
    id: dialog
    objectName: "reflectionShareDialog"
    property var controller: typeof Runtime.reflectionShare !== "undefined" ? Runtime.reflectionShare : null
    property var runData: ({})
    property var runsData: []
    property var manualOrder: []
    readonly property int manualOrderIndex: 2
    readonly property var reflection: runData.reflection || ({})
    readonly property bool busy: controller && controller.busy
    readonly property bool batch: runsData.length > 1
    readonly property bool collection: batch && outputMode.currentIndex === 1
    property int dragFrom: -1
    property int dropIndex: -1
    property real dragY: 0
    /// The rendered rows a grip stands beside, in the order they are saved.
    readonly property list<Item> gripEntries: !batch ? [] : collection ? combined.collectionEntries : stack.previewEntries
    property string exportDate: ""
    readonly property var preferences: typeof Runtime.settings !== "undefined" ? Runtime.settings : null
    readonly property bool identityValid: identityInput.text.trim().length === 0
        || /^[^@\s]+(?: [^@\s]+)*@[^@\s]+(?: [^@\s]+)*$/.test(identityInput.text.trim())
    modal: true
    width: Math.min(800, Overlay.overlay ? Overlay.overlay.width - 48 : 800)
    height: Math.min(850, Overlay.overlay ? Overlay.overlay.height - 48 : 850)
    padding: 20
    closePolicy: busy ? Popup.NoAutoClose : Popup.CloseOnEscape
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    background: DialogFrame {}

    function openForRun(run) {
        openForRuns([run])
    }
    function openForRuns(runs) {
        if (visible || busy || !runs) return
        const valid = []
        const seen = ({})
        for (let i = 0; i < runs.length; ++i) {
            const run = runs[i]
            if (!run || !run.reflection || !run.reflection.text) continue
            if (run.run_id && seen[run.run_id]) continue
            if (run.run_id) seen[run.run_id] = true
            valid.push(run)
        }
        if (valid.length === 0) return
        // Start from the library's own order; the player can turn it here so a
        // long image for friends reads oldest first without a reminder.
        const library = typeof Runtime.reflections !== "undefined" ? Runtime.reflections : null
        orderMode.currentIndex = library && library.runs && library.runs.sortAscending ? 1 : 0
        manualOrder = []
        runsData = ordered(valid)
        runData = runsData[0]
        outputMode.currentIndex = 0
        attribution.checked = true
        identityOption.checked = true
        identityInput.text = preferences ? preferences.shareGameIdentity : ""
        exportDate = Qt.formatDate(new Date(), "yyyy.MM.dd")
        if (controller) controller.resetFeedback()
        open()
    }
    /// \a runs in the order the 排列顺序 choice names, by the records' dates.
    function ordered(runs) {
        const library = typeof Runtime.reflections !== "undefined" ? Runtime.reflections : null
        return library && runs.length > 1 ? library.orderedByDate(runs, orderMode.currentIndex === 1) : runs
    }
    function reorder() {
        runsData = orderMode.currentIndex === manualOrderIndex
            ? (manualOrder.length ? manualOrder : runsData) : ordered(runsData)
        runData = runsData[0]
        if (controller) controller.resetFeedback()
    }
    function moveRun(from: int, to: int): bool {
        if (!visible || exiting || busy || from === to || from < 0 || to < 0
                || from >= runsData.length || to >= runsData.length) return false
        const reordered = Array.from(runsData)
        reordered.splice(to, 0, reordered.splice(from, 1)[0])
        manualOrder = reordered
        orderMode.currentIndex = manualOrderIndex
        reorder()
        return true
    }
    /// Top of the row  entry occupies, in the coordinates of the preview's content.
    function rowTopOf(entry: Item): real {
        if (!entry) return 0
        return collection && entry.parent ? (entry.parent.y + entry.y) * combined.templateScale : entry.y
    }
    /// Height of that row, in the same coordinates.
    function rowHeightOf(entry: Item): real {
        return entry ? entry.height * (collection ? combined.templateScale : 1) : 0
    }
    function updateDropIndex() {
        const y = gripsLayer.mapFromItem(preview, 0, dragY).y
        let slot = 0
        for (; slot < gripEntries.length; ++slot) {
            const entry = gripEntries[slot]
            if (y < rowTopOf(entry) + rowHeightOf(entry) / 2) break
        }
        dropIndex = slot > dragFrom ? slot - 1 : slot
    }
    function finishDrag(commit: bool) {
        const from = dragFrom
        const to = dropIndex
        dragFrom = -1
        dropIndex = -1
        // Wait for the handler to release before changing the repeater's model.
        if (commit && from !== to) Qt.callLater(dialog.moveRun, from, to)
    }
    function captureEntries() {
        const entries = []
        for (let i = 0; i < cards.count; ++i)
            entries.push({ run_id: runsData[i].run_id, label: Theme.dutyName(runsData[i]) || qsTr("未知副本"), item: cards.itemAt(i) })
        return entries
    }
    function saveSelected() {
        if (!controller || busy || dragFrom >= 0 || (identityOption.checked && !identityValid)) return
        rememberIdentity()
        if (batch && outputMode.currentIndex === 0)
            controller.saveBatchPicked(captureEntries(), orderMode.currentIndex === manualOrderIndex)
        else
            controller.savePicked(batch ? combined : cards.itemAt(0))
    }
    function rememberIdentity() {
        if (preferences && identityValid) preferences.shareGameIdentity = identityInput.text.trim()
    }
    onAboutToHide: finishDrag(false)
    onClosed: rememberIdentity()
    onOpened: {
        preview.contentItem.contentY = 0
        if (batch) outputMode.forceActiveFocus()
        else saveButton.forceActiveFocus()
    }
    contentItem: ColumnLayout {
        spacing: 8
        RowLayout {
            Layout.fillWidth: true
            HeadingLabel {
                Layout.fillWidth: true
                text: qsTr("生成分享图片")
                font.pixelSize: Theme.dialogTitleSize(20)
                wrapMode: Text.WordWrap
            }
            AppButton {
                objectName: "reflectionShareCloseButton"
                Layout.alignment: Qt.AlignTop
                Layout.preferredWidth: 28
                Layout.preferredHeight: 28
                text: ""
                iconName: "x"
                compact: true
                variant: "ghost"
                enabled: !dialog.busy
                Accessible.name: qsTr("关闭")
                onClicked: dialog.close()
                Keys.onReturnPressed: clicked()
                Keys.onEnterPressed: clicked()
            }
        }
        Text {
            Layout.fillWidth: true
            text: dialog.batch ? qsTr("已选 %1 条。拖动预览右侧的把手排序，保存时使用当前顺序。").arg(dialog.runsData.length)
                              : qsTr("下方预览完整心得和副本、职业等信息。点击保存后，图片会保存到你选择的位置；如需分享，请自行发送图片。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        RowLayout {
            Layout.fillWidth: true
            visible: dialog.batch
            spacing: 8
            // Before 输出方式 so Tab from the output mode still lands on 显示游戏 ID.
            Text { text: qsTr("排列顺序"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
            StyledComboBox {
                id: orderMode
                objectName: "reflectionImageOrder"
                Layout.preferredWidth: 120
                model: [qsTr("最新在前"), qsTr("最早在前"), qsTr("手动排序")]
                enabled: !dialog.busy && dialog.dragFrom < 0
                onActivated: dialog.reorder()
            }
            Text { text: qsTr("输出方式"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
            StyledComboBox {
                id: outputMode
                objectName: "reflectionImageOutputMode"
                Layout.fillWidth: true
                model: [qsTr("逐条 PNG（每条一张，选择文件夹）"), qsTr("合成长图（按预览顺序保存一张）")]
                enabled: !dialog.busy && dialog.dragFrom < 0
                onActivated: if (dialog.controller) dialog.controller.resetFeedback()
            }
        }
        RowLayout {
            Layout.fillWidth: true
            spacing: 8
            SelectionCheckBox {
                id: identityOption
                objectName: "reflectionShareIdentityOption"
                text: qsTr("显示游戏 ID")
                checked: true
                enabled: !dialog.busy
                onToggled: if (dialog.controller) dialog.controller.resetFeedback()
            }
            StyledTextField {
                id: identityInput
                objectName: "reflectionShareIdentityInput"
                Layout.fillWidth: true
                placeholderText: qsTr("用户名@服务器名（仅保存在本机）")
                Accessible.name: qsTr("游戏 ID")
                maximumLength: 128
                enabled: !dialog.busy
                font.family: Theme.bodyFamily
                onEditingFinished: dialog.rememberIdentity()
                onTextEdited: if (dialog.controller) dialog.controller.resetFeedback()
            }
        }
        Text {
            Layout.fillWidth: true
            visible: identityOption.checked && !dialog.identityValid
            text: qsTr("请按“用户名@服务器名”填写，或关闭游戏 ID 显示。")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        SelectionCheckBox {
            id: attribution
            objectName: "reflectionShareAttributionOption"
            Layout.fillWidth: true
            text: qsTr("显示 Powered by 导随记录器 署名")
            checked: true
            enabled: !dialog.busy
            onToggled: if (dialog.controller) dialog.controller.resetFeedback()
        }
        ScrollView {
            id: preview
            objectName: "reflectionSharePreview"
            Layout.fillWidth: true
            Layout.fillHeight: true
            Layout.minimumHeight: 80
            // Rectangle 的显式 height 不会成为 ScrollView 的隐式内容高度。
            // 显式绑定完整卡片高度，让滚轮和滚动条都能抵达最后一条心得。
            Layout.preferredHeight: 320
            clip: true
            contentWidth: availableWidth
            contentHeight: dialog.collection ? combined.height : stack.height
            ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
            ScrollBar.vertical.policy: ScrollBar.AsNeeded
            Timer {
                interval: 16
                repeat: true
                running: dialog.dragFrom >= 0
                onTriggered: {
                    const margin = Math.min(40, preview.height / 3)
                    const y = dialog.dragY
                    const speed = y < margin ? Math.max(-24, (y - margin) / 2)
                        : y > preview.height - margin ? Math.min(24, (y - preview.height + margin) / 2) : 0
                    const flick = preview.contentItem as Flickable
                    if (speed === 0 || !flick) return
                    const next = Math.max(0, Math.min(preview.contentHeight - preview.height, flick.contentY + speed))
                    if (next === flick.contentY) return
                    flick.contentY = next
                    dialog.updateDropIndex()
                }
            }
            // Repeater 实例化每一条完整卡片；抓图对象为卡片/组合自身，绝不捕获可见视口。
            Item {
                width: preview.availableWidth
                height: dialog.collection ? combined.height : stack.height
                ReflectionShareCard {
                    id: combined
                    objectName: "reflectionShareCombinedCard"
                    width: parent.width
                    visible: dialog.collection
                    runsData: dialog.runsData
                    showAttribution: attribution.checked
                    gameIdentity: identityOption.checked ? identityInput.text.trim() : ""
                    exportDate: dialog.exportDate
                }
                Column {
                    id: stack
                    readonly property list<Item> previewEntries: children.filter(child => child !== cards)
                    objectName: "reflectionShareIndividualCards"
                    width: parent.width
                    visible: !dialog.collection
                    spacing: 12
                    Repeater {
                        id: cards
                        model: dialog.runsData
                        ReflectionShareCard {
                            required property var modelData
                            width: stack.width
                            runData: modelData
                            showAttribution: attribution.checked
                            gameIdentity: identityOption.checked ? identityInput.text.trim() : ""
                            exportDate: dialog.exportDate
                        }
                    }
                }
                // The grips and drop indicator are siblings of the rendered cards.
                Item {
                    id: gripsLayer
                    anchors.fill: parent
                    visible: dialog.batch
                    Repeater {
                        id: grips
                        model: dialog.gripEntries
                        Rectangle {
                            id: grip
                            required property int index
                            required property Item modelData
                            readonly property real rowTop: dialog.rowTopOf(modelData)
                            readonly property real rowHeight: dialog.rowHeightOf(modelData)
                            objectName: "reflectionShareDragGrip_" + index
                            anchors.right: parent.right
                            anchors.rightMargin: 8
                            y: rowTop + 8
                            width: 28
                            height: 28
                            radius: Theme.radiusS
                            enabled: !dialog.busy
                            activeFocusOnTab: true
                            color: drag.active || hover.hovered || activeFocus ? Theme.fillStrong : Theme.surfaceRaised
                            border.color: activeFocus || drag.active ? Theme.accentStrong : Theme.borderStrong
                            Accessible.role: Accessible.Button
                            Accessible.name: qsTr("拖动排序") + " · " + (Theme.dutyName(dialog.runsData[index]) || qsTr("未知副本"))
                            ToolTip.visible: hover.hovered && dialog.dragFrom < 0
                            ToolTip.delay: 500
                            ToolTip.text: qsTr("拖动排序，也可按 ↑ 或 ↓ 移动")
                            Image {
                                anchors.centerIn: parent
                                width: 16
                                height: 16
                                source: Lucide.source("grip-vertical", Theme.textPrimary)
                                Accessible.ignored: true
                            }
                            HoverHandler { id: hover; cursorShape: Qt.OpenHandCursor }
                            DragHandler {
                                id: drag
                                target: null
                                xAxis.enabled: false
                                cursorShape: Qt.ClosedHandCursor
                                grabPermissions: PointerHandler.CanTakeOverFromItems
                                                 | PointerHandler.CanTakeOverFromHandlersOfDifferentType
                                onTranslationChanged: {
                                    if (!active || dialog.dragFrom !== grip.index) return
                                    dialog.dragY = preview.mapFromItem(null, centroid.scenePosition.x, centroid.scenePosition.y).y
                                    dialog.updateDropIndex()
                                }
                                onActiveChanged: {
                                    if (active) {
                                        const flick = preview.contentItem as Flickable
                                        if (flick) flick.cancelFlick()
                                        dialog.dragFrom = grip.index
                                        dialog.dropIndex = grip.index
                                        dialog.dragY = preview.mapFromItem(null, centroid.scenePosition.x, centroid.scenePosition.y).y
                                        dialog.updateDropIndex()
                                    }
                                }
                                onGrabChanged: function(transition, point) {
                                    if (dialog.dragFrom !== grip.index) return
                                    if (transition === PointerDevice.UngrabExclusive) dialog.finishDrag(true)
                                    else if (transition === PointerDevice.CancelGrabExclusive) dialog.finishDrag(false)
                                }
                            }
                            function moveBy(step: int) {
                                const next = index + step
                                if (dialog.moveRun(index, next))
                                    Qt.callLater(function() { grips.itemAt(next)?.forceActiveFocus() })
                            }
                            Keys.onUpPressed: moveBy(-1)
                            Keys.onDownPressed: moveBy(1)
                            onActiveFocusChanged: {
                                const flick = preview.contentItem as Flickable
                                if (!activeFocus || !flick) return
                                flick.contentY = Math.max(y + height - preview.height, Math.min(y, flick.contentY))
                            }
                        }
                    }
                    Rectangle {
                        visible: dialog.dragFrom >= 0 && dialog.dropIndex !== dialog.dragFrom
                        x: 4
                        width: parent.width - 8
                        height: 3
                        radius: 1.5
                        color: Theme.accentStrong
                        y: {
                            if (!visible) return 0
                            const slot = dialog.dropIndex + (dialog.dropIndex > dialog.dragFrom ? 1 : 0)
                            const entries = dialog.gripEntries
                            const hasNext = slot < entries.length
                            if (slot <= 0) return hasNext ? dialog.rowTopOf(entries[slot]) : 0
                            const bottom = dialog.rowTopOf(entries[slot - 1]) + dialog.rowHeightOf(entries[slot - 1])
                            return hasNext ? (bottom + dialog.rowTopOf(entries[slot])) / 2 : bottom - height
                        }
                    }
                }
            }
        }
        Text {
            objectName: "reflectionShareFeedback"
            Layout.fillWidth: true
            visible: text.length > 0
            text: dialog.controller ? dialog.controller.feedback : qsTr("暂时无法保存图片，请关闭后重新打开。")
            textFormat: Text.PlainText
            color: dialog.controller && dialog.controller.failedCount > 0 ? Theme.orangeText
                : dialog.controller && (dialog.controller.savedPath.length > 0 || dialog.controller.successfulCount > 0) ? Theme.green : Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        ScrollView {
            id: batchResultsView
            objectName: "reflectionBatchResults"
            Layout.fillWidth: true
            Layout.minimumHeight: 32
            Layout.preferredHeight: Math.min(110, resultContents.implicitHeight)
            visible: !!dialog.controller && dialog.controller.batchResults.length > 0
            clip: true
            contentWidth: availableWidth
            ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
            Column {
                id: resultContents
                width: batchResultsView.availableWidth
                spacing: 8
                Repeater {
                    model: dialog.controller ? dialog.controller.batchResults : []
                    Text {
                        required property var modelData
                        width: resultContents.width
                        text: (modelData.label || qsTr("未知副本")) + " · "
                            + (modelData.status === "saved" ? qsTr("已保存：%1").arg(modelData.path)
                               : modelData.status === "failed" ? qsTr("失败：%1").arg(modelData.error) : qsTr("等待生成"))
                        textFormat: Text.PlainText
                        color: modelData.status === "failed" ? Theme.red : Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WrapAnywhere
                    }
                }
            }
        }
        RowLayout {
            Layout.fillWidth: true
            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "retryFailedReflectionImagesButton"
                visible: !!dialog.controller && dialog.controller.failedCount > 0
                text: qsTr("仅重试失败项")
                enabled: !dialog.busy
                onClicked: dialog.controller.retryFailed()
            }
            AppButton {
                id: saveButton
                objectName: "saveReflectionImageButton"
                text: dialog.busy ? qsTr("正在保存…")
                    : dialog.batch && outputMode.currentIndex === 0 ? qsTr("保存逐条 PNG")
                    : dialog.batch ? qsTr("保存合成长图") : qsTr("保存 PNG 图片")
                iconName: "image-down"
                variant: "primary"
                enabled: !!dialog.controller && !dialog.busy && dialog.dragFrom < 0
                    && (!identityOption.checked || dialog.identityValid)
                onClicked: dialog.saveSelected()
            }
        }
    }
}
