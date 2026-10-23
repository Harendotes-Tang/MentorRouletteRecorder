import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// ---------------------------------------------------------------------------
// 捕获诊断（docs/ui-design.md §4.4）
//
// One left-aligned column, at most 1080 px wide, 12 px between cards:
//   标题行 ............ player: the state as a badge; maintainer: 开始 / 停止
//   CaptureHeroPanel ... what the capture is doing now (降级模式 included), then
//                        链路: FF14 进程 → Npcap → 适配器 → 协议档案
//   GameSelectionPanel . 记录对象, only while there is something to choose
//   CaptureNotices ..... adapter and readiness notices, the calibration row
//                        (本机校准 | 共享校准), 协议档案
//   解析 | 工具 ........ continue the left/right calibration columns independently;
//                        without calibration, use the same 2:1 split
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
    signal openRegionSettingsRequested()

    readonly property int contentMaxWidth: 1080
    readonly property bool maintainerTools: Runtime.app.maintainerToolsVisible
    property bool observationGuideExpanded: false

    Connections {
        target: Runtime.app
        function onCurrentPageChanged() {
            if (Runtime.app.currentPage === 4)
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
            spacing: 12

            PageHeader {
                title: qsTr("捕获诊断")
                subtitle: qsTr("被动监听 · 不发包 · 不注入")

                // Decision 2: a player has nothing to start or stop - the
                // capture follows the game - so the corner says what it is
                // doing, in the words the hero card and the sidebar use.
                StatusTag {
                    objectName: "captureHeaderStatus"
                    visible: !page.maintainerTools
                    Layout.bottomMargin: 4
                    text: hero.badgeText
                    tone: hero.badgeTone
                    labelObjectName: "captureHeaderStatusText"
                }

                AppButton {
                    objectName: "captureHeaderAction"
                    visible: page.maintainerTools
                    text: Runtime.app.captureActionLabel
                    variant: "primary"
                    enabled: Runtime.app.captureActionEnabled
                    onClicked: Runtime.app.toggleCapture()
                }
            }

            CaptureHeroPanel {
                id: hero
                Layout.fillWidth: true
                Layout.topMargin: 4
            }

            GameSelectionPanel {}

            CaptureNotices {
                id: notices
                Layout.fillWidth: true
                // While the hero card talks about something else (Npcap, a game
                // to choose, a silent capture, an error) the calibration row
                // says its own sentence.
                heroSpeaksCalibration: hero.copy.kind === "calibration"
                onOpenCalibrationRequested: page.openCalibrationRequested()
                onOpenRegionSettingsRequested: page.openRegionSettingsRequested()
            }

            // Keep the same card instances while calibration appears/disappears.
            // Wide calibration owns their columns; otherwise this grid owns them.
            GridLayout {
                id: lowerRow
                Layout.fillWidth: true
                visible: !notices.calibrationLocalColumn
                columns: contentColumn.width >= 640 ? 2 : 1
                columnSpacing: 12
                rowSpacing: 12
                readonly property int available: contentColumn.width - lowerRow.columnSpacing

                CaptureParsePanel {
                    parent: notices.calibrationLocalColumn || lowerRow
                    Layout.fillWidth: true
                    Layout.preferredWidth: lowerRow.columns === 2 ? Math.round(lowerRow.available * 2 / 3)
                        : contentColumn.width
                    Layout.alignment: Qt.AlignTop
                }

                CaptureToolsPanel {
                    parent: notices.calibrationSideColumn || lowerRow
                    Layout.fillWidth: true
                    Layout.preferredWidth: lowerRow.columns === 2
                        ? lowerRow.available - Math.round(lowerRow.available * 2 / 3) : contentColumn.width
                    Layout.alignment: Qt.AlignTop
                    onToggleObservationGuideRequested: page.observationGuideExpanded = !page.observationGuideExpanded
                }
            }

            CaptureValidationEvidenceCard {
                Layout.fillWidth: true
                guidedMode: true
                visible: page.observationGuideExpanded
            }

            CaptureMaintainerSection {
                Layout.fillWidth: true
                visible: page.maintainerTools
            }
        }
    }
}
