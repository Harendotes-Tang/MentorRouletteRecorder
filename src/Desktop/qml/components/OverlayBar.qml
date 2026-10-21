import QtQuick
import MentorRecorder

// `.bar`: the float's inset track with a fill, optional quarter ticks (the
// goal bar) and an optional mark (the duty's average on the time bar).
Rectangle {
    id: root

    property real ratio: 0
    property color fillColor: Theme.eorzea ? Theme.gold2 : Theme.accent
    property bool ticks: false
    property bool showMark: false
    property real markRatio: 0

    implicitHeight: 8
    radius: Theme.eorzea ? Theme.radiusXs : height / 2
    color: Theme.insetBackgroundStrong
    border.width: Theme.eorzea ? 1 : 0
    border.color: Theme.insetBorder

    Rectangle {
        id: fill
        anchors.left: parent.left
        anchors.top: parent.top
        anchors.bottom: parent.bottom
        width: Math.round(parent.width * Math.max(0, Math.min(1, root.ratio)))
        radius: parent.radius
        color: root.fillColor
        Behavior on width {
            NumberAnimation { duration: Theme.motion ? 400 : 0; easing.type: Easing.Bezier; easing.bezierCurve: Theme.curveStandard }
        }
    }

    Repeater {
        model: root.ticks ? [0.25, 0.5, 0.75] : []
        delegate: Rectangle {
            required property real modelData
            x: Math.round(root.width * modelData)
            y: 0
            width: 1
            height: root.height
            color: Qt.rgba(Theme.surface.r, Theme.surface.g, Theme.surface.b, 0.7)
        }
    }

    Rectangle {
        visible: root.showMark
        x: Math.round(root.width * Math.max(0, Math.min(1, root.markRatio))) - 1
        y: -3
        width: 2
        height: root.height + 6
        radius: 1
        color: Theme.textPrimary
        border.width: 1
        border.color: Qt.rgba(Theme.surface.r, Theme.surface.g, Theme.surface.b, 0.7)
    }
}
