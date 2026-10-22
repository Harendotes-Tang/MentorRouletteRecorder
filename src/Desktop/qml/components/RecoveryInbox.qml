pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

Dialog {
    id: inbox
    objectName: "recoveryInbox"
    title: qsTr("导随记录器 · 待处理")
    width: Math.min(560, parent ? parent.width - 40 : 560)
    height: Math.min(640, parent ? parent.height - 40 : 640)
    padding: 22
    modal: true
    closePolicy: Popup.CloseOnEscape
    background: DialogFrame { }
    Overlay.modal: Rectangle { color: Theme.modalScrim(inbox.palette.shadow) }
    contentItem: ColumnLayout {
        spacing: 12
        ScrollView {
            Layout.fillWidth: true
            Layout.fillHeight: true
            contentWidth: availableWidth
            clip: true
            ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
            PendingWorkContent {
                width: parent.width
                onChosen: inbox.close()
            }
        }
        RowLayout {
            Layout.fillWidth: true
            AppButton { objectName: "refreshPendingWorkButton"; text: qsTr("刷新"); onClicked: Runtime.app.recovery.refresh() }
            Item { Layout.fillWidth: true }
            AppButton { text: qsTr("关闭"); onClicked: inbox.close() }
        }
    }
}
