pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

// 「发现了一个还没有登记的角色」: shown on a region's achievement card while the
// Collector has told a character apart that the player has not said anything
// about. Until the player answers, that character's records count for nobody,
// so the question is asked where the progress is read.
//
// One character is asked about at a time, the one seen first. The three answers
// are the three the Collector takes: it is a new character (named in the same
// dialog as 添加角色), it is one of the characters already here, or no progress
// is kept for it. Nothing about the character itself is shown: the software
// knows it only as "not one of the others".
InsetBox {
    id: prompt

    required property string region

    readonly property var pending: {
        Theme.trackBindingDependency(Runtime.app.characterIdentities)
        return Runtime.app.pendingCharacterIdentities(prompt.region)
    }
    readonly property var identity: prompt.pending.length > 0 ? prompt.pending[0] : ({})
    readonly property int identityId: Number(prompt.identity.identity_id || 0)
    readonly property int runCount: Number(prompt.identity.run_count || 0)
    readonly property var profiles: {
        Theme.trackBindingDependency(Runtime.app.characterProfiles)
        return Runtime.app.characterProfilesForRegion(prompt.region)
    }
    readonly property bool oneProfile: prompt.profiles.length === 1
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
            Layout.fillWidth: true
            text: qsTr("发现了一个还没有登记的角色")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            font.weight: Font.DemiBold
            wrapMode: Text.WordWrap
        }
        Text {
            objectName: "characterIdentityBody_" + prompt.region
            Layout.fillWidth: true
            text: (prompt.runCount > 0
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
            objectName: "characterIdentityNew_" + prompt.region
            Layout.fillWidth: true
            variant: "primary"
            text: qsTr("这是一个新角色")
            enabled: !prompt.busy
            onClicked: newCharacterDialog.openToAdd(prompt.region, prompt.identityId)
        }

        // The name on a line of its own, where it can wrap; the button stays short.
        Text {
            objectName: "characterIdentityOnly_" + prompt.region
            Layout.fillWidth: true
            visible: prompt.oneProfile
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
            visible: prompt.oneProfile
            text: qsTr("它就是这个角色")
            enabled: !prompt.busy
            onClicked: prompt.bind(Number(prompt.profiles[0].profile_id))
        }

        // Stacked, not side by side: the card is narrow and a character's name must be readable.
        CharacterSelector {
            objectName: "characterIdentityExisting_" + prompt.region
            Layout.fillWidth: true
            visible: !prompt.oneProfile
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
            visible: !prompt.oneProfile && prompt.choiceStands
            text: qsTr("确定是这个角色")
            enabled: !prompt.busy
            onClicked: prompt.bind(prompt.chosenProfileId)
        }

        AppButton {
            objectName: "characterIdentityIgnore_" + prompt.region
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
