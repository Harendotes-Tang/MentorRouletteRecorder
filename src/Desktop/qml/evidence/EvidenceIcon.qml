import QtQuick
import QtQuick.Layouts
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
    // A layout sizes its items by their implicit size, and an Image's (read-only)
    // is the raster's size in device pixels: without this, a 15 px icon on a
    // 150 % screen was laid out at 23 px and blown up soft.
    Layout.preferredWidth: size
    Layout.preferredHeight: size
    sourceSize: Qt.size(Math.round(size * pixelRatio), Math.round(size * pixelRatio))
    source: visible ? Lucide.source(name, color) : ""
    fillMode: Image.PreserveAspectFit
    smooth: true
}
