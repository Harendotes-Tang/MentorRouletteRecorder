import QtQuick
import MentorRecorder

// `.tb`: the one-character duty-category badge (迷 / 讨 / 大 / 团 / 令), white
// on the category's colour.
Rectangle {
    id: root

    property string text: "?"
    property color tone: Theme.neutral500

    implicitWidth: Math.max(16, label.implicitWidth + 6)
    implicitHeight: 16
    radius: Theme.radiusXxs
    color: tone

    Text {
        id: label
        anchors.centerIn: parent
        text: root.text
        color: Theme.badgeForeground
        font.pixelSize: 10
        font.weight: Font.ExtraBold
    }
}
