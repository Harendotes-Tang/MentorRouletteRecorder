pragma ComponentBehavior: Bound

import QtQuick
import MentorRecorder 1.0

// 按 1140 逻辑像素排完整张卷轴，再统一缩放至预览/抓图宽度。
Item {
    id: card
    objectName: "reflectionShareCard"
    property var runData: ({})
    property var runsData: []
    property bool showAttribution: true
    property string gameIdentity: ""
    property string exportDate: Qt.formatDate(new Date(), "yyyy.MM.dd")
    readonly property bool opaquePaper: true
    readonly property var entries: runsData.length > 0 ? runsData : [runData]
    readonly property bool single: entries.length === 1
    readonly property bool spansYears: {
        const years = ({})
        for (let i = 0; i < entries.length; ++i) {
            const date = Runtime.fmt.localDate(entries[i].matched_at_utc)
            if (date !== Runtime.fmt.dash()) years[date.slice(0, 4)] = true
        }
        return Object.keys(years).length > 1
    }
    readonly property real templateScale: Math.max(1, width) / 1140
    readonly property string assetRoot: "qrc:/resources/reflection-share/duty-notes-scroll-v1.4.1/"
    height: Math.ceil(paper.height * templateScale)

    function collectionDates() {
        const dates = []
        for (let i = 0; i < entries.length; ++i) {
            const date = Runtime.fmt.localDate(entries[i].matched_at_utc)
            if (date !== Runtime.fmt.dash()) dates.push(date.replace(/-/g, "."))
        }
        dates.sort()
        let unknown = dates.length < entries.length ? qsTr("%1 条游玩时间未知").arg(entries.length - dates.length) : ""
        if (!dates.length) return [unknown]
        const first = dates[0], last = dates[dates.length - 1]
        const suffix = unknown.length ? " · " + unknown : ""
        if (first === last) return [first + suffix, first.slice(5) + suffix]
        return [first + " – " + last + suffix,
                first + " – " + last.slice(5) + suffix,
                first.slice(5) + " – " + last.slice(5) + suffix]
    }

    // 缩放后的纸张高度会向上取整；补满最后不足一像素的画布，防止 GPU 抓图留透明边。
    Rectangle { anchors.fill: parent; color: "#EFE5CF" }

    Item {
        id: paper
        width: 1140
        height: Math.ceil(Math.max(480, (card.single ? singleEntry.height : timeline.y + timeline.height) + 190))
        scale: card.templateScale
        transformOrigin: Item.TopLeft
        readonly property real dividerY: card.single ? singleEntry.dividerY : Math.max(238, shareHeading.y + shareHeading.height + 38)
        readonly property bool fixedHeader: dividerY === 238

        Item {
            objectName: "reflectionSharePaperBody"
            anchors.fill: parent
            clip: true
            Repeater {
                model: Math.ceil(paper.height / 570) * 2
                Image {
                    required property int index
                    x: (index % 2) * 570; y: Math.floor(index / 2) * 570
                    width: 570; height: 570
                    source: card.assetRoot + "paper_tile.png"
                }
            }
        }
        // 2x 九宫格先在双倍坐标绘制，角饰保持 192 逻辑像素，不随长文拉伸。
        BorderImage {
            objectName: "reflectionShareFrame"
            width: 2280; height: paper.height * 2
            scale: 0.5; transformOrigin: Item.TopLeft
            border { left: 384; right: 384; top: 384; bottom: 384 }
            source: card.assetRoot + "frame_9slice.png"
            visible: !paper.fixedHeader
        }
        Image {
            x: 0; y: 280; width: 1140; height: Math.max(0, paper.height - 480)
            source: card.assetRoot + "scroll_mid.png"
            visible: paper.fixedHeader
        }
        Image {
            objectName: "reflectionShareHeader"
            width: 1140; height: 280
            source: card.assetRoot + "scroll_top.png"
            visible: paper.fixedHeader
        }
        Image {
            x: 706; y: 20; width: 378; height: 215
            source: card.assetRoot + "header_art.png"
            visible: !paper.fixedHeader
        }
        Image {
            objectName: "reflectionShareDivider"
            x: 84; y: paper.dividerY - 12; width: 972; height: 24
            source: card.assetRoot + "header_rule.png"
            visible: !paper.fixedHeader
        }
        Repeater {
            model: 2
            Image {
                required property int index
                x: index === 0 ? 0 : 1106; y: paper.dividerY - 42
                width: 34; height: 84
                source: card.assetRoot + "side_gem.png"
                visible: !paper.fixedHeader
            }
        }
        Image {
            y: paper.height - 200; width: 1140; height: 200
            source: card.assetRoot + "scroll_bottom.png"
            visible: paper.fixedHeader
        }
        Image {
            objectName: "reflectionShareFooter"
            x: 390; y: paper.height - 164; width: 360; height: 48
            source: card.assetRoot + "end_ornament.png"
            visible: !paper.fixedHeader
        }
        Text {
            id: collectionTitle
            // 卷轴整体缩放；按轮廓绘制，避免原生字形位图缩放后出现断笔。
            renderType: Text.CurveRendering
            objectName: "reflectionShareCollectionTitle"
            visible: !card.single
            x: 84; y: 132 - baselineOffset; width: 610
            text: qsTr("导随记录"); textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: "#2E2118"
            font { family: "MR Reflection Serif"; pixelSize: 56; bold: true; letterSpacing: 8 }
        }
        ReflectionShareHeading {
            id: shareHeading
            objectName: card.single ? "reflectionShareSingleHeading" : "reflectionShareCollectionMeta"
            single: card.single; gameIdentity: card.gameIdentity
            x: card.single ? 84 : 86
            y: !card.single && collectionTitle.lineCount > 1
                ? Math.max(186 - textBaseline, collectionTitle.y + collectionTitle.height + 16)
                : (card.single ? 94 : 186) - textBaseline
            width: card.single ? 716 : 726
            countLabel: card.single ? "" : qsTr("共 %1 条").arg(card.entries.length)
            dateOptions: {
                if (!card.single) return card.collectionDates()
                const date = singleEntry.dateLabel()
                return singleEntry.knownTime ? [date, date.slice(5)] : [date]
            }
        }
        ReflectionShareEntry {
            id: singleEntry
            visible: card.single
            runData: card.entries[0] || ({})
            single: true
            headerExtraHeight: Math.max(0, shareHeading.height - 36)
        }
        Image {
            x: 220; y: timeline.y + 32; width: 8
            height: {
                const last = entriesRepeater.count > 0 ? entriesRepeater.itemAt(entriesRepeater.count - 1) : null
                return Math.max(0, timeline.height - (last ? last.height : 0))
            }
            visible: !card.single && card.entries.length > 1
            source: card.assetRoot + "rail_line.png"
        }
        Column {
            id: timeline
            x: 0; y: paper.dividerY + 54; width: 1140
            visible: !card.single; spacing: 76
            Repeater {
                id: entriesRepeater
                model: card.single ? [] : card.entries
                ReflectionShareEntry {
                    id: timelineEntry
                    required property var modelData
                    required property int index
                    runData: modelData; entryIndex: index; single: false
                    showYear: card.spansYears
                    Image {
                        x: 262; y: parent.height + 34; width: 794; height: 8
                        visible: timelineEntry.index < card.entries.length - 1
                        source: card.assetRoot + "entry_rule.png"
                    }
                }
            }
        }
        Item {
            id: footerLine
            objectName: "reflectionShareFooterLine"
            readonly property bool stacked: card.showAttribution
                && footerDate.width + footerSeparator.width + footerBrand.implicitWidth > 520
            x: (1140 - width) / 2; y: paper.height - 88
            width: stacked ? Math.max(footerDate.width, footerBrand.width)
                : footerDate.width + (card.showAttribution ? footerSeparator.width + footerBrand.width : 0)
            Text {
                id: footerDate
                renderType: Text.CurveRendering
                objectName: "reflectionShareExportDate"
                x: footerLine.stacked ? (footerLine.width - width) / 2 : 0
                y: -baselineOffset; width: implicitWidth
                text: qsTr("导出于 %1").arg(card.exportDate); textFormat: Text.PlainText
                color: "#6E5A44"
                font { family: "MR Reflection Serif"; pixelSize: 18; letterSpacing: 2 }
            }
            Text {
                id: footerSeparator
                renderType: Text.CurveRendering
                x: footerDate.width; y: -baselineOffset; width: implicitWidth + 28
                text: "·"; horizontalAlignment: Text.AlignHCenter; color: "#6E5A44"; font: footerDate.font
                visible: card.showAttribution && !footerLine.stacked
            }
            Text {
                id: footerBrand
                renderType: Text.CurveRendering
                objectName: "reflectionShareBrand"
                x: footerLine.stacked ? (footerLine.width - width) / 2 : footerDate.width + footerSeparator.width
                y: (footerLine.stacked ? 28 : 0) - baselineOffset
                width: Math.min(implicitWidth, 520)
                text: qsTr("Powered by 导随记录器"); textFormat: Text.PlainText; wrapMode: Text.Wrap
                color: "#6E5A44"; font: footerDate.font
                visible: card.showAttribution
            }
        }
    }
}
