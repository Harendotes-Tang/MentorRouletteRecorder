import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

Button {
    id: root
    property var run: ({})
    focusPolicy: Qt.NoFocus
    action: DutyGuideAction { run: root.run }
}
