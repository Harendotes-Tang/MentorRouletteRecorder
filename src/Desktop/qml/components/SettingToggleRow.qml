import QtQuick
import QtQuick.Layouts
import MentorRecorder

// Settings row whose control is a switch. Label, sub-label and divider are
// provided by SettingsRow.
SettingsRow {
    id: root

    property bool checked: false
    property bool toggleEnabled: true

    signal toggled(bool checked)

    ToggleSwitch {
        Layout.alignment: Qt.AlignVCenter
        checked: root.checked
        enabled: root.toggleEnabled
        onToggled: function(value) { root.toggled(value) }
    }
}
