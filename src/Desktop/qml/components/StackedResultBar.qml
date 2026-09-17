import QtQuick
import QtQuick.Layouts
import MentorRecorder

// The 12 px result-distribution bar: 2 px gaps between segments and a hairline
// frame in the eorzea style.
Rectangle {
    id: root

    property var buckets: []

    implicitHeight: Theme.eorzea ? 12 : 14
    radius: Theme.eorzea ? Theme.radiusXs : height / 2
    color: Theme.eorzea ? Theme.insetBackground : Theme.surfaceMuted
    border.width: Theme.eorzea ? 1 : 0
    border.color: Theme.border
    clip: true

    readonly property real totalCount: {
        let total = 0
        for (let i = 0; i < buckets.length; ++i)
            total += Number(buckets[i].count || 0)
        return total
    }

    RowLayout {
        anchors.fill: parent
        anchors.margins: Theme.eorzea ? 1 : 0
        spacing: Theme.eorzea ? 2 : 0

        Repeater {
            model: root.buckets

            delegate: Rectangle {
                required property var modelData

                Layout.fillHeight: true
                Layout.preferredWidth: root.totalCount > 0
                                       ? root.width * Number(modelData.count || 0) / root.totalCount
                                       : 0
                color: Theme.token(modelData.color_token || "")
                visible: Layout.preferredWidth > 0
            }
        }
    }
}
