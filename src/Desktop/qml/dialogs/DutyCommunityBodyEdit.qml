pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

// 宏内容 / 提醒内容 of the 副本提醒与导随宏 editor (Design artifact: 宏内容带行号，15 行一屏看全).
// A macro keeps each command on its own line, numbered beside the text: room for 15 lines at
// least, the ones past 15 in red (the game takes 15 lines; saving is not stopped). A reminder
// wraps and has no numbers.
Rectangle {
    id: box

    property bool macro: true
    /// Drawn in red: the form found the text missing.
    property bool problem: false
    property alias text: area.text
    property alias placeholderText: area.placeholderText
    readonly property alias area: area
    /// Lines with something in them, trailing empty lines not counted.
    readonly property int lineCount: area.text.trim().length ? area.text.replace(/\s+$/, "").split("\n").length : 0
    readonly property int shownRows: Math.max(15, area.text.split("\n").length)
    // One row of text, taken from the editor itself so the numbers stay level with the lines.
    readonly property real rowHeight: Math.max(1, area.cursorRectangle.height)
    readonly property real gutterWidth: macro ? 44 : 0
    // Until the text is clicked into, an insert goes to the end.
    property bool used: false

    // 插入 (Design: 指令另起一行，占位符插在光标处): a command (/p, /e) starts a line of its own,
    // or takes an empty one; a placeholder (<se.1>, <t>) goes where the cursor is, after a space.
    function insert(token, command) {
        const value = area.text
        let start = used ? area.selectionStart : value.length
        let end = used ? area.selectionEnd : value.length
        let piece
        if (command) {
            const lineStart = value.lastIndexOf("\n", start - 1) + 1
            const next = value.indexOf("\n", start)
            const lineEnd = next < 0 ? value.length : next
            if (value.slice(lineStart, lineEnd).trim() === "") {
                start = lineStart
                end = lineEnd
                piece = token + " "
            } else {
                start = end = lineEnd
                piece = "\n" + token + " "
            }
        } else {
            const before = value.slice(0, start)
            piece = (before && !/\s$/.test(before) ? " " : "") + token
        }
        // remove + insert keep the edit on the undo stack.
        area.remove(start, end)
        area.insert(start, piece)
        used = true
        area.forceActiveFocus()
        area.cursorPosition = start + piece.length
    }

    implicitHeight: 120
    radius: Theme.radiusS
    color: Theme.eorzea ? (Theme.dark ? "#47000000" : "#8cffffff") : Theme.surface
    border.width: 1
    border.color: problem ? Theme.red
                : area.activeFocus ? (Theme.eorzea ? Theme.gold : Theme.accent)
                : (Theme.eorzea ? Theme.border : Theme.neutral300)
    clip: true

    Flickable {
        id: flick
        anchors.fill: parent
        anchors.margins: 1
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        contentWidth: box.macro ? Math.max(width, box.gutterWidth + area.implicitWidth) : width
        contentHeight: Math.max(height, area.height)
        ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
        ScrollBar.horizontal: ScrollBar { policy: ScrollBar.AsNeeded }

        // Keeps the line being typed in view: the editor is not the view's own content.
        function reveal(rect) {
            const x = rect.x + area.x
            if (rect.y < contentY)
                contentY = rect.y
            else if (rect.y + rect.height > contentY + height)
                contentY = rect.y + rect.height - height
            if (x < contentX + box.gutterWidth)
                contentX = Math.max(0, x - box.gutterWidth - 12)
            else if (x + 12 > contentX + width)
                contentX = x + 12 - width
        }

        Column {
            id: gutter
            visible: box.macro
            x: 0
            width: box.gutterWidth
            topPadding: area.topPadding
            Repeater {
                model: box.macro ? box.shownRows : 0
                delegate: Text {
                    required property int index
                    width: gutter.width - 10
                    height: box.rowHeight
                    horizontalAlignment: Text.AlignRight
                    verticalAlignment: Text.AlignVCenter
                    text: index + 1
                    color: index >= 15 ? Theme.red : Theme.textSecondary
                    opacity: index >= 15 ? 1 : index < box.lineCount ? 0.85 : 0.38
                    font.family: Theme.monoFamily
                    font.pixelSize: Theme.fs(12)
                    font.weight: index >= 15 ? Font.DemiBold : Font.Normal
                }
            }
        }
        Rectangle {
            visible: box.macro
            x: box.gutterWidth - 1
            width: 1
            height: flick.contentHeight
            color: Theme.border
        }

        TextArea {
            id: area
            objectName: "dutyCommunityEditorBody"
            x: box.gutterWidth
            width: box.macro ? Math.max(flick.width - box.gutterWidth, implicitWidth) : flick.width
            // 15 rows at least for a macro, so a whole macro is in view at once.
            height: Math.max(flick.height, implicitHeight,
                             box.macro ? box.shownRows * box.rowHeight + topPadding + bottomPadding : 0)
            renderType: Theme.bodyRenderType
            textFormat: TextEdit.PlainText
            wrapMode: box.macro ? TextEdit.NoWrap : TextEdit.Wrap
            selectByMouse: true
            color: Theme.textPrimary
            placeholderTextColor: Theme.textMuted
            font.family: box.macro ? Theme.monoFamily : Theme.bodyFamily
            font.pixelSize: Theme.fs(13)
            leftPadding: 12
            rightPadding: 12
            topPadding: 10
            bottomPadding: 10
            background: null
            onActiveFocusChanged: if (activeFocus) box.used = true
            onCursorRectangleChanged: if (activeFocus) flick.reveal(cursorRectangle)
        }
    }
}
