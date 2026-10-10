import QtQuick
import QtQuick.Layouts
import MentorRecorder

// The same Canvas and bucket snapshot render with Qt Graphs present or absent.
// Counts and null shares come from DungeonStatsModel, never from table Top-N.
Card {
    id: root

    property string title: ""
    property string detail: ""
    property var buckets: []
    readonly property real completedTotal: {
        let total = 0
        for (const bucket of buckets)
            total += Number(bucket.completed_count || 0)
        return total
    }
    readonly property bool stacked: width < 420

    padding: 16
    spacing: 10

    Accessible.role: Accessible.StaticText
    Accessible.name: title + " · " + qsTr("%1 次通关").arg(completedTotal)

    function shareText(bucket) {
        if (bucket.share === null || bucket.share === undefined || completedTotal <= 0)
            return "—"
        return (Number(bucket.share) * 100).toFixed(1) + "%"
    }

    onBucketsChanged: pie.requestPaint()

    Connections {
        target: Theme
        function onDarkChanged() { pie.requestPaint() }
        function onUiStyleChanged() { pie.requestPaint() }
    }

    Text {
        objectName: "compositionTitle"
        Layout.fillWidth: true
        text: root.title
        textFormat: Text.PlainText
        wrapMode: Text.WordWrap
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(14)
        font.bold: true
    }

    Text {
        objectName: "compositionTotal"
        Layout.fillWidth: true
        text: qsTr("全部通关 %1 次").arg(root.completedTotal)
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        font.family: Theme.figureFamily
        font.features: ({ "tnum": 1 })
    }

    GridLayout {
        Layout.fillWidth: true
        columns: root.stacked ? 1 : 2
        columnSpacing: 16
        rowSpacing: 12

        Item {
            Layout.preferredWidth: 148
            Layout.preferredHeight: 148
            Layout.alignment: root.stacked ? Qt.AlignHCenter : Qt.AlignVCenter

            Canvas {
                id: pie
                objectName: "completionPieCanvas"
                anchors.fill: parent
                onWidthChanged: requestPaint()
                onHeightChanged: requestPaint()

                onPaint: {
                    const ctx = getContext("2d")
                    ctx.reset()
                    const cx = width / 2, cy = height / 2
                    const radius = Math.max(0, Math.min(width, height) / 2 - 2)
                    if (root.completedTotal <= 0) {
                        // An outline means no eligible completions, not a 100% unknown slice.
                        ctx.strokeStyle = Theme.border
                        ctx.lineWidth = 2
                        ctx.beginPath()
                        ctx.arc(cx, cy, radius, 0, Math.PI * 2)
                        ctx.stroke()
                        return
                    }
                    let angle = -Math.PI / 2
                    for (const bucket of root.buckets) {
                        const count = Number(bucket.completed_count || 0)
                        if (count <= 0)
                            continue
                        const end = angle + Math.PI * 2 * count / root.completedTotal
                        ctx.beginPath()
                        ctx.moveTo(cx, cy)
                        ctx.arc(cx, cy, radius, angle, end)
                        ctx.closePath()
                        ctx.fillStyle = Theme.token(bucket.color_token || "neutral500")
                        ctx.fill()
                        ctx.strokeStyle = Theme.surface
                        ctx.lineWidth = 1
                        ctx.stroke()
                        angle = end
                    }
                }
            }

            Text {
                objectName: "completionPieEmpty"
                anchors.centerIn: parent
                visible: root.completedTotal <= 0
                text: qsTr("暂无通关")
                color: Theme.textMuted
                font.pixelSize: Theme.fs(12)
            }
        }

        ColumnLayout {
            objectName: "completionPieLegend"
            Layout.fillWidth: true
            spacing: 8

            Repeater {
                model: root.buckets
                delegate: RowLayout {
                    required property var modelData
                    required property int index

                    objectName: "completionPieLegendRow" + index
                    Layout.fillWidth: true
                    spacing: 8

                    Rectangle {
                        Layout.preferredWidth: 9
                        Layout.preferredHeight: 9
                        radius: 2
                        color: Theme.token(modelData.color_token || "neutral500")
                    }

                    Text {
                        objectName: "completionPieLabel" + index
                        Layout.fillWidth: true
                        Layout.minimumWidth: 0
                        text: modelData.label || qsTr("未识别")
                        textFormat: Text.PlainText
                        wrapMode: Text.WordWrap
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                    }

                    Text {
                        objectName: "completionPieCount" + index
                        text: qsTr("%1 次").arg(modelData.completed_count || 0)
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                        horizontalAlignment: Text.AlignRight
                    }

                    Text {
                        objectName: "completionPieShare" + index
                        Layout.preferredWidth: 54
                        text: root.shareText(modelData)
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                        horizontalAlignment: Text.AlignRight
                    }
                }
            }
        }
    }

    Text {
        objectName: "compositionDetail"
        Layout.fillWidth: true
        visible: root.detail.length > 0
        text: root.detail
        textFormat: Text.PlainText
        wrapMode: Text.WordWrap
        color: Theme.textMuted
        font.pixelSize: Theme.fs(11)
    }
}
