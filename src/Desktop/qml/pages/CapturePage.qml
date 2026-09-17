import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// ---------------------------------------------------------------------------
// 捕获诊断（docs/ui-design.md §4.4）
//
// One left-aligned column, at most 1040 px wide, 20 px between blocks:
//   标题行 ............ player: listening status in words; maintainer: 开始 / 停止
//   CaptureNpcapPanel .. 降级模式, only while Npcap is missing or unusable
//   CaptureNotices ..... calibration card, 协议档案, no-traffic and adapter notices
//   CaptureChainPanel .. 链路: FF14 进程 → Npcap → 适配器 → 协议档案
//   解析 | 工具 ........ CaptureParsePanel (1.3) beside CaptureToolsPanel (1)
//   CaptureMaintainerSection  维护者工具, only with App.maintainerToolsVisible
//
// Every value is a contract field ($defs/CaptureStatus, GetStatus extras,
// ListCaptureAdapters, GetProtocolProfileStatus). A field the Collector did not
// send renders as an em dash or a sentence saying so, never as a 0 this page
// invented (App.captureCounters copies only what arrived). The page reads App,
// Fmt and Theme only - no Settings - so tests can load it in isolation.
// ---------------------------------------------------------------------------
Item {
    id: page

    // 校准对话框与其它模态一样挂在 Main.qml 的 Overlay 上，故此处仅发出信号。
    signal openCalibrationRequested()

    readonly property int contentMaxWidth: 1040
    readonly property bool maintainerTools: App.maintainerToolsVisible
    readonly property var npcap: App.npcapInfo
    // Installed is not enough: NOT_WINPCAP_COMPATIBLE, NPCAP_ADMIN_ONLY and
    // LOAD_FAILED are installed drivers this Collector still cannot open.
    readonly property bool npcapProblem: !App.npcapInstalled
                                         || (!!page.npcap.status && page.npcap.status !== "READY")

    // The words the sidebar uses for the same state (Main.qml captureBadgeText),
    // with 等待游戏启动 spelled out.
    function listeningText() {
        const state = App.recording.state
        if (state === "listening")
            return qsTr("自动监听中")
        if (App.recording.silent)
            return qsTr("监听中 · 收不到游戏数据")
        if (state === "waiting")
            return qsTr("等待游戏启动")
        if (state === "calibrating")
            return qsTr("校准中")
        if (state === "calibration_ready")
            return qsTr("校准待核对")
        if (state === "calibration_blocked" || App.recording.blocked)
            return qsTr("无法自动记录")
        return qsTr("检查中")
    }

    function listeningColor() {
        return App.recording.attention ? Theme.orange
             : App.recording.state === "listening" ? Theme.green : Theme.neutral400
    }

    Connections {
        target: App
        function onCurrentPageChanged() {
            if (App.currentPage === 4)
                Qt.callLater(function() { scroll.contentY = 0 })
        }
    }

    Flickable {
        id: scroll
        anchors.fill: parent
        contentWidth: width
        contentHeight: contentColumn.implicitHeight
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        ScrollBar.vertical: ScrollBar {
            policy: scroll.contentHeight > scroll.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff
        }

        ColumnLayout {
            id: contentColumn
            width: Math.min(page.contentMaxWidth, scroll.width - Theme.scrollGutter)
            spacing: 20

            PageHeader {
                title: qsTr("捕获诊断")
                subtitle: qsTr("被动监听 · 不发包 · 不注入")

                // Decision 2: a player has nothing to start or stop - the
                // capture follows the game - so the corner says what it is doing.
                RowLayout {
                    objectName: "captureHeaderStatus"
                    visible: !page.maintainerTools
                    Layout.bottomMargin: 4
                    spacing: 8

                    Rectangle {
                        Layout.alignment: Qt.AlignVCenter
                        implicitWidth: 8
                        implicitHeight: 8
                        radius: 4
                        color: page.listeningColor()
                    }

                    Text {
                        objectName: "captureHeaderStatusText"
                        text: page.listeningText()
                        textFormat: Text.PlainText
                        color: App.recording.attention ? Theme.orangeText : Theme.textPrimary
                        font.pixelSize: Theme.fs(13)
                        font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                    }
                }

                AppButton {
                    objectName: "captureHeaderAction"
                    visible: page.maintainerTools
                    text: App.captureActionLabel
                    variant: "primary"
                    enabled: App.captureActionEnabled
                    onClicked: App.toggleCapture()
                }
            }

            CaptureNpcapPanel {
                Layout.fillWidth: true
                visible: page.npcapProblem
            }

            CaptureNotices {
                Layout.fillWidth: true
                onOpenCalibrationRequested: page.openCalibrationRequested()
            }

            CaptureChainPanel {
                Layout.fillWidth: true
            }

            // grid-template-columns:1.3fr 1fr; gap:16px; align-items:start
            RowLayout {
                Layout.fillWidth: true
                spacing: 16

                CaptureParsePanel {
                    Layout.fillWidth: true
                    Layout.preferredWidth: 1
                    Layout.horizontalStretchFactor: 13
                    Layout.alignment: Qt.AlignTop
                }

                CaptureToolsPanel {
                    Layout.fillWidth: true
                    Layout.preferredWidth: 1
                    Layout.horizontalStretchFactor: 10
                    Layout.alignment: Qt.AlignTop
                }
            }

            CaptureMaintainerSection {
                Layout.fillWidth: true
                visible: page.maintainerTools
            }
        }
    }
}
