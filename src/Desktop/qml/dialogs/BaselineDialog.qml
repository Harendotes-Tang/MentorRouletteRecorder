pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

// First-run onboarding: the historical 导随 baseline and the achievement goal.
Dialog {
    id: dialog

    property int goalCount: 2000
    property int baselineCount: 0
    /// The completions this software recorded that count towards the goal: the
    /// Collector's achievement_progress less its baseline. They are added on top of
    /// any baseline (B3-1), and the game's own total already holds them.
    property int recordedCount: 0
    /// True once goalCount / baselineCount are what the Collector stores, read on
    /// the current connection. Until then they are defaults and nothing may be
    /// saved: even 从 0 开始 would overwrite a stored goal (audit 2026-10-03, CS7-D3).
    property bool settingsLoaded: false
    /// Opened again from 设置 · 目标 rather than by a first run: the guide can
    /// then be left with 取消 or Esc without changing anything (review OL-1).
    property bool reopened: false
    /// Its own save is out. The guide ends only on that save's answer: a refusal
    /// leaves it open with what was typed, and only an accepted save closes it and
    /// completes the first run (audit 2026-10-03, DT6-X1). Main.qml takes the answer.
    property bool saving: false
    /// A baseline save is out, whoever sent it. The guide does not send beside one,
    /// so the answer it waits for cannot be another's.
    property bool busy: false
    /// The guide sent a save since it was opened. An accepted save closes it, so
    /// while it is still open and not saving, that save was refused.
    property bool saveSent: false

    // The goal goes out with the answer; goalCount stays bound to the goal in
    // force, so the next opening offers that one.
    signal saved(int baseline, int goal, string reason)
    signal skipped(int goal, string reason)

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 520
    padding: 20
    // While its save is out it cannot be dismissed, so the answer lands on it.
    closePolicy: reopened && !saving ? Popup.CloseOnEscape : Popup.NoAutoClose

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
    // Set when the user types, so stored values that arrive later never
    // overwrite an answer being written.
    property bool edited: false

    // Parsed the way 保存并开始 parses it, so the line below describes what is sent:
    // a whole number of at least 0, else -1. An empty field is no number, and is
    // not saved as 0 (从 0 开始 is there for that).
    readonly property int typedBaseline: {
        const parsed = Number(baselineText)
        return baselineText.trim().length > 0 && Number.isFinite(parsed) && parsed >= 0
            ? Math.floor(parsed) : -1
    }
    // The progress the typed baseline gives (B3-1): every completion this software
    // recorded is added on top of it, whenever it ended. Shown once there are such
    // records - a guide opened late - because the game's own total already holds
    // them: typed in as the baseline they would count twice. Until the field holds
    // a number the stored baseline stands.
    readonly property int previewBaseline: typedBaseline >= 0 ? typedBaseline : baselineCount
    readonly property string previewText: recordedCount <= 0 ? ""
        : qsTr("软件中已有 %1 次通关计入进度，总进度为：%2 + %1 = %3。如果按游戏内成就面板的总次数填写起始次数，请先减去这 %1 次，避免重复计算。")
              .arg(recordedCount).arg(previewBaseline).arg(previewBaseline + recordedCount)

    function openDialog(fromSettings) {
        reopened = fromSettings === true
        edited = false
        saving = false
        saveSent = false
        baselineText = String(baselineCount)
        goalText = String(goalCount)
        errorText = ""
        open()
    }

    // Called just before a save goes out. The values stay as sent until the
    // answer: a refusal is shown beside them, and stored values read meanwhile do
    // not replace them.
    function beginSave() {
        errorText = ""
        edited = true
        saving = true
        saveSent = true
    }

    // The stored settings may arrive after the guide opened (a first run opens it
    // at start-up): an untouched form takes them.
    function refill() {
        if (edited)
            return
        baselineText = String(baselineCount)
        goalText = String(goalCount)
    }
    onBaselineCountChanged: refill()
    onGoalCountChanged: refill()
    onSettingsLoadedChanged: refill()

    contentItem: ColumnLayout {
        spacing: 14

        CardKicker { text: dialog.reopened ? qsTr("起始次数") : qsTr("首次启动 · 1 / 1") }

        HeadingLabel {
            Layout.fillWidth: true
            text: qsTr("开始记录前，你已完成多少次导随？")
            font.pixelSize: Theme.dialogTitleSize(24)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("起始次数是开始用本软件记录前，已经完成的导随次数。目标是 %1 次。总进度 = 起始次数 + 软件中计入进度的通关次数。导入旧记录时，若确认它们已包含在起始次数中，软件会相应扣减，避免重复计数。已有数据可保留或调整。")
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

            FieldLabel { text: qsTr("起始次数") }
            FieldLabel { text: qsTr("目标") }

            StyledTextField {
                id: baselineField
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                implicitHeight: 48
                font.pixelSize: Theme.fs(22)
                font.bold: true
                enabled: dialog.settingsLoaded && !dialog.saving
                text: dialog.baselineText
                placeholderText: qsTr("例如 1374")
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 0; top: 999999 }
                onTextChanged: {
                    dialog.baselineText = text
                    dialog.errorText = ""
                    if (activeFocus)
                        dialog.edited = true
                }
            }

            StyledTextField {
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                implicitHeight: 48
                font.pixelSize: Theme.fs(22)
                font.bold: true
                enabled: dialog.settingsLoaded && !dialog.saving
                text: dialog.goalText
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 1; top: 999999 }
                onTextChanged: {
                    dialog.goalText = text
                    dialog.errorText = ""
                    if (activeFocus)
                        dialog.edited = true
                }
            }
        }

        Text {
            objectName: "baselineWaitingText"
            Layout.fillWidth: true
            visible: !dialog.settingsLoaded
            text: qsTr("正在读取已保存的目标和起始次数，读取完成后才能修改和保存。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            objectName: "baselinePreviewText"
            Layout.fillWidth: true
            visible: dialog.settingsLoaded && text.length > 0
            text: dialog.previewText
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
                objectName: "baselineSkipButton"
                text: qsTr("从 0 开始")
                enabled: dialog.settingsLoaded && !dialog.saving && !dialog.busy
                // The goal shown, not the one stored: it may have been changed
                // here (audit 2026-10-03, DT6-X2).
                onClicked: {
                    const goal = Number(dialog.goalText)
                    if (!Number.isFinite(goal) || goal < 1) {
                        dialog.errorText = qsTr("目标值必须是大于 0 的整数。")
                        return
                    }
                    dialog.beginSave()
                    dialog.skipped(Math.floor(goal),
                                   dialog.reopened ? QT_TRANSLATE_NOOP("SystemReason", "在设置中改为从 0 开始")
                                                   : QT_TRANSLATE_NOOP("SystemReason", "首次启动未填写起始次数"))
                }
            }

            Item {
                Layout.fillWidth: true
            }

            // A first run has to be answered, but not with values never read:
            // while they are missing - a Collector that never answers included -
            // the guide can be left for later and comes back on the next start.
            // So it can once a save was refused: a Collector that keeps refusing
            // would otherwise leave a modal window with no way out (review V4-2).
            AppButton {
                objectName: "baselineCancelButton"
                visible: dialog.reopened || !dialog.settingsLoaded
                         || (dialog.saveSent && !dialog.saving)
                enabled: !dialog.saving
                text: dialog.reopened ? qsTr("取消") : qsTr("稍后填写")
                onClicked: dialog.close()
            }

            AppButton {
                objectName: "baselineSaveButton"
                variant: "primary"
                enabled: dialog.settingsLoaded && !dialog.saving && !dialog.busy
                text: dialog.saving ? qsTr("保存中…") : qsTr("保存并开始")
                onClicked: {
                    const parsed = dialog.typedBaseline
                    const goal = Number(dialog.goalText)
                    if (parsed < 0) {
                        dialog.errorText = qsTr("起始次数必须是大于等于 0 的整数。")
                        return
                    }
                    if (!Number.isFinite(goal) || goal < 1) {
                        dialog.errorText = qsTr("目标值必须是大于 0 的整数。")
                        return
                    }
                    dialog.beginSave()
                    dialog.saved(Math.floor(parsed), Math.floor(goal),
                                 dialog.reopened ? QT_TRANSLATE_NOOP("SystemReason", "在设置中重新填写起始次数")
                                                 : QT_TRANSLATE_NOOP("SystemReason", "首次启动填写起始次数"))
                }
            }
        }
    }
}
