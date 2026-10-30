pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

Card {
    id: card
    required property var achievement
    property bool compact: false
    signal editRequested(string region)
    objectName: achievement.region === "CN" ? "achievementCard" : "achievementCard_" + achievement.region
    padding: 20
    spacing: 8
    CardKicker {
        Layout.fillWidth: true
        text: Theme.live(Runtime.fmt.regionLabel(card.achievement.region)) + " · "
              + qsTr("成就进度 · %1 次").arg(card.achievement.goal_count)
    }
    Item {
        Layout.fillWidth: true
        Layout.preferredHeight: card.compact ? 148 : 208
        ProgressRing {
            objectName: card.achievement.region === "CN" ? "achievementRing" : "achievementRing_" + card.achievement.region
            anchors.centerIn: parent
            width: card.compact ? 144 : 200
            height: width
            value: Number(card.achievement.achievement_progress || 0)
            maximum: Math.max(1, Number(card.achievement.goal_count || 2000))
            label: String(value)
            detail: Runtime.fmt.percent(value / maximum, 1)
        }
        AchievementConfetti {
            id: confetti
            objectName: card.achievement.region === "CN" ? "achievementConfetti" : "achievementConfetti_" + card.achievement.region
            anchors.fill: parent
            z: 1
        }
        Connections {
            target: Runtime.app
            function onRegionalAchievementMilestoneReached(region, completedLaps) {
                if (region === card.achievement.region) confetti.burst()
            }
        }
    }
    Text {
        objectName: card.achievement.region === "CN" ? "achievementRemaining" : "achievementRemaining_" + card.achievement.region
        Layout.alignment: Qt.AlignHCenter
        text: qsTr("还差 %1 次").arg(card.achievement.remaining)
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(13)
        font.features: ({"tnum": 1})
    }
    RowLayout {
        Layout.alignment: Qt.AlignHCenter
        Text {
            text: qsTr("含起始次数 %1 次").arg(card.achievement.baseline_completed_count)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
        }
        AppButton {
            text: qsTr("修改")
            variant: "ghost"
            onClicked: card.editRequested(card.achievement.region)
        }
    }
}
