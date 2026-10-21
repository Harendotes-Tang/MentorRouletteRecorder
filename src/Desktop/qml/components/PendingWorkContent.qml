import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

ColumnLayout {
    id: content
    property int limit: 0
    property bool prominentHeader: false
    signal chosen()
    spacing: 10
    readonly property var recovery: typeof App !== "undefined" && App.recovery ? App.recovery : null
    Text {
        objectName: "pendingWorkTitle"
        Layout.fillWidth: true
        text: qsTr("待处理 · %1").arg(content.recovery ? content.recovery.count : 0)
        color: content.prominentHeader ? Theme.textPrimary : Theme.textSecondary
        font.pixelSize: Theme.fs(content.prominentHeader ? 14 : 12)
        font.bold: content.prominentHeader
    }
    Text {
        Layout.fillWidth: true
        visible: !!content.recovery && content.recovery.busy
        text: qsTr("正在读取待处理记录…")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
    }
    Text {
        Layout.fillWidth: true
        visible: !!content.recovery && content.recovery.error.length > 0
        text: content.recovery ? content.recovery.error : ""
        wrapMode: Text.WordWrap
        color: Theme.red
        font.pixelSize: Theme.fs(12)
    }
    Repeater {
        model: content.recovery ? (content.limit > 0 ? content.recovery.items.slice(0, content.limit) : content.recovery.items) : []
        delegate: ColumnLayout {
            required property var modelData
            Layout.fillWidth: true
            spacing: 3
            Text {
                Layout.fillWidth: true
                text: Theme.dutyName(modelData) || qsTr("未知副本")
                textFormat: Text.PlainText
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                wrapMode: Text.WordWrap
            }
            Text {
                Layout.fillWidth: true
                text: [Theme.jobName(modelData) || qsTr("未知职业"),
                       Fmt.localDateTime(modelData.ended_at_utc || modelData.matched_at_utc)].join(" · ")
                textFormat: Text.PlainText
                wrapMode: Text.WordWrap
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
            }
            RowLayout {
                Layout.fillWidth: true
                Text {
                    Layout.fillWidth: true
                    text: [modelData.needsResult ? qsTr("待复核") : "",
                           modelData.needsReflection ? qsTr("待补笔记") : "",
                           modelData.hasDraft ? qsTr("有草稿") : ""].filter(function(t) { return t.length > 0 }).join(" · ")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    wrapMode: Text.WordWrap
                }
                // A record that is gone, or could not be read, can always be taken
                // off the list. A kept draft is deleted with it, so that asks once
                // more (review I2-1).
                AppButton {
                    id: dismissButton
                    property bool confirming: false
                    objectName: "dismissPendingWork_" + (modelData.runId || modelData.run_id)
                    visible: modelData.recoveryUnavailable === true
                    text: confirming ? qsTr("确认移除（草稿一并删除）") : qsTr("移除提醒")
                    variant: "ghost"
                    compact: true
                    enabled: !content.recovery.busy
                    onClicked: {
                        if (modelData.hasDraft && !confirming) {
                            confirming = true
                            return
                        }
                        content.recovery.dismiss(modelData.runId || modelData.run_id)
                    }
                }
                AppButton {
                    objectName: "openPendingWork_" + (modelData.runId || modelData.run_id)
                    text: qsTr("继续处理")
                    compact: true
                    enabled: !content.recovery.busy
                    onClicked: {
                        const scope = content.recovery.databaseKey
                        const runId = modelData.runId || modelData.run_id
                        content.chosen()
                        Qt.callLater(function() {
                            if (scope === content.recovery.databaseKey)
                                content.recovery.requestOpen(runId)
                        })
                    }
                }
            }
        }
    }
    Text {
        Layout.fillWidth: true
        visible: !!content.recovery && !content.recovery.busy && content.recovery.count === 0
                 && content.recovery.error.length === 0
        text: qsTr("没有待处理的记录。关闭提醒不会丢失记录，之后可从这里继续。")
        color: Theme.textSecondary
        wrapMode: Text.WordWrap
        font.pixelSize: Theme.fs(12)
    }
    AppButton {
        objectName: "pendingWorkAllButton"
        visible: content.limit > 0 && !!content.recovery
                 && (content.recovery.count > content.limit || content.recovery.error.length > 0)
        text: qsTr("查看全部待处理")
        compact: true
        onClicked: content.recovery.requestInbox()
    }
}
