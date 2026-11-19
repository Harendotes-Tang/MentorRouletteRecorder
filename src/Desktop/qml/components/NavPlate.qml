import QtQuick
import MentorRecorder 1.0

// The plate behind the sidebar's current page, drawn once and slid from entry to
// entry (owner, 2026-11-19): it eases into place instead of one plate vanishing
// and another appearing. The same look as NavItem's own plate: accent-100 in the
// classic theme, the gold gradient with its 2 px bar in eorzea.
Rectangle {
    id: plate

    /// The entry of the current page; null when that page has no entry here.
    property Item target: null
    /// Slides only between two entries: the first placement, and one after the
    /// plate was hidden, are immediate.
    property bool settled: false

    visible: target !== null
    y: target ? target.y : 0
    height: target ? target.height : 0
    radius: Theme.radiusS
    color: Theme.eorzea ? "transparent" : Theme.accentMuted
    onTargetChanged: {
        if (!target) settled = false
        else Qt.callLater(function() { plate.settled = plate.target !== null })
    }

    Behavior on y {
        enabled: plate.settled && Theme.motion
        NumberAnimation { duration: 260; easing.type: Easing.OutQuint }
    }
    Behavior on height {
        enabled: plate.settled && Theme.motion
        NumberAnimation { duration: 260; easing.type: Easing.OutQuint }
    }

    Rectangle {
        anchors.fill: parent
        radius: parent.radius
        visible: Theme.eorzea
        gradient: Gradient {
            orientation: Gradient.Horizontal
            GradientStop { position: 0.0; color: Theme.navActiveStart }
            GradientStop { position: 1.0; color: Theme.navActiveEnd }
        }
    }
    Rectangle {
        anchors.left: parent.left
        anchors.top: parent.top
        anchors.bottom: parent.bottom
        anchors.topMargin: 4
        anchors.bottomMargin: 4
        width: 2
        visible: Theme.eorzea
        color: Theme.gold2
        Rectangle {
            anchors.centerIn: parent
            width: 6
            height: parent.height + 4
            color: Theme.gold
            opacity: 0.28
        }
    }
}
