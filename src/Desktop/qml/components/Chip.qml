import QtQuick
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// `.chip`: a tag-shaped toggle used by the history filter row. Classic is a
// 4x8-padded fill plate with a 4 px radius; accent-100 + accent when on.
Rectangle {
    id: root

    property string text: ""
    // A pure view of the owner's state, like ToggleSwitch: the chip never flips
    // itself, since assigning to `checked` from inside would destroy the
    // caller's binding for good (review L3-1).
    property bool checked: false
    // A chip that only removes a filter (e.g. the duty drill-down) shows a ×.
    property bool removable: false

    /// Emitted with the value the user asked for. The owner stores it; the
    /// binding on checked brings it back.
    signal toggled(bool checked)
    // Emitted instead of toggled when the chip is removable.
    signal removed()

    implicitHeight: Theme.eorzea ? 28 : 22
    implicitWidth: label.implicitWidth + 16 + (root.removable ? 18 : 0)
    radius: Theme.eorzea ? Theme.radiusS : Theme.radiusXs
    color: root.checked
           ? Theme.accentMuted
           : (Theme.eorzea ? "transparent" : Theme.fill)
    border.width: Theme.eorzea ? 1 : 0
    border.color: root.checked
                  ? Theme.gold
                  : (hover.hovered ? Theme.gold3 : Theme.border)

    HoverHandler { id: hover }

    Row {
        anchors.centerIn: parent
        spacing: 6
        Text {
            id: label
            textFormat: Text.PlainText
            text: root.text
            color: root.checked
                   ? (Theme.eorzea ? (Theme.dark ? Theme.gold2 : Theme.accent700) : Theme.accent)
                   : Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
        }
        Image {
            visible: root.removable
            width: 12
            height: 12
            y: (label.height - height) / 2
            source: Lucide.source("x", label.color)
            sourceSize: Qt.size(24, 24)
            Accessible.ignored: true
        }
    }

    TapHandler {
        onTapped: {
            if (root.removable) {
                root.removed()
                return
            }
            root.toggled(!root.checked)
        }
    }
}
