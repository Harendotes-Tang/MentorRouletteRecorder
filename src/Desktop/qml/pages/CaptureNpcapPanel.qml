import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 降级模式：one orange-framed panel - explanation and its two buttons on the
// left, the four install steps on the right (grid-template-columns:1fr 1fr;
// gap:24px). Shown while Npcap is missing or installed in a way this Collector
// cannot open.
Card {
    id: panel
    objectName: "captureNpcapPanel"

    readonly property var npcap: App.npcapInfo
    readonly property string status: String(panel.npcap.status || "")
    readonly property bool notInstalled: !App.npcapInstalled || panel.status === ""
                                         || panel.status === "NOT_INSTALLED"

    // GetStatus.npcap.status (CaptureWire.StatusExtras), in words.
    function reasonText() {
        switch (panel.status) {
        case "NOT_WINPCAP_COMPATIBLE": return qsTr("Npcap 没有以 WinPcap 兼容模式安装。")
        case "NPCAP_ADMIN_ONLY": return qsTr("Npcap 被设为仅限管理员使用，而本软件没有以管理员身份运行。")
        case "LOAD_FAILED": return qsTr("Npcap 已登记，但驱动文件无法加载。")
        default: return qsTr("Npcap 暂时用不了。")
        }
    }

    // The fixed sentence covers a missing driver. For a driver that is present
    // but unusable, the Collector's own install_hint names the exact fix, so the
    // UI and --capture-doctor never disagree.
    function explanation() {
        const base = qsTr("被动抓包依赖 Npcap 驱动，出于许可证限制需自行安装。安装前仍可手动记录、统计、导出与备份。")
        if (panel.notInstalled)
            return base
        const hint = panel.npcap.install_hint || App.adapterInfo.install_hint || ""
        return panel.reasonText() + (hint.length > 0 ? hint : base)
    }

    horizontalPadding: 24
    verticalPadding: 20
    borderColor: Theme.orange
    spacing: 0

    RowLayout {
        Layout.fillWidth: true
        spacing: 24

        ColumnLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            Layout.preferredWidth: 1
            spacing: 8

            CardKicker {
                Layout.fillWidth: true
                text: qsTr("降级模式")
                color: Theme.orangeText
            }

            HeadingLabel {
                objectName: "captureNpcapHeadline"
                Layout.fillWidth: true
                text: panel.notInstalled ? qsTr("未安装 Npcap") : qsTr("Npcap 暂时用不了")
                font.pixelSize: Theme.fs(22)
                wrapMode: Text.WordWrap
            }

            Text {
                objectName: "captureNpcapExplanation"
                Layout.fillWidth: true
                text: panel.explanation()
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(13)
                lineHeight: 1.3
                wrapMode: Text.WordWrap
            }

            // margin-top:auto - the buttons sit on the bottom edge whichever
            // column is taller.
            Item {
                Layout.fillHeight: true
                Layout.minimumHeight: 4
            }

            RowLayout {
                Layout.fillWidth: true
                spacing: 8

                AppButton {
                    objectName: "openNpcapButton"
                    text: qsTr("打开 npcap.com")
                    variant: "primary"
                    onClicked: App.openNpcapWebsite()
                }

                AppButton {
                    objectName: "redetectNpcapButton"
                    text: qsTr("重新检测")
                    onClicked: App.rescanAdapters()
                }

                Item { Layout.fillWidth: true }
            }
        }

        ColumnLayout {
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            Layout.alignment: Qt.AlignTop
            spacing: 6

            Repeater {
                model: [
                    qsTr("从 npcap.com 下载安装包（需管理员权限）。"),
                    qsTr("勾选 “Install Npcap in WinPcap API-compatible Mode”。"),
                    qsTr("不勾选 “Restrict … to Administrators only”。"),
                    qsTr("安装完成后点击“重新检测”。")
                ]

                delegate: RowLayout {
                    required property string modelData
                    required property int index

                    Layout.fillWidth: true
                    spacing: 8

                    Text {
                        Layout.alignment: Qt.AlignTop
                        Layout.preferredWidth: 14
                        text: String(index + 1) + "."
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(false)
                        font.features: ({ "tnum": 1 })
                    }

                    Text {
                        Layout.fillWidth: true
                        text: modelData
                        textFormat: Text.PlainText
                        color: Theme.textPrimary
                        opacity: 0.85
                        font.pixelSize: Theme.fs(13)
                        lineHeight: 1.3
                        wrapMode: Text.WordWrap
                    }
                }
            }
        }
    }
}
