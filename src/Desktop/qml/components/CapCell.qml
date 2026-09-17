import QtQuick
import QtQuick.Layouts
import MentorRecorder

// One tile of the capture-diagnostics grid: kicker, value, sub-label.
Card {
    id: root

    property string kicker: ""
    property string value: "—"
    property string sub: ""
    property color valueColor: Theme.textPrimary

    Layout.fillWidth: true
    Layout.preferredHeight: 84
    padding: 14
    spacing: 4

    Text {
        Layout.fillWidth: true
        text: root.kicker
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        font.bold: true
        elide: Text.ElideRight
    }

    Text {
        Layout.fillWidth: true
        text: root.value
        color: root.valueColor
        font.family: Theme.figureFamily
        font.pixelSize: Theme.fs(15)
        font.weight: Theme.figureWeight(true)
        font.features: ({ "tnum": 1 })
        elide: Text.ElideRight
    }

    Text {
        Layout.fillWidth: true
        text: root.sub
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        elide: Text.ElideRight
    }
}
