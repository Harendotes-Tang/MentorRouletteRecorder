pragma ComponentBehavior: Bound

import QtQuick
import MentorRecorder 1.0

Item {
    id: root

    property var buckets: []

    implicitHeight: 156

    readonly property real barWidth: buckets.length > 0
        ? Math.max(0, (bars.width - bars.spacing * (buckets.length - 1)) / buckets.length) : 0
    readonly property int hoveredIndex: hoverArea.containsMouse && barWidth > 0
        && hoverArea.mouseX >= 0 && hoverArea.mouseX < bars.width
        ? Math.floor(hoverArea.mouseX / (barWidth + bars.spacing)) : -1
    readonly property point hoveredBarTop: Qt.point(
        hoveredIndex * (barWidth + bars.spacing) + barWidth / 2,
        bars.height - barHeight(buckets[hoveredIndex]?.count || 0))

    readonly property int maxCount: {
        let maxValue = 1
        for (let i = 0; i < buckets.length; ++i)
            maxValue = Math.max(maxValue, Number(buckets[i].count || 0))
        return maxValue
    }

    function barHeight(count: real): real {
        return Math.max(2, bars.height * count / maxCount)
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
        objectName: "trendFallbackBars"

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
                required property int index
                required property var modelData

                readonly property bool hovered: root.hoveredIndex === index
                width: root.barWidth
                height: bars.height

                Rectangle {
                    id: bar
                    objectName: "trendFallbackBar"

                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: root.barHeight(Number(bucket.modelData.count || 0))
                    radius: Theme.eorzea ? Theme.radiusXxs : 3
                    readonly property color classicColor: bucket.hovered ? Theme.accentStrong : Theme.accent
                    opacity: bucket.hovered || !Theme.eorzea ? 1.0 : 0.92

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
            }
        }
    }

    MouseArea {
        id: hoverArea
        anchors.fill: bars
        hoverEnabled: true
        acceptedButtons: Qt.NoButton
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
