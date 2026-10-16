import QtQuick
import MentorRecorder

// 来源时间与真实游玩时间保持区分；仅投影记录，不推断职业或通关结果。
Item {
    id: entry
    objectName: "reflectionShareEntry"
    property var runData: ({})
    property bool single: false
    property int entryIndex: 0
    property bool showYear: false
    readonly property var reflection: runData.reflection || ({})
    readonly property bool completed: runData.result === "COMPLETED"
    readonly property string assetRoot: "qrc:/resources/reflection-share/duty-notes-scroll-v1.4.1/"
    readonly property real dateShift: single ? Math.max(0, timeText.lineCount - 1) * 30 : 0
    readonly property real titleShift: {
        const shift = single ? dateShift + (dutyTitle.lineCount > 1 ? 18 + Math.max(0, dutyTitle.lineCount - 2) * 40 : 0)
            : Math.max(0, dutyTitle.lineCount - 1) * 48
        // 标注仅定义到两行；更长标题保留全文，并让属性离开整个 Qt 文本布局框。
        return single && dutyTitle.lineCount > 2
            ? Math.max(shift, dutyTitle.y + dutyTitle.height + 4 - 192 + resultText.baselineOffset) : shift
    }
    readonly property real dividerY: Math.max(238, metadataBottom + 20)
    readonly property real metadataBottom: Math.max((single ? 200 : 77) + titleShift, mood.y + mood.height)
    readonly property real bodyTop: single ? dividerY + 30
        : Math.max(92 + titleShift, metadataBottom + 15, !knownTime ? timeText.y + timeText.height + 16 : 0)
    readonly property string playedDate: runData.matched_at_utc ? Fmt.localDate(runData.matched_at_utc) : Fmt.dash()
    readonly property bool knownTime: playedDate !== Fmt.dash()
    width: 1140
    height: body.text.length > 0 ? bodyTop + Math.max(1, body.lineCount) * 48
        : single ? Math.max(290, bodyTop + 22) : Math.max(76, bodyTop)

    function dateLabel() {
        if (knownTime) return Fmt.localDateTime(runData.matched_at_utc).replace(/-/g, ".").slice(0, 16)
        const source = runData.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站记录时间：%1 · 游玩时间未知").arg(Fmt.localDateTime(source.source_recorded_at_utc))
        if (source.source_recorded_at)
            return qsTr("原站记录时间：%1 · 游玩时间未知").arg(source.source_recorded_at)
        return qsTr("游玩时间未知")
    }
    TextMetrics {
        id: titleMetrics
        text: dutyTitle.text
        font { family: "Noto Serif SC"; pixelSize: 48; bold: true; letterSpacing: 1 }
    }
    Text {
        id: eyebrow
        objectName: "reflectionShareEyebrow"
        visible: entry.single
        x: 84; y: 94 - baselineOffset
        text: qsTr("导随记录"); textFormat: Text.PlainText; color: "#45342A"
        font { family: "Noto Serif SC"; pixelSize: 24; bold: true; letterSpacing: 6 }
    }
    Text {
        id: dateSeparator
        visible: entry.single
        x: eyebrow.x + eyebrow.width + 14; y: 94 - baselineOffset
        text: "·"; color: "#6E5A44"
        font { family: "Noto Serif SC"; pixelSize: 22 }
    }
    Text {
        id: timeText
        objectName: "reflectionShareTimeText"
        visible: entry.single || !entry.knownTime
        x: entry.single ? dateSeparator.x + dateSeparator.width + 14 : 262
        y: entry.single ? 94 - baselineOffset : entry.metadataBottom + 12
        width: entry.single ? 800 - x : 794
        text: entry.dateLabel()
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#6E5A44"
        font { family: "Noto Serif SC"; pixelSize: entry.single ? 22 : 18; letterSpacing: 1 }
        lineHeightMode: Text.FixedHeight; lineHeight: entry.single ? 30 : 28
    }
    Text {
        visible: !entry.single
        x: 84; y: 28 - baselineOffset; width: 106
        text: entry.knownTime ? entry.playedDate.slice(5).replace("-", ".") : qsTr("时间未知")
        textFormat: Text.PlainText; horizontalAlignment: Text.AlignRight; wrapMode: Text.Wrap; color: "#45342A"
        font { family: "Noto Serif SC"; pixelSize: entry.knownTime ? 30 : 20; bold: true; letterSpacing: 0.5 }
    }
    Text {
        visible: !entry.single && entry.knownTime
        x: 84; y: 54 - baselineOffset; width: 106
        text: entry.knownTime ? Fmt.localTime(entry.runData.matched_at_utc).slice(0, 5) : ""
        textFormat: Text.PlainText; horizontalAlignment: Text.AlignRight; color: "#6E5A44"
        font { family: "Noto Serif SC"; pixelSize: 21; letterSpacing: 0.5 }
    }
    Text {
        visible: !entry.single && entry.knownTime && entry.showYear
        x: 84; y: 79 - baselineOffset; width: 106
        text: entry.knownTime ? entry.playedDate.slice(0, 4) : ""
        textFormat: Text.PlainText; horizontalAlignment: Text.AlignRight; color: "#6E5A44"
        font { family: "Noto Serif SC"; pixelSize: 17 }
    }
    Image {
        visible: !entry.single
        x: 208; y: 16; width: 32; height: 32
        source: entry.assetRoot + "rail_node.png"
    }
    Image {
        x: entry.single ? 72 : 250; y: entry.single ? 112 + entry.dateShift : -12
        width: 88; height: 88
        source: entry.assetRoot + "icon_plate.png"
    }
    Item {
        id: job
        objectName: "reflectionShareJobIcon"
        readonly property var jobId: entry.runData.job_id
        x: entry.single ? 84 : 262; y: entry.single ? 124 + entry.dateShift : 0
        width: 64; height: 64
        Image {
            id: jobImage
            objectName: "reflectionShareGameJobImage"
            anchors.fill: parent
            source: Jobs.framedIcon(job.jobId)
            fillMode: Image.PreserveAspectFit
            visible: source.toString().length > 0 && status === Image.Ready
        }
        Image {
            objectName: "reflectionShareJobFallback"
            anchors.fill: parent
            visible: !jobImage.visible
            source: entry.assetRoot + (Theme.jobName(entry.runData) ? "icon_tile.png" : "icon_placeholder.png")
        }
        Text {
            anchors.centerIn: parent
            visible: !jobImage.visible && !!Theme.jobName(entry.runData)
            text: Theme.jobName(entry.runData).slice(0, 1); textFormat: Text.PlainText; color: "#E9CF98"
            font { family: "Noto Serif SC"; pixelSize: 34; bold: true }
        }
    }
    Text {
        id: dutyTitle
        objectName: "reflectionShareDutyTitle"
        x: entry.single ? 168 : 346
        y: entry.single ? (lineCount > 1 ? 138 : 156) - baselineOffset + entry.dateShift : 35 - baselineOffset
        width: entry.single ? 632 : entry.completed ? 506 : 710
        text: Theme.dutyName(entry.runData) || qsTr("未知副本")
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#2E2118"
        font.family: "Noto Serif SC"
        font.pixelSize: {
            if (!entry.single) return 38
            const sizes = [48, 42, 36, 32]
            for (let i = 0; i < sizes.length; ++i)
                if (titleMetrics.advanceWidth * sizes[i] / 48 <= width) return sizes[i]
            return 32
        }
        font.bold: true; font.letterSpacing: 1
        lineHeightMode: Text.FixedHeight
        lineHeight: entry.single ? (lineCount > 1 ? 40 : font.pixelSize + 10) : 48
    }
    Text {
        id: resultText
        objectName: "reflectionShareResultText"
        x: dutyTitle.x
        y: (entry.single ? 192 : 69) + entry.titleShift - baselineOffset
        width: dutyTitle.width
        text: Theme.live((Theme.jobName(entry.runData) || qsTr("未知职业")) + " · " + Fmt.resultLabel(entry.runData.result || "UNKNOWN"))
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#6E5A44"
        font { family: "Noto Serif SC"; pixelSize: 22; letterSpacing: 0.5 }
        lineHeightMode: Text.FixedHeight; lineHeight: 30
    }
    Item {
        id: mood
        readonly property bool beside: resultText.implicitWidth + 14 + width <= resultText.width
        x: beside ? resultText.x + resultText.implicitWidth + 14 : resultText.x
        y: beside ? (entry.single ? 170 : 47) + entry.titleShift : resultText.y + resultText.height + 8
        width: moodText.implicitWidth + 24; height: 30
        BorderImage {
            anchors.fill: parent
            border { left: 8; right: 8; top: 8; bottom: 8 }
            source: entry.assetRoot + "tag_9slice.png"
        }
        Text {
            id: moodText
            objectName: "reflectionShareMoodText"
            x: 12; y: 22 - baselineOffset
            text: Theme.moodLabel(entry.reflection.mood); textFormat: Text.PlainText; color: "#6B4E2E"
            font { family: "Noto Serif SC"; pixelSize: 20; letterSpacing: 1 }
        }
    }
    Image {
        objectName: "reflectionShareResultStamp"
        x: 858; y: entry.single ? entry.height : -29
        width: 208; height: 124; visible: entry.completed
        source: entry.completed ? entry.assetRoot + "stamp_complete_" + (entry.entryIndex % 3 + 1) + ".png" : ""
    }
    Text {
        id: body
        objectName: "reflectionShareFullText"
        x: entry.single ? 84 : 262
        // 标注以首行基线（单条 304、长卷 y0+128）定义，消除字体引擎留白差异。
        y: entry.bodyTop + 36 - baselineOffset
        width: entry.single ? 972 : 794
        text: entry.reflection.text || ""
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#45342A"
        font { family: "Noto Serif SC"; pixelSize: 28 }
        lineHeightMode: Text.FixedHeight; lineHeight: 48
    }
}
