import QtQuick
import QtQuick.Layouts
import MentorRecorder

// A `.panel` of the settings page.
//
// Row panels (the default) hold SettingsRow entries, which carry their own
// 12 px padding and dividers; the panel adds only `padding:8px 24px 12px` and a
// kicker with `padding:12px 0 4px`. Free panels (`freeLayout: true`) use the
// prototype's `padding:20px 24px; gap:12-14px` block.
Card {
    id: root

    property string kicker: ""
    property bool freeLayout: false

    Layout.fillWidth: true
    horizontalPadding: Theme.eorzea ? 24 : 20
    verticalPadding: freeLayout ? (Theme.eorzea ? 20 : 18) : (Theme.eorzea ? 8 : 10)
    spacing: freeLayout ? 12 : 0

    CardKicker {
        Layout.fillWidth: true
        Layout.topMargin: root.freeLayout ? 0 : 12
        Layout.bottomMargin: root.freeLayout ? 0 : 4
        visible: root.kicker.length > 0
        text: root.kicker
    }
}
