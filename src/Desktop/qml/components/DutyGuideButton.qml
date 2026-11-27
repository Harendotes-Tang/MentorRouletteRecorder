import QtQuick
import MentorRecorder 1.0

AppButton {
    id: root
    property var run: ({})
    property alias showSource: guide.showSource
    focusPolicy: Qt.NoFocus
    iconName: "book-open-text"
    trailingIconName: "external-link"
    // The site's name, in the application's bubble (dashboardDutyGuideTip on the dashboard).
    tip: guide.tip
    action: DutyGuideAction {
        id: guide
        run: root.run
    }
}
