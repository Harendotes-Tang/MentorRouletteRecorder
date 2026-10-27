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
    // The native presenter can supply the precise work area, including a
    // taskbar on the left/top. The QML fallback stays on this screen.
    property rect availableGeometry: Qt.rect(Screen.virtualX, Screen.virtualY,
        Math.min(Screen.width, Screen.desktopAvailableWidth),
        Math.min(Screen.height, Screen.desktopAvailableHeight))
    readonly property bool warningWanted: !!recording
        && typeof recording.warningActive !== "undefined" && recording.warningActive
        && typeof recording.warningPending !== "undefined" && recording.warningPending
    signal diagnosisRequested()

    transientParent: null
    visible: false
    modality: Qt.NonModal
    flags: Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.WindowDoesNotAcceptFocus
    title: qsTr("导随记录器 · 自动记录暂不可用")
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
        if (warningWanted && !foregroundDialogVisible) {
            if (!visible) showWarning()
        } else {
            hide()
        }
    }

    onWarningWantedChanged: Qt.callLater(synchronize)
    onForegroundDialogVisibleChanged: Qt.callLater(synchronize)
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
                source: Lucide.source("radio", Theme.toneColor("warn"))
                sourceSize.width: 22 * Screen.devicePixelRatio
                sourceSize.height: 22 * Screen.devicePixelRatio
            }
            Label {
                Layout.fillWidth: true
                text: qsTr("自动记录暂不可用")
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
                    text: warning.recording && typeof warning.recording.warningMessage !== "undefined"
                          ? warning.recording.warningMessage : ""
                    textFormat: Text.PlainText
                    wrapMode: Text.WordWrap
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(14)
                }
                Label {
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
