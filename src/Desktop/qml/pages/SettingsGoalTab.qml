import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 设置 · 成就: 目标值、安装前已完成次数（基数）与修改原因。
ColumnLayout {
    id: tab

    signal openBaselineRequested()

    Layout.fillWidth: true
    spacing: 16

    property string baselineText: String(App.baselineCount)
    property string goalText: String(App.goalCount)
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
    readonly property bool settingsLoaded: App.achievementSettingsLoaded

    // The Collector's achievement_progress, and the part of it this software
    // recorded. Not completed_count: a COMPLETED run that does not count towards
    // the goal is in that total only (review OK-1).
    readonly property int progressCount: App.dashboard.achievement_progress !== undefined
        ? Number(App.dashboard.achievement_progress) : App.baselineCount
    readonly property int recordedCount: Math.max(0, tab.progressCount - App.baselineCount)

    Connections {
        target: App
        function onDashboardChanged() {
            if (tab.achievementEdited)
                return
            tab.baselineText = String(App.baselineCount)
            tab.goalText = String(App.goalCount)
        }
        // The Collector's own refusal (ERR_REASON_REQUIRED, ERR_BAD_REQUEST …)
        // of this card's save is shown next to the field, not only in a toast
        // that scrolls away.
        function onBaselineFailed(code, message) {
            if (tab.ownSavesOut === 0)
                return
            tab.ownSavesOut -= 1
            tab.baselineErrorText = App.errorText(message, code)
        }
        function onBaselineSaved(goal, baseline) {
            if (tab.ownSavesOut > 0)
                tab.ownSavesOut -= 1
        }
    }

    function submitAchievement() {
        const goal = Number(tab.goalText)
        const baseline = Number(tab.baselineText)
        if (!Number.isFinite(goal) || goal < 1) {
            baselineErrorText = qsTr("目标值必须是大于 0 的整数。")
            return
        }
        if (!Number.isFinite(baseline) || baseline < 0) {
            baselineErrorText = qsTr("基数必须是大于等于 0 的整数。")
            return
        }
        if (!baselineReasonText.trim()) {
            baselineErrorText = App.errorText(qsTr("修改成就进度前必须填写原因。"), "ERR_REASON_REQUIRED")
            return
        }
        baselineErrorText = ""
        achievementEdited = false
        // Counted before the request: a refusal can arrive inside the call.
        ownSavesOut += 1
        App.updateAchievementBaseline(Math.floor(goal), Math.floor(baseline),
                                      baselineReasonText.trim())
    }

    SettingsPanel {
        objectName: "achievementSettingsCard"
        freeLayout: true
        kicker: qsTr("成就进度")

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
                text: qsTr("安装前已完成次数（基数）")
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
            text: qsTr("还没有从采集服务读到已保存的目标与基数，读到之后才能修改和保存。")
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
                placeholderText: qsTr("例如：补录安装前的历史完成数")
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
                    text: qsTr("进度 = 基数 + 软件记录")
                    color: Theme.textSecondary
                    opacity: Theme.dimOpacity(0.6)
                    font.pixelSize: Theme.fs(12)
                }
                Text {
                    objectName: "progressFormulaFigures"
                    text: "%1 + %2 = %3".arg(App.baselineCount)
                                         .arg(tab.recordedCount)
                                         .arg(tab.progressCount)
                    color: Theme.gold2
                    font.family: Theme.numFamily
                    font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                    font.pixelSize: Theme.fs(18)
                    font.features: ({ "tnum": 1 })
                }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("修改后立即重算 · 导入时按记录编号去重")
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
                onClicked: tab.openBaselineRequested()
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
