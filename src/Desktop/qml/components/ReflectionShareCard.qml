import QtQuick
import MentorRecorder

// 按 1140 逻辑像素排完整张卷轴，再统一缩放至预览/抓图宽度。
Item {
    id: card
    objectName: "reflectionShareCard"
    property var runData: ({})
    property var runsData: []
    property bool showAttribution: true
    property string exportDate: Qt.formatDate(new Date(), "yyyy.MM.dd")
    readonly property bool opaquePaper: true
    readonly property var entries: runsData.length > 0 ? runsData : [runData]
    readonly property bool single: entries.length === 1
    readonly property bool spansYears: {
        const years = ({})
        for (let i = 0; i < entries.length; ++i) {
            const date = Fmt.localDate(entries[i].matched_at_utc)
            if (date !== Fmt.dash()) years[date.slice(0, 4)] = true
        }
        return Object.keys(years).length > 1
    }
    readonly property real templateScale: Math.max(1, width) / 1140
    readonly property string assetRoot: "qrc:/resources/reflection-share/duty-notes-scroll-v1.4.1/"
    height: Math.ceil(paper.height * templateScale)

    function collectionLabel() {
        const dates = []
        for (let i = 0; i < entries.length; ++i) {
            if (!entries[i].matched_at_utc) continue
            const value = Fmt.localDate(entries[i].matched_at_utc)
            if (value !== Fmt.dash()) dates.push(value.replace(/-/g, "."))
        }
        dates.sort()
        let label = qsTr("共 %1 条").arg(entries.length)
        if (dates.length > 0)
            label += " · " + dates[0] + (dates[0] === dates[dates.length - 1] ? "" : " – " + dates[dates.length - 1])
        if (dates.length < entries.length)
            label += " · " + qsTr("%1 条游玩时间未知").arg(entries.length - dates.length)
        return label
    }

    Item {
        id: paper
        width: 1140
        height: Math.ceil(Math.max(480, (card.single ? singleEntry.height : timeline.y + timeline.height) + 190))
        scale: card.templateScale
        transformOrigin: Item.TopLeft
        readonly property real dividerY: card.single ? singleEntry.dividerY : Math.max(238, collectionMeta.y + collectionMeta.height + 38)
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
            objectName: "reflectionShareCollectionTitle"
            visible: !card.single
            x: 84; y: 132 - baselineOffset; width: 610
            text: qsTr("导随记录"); textFormat: Text.PlainText
            color: "#2E2118"
            font { family: "Noto Serif SC"; pixelSize: 56; bold: true; letterSpacing: 8 }
        }
        Text {
            id: collectionMeta
            objectName: "reflectionShareCollectionMeta"
            visible: !card.single
            x: 86; y: 186 - baselineOffset; width: 610
            text: card.collectionLabel(); textFormat: Text.PlainText
            wrapMode: Text.Wrap; color: "#6E5A44"
            font { family: "Noto Serif SC"; pixelSize: 24; letterSpacing: 1.5 }
        }
        ReflectionShareEntry {
            id: singleEntry
            visible: card.single
            runData: card.entries[0] || ({})
            single: true
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
                    required property var modelData
                    required property int index
                    runData: modelData; entryIndex: index; single: false
                    showYear: card.spansYears
                    Image {
                        x: 262; y: parent.height + 34; width: 794; height: 8
                        visible: index < card.entries.length - 1
                        source: card.assetRoot + "entry_rule.png"
                    }
                }
            }
        }
        Text {
            objectName: "reflectionShareExportDate"
            x: 84; y: paper.height - 88 - baselineOffset; width: 972
            text: qsTr("导出于 %1").arg(card.exportDate); textFormat: Text.PlainText
            horizontalAlignment: Text.AlignHCenter; color: "#6E5A44"
            font { family: "Noto Serif SC"; pixelSize: 20; letterSpacing: 3 }
        }
        Text {
            objectName: "reflectionShareBrand"
            x: 84; y: paper.height - 49 - baselineOffset; width: 972
            text: qsTr("Powered by 导随记录器"); textFormat: Text.PlainText
            horizontalAlignment: Text.AlignHCenter; color: "#6E5A44"
            font { family: Theme.bodyFamily; pixelSize: 16 }
            visible: card.showAttribution
        }
    }
}
