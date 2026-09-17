import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// "UI 缩放 [====o----] 125%" - the prototype's <input type="range"> row.
RowLayout {
    id: root

    property string label: ""
    property int from: 0
    property int to: 100
    property int stepSize: 1
    property int value: 0
    property string suffix: ""

    signal moved(int value)

    Layout.fillWidth: true
    spacing: 12

    // Hidden when empty: an empty label still consumes a `spacing` of its own
    // and pushes the value 12 px past a row sized for slider + value.
    Text {
        Layout.fillWidth: true
        visible: root.label.length > 0
        text: root.label
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(13)
        font.bold: true
        elide: Text.ElideRight
    }

    Slider {
        id: slider

        Layout.preferredWidth: 160
        from: root.from
        to: root.to
        stepSize: root.stepSize
        snapMode: Slider.SnapAlways
        focusPolicy: Qt.TabFocus

        // Dragging writes Slider.value, which would destroy an inline
        // `value: root.value` binding and leave the handle out of sync with a
        // clamped, rejected or externally changed setting. Same pattern as
        // CapturePage's adapter combo box.
        Binding {
            target: slider
            property: "value"
            value: root.value
            restoreMode: Binding.RestoreNone
        }

        background: Rectangle {
            x: slider.leftPadding
            y: slider.topPadding + slider.availableHeight / 2 - height / 2
            width: slider.availableWidth
            height: 4
            radius: 2
            color: Theme.fillStrong

            Rectangle {
                width: slider.visualPosition * parent.width
                height: parent.height
                radius: 2
                color: Theme.accent
            }
        }

        handle: Rectangle {
            x: slider.leftPadding + slider.visualPosition * (slider.availableWidth - width)
            y: slider.topPadding + slider.availableHeight / 2 - height / 2
            width: 16
            height: 16
            radius: 8
            color: Theme.accent
            border.width: 2
            border.color: Theme.surface
        }

        onMoved: root.moved(Math.round(value))
    }

    Text {
        Layout.preferredWidth: 44
        text: String(root.value) + root.suffix
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(13)
        font.family: Theme.figureFamily
        font.weight: Theme.figureWeight(false)
        font.features: ({ "tnum": 1 })
        horizontalAlignment: Text.AlignRight
    }
}
