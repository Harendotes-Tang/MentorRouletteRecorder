import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// `.card-kicker`: a small gold ◆ followed by a wide-tracked label (eorzea);
// a plain t3 label at 600 in the text colour (classic).
RowLayout {
    id: root

    property alias text: label.text
    property color color: Theme.eorzea ? Theme.gold : Theme.textPrimary

    spacing: 8

    Image {
        visible: Theme.eorzea
        source: Lucide.source("diamond", root.color)
        sourceSize: Qt.size(16, 16)
        opacity: 0.9
        Layout.preferredWidth: 8
        Layout.preferredHeight: 8
        Layout.alignment: Qt.AlignVCenter
        Accessible.ignored: true
    }

    Text {
        id: label
        textFormat: Text.PlainText
        Layout.fillWidth: true
        color: root.color
        font.pixelSize: Theme.eorzea ? 12 : 13
        font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
        font.letterSpacing: Theme.eorzea ? 0.9 : 0
        elide: Text.ElideRight
    }
}
