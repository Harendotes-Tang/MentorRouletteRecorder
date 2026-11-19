pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

MotionDialog {
    id: dialog
    objectName: "historyRegionMigrationDialog"

    property string source: ""
    property int checkedCount: 0
    property int targetProfile: 0
    property bool submitted: false
    readonly property var preview: Runtime.app.historyRegionMigrationPreview
    readonly property bool previewReady: dialog.preview.target_baseline !== undefined
    readonly property bool busy: Runtime.app.historyBatchRunning || Runtime.app.historyRegionMigrationLoading
    readonly property string destination: regionBox.currentValue || ""
    readonly property int maxOverlap: dialog.previewReady
                                      ? Math.min(dialog.preview.contributing_count, dialog.preview.target_baseline) : 0
    readonly property bool overlapValid: overlapField.acceptableInput && Number(overlapField.text) <= dialog.maxOverlap
    readonly property var regions: {
        const choices = [{ value: "", label: qsTr("请选择目标区服") }]
        for (const region of ["CN", "TC", "GLOBAL"])
            if (region !== dialog.source)
                choices.push({ value: region, label: Theme.live(Runtime.fmt.regionLabel(region)) })
        return choices
    }

    modal: true
    focus: true
    padding: 20
    closePolicy: Runtime.app.historyBatchRunning ? Popup.NoAutoClose : Popup.CloseOnEscape
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }

    function resetPreview() {
        Runtime.app.clearHistoryRegionMigrationPreview()
        overlapField.clear()
        dialog.submitted = false
    }

    function ask(region, count) {
        dialog.source = region
        dialog.checkedCount = count
        regionBox.currentIndex = 0
        dialog.targetProfile = 0
        reasonField.clear()
        dialog.resetPreview()
        dialog.open()
    }

    onClosed: dialog.resetPreview()
    onOpened: regionBox.forceActiveFocus()

    Connections {
        target: Runtime.app
        enabled: dialog.visible
        function onHistoryBatchChanged() {
            if (dialog.submitted && !Runtime.app.historyBatchRunning && Runtime.app.checkedHistoryRunCount === 0)
                dialog.close()
        }
        function onCheckedHistoryRunsChanged() {
            if (!Runtime.app.historyBatchRunning
                && (dialog.checkedCount !== Runtime.app.checkedHistoryRunCount
                    || dialog.source !== Runtime.app.historyMigrationSource))
                dialog.close()
        }
        function onCharacterProfilesChanged() {
            if (dialog.targetProfile > 0 && Runtime.app.characterProfile(dialog.targetProfile).profile_id === undefined) {
                dialog.targetProfile = 0
                dialog.resetPreview()
            }
        }
    }

    contentItem: ColumnLayout {
        spacing: 12

        HeadingLabel {
            Layout.fillWidth: true
            text: qsTr("从 %2 迁移 %1 条记录").arg(dialog.checkedCount)
                  .arg(Theme.live(Runtime.fmt.regionLabel(dialog.source)))
            wrapMode: Text.Wrap
        }

        StyledComboBox {
            id: regionBox
            objectName: "historyRegionMigrationRegion"
            Layout.fillWidth: true
            model: dialog.regions
            textRole: "label"
            valueRole: "value"
            enabled: !dialog.busy
            Accessible.name: qsTr("目标区服")
            onActivated: {
                dialog.targetProfile = 0
                dialog.resetPreview()
            }
        }

        CharacterSelector {
            id: roleBox
            objectName: "historyRegionMigrationProfile"
            Layout.fillWidth: true
            region: dialog.destination
            selection: dialog.targetProfile
            includeAll: true
            allLabel: qsTr("请选择目标角色")
            enabled: dialog.destination !== "" && !dialog.busy
            Accessible.name: qsTr("目标角色")
            onProfilePicked: function(profileId) {
                dialog.targetProfile = profileId
                dialog.resetPreview()
            }
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.destination !== "" && roleBox.profiles.length === 0
            text: qsTr("目标区服还没有角色，可在「设置 › 成就」中添加。")
            textFormat: Text.PlainText
            color: Theme.textSecondary
            wrapMode: Text.Wrap
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.previewReady
            text: dialog.previewReady
                  ? qsTr("%1 条通关将计入目标角色。目标角色的起始次数为 %2。")
                    .arg(dialog.preview.contributing_count).arg(dialog.preview.target_baseline)
                  : ""
            textFormat: Text.PlainText
            color: Theme.textPrimary
            wrapMode: Text.Wrap
        }

        // Owner decision 2026-11-19 (audit H1-1): the source's starting count is never
        // changed by a move, and the preview says so before anything is confirmed.
        Text {
            objectName: "historyRegionMigrationSourceNote"
            Layout.fillWidth: true
            visible: dialog.previewReady
            text: qsTr("来源角色的起始次数不会变化。这些记录导入时如果已从来源的起始次数中扣除，迁移后请到「设置 › 成就」自行调整。")
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.previewReady
            text: qsTr("已包含的通关数")
            color: Theme.textSecondary
            wrapMode: Text.Wrap
        }

        StyledTextField {
            id: overlapField
            objectName: "historyRegionMigrationOverlap"
            Layout.fillWidth: true
            visible: dialog.previewReady
            enabled: !dialog.busy
            placeholderText: qsTr("已包含在起始次数中的通关数（0–%1）").arg(dialog.maxOverlap)
            Accessible.name: qsTr("已包含的通关数")
            inputMethodHints: Qt.ImhDigitsOnly
            maximumLength: 4
            validator: RegularExpressionValidator { regularExpression: /[0-9]{1,4}/ }
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.previewReady && dialog.overlapValid
            text: visible
                  ? qsTr("目标起始次数变为 %1，目标成就进度增加 %2。来源起始次数保持原值。")
                    .arg(dialog.preview.target_baseline - Number(overlapField.text))
                    .arg(dialog.preview.contributing_count - Number(overlapField.text))
                  : ""
            textFormat: Text.PlainText
            color: Theme.textPrimary
            wrapMode: Text.Wrap
        }

        StyledTextField {
            id: reasonField
            objectName: "historyRegionMigrationReason"
            Layout.fillWidth: true
            visible: dialog.previewReady
            enabled: !dialog.busy
            maximumLength: 500
            placeholderText: qsTranslate("HistoryPage", "操作原因（必填）")
            Accessible.name: qsTranslate("HistoryPage", "批量操作原因")
        }

        Text {
            Layout.fillWidth: true
            visible: text.length > 0
            text: dialog.submitted ? Runtime.app.historyBatchFeedback : Runtime.app.historyRegionMigrationFeedback
            textFormat: Text.PlainText
            color: Theme.orangeText
            wrapMode: Text.Wrap
        }

        RowLayout {
            Layout.fillWidth: true
            Item { Layout.fillWidth: true }
            AppButton {
                text: qsTranslate("HistoryPage", "取消")
                enabled: !Runtime.app.historyBatchRunning
                onClicked: dialog.close()
            }
            AppButton {
                objectName: "historyRegionMigrationPreview"
                text: Runtime.app.historyRegionMigrationLoading ? qsTr("正在预览…") : qsTr("预览")
                enabled: Runtime.app.historyMigrationReady && !dialog.busy
                         && dialog.destination !== "" && dialog.targetProfile > 0
                onClicked: {
                    dialog.resetPreview()
                    Runtime.app.previewHistoryRegionMigration(dialog.destination, dialog.targetProfile)
                }
            }
            AppButton {
                objectName: "historyRegionMigrationConfirm"
                text: Runtime.app.historyBatchRunning ? qsTranslate("HistoryPage", "正在处理…") : qsTr("确认迁移")
                variant: "primary"
                visible: dialog.previewReady
                enabled: Runtime.app.historyMigrationReady && !dialog.busy
                         && dialog.overlapValid && reasonField.text.trim().length > 0
                onClicked: {
                    dialog.submitted = true
                    Runtime.app.movePreviewedHistoryRuns(Number(overlapField.text), reasonField.text)
                }
            }
        }
    }
}
