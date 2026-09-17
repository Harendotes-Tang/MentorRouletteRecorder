import QtQuick
import QtQuick.Layouts
import MentorRecorder

// `.card-kicker`: a small gold ◆ followed by a wide-tracked label (eorzea);
// a plain t3 label at 600 in the text colour (classic).
RowLayout {
    id: root

    property alias text: label.text
    property color color: Theme.eorzea ? Theme.gold : Theme.textPrimary

    spacing: 8

    Text {
        visible: Theme.eorzea
        text: "◆"
        color: root.color
        opacity: 0.9
        font.pixelSize: 7
        Layout.alignment: Qt.AlignVCenter
    }

    Text {
        id: label
        Layout.fillWidth: true
        color: root.color
        font.pixelSize: Theme.eorzea ? 12 : 13
        font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
        font.letterSpacing: Theme.eorzea ? 0.9 : 0
        elide: Text.ElideRight
    }
}
