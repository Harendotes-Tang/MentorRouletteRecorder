import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// `.ev-hint`: a secondary 12 px line, optionally led by a small icon.
RowLayout {
    id: root

    property alias text: label.text
    property string iconName: ""
    property color color: Theme.textSecondary

    spacing: 6
    Layout.fillWidth: true

    EvidenceIcon {
        visible: root.iconName.length > 0
        Layout.alignment: Qt.AlignTop
        Layout.topMargin: 2
        name: root.iconName
        color: root.color
        size: 13
    }

    Text {
        id: label
        Layout.fillWidth: true
        textFormat: Text.PlainText
        color: root.color
        font.pixelSize: Theme.fs(12)
        lineHeight: 1.35
        wrapMode: Text.Wrap
    }
}
