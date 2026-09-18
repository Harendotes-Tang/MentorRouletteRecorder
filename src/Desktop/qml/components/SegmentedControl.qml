import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// `.seg` / `.seg-opt`: an inset track of radio-like options. Classic uses the
// workbench fill track with 2 px padding; the chosen option is a surface chip
// (dark: fill-2) with a divider ring and accent text at 600.
Rectangle {
    id: root

    property var options: []
    property var currentValue

    signal activated(var value)
    /// Option that emitted the last activated(); the theme reveal centres on it.
    property Item activatedOption: null

    // Qt Basic's own horizontal padding (padding 6 + 2); the chosen / resting
    // options move off it by +3 / -2 px.
    readonly property real optionPadding: 8
    readonly property bool hasSelection: {
        for (let index = 0; index < root.options.length; ++index) {
            if (root.options[index].value === root.currentValue)
                return true
        }
        return false
    }

    implicitHeight: Theme.eorzea ? 30 : 28
    radius: Theme.eorzea ? Theme.radiusM : Theme.radiusS
    color: Theme.eorzea
           ? (Theme.dark ? "#47000000" : "#66ffffff")
           : Theme.fill
    border.width: Theme.eorzea ? 1 : 0
    border.color: Theme.border

    RowLayout {
        anchors.fill: parent
        anchors.margins: 2
        spacing: 2

        Repeater {
            model: root.options

            delegate: Button {
                id: option

                required property var modelData

                readonly property bool current: modelData.value === root.currentValue
                // `.seg:has(input:checked)`: the chosen option grows, the others
                // shrink and dim until the pointer is over them.
                readonly property bool resting: root.hasSelection && !option.current

                Layout.fillWidth: true
                Layout.fillHeight: true
                text: modelData.label || ""
                focusPolicy: Qt.TabFocus
                hoverEnabled: true
                leftPadding: root.optionPadding + option.paddingDelta
                rightPadding: root.optionPadding + option.paddingDelta
                property real paddingDelta: option.current ? 3 : (option.resting ? -2 : 0)
                scale: {
                    if (option.down)
                        return 0.92
                    if (option.current)
                        return 1.05
                    if (option.resting)
                        return option.hovered ? 0.97 : 0.94
                    return 1.0
                }
                opacity: option.resting && !option.hovered ? 0.75 : 1.0

                Behavior on scale { NumberAnimation { duration: Theme.motionMedium; easing.type: Easing.Bezier; easing.bezierCurve: Theme.curveStandard } }
                Behavior on opacity { NumberAnimation { duration: Theme.motionMedium; easing.type: Easing.Bezier; easing.bezierCurve: Theme.curveStandard } }
                Behavior on paddingDelta { NumberAnimation { duration: Theme.motionMedium; easing.type: Easing.Bezier; easing.bezierCurve: Theme.curveStandard } }

                background: Rectangle {
                    // Classic chip plate. An unchosen option holds it at zero
                    // alpha so the transition animates alpha only (Theme.clear).
                    readonly property color plate: Theme.dark ? Theme.fillStrong : Theme.surface

                    radius: Theme.eorzea ? Theme.radiusS : Theme.radiusXs
                    color: option.current && !Theme.eorzea ? plate : Theme.clear(plate)
                    Behavior on color { ColorAnimation { duration: Theme.motionMedium } }
                    border.width: option.current ? 1 : 0
                    border.color: Theme.eorzea ? Theme.tabActiveBorder : Theme.border

                    // The game's chosen tab: a dark plate (Theme.tabActive*).
                    Rectangle {
                        anchors.fill: parent
                        anchors.margins: 1
                        radius: Math.max(0, parent.radius - 1)
                        visible: Theme.eorzea && option.current
                        gradient: Gradient {
                            GradientStop { position: 0.0; color: Theme.tabActiveTop }
                            GradientStop { position: 1.0; color: Theme.tabActiveBottom }
                        }
                    }
                }

                contentItem: Text {
                    text: option.text
                    color: {
                        if (!option.current)
                            return Theme.textSecondary
                        if (!Theme.eorzea)
                            return Theme.accent
                        return Theme.tabActiveText
                    }
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment: Text.AlignVCenter
                    font.pixelSize: Theme.eorzea ? 12 : 13
                    font.weight: option.current
                                 ? (Theme.eorzea ? Font.Bold : Font.DemiBold)
                                 : Font.Normal
                }

                onClicked: {
                    root.activatedOption = option
                    root.activated(modelData.value)
                }
            }
        }
    }
}
