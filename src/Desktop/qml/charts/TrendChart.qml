pragma ComponentBehavior: Bound

import QtQuick
import MentorRecorder 1.0

// Completion trend. Qt Graphs draws the bars when the module can be instantiated
// in this environment (main() probes it once and publishes `GraphsAvailable`);
// the hand-drawn TrendBars row is the fallback, so a deployment without the
// QtGraphs plugin still renders a usable chart.
Item {
    id: root

    property var buckets: []

    readonly property bool useGraphs: (typeof Runtime.graphsAvailable !== "undefined")
                                      && Runtime.graphsAvailable
    readonly property int maxCount: {
        let maxValue = 1
        for (let i = 0; i < buckets.length; ++i)
            maxValue = Math.max(maxValue, Number(buckets[i].count || 0))
        return maxValue
    }

    implicitHeight: 156

    Loader {
        id: chart
        anchors.fill: parent
        sourceComponent: root.useGraphs ? graphsChart : fallbackChart
    }

    // Paint in the chart's scene so the label cannot take hover away from a bar.
    Rectangle {
        id: hoverTip
        objectName: "trendHoverTip"
        readonly property var renderer: chart.item
        readonly property var bucket: renderer ? root.buckets[renderer.hoveredIndex] : undefined
        readonly property point barTop: renderer ? renderer.hoveredBarTop : Qt.point(0, 0)
        readonly property alias text: label.text
        visible: text.length > 0
        width: label.implicitWidth + 16
        height: label.implicitHeight + 10
        x: Math.round(Math.max(0, Math.min(root.width - width, barTop.x - width / 2)))
        y: Math.round(barTop.y - height - 6)
        radius: Theme.radiusS
        color: Theme.surface
        border.width: 1
        border.color: Theme.border

        Text {
            id: label
            anchors.centerIn: parent
            text: hoverTip.bucket && hoverTip.bucket.count > 0
                  ? qsTranslate("GraphsTrendChart", "%1 · %2 次").arg(hoverTip.bucket.label).arg(hoverTip.bucket.count)
                  : ""
            textFormat: Text.PlainText
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(12)
            font.weight: Font.Medium
        }
    }

    Component {
        id: fallbackChart

        TrendBars {
            objectName: "completionTrendBars"
            buckets: root.buckets
        }
    }

    Component {
        id: graphsChart

        GraphsTrendChart {
            objectName: "completionTrendChart"
            buckets: root.buckets
            maxCount: root.maxCount
        }
    }
}
