import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 迷你条: the goal progress on one line (crest, figure, bar, percentage) and,
// during a run, the duty and its time on a second one. The whole bar is the
// drag handle. The prototype's 2 px pace line along the bottom edge is not
// drawn (owner, 2026-10-28): the time against the average stays in the full
// float's 副本用时 module.
Item {
    id: root

    required property var ov
    required property var controller
    property bool movingEnabled: true
    property bool secondaryActionsEnabled: true
    signal lockRequested()
    signal expandRequested()
    signal moveRequested()

    implicitHeight: column.implicitHeight + 6 + 6

    MouseArea {
        anchors.fill: parent
        enabled: !root.controller.locked && root.movingEnabled
        cursorShape: root.controller.locked ? Qt.ArrowCursor : Qt.SizeAllCursor
        onPressed: root.moveRequested()
    }

    ColumnLayout {
        id: column
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.leftMargin: 6
        anchors.rightMargin: 3
        anchors.topMargin: 6
        spacing: 0

        RowLayout {
            objectName: "floatingMiniProgress"
            Layout.fillWidth: true
            Layout.preferredHeight: 24
            spacing: 4

            Item {
                Layout.preferredWidth: 16
                Layout.preferredHeight: 16
                Layout.alignment: Qt.AlignVCenter
                OverlayIcon { anchors.fill: parent; name: "crest"; color: root.ov.accent; size: 16 }
                // `.mb-badge`: how many records wait; click to open them.
                Rectangle {
                    objectName: "floatingMiniPendingBadge"
                    visible: root.ov.pendingCount > 0
                    x: parent.width - width + 7
                    y: -6
                    width: Math.max(13, badge.implicitWidth + 6)
                    height: 13
                    radius: 6.5
                    color: root.ov.warn
                    border.width: 1.5
                    border.color: Theme.surface
                    Text {
                        id: badge
                        anchors.centerIn: parent
                        text: String(root.ov.pendingCount)
                        color: Theme.badgeForeground
                        font.pixelSize: 9
                        font.weight: Font.ExtraBold
                    }
                    MouseArea {
                        anchors.fill: parent
                        anchors.margins: -3
                        enabled: !root.controller.locked && root.secondaryActionsEnabled
                        cursorShape: Qt.PointingHandCursor
                        onClicked: root.ov.openPending()
                    }
                    HoverHandler { id: badgeHover }
                    OverlayToolTip { clearOf: root; visible: badgeHover.hovered; text: root.ov.pendingTip }
                }
            }
            Text {
                objectName: "floatingMiniCount"
                Layout.leftMargin: 3
                Layout.alignment: Qt.AlignBottom
                text: root.ov.progressKnown ? String(root.ov.progress) : Runtime.fmt.dash()
                color: root.ov.figure
                font.family: Theme.numFamily
                font.pixelSize: Theme.fs(16)
                font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                font.features: ({ "tnum": 1 })
            }
            Text {
                Layout.alignment: Qt.AlignBottom
                Layout.bottomMargin: 2
                text: "/" + root.ov.goal
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                font.family: Theme.figureFamily
                font.features: ({ "tnum": 1 })
            }
            OverlayPlusPill { visible: root.ov.plus > 0 && root.width >= 300; count: root.ov.plus; tone: root.ov.ok; Layout.alignment: Qt.AlignVCenter }
            OverlayBar {
                Layout.fillWidth: true
                Layout.minimumWidth: 12
                Layout.alignment: Qt.AlignVCenter
                implicitHeight: 6
                ratio: root.ov.progressKnown ? root.ov.progressRatio : 0
                fillColor: root.ov.barFill
            }
            Text {
                Layout.alignment: Qt.AlignVCenter
                text: root.ov.progressKnown ? Runtime.fmt.percent(root.ov.progressRatio, 0) : ""
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(11.5)
                font.weight: Font.Bold
                font.family: Theme.figureFamily
                font.features: ({ "tnum": 1 })
            }
            RowLayout {
                objectName: "floatingMiniActions"
                visible: !root.controller.locked
                spacing: 4
                OverlayIconButton {
                    objectName: "floatingExpandButton"
                    iconName: "to-full"
                    tipClearOf: root
                    tip: qsTr("展开完整浮窗")
                    onClicked: root.expandRequested()
                }
                OverlayIconButton { objectName: "floatingLockButton"; enabled: root.secondaryActionsEnabled; iconName: "lock"; tipClearOf: root; tip: qsTr("锁定位置和大小，鼠标穿透；在主窗口或托盘解锁"); onClicked: root.lockRequested() }
                // The buttons sit inside the progress row: while the pointer rests
                // on one of them its own tip speaks, not the row's.
                HoverHandler { id: buttonsHover }
            }
            HoverHandler { id: progressHover }
            OverlayToolTip {
                objectName: "floatingMiniProgressTip"
                clearOf: root
                visible: progressHover.hovered && !buttonsHover.hovered && !badgeHover.hovered && root.ov.progressKnown
                text: root.ov.progressTip
            }
        }

        Rectangle {
            visible: root.ov.inRun
            Layout.fillWidth: true
            Layout.topMargin: 6
            implicitHeight: 1
            color: Theme.border
        }

        RowLayout {
            objectName: "floatingMiniRun"
            visible: root.ov.inRun
            Layout.fillWidth: true
            Layout.topMargin: 6
            Layout.preferredHeight: 28
            Layout.rightMargin: 4
            spacing: 6

            OverlayRunRow {
                Layout.fillWidth: true
                ov: root.ov
                run: root.ov.run
                compact: true
                categoryArt: true
                nameSize: 12.5
                name: root.ov.matched ? qsTr("已匹配") : root.ov.runName
                status: root.ov.matched ? "" : root.ov.entered ? "live"
                      : root.ov.completed ? "done" : root.ov.ended ? "ended" : ""
            }
            Text {
                visible: root.ov.deltaText.length > 0
                text: root.ov.deltaText
                color: root.ov.underAverage ? root.ov.ok : root.ov.warn
                font.pixelSize: Theme.fs(11)
                font.weight: Font.ExtraBold
                font.family: Theme.figureFamily
                font.features: ({ "tnum": 1 })
            }
            Text {
                objectName: "floatingMiniElapsed"
                text: root.ov.elapsedText
                color: root.ov.completed ? root.ov.ok : root.ov.matched ? root.ov.accent
                     : root.ov.ended ? Theme.textSecondary : Theme.textPrimary
                font.family: Theme.numFamily
                font.pixelSize: Theme.fs(15)
                font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                font.features: ({ "tnum": 1 })
            }
            HoverHandler { id: runHover }
            OverlayToolTip { clearOf: root; visible: runHover.hovered; text: root.ov.runTip + "\n" + root.ov.timeTip }
        }
    }
}
