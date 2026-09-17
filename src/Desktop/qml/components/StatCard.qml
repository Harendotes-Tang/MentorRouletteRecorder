import QtQuick
import QtQuick.Layouts
import MentorRecorder

Card {
    id: root

    property string title: ""
    property string value: ""

    implicitHeight: 116
    spacing: 8

    Text {
        Layout.fillWidth: true
        text: root.title
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        elide: Text.ElideRight
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
