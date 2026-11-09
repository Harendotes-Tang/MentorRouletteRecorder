pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// Shared form body; the controller owns validation, request state and input.
ColumnLayout {
    id: dialogBody

    required property var dialog
    readonly property string submitLabel: {
        if (dialogBody.dialog.submitting)
            return qsTranslate("EditRunDialog", "提交中…")
        if (dialogBody.dialog.recordSaved)
            return dialogBody.dialog.reflectionPending ? qsTranslate("EditRunDialog", "重试心情和心得")
                                                       : qsTranslate("EditRunDialog", "重试图片")
        if (dialogBody.dialog.awaitingRetry)
            return qsTranslate("EditRunDialog", "原样重试")
        return dialogBody.dialog.editMode ? qsTranslate("EditRunDialog", "保存修正")
                                          : qsTranslate("EditRunDialog", "添加记录")
    }
    readonly property alias viewport: formScroll
    function showDutySelection() {
        dutyStep.showSelection();
    }

    spacing: 0

    // ------------------------------------------------ 标题与步骤条 --
    ColumnLayout {
        id: headerBlock

        Layout.fillWidth: true
        Layout.leftMargin: 24
        Layout.rightMargin: 24
        Layout.topMargin: 20
        spacing: 14

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            HeadingLabel {
                text: dialogBody.dialog.editMode ? qsTranslate("EditRunDialog", "手动修正") : qsTranslate("EditRunDialog", "新增遗漏记录")
                font.pixelSize: Theme.dialogTitleSize(24)
            }

            Text {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignBaseline
                // Raw identifiers and column names are maintainer material; a
                // player sees the same fact in plain Chinese.
                text: dialogBody.dialog.editMode && dialogBody.dialog.runData ? (Runtime.app.maintainerToolsVisible ? qsTranslate("EditRunDialog", "%1 · 当前修订 %2").arg(dialogBody.dialog.runData.run_id).arg(dialogBody.dialog.runData.revision || 1) : qsTranslate("EditRunDialog", "修正结果与进度，原记录会保留")) : (Runtime.app.maintainerToolsVisible ? qsTranslate("EditRunDialog", "来源：手动新增") : qsTranslate("EditRunDialog", "这条记录由你手动新增"))
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                elide: Text.ElideMiddle
            }

            Text {
                objectName: "wizardStepLabel"
                Layout.alignment: Qt.AlignBaseline
                text: qsTranslate("EditRunDialog", "第 %1 / 3 步").arg(dialogBody.dialog.currentStep)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }
        }

        RowLayout {
            objectName: "wizardStepBar"
            enabled: !dialogBody.dialog.submitting && !dialogBody.dialog.recordSaved && !dialogBody.dialog.awaitingRetry
            Layout.fillWidth: true
            spacing: 8

            Repeater {
                model: dialogBody.dialog.stepItems

                delegate: Item {
                    id: stepSegment

                    required property var modelData
                    readonly property bool active: dialogBody.dialog.currentStep === modelData.n
                    readonly property bool done: dialogBody.dialog.currentStep > modelData.n
                    readonly property color activeColor: Theme.eorzea ? Theme.gold : Theme.accent

                    objectName: "wizardStep_" + modelData.n
                    Layout.fillWidth: true
                    Layout.preferredWidth: 1
                    implicitHeight: 40

                    Accessible.role: Accessible.PageTab
                    Accessible.name: modelData.label
                    Accessible.selected: active
                    Accessible.onPressAction: dialogBody.dialog.goToStep(modelData.n)

                    Rectangle {
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.bottom: parent.bottom
                        height: 2
                        color: stepSegment.active ? stepSegment.activeColor : Theme.border
                        Behavior on color {
                            ColorAnimation {
                                duration: Theme.motionFast
                            }
                        }
                    }

                    RowLayout {
                        anchors.fill: parent
                        anchors.bottomMargin: 4
                        spacing: 8

                        Rectangle {
                            implicitWidth: 20
                            implicitHeight: 20
                            radius: 10
                            color: stepSegment.active ? stepSegment.activeColor : stepSegment.done ? Theme.accentMuted : "transparent"
                            border.width: 1
                            border.color: stepSegment.active ? stepSegment.activeColor : (Theme.eorzea ? Theme.gold3 : Theme.neutral300)

                            Text {
                                anchors.centerIn: parent
                                text: stepSegment.modelData.n
                                color: stepSegment.active ? (Theme.eorzea ? Theme.badgeForeground : "#ffffff") : stepSegment.done ? (Theme.eorzea ? Theme.gold2 : Theme.accent) : Theme.textSecondary
                                font.pixelSize: 11
                                font.bold: true
                            }
                        }

                        Text {
                            text: stepSegment.modelData.label
                            color: stepSegment.active ? Theme.textPrimary : Theme.textSecondary
                            font.pixelSize: Theme.fs(13)
                            font.weight: Font.DemiBold
                        }

                        Text {
                            Layout.fillWidth: true
                            horizontalAlignment: Text.AlignRight
                            text: stepSegment.modelData.value
                            textFormat: Text.PlainText
                            color: Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            elide: Text.ElideRight
                        }
                    }

                    HoverHandler {
                        cursorShape: Qt.PointingHandCursor
                    }
                    TapHandler {
                        onTapped: dialogBody.dialog.goToStep(stepSegment.modelData.n)
                    }
                }
            }
        }
    }

    // ----------------------------------------------------------- 正文 --
    Flickable {
        id: formScroll

        objectName: "wizardBody"

        /// Width kept free on the right so the scroll bar never sits on
        /// top of a field or of the last diff column.
        readonly property real gutter: 12

        Layout.fillWidth: true
        Layout.fillHeight: true
        Layout.leftMargin: 24
        Layout.rightMargin: 24 - gutter
        clip: true
        contentWidth: width
        contentHeight: stepColumn.implicitHeight + 36
        boundsBehavior: Flickable.StopAtBounds
        // AlwaysOn rather than the AsNeeded used by the pages: a dialogBody.dialog
        // body clipped inside its own frame is not obviously scrollable,
        // and the required reason field can sit below the fold.
        ScrollBar.vertical: ScrollBar {
            policy: formScroll.contentHeight > formScroll.height + 0.5 ? ScrollBar.AlwaysOn : ScrollBar.AlwaysOff
        }

        ColumnLayout {
            id: stepColumn

            y: 18
            width: formScroll.width - formScroll.gutter
            spacing: 16
            enabled: !dialogBody.dialog.submitting && !dialogBody.dialog.awaitingRetry

            // ------------------------------------------ 第 1 步：结果 --
            RunWizardResultStep {
                objectName: "wizardStep1"
                enabled: !dialogBody.dialog.recordSaved
                Layout.fillWidth: true
                visible: dialogBody.dialog.currentStep === 1
                opacity: dialogBody.dialog.stepProgress
                transform: WizardSlide {
                    progress: dialogBody.dialog.stepProgress
                    direction: dialogBody.dialog.stepDirection
                }
                wizard: dialogBody.dialog
            }

            // ------------------------------------ 第 2 步：副本与职业 --
            RunWizardDutyStep {
                id: dutyStep

                objectName: "wizardStep2"
                enabled: !dialogBody.dialog.recordSaved
                Layout.fillWidth: true
                visible: dialogBody.dialog.currentStep === 2
                opacity: dialogBody.dialog.stepProgress
                transform: WizardSlide {
                    progress: dialogBody.dialog.stepProgress
                    direction: dialogBody.dialog.stepDirection
                }
                wizard: dialogBody.dialog
            }

            // -------------------------------------- 第 3 步：时间与原因 --
            RunWizardTimeStep {
                objectName: "wizardStep3"
                Layout.fillWidth: true
                visible: dialogBody.dialog.currentStep === 3
                opacity: dialogBody.dialog.stepProgress
                transform: WizardSlide {
                    progress: dialogBody.dialog.stepProgress
                    direction: dialogBody.dialog.stepDirection
                }
                wizard: dialogBody.dialog
            }
        }
    }

    // ------------------------------------------------------- 按钮栏 --
    Rectangle {
        Layout.fillWidth: true
        Layout.preferredHeight: 1
        color: Theme.border
    }

    RowLayout {
        id: actionsRow

        Layout.fillWidth: true
        Layout.leftMargin: 24
        Layout.rightMargin: 24
        Layout.topMargin: 14
        Layout.bottomMargin: 14
        spacing: 10

        Text {
            Layout.fillWidth: true
            text: dialogBody.dialog.recordSaved ? (dialogBody.dialog.reflectionPending ? qsTranslate("EditRunDialog", "记录已保存，请重试心情和心得") : qsTranslate("EditRunDialog", "记录已保存，请重试图片")) : dialogBody.dialog.progressTitle
            color: dialogBody.dialog.progressDelta > 0 ? Theme.green : Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            font.weight: Font.DemiBold
            elide: Text.ElideRight
        }

        AppButton {
            text: dialogBody.dialog.recordSaved ? qsTranslate("EditRunDialog", "关闭") : qsTranslate("EditRunDialog", "取消")
            enabled: !dialogBody.dialog.submitting
            onClicked: dialogBody.dialog.closeEditor()
        }

        AppButton {
            objectName: "prevStepButton"
            visible: dialogBody.dialog.currentStep > 1
            text: qsTranslate("EditRunDialog", "上一步")
            enabled: !dialogBody.dialog.submitting && !dialogBody.dialog.recordSaved && !dialogBody.dialog.awaitingRetry
            onClicked: dialogBody.dialog.prevStep()
        }

        AppButton {
            objectName: "nextStepButton"
            visible: dialogBody.dialog.currentStep < 3
            variant: "primary"
            text: qsTranslate("EditRunDialog", "下一步")
            enabled: !dialogBody.dialog.submitting && !dialogBody.dialog.recordSaved
            onClicked: dialogBody.dialog.nextStep()
        }

        AppButton {
            variant: "primary"
            objectName: "saveRunButton"
            visible: dialogBody.dialog.currentStep === 3
            enabled: !dialogBody.dialog.submitting && (dialogBody.dialog.recordSaved || dialogBody.dialog.awaitingRetry || !dialogBody.dialog.editMode || dialogBody.dialog.diffRows.length > 0 || dialogBody.dialog.imagesDirty || dialogBody.dialog.confirmsImportedFacts)
            text: dialogBody.submitLabel
            onClicked: dialogBody.dialog.submit()
        }
    }
}
