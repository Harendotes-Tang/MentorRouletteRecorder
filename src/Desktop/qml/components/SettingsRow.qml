import QtQuick
import QtQuick.Layouts
import MentorRecorder

// One row of a settings panel - the prototype's
//   display:grid; grid-template-columns:1fr auto; gap:16px; padding:12px 0;
//   border-bottom:1px solid var(--color-divider)
// Label (600) with an optional sub-line on the left, the control on the right,
// and a divider underneath that a panel's last row suppresses.
Item {
    id: root

    property string label: ""
    // The label drawn as a `.card-kicker` (the 本地语音播报 header row).
    property bool labelIsKicker: false
    property string description: ""
    property color descriptionColor: Theme.textSecondary
    property bool showDivider: true
    property alias controlSpacing: controlRow.spacing
    default property alias control: controlRow.data

    Layout.fillWidth: true
    implicitWidth: textColumn.implicitWidth + 16 + controlRow.implicitWidth
    implicitHeight: Math.max(textColumn.implicitHeight, controlRow.implicitHeight) + 24

    ColumnLayout {
        id: textColumn

        anchors.left: parent.left
        anchors.right: controlRow.left
        anchors.rightMargin: 16
        anchors.verticalCenter: parent.verticalCenter
        spacing: 2

        CardKicker {
            Layout.fillWidth: true
            visible: root.labelIsKicker && root.label.length > 0
            text: root.label
        }

        Text {
            Layout.fillWidth: true
            visible: !root.labelIsKicker && root.label.length > 0
            text: root.label
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
            elide: Text.ElideRight
        }

        Text {
            Layout.fillWidth: true
            visible: root.description.length > 0
            text: root.description
            Layout.topMargin: root.labelIsKicker ? 2 : 0
            color: root.descriptionColor
            font.pixelSize: Theme.fs(12)
            lineHeight: 1.2
            wrapMode: Text.WordWrap
        }
    }

    RowLayout {
        id: controlRow

        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        spacing: 10
    }

    Rectangle {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        visible: root.showDivider
        color: Theme.border
    }
}
