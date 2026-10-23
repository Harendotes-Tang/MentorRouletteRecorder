import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

// `.ov-tip`: the explanation of a module, a bar or a button, shown while the
// pointer rests on it. With 文字标签 off these are the only words the float
// has, so they say what the graphic stands for and the numbers behind it.
ToolTip {
    id: tip

    delay: 350
    timeout: -1
    margins: 4
    padding: 0
    // A window of its own (Qt 6.8+). The float is only as tall as its content,
    // the mini bar a single line, so a tip kept inside the float's window could
    // only lie over the very thing it explains, buttons included (2026-10-20).
    // Outside, it is the screen edge that pushes the tip back in.
    popupType: Popup.Window
    // The item the tip keeps clear of and centres on: its own parent, or, in the
    // mini bar, the whole bar, so the tip sits above the line and not on its edge.
    property Item clearOf: parent
    readonly property int gap: 6

    // Centred on the item, above it; below it when the screen leaves no room
    // above (the bar docked at the top edge), where the screen edge would
    // otherwise push the tip down onto the item. Decided each time the tip
    // opens, since the window may have moved in between.
    function place() {
        if (!clearOf || !parent) return
        const origin = clearOf.mapToItem(parent, 0, 0)
        x = Math.round(origin.x + (clearOf.width - implicitWidth) / 2)
        const above = Math.round(origin.y) - implicitHeight - gap
        y = roomAbove() >= implicitHeight + gap ? above : Math.round(origin.y + clearOf.height) + gap
    }
    // Pixels between the top of the screen the item is on and the item; -1 when unknown.
    function roomAbove() {
        const top = clearOf.mapToGlobal(0, 0)
        const screens = Application.screens
        for (let i = 0; i < screens.length; ++i) {
            const s = screens[i]
            if (top.x >= s.virtualX && top.x < s.virtualX + s.width
                    && top.y >= s.virtualY && top.y < s.virtualY + s.height)
                return top.y - s.virtualY
        }
        return -1
    }
    onAboutToShow: place()
    // A tooltip wraps at 250 px, like the design's max-width; the box is no
    // wider than the wrapped text.
    contentWidth: Math.min(label.implicitWidth, 250)

    contentItem: Text {
        id: label
        text: tip.text
        // Duty names and categories may come from an imported file: shown as
        // written, never as markup (which could fetch an image).
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
