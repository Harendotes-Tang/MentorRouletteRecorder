import QtQuick
import QtQuick.Controls
import MentorRecorder

// `textarea.input`: same inset well as StyledTextField, wrapping and taller.
TextArea {
    id: control

    selectByMouse: true
    wrapMode: TextEdit.Wrap
    // The contract allows 2000 characters; a pinned layout height must not
    // let the overflow paint over the rows below.
    clip: true
    color: Theme.textPrimary
    placeholderTextColor: Theme.textMuted
    font.pixelSize: Theme.fs(13)
    leftPadding: 10
    rightPadding: 10
    topPadding: 8
    bottomPadding: 8

    background: Rectangle {
        radius: Theme.radiusS
        color: Theme.eorzea
               ? (Theme.dark ? "#47000000" : "#8cffffff")
               : (control.enabled && !control.readOnly ? Theme.surface : Theme.fill)
        border.width: 1
        border.color: control.activeFocus
                      ? (Theme.eorzea ? Theme.gold : Theme.accent)
                      : (control.hovered && control.enabled
                         ? Theme.neutral400
                         : (Theme.eorzea ? Theme.border : Theme.neutral300))
        // `.input:hover:not(:focus)`: neutral-400 in both styles, faded on --dur.
        Behavior on border.color { ColorAnimation { duration: Theme.motionControl } }

        // The area clips its children, so the classic focus ring sits inside the frame.
        Rectangle {
            anchors.fill: parent
            anchors.margins: 1
            radius: Math.max(0, parent.radius - 1)
            visible: !Theme.eorzea && control.activeFocus
            color: "transparent"
            border.width: 2
            border.color: Theme.accentMuted
        }
    }
}
