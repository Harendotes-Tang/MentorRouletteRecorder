import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Dialog {
    id: dialog
    objectName: "reflectionShareDialog"
    property var controller: typeof ReflectionShare !== "undefined" ? ReflectionShare : null
    property var runData: ({})
    property var runsData: []
    readonly property var reflection: runData.reflection || ({})
    readonly property bool busy: controller && controller.busy
    readonly property bool batch: runsData.length > 1
    readonly property bool collection: batch && outputMode.currentIndex === 1
    property string exportDate: ""
    readonly property var preferences: typeof Settings !== "undefined" ? Settings : null
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
        runsData = valid
        runData = valid[0]
        outputMode.currentIndex = 0
        attribution.checked = true
        identityOption.checked = true
        identityInput.text = preferences ? preferences.shareGameIdentity : ""
        exportDate = Qt.formatDate(new Date(), "yyyy.MM.dd")
        if (controller) controller.resetFeedback()
        open()
    }
    function captureEntries() {
        const entries = []
        for (let i = 0; i < cards.count; ++i)
            entries.push({ run_id: runsData[i].run_id, label: Theme.dutyName(runsData[i]) || qsTr("未知副本"), item: cards.itemAt(i) })
        return entries
    }
    function saveSelected() {
        if (!controller || busy || (identityOption.checked && !identityValid)) return
        rememberIdentity()
        if (batch && outputMode.currentIndex === 0)
            controller.saveBatchPicked(captureEntries())
        else
            controller.savePicked(batch ? combined : cards.itemAt(0))
    }
    function rememberIdentity() {
        if (preferences && identityValid) preferences.shareGameIdentity = identityInput.text.trim()
    }
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
            text: dialog.batch ? qsTr("已选 %1 条。下方按选择顺序预览完整心得及副本、职业等信息。点击保存后，图片会保存到你选择的位置；如需分享，请自行发送图片。").arg(dialog.runsData.length)
                              : qsTr("下方预览完整心得和副本、职业等信息。点击保存后，图片会保存到你选择的位置；如需分享，请自行发送图片。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        RowLayout {
            Layout.fillWidth: true
            visible: dialog.batch
            spacing: 8
            Text { text: qsTr("输出方式"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
            StyledComboBox {
                id: outputMode
                objectName: "reflectionImageOutputMode"
                Layout.fillWidth: true
                model: [qsTr("逐条 PNG（每条一张，选择文件夹）"), qsTr("合成长图（按预览顺序保存一张）")]
                enabled: !dialog.busy
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
            }
        }
        Text {
            objectName: "reflectionShareFeedback"
            Layout.fillWidth: true
            visible: text.length > 0
            text: controller ? controller.feedback : qsTr("暂时无法保存图片，请关闭后重新打开。")
            textFormat: Text.PlainText
            color: controller && controller.failedCount > 0 ? Theme.orangeText
                : controller && (controller.savedPath.length > 0 || controller.successfulCount > 0) ? Theme.green : Theme.textSecondary
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
                enabled: !!dialog.controller && !dialog.busy && (!identityOption.checked || dialog.identityValid)
                onClicked: dialog.saveSelected()
            }
        }
    }
}
