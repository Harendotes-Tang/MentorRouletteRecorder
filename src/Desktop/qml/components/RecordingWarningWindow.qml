pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// Independent of the main window's native owner/minimised state. Automatic
// show() never asks Windows to activate this window or the main application.
Window {
    id: warning
    objectName: "recordingWarningWindow"

    property var recording: null
    property var anchorWindow: null
    property bool foregroundDialogVisible: false
    // The main window is what the player is looking at: good news is said there, in a toast,
    // not in a window laid over it (owner decision 2026-11-19, audit K1-3).
    property bool mainWindowForeground: false
    // How long the recovery notice stays before it goes by itself. It asks for nothing, so it
    // must not sit over the game until it is clicked away.
    property int recoveryDismissMs: 8000
    // The native presenter can supply the precise work area, including a
    // taskbar on the left/top. The QML fallback stays on this screen.
    property rect availableGeometry: Qt.rect(Screen.virtualX, Screen.virtualY,
        Math.min(Screen.width, Screen.desktopAvailableWidth),
        Math.min(Screen.height, Screen.desktopAvailableHeight))
    readonly property bool warningWanted: !!recording
        && typeof recording.warningActive !== "undefined" && recording.warningActive
        && typeof recording.warningPending !== "undefined" && recording.warningPending
    readonly property bool recoveryWanted: !!recording
        && typeof recording.recoveryPending !== "undefined" && recording.recoveryPending
    signal diagnosisRequested()
    /// The recovery was said in the main window instead of here.
    signal recoveryShownInMainWindow()

    transientParent: null
    visible: false
    modality: Qt.NonModal
    flags: Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.WindowDoesNotAcceptFocus
    title: recoveryWanted ? qsTr("导随记录器 · 自动记录已恢复") : qsTr("导随记录器 · 自动记录暂不可用")
    color: "transparent"
    width: Math.max(300, Math.min(440, availableGeometry.width - 32))
    height: Math.min(warningContent.implicitHeight + 40, availableGeometry.height - 32)

    function acknowledge() {
        if (recording && recording.acknowledgeWarning) recording.acknowledgeWarning()
        hide()
    }

    function placeOnScreen() {
        const bounds = availableGeometry
        x = Math.max(bounds.x + 16, bounds.x + bounds.width - width - 24)
        y = Math.max(bounds.y + 16, bounds.y + Math.min(120, bounds.height - height - 24))
    }

    function showWarning() {
        const target = anchorWindow && anchorWindow.screen ? anchorWindow.screen : warning.screen
        if (target && warning.screen !== target) warning.screen = target
        placeOnScreen()
        show()
    }

    function synchronize() {
        if (recoveryWanted && mainWindowForeground) {
            recoveryShownInMainWindow()
            acknowledge()
        } else if (recoveryWanted || (warningWanted && !foregroundDialogVisible)) {
            if (!visible) showWarning()
        } else {
            hide()
        }
    }

    Timer {
        interval: warning.recoveryDismissMs
        running: warning.visible && warning.recoveryWanted
        onTriggered: warning.acknowledge()
    }

    onWarningWantedChanged: Qt.callLater(synchronize)
    onRecoveryWantedChanged: Qt.callLater(synchronize)
    onForegroundDialogVisibleChanged: Qt.callLater(synchronize)
    onMainWindowForegroundChanged: Qt.callLater(synchronize)
    onWidthChanged: { if (visible) placeOnScreen() }
    onHeightChanged: { if (visible) placeOnScreen() }
    onAvailableGeometryChanged: { if (visible) placeOnScreen() }
    onClosing: function(close) { close.accepted = false; acknowledge() }
    Component.onCompleted: Qt.callLater(synchronize)
    Connections {
        target: warning.recording
        ignoreUnknownSignals: true
        function onChanged() { Qt.callLater(warning.synchronize) }
    }

    DialogFrame { anchors.fill: parent; opaque: true }
    ColumnLayout {
        id: warningContent
        anchors.fill: parent
        anchors.margins: 20
        spacing: 14
        RowLayout {
            spacing: 10
            Image {
                Layout.preferredWidth: 22
                Layout.preferredHeight: 22
                source: warning.recoveryWanted ? Lucide.source("check", Theme.toneColor("ok"))
                                               : Lucide.source("radio", Theme.toneColor("warn"))
                sourceSize.width: 22 * Screen.devicePixelRatio
                sourceSize.height: 22 * Screen.devicePixelRatio
            }
            Label {
                Layout.fillWidth: true
                text: warning.recoveryWanted ? qsTr("自动记录已恢复") : qsTr("自动记录暂不可用")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(18)
                font.bold: true
                wrapMode: Text.WordWrap
            }
        }
        ScrollView {
            id: warningScroll
            Layout.fillWidth: true
            Layout.fillHeight: true
            implicitHeight: warningBody.implicitHeight
            contentWidth: availableWidth
            clip: true
            ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
            ColumnLayout {
                id: warningBody
                width: warningScroll.availableWidth
                spacing: 14
                Label {
                    objectName: "recordingWarningMessage"
                    Layout.fillWidth: true
                    text: warning.recoveryWanted ? qsTr("已重新读取到游戏数据，可以继续自动记录。")
                          : warning.recording && typeof warning.recording.warningMessage !== "undefined"
                            ? warning.recording.warningMessage : ""
                    textFormat: Text.PlainText
                    wrapMode: Text.WordWrap
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(14)
                }
                Label {
                    visible: !warning.recoveryWanted
                    Layout.fillWidth: true
                    text: qsTr("恢复前，副本可能无法自动记录。后台仍会继续尝试恢复。")
                    textFormat: Text.PlainText
                    wrapMode: Text.WordWrap
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                }
            }
        }
        RowLayout {
            Layout.fillWidth: true
            spacing: 10
            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "recordingWarningDiagnose"
                visible: !warning.recoveryWanted
                text: qsTr("查看诊断")
                onClicked: warning.diagnosisRequested()
            }
            AppButton {
                objectName: "recordingWarningAcknowledge"
                text: qsTr("我知道了")
                variant: "primary"
                onClicked: warning.acknowledge()
            }
        }
    }
}
