pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 累计进度: the count toward the goal as one large figure over a bar with
// quarter ticks; a "+N" pill appears for a moment after each new completion.
ColumnLayout {
    id: root

    required property var ov
    spacing: 0

    OverlayModuleLabel { ov: root.ov; text: qsTr("累计进度"); Layout.bottomMargin: 5 }

    Repeater {
        model: root.ov.regionalAchievements
        delegate: ColumnLayout {
            id: regionalProgress
            required property var modelData
            Layout.fillWidth: true
            spacing: 4
            Text {
                Layout.fillWidth: true
                text: Theme.live(Runtime.fmt.regionLabel(regionalProgress.modelData.region))
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
            }
            RowLayout {
                Layout.fillWidth: true
                Text {
                    objectName: regionalProgress.modelData.region === "CN" ? "floatingAchievementProgress" : "floatingAchievementProgress_" + regionalProgress.modelData.region
                    text: root.ov.progressKnown ? String(regionalProgress.modelData.achievement_progress) : Runtime.fmt.dash()
                    color: root.ov.figure
                    font.family: Theme.numFamily
                    font.pixelSize: Theme.fs(26)
                    font.features: ({"tnum": 1})
                }
                Text { text: "/" + regionalProgress.modelData.goal_count; color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                Item { Layout.fillWidth: true }
                Text {
                    text: Runtime.fmt.percent(regionalProgress.modelData.achievement_progress / Math.max(1, regionalProgress.modelData.goal_count), 1)
                    color: root.ov.accent
                    font.pixelSize: Theme.fs(13)
                }
            }
            OverlayBar {
                Layout.fillWidth: true
                Layout.bottomMargin: 8
                implicitHeight: 8
                ratio: Math.min(1, regionalProgress.modelData.achievement_progress / Math.max(1, regionalProgress.modelData.goal_count))
                fillColor: root.ov.barFill
                ticks: true
            }
        }
    }

    HoverHandler { id: hover }
    OverlayToolTip { visible: hover.hovered && root.ov.progressKnown; text: root.ov.progressTip }
}
