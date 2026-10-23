pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 副本用时: the elapsed time as a figure, the duty's average at the right, and
// a pace bar whose mark is the average (its category's while the duty has
// none; no bar without either). While a match waits for entry the bar shows
// moving stripes instead.
ColumnLayout {
    id: root

    required property var ov
    spacing: 0

    // The module lives on while the float's window is hidden or minimized; the
    // sand and the wait stripes run only while someone can see them.
    readonly property bool windowShown: !Window.window
        || (Window.window.visible && Window.window.visibility !== Window.Minimized)
    readonly property color figureColor: root.ov.completed ? root.ov.ok
                                       : root.ov.ended ? Theme.textSecondary
                                       : root.ov.matched ? root.ov.accent : Theme.textPrimary

    OverlayModuleLabel { ov: root.ov; text: qsTr("副本用时"); Layout.bottomMargin: 5 }

    RowLayout {
        Layout.fillWidth: true
        spacing: 6

        OverlayIcon { visible: !root.ov.matched; name: "time"; color: Theme.textSecondary; Layout.alignment: Qt.AlignVCenter }
        OverlayHourglass {
            objectName: "floatingWaitHourglass"
            visible: root.ov.matched
            color: Theme.textSecondary
            running: visible && Theme.motion && root.windowShown
            Layout.alignment: Qt.AlignVCenter
        }
        Text {
            objectName: "floatingElapsed"
            text: root.ov.elapsedText
            color: root.figureColor
            font.family: Theme.numFamily
            font.pixelSize: Theme.fs(22)
            font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
            font.features: ({ "tnum": 1 })
            Layout.alignment: Qt.AlignBottom
        }
        Text {
            visible: root.ov.deltaText.length > 0
            text: root.ov.deltaText
            color: root.ov.underAverage ? root.ov.ok : root.ov.warn
            font.pixelSize: Theme.fs(11)
            font.weight: Font.ExtraBold
            font.family: Theme.figureFamily
            font.features: ({ "tnum": 1 })
            Layout.alignment: Qt.AlignBottom
            Layout.bottomMargin: 2
        }
        Item { Layout.fillWidth: true }
        Text {
            visible: !root.ov.matched && root.ov.hasReference
            text: root.ov.averageText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            font.family: Theme.figureFamily
            font.features: ({ "tnum": 1 })
            Layout.alignment: Qt.AlignBottom
        }
    }

    OverlayBar {
        objectName: "floatingPaceBar"
        visible: !root.ov.matched && root.ov.hasReference
        Layout.fillWidth: true
        Layout.topMargin: 8
        implicitHeight: 6
        ratio: root.ov.paceRatio
        fillColor: root.ov.overAverage ? root.ov.warn : root.ov.underAverage ? root.ov.ok : root.ov.barFill
        showMark: root.ov.hasReference
        markRatio: root.ov.averageRatio
    }

    // `.tm-wait`: 45° stripes sliding to the right while the queue pops.
    Rectangle {
        id: waitBar
        visible: root.ov.matched
        Layout.fillWidth: true
        Layout.topMargin: 8
        implicitHeight: 6
        radius: Theme.eorzea ? Theme.radiusXs : 3
        color: Theme.insetBackgroundStrong
        clip: true
        Row {
            id: stripes
            x: -24
            spacing: 6
            Repeater {
                model: Math.ceil(waitBar.width / 12) + 3
                delegate: Rectangle {
                    width: 6
                    height: waitBar.height
                    color: Qt.rgba(root.ov.accent.r, root.ov.accent.g, root.ov.accent.b, 0.45)
                    transform: Rotation { origin.x: 3; origin.y: waitBar.height / 2; angle: 45 }
                }
            }
            NumberAnimation on x {
                running: waitBar.visible && Theme.motion && root.windowShown
                loops: Animation.Infinite
                from: -24
                to: 0
                duration: 1000
            }
        }
    }

    HoverHandler { id: hover }
    OverlayToolTip { visible: hover.hovered; text: root.ov.timeTip }
}
