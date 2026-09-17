import QtQuick
import MentorRecorder

Item {
    id: root

    property variant jobId
    property real size: 32
    // The game's own framed job icon; the plain glyph on request.
    property bool framed: true

    width: size
    height: size

    // Only behind the lettered fallback badge: the glyph carries no plate.
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
        source: framed ? Jobs.framedIcon(root.jobId) : Jobs.plainIcon(root.jobId)
        fillMode: Image.PreserveAspectFit
        // Distributed builds ship no game art: the badge below is the normal look.
        visible: source !== "" && status === Image.Ready
    }

    Text {
        anchors.centerIn: parent
        text: Jobs.abbreviation(root.jobId)
        color: Theme.eorzea ? Theme.gold2 : Theme.textPrimary
        font.pixelSize: 10
        font.bold: true
        visible: !image.visible
    }
}
