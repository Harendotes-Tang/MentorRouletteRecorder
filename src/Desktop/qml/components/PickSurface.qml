import QtQuick
import MentorRecorder

// `.pick`: a selectable card or chip of the record wizard. An inset plate with
// a gold-3 hover frame (eorzea) or no frame at rest (classic); the chosen one
// is accent-100 with a doubled accent frame.
//
// A pure view like ToggleSwitch: `selected` is never written from inside, so
// the owner's binding survives every click. `picked()` asks the owner to
// select this one.
Rectangle {
    id: root

    property bool selected: false
    property string accessibleName: ""

    signal picked()

    readonly property bool hovered: hover.hovered

    radius: Theme.radiusS
    // `.row-c:active`: accent-100 while pressed, as a selected row.
    color: selected || tap.pressed ? Theme.accentMuted : Theme.insetBackground
    border.width: selected ? 2 : 1
    border.color: {
        if (selected)
            return Theme.eorzea ? Theme.gold : Theme.accent
        if (hover.hovered || activeFocus)
            return Theme.eorzea ? Theme.gold3 : Theme.neutral300
        return Theme.eorzea ? Theme.insetBorder : Theme.clear(Theme.border)
    }
    scale: tap.pressed ? 0.98 : 1.0

    Behavior on color { ColorAnimation { duration: Theme.motionFast } }
    Behavior on scale {
        NumberAnimation {
            duration: Theme.motionControl
            easing.type: Easing.Bezier
            easing.bezierCurve: Theme.curveStandard
        }
    }

    activeFocusOnTab: true
    Accessible.role: Accessible.RadioButton
    Accessible.name: root.accessibleName
    Accessible.checkable: true
    Accessible.checked: root.selected
    Accessible.onPressAction: root.picked()
    Keys.onSpacePressed: root.picked()
    Keys.onReturnPressed: root.picked()
    Keys.onEnterPressed: root.picked()

    HoverHandler {
        id: hover
        cursorShape: Qt.PointingHandCursor
    }

    TapHandler {
        id: tap
        onTapped: root.picked()
    }

    // Keyboard focus ring, outside the frame so it never hides the selection.
    Rectangle {
        anchors.fill: parent
        anchors.margins: -3
        radius: root.radius + 3
        color: "transparent"
        border.width: 2
        border.color: Theme.eorzea ? Theme.gold2 : Theme.accent
        visible: root.activeFocus
    }
}
