import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

Card {
    id: root

    property string title: ""
    property string value: ""

    implicitHeight: 116
    spacing: 8

    // A translated title that does not fit a narrow window's card keeps its
    // ellipsis and shows in full on hover.
    Text {
        id: titleText
        Layout.fillWidth: true
        text: root.title
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        elide: Text.ElideRight

        HoverHandler { id: titleHover }
        ToolTip.visible: titleHover.hovered && titleText.truncated
        ToolTip.text: root.title
    }

    Text {
        id: valueText
        Layout.fillWidth: true
        text: root.value
        // A changed value fades once instead of snapping.
        onTextChanged: if (Theme.motion) pulse.restart()
        SequentialAnimation {
            id: pulse
            NumberAnimation { target: valueText; property: "opacity"; to: 0.35; duration: Theme.motionFast }
            NumberAnimation { target: valueText; property: "opacity"; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
        }
        color: Theme.eorzea ? Theme.headingColor : Theme.textPrimary
        font.family: Theme.numFamily
        font.pixelSize: Theme.fs(26)
        font.weight: Theme.figureWeight(true)
        font.features: ({ "tnum": 1 })
        elide: Text.ElideRight
    }
}
