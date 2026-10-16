import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Dialog {
    id: dialog
    objectName: "releaseNotesDialog"
    property var notesController: null

    modal: true
    anchors.centerIn: Overlay.overlay
    width: Math.min(640, (parent ? parent.width : 688) - 48)
    height: Math.min(560, (parent ? parent.height : 608) - 48,
                     titleLabel.implicitHeight + versionLabel.implicitHeight
                     + notesText.implicitHeight + acknowledgeButton.implicitHeight
                     + 48 + topPadding + bottomPadding)
    padding: 24
    closePolicy: Popup.CloseOnEscape
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }

    onOpened: {
        notesView.contentY = 0
        acknowledgeButton.forceActiveFocus(Qt.PopupFocusReason)
    }
    onClosed: {
        if (notesController) notesController.acknowledge()
    }

    contentItem: ColumnLayout {
        spacing: 16
        HeadingLabel {
            id: titleLabel
            Layout.fillWidth: true
            text: qsTr("本次更新")
            font.pixelSize: Theme.dialogTitleSize(24)
        }
        Label {
            id: versionLabel
            objectName: "releaseNotesVersion"
            Layout.fillWidth: true
            text: dialog.notesController ? qsTr("版本 %1").arg(dialog.notesController.version) : ""
            textFormat: Text.PlainText
            color: Theme.textSecondary
        }
        Flickable {
            id: notesView
            objectName: "releaseNotesScroll"
            Layout.fillWidth: true
            Layout.fillHeight: true
            contentWidth: width
            contentHeight: notesText.implicitHeight
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {}
            TextEdit {
                id: notesText
                objectName: "releaseNotesText"
                width: notesView.width - Theme.scrollGutter
                text: dialog.notesController ? dialog.notesController.text : ""
                textFormat: TextEdit.PlainText
                readOnly: true
                selectByMouse: true
                wrapMode: TextEdit.Wrap
                color: Theme.textPrimary
                font.pixelSize: 14
                activeFocusOnTab: true
            }
        }
        AppButton {
            id: acknowledgeButton
            objectName: "releaseNotesAcknowledge"
            Layout.alignment: Qt.AlignRight
            text: qsTr("我知道了")
            variant: "primary"
            onClicked: dialog.close()
        }
    }
}
