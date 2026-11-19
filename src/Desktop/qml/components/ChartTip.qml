import QtQuick
import MentorRecorder 1.0

// The figure behind one bar of a chart: a themed bubble that sits over the bar
// the pointer is on and moves with it, instead of a system tool tip centred on
// the whole chart. Drawn inside the chart, so it needs no window of its own.
Rectangle {
    id: tip

    property string text: ""
    /// Centre of the bar and its top edge, in the parent's coordinates.
    property real barCenter: 0
    property real barTop: 0
    property bool shown: false
    readonly property int gap: 6

    // Kept inside the chart: slid back from either end, and laid over the top of
    // a bar that leaves no room above it.
    x: Math.round(Math.max(0, Math.min((parent ? parent.width : 0) - width, barCenter - width / 2)))
    y: Math.round(Math.max(0, barTop - height - gap))
    z: 10
    width: label.implicitWidth
    height: label.implicitHeight
    radius: Theme.radiusS
    color: Theme.surface
    border.width: 1
    border.color: Theme.border
    visible: opacity > 0
    opacity: shown ? 1 : 0
    Behavior on opacity { NumberAnimation { duration: Theme.motionFast } }

    Text {
        id: label
        text: tip.text
        textFormat: Text.PlainText
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(12)
        font.weight: Font.Medium
        font.features: ({ "tnum": 1 })
        topPadding: 5
        bottomPadding: 5
        leftPadding: 9
        rightPadding: 9
    }
}
