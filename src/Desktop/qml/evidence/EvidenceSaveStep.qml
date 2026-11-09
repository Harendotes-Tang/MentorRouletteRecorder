pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// ④ 停止采证，保存文件: the round stops and is written; the player ticks off the
// files as they copy them out, with a button to the folder each one lives in.
// A failed round lands here too, with why it failed: whatever it left is still
// handed over (review K4-4).
EvidenceStep {
    id: step

    required property EvidenceSession session

    readonly property bool stopping: step.session.phase === "stopping"
    readonly property bool failed: step.session.phase === "failed"
    readonly property bool whole: step.session.saved && step.session.fileSaved
    readonly property bool filesLocked: !step.session.ended

    number: 4
    title: step.session.stepTitles[3]

    function runFileAction(action) {
        switch (action) {
        case "folder": Runtime.app.openCaptureValidationFolder(); break
        case "diagnostics": Runtime.app.exportDiagnosticsReport(); break
        case "logs": Runtime.app.openLogsFolder(); break
        }
    }
    function fileActionLabel(action) {
        switch (action) {
        case "folder": return qsTr("打开所在目录")
        case "diagnostics": return qsTr("导出诊断报告")
        case "logs": return qsTr("打开日志目录")
        default: return ""
        }
    }
    function fileActionEnabled(action) {
        if (action === "folder")
            return !!step.session.status.trace_path && !Runtime.app.usingMockData
        return true
    }

    // `.ev-save`
    RowLayout {
        Layout.fillWidth: true
        spacing: 12

        // Still recording: stop from here too.
        AppButton {
            visible: step.session.recording
            text: Runtime.app.captureCommandBusy ? qsTr("处理中…") : qsTr("停止采证")
            variant: "primary"
            enabled: Runtime.app.templateObservationActionEnabled && !Runtime.app.captureCommandBusy
            onClicked: step.session.stop()
        }
        EvidenceHint { visible: step.session.recording; text: qsTr("离本回到外面再停止") }

        // `.ev-spin`
        Item {
            visible: step.stopping
            implicitWidth: 16
            implicitHeight: 16
            Rectangle {
                anchors.fill: parent
                radius: 8
                color: "transparent"
                border.width: 2
                border.color: Theme.border
            }
            // the accent quarter arc that turns on the track
            Canvas {
                id: spinner
                anchors.fill: parent
                rotation: 0
                onPaint: {
                    const context = getContext("2d")
                    context.reset()
                    context.lineWidth = 2
                    context.strokeStyle = String(Theme.accent)
                    context.beginPath()
                    context.arc(width / 2, height / 2, width / 2 - 1, -Math.PI / 2, 0)
                    context.stroke()
                }
                Connections { target: Theme; function onDarkChanged() { spinner.requestPaint() } }
                RotationAnimation on rotation {
                    running: step.stopping && Theme.motion
                    loops: Animation.Infinite
                    from: 0
                    to: 360
                    duration: 800
                }
            }
        }
        Text {
            visible: step.stopping
            textFormat: Text.PlainText
            text: qsTr("正在保存…")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            font.weight: Font.Bold
        }

        // `.ev-okc`
        Rectangle {
            visible: step.session.ended
            implicitWidth: 22
            implicitHeight: 22
            radius: 11
            color: step.whole ? Theme.green : step.failed ? Theme.red : Theme.orange
            Image {
                anchors.centerIn: parent
                width: 14; height: 14
                source: Lucide.source(step.whole ? "check" : "circle-alert", Theme.badgeForeground)
                sourceSize: Qt.size(28, 28)
                Accessible.ignored: true
            }
        }
        Text {
            objectName: "evidenceSavedText"
            visible: step.session.ended
            textFormat: Text.PlainText
            text: step.failed ? qsTr("采证失败")
                  : step.session.fileSaved
                  ? qsTr("已保存 · %1").arg(step.session.endedAt ? Runtime.fmt.localTime(step.session.endedAt) : Runtime.fmt.dash())
                  : qsTr("已结束，文件不完整")
            color: step.whole ? Theme.textPrimary : step.failed ? Theme.red : Theme.orangeText
            font.pixelSize: Theme.fs(13)
            font.weight: Font.Bold
        }
        EvidenceHint {
            visible: step.session.ended
            text: step.whole ? qsTr("先截一张图，留下状态和四项计数")
                             : qsTr("有多少文件就回传多少，缺的会写进回传说明。")
        }
    }

    // Why the round failed: the Collector's sentence (or the one for its code),
    // and the code itself only in the maintainer's view (K2-4).
    Text {
        objectName: "evidenceFailureText"
        Layout.fillWidth: true
        visible: step.failed && step.session.failureText.length > 0
        textFormat: Text.PlainText
        text: step.session.failureText
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }
    Text {
        Layout.fillWidth: true
        visible: step.failed && step.session.technical && !!step.session.status.error_code
        textFormat: Text.PlainText
        text: qsTr("错误码：") + (step.session.status.error_code || "")
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }
    // A refused stop, said where it was pressed (review I4-1).
    Text {
        objectName: "evidenceSaveError"
        Layout.fillWidth: true
        visible: Runtime.app.validationError.length > 0
        textFormat: Text.PlainText
        text: qsTr("采证操作失败：") + Runtime.app.validationError
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }

    // The Collector's trace, folded to %USERPROFILE% exactly as it logs it
    // (docs/privacy-boundary.md §5); the maintainer's view adds the checksum.
    Text {
        Layout.fillWidth: true
        visible: !!step.session.status.trace_path
        textFormat: Text.PlainText
        text: !step.session.technical
              ? qsTr("采证文件：%1").arg(Runtime.fmt.foldUserPath(step.session.status.trace_path || "") || Runtime.fmt.dash())
              : qsTr("Trace：%1\nSHA256 文件：%2\nSHA256：%3")
                .arg(Runtime.fmt.foldUserPath(step.session.status.trace_path || "") || Runtime.fmt.dash())
                .arg(Runtime.fmt.foldUserPath(step.session.status.sha256_path || "") || Runtime.fmt.dash())
                .arg(step.session.status.sha256 || Runtime.fmt.dash())
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
        font.family: Theme.monoFamily
        wrapMode: Text.WrapAnywhere
    }
    Text {
        Layout.fillWidth: true
        visible: Runtime.app.usingMockData && !!step.session.status.trace_path
        textFormat: Text.PlainText
        text: qsTr("当前是截图用模拟路径，不会当作可打开的真实取证。")
        color: Theme.orangeText
        font.pixelSize: Theme.fs(11)
        wrapMode: Text.Wrap
    }

    // `.ev-files`
    Rectangle {
        objectName: "evidenceFileList"
        Layout.fillWidth: true
        implicitHeight: fileRows.implicitHeight + 2
        radius: Theme.radiusM
        color: "transparent"
        border.width: 1
        border.color: Theme.border
        clip: true

        ColumnLayout {
            id: fileRows
            anchors.fill: parent
            anchors.margins: 1
            spacing: 0

            Repeater {
                model: step.session.fileDefs
                delegate: ColumnLayout {
                    id: fileRow
                    required property var modelData
                    required property int index
                    readonly property bool copied: !!step.session.files[modelData.key]
                    Layout.fillWidth: true
                    spacing: 0

                    // `.ev-fsep`: the optional files begin here.
                    Rectangle {
                        visible: !fileRow.modelData.required
                                 && (fileRow.index === 0 || step.session.fileDefs[fileRow.index - 1].required)
                        Layout.fillWidth: true
                        implicitHeight: separatorLabel.implicitHeight + 10
                        color: Theme.insetBackground
                        Rectangle { anchors.top: parent.top; width: parent.width; height: 1; color: Theme.border }
                        Text {
                            id: separatorLabel
                            anchors.left: parent.left
                            anchors.leftMargin: 12
                            anchors.verticalCenter: parent.verticalCenter
                            textFormat: Text.PlainText
                            text: qsTr("按需附加")
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(11)
                            font.weight: Font.DemiBold
                            font.letterSpacing: 0.6
                        }
                    }

                    Rectangle {
                        visible: fileRow.index > 0
                        Layout.fillWidth: true
                        implicitHeight: 1
                        color: Theme.border
                    }

                    // `.ev-file`
                    RowLayout {
                        Layout.fillWidth: true
                        Layout.leftMargin: 12
                        Layout.rightMargin: 12
                        Layout.topMargin: 5
                        Layout.bottomMargin: 5
                        Layout.minimumHeight: 30
                        spacing: 12

                        SelectionCheckBox {
                            objectName: "evidenceFile_" + fileRow.modelData.key
                            Layout.fillWidth: true
                            Layout.preferredWidth: 10
                            text: fileRow.modelData.name
                            enabled: !step.filesLocked
                            font.pixelSize: Theme.fs(fileRow.modelData.mono ? 12 : 13)
                            font.family: fileRow.modelData.mono ? Theme.monoFamily : Theme.bodyFamily
                            font.weight: fileRow.modelData.mono ? Font.Medium : Font.DemiBold
                            font.strikeout: fileRow.copied
                            Binding on checked {
                                value: fileRow.copied
                                restoreMode: Binding.RestoreNone
                            }
                            onToggled: step.session.toggleFile(fileRow.modelData.key)
                        }
                        Text {
                            Layout.fillWidth: true
                            Layout.preferredWidth: 10
                            textFormat: Text.PlainText
                            text: fileRow.modelData.desc
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                            wrapMode: Text.Wrap
                        }
                        Item {
                            Layout.preferredWidth: 136
                            Layout.minimumWidth: 136
                            implicitHeight: Math.max(fileAction.implicitHeight, fileKey.implicitHeight)
                            AppButton {
                                id: fileAction
                                objectName: "evidenceFileAction_" + fileRow.modelData.key
                                anchors.right: parent.right
                                anchors.verticalCenter: parent.verticalCenter
                                visible: fileRow.modelData.action.length > 0
                                compact: true
                                text: step.fileActionLabel(fileRow.modelData.action)
                                enabled: step.fileActionEnabled(fileRow.modelData.action)
                                onClicked: step.runFileAction(fileRow.modelData.action)
                            }
                            // `.ev-key`
                            Rectangle {
                                id: fileKey
                                anchors.right: parent.right
                                anchors.verticalCenter: parent.verticalCenter
                                visible: fileRow.modelData.hint.length > 0
                                implicitWidth: keyLabel.implicitWidth + 14
                                implicitHeight: keyLabel.implicitHeight + 6
                                radius: Theme.radiusXs
                                color: Theme.insetBackgroundStrong
                                border.width: 1
                                border.color: Theme.insetBorder
                                Text {
                                    id: keyLabel
                                    anchors.centerIn: parent
                                    textFormat: Text.PlainText
                                    text: fileRow.modelData.hint
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(11)
                                    font.family: Theme.monoFamily
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // `.ev-notes`
    ColumnLayout {
        Layout.fillWidth: true
        spacing: 4
        EvidenceHint { iconName: "file-check"; text: qsTr("每轮分开保存，不改原文件和文件名。") }
        EvidenceHint { iconName: "history"; text: qsTr("采证目录只留最近 10 份、7 天内，记得及时复制出去。") }
        EvidenceHint { iconName: "file-down"; text: qsTr("失败也照样回传已有的文件，缺的会写进回传说明。") }
    }

    // `.ev-acts`
    RowLayout {
        Layout.fillWidth: true
        spacing: 12
        AppButton {
            objectName: "evidenceToReportButton"
            text: qsTr("下一步：生成回传说明")
            variant: "primary"
            iconName: "chevron-right"
            iconAfterText: true
            enabled: step.session.ended
            onClicked: step.session.reportOpen = true
        }
        EvidenceHint {
            visible: step.session.ended && step.session.missingFiles.length > 0
            text: qsTr("还缺 %1 项，也能继续，回传说明里会注明").arg(step.session.missingFiles.length)
        }
    }
}
