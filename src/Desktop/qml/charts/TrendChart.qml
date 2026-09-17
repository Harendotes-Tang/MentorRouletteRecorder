import QtQuick
import MentorRecorder

// Completion trend. Qt Graphs draws the bars when the module can be instantiated
// in this environment (main() probes it once and publishes `GraphsAvailable`);
// the hand-drawn TrendBars row is the fallback, so a deployment without the
// QtGraphs plugin still renders a usable chart.
Item {
    id: root

    property var buckets: []

    readonly property bool useGraphs: (typeof GraphsAvailable !== "undefined")
                                      && GraphsAvailable
    readonly property int maxCount: {
        let maxValue = 1
        for (let i = 0; i < buckets.length; ++i)
            maxValue = Math.max(maxValue, Number(buckets[i].count || 0))
        return maxValue
    }

    implicitHeight: 156

    Loader {
        anchors.fill: parent
        sourceComponent: root.useGraphs ? graphsChart : fallbackChart
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
