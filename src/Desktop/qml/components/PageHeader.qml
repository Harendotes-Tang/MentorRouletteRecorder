import QtQuick
import QtQuick.Layouts
import MentorRecorder

// The header every page shares: serif gold title, a muted subtitle, a slot for
// actions on the right, and the gilded rule
//   border-bottom:1px solid gold-3
//   background:linear-gradient(90deg,rule-accent,transparent 45%) bottom/100% 1px
ColumnLayout {
    id: root

    property string title: ""
    property string subtitle: ""
    default property alias actions: actionRow.data

    Layout.fillWidth: true
    spacing: 10

    RowLayout {
        Layout.fillWidth: true
        spacing: 16

        HeadingLabel {
            text: root.title
            font.pixelSize: Theme.pageTitleSize
        }

        Text {
            Layout.alignment: Qt.AlignBottom
            Layout.bottomMargin: 4
            Layout.fillWidth: true
            text: root.subtitle
            color: Theme.textSecondary
            // workbench: the subtitle next to the h2 is t5.
            font.pixelSize: Theme.eorzea ? 13 : 12
            elide: Text.ElideRight
        }

        RowLayout {
            id: actionRow
            Layout.alignment: Qt.AlignBottom
            spacing: 8
        }
    }

    Item {
        Layout.fillWidth: true
        Layout.preferredHeight: 1

        Rectangle {
            anchors.fill: parent
            color: Theme.eorzea ? Theme.gold3 : Theme.border
        }

        // The bright accent segment fading out at 45 %.
        Rectangle {
            anchors.left: parent.left
            anchors.top: parent.top
            anchors.bottom: parent.bottom
            width: parent.width * 0.45
            visible: Theme.eorzea
            gradient: Gradient {
                orientation: Gradient.Horizontal
                GradientStop { position: 0.0; color: Theme.ruleAccent }
                GradientStop { position: 1.0; color: "transparent" }
            }
        }
    }
}
