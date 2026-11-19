pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

// 「发现了一个还没有登记的角色」: shown on a region's achievement card while the
// Collector has a character waiting for the player's answer. Upgraded-profile
// confirmations come first. Previously bound characters keep their progress;
// unbound characters can be registered separately, assigned or ignored.
InsetBox {
    id: prompt

    required property string region

    readonly property var pending: {
        Theme.trackBindingDependency(Runtime.app.characterIdentities)
        return Runtime.app.pendingCharacterIdentities(prompt.region)
    }
    readonly property var identity: prompt.pending.length > 0 ? prompt.pending[0] : ({})
    readonly property int identityId: Number(prompt.identity.identity_id || 0)
    readonly property int registrationProfileId: Number(prompt.identity.registration_profile_id || 0)
    readonly property bool unbound: prompt.identity.state !== "BOUND"
    readonly property int runCount: Number(prompt.identity.run_count || 0)
    readonly property var profiles: {
        Theme.trackBindingDependency(Runtime.app.characterProfiles)
        return Runtime.app.characterProfilesForRegion(prompt.region)
    }
    readonly property bool oneProfile: prompt.profiles.length === 1
    readonly property var registrationProfile: prompt.profiles.find(
        profile => Number(profile.profile_id) === prompt.registrationProfileId) || ({})
    readonly property CharacterNames names: CharacterNames {}
    // The character picked in the list; 0 until the player picks one. A region
    // with one character needs no list: its button names that character.
    property int chosenProfileId: 0
    // This prompt's own answer still unanswered; another card's must not clear it.
    property bool waiting: false
    property string errorText: ""
    readonly property bool busy: prompt.waiting || Runtime.app.characterProfileSaving
    // The character picked in the list, while it is still one of the region's.
    readonly property bool choiceStands: {
        for (const profile of prompt.profiles)
            if (Number(profile.profile_id) === prompt.chosenProfileId) return true
        return false
    }

    objectName: "characterIdentityPrompt_" + prompt.region
    visible: prompt.identityId > 0
    implicitHeight: content.implicitHeight + 24
    border.width: 1
    border.color: Theme.accent

    // Another character is asked about next: nothing chosen for the last one carries over.
    onIdentityIdChanged: {
        prompt.chosenProfileId = 0
        prompt.errorText = ""
        prompt.waiting = false
        // A dialog opened for the character asked about before would register the wrong one.
        if (newCharacterDialog.opened && !newCharacterDialog.waiting)
            newCharacterDialog.close()
    }

    function bind(profileId) {
        if (profileId <= 0 || prompt.identityId <= 0)
            return
        if (profileId === prompt.registrationProfileId) {
            newCharacterDialog.openToRename(prompt.registrationProfile, prompt.identityId)
            return
        }
        prompt.errorText = ""
        prompt.waiting = true
        Runtime.app.bindCharacterIdentity(prompt.identityId, profileId)
    }

    function ignore() {
        if (prompt.identityId <= 0)
            return
        prompt.errorText = ""
        prompt.waiting = true
        Runtime.app.ignoreCharacterIdentity(prompt.identityId)
    }

    Connections {
        target: Runtime.app
        enabled: prompt.waiting
        function onCharacterProfileSaved(profileId, action) {
            if (action === "bind" || action === "ignore")
                prompt.waiting = false
        }
        function onCharacterProfileFailed(action, code, message) {
            if (action !== "bind" && action !== "ignore")
                return
            prompt.waiting = false
            prompt.errorText = Runtime.app.errorText(message, code)
        }
    }

    QtObject {
        readonly property CharacterProfileDialog dialog: CharacterProfileDialog {
            id: newCharacterDialog
            parent: Overlay.overlay
            anchors.centerIn: parent
        }
    }

    ColumnLayout {
        id: content
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.margins: 12
        spacing: 8

        Text {
            objectName: "characterIdentityHeading_" + prompt.region
            Layout.fillWidth: true
            // A bound character is already counted: its prompt only offers a name.
            text: prompt.unbound ? qsTr("发现了一个还没有登记的角色") : qsTr("给「原有记录」起个名字")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            font.weight: Font.DemiBold
            wrapMode: Text.WordWrap
        }
        Text {
            objectName: "characterIdentityBody_" + prompt.region
            Layout.fillWidth: true
            text: !prompt.unbound
                  ? qsTr("软件已经认出这个角色，新记录照常计入「原有记录」，已有记录和成就进度不变。给它起个名字，以后有多个角色时更容易区分。")
                    + "\n" + qsTr("如果认错了角色，可在「设置 › 成就」解除绑定。")
                  : prompt.registrationProfileId > 0
                  ? qsTr("输入角色名并确认绑定「原有记录」。已有记录和成就进度会保留。")
                  : (prompt.runCount > 0
                   ? qsTr("它已经有 %1 条记录，现在不计入任何角色的进度。").arg(prompt.runCount)
                   : qsTr("在你说明之前，它的记录不计入任何角色的进度。"))
                  + qsTr("说明它是哪个角色后，这些记录会自动归到那个角色名下。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            lineHeight: 1.35
            wrapMode: Text.WordWrap
        }
        Text {
            Layout.fillWidth: true
            visible: prompt.pending.length > 1
            text: qsTr("另外还有 %1 个角色等待说明，回答完这一个再问下一个。").arg(prompt.pending.length - 1)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }

        AppButton {
            objectName: "characterIdentityRegister_" + prompt.region
            Layout.fillWidth: true
            visible: prompt.registrationProfileId > 0
            variant: "primary"
            text: prompt.unbound ? qsTr("登记并绑定原有记录") : qsTr("给它起名")
            enabled: !prompt.busy && prompt.registrationProfile.profile_id !== undefined
            onClicked: prompt.bind(prompt.registrationProfileId)
        }
        // Owner decision 2026-11-19 (audit K1-1): a player whose character is already
        // counted is asked once and may leave it; nothing is held back meanwhile.
        AppButton {
            objectName: "characterIdentityLater_" + prompt.region
            Layout.fillWidth: true
            visible: !prompt.unbound && prompt.registrationProfileId > 0
            variant: "ghost"
            text: qsTr("以后再说")
            enabled: !prompt.busy
            onClicked: Runtime.app.dismissCharacterRegistration(prompt.identityId)
        }
        AppButton {
            objectName: "characterIdentityNew_" + prompt.region
            Layout.fillWidth: true
            visible: prompt.unbound
            variant: prompt.registrationProfileId > 0 ? "ghost" : "primary"
            text: qsTr("这是一个新角色")
            enabled: !prompt.busy
            onClicked: newCharacterDialog.openToAdd(prompt.region, prompt.identityId)
        }

        // The name on a line of its own, where it can wrap; the button stays short.
        Text {
            objectName: "characterIdentityOnly_" + prompt.region
            Layout.fillWidth: true
            visible: prompt.oneProfile && prompt.registrationProfileId === 0
            text: prompt.oneProfile
                ? qsTr("已有的角色：「%1」").arg(prompt.names.label(prompt.profiles[0]))
                : ""
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
        AppButton {
            objectName: "characterIdentitySame_" + prompt.region
            Layout.fillWidth: true
            visible: prompt.oneProfile && prompt.registrationProfileId === 0
            text: qsTr("它就是这个角色")
            enabled: !prompt.busy
            onClicked: prompt.bind(Number(prompt.profiles[0].profile_id))
        }

        // Stacked, not side by side: the card is narrow and a character's name must be readable.
        CharacterSelector {
            objectName: "characterIdentityExisting_" + prompt.region
            Layout.fillWidth: true
            visible: !prompt.oneProfile && prompt.unbound
            region: prompt.region
            selection: prompt.chosenProfileId
            includeAll: true
            allLabel: qsTr("它是已有的角色，请选择")
            markCurrent: false
            enabled: !prompt.busy
            Accessible.name: qsTr("它是已有的哪个角色")
            onProfilePicked: function(profileId) { prompt.chosenProfileId = profileId }
        }
        AppButton {
            objectName: "characterIdentityBind_" + prompt.region
            Layout.fillWidth: true
            visible: !prompt.oneProfile && prompt.choiceStands && prompt.unbound
            text: qsTr("确定是这个角色")
            enabled: !prompt.busy
            onClicked: prompt.bind(prompt.chosenProfileId)
        }

        AppButton {
            objectName: "characterIdentityIgnore_" + prompt.region
            visible: prompt.unbound
            Layout.alignment: Qt.AlignHCenter
            variant: "ghost"
            text: qsTr("不统计这个角色")
            enabled: !prompt.busy
            onClicked: prompt.ignore()
        }

        Text {
            objectName: "characterIdentityError_" + prompt.region
            Layout.fillWidth: true
            visible: prompt.errorText.length > 0
            text: prompt.errorText
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
    }
}
