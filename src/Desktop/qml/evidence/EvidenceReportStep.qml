pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// ⑤ 打包回传给维护者: the few facts only the player knows (booster or VPN, duty,
// job, what happened) beside the 回传说明 the page already filled in; then what
// goes into the ZIP, what must not, the optional candidate summary, and the
// next round.
EvidenceStep {
    id: step

    required property EvidenceSession session

    property bool copied: false
    property bool optionalOpen: false
    readonly property var evidence: typeof Runtime.calibrationEvidence !== "undefined" ? Runtime.calibrationEvidence : null
    readonly property bool wide: width >= 720

    number: 5
    title: step.session.stepTitles[4]

    Timer {
        id: copiedReset
        interval: 2500
        onTriggered: step.copied = false
    }

    // `.ev-rep`: 210 px of fields, the report beside them
    GridLayout {
        Layout.fillWidth: true
        columns: step.wide ? 2 : 1
        columnSpacing: 16
        rowSpacing: 16

        // `.ev-rf` -- what only the player knows about an event round; an identity
        // round has nothing of the kind to ask.
        ColumnLayout {
            visible: !step.session.identity
            Layout.alignment: Qt.AlignTop
            Layout.preferredWidth: step.wide ? 210 : -1
            Layout.minimumWidth: step.wide ? 210 : 0
            Layout.maximumWidth: step.wide ? 210 : 16777215
            Layout.fillWidth: !step.wide
            spacing: 5

            FieldLabel { text: qsTr("加速器或 VPN") }
            SegmentedControl {
                objectName: "evidenceVpnKind"
                Layout.fillWidth: true
                options: [
                    { value: "none", label: qsTr("未使用") },
                    { value: "acc", label: qsTr("加速器") },
                    { value: "vpn", label: "VPN" }
                ]
                currentValue: step.session.vpn
                onActivated: function(value) { step.session.vpn = value }
            }
            StyledTextField {
                objectName: "evidenceVpnName"
                visible: step.session.vpn !== "none"
                Layout.fillWidth: true
                placeholderText: qsTr("名称")
                text: step.session.vpnName
                onTextEdited: step.session.vpnName = text
                Accessible.name: qsTr("加速器或 VPN 的名称")
            }

            FieldLabel { Layout.topMargin: 5; text: qsTr("副本") }
            StyledTextField {
                objectName: "evidenceDutyField"
                Layout.fillWidth: true
                placeholderText: qsTr("例：天狼星灯塔")
                text: step.session.duty
                onTextEdited: step.session.duty = text
                Accessible.name: qsTr("副本")
            }

            FieldLabel { Layout.topMargin: 5; text: qsTr("职业") }
            StyledTextField {
                objectName: "evidenceJobField"
                Layout.fillWidth: true
                placeholderText: qsTr("例：白魔法师")
                text: step.session.job
                onTextEdited: step.session.job = text
                Accessible.name: qsTr("职业")
            }

            FieldLabel { Layout.topMargin: 5; text: qsTr("这一轮的情况") }
            Flow {
                Layout.fillWidth: true
                spacing: 5
                Repeater {
                    model: step.session.chipDefs
                    delegate: Chip {
                        required property var modelData
                        objectName: "evidenceReportChip_" + modelData.key
                        text: modelData.label
                        checked: !!step.session.chips[modelData.key]
                        onToggled: function(checked) { step.session.toggleChip(modelData.key, checked) }
                    }
                }
            }
        }

        // `.ev-ro`
        ColumnLayout {
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignTop
            spacing: 8
            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                FieldLabel { text: qsTr("回传说明") }
                Text {
                    Layout.fillWidth: true
                    textFormat: Text.PlainText
                    text: step.session.identity ? qsTr("版本、时间、区服、步骤、结论和计数已自动填好")
                                                : qsTr("版本、时间、区服、标记和计数已自动填好")
                    color: Theme.green
                    font.pixelSize: Theme.fs(11)
                    elide: Text.ElideRight
                }
            }
            // `.ev-pre`
            InsetBox {
                Layout.fillWidth: true
                implicitHeight: Math.min(236, reportText.implicitHeight + 20)
                clip: true
                Flickable {
                    anchors.fill: parent
                    anchors.margins: 10
                    contentWidth: width
                    contentHeight: reportText.implicitHeight
                    boundsBehavior: Flickable.StopAtBounds
                    clip: true
                    Text {
                        id: reportText
                        objectName: "evidenceReportText"
                        width: parent.width
                        textFormat: Text.PlainText
                        text: step.session.report
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(11.5)
                        font.family: Theme.monoFamily
                        lineHeight: 1.5
                        wrapMode: Text.Wrap
                    }
                }
            }
            AppButton {
                objectName: "evidenceCopyReportButton"
                text: step.copied ? qsTr("已复制") : qsTr("复制回传说明")
                variant: "primary"
                iconName: step.copied ? "clipboard-check" : "clipboard-copy"
                onClicked: {
                    Runtime.app.copyToClipboard(step.session.report)
                    step.copied = true
                    copiedReset.restart()
                    Runtime.app.showToast(step.session.identity
                                          ? qsTr("已复制回传说明，和两个文件一起发给维护者")
                                          : qsTr("已复制回传说明，粘贴到 ZIP 旁边的文字里就行"))
                }
            }
        }
    }

    // `.ev-zip`
    InsetBox {
        Layout.fillWidth: true
        implicitHeight: zipRows.implicitHeight + 20
        ColumnLayout {
            id: zipRows
            anchors.fill: parent
            anchors.margins: 10
            spacing: 6
            EvidenceHint {
                objectName: "evidenceZipHint"
                iconName: "file-input"
                color: Theme.textPrimary
                // A round whose log is required (EvidenceSession.oodleSearch) sends the log
                // too, and it says what is in it (audit 2026-11-26 K4-1).
                text: step.session.identity
                      ? qsTr("私下交给维护者：trace.jsonl、trace.jsonl.sha256 和这段说明。")
                      : step.session.oodleSearch.length > 0
                        ? qsTr("打包成一个 ZIP，私下交给维护者：采证文件、诊断 JSON、日志 collector-*.log（含几小段游戏主程序的指令字节）、截图和这段说明。")
                        : qsTr("打包成一个 ZIP，私下交给维护者：采证文件、诊断 JSON、截图和这段说明。")
            }
            EvidenceHint {
                iconName: "x"
                text: qsTr("不要附数据库、游戏程序或 DLL、账号密码和原始抓包；完整 trace 不要贴到公开 Issue。")
            }
        }
    }

    // `.ev-opt`
    AppButton {
        objectName: "evidenceOptionalToggle"
        // The candidate summary is made from an event round only.
        visible: !step.session.identity
        variant: "ghost"
        compact: true
        text: qsTr("候选摘要（可选）")
        iconName: "chevron-right"
        onClicked: step.optionalOpen = !step.optionalOpen
        Accessible.description: step.optionalOpen ? qsTr("收起") : qsTr("展开")
    }
    RowLayout {
        Layout.fillWidth: true
        visible: step.optionalOpen && !step.session.identity
        spacing: 16
        Rectangle { Layout.fillHeight: true; implicitWidth: 2; color: Theme.border }
        ColumnLayout {
            Layout.fillWidth: true
            spacing: 8
            AppButton {
                objectName: "previewTemplateObservationButton"
                text: qsTr("预览候选摘要")
                enabled: !!step.evidence && !step.evidence.busy && step.session.saved && step.session.fileSaved
                onClicked: step.evidence.previewTemplateObservation()
            }
            EvidenceHint {
                text: qsTr("预览只针对最近一轮，要在下一轮或重开软件前处理。确认后会复制摘要并打开 GitHub 提交页；也可以把复制的 JSON 存成文件一起回传，不必发 Issue。")
            }
        }
    }

    // `.ev-end`
    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
    RowLayout {
        Layout.fillWidth: true
        spacing: 16
        EvidenceHint {
            text: step.session.identity
                  ? qsTr("结论只说明这一轮读到的连接标识是否不同，不代表软件自动识别角色所用的登录标识；请把文件回传给维护者核对。")
                  : qsTr("采证完成只代表拿到候选线索，仍需维护者验证。先回传一轮，确认可用后再补一轮。")
        }
        AppButton {
            objectName: "evidenceNextRoundButton"
            text: qsTr("开始下一轮")
            onClicked: step.session.startNextRound()
        }
    }
}
