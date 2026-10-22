import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// 累计进度: the count toward the goal as one large figure over a bar with
// quarter ticks; a "+N" pill appears for a moment after each new completion.
ColumnLayout {
    id: root

    required property var ov
    spacing: 0

    OverlayModuleLabel { ov: root.ov; text: qsTr("累计进度"); Layout.bottomMargin: 5 }

    RowLayout {
        Layout.fillWidth: true
        spacing: 6

        OverlayIcon { source: Lucide.source("target", color); color: Theme.textSecondary; Layout.alignment: Qt.AlignVCenter }

        Text {
            objectName: "floatingAchievementProgress"
            text: root.ov.progressKnown ? String(root.ov.progress) : Runtime.fmt.dash()
            color: root.ov.figure
            font.family: Theme.numFamily
            font.pixelSize: Theme.fs(26)
            font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
            font.features: ({ "tnum": 1 })
            Layout.alignment: Qt.AlignVCenter
        }
        Text {
            text: "/" + root.ov.goal
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            font.family: Theme.figureFamily
            font.features: ({ "tnum": 1 })
            Layout.alignment: Qt.AlignVCenter
        }
        OverlayPlusPill { visible: root.ov.plus > 0; count: root.ov.plus; tone: root.ov.ok; Layout.alignment: Qt.AlignVCenter }
        Item { Layout.fillWidth: true }
        Text {
            text: root.ov.progressKnown ? Runtime.fmt.percent(root.ov.progressRatio, 1) : ""
            color: root.ov.accent
            font.pixelSize: Theme.fs(13)
            font.weight: Font.Bold
            font.family: Theme.figureFamily
            font.features: ({ "tnum": 1 })
            Layout.alignment: Qt.AlignVCenter
        }
    }

    OverlayBar {
        Layout.fillWidth: true
        Layout.topMargin: 8
        implicitHeight: 8
        ratio: root.ov.progressKnown ? root.ov.progressRatio : 0
        fillColor: root.ov.barFill
        ticks: true
    }

    HoverHandler { id: hover }
    OverlayToolTip { visible: hover.hovered && root.ov.progressKnown; text: root.ov.progressTip }
}
