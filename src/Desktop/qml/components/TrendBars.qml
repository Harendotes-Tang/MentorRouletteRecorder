pragma ComponentBehavior: Bound

import QtQuick
import MentorRecorder 1.0

Item {
    id: root

    property var buckets: []
    /// The bar under the pointer, -1 for none.
    property int hoveredIndex: -1

    implicitHeight: 156

    function countAt(index) {
        return index >= 0 && index < buckets.length ? Number(buckets[index].count || 0) : 0
    }

    readonly property int maxCount: {
        let maxValue = 1
        for (let i = 0; i < buckets.length; ++i)
            maxValue = Math.max(maxValue, Number(buckets[i].count || 0))
        return maxValue
    }

    Rectangle {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.bottomMargin: 18
        height: 1
        color: Theme.eorzea ? Theme.gold3 : Theme.border
    }

    Row {
        id: bars

        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.bottom: parent.bottom
        anchors.bottomMargin: 19
        spacing: 4

        Repeater {
            model: root.buckets

            delegate: Item {
                id: bucket
                required property var modelData
                required property int index

                width: root.buckets.length > 0
                       ? (bars.width - bars.spacing * (root.buckets.length - 1)) / root.buckets.length
                       : 0
                height: bars.height

                Rectangle {
                    id: bar

                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: Math.max(2, parent.height * Number(bucket.modelData.count || 0) / root.maxCount)
                    radius: Theme.eorzea ? Theme.radiusXxs : 3
                    readonly property color classicColor: hover.hovered ? Theme.accentStrong
                                                                       : Theme.accent
                    opacity: hover.hovered ? 1.0 : (Theme.eorzea ? 0.92 : 1.0)

                    gradient: Gradient {
                        GradientStop {
                            position: 0.0
                            color: Theme.eorzea ? Theme.barFillEnd : bar.classicColor
                        }
                        GradientStop {
                            position: 1.0
                            color: Theme.eorzea ? Theme.barFillStart : bar.classicColor
                        }
                    }
                }

                HoverHandler {
                    id: hover
                    onHoveredChanged: {
                        if (hovered) root.hoveredIndex = bucket.index
                        else if (root.hoveredIndex === bucket.index) root.hoveredIndex = -1
                    }
                }
            }
        }
    }

    ChartTip {
        id: tip
        objectName: "trendTip"
        // The last bucket stays in place while the bubble fades out.
        property int index: 0
        readonly property var bucket: index < root.buckets.length ? root.buckets[index] : ({})
        readonly property real slot: root.buckets.length > 0
                                     ? (bars.width + bars.spacing) / root.buckets.length : 0
        // An empty day has no bar to explain: nothing is shown over it.
        shown: root.countAt(root.hoveredIndex) > 0
        text: qsTr("%1 · %2 次").arg(bucket.label || "").arg(bucket.count || 0)
        barCenter: bars.x + index * slot + (slot - bars.spacing) / 2
        barTop: bars.y + bars.height * (1 - Number(bucket.count || 0) / root.maxCount)
    }
    onHoveredIndexChanged: if (countAt(hoveredIndex) > 0) tip.index = hoveredIndex

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
