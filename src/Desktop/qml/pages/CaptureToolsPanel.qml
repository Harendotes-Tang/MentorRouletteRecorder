import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 工具: rows with dividers (the settings rows' grid 1fr auto; padding 12px 0).
//   导出脱敏诊断报告 .. ExportDiagnosticsReport; the result arrives as a toast
//   离线回放测试样本 .. maintainers only, and only as a sentence: ipc-v1 has no
//                      replay message, the Collector's --replay command line does it
//   采集器告警 ........ GetStatus.warnings, when there are any
// No 诊断模式 switch: nothing reads Settings.diagnosticsMode.
Card {
    id: tools
    objectName: "captureToolsPanel"

    readonly property bool maintainer: Runtime.app.maintainerToolsVisible
    // Built in the interface language on every read (warning_refs).
    readonly property var warnings: Theme.live(Runtime.app.collectorWarnings) || []
    readonly property var evidence: typeof Runtime.calibrationEvidence !== "undefined" ? Runtime.calibrationEvidence : null
    signal toggleObservationGuideRequested()

    horizontalPadding: 20
    verticalPadding: 16
    spacing: 0

    CardKicker {
        Layout.fillWidth: true
        Layout.bottomMargin: 4
        text: qsTr("工具")
    }

    SettingsRow {
        objectName: "calibrationEvidenceRow"
        visible: !!tools.evidence
        label: qsTr("共享校准证据")
        description: qsTr("先预览公开摘要，再由你提交到 GitHub")
        AppButton {
            objectName: "previewCalibrationEvidenceButton"
            text: qsTr("预览")
            enabled: !!tools.evidence && !tools.evidence.busy
            onClicked: tools.evidence.previewCalibration()
        }
    }

    SettingsRow {
        objectName: "templateObservationGuideRow"
        visible: !!tools.evidence
        label: qsTr("帮助适配其他区服")
        description: qsTr("按步骤采集报文元数据，未适配区服仍不正式记录")
        AppButton {
            objectName: "templateObservationGuideButton"
            text: qsTr("采证步骤")
            onClicked: tools.toggleObservationGuideRequested()
        }
    }

    SettingsRow {
        objectName: "exportDiagnosticsRow"
        label: qsTr("导出诊断报告")
        // What a player needs before pressing 导出 is what the file omits.
        description: tools.maintainer
                     ? qsTr("ExportDiagnosticsReport · 由正在运行的采集器写入本地 JSON · 不含报文、IP、路径与账号")
                     : qsTr("不含游戏内容、网络地址与账号")
        showDivider: tools.maintainer || tools.warnings.length > 0

        AppButton {
            objectName: "exportDiagnosticsButton"
            text: qsTr("导出")
            onClicked: Runtime.app.exportDiagnosticsReport()
        }
    }

    SettingsRow {
        objectName: "offlineReplayRow"
        visible: tools.maintainer
        label: qsTr("离线回放测试样本")
        description: qsTr("使用 --replay 回放样本；默认写入独立临时数据库，重复回放同一样本不会重复新增。")
        showDivider: tools.warnings.length > 0
    }

    ColumnLayout {
        objectName: "collectorWarningsList"
        Layout.fillWidth: true
        Layout.topMargin: 12
        visible: tools.warnings.length > 0
        spacing: 3

        Text {
            text: qsTr("后台服务提示")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
        }

        Repeater {
            model: tools.warnings

            delegate: Text {
                required property var modelData

                Layout.fillWidth: true
                text: "· " + String(modelData)
                textFormat: Text.PlainText
                color: Theme.orangeText
                font.pixelSize: Theme.fs(11)
                wrapMode: Text.WordWrap
            }
        }
    }
}
