import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 工具: rows with dividers (the settings rows' grid 1fr auto; padding 12px 0).
//   导出脱敏诊断报告 .. ExportDiagnosticsReport; the result arrives as a toast
//   离线回放测试样本 .. maintainers only, and only as a sentence: ipc-v1 has no
//                      replay message, the Collector's --replay command line does it
//   采集器告警 ........ GetStatus.warnings, when there are any
// No 诊断模式 switch: nothing reads Settings.diagnosticsMode.
Card {
    id: tools
    objectName: "captureToolsPanel"

    readonly property bool maintainer: App.maintainerToolsVisible
    readonly property var warnings: App.collectorWarnings || []

    horizontalPadding: 24
    verticalPadding: 18
    spacing: 0

    CardKicker {
        Layout.fillWidth: true
        Layout.bottomMargin: 4
        text: qsTr("工具")
    }

    SettingsRow {
        objectName: "exportDiagnosticsRow"
        label: qsTr("导出脱敏诊断报告")
        // What a player needs before pressing 导出 is what the file omits.
        description: tools.maintainer
                     ? qsTr("ExportDiagnosticsReport · 由正在运行的采集器写入本地 JSON · 不含报文、IP、路径与账号")
                     : qsTr("不含游戏内容、网络地址与账号")
        showDivider: tools.maintainer || tools.warnings.length > 0

        AppButton {
            objectName: "exportDiagnosticsButton"
            text: qsTr("导出")
            onClicked: App.exportDiagnosticsReport()
        }
    }

    SettingsRow {
        objectName: "offlineReplayRow"
        visible: tools.maintainer
        label: qsTr("离线回放测试样本")
        description: qsTr("用采集器的 --replay 命令行，幂等、不产生新记录")
        showDivider: tools.warnings.length > 0
    }

    ColumnLayout {
        objectName: "collectorWarningsList"
        Layout.fillWidth: true
        Layout.topMargin: 12
        visible: tools.warnings.length > 0
        spacing: 3

        Text {
            text: qsTr("采集器告警")
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
