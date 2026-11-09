pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// Full note/result form shared by the main workspace and the modal editor.
ScrollView {
    id: formViewport
    required property var dialog
    readonly property alias noteEditor: textArea
    visible: !formViewport.dialog.standalone
    implicitHeight: form.implicitHeight
    contentWidth: availableWidth
    clip: true
    ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
    ColumnLayout {
        id: form
        width: formViewport.availableWidth
        spacing: 14

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            Image {
                visible: formViewport.dialog.reminderChrome
                source: "qrc:/app/app.png"
                Layout.preferredWidth: 28
                Layout.preferredHeight: 28
                sourceSize.width: 56
                sourceSize.height: 56
            }
            CardKicker {
                text: formViewport.dialog.reminderChrome ? qsTranslate("ReflectionDialog", "导随记录器") : formViewport.dialog.kicker
            }

            Item {
                Layout.fillWidth: true
            }

            Text {
                text: formViewport.dialog.reminderChrome ? formViewport.dialog.kicker : formViewport.dialog.runId
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                elide: Text.ElideMiddle
                Layout.maximumWidth: 260
            }
            AppButton {
                objectName: formViewport.dialog.standalone ? "deferReflectionButtonFull" : "deferReflectionButton"
                visible: formViewport.dialog.reminderChrome
                text: qsTranslate("ReflectionDialog", "收起")
                iconName: "chevrons-down"
                compact: true
                enabled: !formViewport.dialog.busy
                onClicked: formViewport.dialog.defer()
            }
            DragHandler {
                enabled: formViewport.dialog.standalone
                target: null
                onActiveChanged: if (active && formViewport.dialog.parent && formViewport.dialog.parent.Window.window)
                    formViewport.dialog.parent.Window.window.startSystemMove()
            }
        }

        HeadingLabel {
            Layout.fillWidth: true
            text: formViewport.dialog.reminderChrome ? (formViewport.dialog.askingResult ? qsTranslate("ReflectionDialog", "本次结果待确认") : qsTranslate("ReflectionDialog", "本次笔记待补录")) : (formViewport.dialog.askingResult ? qsTranslate("ReflectionDialog", "本次导随结果") : qsTranslate("ReflectionDialog", "导随笔记"))
            font.pixelSize: Theme.dialogTitleSize(20)
        }

        Text {
            Layout.fillWidth: true
            visible: formViewport.dialog.reminderChrome
            text: qsTranslate("ReflectionDialog", "关闭只会收起提醒，待办和草稿会保留，可从“待处理”继续。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
        Text {
            Layout.fillWidth: true
            visible: formViewport.dialog.unavailable
            text: qsTranslate("ReflectionDialog", "这条记录已删除或暂时无法读取。草稿仍可复制，当前不能提交。")
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
        Text {
            Layout.fillWidth: true
            visible: !!formViewport.dialog.recovery && formViewport.dialog.recovery.error.length > 0
            text: formViewport.dialog.recovery ? formViewport.dialog.recovery.error : ""
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
        AppButton {
            objectName: "reviewRecoveredRecordButton"
            visible: !formViewport.dialog.askingResult && !!formViewport.dialog.runData.pending_review && !formViewport.dialog.unavailable
            text: qsTranslate("ReflectionDialog", "在历史记录中复核")
            enabled: !formViewport.dialog.busy
            onClicked: formViewport.dialog.requestReview()
        }
        ColumnLayout {
            Layout.fillWidth: true
            visible: formViewport.dialog.conflictUnresolved
            Text {
                Layout.fillWidth: true
                text: qsTranslate("ReflectionDialog", "已保存的笔记有变化。下面保留了旧草稿，请选择要继续编辑的内容。最新笔记：\n%1").arg(formViewport.dialog.displayNote(formViewport.dialog.baselineText) || qsTranslate("ReflectionDialog", "（空）"))
                textFormat: Text.PlainText
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
            RowLayout {
                AppButton {
                    objectName: formViewport.dialog.standalone ? "keepRecoveryDraftButtonFull" : "keepRecoveryDraftButton"
                    text: qsTranslate("ReflectionDialog", "继续使用草稿")
                    onClicked: {
                        formViewport.dialog.conflictUnresolved = false;
                        formViewport.dialog.persistDraft(true);
                    }
                }
                AppButton {
                    objectName: formViewport.dialog.standalone ? "useLatestNoteButtonFull" : "useLatestNoteButton"
                    text: qsTranslate("ReflectionDialog", "使用最新笔记")
                    onClicked: formViewport.dialog.useLatestNote()
                }
            }
        }

        ColumnLayout {
            objectName: "missingJobField"
            Layout.fillWidth: true
            visible: formViewport.dialog.needsJob
            spacing: 6

            FieldLabel {
                text: qsTranslate("ReflectionDialog", "本次职业（未自动识别，可补录）")
            }
            StyledComboBox {
                id: completedJobCombo
                objectName: "completedJobCombo"
                Layout.fillWidth: true
                enabled: !formViewport.dialog.busy
                model: formViewport.dialog.jobOptions
                textRole: "label"
                showJobIcons: true
                Binding {
                    target: completedJobCombo
                    property: "currentIndex"
                    value: formViewport.dialog.jobIndex
                    restoreMode: Binding.RestoreNone
                }
                onActivated: formViewport.dialog.jobIndex = currentIndex
            }
        }

        // The question itself. Deliberately three plain answers and no default:
        // the program has no evidence either way and must not pick for the user.
        ColumnLayout {
            Layout.fillWidth: true
            visible: formViewport.dialog.askingResult
            spacing: 8

            Text {
                Layout.fillWidth: true
                textFormat: Text.PlainText
                text: qsTranslate("ReflectionDialog", "《%1》通关了吗？软件尚未确认本次结果，" + "只有你确认“通关”后这一次才会计入导随次数。").arg(formViewport.dialog.dutyText)
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                wrapMode: Text.WordWrap
            }

            RowLayout {
                Layout.fillWidth: true
                spacing: 8

                AppButton {
                    objectName: formViewport.dialog.standalone ? "confirmCompletedButtonFull" : "confirmCompletedButton"
                    variant: "primary"
                    enabled: !formViewport.dialog.busy && !formViewport.dialog.conflictUnresolved && !formViewport.dialog.unavailable
                    text: qsTranslate("ReflectionDialog", "通关")
                    onClicked: formViewport.dialog.resolveWith("COMPLETED", QT_TRANSLATE_NOOP("SystemReason", "用户确认通关"))
                }

                AppButton {
                    objectName: formViewport.dialog.standalone ? "confirmLeftButtonFull" : "confirmLeftButton"
                    enabled: !formViewport.dialog.busy && !formViewport.dialog.conflictUnresolved && !formViewport.dialog.unavailable
                    text: qsTranslate("ReflectionDialog", "退出 / 放弃")
                    onClicked: formViewport.dialog.resolveWith("LEFT_OR_ABANDONED", QT_TRANSLATE_NOOP("SystemReason", "用户确认退出或放弃"))
                }

                Item {
                    Layout.fillWidth: true
                }

                AppButton {
                    objectName: formViewport.dialog.inlinePresentation ? "deferReflectionButton" : "deferReflectionButtonFull"
                    enabled: !formViewport.dialog.resolving
                    text: qsTranslate("ReflectionDialog", "稍后再说")
                    // Nothing is written: the run stays 待复核 and 待处理
                    // keeps offering the same question.
                    onClicked: formViewport.dialog.defer()
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

                // Set when the duty name takes its second line; the columns then
                // hang from the top so their labels stay level.
                property bool dutyWrapped: false

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
                        // A long duty name (most English and Japanese ones) takes a
                        // second line instead of an ellipsis; the others stay on one.
                        {
                            k: qsTranslate("ReflectionDialog", "副本"),
                            wraps: true,
                            v: Theme.dutyName(formViewport.dialog.runData) || qsTranslate("ReflectionDialog", "未知副本")
                        },
                        {
                            k: qsTranslate("ReflectionDialog", "职业"),
                            v: formViewport.dialog.selectedJobId > 0 ? formViewport.dialog.jobOptions[formViewport.dialog.jobIndex].label : Theme.jobName(formViewport.dialog.runData) || qsTranslate("ReflectionDialog", "未知")
                        },
                        {
                            k: qsTranslate("ReflectionDialog", "结果"),
                            v: Runtime.fmt.resultLabel(formViewport.dialog.runData.result || "UNKNOWN")
                        },
                        {
                            k: qsTranslate("ReflectionDialog", "耗时"),
                            v: Runtime.fmt.duration(formViewport.dialog.runData.duration_ms)
                        }
                    ]

                    delegate: ColumnLayout {
                        id: summaryItem
                        required property var modelData

                        Layout.fillWidth: true
                        Layout.preferredWidth: 1
                        Layout.alignment: summaryGrid.dutyWrapped ? Qt.AlignLeft | Qt.AlignTop : Qt.AlignLeft | Qt.AlignVCenter
                        spacing: 2

                        Text {
                            text: summaryItem.modelData.k
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(11)
                        }

                        Text {
                            Layout.fillWidth: true
                            text: summaryItem.modelData.v
                            textFormat: Text.PlainText
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(12)
                            font.weight: Theme.figureWeight(true)
                            font.family: Theme.figureFamily
                            font.features: ({
                                    "tnum": 1
                                })
                            wrapMode: summaryItem.modelData.wraps === true ? Text.WordWrap : Text.NoWrap
                            maximumLineCount: summaryItem.modelData.wraps === true ? 2 : 1
                            elide: Text.ElideRight
                            onLineCountChanged: if (summaryItem.modelData.wraps === true)
                                summaryGrid.dutyWrapped = lineCount > 1
                        }
                    }
                }
            }
        }

        RowLayout {
            Layout.fillWidth: true
            visible: !formViewport.dialog.askingResult || formViewport.dialog.promptEnabled
            spacing: 12

            Text {
                text: formViewport.dialog.mood === "unknown" ? qsTranslate("ReflectionDialog", "这次感觉 · 未记录") : qsTranslate("ReflectionDialog", "这次感觉")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            SegmentedControl {
                Layout.preferredWidth: 220
                enabled: !formViewport.dialog.busy
                options: [
                    {
                        value: "good",
                        label: qsTranslate("ReflectionDialog", "顺利")
                    },
                    {
                        value: "ok",
                        label: qsTranslate("ReflectionDialog", "一般")
                    },
                    {
                        value: "bad",
                        label: qsTranslate("ReflectionDialog", "糟心")
                    }
                ]
                currentValue: formViewport.dialog.mood
                onActivated: function (value) {
                    formViewport.dialog.mood = value;
                }
            }

            Item {
                Layout.fillWidth: true
            }
        }

        ScrollView {
            id: textViewport
            Layout.fillWidth: true
            visible: !formViewport.dialog.askingResult || formViewport.dialog.promptEnabled
            Layout.preferredHeight: 132
            contentWidth: availableWidth
            clip: true
            ScrollBar.horizontal.policy: ScrollBar.AlwaysOff

            StyledTextArea {
                id: textArea
                objectName: formViewport.dialog.standalone ? "reflectionTextAreaFull" : "reflectionTextArea"
                width: textViewport.availableWidth
                readOnly: formViewport.dialog.busy
                text: formViewport.dialog.noteText
                onTextChanged: {
                    if (formViewport.dialog.noteText !== text)
                        formViewport.dialog.noteText = text
                }
                Connections {
                    target: formViewport.dialog.editSession
                    function onTextChanged() {
                        if (textArea.text !== formViewport.dialog.noteText)
                            textArea.text = formViewport.dialog.noteText
                    }
                }
                placeholderText: qsTranslate("ReflectionDialog", "记下这次导随的感想：新人表现、机制提醒、想对自己说的话……")
                // ScrollView keeps the cursor visible; native text paste and
                // the image-only shortcut retain their existing behavior.
                Keys.priority: Keys.BeforeItem
                Keys.onPressed: function (event) { formViewport.dialog.handleNotePaste(event) }
            }
        }

        NoteImageStrip {
            objectName: "reflectionNoteImages"
            Layout.fillWidth: true
            visible: !!formViewport.dialog.imageStore && (!formViewport.dialog.askingResult || formViewport.dialog.promptEnabled)
            editable: !!formViewport.dialog.imageStore && !formViewport.dialog.busy
            rows: formViewport.dialog.noteImages
            thumbSize: 64
            onAddRequested: formViewport.dialog.addNoteImage()
            onRemoveRequested: function (row) {
                formViewport.dialog.removeNoteImage(row);
            }
        }

        RowLayout {
            Layout.fillWidth: true
            visible: !!formViewport.dialog.imageStore && (!formViewport.dialog.askingResult || formViewport.dialog.promptEnabled)
            spacing: 8
            AppButton {
                objectName: "pasteReflectionImageButton"
                text: qsTranslate("ReflectionDialog", "粘贴图片")
                iconName: "clipboard-paste"
                compact: true
                enabled: !formViewport.dialog.busy
                onClicked: formViewport.dialog.pasteNoteImage()
            }
            Text {
                Layout.fillWidth: true
                text: qsTranslate("ReflectionDialog", "Ctrl+V · 图片立即保存到本条记录")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                wrapMode: Text.WordWrap
            }
        }

        Text {
            objectName: "reflectionNoteImageError"
            Layout.fillWidth: true
            visible: formViewport.dialog.noteImageError.length > 0
            text: formViewport.dialog.noteImageError
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            visible: formViewport.dialog.errorText.length > 0
            text: formViewport.dialog.errorText
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
                accessibleName: formViewport.dialog.askingResult ? qsTranslate("ReflectionDialog", "结束后自动询问") : qsTranslate("ReflectionDialog", "通关后提醒记笔记")
                scale: 0.8
                checked: formViewport.dialog.askingResult ? formViewport.dialog.confirmEnabled : formViewport.dialog.promptEnabled
                onToggled: function (value) {
                    if (typeof Runtime.settings === "undefined")
                        return;
                    if (formViewport.dialog.askingResult) {
                        if (Runtime.settings.confirmPrompt !== undefined)
                            Runtime.settings.confirmPrompt = value;
                    } else if (Runtime.settings.reflectPrompt !== undefined) {
                        Runtime.settings.reflectPrompt = value;
                    }
                }
            }

            Text {
                text: formViewport.dialog.askingResult ? qsTranslate("ReflectionDialog", "结束后自动询问") : qsTranslate("ReflectionDialog", "通关后提醒记笔记")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            Item {
                Layout.fillWidth: true
            }

            AppButton {
                visible: !formViewport.dialog.askingResult
                enabled: !formViewport.dialog.busy
                objectName: formViewport.dialog.inlinePresentation ? "deferReflectionButton" : "deferReflectionButtonFull"
                text: qsTranslate("ReflectionDialog", "稍后补录")
                onClicked: formViewport.dialog.defer()
            }

            AppButton {
                objectName: formViewport.dialog.standalone ? "saveReflectionButtonFull" : "saveReflectionButton"
                visible: !formViewport.dialog.askingResult
                variant: "primary"
                // A save started elsewhere (the dashboard's 补录) makes the
                // controller drop a second call, so the button waits instead of
                // pretending to submit.
                enabled: !formViewport.dialog.busy && !formViewport.dialog.conflictUnresolved && !formViewport.dialog.unavailable && !(typeof Runtime.app !== "undefined" && Runtime.app.reflectionSaving === true)
                text: formViewport.dialog.submitting ? qsTranslate("ReflectionDialog", "保存中…") : formViewport.dialog.selectedJobId > 0 ? qsTranslate("ReflectionDialog", "保存记录") : (textArea.text.trim().length === 0 && !!formViewport.dialog.runData.reflection ? qsTranslate("ReflectionDialog", "清空笔记") : qsTranslate("ReflectionDialog", "保存笔记"))
                onClicked: formViewport.dialog.saveExplicitNote()
            }
        }
    }
}
