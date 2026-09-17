import QtQuick
import MentorRecorder

// `.chip`: a tag-shaped toggle used by the history filter row. Classic is a
// 4x8-padded fill plate with a 4 px radius; accent-100 + accent when on.
Rectangle {
    id: root

    property string text: ""
    property bool checked: false
    // A chip that only removes a filter (e.g. the duty drill-down) shows a ×.
    property bool removable: false

    signal toggled(bool checked)
    // Emitted instead of toggled when the chip is removable.
    signal removed()

    implicitHeight: Theme.eorzea ? 28 : 22
    implicitWidth: label.implicitWidth + 16
    radius: Theme.eorzea ? Theme.radiusS : Theme.radiusXs
    color: root.checked
           ? Theme.accentMuted
           : (Theme.eorzea ? "transparent" : Theme.fill)
    border.width: Theme.eorzea ? 1 : 0
    border.color: root.checked
                  ? Theme.gold
                  : (hover.hovered ? Theme.gold3 : Theme.border)

    HoverHandler { id: hover }

    Text {
        id: label
        anchors.centerIn: parent
        text: root.removable ? root.text + "  ✕" : root.text
        color: root.checked
               ? (Theme.eorzea ? (Theme.dark ? Theme.gold2 : Theme.accent700) : Theme.accent)
               : Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        font.weight: Theme.eorzea ? Font.Bold : Font.Normal
    }

    TapHandler {
        onTapped: {
            if (root.removable) {
                root.removed()
                return
            }
            root.checked = !root.checked
            root.toggled(root.checked)
        }
    }
}
