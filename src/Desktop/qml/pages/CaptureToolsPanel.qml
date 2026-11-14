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
    signal openIdentityEvidenceRequested()
    // 帮助适配其他区服 follows the round as the 采证 page tells it - the page's own
    // EvidenceSession, so a cancelled wait, an incomplete file and 开始下一轮 read
    // the same here (review K2-2): 采证中 with the clock, 已保存 waiting for the
    // hand-over, 已结束 with an incomplete file, 采证失败 for a failed round
    // (review RV4-4), else the standing sentence.
    property EvidenceSession evidenceSession: null
    readonly property EvidenceClock evidenceClock: EvidenceClock {}
    readonly property bool evidenceActive: Runtime.app.validationActive
    readonly property bool evidenceEnded: !!tools.evidenceSession && tools.evidenceSession.ended
    readonly property bool evidenceFailed: tools.evidenceEnded && tools.evidenceSession.phase === "failed"
    readonly property bool evidenceSaved: tools.evidenceEnded && tools.evidenceSession.saved
                                          && tools.evidenceSession.fileSaved
    // The round under way, or over and still on the page, is a 角色识别 one: its
    // state is told on that row and the 帮助适配其他区服 row keeps its standing sentence.
    readonly property bool evidenceIdentity: !!tools.evidenceSession && tools.evidenceSession.identity
                                             && (tools.evidenceActive || tools.evidenceEnded)

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
        description: tools.evidenceIdentity ? qsTr("按步骤采集报文元数据，未适配的区服仍不正式记录")
                     : tools.evidenceActive ? qsTr("采证中 · %1").arg(tools.evidenceClock.elapsedText)
                     : tools.evidenceSaved ? qsTr("已保存，等回传")
                     : tools.evidenceFailed ? qsTr("采证失败，可回传已有文件")
                     : tools.evidenceEnded ? qsTr("已结束，文件不完整")
                     : qsTr("按步骤采集报文元数据，未适配的区服仍不正式记录")
        descriptionColor: tools.evidenceActive && !tools.evidenceIdentity ? Theme.orangeText : Theme.textSecondary
        AppButton {
            objectName: "templateObservationGuideButton"
            text: !tools.evidenceIdentity && (tools.evidenceActive || tools.evidenceEnded) ? qsTr("继续") : qsTr("采证步骤")
            // One round at a time: while the other kind is under way this row waits.
            enabled: !(tools.evidenceIdentity && tools.evidenceActive)
            onClicked: tools.toggleObservationGuideRequested()
        }
    }

    SettingsRow {
        objectName: "identityEvidenceRow"
        visible: !!tools.evidence
        label: qsTr("角色识别采证")
        description: !tools.evidenceIdentity ? qsTr("两个角色轮流登录，核实软件能否区分它们；不新增正式记录")
                     : tools.evidenceActive ? qsTr("采证中 · %1").arg(tools.evidenceClock.elapsedText)
                     : tools.evidenceSaved ? qsTr("已保存，等回传")
                     : tools.evidenceFailed ? qsTr("采证失败，可回传已有文件")
                     : qsTr("已结束，文件不完整")
        descriptionColor: tools.evidenceActive && tools.evidenceIdentity ? Theme.orangeText : Theme.textSecondary
        AppButton {
            objectName: "identityEvidenceButton"
            text: tools.evidenceIdentity ? qsTr("继续") : qsTr("采证步骤")
            enabled: tools.evidenceIdentity || !tools.evidenceActive
            onClicked: tools.openIdentityEvidenceRequested()
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
