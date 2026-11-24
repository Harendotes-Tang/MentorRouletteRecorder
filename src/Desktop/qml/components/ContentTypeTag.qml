import QtQuick
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// The kind of a community entry: a macro in the skin's blue, a reminder in its yellow.
// The icon and the word carry the difference as well as the colour.
Rectangle {
    id: root

    /// "macro" | "reminder"
    property string type: "macro"
    /// The compact list keeps the icon alone; the word stays the accessible name.
    property bool iconOnly: false
    readonly property bool macro: type === "macro"
    readonly property color base: macro ? Theme.blue
                                        : (Theme.eorzea ? Theme.yellow : Theme.orangeText)
    readonly property color ink: Qt.rgba(base.r * 0.7 + Theme.textPrimary.r * 0.3,
                                         base.g * 0.7 + Theme.textPrimary.g * 0.3,
                                         base.b * 0.7 + Theme.textPrimary.b * 0.3, 1)
    readonly property string label: macro ? qsTr("宏") : qsTr("提醒")

    implicitHeight: 20
    implicitWidth: iconOnly ? 22 : content.implicitWidth + 14
    radius: Theme.eorzea ? Theme.radiusS : height / 2
    color: Qt.rgba(base.r, base.g, base.b, 0.14)
    border.width: Theme.eorzea ? 1 : 0
    border.color: Qt.rgba(base.r, base.g, base.b, 0.42)
    Accessible.role: Accessible.StaticText
    Accessible.name: label

    Row {
        id: content
        anchors.centerIn: parent
        spacing: 4
        Image {
            width: 12
            height: 12
            anchors.verticalCenter: parent.verticalCenter
            source: Lucide.source(root.macro ? "clipboard-copy" : "bell", root.ink)
            sourceSize: Qt.size(24, 24)
        }
        Text {
            renderType: Theme.bodyRenderType
            visible: !root.iconOnly
            text: root.label
            color: root.ink
            font.pixelSize: Theme.fs(11)
            font.weight: Font.DemiBold
        }
    }
}
