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
        if (controller) controller.resetFeedback()
        open()
    }
    function captureEntries() {
        const entries = []
        for (let i = 0; i < cards.count; ++i)
            entries.push({ run_id: runsData[i].run_id, label: runsData[i].duty_name || qsTr("未知副本"), item: cards.itemAt(i) })
        return entries
    }
    function saveSelected() {
        if (!controller || busy) return
        if (batch && outputMode.currentIndex === 0)
            controller.saveBatchPicked(captureEntries())
        else
            controller.savePicked(batch ? combined : cards.itemAt(0))
    }
    onOpened: {
        if (batch) outputMode.forceActiveFocus()
        else saveButton.forceActiveFocus()
    }
    contentItem: ColumnLayout {
        spacing: 12
        HeadingLabel { text: qsTr("生成分享图片"); font.pixelSize: Theme.dialogTitleSize(20) }
        Text {
            Layout.fillWidth: true
            text: dialog.batch ? qsTr("已选 %1 条。预览按选择顺序包含完整心得和元数据，图片仅保存到你选择的位置。").arg(dialog.runsData.length)
                              : qsTr("预览包含完整心得和副本、职业等信息。图片仅保存到你选择的位置。")
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
        ScrollView {
            id: preview
            objectName: "reflectionSharePreview"
            Layout.fillWidth: true
            Layout.fillHeight: true
            clip: true
            contentWidth: availableWidth
            ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
            // Repeater 实例化每一条完整卡片；抓图对象为卡片/组合自身，绝不捕获可见视口。
            Rectangle {
                id: combined
                objectName: "reflectionShareCombinedCard"
                width: preview.availableWidth
                height: stack.implicitHeight
                color: Theme.surface
                Column {
                    id: stack
                    width: parent.width
                    spacing: 12
                    Repeater {
                        id: cards
                        model: dialog.runsData
                        ReflectionShareCard {
                            required property var modelData
                            width: stack.width
                            runData: modelData
                        }
                    }
                }
            }
        }
        Text {
            objectName: "reflectionShareFeedback"
            Layout.fillWidth: true
            visible: text.length > 0
            text: controller ? controller.feedback : qsTr("分享服务未就绪。")
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
            AppButton { text: qsTr("关闭"); enabled: !dialog.busy; onClicked: dialog.close() }
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
                enabled: !!dialog.controller && !dialog.busy
                onClicked: dialog.saveSelected()
            }
        }
    }
}
