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
    // The drag as it is drawn (2026-11-19, after the task editor of another of the
    // owner's programs): the row that is picked up stays behind as a faint
    // placeholder, a slightly smaller copy of it follows the pointer, the rows in
    // between slide out of its way, and on release the copy glides into the gap
    // before the order really changes.
    /// The pointer, in the coordinates of the preview's content.
    property real dragLayerY: 0
    /// Pointer minus the top of the row when it was picked up.
    property real dragGrabOffset: 0
    property real dragLeft: 0
    property real dragWidth: 0
    /// The copy is on its way into the gap; nothing else can be picked up.
    property bool dragSettling: false
    /// Rows slide while a drag is under way and jump back when it ends: by then
    /// the new order puts every row where the slide had left it.
    property bool dragAnimated: false
    readonly property int dragDuration: Theme.motion ? 160 : 0
    /// The hovered plate of a row's buttons: the resting plate tinted, still opaque.
    readonly property color gripHoverColor: Qt.tint(Theme.surfaceRaised, Theme.fillStrong)
    /// The rendered rows a grip stands beside, in the order they are saved.
    readonly property list<Item> gripEntries: !batch ? [] : collection ? combined.collectionEntries : stack.previewEntries
    property string exportDate: ""
    readonly property var preferences: typeof Runtime.settings !== "undefined" ? Runtime.settings : null
    readonly property bool identityValid: identityInput.text.trim().length === 0
        || /^[^@\s]+(?: [^@\s]+)*@[^@\s]+(?: [^@\s]+)*$/.test(identityInput.text.trim())
    /// Hosted by ReflectionShareWindow: the dialog is the whole content of a window
    /// of its own, which the player can resize and maximise (owner, 2026-11-19).
    /// The window has the title and the close button; nothing is dimmed behind it.
    property bool windowed: false
    /// Cards are no wider than this in a window, however wide it is made: a taller
    /// window then shows more of them, not the same ones larger.
    readonly property int cardLimit: 900
    modal: !windowed
    dim: !windowed
    width: windowed ? (Overlay.overlay ? Overlay.overlay.width : 800)
                    : Math.min(800, Overlay.overlay ? Overlay.overlay.width - 48 : 800)
    height: windowed ? (Overlay.overlay ? Overlay.overlay.height : 850)
                     : Math.min(850, Overlay.overlay ? Overlay.overlay.height - 48 : 850)
    padding: 20
    closePolicy: busy ? Popup.NoAutoClose : Popup.CloseOnEscape
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    background: Loader {
        sourceComponent: dialog.windowed ? flatBackground : framedBackground
        Component { id: framedBackground; DialogFrame {} }
        Component { id: flatBackground; Rectangle { color: Theme.surface } }
    }

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
        exportDate = typeof Runtime.fmt !== "undefined" && Runtime.fmt
                     ? Runtime.fmt.todayLocal().replace(/-/g, ".") : Qt.formatDate(new Date(), "yyyy.MM.dd")
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
        // A drag under way holds row indexes into this order: nothing else may change it
        // until the drag has ended (audit 2026-11-20 L2-1). completeDrag ends it first.
        if (!visible || exiting || busy || dragFrom >= 0 || from === to || from < 0 || to < 0
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
    /// The space between two rows.
    function rowGap(): real {
        return gripEntries.length > 1
            ? rowTopOf(gripEntries[1]) - rowTopOf(gripEntries[0]) - rowHeightOf(gripEntries[0]) : 0
    }
    /// How far the row at  index is moved out of its place by the drag under way:
    /// the picked-up row to the slot it would be dropped in, the rows between
    /// that slot and its old one by its height the other way.
    function rowShift(index: int): real {
        const entries = gripEntries
        if (dragFrom < 0 || dropIndex < 0 || dragFrom >= entries.length || dropIndex >= entries.length
                || dropIndex === dragFrom) return 0
        const moved = entries[dragFrom]
        if (index === dragFrom) {
            const target = entries[dropIndex]
            return dropIndex > dragFrom
                ? rowTopOf(target) + rowHeightOf(target) - rowHeightOf(moved) - rowTopOf(moved)
                : rowTopOf(target) - rowTopOf(moved)
        }
        const pitch = rowHeightOf(moved) + rowGap()
        if (dragFrom < index && index <= dropIndex) return -pitch
        if (dropIndex <= index && index < dragFrom) return pitch
        return 0
    }
    function beginDrag(index: int, sceneX: real, sceneY: real) {
        if (dragFrom >= 0 || index < 0 || index >= gripEntries.length) return
        const entry = gripEntries[index]
        const box = gripsLayer.mapFromItem(entry, 0, 0, entry.width, entry.height)
        dragLeft = box.x
        dragWidth = box.width
        dragFrom = index
        dropIndex = index
        dragY = preview.mapFromItem(null, sceneX, sceneY).y
        dragLayerY = gripsLayer.mapFromItem(preview, 0, dragY).y
        dragGrabOffset = dragLayerY - rowTopOf(entry)
        dragAnimated = true
        ghost.lift = 1
        lift.restart()
        updateDropIndex()
    }
    function updateDropIndex() {
        const y = gripsLayer.mapFromItem(preview, 0, dragY).y
        dragLayerY = y
        let slot = 0
        for (; slot < gripEntries.length; ++slot) {
            const entry = gripEntries[slot]
            if (y < rowTopOf(entry) + rowHeightOf(entry) / 2) break
        }
        dropIndex = slot > dragFrom ? slot - 1 : slot
    }
    /// The pointer let go: the copy glides into the gap (back to where the row
    /// came from when the drag was cancelled), then the order changes.
    function finishDrag(commit: bool) {
        if (dragFrom < 0 || dragSettling) return
        if (!commit) dropIndex = dragFrom
        lift.stop()
        ghost.restY = ghost.y
        settle.landing = rowTopOf(gripEntries[dragFrom]) + rowShift(dragFrom)
        dragSettling = true
        settle.restart()
    }
    function completeDrag() {
        const from = dragFrom
        const to = dropIndex
        endDrag()
        if (from < 0 || from === to) return
        // Without motion this runs inside the handler's own release: wait for it
        // before changing the repeater's model. With motion the handler let go
        // long ago, and waiting would paint one frame in the old order.
        if (dragDuration > 0) moveRun(from, to)
        else Qt.callLater(dialog.moveRun, from, to)
    }
    /// Forget the drag at once, every row back in its own place.
    function endDrag() {
        settle.stop()
        lift.stop()
        dragAnimated = false
        dragSettling = false
        dragFrom = -1
        dropIndex = -1
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
    onAboutToHide: endDrag()
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
            visible: !dialog.windowed
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
                running: dialog.dragFrom >= 0 && !dialog.dragSettling
                onTriggered: {
                    // A card is taller than the edge it is dragged to: a wide edge
                    // zone and a quick top speed, or a long list takes for ever.
                    const margin = Math.min(72, preview.height / 3)
                    const y = dialog.dragY
                    const speed = y < margin ? Math.max(-40, (y - margin) / 2)
                        : y > preview.height - margin ? Math.min(40, (y - preview.height + margin) / 2) : 0
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
                id: sheet
                readonly property real cardWidth: dialog.windowed ? Math.min(width, dialog.cardLimit) : width
                readonly property real cardLeft: Math.round((width - cardWidth) / 2)
                width: preview.availableWidth
                height: dialog.collection ? combined.height : stack.height
                ReflectionShareCard {
                    id: combined
                    objectName: "reflectionShareCombinedCard"
                    x: sheet.cardLeft
                    width: sheet.cardWidth
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
                    x: sheet.cardLeft
                    width: sheet.cardWidth
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
                    x: sheet.cardLeft
                    width: sheet.cardWidth
                    height: parent.height
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
                            /// Where the drag under way wants this row, and where it is drawn.
                            readonly property real shift: dialog.rowShift(index)
                            property real slide: shift
                            Behavior on slide {
                                enabled: dialog.dragAnimated
                                NumberAnimation { duration: dialog.dragDuration; easing.type: Easing.InOutCubic }
                            }
                            objectName: "reflectionShareDragGrip_" + index
                            anchors.right: parent.right
                            anchors.rightMargin: 8
                            y: rowTop + slide + 8
                            width: 28
                            height: 28
                            radius: Theme.radiusS
                            enabled: !dialog.busy && !dialog.dragSettling
                            // The row itself is moved and dimmed from here: it belongs to
                            // the card that is saved, which knows nothing about dragging.
                            // Saving waits for the drag to end, when both are undone.
                            Translate {
                                id: rowMove
                                y: grip.slide / (dialog.collection ? combined.templateScale : 1)
                            }
                            Component.onCompleted: modelData.transform = [rowMove]
                            Binding {
                                target: grip.modelData
                                property: "opacity"
                                value: 0.25
                                when: dialog.dragFrom === grip.index
                                restoreMode: Binding.RestoreBindingOrValue
                            }
                            activeFocusOnTab: true
                            // Over a card, not over the window: the plate stays opaque when
                            // hovered, or the icon is lost against the parchment.
                            color: drag.active || hover.hovered || activeFocus ? dialog.gripHoverColor : Theme.surfaceRaised
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
                            // 移到最前 / 移到最后: one click where a drag would have to
                            // cross the whole list (owner, 2026-11-19).
                            Repeater {
                                model: [{ toEnd: false, label: qsTr("移到最前") }, { toEnd: true, label: qsTr("移到最后") }]
                                Rectangle {
                                    id: jump
                                    required property var modelData
                                    required property int index
                                    readonly property int target: modelData.toEnd ? dialog.runsData.length - 1 : 0
                                    objectName: (modelData.toEnd ? "reflectionShareMoveLast_" : "reflectionShareMoveFirst_") + grip.index
                                    visible: grip.index !== target && dialog.dragFrom < 0
                                    x: (index - 2) * 32
                                    width: 28
                                    height: 28
                                    radius: Theme.radiusS
                                    color: jumpHover.hovered ? dialog.gripHoverColor : Theme.surfaceRaised
                                    border.color: Theme.borderStrong
                                    Accessible.role: Accessible.Button
                                    Accessible.name: modelData.label
                                    ToolTip.visible: jumpHover.hovered
                                    ToolTip.delay: 500
                                    ToolTip.text: modelData.label
                                    Image {
                                        anchors.centerIn: parent
                                        width: 16
                                        height: 16
                                        rotation: jump.modelData.toEnd ? 0 : 180
                                        source: Lucide.source("chevrons-down", Theme.textPrimary)
                                        Accessible.ignored: true
                                    }
                                    HoverHandler { id: jumpHover; cursorShape: Qt.PointingHandCursor }
                                    TapHandler {
                                        enabled: !dialog.busy
                                        // The model changes after this handler has let go.
                                        onTapped: Qt.callLater(dialog.moveRun, grip.index, jump.target)
                                    }
                                }
                            }
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
                                        dialog.beginDrag(grip.index, centroid.scenePosition.x, centroid.scenePosition.y)
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
                    // Collection entries share the sheet's paper. Give the lifted
                    // row its own opaque backing as it crosses other entries.
                    Rectangle {
                        id: ghost
                        objectName: "reflectionShareDragGhost"
                        readonly property Item row: dialog.dragFrom >= 0 && dialog.dragFrom < dialog.gripEntries.length
                                                    ? dialog.gripEntries[dialog.dragFrom] : null
                        /// 1 at rest, a little smaller while carried.
                        property real lift: 1
                        /// Where the copy is while it glides into the gap.
                        property real restY: 0
                        visible: row !== null
                        color: combined.paperColor
                        clip: true
                        z: 5
                        x: dialog.dragLeft
                        width: dialog.dragWidth
                        height: dialog.rowHeightOf(row)
                        y: dialog.dragSettling ? restY : dialog.dragLayerY - dialog.dragGrabOffset
                        // Shrinks towards the grip, so the pointer keeps hold of it.
                        transform: Scale {
                            origin.x: ghost.width - 22
                            origin.y: dialog.dragGrabOffset
                            xScale: ghost.lift
                            yScale: ghost.lift
                        }
                        ShaderEffectSource {
                            anchors.fill: parent
                            sourceItem: ghost.row
                            live: false
                        }
                        Rectangle {
                            anchors.fill: parent
                            color: "transparent"
                            radius: Theme.radiusS
                            border.width: 2
                            border.color: Theme.accentStrong
                        }
                    }
                    NumberAnimation {
                        id: lift
                        target: ghost
                        property: "lift"
                        to: 0.94
                        duration: Theme.motion ? 150 : 0
                        easing.type: Easing.OutQuad
                    }
                    SequentialAnimation {
                        id: settle
                        property real landing: 0
                        ParallelAnimation {
                            NumberAnimation {
                                target: ghost; property: "restY"; to: settle.landing
                                duration: dialog.dragDuration; easing.type: Easing.InOutCubic
                            }
                            NumberAnimation {
                                target: ghost; property: "lift"; to: 1
                                duration: dialog.dragDuration; easing.type: Easing.InOutCubic
                            }
                        }
                        ScriptAction { script: dialog.completeDrag() }
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
