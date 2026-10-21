import QtQuick
import QtQuick.Controls
import MentorRecorder

// The window of the result popup (dialogs/ResultPopupContent.qml): a
// frameless tool window beside the statistics float - to its left when there
// is room, else to its right, top edges level - or, with the float closed,
// near the screen's top-right corner. It is sized by the editor it hosts.
Window {
    id: reminder
    objectName: "recoveryReminderWindow"
    property var editor: null
    property var overlayWindow: null
    transientParent: null
    visible: false
    color: "transparent"
    title: qsTr("导随记录器 · 本次记录待处理")
    flags: Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint
    width: editor ? editor.width : 320
    height: editor ? Math.min(editor.implicitHeight, Screen.height - 64) : 300
    minimumWidth: 320
    function prepare(manual) {
        const gap = 12
        // The float's screen, when it is up: the popup goes beside it, not onto
        // whichever screen this window was on last time.
        const target = overlayWindow && overlayWindow.visible && overlayWindow.screen ? overlayWindow.screen : reminder.screen
        if (target && reminder.screen !== target) reminder.screen = target
        const screenLeft = target ? target.virtualX : Screen.virtualX
        const screenTop = target ? target.virtualY : Screen.virtualY
        const screenRight = screenLeft + (target ? target.width : Screen.width)
        const screenBottom = screenTop + (target ? target.height : Screen.height)
        // The editor's own width, not its current one: it is sized by this window.
        width = Math.min(editor && editor.dialogWidth ? editor.dialogWidth : 320, Screen.width - 32)
        let targetX
        let targetY
        if (overlayWindow && overlayWindow.visible) {
            targetX = overlayWindow.x - gap - width
            if (targetX < screenLeft + 8) targetX = overlayWindow.x + overlayWindow.width + gap
            targetY = overlayWindow.y
        } else {
            targetX = screenRight - 24 - width
            targetY = screenTop + 176
        }
        x = Math.max(screenLeft + 8, Math.min(screenRight - width - 8, targetX))
        y = Math.max(screenTop + 8, Math.min(screenBottom - height - 8, targetY))
        show()
        if (manual) requestActivate()
    }
    onClosing: function(event) {
        event.accepted = false
        if (editor && !editor.busy) editor.defer()
    }
}
