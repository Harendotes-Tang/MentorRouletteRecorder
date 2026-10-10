import QtQuick
import QtQuick.Templates as T
import MentorRecorder

// `.sw`: 40x22 square-ish inset track in the eorzea style; in the classic one
// workbench's 36x20 pill, neutral-300 when off, accent when on, with a 16 px
// white knob.
FocusScope {
    id: root

    property string accessibleName: ""
    activeFocusOnTab: enabled

    // A pure view of the owning setting. The switch does NOT flip itself:
    // assigning to `checked` from inside destroys the caller's binding for
    // good, after which the knob shows the last tap instead of the stored
    // value, and a rejected, clamped or externally made change is never drawn.
    property bool checked: false

    /// Emitted with the value the user asked for. The owner writes it; the
    /// binding on checked brings it back.
    signal toggled(bool checked)

    // `.sw:active::after`: the knob stretches from 16 to 20 px while pressed,
    // growing away from the side it rests on.
    readonly property bool pressed: activation.pressed
    readonly property int knobSize: 16

    implicitWidth: Theme.eorzea ? 40 : 36
    implicitHeight: Theme.eorzea ? 22 : 20
    opacity: enabled ? 1.0 : 0.4

    Rectangle {
        id: track

        anchors.fill: parent
        radius: Theme.eorzea ? Theme.radiusS : height / 2
        color: Theme.eorzea
               ? (root.checked ? Theme.switchTrackOn : Theme.insetBackgroundStrong)
               : (root.checked ? Theme.accent : Theme.neutral300)
        border.width: Theme.eorzea ? 1 : 0
        border.color: root.checked ? Theme.gold3 : Theme.border

        Behavior on color {
            ColorAnimation { duration: Theme.motionControl }
        }
    }

    // box-shadow: 0 1px 3px rgba(0,0,0,.2) under the classic knob.
    Rectangle {
        visible: !Theme.eorzea
        width: knob.width
        height: knob.height
        radius: knob.radius
        x: knob.x
        y: knob.y + 1
        color: "#33000000"
    }

    Rectangle {
        id: knob

        width: root.pressed ? root.knobSize + 4 : root.knobSize
        height: root.knobSize
        radius: Theme.eorzea ? Theme.radiusXs : height / 2
        y: (root.height - height) / 2
        x: root.checked ? root.width - width - 2 : 2
        gradient: Gradient {
            GradientStop {
                position: 0.0
                color: Theme.eorzea ? (root.checked ? Theme.switchKnobTop : "#8f97a6") : "#ffffff"
            }
            GradientStop {
                position: 1.0
                color: Theme.eorzea ? (root.checked ? Theme.switchKnobBottom : "#5a6272") : "#ffffff"
            }
        }

        // `left .2s` / `width .15s`, both on workbench's --ease.
        Behavior on x {
            NumberAnimation {
                duration: Theme.motionControl
                easing.type: Easing.Bezier
                easing.bezierCurve: Theme.curveStandard
            }
        }
        Behavior on width {
            NumberAnimation {
                duration: Math.round(Theme.motionControl * 0.75)
                easing.type: Easing.Bezier
                easing.bezierCurve: Theme.curveStandard
            }
        }
    }

    // `.sw:hover { filter: brightness(.96) }`: a 4 % black veil over the whole
    // switch. It fades by opacity, never by colour.
    Rectangle {
        anchors.fill: parent
        radius: track.radius
        color: "#0a000000"
        visible: opacity > 0
        opacity: hover.hovered && root.enabled ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: Theme.motionControl } }
    }

    HoverHandler {
        id: hover
    }

    // Native checkbox owns Space, press/release cancellation and accessibility.
    // Returning the owner's current value preserves rejected/asynchronous writes
    // instead of leaving an optimistic checked state inside the visual control.
    T.CheckBox {
        id: activation
        objectName: "toggleActivation"
        anchors.fill: parent
        focus: true
        focusPolicy: Qt.NoFocus
        checked: root.checked
        Accessible.name: root.accessibleName
        nextCheckState: function() {
            root.toggled(!root.checked)
            return root.checked ? Qt.Checked : Qt.Unchecked
        }
    }

    // Retain the record wizard's existing Return shortcut at the shared control.
    Keys.onReturnPressed: function(event) {
        if (!event.isAutoRepeat)
            root.toggled(!root.checked)
    }

    Rectangle {
        objectName: "toggleFocusRing"
        anchors.fill: parent
        anchors.margins: -3
        radius: track.radius + 3
        color: "transparent"
        border.width: 2
        border.color: Theme.eorzea ? Theme.gold2 : Theme.accent
        visible: root.activeFocus
    }
}
