import QtQuick
import MentorRecorder

// `.tag`: eorzea draws a 1 px currentColor frame on a translucent plate;
// classic is workbench's tinted pill (t6, regular weight).
Rectangle {
    id: root

    property string text: ""
    property string variant: "neutral"

    implicitHeight: Theme.eorzea ? 22 : 18
    implicitWidth: label.implicitWidth + 16
    radius: Theme.eorzea ? Theme.radiusS : height / 2
    color: Theme.tagBackground(variant)
    border.width: Theme.eorzea ? 1 : 0
    border.color: Theme.tagBorder(variant)

    Text {
        id: label
        anchors.centerIn: parent
        text: root.text
        color: Theme.tagForeground(root.variant)
        font.pixelSize: Theme.fs(11)
        font.weight: Theme.eorzea ? Font.Bold : Font.Normal
        font.letterSpacing: Theme.eorzea ? 0.2 : 0
    }
}
