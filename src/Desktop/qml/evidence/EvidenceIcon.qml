import QtQuick
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// A Lucide stroke icon drawn in a colour, at the screen's pixel density.
Image {
    id: icon

    property string name: ""
    property color color: Theme.textSecondary
    property int size: 14
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1

    visible: name.length > 0 && Lucide.has(name)
    width: size
    height: size
    sourceSize: Qt.size(Math.round(size * pixelRatio), Math.round(size * pixelRatio))
    source: visible ? Lucide.source(name, color) : ""
    fillMode: Image.PreserveAspectFit
    smooth: true
}
