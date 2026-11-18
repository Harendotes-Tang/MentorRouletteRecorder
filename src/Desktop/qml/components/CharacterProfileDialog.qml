pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

// 添加角色 / 给角色改名（设置 · 成就），以及把一个还没有登记的角色登记为新角色（总览）。
//
// The name is what the player calls the character here - 大号, 小号 - and is
// never read from the game. Adding asks for the character's own goal and
// starting count as well, because nothing is copied from another character.
MotionDialog {
    id: dialog
    objectName: "characterProfileDialog"

    // 0 adds a character to `region`; otherwise the profile being renamed.
    property int profileId: 0
    // A character the Collector told apart that this new profile is: it is bound
    // in the same request, takes the records made for it and becomes the current
    // character. 0 when the player is simply adding a character.
    property int identityId: 0
    property string region: "CN"
    property string nameText: ""
    property string goalText: "2000"
    property string baselineText: "0"
    property bool makeCurrent: false
    property string errorText: ""
    // This dialog's own request still unanswered; another window's change must
    // not close it.
    property bool waiting: false
    readonly property bool adding: dialog.profileId === 0
    readonly property bool registering: dialog.profileId > 0 && dialog.identityId > 0
    readonly property string saveAction: dialog.registering ? "register" : dialog.adding ? "create" : "rename"
    // This dialog's own request was answered; the profile it created or renamed.
    signal saved(int profileId, string action)
    readonly property int maxNameLength: 24

    modal: true
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 460
    padding: 20
    closePolicy: dialog.waiting ? Popup.NoAutoClose : Popup.CloseOnEscape

    background: DialogFrame {}

    function openToAdd(region, identityId) {
        dialog.profileId = 0
        dialog.identityId = Number(identityId) || 0
        dialog.region = region
        dialog.nameText = ""
        dialog.goalText = "2000"
        dialog.baselineText = "0"
        dialog.makeCurrent = false
        dialog.present()
    }

    function openToRename(profile, identityId) {
        dialog.identityId = Number(identityId) || 0
        dialog.profileId = Number(profile.profile_id)
        dialog.region = String(profile.region)
        dialog.nameText = String(profile.display_name || "")
        dialog.present()
    }

    function present() {
        dialog.errorText = ""
        dialog.waiting = false
        dialog.open()
        nameField.forceActiveFocus(Qt.OtherFocusReason)
        nameField.selectAll()
    }

    function submit() {
        const name = dialog.nameText.trim()
        if (name.length === 0 || name.length > dialog.maxNameLength) {
            dialog.errorText = qsTr("请填写角色名称，最多 %1 个字。").arg(dialog.maxNameLength)
            return
        }
        if (!dialog.adding) {
            dialog.errorText = ""
            dialog.waiting = true
            if (dialog.registering)
                Runtime.app.bindCharacterIdentity(dialog.identityId, dialog.profileId, name)
            else
                Runtime.app.renameCharacterProfile(dialog.profileId, name)
            return
        }
        const goal = Number(dialog.goalText)
        const baseline = Number(dialog.baselineText)
        if (dialog.goalText.trim().length === 0 || !Number.isFinite(goal) || goal < 1) {
            dialog.errorText = qsTranslate("SettingsGoalTab", "「目标值」请填写大于 0 的整数。")
            return
        }
        if (dialog.baselineText.trim().length === 0 || !Number.isFinite(baseline) || baseline < 0) {
            dialog.errorText = qsTranslate("SettingsGoalTab", "「起始次数」请填写 0 或正整数。")
            return
        }
        dialog.errorText = ""
        dialog.waiting = true
        Runtime.app.createCharacterProfile(dialog.region, name, Math.floor(goal), Math.floor(baseline),
                                           dialog.makeCurrent, dialog.identityId)
    }

    Connections {
        target: Runtime.app
        enabled: dialog.waiting
        function onCharacterProfileSaved(profileId, action) {
            if (action !== dialog.saveAction)
                return
            dialog.waiting = false
            dialog.close()
            dialog.saved(profileId, action)
        }
        function onCharacterProfileFailed(action, code, message) {
            if (action !== dialog.saveAction)
                return
            dialog.waiting = false
            dialog.errorText = Runtime.app.errorText(message, code)
        }
    }

    contentItem: ColumnLayout {
        spacing: 12

        CardKicker {
            text: Theme.live(Runtime.fmt.regionLabel(dialog.region)) + " · " + qsTr("角色")
        }

        HeadingLabel {
            Layout.fillWidth: true
            text: dialog.registering ? qsTr("登记原有角色") : !dialog.adding ? qsTr("给角色改名")
                : dialog.identityId > 0 ? qsTr("登记新角色") : qsTr("添加角色")
            font.pixelSize: Theme.dialogTitleSize(20)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.adding
            text: qsTr("每个角色有自己的目标、起始次数和进度。名称由你自己起，方便区分即可，软件不会读取游戏里的角色名。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            lineHeight: 1.35
            wrapMode: Text.WordWrap
        }

        ColumnLayout {
            Layout.fillWidth: true
            spacing: 4
            FieldLabel { text: qsTr("角色名称") }
            StyledTextField {
                id: nameField
                objectName: "characterNameField"
                Layout.fillWidth: true
                enabled: !dialog.waiting
                maximumLength: dialog.maxNameLength
                placeholderText: qsTr("例如：大号、小号")
                text: dialog.nameText
                onTextChanged: dialog.nameText = text
                // What was wrong with the name is no longer true of the one being typed.
                onTextEdited: dialog.errorText = ""
                onAccepted: dialog.submit()
            }
        }

        GridLayout {
            Layout.fillWidth: true
            visible: dialog.adding
            columns: 2
            columnSpacing: 12
            rowSpacing: 4

            FieldLabel {
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                text: qsTranslate("SettingsGoalTab", "目标值")
            }
            FieldLabel {
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                text: qsTranslate("SettingsGoalTab", "起始次数")
            }
            StyledTextField {
                objectName: "characterGoalField"
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                enabled: !dialog.waiting
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 1; top: 999999 }
                text: dialog.goalText
                onTextChanged: dialog.goalText = text
            }
            StyledTextField {
                objectName: "characterBaselineField"
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                enabled: !dialog.waiting
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 0; top: 999999 }
                text: dialog.baselineText
                onTextChanged: dialog.baselineText = text
            }
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.adding
            text: qsTr("起始次数：这个角色在游戏成就里已经完成的导随次数，之后记录的通关会加在它上面。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            lineHeight: 1.35
            wrapMode: Text.WordWrap
        }

        Text {
            objectName: "characterIdentityHint"
            Layout.fillWidth: true
            visible: dialog.adding && dialog.identityId > 0
            // Switching by itself is promised only where the Collector does it.
            text: qsTr("添加后，这个角色之前没有归属的记录会记到它名下。")
                  + (Runtime.app.characterIdentification(dialog.region) === "AUTOMATIC"
                     ? qsTr("以后登录这个角色时，软件会自动切换过来。") : "")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            lineHeight: 1.35
            wrapMode: Text.WordWrap
        }

        RowLayout {
            Layout.fillWidth: true
            // A character being registered becomes the current one anyway.
            visible: dialog.adding && dialog.identityId === 0
            spacing: 12

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 2
                Text {
                    Layout.fillWidth: true
                    text: qsTr("现在就切换到这个角色")
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(13)
                    wrapMode: Text.WordWrap
                }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("打开后，之后的新记录记在这个角色名下；以前的记录不变。")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    wrapMode: Text.WordWrap
                }
            }
            ToggleSwitch {
                objectName: "characterMakeCurrentSwitch"
                Layout.alignment: Qt.AlignVCenter
                accessibleName: qsTr("现在就切换到这个角色")
                enabled: !dialog.waiting
                checked: dialog.makeCurrent
                onToggled: function(value) { dialog.makeCurrent = value }
            }
        }

        Text {
            objectName: "characterProfileError"
            Layout.fillWidth: true
            visible: dialog.errorText.length > 0
            text: dialog.errorText
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "characterProfileCancel"
                text: qsTr("取消")
                enabled: !dialog.waiting
                onClicked: dialog.close()
            }
            AppButton {
                objectName: "characterProfileSave"
                variant: "primary"
                text: dialog.registering ? qsTr("确认绑定") : dialog.adding ? qsTr("添加") : qsTr("保存")
                enabled: !dialog.waiting
                onClicked: dialog.submit()
            }
        }
    }
}
