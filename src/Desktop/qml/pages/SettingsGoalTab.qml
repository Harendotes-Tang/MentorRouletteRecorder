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

    readonly property int recordedCount: App.dashboard.completed_count || 0

    Connections {
        target: App
        function onDashboardChanged() {
            if (tab.achievementEdited)
                return
            tab.baselineText = String(App.baselineCount)
            tab.goalText = String(App.goalCount)
        }
        // The Collector's own refusal (ERR_REASON_REQUIRED, ERR_BAD_REQUEST …)
        // is shown next to the field, not only in a toast that scrolls away.
        function onBaselineFailed(code, message) {
            tab.baselineErrorText = (message && message.length > 0 ? message : code)
                                    + " (" + code + ")"
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
            baselineErrorText = qsTr("修改成就进度前必须填写原因（ERR_REASON_REQUIRED）。")
            return
        }
        baselineErrorText = ""
        achievementEdited = false
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
                                         .arg(App.baselineCount + tab.recordedCount)
                    color: Theme.gold2
                    font.family: Theme.numFamily
                    font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                    font.pixelSize: Theme.fs(18)
                    font.features: ({ "tnum": 1 })
                }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("修改后立即重算 · 导入按 run_id 去重")
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
                onClicked: tab.submitAchievement()
            }
        }
    }
}
