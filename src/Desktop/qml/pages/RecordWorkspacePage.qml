pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// Real page content: the controllers remain alive without opening their Popups.
Pane {
    id: page
    objectName: "recordWorkspacePage"
    required property var manualEditor
    required property var reflectionEditor
    readonly property alias manualContent: manualBody
    readonly property alias reflectionContent: reflectionBody
    signal backRequested

    padding: 0
    font.family: Theme.bodyFamily
    background: null
    contentItem: ColumnLayout {
        spacing: 14
        AppButton {
            objectName: "recordWorkspaceBackButton"
            text: qsTr("返回")
            overlayIcon: "chevron-left"
            variant: "ghost"
            enabled: !page.manualEditor.submitting && !page.reflectionEditor.busy
            onClicked: page.backRequested()
        }
        EditRunContent {
            id: manualBody
            objectName: "manualRecordPageContent"
            Layout.fillWidth: true
            Layout.fillHeight: true
            dialog: page.manualEditor
            visible: page.manualEditor.inlineActive
        }
        ReflectionEditorContent {
            id: reflectionBody
            objectName: "pendingRecordPageContent"
            Layout.fillWidth: true
            Layout.fillHeight: true
            dialog: page.reflectionEditor
            visible: page.reflectionEditor.inlineActive
        }
    }
}
