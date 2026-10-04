import QtQuick
import QtQuick.Controls
import MentorRecorder

// `.input`: an inset well with a gold focus ring (eorzea); workbench's 30 px
// surface field with a neutral-300 frame and an accent focus ring (classic).
TextField {
    id: control

    focusPolicy: Qt.TabFocus
    selectByMouse: true
    implicitHeight: Theme.eorzea ? 32 : 30
    color: Theme.textPrimary
    placeholderTextColor: Theme.textMuted
    font.pixelSize: Theme.fs(13)
    leftPadding: 10
    rightPadding: 10

    /// An example a player may take as it is: Tab on the empty field enters it
    /// (owner's request B3-2). Only for free-text reason and note fields; in a
    /// number or configuration field an example would be wrong data. Such a field
    /// ends its placeholder with exampleHint, so the shortcut can be found.
    property string exampleText: ""
    readonly property string exampleHint: qsTr("（按 Tab 填入）")

    // A read-only field scrolls to the end of its text by default, which hides
    // the beginning of a long path.
    onTextChanged: if (readOnly) Qt.callLater(function() { cursorPosition = 0 })

    // insert() is the path typing takes: a binding on text stays in place, the
    // change can be undone, and whatever reacts to the text sees it as typed.
    // Shift+Tab, a field that holds text, and a Tab while an input method is
    // composing all move focus as before.
    Keys.onTabPressed: function(event) {
        event.accepted = control.exampleText.length > 0 && control.length === 0
                         && control.preeditText.length === 0 && !control.readOnly
                         && event.modifiers === Qt.NoModifier
        if (!event.accepted)
            return
        control.insert(0, control.exampleText)
        control.cursorPosition = control.length
    }

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

        // The focus ring of `.input:focus-visible`.
        Rectangle {
            anchors.fill: parent
            anchors.margins: -2
            radius: parent.radius + 2
            visible: Theme.eorzea && control.activeFocus
            color: "transparent"
            border.width: 2
            border.color: Theme.accentMuted
        }

        // workbench `.input:focus-visible`: 3 px accent-100 ring outside the frame.
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
}
