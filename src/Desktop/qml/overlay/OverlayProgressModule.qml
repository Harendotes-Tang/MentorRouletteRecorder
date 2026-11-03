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
                    // A region is listed only once it has been read on this
                    // connection, so its figure is known; the 成就 editor's
                    // region is no gate for it (review L3-2).
                    text: regionalProgress.modelData.achievement_progress !== undefined
                          ? String(regionalProgress.modelData.achievement_progress) : Runtime.fmt.dash()
                    color: root.ov.figure
                    font.family: Theme.numFamily
                    font.pixelSize: Theme.fs(26)
                    font.features: ({"tnum": 1})
                }
                Text { text: "/" + regionalProgress.modelData.goal_count; color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                // `.plus`: completions counted while the float is open, beside the
                // first region's figure, the one they raised (review L3-2).
                OverlayPlusPill {
                    objectName: "floatingProgressPlus"
                    visible: root.ov.plus > 0 && regionalProgress.modelData.region === root.ov.primaryRegion
                    count: root.ov.plus
                    tone: root.ov.ok
                    Layout.alignment: Qt.AlignVCenter
                }
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
