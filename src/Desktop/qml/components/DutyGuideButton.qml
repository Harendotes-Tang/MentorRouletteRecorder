import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

AppButton {
    id: root
    property var run: ({})
    property alias showSource: guide.showSource
    focusPolicy: Qt.NoFocus
    iconName: "book-open-text"
    ToolTip.visible: hovered
    ToolTip.text: guide.tip
    action: DutyGuideAction {
        id: guide
        run: root.run
    }
}
