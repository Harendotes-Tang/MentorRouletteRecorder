import QtQuick
import MentorRecorder

// `.dialog`: the panel gradient, a gold-3 frame and the 2 px gradient rule
// that `.dialog::before` paints across the top.
Rectangle {
    id: root

    property bool opaque: false
    function surfaceColor(value) {
        return opaque ? Qt.rgba(value.r, value.g, value.b, 1) : value
    }

    radius: Theme.radiusL
    border.width: 1
    border.color: Theme.eorzea ? Theme.gold3 : Theme.border

    gradient: Gradient {
        GradientStop {
            position: 0.0
            color: root.surfaceColor(Theme.eorzea ? Theme.panelTop : Theme.surface)
        }
        GradientStop {
            position: 1.0
            color: root.surfaceColor(Theme.eorzea ? Theme.panelBottom : Theme.surface)
        }
    }

    // The dialog's chips, cards and rows listen with TapHandlers, which take only a passive
    // grab and leave the press unaccepted, so it kept travelling down to the page behind the
    // modal dialog: a history row under it was re-selected mid-edit and the correction was
    // saved onto that run. This handler accepts the press, which ends the delivery here.
    TapHandler {
        acceptedButtons: Qt.AllButtons
        gesturePolicy: TapHandler.ReleaseWithinBounds
    }

    Rectangle {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.leftMargin: 18
        anchors.rightMargin: 18
        height: 2
        visible: Theme.eorzea

        gradient: Gradient {
            orientation: Gradient.Horizontal
            GradientStop { position: 0.0; color: "transparent" }
            GradientStop { position: 0.5; color: Theme.gold2 }
            GradientStop { position: 1.0; color: "transparent" }
        }
    }
}
