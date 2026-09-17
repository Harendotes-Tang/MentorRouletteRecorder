import QtQuick
import MentorRecorder

Item {
    id: root

    property string category: ""
    property real size: 28

    width: size
    height: size

    // The framed plate is the fallback for a build without the game art (a
    // lettered badge needs an edge); the icon itself stands alone.
    Rectangle {
        anchors.fill: parent
        visible: !image.visible
        radius: Theme.eorzea ? Theme.radiusXs : width / 2
        color: Theme.eorzea ? Theme.insetBackgroundStrong : Theme.surfaceRaised
        border.width: 1
        border.color: Theme.eorzea ? Theme.insetBorder : Theme.border
    }

    Image {
        id: image
        anchors.fill: parent
        anchors.margins: 1
        source: Jobs.categoryIcon(root.category)
        fillMode: Image.PreserveAspectFit
        visible: source !== "" && status === Image.Ready
    }

    Text {
        anchors.centerIn: parent
        text: root.category.length > 0 ? root.category.substring(0, 1) : qsTr("?")
        color: Theme.eorzea ? Theme.gold2 : Theme.textSecondary
        font.pixelSize: 11
        font.bold: true
        visible: !image.visible
    }
}
