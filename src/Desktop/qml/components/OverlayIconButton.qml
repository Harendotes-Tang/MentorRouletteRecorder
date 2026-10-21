import QtQuick
import QtQuick.Controls
import MentorRecorder

// `.ov-ib`: a 24 px ghost icon button of the float and the popup. `on` paints
// it in the accent (the open 自定义 panel, the bell while prompts are on).
AbstractButton {
    id: control

    property string iconName: ""
    property string tip: ""
    // What the tip keeps clear of: the button itself, or a bar the button sits in.
    property Item tipClearOf: control
    property bool on: false
    property real buttonSize: 24

    implicitWidth: buttonSize
    implicitHeight: buttonSize
    hoverEnabled: true
    focusPolicy: Qt.TabFocus
    opacity: enabled ? 1 : 0.3
    Accessible.name: tip
    scale: Theme.motion && control.down ? 0.94 : 1
    Behavior on scale { NumberAnimation { duration: Theme.motionFast } }

    readonly property color foreground: {
        if (control.on)
            return Theme.eorzea ? Theme.gold2 : Theme.accent
        return control.hovered ? Theme.textPrimary : Theme.textSecondary
    }

    background: Rectangle {
        radius: Theme.radiusS
        color: control.on ? (Theme.eorzea ? Theme.fillStrong : Theme.accentMuted)
                          : (control.hovered && control.enabled ? Theme.fill : Theme.clear(Theme.fill))
        Behavior on color { ColorAnimation { duration: Theme.motionFast } }
        border.width: control.activeFocus ? 2 : 0
        border.color: Theme.eorzea ? Theme.gold2 : Theme.accent
    }

    contentItem: Item {
        OverlayIcon {
            anchors.centerIn: parent
            name: control.iconName
            color: control.foreground
            size: 14
        }
    }

    OverlayToolTip {
        objectName: control.objectName.length > 0 ? control.objectName + "Tip" : ""
        clearOf: control.tipClearOf
        text: control.tip
        visible: control.tip.length > 0 && control.hovered && control.enabled
    }
}
