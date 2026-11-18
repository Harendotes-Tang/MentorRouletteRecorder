import QtQuick
import MentorRecorder 1.0

OverlayIconButton {
    objectName: "floatingDutyGuide"
    iconName: "guide"
    focusPolicy: Qt.NoFocus
    tip: guide.text
    action: DutyGuideAction {
        id: guide
        run: Runtime.app.currentGuideRun || ({})
    }
}
