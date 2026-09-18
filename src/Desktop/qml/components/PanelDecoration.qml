import QtQuick
import MentorRecorder

// `.panel::before` / `.panel::after` of ff14.css: two gilded corner ticks plus
// the 1 px inner highlight of `--shadow-sm`. Harendotes drops the ticks for a
// frame that runs flame -> old gold -> nothing from the top-left corner to the
// bottom-right one, over the panel's own divider-coloured border. Anchor it
// over any panel-like surface; it draws nothing at all in the classic style.
Item {
    id: root

    property int tickSize: 10
    property real tickWidth: 1.5
    // Follows the host's corners so the glowing frame sits on its border.
    readonly property real cornerRadius: parent && parent.radius !== undefined
                                         ? parent.radius : Theme.radiusM

    anchors.fill: parent
    visible: Theme.eorzea
    z: 2

    // inset 0 1px 0 rgba(255,235,190,.06). Not in harendotes: a second line under
    // its frame's top edge would double it, and the tint is a warm one.
    Rectangle {
        visible: !Theme.harendotes
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.margins: 1
        height: 1
        color: Theme.dark ? "#0fffebbe" : "#b3ffffff"
    }

    // ---------------------------------------------------- harendotes frame --
    // A diagonal gradient along a 1 px ring. With t = (x / w + y / h) / 2 the top
    // and left edges cover t = 0 .. 0.5 and the right and bottom ones 0.5 .. 1, so
    // four straight gradients make the diagonal one exactly, without a Canvas or
    // Qt Quick Shapes. The tail is transparent: the panel's own border shows there
    // and the panel never loses its edge.
    //   t = 0     flame, opaque
    //   t = 0.22  old gold, 75 %
    //   t = 0.5   old gold, 30 %
    //   t = 0.8   nothing
    readonly property color frameStart: Theme.accent
    readonly property color frameMid: Qt.rgba(Theme.oldGold.r, Theme.oldGold.g, Theme.oldGold.b, 0.75)
    readonly property color frameHalf: Qt.rgba(Theme.oldGold.r, Theme.oldGold.g, Theme.oldGold.b, 0.30)
    readonly property color frameEnd: Theme.clear(Theme.oldGold)

    Item {
        id: frame

        anchors.fill: parent
        visible: Theme.harendotes

        // top: t 0 -> 0.5, left to right
        Rectangle {
            x: root.cornerRadius
            width: Math.max(0, frame.width - 2 * root.cornerRadius)
            height: 1
            gradient: Gradient {
                orientation: Gradient.Horizontal
                GradientStop { position: 0.0; color: root.frameStart }
                GradientStop { position: 0.44; color: root.frameMid }
                GradientStop { position: 1.0; color: root.frameHalf }
            }
        }

        // left: t 0 -> 0.5, top to bottom
        Rectangle {
            y: root.cornerRadius
            width: 1
            height: Math.max(0, frame.height - 2 * root.cornerRadius)
            gradient: Gradient {
                GradientStop { position: 0.0; color: root.frameStart }
                GradientStop { position: 0.44; color: root.frameMid }
                GradientStop { position: 1.0; color: root.frameHalf }
            }
        }

        // right: t 0.5 -> 1, top to bottom
        Rectangle {
            x: frame.width - 1
            y: root.cornerRadius
            width: 1
            height: Math.max(0, frame.height - 2 * root.cornerRadius)
            gradient: Gradient {
                GradientStop { position: 0.0; color: root.frameHalf }
                GradientStop { position: 0.6; color: root.frameEnd }
                GradientStop { position: 1.0; color: root.frameEnd }
            }
        }

        // bottom: t 0.5 -> 1, left to right
        Rectangle {
            x: root.cornerRadius
            y: frame.height - 1
            width: Math.max(0, frame.width - 2 * root.cornerRadius)
            height: 1
            gradient: Gradient {
                orientation: Gradient.Horizontal
                GradientStop { position: 0.0; color: root.frameHalf }
                GradientStop { position: 0.6; color: root.frameEnd }
                GradientStop { position: 1.0; color: root.frameEnd }
            }
        }

        // The three lit corners: a quarter of a 1 px circle each, in the colour the
        // gradient has there. The bottom-right one is transparent and not drawn.
        Repeater {
            model: [
                { right: false, bottom: false, start: true },
                { right: true, bottom: false, start: false },
                { right: false, bottom: true, start: false }
            ]

            delegate: Item {
                required property var modelData

                x: modelData.right ? frame.width - root.cornerRadius : 0
                y: modelData.bottom ? frame.height - root.cornerRadius : 0
                width: root.cornerRadius
                height: root.cornerRadius
                clip: true
                visible: root.cornerRadius > 0

                Rectangle {
                    x: parent.modelData.right ? -root.cornerRadius : 0
                    y: parent.modelData.bottom ? -root.cornerRadius : 0
                    width: 2 * root.cornerRadius
                    height: 2 * root.cornerRadius
                    radius: root.cornerRadius
                    color: "transparent"
                    border.width: 1
                    border.color: parent.modelData.start ? root.frameStart : root.frameHalf
                }
            }
        }
    }

    Repeater {
        model: [
            { x: 0, y: 0, w: root.tickSize, h: root.tickWidth },
            { x: 0, y: 0, w: root.tickWidth, h: root.tickSize }
        ]

        delegate: Rectangle {
            required property var modelData

            visible: !Theme.harendotes
            x: modelData.x
            y: modelData.y
            width: modelData.w
            height: modelData.h
            color: Theme.gold
            opacity: 0.85
        }
    }

    Repeater {
        model: [
            { w: root.tickSize, h: root.tickWidth },
            { w: root.tickWidth, h: root.tickSize }
        ]

        delegate: Rectangle {
            required property var modelData

            visible: !Theme.harendotes
            x: root.width - modelData.w
            y: root.height - modelData.h
            width: modelData.w
            height: modelData.h
            color: Theme.gold
            opacity: 0.85
        }
    }
}
