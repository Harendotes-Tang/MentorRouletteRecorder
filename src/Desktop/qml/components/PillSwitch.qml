import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// A switch with a label and a description. The track is ToggleSwitch, so it
// follows the style like every other switch, but unlike ToggleSwitch it flips
// its own state.
RowLayout {
    id: root

    property alias text: label.text
    property alias description: descriptionLabel.text
    property alias checked: toggle.checked

    signal toggled(bool checked)

    spacing: 10

    ToggleSwitch {
        id: toggle
        Layout.alignment: Qt.AlignVCenter
        onToggled: function(value) {
            toggle.checked = value
            root.toggled(value)
        }
    }

    ColumnLayout {
        spacing: 2

        Text {
            id: label
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(14)
        }

        Text {
            id: descriptionLabel
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
    }
}
