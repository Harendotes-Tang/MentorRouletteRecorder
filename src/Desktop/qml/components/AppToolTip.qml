import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

// The application's own tool tip: the themed bubble of the float (`.ov-tip`) and
// the charts (ChartTip) - surface fill, a 1 px border, small corners, 12 px medium
// text in both themes - instead of Qt's square, black-bordered default. Drawn in
// the item's window, centred above it; OverlayToolTip gives it a window of its own.
ToolTip {
    id: tip

    // Longer text wraps here; the box is no wider than the wrapped text.
    property int maxTextWidth: 360

    padding: 0
    contentWidth: Math.min(label.implicitWidth, maxTextWidth)

    contentItem: Text {
        id: label
        text: tip.text
        // Duty names and site names may come from data: shown as written, never
        // as markup (which could fetch an image).
        textFormat: Text.PlainText
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(12)
        font.weight: Font.Medium
        lineHeight: 1.35
        wrapMode: Text.Wrap
        padding: 8
        leftPadding: 10
        rightPadding: 10
    }

    background: Rectangle {
        radius: Theme.radiusS
        color: Theme.surface
        border.width: 1
        border.color: Theme.border
    }

    enter: Transition {
        NumberAnimation { property: "opacity"; from: 0; to: 1; duration: Theme.motionFast }
    }
    exit: Transition {
        NumberAnimation { property: "opacity"; from: 1; to: 0; duration: Theme.motionFast }
    }
}
