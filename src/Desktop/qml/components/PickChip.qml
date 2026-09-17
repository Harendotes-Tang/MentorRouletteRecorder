import QtQuick
import MentorRecorder

// `.tag.chip[data-on]` of the record wizard: one choice of a single-choice row
// (等级区间, 难度, 最近打过, 未知职业). Unlike Chip it never flips itself - the
// owner binds `checked` and answers `picked()` - so a row of them always shows
// the owner's value.
Rectangle {
    id: root

    property string text: ""
    property bool checked: false

    signal picked()

    implicitHeight: Theme.eorzea ? 26 : 22
    implicitWidth: label.implicitWidth + 16
    radius: Theme.eorzea ? Theme.radiusS : Theme.radiusXs
    color: root.checked
           ? Theme.accentMuted
           : (Theme.eorzea ? "transparent" : Theme.fill)
    border.width: Theme.eorzea ? 1 : 0
    border.color: root.checked
                  ? Theme.gold
                  : (hover.hovered ? Theme.gold3 : Theme.border)
    scale: tap.pressed ? 0.96 : 1.0

    Behavior on scale { NumberAnimation { duration: Theme.motionFast; easing.type: Easing.OutCubic } }

    activeFocusOnTab: true
    Accessible.role: Accessible.RadioButton
    Accessible.name: root.text
    Accessible.checkable: true
    Accessible.checked: root.checked
    Accessible.onPressAction: root.picked()
    Keys.onSpacePressed: root.picked()
    Keys.onReturnPressed: root.picked()

    HoverHandler {
        id: hover
        cursorShape: Qt.PointingHandCursor
    }

    TapHandler {
        id: tap
        onTapped: root.picked()
    }

    Text {
        id: label
        anchors.centerIn: parent
        text: root.text
        color: root.checked
               ? (Theme.eorzea ? (Theme.dark ? Theme.gold2 : Theme.accent700) : Theme.accent)
               : Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        font.weight: root.checked || Theme.eorzea ? Font.DemiBold : Font.Normal
    }

    Rectangle {
        anchors.fill: parent
        anchors.margins: -2
        radius: root.radius + 2
        color: "transparent"
        border.width: 2
        border.color: Theme.eorzea ? Theme.gold2 : Theme.accent
        visible: root.activeFocus
    }
}
