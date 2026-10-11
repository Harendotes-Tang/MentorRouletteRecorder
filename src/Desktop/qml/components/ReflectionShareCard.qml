import QtQuick
import MentorRecorder

// 单条、逐条 PNG 与合成长图共用此卡片；所有文字在基准画布完整排版后统一缩放。
Item {
    id: card
    objectName: "reflectionShareCard"
    property var runData: ({})
    property bool showAttribution: true
    readonly property var reflection: runData.reflection || ({})
    readonly property real templateScale: Math.max(1, width) / 1536
    readonly property string assetRoot: "qrc:/resources/reflection-share/eorzea-guild-v5/"
    height: Math.ceil(paper.height * templateScale)

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

    Item {
        id: paper
        width: 1536
        height: Math.ceil(Math.max(700, body.y + body.height + 156) / 4) * 4
        scale: card.templateScale
        transformOrigin: Item.TopLeft
        readonly property color ink: "#392618"
        readonly property color secondaryInk: "#6F5740"

        Item {
            id: bodyPaper
            objectName: "reflectionSharePaperBody"
            y: 254
            width: 1536
            // 顶底各多铺 2 个基准像素至固定片下，覆盖高 DPI 软件渲染对裁剪边界的取整空隙。
            height: paper.height - 254 - 134 + 2
            clip: true
            Repeater {
                model: Math.ceil(bodyPaper.height / 256)
                Image {
                    required property int index
                    y: index * 256
                    width: 1536
                    height: 256
                    source: card.assetRoot + "background/body-tile.png"
                }
            }
        }
        Image { objectName: "reflectionShareHeader"; width: 1536; height: 256; source: card.assetRoot + "background/header.png" }
        Image {
            y: paper.height - 134 - 32
            width: 1536; height: 32
            source: card.assetRoot + "background/body-end-transition.png"
        }
        Image {
            objectName: "reflectionShareFooter"
            y: paper.height - 134
            width: 1536; height: 134
            source: card.assetRoot + "background/footer.png"
        }

        Text {
            x: 112; y: 57
            text: qsTr("导随心得")
            color: paper.ink
            font.family: "Noto Serif SC"
            font.pixelSize: 32
            font.weight: Font.DemiBold
            font.letterSpacing: 8
        }
        Image { x: 362; y: 65; width: 448; height: 32; source: card.assetRoot + "decor/title-rule.png" }
        Image { x: 67; y: 132; width: 32; height: 88; source: card.assetRoot + "decor/side-flourish.png" }
        JobIcon { objectName: "reflectionShareJobIcon"; x: 128; y: 132; jobId: card.runData.job_id; size: 96 }
        Text {
            id: dutyTitle
            objectName: "reflectionShareDutyTitle"
            x: 244; y: 129; width: 1180
            text: card.runData.duty_name || qsTr("未知副本")
            textFormat: Text.PlainText
            color: paper.ink
            font.family: "Noto Serif SC"
            font.pixelSize: 64
            font.weight: Font.DemiBold
            wrapMode: Text.Wrap
        }
        Text {
            id: resultText
            objectName: "reflectionShareResultText"
            x: 244; y: Math.max(216, dutyTitle.y + dutyTitle.height); width: 1180
            text: (card.runData.job_name || qsTr("未知职业")) + " · " + Fmt.resultLabel(card.runData.result || "UNKNOWN")
            textFormat: Text.PlainText
            color: paper.ink
            font.family: "Noto Serif SC"
            font.pixelSize: 32
            font.weight: Font.Medium
            wrapMode: Text.Wrap
        }
        Text {
            id: timeText
            objectName: "reflectionShareTimeText"
            x: 244; y: Math.max(268, resultText.y + resultText.height + 8); width: 1180
            text: card.dateLabel()
            textFormat: Text.PlainText
            color: paper.secondaryInk
            font.family: Theme.bodyFamily
            font.pixelSize: 29
            wrapMode: Text.Wrap
        }
        Text {
            id: moodText
            objectName: "reflectionShareMoodText"
            x: 244; y: Math.max(317, timeText.y + timeText.height + 10); width: 1180
            text: Theme.moodLabel(card.reflection.mood)
            textFormat: Text.PlainText
            color: card.reflection.mood === "bad" ? "#943D23" : paper.secondaryInk
            font.family: "Noto Serif SC"
            font.pixelSize: 30
            font.weight: Font.DemiBold
            wrapMode: Text.Wrap
        }
        Image {
            id: divider
            objectName: "reflectionShareDivider"
            x: 112; y: Math.max(367, moodText.y + moodText.height + 24)
            width: 1312; height: 40
            source: card.assetRoot + "decor/divider-full.png"
        }
        Text {
            id: body
            objectName: "reflectionShareFullText"
            x: 112; y: divider.y + 61; width: 1312
            text: card.reflection.text || ""
            textFormat: Text.PlainText
            color: paper.ink
            font.family: "Noto Serif SC"
            font.pixelSize: 40
            wrapMode: Text.Wrap
            lineHeightMode: Text.FixedHeight
            lineHeight: 66
        }
        Image {
            objectName: "reflectionShareBrand"
            x: (paper.width - width) / 2; y: paper.height - 48
            width: 320; height: 32
            source: card.assetRoot + "decor/footer-brand.png"
            visible: card.showAttribution
            Accessible.name: "Powered by 导随记录器"
            Accessible.role: Accessible.StaticText
        }
        // 交叠信息区是已确认设计。此 PNG 自带倾斜与 43% 浓度，不能再次旋转或降低透明度。
        Image {
            objectName: "reflectionShareResultStamp"
            x: 44; y: 30; width: 426; height: 320
            source: card.runData.result === "COMPLETED" ? card.assetRoot + "decor/mission-complete-stamp-diagonal.png" : ""
            visible: source.toString().length > 0
        }
    }
}
