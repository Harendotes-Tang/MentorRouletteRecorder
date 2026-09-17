import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// First-run onboarding: the historical 导随 baseline and the achievement goal.
Dialog {
    id: dialog

    property int goalCount: 2000
    property int baselineCount: 0

    signal saved(int baseline, string reason)
    signal skipped(string reason)

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 520
    padding: 20
    closePolicy: Popup.NoAutoClose

    background: DialogFrame {}

    enter: Transition {
        NumberAnimation { property: "opacity"; from: 0; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
        NumberAnimation { property: "scale"; from: 0.97; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
    }
    exit: Transition {
        NumberAnimation { property: "opacity"; from: 1; to: 0; duration: Theme.motionFast }
    }

    property string baselineText: String(baselineCount)
    property string goalText: String(goalCount)
    property string errorText: ""

    function openDialog() {
        baselineText = String(baselineCount)
        goalText = String(goalCount)
        errorText = ""
        open()
    }

    contentItem: ColumnLayout {
        spacing: 14

        CardKicker { text: qsTr("首次启动 · 1 / 1") }

        HeadingLabel {
            Layout.fillWidth: true
            text: qsTr("你已经完成了多少次导随？")
            font.pixelSize: Theme.dialogTitleSize(24)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("软件只能记录安装之后的导随。填写游戏内成就面板显示的当前完成数作为基数，%1 次进度 = 基数 + 软件记录。之后可在设置中修改。")
                  .arg(dialog.goalText)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.WordWrap
        }

        GridLayout {
            Layout.fillWidth: true
            columns: 2
            columnSpacing: 12
            rowSpacing: 4

            FieldLabel { text: qsTr("当前已完成次数") }
            FieldLabel { text: qsTr("目标") }

            StyledTextField {
                id: baselineField
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                implicitHeight: 48
                font.pixelSize: Theme.fs(22)
                font.bold: true
                text: dialog.baselineText
                placeholderText: qsTr("例如 1374")
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 0; top: 999999 }
                onTextChanged: {
                    dialog.baselineText = text
                    dialog.errorText = ""
                }
            }

            StyledTextField {
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                implicitHeight: 48
                font.pixelSize: Theme.fs(22)
                font.bold: true
                text: dialog.goalText
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 1; top: 999999 }
                onTextChanged: {
                    dialog.goalText = text
                    dialog.errorText = ""
                }
            }
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("生效时间 %1 · 之前的自动记录不会重复计入。")
                  .arg(Fmt.localDate(new Date().toISOString()))
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            text: dialog.errorText
            visible: text.length > 0
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 10

            AppButton {
                text: qsTr("从 0 开始")
                onClicked: dialog.skipped(qsTr("首次启动未填写历史基数"))
            }

            Item {
                Layout.fillWidth: true
            }

            AppButton {
                variant: "primary"
                text: qsTr("保存并开始")
                onClicked: {
                    const parsed = Number(dialog.baselineText)
                    const goal = Number(dialog.goalText)
                    if (!Number.isFinite(parsed) || parsed < 0) {
                        dialog.errorText = qsTr("基数必须是大于等于 0 的整数。")
                        return
                    }
                    if (!Number.isFinite(goal) || goal < 1) {
                        dialog.errorText = qsTr("目标值必须是大于 0 的整数。")
                        return
                    }
                    dialog.goalCount = Math.floor(goal)
                    dialog.saved(Math.floor(parsed), qsTr("首次启动填写基数"))
                }
            }
        }
    }
}
