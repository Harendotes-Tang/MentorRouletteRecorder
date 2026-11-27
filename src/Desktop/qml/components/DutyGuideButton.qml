import QtQuick
import MentorRecorder 1.0

AppButton {
    id: root
    property var run: ({})
    property alias showSource: guide.showSource
    focusPolicy: Qt.NoFocus
    iconName: "book-open-text"
    trailingIconName: "external-link"
    action: DutyGuideAction {
        id: guide
        run: root.run
    }
    // The application's themed tip, not Qt's square default (2026-11-27); named
    // after the button, e.g. dashboardDutyGuideTip.
    AppToolTip {
        objectName: root.objectName.length > 0 ? root.objectName + "Tip" : ""
        visible: root.hovered && text.length > 0
        text: guide.tip
    }
}
