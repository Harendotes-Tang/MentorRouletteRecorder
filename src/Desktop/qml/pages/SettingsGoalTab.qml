import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// 设置 · 成就: 目标值、安装前已完成次数（基数）与修改原因；有多个角色时按角色分别保存。
ColumnLayout {
    id: tab

    signal openBaselineRequested()

    Layout.fillWidth: true
    spacing: 16

    property string baselineText: String(Runtime.app.baselineCount)
    property string goalText: String(Runtime.app.goalCount)
    property string baselineReasonText: ""
    property string baselineErrorText: ""
    // Set as soon as the user types and cleared on submit, so a dashboard
    // refresh never overwrites a half-finished edit.
    property bool achievementEdited: false
    // The card's own saves still unanswered. The first-run guide, reopened from
    // here, saves through the same request, and its refusal is the guide's to show
    // (review S33-4). The guide never sends beside another baseline save and the
    // card cannot send while the guide is open, so the next baseline answer while
    // one of these is out is the card's.
    property int ownSavesOut: 0
    // Until the stored goal and baseline have been read on this connection the
    // fields hold defaults; saving them would overwrite the stored baseline
    // (audit 2026-10-03, CS7-D3), so they can be neither edited nor saved.
    readonly property bool settingsLoaded: Runtime.app.achievementSettingsLoaded

    // The Collector's achievement_progress, and the part of it this software
    // recorded. Not completed_count: a COMPLETED run that does not count towards
    // the goal is in that total only (review OK-1).
    readonly property var regionalAchievement: {
        Theme.trackBindingDependency(Runtime.app.dashboard)
        // Another character's own progress, when that is whose numbers are being edited.
        return Runtime.app.baselineProfileId !== 0 && tab.editedCharacterId === Runtime.app.baselineProfileId
            ? tab.editedCharacter
            : Runtime.app.achievementForRegion(Runtime.app.baselineRegion)
    }
    readonly property int progressCount: regionalAchievement.achievement_progress !== undefined
        ? Number(regionalAchievement.achievement_progress) : Runtime.app.baselineCount
    readonly property int recordedCount: Math.max(0, tab.progressCount - Runtime.app.baselineCount)
    // The baseline field read the way 保存 reads it: a whole number of at least 0,
    // else -1. An empty field is no number, and is not saved as 0.
    readonly property int typedBaseline: tab.parseBaseline(tab.baselineText)
    // The formula previews what 保存 would give (B3-1): the baseline is what was
    // completed before installing, and every completion this software recorded is
    // added on top. Until the field holds a number the stored figures stand.
    readonly property int previewBaseline: tab.typedBaseline >= 0 ? tab.typedBaseline : Runtime.app.baselineCount

    function parseBaseline(text) {
        const value = Number(text)
        return String(text).trim().length > 0 && Number.isFinite(value) && value >= 0
            ? Math.floor(value) : -1
    }

    // ---------------------------------------------------------------- 角色 --
    // A region holds one character profile until the player adds another; the
    // character row then also picks whose goal and starting count the fields
    // below edit. Runtime.app.baselineProfileId is 0 while that is the region's
    // current character.
    readonly property CharacterNames characterNames: CharacterNames {}
    readonly property var regionCharacters: {
        Theme.trackBindingDependency(Runtime.app.characterProfiles)
        return Runtime.app.characterProfilesForRegion(Runtime.app.baselineRegion)
    }
    readonly property bool severalCharacters: tab.regionCharacters.length > 1
    readonly property var currentCharacter: {
        for (const profile of tab.regionCharacters)
            if (profile.is_current) return profile
        return ({})
    }
    readonly property var editedCharacter: {
        if (Runtime.app.baselineProfileId === 0)
            return tab.currentCharacter
        for (const profile of tab.regionCharacters)
            if (Number(profile.profile_id) === Runtime.app.baselineProfileId) return profile
        return tab.currentCharacter
    }
    readonly property int editedCharacterId: Number(tab.editedCharacter.profile_id || 0)
    readonly property bool editingCurrentCharacter: tab.editedCharacter.is_current === true
    property string characterErrorText: ""
    // 删除 asks once more on the button itself before it is sent.
    property bool deleteArmed: false
    // This tab's own 设为当前角色 / 删除 still unanswered. The overview card switches through the
    // same request and tells its own refusals.
    property int ownCharacterRequests: 0
    function selectEditedCharacter() {
        tab.characterErrorText = ""
        tab.ownCharacterRequests += 1
        Runtime.app.selectCharacterProfile(tab.editedCharacterId)
    }
    onEditedCharacterIdChanged: {
        tab.deleteArmed = false
        tab.characterErrorText = ""
    }

    function pickCharacter(profileId) {
        tab.achievementEdited = false
        tab.baselineErrorText = ""
        const current = Number(tab.currentCharacter.profile_id || 0)
        Runtime.app.baselineProfileId = profileId === current ? 0 : profileId
    }

    function deleteEditedCharacter() {
        if (!tab.deleteArmed) {
            tab.deleteArmed = true
            return
        }
        tab.deleteArmed = false
        tab.characterErrorText = ""
        tab.ownCharacterRequests += 1
        Runtime.app.deleteCharacterProfile(tab.editedCharacterId)
    }

    Connections {
        target: Runtime.app
        // The dialog shows its own refusals; switching and deleting answer here.
        function onCharacterProfileFailed(action, code, message) {
            if ((action !== "select" && action !== "delete") || tab.ownCharacterRequests === 0)
                return
            tab.ownCharacterRequests -= 1
            tab.characterErrorText = Runtime.app.errorText(message, code)
        }
        function onCharacterProfileSaved(profileId, action) {
            if ((action === "select" || action === "delete") && tab.ownCharacterRequests > 0)
                tab.ownCharacterRequests -= 1
            tab.characterErrorText = ""
            // A character just added is the one to look at next.
            if (action === "create")
                tab.pickCharacter(profileId)
        }
        function onBaselineProfileIdChanged() {
            tab.baselineErrorText = ""
            tab.baselineText = String(Runtime.app.baselineCount)
            tab.goalText = String(Runtime.app.goalCount)
            tab.achievementEdited = false
        }
    }

    // Held by an object of its own, as SettingsSpeechTab holds its dialog: the tab is a
    // layout, and a dialog is not one of its rows.
    QtObject {
        readonly property CharacterProfileDialog dialog: CharacterProfileDialog {
            id: characterDialog

            parent: Overlay.overlay
            anchors.centerIn: parent
        }
    }

    // 总览成就卡片上的「修改」把光标放进基数输入框，已有的数字被选中，直接输入即替换。
    // 已保存的目标与基数还没读到时输入框不可用，读到后再放进去。
    property bool baselineFocusPending: false

    function focusBaselineField() {
        if (!tab.settingsLoaded) {
            tab.baselineFocusPending = true
            return
        }
        tab.baselineFocusPending = false
        baselineField.forceActiveFocus(Qt.OtherFocusReason)
        baselineField.selectAll()
    }

    onSettingsLoadedChanged: {
        if (!tab.settingsLoaded || !tab.baselineFocusPending)
            return
        // Only while the tab is still on screen; called later, because the field
        // takes the stored figure and becomes usable in the same change.
        if (tab.visible)
            Qt.callLater(tab.focusBaselineField)
        else
            tab.baselineFocusPending = false
    }

    Connections {
        target: Runtime.app
        function onDashboardChanged() {
            if (tab.achievementEdited)
                return
            tab.baselineText = String(Runtime.app.baselineCount)
            tab.goalText = String(Runtime.app.goalCount)
        }
        // A region picked anywhere - this tab, the dashboard card's 修改, the
        // first-run guide - drops the previous region's unsaved numbers: saved,
        // they would be written onto the new region (review K1-2).
        function onBaselineRegionChanged() {
            tab.baselineErrorText = ""
            tab.baselineText = String(Runtime.app.baselineCount)
            tab.goalText = String(Runtime.app.goalCount)
            // Last: refilling a focused field counts as typing in it.
            tab.achievementEdited = false
        }
        // The Collector's own refusal (ERR_REASON_REQUIRED, ERR_BAD_REQUEST …)
        // of this card's save is shown next to the field, not only in a toast
        // that scrolls away.
        function onBaselineFailed(code, message) {
            if (tab.ownSavesOut === 0)
                return
            tab.ownSavesOut -= 1
            tab.baselineErrorText = Runtime.app.errorText(message, code)
        }
        function onBaselineSaved(goal, baseline) {
            if (tab.ownSavesOut > 0)
                tab.ownSavesOut -= 1
        }
    }

    function submitAchievement() {
        const goal = Number(tab.goalText)
        const baseline = tab.typedBaseline
        if (!Number.isFinite(goal) || goal < 1) {
            baselineErrorText = qsTr("「目标值」请填写大于 0 的整数。")
            return
        }
        if (baseline < 0) {
            baselineErrorText = qsTr("「起始次数」请填写 0 或正整数。")
            return
        }
        if (!baselineReasonText.trim()) {
            baselineErrorText = Runtime.app.errorText(qsTr("请填写「修改原因」后再点「保存」。"), "ERR_REASON_REQUIRED")
            return
        }
        baselineErrorText = ""
        achievementEdited = false
        // Counted before the request: a refusal can arrive inside the call.
        ownSavesOut += 1
        Runtime.app.updateAchievementBaseline(Math.floor(goal), baseline,
                                      Theme.storedReason(baselineReasonText))
    }

    SettingsPanel {
        objectName: "achievementSettingsCard"
        freeLayout: true
        kicker: qsTr("成就进度")
        SettingsRow {
            label: qsTr("成就区服")
            description: qsTr("各区服的目标、起始次数和累计进度分别保存。")
            RecordRegionSelector {
                objectName: "achievementRegionSelector"
                Layout.preferredWidth: 170
                includeAll: false
                selection: Runtime.app.baselineRegion
                enabled: !Runtime.app.baselineSaving
                onRegionPicked: function(region) {
                    tab.achievementEdited = false
                    tab.baselineErrorText = ""
                    Runtime.app.baselineRegion = region
                }
            }
        }

        SettingsRow {
            objectName: "achievementCharacterRow"
            label: qsTr("角色")
            description: tab.severalCharacters
                ? qsTr("每个角色的目标、起始次数和进度分别保存。下面修改的是所选角色。")
                : qsTr("有多个角色时，可以为每个角色分别保存目标和进度。")
            controlSpacing: 8
            CharacterSelector {
                objectName: "achievementCharacterSelector"
                Layout.preferredWidth: 170
                visible: tab.severalCharacters
                region: Runtime.app.baselineRegion
                selection: tab.editedCharacterId
                enabled: !Runtime.app.baselineSaving && !Runtime.app.characterProfileSaving
                onProfilePicked: function(profileId) { tab.pickCharacter(profileId) }
            }
            AppButton {
                objectName: "addCharacterButton"
                text: qsTr("添加角色")
                enabled: tab.settingsLoaded && !Runtime.app.characterProfileSaving
                onClicked: characterDialog.openToAdd(Runtime.app.baselineRegion)
            }
        }

        SettingsRow {
            objectName: "achievementCharacterActions"
            visible: tab.severalCharacters
            label: tab.characterNames.label(tab.editedCharacter)
            description: tab.editingCurrentCharacter
                ? qsTr("新记录现在记在这个角色名下。换角色上线前，把要用的角色设为当前。")
                : qsTr("新记录现在记在「%1」名下。").arg(tab.characterNames.label(tab.currentCharacter))
            controlSpacing: 8
            AppButton {
                objectName: "selectCharacterButton"
                visible: !tab.editingCurrentCharacter
                text: qsTr("设为当前角色")
                variant: "primary"
                enabled: !Runtime.app.characterProfileSaving
                onClicked: tab.selectEditedCharacter()
            }
            AppButton {
                objectName: "renameCharacterButton"
                text: qsTr("改名")
                enabled: !Runtime.app.characterProfileSaving
                onClicked: characterDialog.openToRename(tab.editedCharacter)
            }
            AppButton {
                objectName: "deleteCharacterButton"
                visible: !tab.editingCurrentCharacter
                text: tab.deleteArmed ? qsTr("确认删除") : qsTr("删除")
                enabled: !Runtime.app.characterProfileSaving
                onClicked: tab.deleteEditedCharacter()
            }
        }

        Text {
            objectName: "characterErrorText"
            Layout.fillWidth: true
            visible: tab.characterErrorText.length > 0
            text: tab.characterErrorText
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        GridLayout {
            Layout.fillWidth: true
            columns: 2
            columnSpacing: 12
            rowSpacing: 4

            FieldLabel {
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                text: qsTr("目标值")
            }
            FieldLabel {
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                text: qsTr("起始次数")
            }

            StyledTextField {
                objectName: "goalField"
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                enabled: tab.settingsLoaded
                text: tab.goalText
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 1; top: 999999 }
                onTextChanged: {
                    tab.goalText = text
                    if (activeFocus)
                        tab.achievementEdited = true
                }
            }

            StyledTextField {
                id: baselineField

                objectName: "baselineField"
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                enabled: tab.settingsLoaded
                text: tab.baselineText
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 0; top: 999999 }
                onTextChanged: {
                    tab.baselineText = text
                    if (activeFocus)
                        tab.achievementEdited = true
                }
            }
        }

        Text {
            objectName: "achievementSettingsWaiting"
            Layout.fillWidth: true
            visible: !tab.settingsLoaded
            text: qsTr("正在读取已保存的目标和起始次数，读取完成后才能修改和保存。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        ColumnLayout {
            Layout.fillWidth: true
            spacing: 4
            // The Collector rejects a baseline change without a reason
            // (ERR_REASON_REQUIRED), so the field stays.
            FieldLabel { text: qsTr("修改原因") }
            StyledTextField {
                objectName: "baselineReasonField"
                Layout.fillWidth: true
                // Stored in its source text when left as it was (Theme.storedReason).
                exampleText: qsTranslate("SystemReason", "调整开始记录前已完成的导随次数")
                placeholderText: qsTr("例如：调整开始记录前已完成的导随次数") + exampleHint
                text: tab.baselineReasonText
                onTextChanged: tab.baselineReasonText = text
            }
        }

        // 进度 = 基数 + 软件记录 (the inset formula of the prototype).
        InsetBox {
            objectName: "progressFormula"
            Layout.fillWidth: true
            implicitHeight: formulaRow.implicitHeight + 24

            RowLayout {
                id: formulaRow

                anchors.left: parent.left
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                anchors.leftMargin: 14
                anchors.rightMargin: 14
                spacing: 16

                Text {
                    text: qsTr("进度 = 起始次数 + 软件记录")
                    color: Theme.textSecondary
                    opacity: Theme.dimOpacity(0.6)
                    font.pixelSize: Theme.fs(12)
                }
                Text {
                    objectName: "progressFormulaFigures"
                    text: "%1 + %2 = %3".arg(tab.previewBaseline)
                                         .arg(tab.recordedCount)
                                         .arg(tab.previewBaseline + tab.recordedCount)
                    color: Theme.gold2
                    font.family: Theme.numFamily
                    font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                    font.pixelSize: Theme.fs(18)
                    font.features: ({ "tnum": 1 })
                }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("起始次数：开始用本软件记录前，已经完成的导随次数。可保留或调整，点「保存」后生效；导入时可扣除重复计数。")
                    color: Theme.textSecondary
                    opacity: Theme.dimOpacity(0.5)
                    font.pixelSize: Theme.fs(12)
                    horizontalAlignment: Text.AlignRight
                    wrapMode: Text.WordWrap
                }
            }
        }

        Text {
            objectName: "baselineErrorText"
            Layout.fillWidth: true
            visible: tab.baselineErrorText.length > 0
            text: tab.baselineErrorText
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        RowLayout {
            Layout.fillWidth: true
            AppButton {
                text: qsTr("重新打开首次引导")
                // The guide asks about the region's current character and names nobody, so it
                // must not be left editing another one picked above.
                onClicked: {
                    Runtime.app.baselineProfileId = 0
                    tab.openBaselineRequested()
                }
            }
            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "saveAchievementButton"
                text: qsTr("保存")
                variant: "primary"
                enabled: tab.settingsLoaded
                onClicked: tab.submitAchievement()
            }
        }
    }
}
