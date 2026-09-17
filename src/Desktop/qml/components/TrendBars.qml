import QtQuick
import QtQuick.Controls
import MentorRecorder

Item {
    id: root

    property var buckets: []

    implicitHeight: 156

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
                required property var modelData

                width: root.buckets.length > 0
                       ? (bars.width - bars.spacing * (root.buckets.length - 1)) / root.buckets.length
                       : 0
                height: bars.height

                Rectangle {
                    id: bar

                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: Math.max(2, parent.height * Number(modelData.count || 0) / root.maxCount)
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

                HoverHandler { id: hover }
                ToolTip.visible: hover.hovered
                ToolTip.text: (modelData.label || "") + " · "
                              + String(modelData.count || 0) + qsTr(" 次")
            }
        }
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
