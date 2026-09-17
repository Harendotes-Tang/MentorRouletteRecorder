import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 导随心得: the per-run diary entry. Opened automatically after a COMPLETED
// run (Settings.reflectPrompt), from the dashboard's 补录笔记 button and from
// the run detail panel's 笔记 tab.
//
// It also carries the 本次导随结果 question: with the shipping 国服 protocol
// profile no packet says whether a duty was cleared, so a finished 导随 stops
// at 待复核 and the achievement count only moves once the user answers 通关.
// The program never decides a result by itself.
Dialog {
    id: dialog

    property var runData: ({})
    property string kicker: qsTr("补录笔记")
    property string mood: "good"
    property string errorText: ""
    property bool submitting: false
    // True while the dialog is asking 通关 / 未通关 first. The 心得 fields stay
    // visible underneath, so answering both is one window and one click.
    property bool askingResult: false
    property bool resolving: false
    property bool waitingForReflection: false
    property string pendingResult: ""
    property string pendingReason: ""
    property int jobIndex: 0
    readonly property bool needsJob: !(runData && runData.job_id > 0)
    readonly property var jobOptions: [{ job_id: null, job_name: qsTr("稍后补录") }]
        .concat(typeof App !== "undefined" && App.battleJobOptions ? App.battleJobOptions : [])
    readonly property int selectedJobId: needsJob && jobIndex > 0 && jobIndex < jobOptions.length
                                         ? jobOptions[jobIndex].job_id : 0
    // True between App.resultConfirmationShown() for the run on screen and the
    // matching App.resultConfirmationClosed(); the closing half must run on
    // every way out of this window - see onClosed.
    property bool resultAcknowledged: false
    readonly property bool busy: submitting || resolving

    readonly property string runId: runData && runData.run_id ? runData.run_id : ""
    readonly property int runRevision: runData && runData.revision !== undefined
                                       ? Number(runData.revision) : -1
    readonly property bool promptEnabled: (typeof Settings !== "undefined"
                                           && Settings.reflectPrompt !== undefined)
                                          ? Settings.reflectPrompt : true
    readonly property bool confirmEnabled: (typeof Settings !== "undefined"
                                            && Settings.confirmPrompt !== undefined)
                                           ? Settings.confirmPrompt : true
    readonly property string dutyText: (runData && runData.duty_name)
                                       ? runData.duty_name : qsTr("未知副本")

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 560
    padding: 22
    // No dismissal while a request is in flight, so the reply always lands on
    // the record that sent it.
    closePolicy: busy ? Popup.NoAutoClose : Popup.CloseOnEscape

    background: DialogFrame {}

    enter: Transition {
        NumberAnimation { property: "opacity"; from: 0; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
        NumberAnimation { property: "scale"; from: 0.97; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
    }
    exit: Transition {
        NumberAnimation { property: "opacity"; from: 1; to: 0; duration: Theme.motionFast }
    }

    // Every way out of the window lands here - 通关 / 未通关 accepted, 稍后再说,
    // Escape, a click outside, and the programmatic close() after a saved 心得
    // or an accepted correction. Reporting the close from one place keeps a
    // path added later from omitting it; without the report the controller
    // believes a dialog is still up and silences every further question for the
    // rest of the session.
    //
    // Reported unconditionally, not only for a 结果 question: a run the dialog
    // could not show stays queued, and resultConfirmationClosed() is the only
    // thing that re-offers it. The call is idempotent when no question is on
    // screen.
    onClosed: {
        dialog.resultAcknowledged = false
        if (typeof App !== "undefined"
            && typeof App.resultConfirmationClosed === "function")
            App.resultConfirmationClosed()
    }

    function openForRun(run, kickerText) {
        if (!run || !run.run_id || dialog.busy)
            return
        dialog.runData = run
        dialog.jobIndex = 0
        const existing = run.reflection ? run.reflection : null
        dialog.kicker = kickerText && kickerText.length > 0
                        ? kickerText
                        : (existing ? qsTr("编辑笔记") : qsTr("补录笔记"))
        dialog.mood = existing && existing.mood ? existing.mood : "good"
        textArea.text = existing && existing.text ? existing.text : ""
        dialog.errorText = ""
        dialog.submitting = false
        dialog.askingResult = false
        dialog.resolving = false
        dialog.waitingForReflection = false
        dialog.open()
    }

    // The duty just ended and nothing observed whether it was cleared. The
    // controller re-offers a question the dialog could not show, so the same run
    // can arrive twice: text already typed for it must survive, and only a
    // different run starts over.
    function openForResult(run) {
        if (!run || !run.run_id || dialog.busy)
            return
        const sameRun = dialog.runId.length > 0 && dialog.runId === run.run_id
        dialog.runData = run
        dialog.kicker = qsTr("刚刚结束")
        if (!sameRun) {
            dialog.jobIndex = 0
            dialog.mood = "good"
            textArea.text = ""
        }
        dialog.errorText = ""
        dialog.submitting = false
        dialog.askingResult = true
        dialog.resolving = false
        dialog.waitingForReflection = false
        dialog.open()
        // Acknowledge only once the window is up: until this call the run is not
        // recorded as asked, so a dialog that never opened stays re-offerable.
        if (typeof App !== "undefined"
            && typeof App.resultConfirmationShown === "function") {
            dialog.resultAcknowledged = true
            App.resultConfirmationShown(run.run_id)
        }
    }

    // 通关 / 未通关. Any 心得 already typed is saved with it, and the window
    // stays open until the Collector accepts the correction: a refusal (a stale
    // revision, for instance) must be visible here, not only in a toast.
    function resolveWith(result, reasonText) {
        if (dialog.runId.length === 0 || dialog.busy)
            return
        if (typeof App === "undefined" || !App.resolveRunResult) {
            dialog.errorText = qsTr("当前版本尚未接入结果确认接口。")
            return
        }
        dialog.errorText = ""
        const note = textArea.text.trim()
        const saveNote = note.length > 0 || (!dialog.askingResult && !!dialog.runData.reflection)
        if (saveNote && !App.saveReflection) {
            dialog.errorText = qsTr("当前版本尚未接入笔记保存接口。")
            return
        }
        if (saveNote && App.reflectionSaving === true) {
            dialog.errorText = qsTr("上一条笔记仍在保存中，请稍候再试。")
            return
        }
        dialog.pendingResult = result
        dialog.pendingReason = reasonText
        dialog.resolving = true
        // Submit the correction only after the 心得 was accepted, so a failed
        // save cannot be hidden by a successful result write.
        if (saveNote) {
            dialog.waitingForReflection = true
            App.saveReflection(dialog.runId, dialog.mood, note)
        } else {
            submitPendingResult()
        }
    }

    function submitPendingResult() {
        dialog.waitingForReflection = false
        App.resolveRunResult(dialog.runId, dialog.runRevision,
                             dialog.pendingResult, dialog.pendingReason, dialog.selectedJobId)
    }

    Connections {
        target: typeof App !== "undefined" ? App : null
        ignoreUnknownSignals: true

        function onReflectionSaved(runId, cleared) {
            if (!dialog.visible || runId !== dialog.runId
                || (!dialog.submitting && !dialog.waitingForReflection))
                return
            dialog.submitting = false
            if (dialog.waitingForReflection) {
                dialog.submitPendingResult()
                return
            }
            dialog.close()
        }

        // The controller decides whether the question may be asked (settings,
        // replayed events, one run only once); nothing here repeats those rules.
        function onResultConfirmationRequested(run) {
            dialog.openForResult(run)
        }

        // A live run_updated, or the controller's retry after
        // ERR_REVISION_CONFLICT, moved the record on; the next correction must
        // carry the new revision or be refused for a revision that is only
        // stale on screen. runData is a plain var, so a new object is assigned
        // to make runRevision re-evaluate.
        function onRunRevisionChanged(runId, revision) {
            if (dialog.runId.length === 0 || runId !== dialog.runId)
                return
            var next = Object.assign({}, dialog.runData)
            next.revision = revision
            dialog.runData = next
        }

        function onMutationSucceeded(kind, runId, revision, auditEventId) {
            if (!dialog.visible || !dialog.resolving || dialog.waitingForReflection
                || kind !== "review" || runId !== dialog.runId)
                return
            dialog.resolving = false
            dialog.close()
        }

        function onMutationFailed(code, message) {
            if (!dialog.visible || !dialog.resolving || dialog.waitingForReflection)
                return
            dialog.resolving = false
            dialog.errorText = (message && message.length > 0 ? message : code)
                               + " (" + code + ")"
        }

        function onReflectionFailed(code, message) {
            if (!dialog.visible || (!dialog.submitting && !dialog.waitingForReflection))
                return
            dialog.submitting = false
            dialog.waitingForReflection = false
            dialog.resolving = false
            if ((!message || message.length === 0) && (!code || code.length === 0))
                dialog.errorText = qsTr("保存失败，请重试。")
            else
                dialog.errorText = (message && message.length > 0 ? message : code)
                                   + " (" + code + ")"
        }
    }

    contentItem: ColumnLayout {
        spacing: 14

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            CardKicker { text: dialog.kicker }

            Item { Layout.fillWidth: true }

            Text {
                text: dialog.runId
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                elide: Text.ElideMiddle
                Layout.maximumWidth: 260
            }
        }

        HeadingLabel {
            Layout.fillWidth: true
            text: dialog.askingResult ? qsTr("本次导随结果") : qsTr("导随笔记")
            font.pixelSize: Theme.dialogTitleSize(20)
        }

        ColumnLayout {
            objectName: "missingJobField"
            Layout.fillWidth: true
            visible: dialog.needsJob
            spacing: 6

            FieldLabel { text: qsTr("本次职业（未自动识别，可补录）") }
            StyledComboBox {
                id: completedJobCombo
                objectName: "completedJobCombo"
                Layout.fillWidth: true
                enabled: !dialog.busy
                model: dialog.jobOptions
                textRole: "job_name"
                showJobIcons: true
                Binding {
                    target: completedJobCombo
                    property: "currentIndex"
                    value: dialog.jobIndex
                    restoreMode: Binding.RestoreNone
                }
                onActivated: dialog.jobIndex = currentIndex
            }
        }

        // The question itself. Deliberately three plain answers and no default:
        // the program has no evidence either way and must not pick for the user.
        ColumnLayout {
            Layout.fillWidth: true
            visible: dialog.askingResult
            spacing: 8

            Text {
                Layout.fillWidth: true
                text: qsTr("《%1》打完了吗？国服看不到通关判定，"
                           + "只有你确认“通关”后这一次才会计入导随次数。").arg(dialog.dutyText)
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                wrapMode: Text.WordWrap
            }

            RowLayout {
                Layout.fillWidth: true
                spacing: 8

                AppButton {
                    objectName: "confirmCompletedButton"
                    variant: "primary"
                    enabled: !dialog.resolving
                    text: qsTr("通关")
                    onClicked: dialog.resolveWith("COMPLETED", qsTr("用户确认通关"))
                }

                AppButton {
                    objectName: "confirmLeftButton"
                    enabled: !dialog.resolving
                    text: qsTr("未通关 / 中途离开")
                    onClicked: dialog.resolveWith("LEFT_OR_ABANDONED",
                                                  qsTr("用户确认未通关"))
                }

                Item { Layout.fillWidth: true }

                AppButton {
                    enabled: !dialog.resolving
                    text: qsTr("稍后再说")
                    // Nothing is written: the run stays 待复核 and the total
                    // 总览 banner keeps offering the same two buttons.
                    onClicked: dialog.close()
                }
            }

            Rectangle {
                Layout.fillWidth: true
                Layout.topMargin: 2
                Layout.preferredHeight: 1
                color: Theme.border
            }
        }

        InsetBox {
            Layout.fillWidth: true
            implicitHeight: summaryGrid.implicitHeight + 20

            GridLayout {
                id: summaryGrid

                anchors.left: parent.left
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                anchors.leftMargin: 12
                anchors.rightMargin: 12
                columns: 4
                columnSpacing: 8
                rowSpacing: 4

                Repeater {
                    model: [
                        { k: qsTr("副本"), v: dialog.runData.duty_name || qsTr("未知副本") },
                        { k: qsTr("职业"), v: dialog.selectedJobId > 0
                            ? dialog.jobOptions[dialog.jobIndex].job_name : dialog.runData.job_name || qsTr("未知") },
                        { k: qsTr("结果"), v: Fmt.resultLabel(dialog.runData.result || "UNKNOWN") },
                        { k: qsTr("耗时"), v: Fmt.duration(dialog.runData.duration_ms) }
                    ]

                    delegate: ColumnLayout {
                        required property var modelData

                        Layout.fillWidth: true
                        Layout.preferredWidth: 1
                        spacing: 2

                        Text {
                            text: modelData.k
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(11)
                        }

                        Text {
                            Layout.fillWidth: true
                            text: modelData.v
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(12)
                            font.weight: Theme.figureWeight(true)
                            font.family: Theme.figureFamily
                            font.features: ({ "tnum": 1 })
                            elide: Text.ElideRight
                        }
                    }
                }
            }
        }

        RowLayout {
            Layout.fillWidth: true
            visible: !dialog.askingResult || dialog.promptEnabled
            spacing: 12

            Text {
                text: qsTr("这次感觉")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            SegmentedControl {
                Layout.preferredWidth: 220
                enabled: !dialog.busy
                options: [
                    { value: "good", label: qsTr("顺利") },
                    { value: "ok", label: qsTr("一般") },
                    { value: "bad", label: qsTr("糟心") }
                ]
                currentValue: dialog.mood
                onActivated: function(value) { dialog.mood = value }
            }

            Item { Layout.fillWidth: true }
        }

        StyledTextArea {
            id: textArea
            objectName: "reflectionTextArea"

            Layout.fillWidth: true
            readOnly: dialog.busy
            visible: !dialog.askingResult || dialog.promptEnabled
            Layout.preferredHeight: 132
            placeholderText: qsTr("记下这次导随的感想：新人表现、机制提醒、想对自己说的话……")
        }

        Text {
            Layout.fillWidth: true
            visible: dialog.errorText.length > 0
            text: dialog.errorText
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            Layout.topMargin: 6
            color: Theme.border
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            ToggleSwitch {
                scale: 0.8
                checked: dialog.askingResult ? dialog.confirmEnabled : dialog.promptEnabled
                onToggled: function(value) {
                    if (typeof Settings === "undefined")
                        return
                    if (dialog.askingResult) {
                        if (Settings.confirmPrompt !== undefined)
                            Settings.confirmPrompt = value
                    } else if (Settings.reflectPrompt !== undefined) {
                        Settings.reflectPrompt = value
                    }
                }
            }

            Text {
                text: dialog.askingResult ? qsTr("结束后自动询问") : qsTr("通关后自动弹出")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            Item { Layout.fillWidth: true }

            AppButton {
                visible: !dialog.askingResult
                enabled: !dialog.busy
                text: qsTr("稍后补录")
                onClicked: dialog.close()
            }

            AppButton {
                objectName: "saveReflectionButton"
                visible: !dialog.askingResult
                variant: "primary"
                // A save started elsewhere (the dashboard's 补录) makes the
                // controller drop a second call, so the button waits instead of
                // pretending to submit.
                enabled: !dialog.busy
                         && !(typeof App !== "undefined" && App.reflectionSaving === true)
                text: dialog.submitting
                      ? qsTr("保存中…")
                      : dialog.selectedJobId > 0 ? qsTr("保存记录")
                      : (textArea.text.trim().length === 0 && !!dialog.runData.reflection
                         ? qsTr("清空笔记") : qsTr("保存笔记"))
                onClicked: {
                    if (dialog.runId.length === 0)
                        return
                    if (dialog.selectedJobId > 0) {
                        dialog.resolveWith(dialog.runData.result, qsTr("补录本次导随职业"))
                        return
                    }
                    if (typeof App === "undefined" || !App.saveReflection) {
                        dialog.errorText = qsTr("当前版本尚未接入笔记保存接口。")
                        return
                    }
                    dialog.errorText = ""
                    dialog.submitting = true
                    App.saveReflection(dialog.runId, dialog.mood, textArea.text.trim())
                }
            }
        }
    }
}
