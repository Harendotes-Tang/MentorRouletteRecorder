import QtQuick
import MentorRecorder 1.0
import "OverlayIcons.js" as Icons

// One of the float's own line icons (OverlayIcons.js), drawn in `color` at
// device pixels so the strokes stay crisp on a scaled display.
Image {
    id: root

    property string name: ""
    property color color: Theme.textSecondary
    property real size: 14

    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1

    width: size
    height: size
    sourceSize: Qt.size(Math.round(size * pixelRatio), Math.round(size * pixelRatio))
    source: name.length > 0 ? Icons.source(name, color) : ""
    fillMode: Image.PreserveAspectFit
    smooth: true
}
