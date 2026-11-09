pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "Lucide.js" as Lucide

Button {
    id: control
    property string iconName: ""
    property bool selected: false
    property bool invalid: false
    property real menuWidth: 280
    readonly property color filterAccent: invalid ? Theme.red : (Theme.dark ? "#ff9365" : "#ba350b")
    readonly property bool windowActive: !Window.window || (Window.window.visible && Window.window.visibility !== Window.Minimized)
    readonly property color filterTint: Theme.dark ? "#36251f" : "#f7e5dc"
    readonly property alias menu: popup
    property alias menuContent: popup.contentItem
    implicitHeight: Theme.eorzea ? 32 : 30
    implicitWidth: Math.ceil(contentItem.implicitWidth) + leftPadding + rightPadding
    leftPadding: 10
    rightPadding: 10
    focusPolicy: Qt.StrongFocus
    Accessible.name: text
    onClicked: popup.visible ? popup.close() : popup.open()
    onVisibleChanged: if (!visible) popup.close()
    onEnabledChanged: if (!enabled) popup.close()
    onWindowActiveChanged: if (!windowActive) popup.close()
    background: Rectangle {
        radius: Theme.radiusS
        color: Theme.eorzea
               ? (Theme.dark ? "#47000000" : "#8cffffff")
               : (control.enabled ? Theme.surface : Theme.fill)
        border.color: control.invalid ? Theme.red
                      : control.activeFocus ? (Theme.eorzea ? Theme.gold : Theme.accent)
                      : control.hovered && control.enabled ? Theme.neutral400
                      : Theme.eorzea ? Theme.border : Theme.neutral300
        border.width: 1
        Behavior on border.color { ColorAnimation { duration: Theme.motionControl } }
        Rectangle {
            anchors.fill: parent
            anchors.margins: -3
            radius: parent.radius + 3
            visible: !Theme.eorzea && control.activeFocus
            color: "transparent"
            border.width: 3
            border.color: Theme.accentMuted
        }
    }
    contentItem: RowLayout {
        spacing: 6
        Image {
            Layout.preferredWidth: 14; Layout.preferredHeight: 14
            source: Lucide.source(control.iconName, control.selected ? control.filterAccent : Theme.textSecondary)
            sourceSize: Qt.size(28, 28)
        }
        Text { renderType: Theme.bodyRenderType;
            id: label
            Layout.fillWidth: true
            text: control.text
            font.pixelSize: Theme.fs(13)
            color: Theme.textPrimary
            verticalAlignment: Text.AlignVCenter
        }
        Image {
            Layout.preferredWidth: 12; Layout.preferredHeight: 12
            source: Lucide.source("chevron-down", Theme.eorzea ? Theme.gold : Theme.textSecondary)
            sourceSize: Qt.size(24, 24)
            rotation: popup.visible ? 180 : 0
        }
    }
    Popup {
        id: popup
        x: control.width - width
        y: control.height + 5
        width: control.menuWidth
        padding: 12
        focus: true
        closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutsideParent
        onOpened: contentItem.forceActiveFocus(Qt.PopupFocusReason)
        onClosed: if (control.visible) control.forceActiveFocus(Qt.PopupFocusReason)
        background: Rectangle {
            radius: Theme.radiusS
            color: Theme.surface
            border.color: Theme.eorzea ? Theme.gold3 : Theme.border
            border.width: 1
        }
    }
}
