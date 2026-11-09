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
    implicitHeight: 32
    implicitWidth: Math.ceil(label.implicitWidth) + 64
    leftPadding: 12
    rightPadding: 10
    focusPolicy: Qt.StrongFocus
    Accessible.name: text
    onClicked: popup.visible ? popup.close() : popup.open()
    onVisibleChanged: if (!visible) popup.close()
    onEnabledChanged: if (!enabled) popup.close()
    onWindowActiveChanged: if (!windowActive) popup.close()
    background: Rectangle {
        radius: 2
        color: control.selected || popup.visible ? control.filterTint : Theme.fill
        border.color: control.selected || popup.visible || control.activeFocus
                      ? control.filterAccent : Theme.neutral300
        border.width: 1
    }
    contentItem: RowLayout {
        spacing: 6
        Image {
            Layout.preferredWidth: 14; Layout.preferredHeight: 14
            source: Lucide.source(control.iconName, control.selected ? control.filterAccent : Theme.textSecondary)
            sourceSize: Qt.size(28, 28)
        }
        Text {
            id: label
            Layout.fillWidth: true
            text: control.text
            font.pixelSize: Theme.fs(13)
            font.bold: control.selected
            color: control.selected ? control.filterAccent : Theme.textPrimary
        }
        Image {
            Layout.preferredWidth: 12; Layout.preferredHeight: 12
            source: Lucide.source("chevron-down", Theme.textSecondary)
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
            radius: 2
            color: Theme.surface
            border.color: control.filterAccent
            border.width: 1
        }
    }
}
