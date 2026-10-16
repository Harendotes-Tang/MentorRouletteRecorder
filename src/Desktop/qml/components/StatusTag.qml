import QtQuick
import QtQuick.Layouts
import MentorRecorder

// `.tag.dg-tone`: a status pill with a 6 px dot in the tone's colour before its
// label. Eorzea keeps the framed, translucent plate every tag has; classic tints
// the plate by tone (workbench's --dg-ok-bg / -warn-bg / -bad-bg / -off-bg).
Rectangle {
    id: root

    property string text: ""
    // "ok" | "warn" | "bad" | "off"
    property string tone: "off"
    // Tests read the label by this name.
    property string labelObjectName: ""
    readonly property string variant: Theme.toneVariant(tone)
    readonly property color foreground: Theme.tagForeground(variant)

    implicitHeight: Theme.eorzea ? 22 : 20
    implicitWidth: row.implicitWidth + 18
    radius: Theme.eorzea ? Theme.radiusS : height / 2
    color: Theme.tagBackground(variant)
    border.width: Theme.eorzea ? 1 : 0
    border.color: Theme.tagBorder(variant)
    Behavior on color { ColorAnimation { duration: Theme.motionControl } }

    RowLayout {
        id: row
        anchors.centerIn: parent
        spacing: 6

        Rectangle {
            Layout.alignment: Qt.AlignVCenter
            implicitWidth: 6
            implicitHeight: 6
            radius: 3
            color: root.foreground
        }

        Text {
            objectName: root.labelObjectName
            textFormat: Text.PlainText
            text: root.text
            color: root.foreground
            font.pixelSize: Theme.fs(11)
            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
            font.letterSpacing: Theme.eorzea ? 0.2 : 0
        }
    }
}
