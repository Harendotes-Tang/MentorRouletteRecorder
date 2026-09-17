import QtQuick
import MentorRecorder

// The 8 px horizontal count bar of the 副本统计 / 职业统计 pages:
// an inset track with a hairline frame and a square gold-3 -> gold-2 fill.
Rectangle {
    id: root

    property real ratio: 0

    implicitHeight: 8
    radius: Theme.eorzea ? Theme.radiusXxs : 4
    color: Theme.eorzea ? Theme.insetBackgroundStrong : Theme.fill
    border.width: Theme.eorzea ? 1 : 0
    border.color: Theme.border
    clip: true

    Rectangle {
        id: fill

        anchors.left: parent.left
        anchors.top: parent.top
        anchors.bottom: parent.bottom
        anchors.margins: Theme.eorzea ? 1 : 0
        width: Math.max(0, (parent.width - (Theme.eorzea ? 2 : 0))
                        * Math.max(0, Math.min(1, root.ratio)))
        radius: Theme.eorzea ? 0 : parent.radius

        gradient: Gradient {
            orientation: Gradient.Horizontal
            GradientStop {
                position: 0.0
                color: Theme.eorzea ? Theme.barFillStart : Theme.accent
            }
            GradientStop {
                position: 1.0
                color: Theme.eorzea ? Theme.barFillEnd : Theme.accent
            }
        }
    }
}
