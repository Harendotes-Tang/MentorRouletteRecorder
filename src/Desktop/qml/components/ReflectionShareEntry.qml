import QtQuick
import MentorRecorder 1.0

// 来源时间与真实游玩时间保持区分；仅投影记录，不推断职业或通关结果。
Item {
    id: entry
    objectName: "reflectionShareEntry"
    property var runData: ({})
    property bool single: false
    property int entryIndex: 0
    property bool showYear: false
    property real headerExtraHeight: 0
    readonly property var reflection: runData.reflection || ({})
    readonly property bool completed: runData.result === "COMPLETED"
    readonly property string assetRoot: "qrc:/resources/reflection-share/duty-notes-scroll-v1.4.1/"
    readonly property real dateShift: single ? headerExtraHeight : 0
    readonly property real titleShift: {
        const shift = single ? dateShift + (dutyTitle.lineCount > 1 ? 18 + Math.max(0, dutyTitle.lineCount - 2) * 40 : 0)
            : Math.max(0, dutyTitle.lineCount - 1) * 48
        // 单条与合集都保留多行标题全文，让属性离开整个 Qt 文本布局框。
        return dutyTitle.lineCount > 1
            ? Math.max(shift, dutyTitle.y + dutyTitle.height + 4 - (single ? 192 : 69) + resultText.baselineOffset) : shift
    }
    readonly property real dividerY: Math.max(238, metadataBottom + 20)
    readonly property real metadataBottom: Math.max((single ? 200 : 77) + titleShift,
        resultText.y + resultText.height, mood.visible ? mood.y + mood.height : 0)
    readonly property real bodyTop: single ? dividerY + 30
        : Math.max(92 + titleShift, metadataBottom + 15, !knownTime ? timeText.y + timeText.height + 16 : 0)
    readonly property string playedDate: runData.matched_at_utc ? Runtime.fmt.localDate(runData.matched_at_utc) : Runtime.fmt.dash()
    readonly property bool knownTime: playedDate !== Runtime.fmt.dash()
    width: 1140
    height: body.text.length > 0 ? bodyTop + Math.max(1, body.lineCount) * 48
        : single ? Math.max(290, bodyTop + 22) : Math.max(76, bodyTop)

    function dateLabel() {
        if (knownTime) return Runtime.fmt.localDateTime(runData.matched_at_utc).replace(/-/g, ".").slice(0, 16)
        const source = runData.import_metadata || ({})
        if (source.source_recorded_at_utc)
            return qsTr("原站记录时间：%1 · 游玩时间未知").arg(Runtime.fmt.localDateTime(source.source_recorded_at_utc))
        if (source.source_recorded_at)
            return qsTr("原站记录时间：%1 · 游玩时间未知").arg(source.source_recorded_at)
        return qsTr("游玩时间未知")
    }
    // 常见地点词组和拉丁单词优先整体排版；未知中文回退到字，并平衡孤立尾字。
    // 度量使用当前字号，换行只改变显示，不写回记录中的副本名。
    function wrappedDutyTitle(value) {
        // advanceWidth() 不会登记字体依赖；字号适配后必须按更新的字体重新换行。
        Theme.trackBindingDependency(titleFontMetrics.font)
        const paragraphs = value.split("\n")
        const lines = []
        for (let p = 0; p < paragraphs.length; ++p) {
            const tokens = paragraphs[p].match(/野营地|野營地|野営地|图书馆|圖書館|博物馆|博物館|实验室|實驗室|研究所|追忆馆|追憶館|王城|要塞|神殿|圣殿|聖殿|宫殿|宮殿|迷宫|迷宮|遗迹|遺跡|灵庙|靈廟|陵墓|教皇厅|教皇廳|修道院|讨伐战|討伐戰|歼灭战|殲滅戰|溶洞|溪谷|峡谷|峽谷|海域|庭园|庭園|水道|矿山|礦山|矿洞|礦洞|[ァ-ヺー]+[・：]?|[A-Za-z0-9]+(?:['’._-][A-Za-z0-9]+)*|[\uD800-\uDBFF][\uDC00-\uDFFF]|./g) || [""]
            const words = []
            for (let i = 0; i < tokens.length; ++i) {
                if (/^[)）\]】」』、。，．！？!?；;：・]$/.test(tokens[i]) && words.length) {
                    // 闭合标点随前一词换行，避免单独留下括号或行首中点；保留原始空格。
                    let suffix = tokens[i]
                    while (words.length && /^\s+$/.test(words[words.length - 1])) suffix = words.pop() + suffix
                    if (words.length) words[words.length - 1] += suffix
                    else words.push(suffix)
                } else words.push(tokens[i])
            }
            const rows = []
            let row = []
            for (let wordIndex = 0; wordIndex < words.length; ++wordIndex) {
                const word = words[wordIndex]
                if (row.length && titleFontMetrics.advanceWidth(row.join("") + word) > dutyTitle.width) {
                    rows.push(row)
                    row = []
                }
                // 极长英文单词或窄窗口仍保留全文，由 Text.Wrap 处理单词内部回退。
                row.push(word)
            }
            if (rows.length && /^[\u3400-\u9fff]$/.test(row.join(""))) {
                const previous = rows[rows.length - 1]
                while (previous.length > 1 && row.join("").length < 3
                       && titleFontMetrics.advanceWidth(previous[previous.length - 1] + row.join("")) <= dutyTitle.width)
                    row.unshift(previous.pop())
            }
            rows.push(row)
            for (let rowIndex = 0; rowIndex < rows.length; ++rowIndex) lines.push(rows[rowIndex].join(""))
        }
        return lines.join("\n")
    }
    TextMetrics {
        id: titleMetrics
        renderType: dutyTitle.renderType
        text: Theme.dutyName(entry.runData) || qsTr("未知副本")
        font { family: "MR Reflection Serif"; pixelSize: 48; bold: true; letterSpacing: 1 }
    }
    TextMetrics { id: title42; text: titleMetrics.text; renderType: dutyTitle.renderType; font { family: "MR Reflection Serif"; pixelSize: 42; bold: true; letterSpacing: 1 } }
    TextMetrics { id: title36; text: titleMetrics.text; renderType: dutyTitle.renderType; font { family: "MR Reflection Serif"; pixelSize: 36; bold: true; letterSpacing: 1 } }
    TextMetrics { id: title32; text: titleMetrics.text; renderType: dutyTitle.renderType; font { family: "MR Reflection Serif"; pixelSize: 32; bold: true; letterSpacing: 1 } }
    FontMetrics { id: titleFontMetrics; font: dutyTitle.font }
    Text {
        id: timeText
        renderType: Text.CurveRendering
        objectName: "reflectionShareTimeText"
        visible: !entry.single && !entry.knownTime
        x: 262; y: entry.metadataBottom + 12; width: 794
        text: entry.dateLabel()
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#6E5A44"
        font { family: "MR Reflection Serif"; pixelSize: 18; letterSpacing: 1 }
        lineHeightMode: Text.FixedHeight; lineHeight: 28
    }
    Text {
        renderType: Text.CurveRendering
        visible: !entry.single
        x: 84; y: 28 - baselineOffset; width: 106
        text: entry.knownTime ? entry.playedDate.slice(5).replace("-", ".") : qsTr("时间未知")
        textFormat: Text.PlainText; horizontalAlignment: Text.AlignRight; wrapMode: Text.Wrap; color: "#45342A"
        font { family: "MR Reflection Serif"; pixelSize: entry.knownTime ? 30 : 20; bold: true; letterSpacing: 0.5 }
    }
    Text {
        renderType: Text.CurveRendering
        visible: !entry.single && entry.knownTime
        x: 84; y: 54 - baselineOffset; width: 106
        text: entry.knownTime ? Runtime.fmt.localTime(entry.runData.matched_at_utc).slice(0, 5) : ""
        textFormat: Text.PlainText; horizontalAlignment: Text.AlignRight; color: "#6E5A44"
        font { family: "MR Reflection Serif"; pixelSize: 21; letterSpacing: 0.5 }
    }
    Text {
        renderType: Text.CurveRendering
        visible: !entry.single && entry.knownTime && entry.showYear
        x: 84; y: 79 - baselineOffset; width: 106
        text: entry.knownTime ? entry.playedDate.slice(0, 4) : ""
        textFormat: Text.PlainText; horizontalAlignment: Text.AlignRight; color: "#6E5A44"
        font { family: "MR Reflection Serif"; pixelSize: 17 }
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
            source: Runtime.jobs.framedIcon(job.jobId)
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
            renderType: Text.CurveRendering
            anchors.centerIn: parent
            visible: !jobImage.visible && !!Theme.jobName(entry.runData)
            text: Theme.jobName(entry.runData).slice(0, 1); textFormat: Text.PlainText; color: "#E9CF98"
            font { family: "MR Reflection Serif"; pixelSize: 34; bold: true }
        }
    }
    Text {
        id: dutyTitle
        renderType: Text.CurveRendering
        objectName: "reflectionShareDutyTitle"
        x: entry.single ? 168 : 346
        y: entry.single ? (lineCount > 1 ? 138 : 156) - baselineOffset + entry.dateShift : 35 - baselineOffset
        // 单条标题右沿 808，距装饰最左墨迹仍有 18px；避免中点多出几像素便整词换行。
        width: entry.single ? 640 : entry.completed ? 506 : 710
        text: entry.wrappedDutyTitle(titleMetrics.text)
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#2E2118"
        elide: Text.ElideNone
        font.family: "MR Reflection Serif"
        font.pixelSize: {
            if (!entry.single) return 38
            const sizes = [48, 42, 36, 32]
            const measured = [titleMetrics.advanceWidth, title42.advanceWidth, title36.advanceWidth, title32.advanceWidth]
            for (let i = 0; i < sizes.length; ++i)
                if (measured[i] <= width) return sizes[i]
            return 32
        }
        font.bold: true; font.letterSpacing: 1
        lineHeightMode: Text.FixedHeight
        lineHeight: entry.single ? (lineCount > 1 ? 40 : font.pixelSize + 10) : 48
    }
    Text {
        id: resultText
        renderType: Text.CurveRendering
        objectName: "reflectionShareResultText"
        x: dutyTitle.x
        y: (entry.single ? 192 : 69) + entry.titleShift - baselineOffset
        width: dutyTitle.width
        text: Theme.live((Theme.jobName(entry.runData) || qsTr("未知职业")) + " · " + Runtime.fmt.resultLabel(entry.runData.result || "UNKNOWN"))
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#6E5A44"
        font { family: "MR Reflection Serif"; pixelSize: 22; letterSpacing: 0.5 }
        lineHeightMode: Text.FixedHeight; lineHeight: 30
    }
    Item {
        id: mood
        objectName: "reflectionShareMoodTag"
        visible: ["good", "ok", "bad"].indexOf(entry.reflection.mood) >= 0
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
            renderType: Text.CurveRendering
            objectName: "reflectionShareMoodText"
            x: 12; y: 22 - baselineOffset
            text: Theme.moodLabel(entry.reflection.mood); textFormat: Text.PlainText; color: "#6B4E2E"
            font { family: "MR Reflection Serif"; pixelSize: 20; letterSpacing: 1 }
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
        renderType: Text.CurveRendering
        objectName: "reflectionShareFullText"
        x: entry.single ? 84 : 262
        // 标注以首行基线（单条 304、长卷 y0+128）定义，消除字体引擎留白差异。
        y: entry.bodyTop + 36 - baselineOffset
        width: entry.single ? 972 : 794
        text: entry.reflection.text || ""
        textFormat: Text.PlainText; wrapMode: Text.Wrap; color: "#45342A"
        font { family: "MR Reflection Serif"; pixelSize: 28 }
        lineHeightMode: Text.FixedHeight; lineHeight: 48
    }
}
