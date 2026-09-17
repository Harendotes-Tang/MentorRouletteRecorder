import QtQuick
import MentorRecorder

Rectangle {
    id: root

    property string message: ""
    // Keep the last text while fading out so the box does not collapse mid-animation.
    property string shownMessage: ""
    onMessageChanged: if (message.length > 0) shownMessage = message

    opacity: message.length > 0 ? 1 : 0
    visible: opacity > 0
    transform: Translate { y: root.message.length > 0 ? 0 : 6; Behavior on y { NumberAnimation { duration: Theme.motionMedium; easing.type: Easing.OutCubic } } }
    Behavior on opacity { NumberAnimation { duration: Theme.motionMedium } }
    radius: Theme.radiusS
    color: Theme.surface
    border.width: 1
    border.color: Theme.eorzea ? Theme.gold3 : Theme.borderStrong
    implicitWidth: Math.min(520, content.implicitWidth + 28)
    implicitHeight: content.implicitHeight + 20

    Text {
        id: content
        anchors.fill: parent
        anchors.margins: 10
        text: root.shownMessage
        color: Theme.textPrimary
        wrapMode: Text.WordWrap
        font.pixelSize: Theme.fs(13)
    }
}
