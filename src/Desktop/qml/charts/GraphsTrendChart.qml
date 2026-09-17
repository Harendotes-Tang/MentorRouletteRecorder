import QtQuick
import QtQuick.Controls
import QtGraphs
import MentorRecorder

// Qt Graphs rendering of the completion trend. Loaded only when main() has
// verified that QtGraphs can be instantiated in this environment.
Item {
    id: root

    property var buckets: []
    property int maxCount: 1
    /// The bars are moving (TrendMotionLayer draws them meanwhile).
    readonly property bool transitionRunning: motion.running
    readonly property alias motionLayer: motion

    // Computed from the buckets: `maxCount` is bound by the owner to the same
    // buckets, but in an order the change handler below cannot rely on.
    readonly property int axisMax: {
        let maxValue = 1
        for (let i = 0; i < buckets.length; ++i)
            maxValue = Math.max(maxValue, Number(buckets[i].count || 0))
        return maxValue
    }

    function seriesOf(list) {
        const out = []
        for (let i = 0; i < list.length; ++i) {
            out.push({ t: Date.parse(String(list[i].start_utc || "")),
                       count: Number(list[i].count || 0) })
        }
        return out
    }

    // The same maximum as axisMax, read straight off the series: a change handler
    // runs before the bindings that depend on the same property.
    function maxOf(series) {
        let maxValue = 1
        for (let i = 0; i < series.length; ++i)
            maxValue = Math.max(maxValue, series[i].count)
        return maxValue
    }

    function adoptBuckets() {
        const series = root.seriesOf(root.buckets)
        motion.accept(series, root.maxOf(series))
    }

    onBucketsChanged: root.adoptBuckets()
    Component.onCompleted: root.adoptBuckets()

    readonly property var categories: {
        const out = []
        for (let i = 0; i < buckets.length; ++i)
            out.push(String(buckets[i].label || ""))
        return out
    }

    readonly property var values: {
        const out = []
        for (let i = 0; i < buckets.length; ++i)
            out.push(Number(buckets[i].count || 0))
        return out
    }

    GraphsView {
        id: view

        anchors.fill: parent
        anchors.bottomMargin: 18
        // While the motion layer draws the bars, Qt Graphs' own are hidden.
        opacity: motion.running ? 0 : 1

        theme: GraphsTheme {
            colorScheme: Theme.dark ? GraphsTheme.ColorScheme.Dark
                                    : GraphsTheme.ColorScheme.Light
            backgroundVisible: false
            plotAreaBackgroundVisible: false
            gridVisible: false
            labelsVisible: false
            seriesColors: [Theme.eorzea ? Theme.barFillEnd : Theme.accent]
            borderColors: [Theme.eorzea ? Theme.barFillStart : Theme.accent]
            borderWidth: 0
            labelTextColor: Theme.textSecondary
        }

        axisX: BarCategoryAxis {
            categories: root.categories
            visible: false
            gridVisible: false
            labelsVisible: false
            lineVisible: false
        }

        axisY: ValueAxis {
            min: 0
            max: root.axisMax
            visible: false
            gridVisible: false
            labelsVisible: false
            lineVisible: false
        }

        BarSeries {
            id: series
            barWidth: 0.82
            labelsVisible: false

            BarSet {
                values: root.values
            }
        }
    }

    // Laid exactly over the plot area, so its bars land on Qt Graphs' pixels.
    TrendMotionLayer {
        id: motion

        x: view.x + view.plotArea.x
        y: view.y + view.plotArea.y
        width: view.plotArea.width
        height: view.plotArea.height
        barRatio: series.barWidth
        barColor: Theme.eorzea ? Theme.barFillEnd : Theme.accent
    }

    Rectangle {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.bottomMargin: 18
        height: 1
        color: Theme.eorzea ? Theme.gold3 : Theme.border
    }

    // Bars are drawn by the scene graph, so the tooltip is driven from the pointer
    // position rather than from a per-bar hover handler.
    MouseArea {
        id: hoverArea

        anchors.fill: view
        hoverEnabled: true
        acceptedButtons: Qt.NoButton

        readonly property int index: (root.buckets.length > 0 && containsMouse)
                                     ? Math.min(root.buckets.length - 1,
                                                Math.max(0, Math.floor(mouseX / (width / root.buckets.length))))
                                     : -1

        ToolTip.visible: hoverArea.index >= 0
        ToolTip.text: hoverArea.index >= 0
                      ? (root.buckets[hoverArea.index].label || "") + " · "
                        + String(root.buckets[hoverArea.index].count || 0) + qsTr(" 次")
                      : ""
    }

    Text {
        anchors.left: parent.left
        anchors.bottom: parent.bottom
        text: root.buckets.length > 0 ? (root.buckets[0].label || "") : ""
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        font.family: Theme.figureFamily
        font.weight: Theme.figureWeight(false)
        font.features: ({ "tnum": 1 })
    }

    Text {
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        text: root.buckets.length > 0 ? (root.buckets[root.buckets.length - 1].label || "") : ""
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        font.family: Theme.figureFamily
        font.weight: Theme.figureWeight(false)
        font.features: ({ "tnum": 1 })
    }
}
