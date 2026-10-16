import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Dialog {
    id: dialog
    objectName: "calibrationEvidenceDialog"
    property var evidenceController: null
    modal: true
    anchors.centerIn: Overlay.overlay
    width: Math.min(800, (parent ? parent.width : 848) - 48)
    height: Math.min(650, (parent ? parent.height : 698) - 48)
    padding: 24
    closePolicy: Popup.CloseOnEscape
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    onOpened: {
        evidenceScroll.contentY = 0
        cancelButton.forceActiveFocus(Qt.PopupFocusReason)
    }
    onClosed: if (evidenceController && evidenceController.visible) evidenceController.dismiss()

    Connections {
        target: dialog.evidenceController
        function onChanged() {
            if (dialog.evidenceController.visible && !dialog.opened) dialog.open()
            else if (!dialog.evidenceController.visible && dialog.opened) dialog.close()
        }
    }

    contentItem: ColumnLayout {
        spacing: 12
        HeadingLabel {
            Layout.fillWidth: true
            text: dialog.evidenceController && dialog.evidenceController.kind === "TEMPLATE_OBSERVATION"
                  ? qsTr("预览新区服候选摘要") : qsTr("预览校准证据摘要")
            font.pixelSize: Theme.dialogTitleSize(22)
        }
        Label {
            objectName: "evidencePrivacyNotice"
            Layout.fillWidth: true
            text: qsTr("下方是将公开的完整 JSON。仅包含区服、版本、随机编号及判据或报文元数据，不含原始报文、角色、IP、设备标识和精确游戏时间线。")
            color: Theme.textPrimary
            textFormat: Text.PlainText
            wrapMode: Text.WordWrap
        }
        Label {
            Layout.fillWidth: true
            text: qsTr("提交后，你的 GitHub 账号、Issue 时间和摘要会公开，编辑或删除也可能留下历史及副本。预览和取消不会上传、复制或打开浏览器。")
            color: Theme.orangeText
            textFormat: Text.PlainText
            wrapMode: Text.WordWrap
        }
        RowLayout {
            Layout.fillWidth: true
            visible: !!dialog.evidenceController && dialog.evidenceController.kind === "CODE_VERIFICATION"
                     && dialog.evidenceController.candidateChoices.length > 0
            FieldLabel { text: qsTr("校准候选") }
            StyledComboBox {
                id: candidateSelector
                objectName: "evidenceCandidateSelector"
                Layout.fillWidth: true
                model: dialog.evidenceController ? dialog.evidenceController.candidateChoices : []
                textRole: "label"
                valueRole: "code_sha256"
                Binding {
                    target: candidateSelector
                    property: "currentIndex"
                    value: {
                        if (!dialog.evidenceController) return -1
                        for (let i = 0; i < candidateSelector.model.length; ++i)
                            if (candidateSelector.model[i].code_sha256 === dialog.evidenceController.selectedCode) return i
                        return -1
                    }
                    restoreMode: Binding.RestoreNone
                }
                displayText: currentIndex < 0 ? qsTr("请选择要预览的候选") : currentText
                onActivated: function(index) {
                    dialog.evidenceController.previewCalibration(model[index].code_sha256)
                }
                Accessible.name: qsTr("选择校准证据候选")
            }
        }
        Label {
            objectName: "evidenceStatus"
            Layout.fillWidth: true
            visible: !!dialog.evidenceController && (dialog.evidenceController.busy || dialog.evidenceController.reason.length > 0)
            text: dialog.evidenceController ? (dialog.evidenceController.busy ? qsTr("正在读取本机摘要…") : dialog.evidenceController.reason) : ""
            color: Theme.orangeText
            textFormat: Text.PlainText
            wrapMode: Text.WordWrap
        }
        InsetBox {
            Layout.fillWidth: true
            Layout.fillHeight: true
            Flickable {
                id: evidenceScroll
                objectName: "evidenceScroll"
                anchors.fill: parent
                anchors.margins: 12
                contentWidth: width
                contentHeight: evidenceText.implicitHeight
                clip: true
                boundsBehavior: Flickable.StopAtBounds
                ScrollBar.vertical: ScrollBar {}
                TextEdit {
                    id: evidenceText
                    objectName: "evidenceJsonText"
                    width: evidenceScroll.width - Theme.scrollGutter
                    text: dialog.evidenceController ? dialog.evidenceController.evidenceJson : ""
                    textFormat: TextEdit.PlainText
                    readOnly: true
                    // Copy is a dedicated explicit action after the privacy notice.
                    selectByMouse: false
                    activeFocusOnTab: true
                    wrapMode: TextEdit.WrapAnywhere
                    color: Theme.textPrimary
                    font.family: Theme.monoFamily
                    font.pixelSize: Theme.fs(12)
                    Accessible.name: qsTr("完整公开证据 JSON")
                }
            }
        }
        Label {
            objectName: "evidencePasteHint"
            Layout.fillWidth: true
            visible: !!dialog.evidenceController && dialog.evidenceController.canSubmit && !dialog.evidenceController.evidenceInUrl
            text: qsTr("摘要较长，提交页不会预填。下一步会复制完整内容，请粘贴到 GitHub 的证据摘要字段；内容不会被截断。")
            color: Theme.textSecondary
            textFormat: Text.PlainText
            wrapMode: Text.WordWrap
        }
        Flow {
            Layout.fillWidth: true
            layoutDirection: Qt.RightToLeft
            spacing: 10
            AppButton {
                id: submitButton
                objectName: "evidenceCopyOpenButton"
                text: qsTr("复制摘要并打开提交页")
                variant: "primary"
                enabled: !!dialog.evidenceController && dialog.evidenceController.canSubmit
                onClicked: dialog.evidenceController.copyAndOpen()
            }
            AppButton {
                id: cancelButton
                objectName: "evidenceCancelButton"
                text: qsTr("取消")
                onClicked: dialog.close()
            }
        }
    }
}
