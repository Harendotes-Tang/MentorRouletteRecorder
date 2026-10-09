import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Dialog {
    id: dialog
    objectName: "reflectionShareDialog"
    property var controller: typeof ReflectionShare !== "undefined" ? ReflectionShare : null
    property var runData: ({})
    readonly property var reflection: runData.reflection || ({})
    readonly property bool busy: controller && controller.busy
    modal: true
    width: Math.min(800, Overlay.overlay ? Overlay.overlay.width - 48 : 800)
    height: Math.min(850, Overlay.overlay ? Overlay.overlay.height - 48 : 850)
    padding: 20
    closePolicy: busy ? Popup.NoAutoClose : Popup.CloseOnEscape
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    background: DialogFrame {}

    function openForRun(run) {
        if (visible || busy || !run || !run.reflection || !run.reflection.text) return
        runData = run
        if (controller) controller.resetFeedback()
        open()
    }
    function dateLabel() {
        if (runData.matched_at_utc)
            return qsTr("游玩时间：%1").arg(Fmt.localDateTime(runData.matched_at_utc))
        const source = runData.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站记录时间：%1").arg(Fmt.localDateTime(source.source_recorded_at_utc))
        if (source.source_recorded_at)
            return qsTr("原站记录时间：%1").arg(source.source_recorded_at)
        return qsTr("游玩时间未知")
    }
    contentItem: ColumnLayout {
        spacing: 12
        HeadingLabel { text: qsTr("生成分享图片"); font.pixelSize: Theme.dialogTitleSize(20) }
        Text {
            Layout.fillWidth: true
            text: qsTr("预览包含完整心得和副本、职业等信息。图片仅保存到你选择的位置。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        ScrollView {
            id: preview
            objectName: "reflectionSharePreview"
            Layout.fillWidth: true
            Layout.fillHeight: true
            clip: true
            contentWidth: availableWidth
            ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
            // 捕获卡片自身而不是 ScrollView 的可见视口，长正文不会被分页裁切。
            Rectangle {
                id: card
                objectName: "reflectionShareCard"
                width: preview.availableWidth
                height: shareContents.implicitHeight + 64
                color: Theme.surface
                border.width: 1
                border.color: Theme.border
                ColumnLayout {
                    id: shareContents
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.margins: 32
                    spacing: 16
                    Text { text: qsTr("导随心得"); color: Theme.textSecondary; font.pixelSize: Theme.fs(13) }
                    RowLayout {
                        Layout.fillWidth: true
                        JobIcon { jobId: dialog.runData.job_id; size: 38 }
                        Text {
                            Layout.fillWidth: true
                            text: dialog.runData.duty_name || qsTr("未知副本")
                            textFormat: Text.PlainText
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(22)
                            font.bold: true
                            wrapMode: Text.Wrap
                        }
                    }
                    Text {
                        Layout.fillWidth: true
                        text: (dialog.runData.job_name || qsTr("未知职业")) + " · " + Fmt.resultLabel(dialog.runData.result || "UNKNOWN")
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(14)
                        wrapMode: Text.Wrap
                    }
                    Text {
                        Layout.fillWidth: true
                        text: dialog.dateLabel()
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                        wrapMode: Text.Wrap
                    }
                    Text {
                        text: Theme.moodLabel(dialog.reflection.mood)
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                    }
                    Rectangle { Layout.fillWidth: true; height: 1; color: Theme.border }
                    Text {
                        objectName: "reflectionShareFullText"
                        Layout.fillWidth: true
                        text: dialog.reflection.text || ""
                        textFormat: Text.PlainText
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(18)
                        wrapMode: Text.Wrap
                        lineHeight: 1.55
                    }
                    Text { text: qsTr("导随记录"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                }
            }
        }
        Text {
            objectName: "reflectionShareFeedback"
            Layout.fillWidth: true
            visible: text.length > 0
            text: controller ? controller.feedback : qsTr("分享服务未就绪。")
            textFormat: Text.PlainText
            color: controller && controller.savedPath.length > 0 ? Theme.green : Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        RowLayout {
            Layout.fillWidth: true
            AppButton { text: qsTr("关闭"); enabled: !dialog.busy; onClicked: dialog.close() }
            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "saveReflectionImageButton"
                text: dialog.busy ? qsTr("正在保存…") : qsTr("保存 PNG 图片")
                iconName: "file-down"
                variant: "primary"
                enabled: !!dialog.controller && !dialog.busy
                onClicked: dialog.controller.savePicked(card)
            }
        }
    }
}
