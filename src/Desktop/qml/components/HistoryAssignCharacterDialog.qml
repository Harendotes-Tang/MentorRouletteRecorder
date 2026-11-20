pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

// 历史记录 · 把勾选的记录改到某个角色名下，或改为未分配。
//
// Moving a record changes whose progress it counts in and nothing else: no
// character's starting count moves with it, which the dialog says, because a
// player splitting old records between characters usually has to set those too.
MotionDialog {
    id: dialog
    objectName: "historyAssignCharacterDialog"

    // Region of the ticked records, which the list is narrowed to: a record can only go to a
    // character of its own region. "MIXED" when they span regions: no character fits them all.
    // Both follow the ticks while the dialog is open: the batch acts on every record ticked
    // when it is confirmed, a 勾选全部 that lands meanwhile included (audit 2026-11-20, K3-1).
    readonly property string region: Runtime.app.historyCheckedRegion
    readonly property bool mixedRegions: dialog.region === "MIXED"
    readonly property int checkedCount: Runtime.app.checkedHistoryRunCount
    // A character of the region the ticks had before is no choice for the ones they have now.
    onRegionChanged: if (dialog.target > 0) dialog.target = 0
    // 0 until a character is picked; -1 is 「未分配」.
    property int target: 0
    property bool submitted: false

    modal: true
    focus: true
    padding: 20
    closePolicy: Runtime.app.historyBatchRunning ? Popup.NoAutoClose : Popup.CloseOnEscape
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }

    function ask() {
        // The dialog asks about the ticks as they are now; a 勾选全部 read still on its
        // way is dropped rather than growing the selection under it (K3-1).
        Runtime.app.cancelHistoryCheckAll()
        dialog.target = 0
        dialog.submitted = false
        reasonField.text = qsTranslate("SystemReason", "修改记录所属的角色")
        dialog.open()
    }

    /// The owner calls this as the batch progresses: an accepted change unchecks
    /// its rows, a refused one leaves them checked and its message on show.
    function closeWhenDone(stillChecked) {
        if (dialog.submitted && !Runtime.app.historyBatchRunning && stillChecked === 0)
            dialog.close()
    }

    Connections {
        target: Runtime.app
        enabled: dialog.visible
        function onCharacterProfilesChanged() {
            if (dialog.target > 0 && Runtime.app.characterProfile(dialog.target).profile_id === undefined)
                dialog.target = 0
        }
    }

    function submit() {
        dialog.submitted = true
        Runtime.app.assignCheckedHistoryRunsToProfile(Math.max(0, dialog.target),
                                                      Theme.storedReason(reasonField.text))
    }

    contentItem: ColumnLayout {
        spacing: 14

        HeadingLabel {
            Layout.fillWidth: true
            text: qsTr("把 %1 条记录改到哪个角色？").arg(dialog.checkedCount)
            wrapMode: Text.Wrap
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("记录改到哪个角色名下，就计入哪个角色的进度。各角色的起始次数不会跟着变，需要时请到「设置 › 成就」分别修改。")
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
        }

        Text {
            objectName: "historyAssignCharacterMixed"
            Layout.fillWidth: true
            visible: dialog.mixedRegions
            text: qsTr("勾选的记录来自不同区服，只能一起改为「未分配」。要改到某个角色，请先按区服筛选，再勾选那个区服的记录。")
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: Theme.orangeText
            font.pixelSize: Theme.fs(12)
        }

        CharacterSelector {
            id: targetBox
            objectName: "historyAssignCharacterTarget"
            Layout.fillWidth: true
            region: dialog.region
            selection: dialog.target
            includeUnassigned: true
            enabled: !Runtime.app.historyBatchRunning
            // Nothing is chosen for the player: the first row asks.
            includeAll: true
            allLabel: qsTr("请选择角色")
            Accessible.name: qsTr("改到的角色")
            onProfilePicked: function(profileId) { dialog.target = profileId }
        }

        StyledTextField {
            id: reasonField
            objectName: "historyAssignCharacterReason"
            Layout.fillWidth: true
            maximumLength: 500
            enabled: !Runtime.app.historyBatchRunning
            placeholderText: qsTranslate("HistoryPage", "操作原因（必填）")
            Accessible.name: qsTranslate("HistoryPage", "批量操作原因")
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.submitted && Runtime.app.historyBatchFeedback.length > 0
            text: Runtime.app.historyBatchFeedback
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: Theme.orangeText
            font.pixelSize: Theme.fs(12)
        }

        RowLayout {
            Layout.fillWidth: true
            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "historyAssignCharacterCancel"
                text: qsTranslate("HistoryPage", "取消")
                enabled: !Runtime.app.historyBatchRunning
                onClicked: dialog.close()
            }
            AppButton {
                objectName: "historyAssignCharacterConfirm"
                text: Runtime.app.historyBatchRunning ? qsTranslate("HistoryPage", "正在处理…") : qsTr("确认修改")
                variant: "primary"
                enabled: !Runtime.app.historyBatchRunning && dialog.checkedCount > 0 && dialog.target !== 0
                         && reasonField.text.trim().length > 0
                onClicked: dialog.submit()
            }
        }
    }
}
