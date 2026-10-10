import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 单条和批量输出共用完整卡片，正文和元数据始终按纯文本完整换行。
Rectangle {
    id: card
    objectName: "reflectionShareCard"
    property var runData: ({})
    readonly property var reflection: runData.reflection || ({})
    height: shareContents.implicitHeight + 64
    color: Theme.surface
    border.width: 1
    border.color: Theme.border

    function dateLabel() {
        if (runData.matched_at_utc)
            return qsTr("游玩时间：%1").arg(Fmt.localDateTime(runData.matched_at_utc))
        const source = runData.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站记录时间：%1 · 游玩时间未知").arg(Fmt.localDateTime(source.source_recorded_at_utc))
        if (source.source_recorded_at)
            return qsTr("原站记录时间：%1 · 游玩时间未知").arg(source.source_recorded_at)
        return qsTr("游玩时间未知")
    }

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
            JobIcon { jobId: card.runData.job_id; size: 38 }
            Text {
                Layout.fillWidth: true
                text: card.runData.duty_name || qsTr("未知副本")
                textFormat: Text.PlainText
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(22)
                font.bold: true
                wrapMode: Text.Wrap
            }
        }
        Text {
            Layout.fillWidth: true
            text: (card.runData.job_name || qsTr("未知职业")) + " · " + Fmt.resultLabel(card.runData.result || "UNKNOWN")
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(14)
            wrapMode: Text.Wrap
        }
        Text {
            Layout.fillWidth: true
            text: card.dateLabel()
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.Wrap
        }
        Text {
            text: Theme.moodLabel(card.reflection.mood)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(13)
        }
        Rectangle { Layout.fillWidth: true; height: 1; color: Theme.border }
        Text {
            objectName: "reflectionShareFullText"
            Layout.fillWidth: true
            text: card.reflection.text || ""
            textFormat: Text.PlainText
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(18)
            wrapMode: Text.Wrap
            lineHeight: 1.55
        }
        Text { text: qsTr("导随记录"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
    }
}
