import QtQuick
import MentorRecorder 1.0

// 生成分享图片 in a window of its own, so it can be resized and maximised while the
// order of a long selection is arranged (owner, 2026-11-19). The dialog inside is
// the same component the tests drive on its own; here it fills the window.
Window {
    id: host
    objectName: "reflectionShareWindow"

    readonly property alias dialog: content

    title: qsTr("生成分享图片")
    flags: Qt.Window | Qt.WindowTitleHint | Qt.WindowSystemMenuHint
           | Qt.WindowMinMaxButtonsHint | Qt.WindowCloseButtonHint
    modality: Qt.WindowModal
    color: Theme.surface
    width: 860
    height: Math.max(minimumHeight, Math.min(900, Screen.desktopAvailableHeight - 80))
    minimumWidth: 600
    minimumHeight: 480

    function openForRun(run) { openForRuns([run]) }
    function openForRuns(runs) {
        if (content.visible) { requestActivate(); return }
        // The popup needs a shown window to open in.
        show()
        content.openForRuns(runs)
        if (!content.visible) { hide(); return }
        requestActivate()
    }

    // The title bar's close button: not while a save is under way.
    onClosing: function(close) {
        close.accepted = !content.busy
        if (close.accepted) content.close()
    }

    Item {
        anchors.fill: parent
        ReflectionShareDialog {
            id: content
            windowed: true
            x: 0
            y: 0
            onClosed: host.hide()
        }
    }
}
