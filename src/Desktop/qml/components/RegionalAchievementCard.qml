pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

Card {
    id: card
    required property var achievement
    property bool compact: false
    signal editRequested(string region)
    // Shown only once the region holds a second character: the card then says
    // whose progress this is and lets the player switch before logging in as
    // another one. A player with one character sees the card as it always was.
    readonly property bool severalCharacters: Number(card.achievement.profile_count || 0) > 1
    readonly property string characterName: {
        Theme.trackBindingDependency(Runtime.app.characterProfiles)
        const profile = Runtime.app.characterProfile(Number(card.achievement.profile_id || 0))
        return String(profile.display_name || "")
    }
    readonly property int unassignedCount: Number(card.achievement.unassigned_run_count || 0)
    // How a new record of this region finds its character; "" from an older Collector.
    readonly property string identification: String(card.achievement.identification || "")
    // This card's own switch still unanswered: its refusal is this card's to tell.
    property bool switching: false
    Connections {
        target: Runtime.app
        enabled: card.switching
        function onCharacterProfileSaved(profileId, action) {
            if (action === "select") card.switching = false
        }
        function onCharacterProfileFailed(action, code, message) {
            if (action !== "select")
                return
            card.switching = false
            Runtime.app.showToast(qsTr("没有切换成功：%1").arg(Runtime.app.errorText(message, code)))
        }
    }
    objectName: achievement.region === "CN" ? "achievementCard" : "achievementCard_" + achievement.region
    padding: 20
    spacing: 8
    CardKicker {
        Layout.fillWidth: true
        text: Theme.live(Runtime.fmt.regionLabel(card.achievement.region)) + " · "
              + qsTr("成就进度 · %1 次").arg(card.achievement.goal_count)
    }
    RowLayout {
        objectName: "achievementCharacter_" + card.achievement.region
        Layout.fillWidth: true
        visible: card.severalCharacters || card.characterName.length > 0
        spacing: 8
        Text {
            text: qsTr("当前角色")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
        CharacterSelector {
            objectName: "achievementCharacterSwitch_" + card.achievement.region
            Layout.fillWidth: true
            visible: card.severalCharacters
            region: card.achievement.region
            selection: Number(card.achievement.profile_id || 0)
            markCurrent: false
            enabled: !Runtime.app.characterProfileSaving && !card.switching
            Accessible.name: qsTr("当前角色")
            onProfilePicked: function(profileId) {
                if (profileId === Number(card.achievement.profile_id || 0))
                    return
                card.switching = true
                Runtime.app.selectCharacterProfile(profileId)
            }
        }
        Text {
            objectName: "achievementCharacterName_" + card.achievement.region
            Layout.fillWidth: true
            visible: !card.severalCharacters
            text: card.characterName
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
        }
    }
    Text {
        objectName: "achievementIdentification_" + card.achievement.region
        Layout.fillWidth: true
        visible: card.severalCharacters && card.identification.length > 0
        text: card.identification === "AUTOMATIC"
            ? qsTr("登录游戏后会自动切换到对应的角色。")
            : card.identification === "MANUAL_OFF"
                ? qsTr("自动识别角色已关闭，换角色上线前请在这里选一下。")
                : qsTr("这个区服暂时不能自动识别角色，换角色上线前请在这里选一下。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        wrapMode: Text.WordWrap
    }
    CharacterIdentityPrompt {
        Layout.fillWidth: true
        region: String(card.achievement.region || "")
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
    Text {
        objectName: "achievementUnassigned_" + card.achievement.region
        Layout.fillWidth: true
        visible: card.unassignedCount > 0
        text: qsTr("另有 %1 条记录没有分配角色，不计入任何角色的进度。可在历史记录中筛选「未分配」后分配。").arg(card.unassignedCount)
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        horizontalAlignment: Text.AlignHCenter
        wrapMode: Text.WordWrap
    }
}
