import QtQuick
import MentorRecorder 1.0

OverlayIconButton {
    objectName: "floatingDutyGuide"
    iconName: "guide"
    focusPolicy: Qt.NoFocus
    // Icon only: the tip is the sentence that says it opens the browser, not the text button's caption.
    tip: guide.tip
    action: DutyGuideAction {
        id: guide
        run: Runtime.app.currentGuideRun || ({})
    }
}
