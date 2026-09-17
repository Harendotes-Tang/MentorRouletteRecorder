import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 第一次选在线语音时的确认框（docs/privacy-boundary.md §8.3 "默认关闭"）。
//
// 写明发什么、发给谁、密钥在哪、怎么关。确认一次记在 Settings.ttsOnlineConfirmed，
// 这台机器以后不再问；取消则保持原来的语音。
Dialog {
    id: dialog
    objectName: "onlineSpeechConfirmDialog"

    property string voiceId: ""
    property string voiceLabel: ""
    readonly property bool azure: dialog.voiceId.indexOf("azure:") === 0
    property bool answered: false

    signal confirmed(string voiceId)
    signal cancelled()

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 540
    padding: 20
    closePolicy: Popup.CloseOnEscape

    background: DialogFrame {}

    function ask(id, label) {
        dialog.voiceId = id
        dialog.voiceLabel = label || id
        dialog.answered = false
        dialog.open()
    }

    function confirmChoice() {
        dialog.answered = true
        dialog.close()
        dialog.confirmed(dialog.voiceId)
    }

    // Escape, 取消 and anything else that closes the dialog keep the old voice.
    onClosed: {
        if (!dialog.answered) {
            dialog.answered = true
            dialog.cancelled()
        }
    }

    contentItem: ColumnLayout {
        spacing: 12

        CardKicker { text: qsTr("在线语音 · 需要你确认") }

        HeadingLabel {
            Layout.fillWidth: true
            text: qsTr("改用在线语音播报？")
            font.pixelSize: Theme.dialogTitleSize(20)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("你选的是「%1」。它不是在本机合成的：每次播报都要把这句话发给语音服务。").arg(dialog.voiceLabel)
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
            wrapMode: Text.WordWrap
        }

        Repeater {
            model: [
                {
                    title: qsTr("发送什么"),
                    body: qsTr("只发这一句要播报的话（模板填好后的一句，含副本名与进度数字），以及音色和语速。"
                               + "不带账号、角色名、队友、游戏报文或任何能认出这台电脑的信息。")
                },
                {
                    title: qsTr("发给谁"),
                    body: dialog.azure
                          ? qsTr("微软 Azure 语音（你填写的区域）。请求由后台的采集器发出，对方能看到请求来自哪个 IP 地址，并按它自己的隐私政策处理收到的文字。")
                          : qsTr("你填写地址的 OpenAI 兼容服务。请求由后台的采集器发出，对方能看到请求来自哪个 IP 地址，并按它自己的隐私政策处理收到的文字。")
                },
                {
                    title: qsTr("密钥"),
                    body: qsTr("用你自己申请的密钥。它只保存在本机，由 Windows 加密，不会写进日志或诊断报告，也不会被发给别的服务。")
                },
                {
                    title: qsTr("怎么关"),
                    body: qsTr("在「设置 → 播报」把语音引擎改回本机语音，之后一个请求也不发。在线语音出错时，这一句会自动改用本机语音。")
                }
            ]

            delegate: ColumnLayout {
                required property var modelData

                Layout.fillWidth: true
                spacing: 2

                Text {
                    Layout.fillWidth: true
                    text: "· " + modelData.title
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(13)
                    font.bold: true
                    wrapMode: Text.WordWrap
                }
                Text {
                    Layout.fillWidth: true
                    Layout.leftMargin: 12
                    text: modelData.body
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    lineHeight: 1.35
                    wrapMode: Text.WordWrap
                }
            }
        }

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            Layout.topMargin: 4
            color: Theme.border
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            Text {
                Layout.fillWidth: true
                text: qsTr("这台电脑只问这一次。")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                wrapMode: Text.WordWrap
            }
            AppButton {
                objectName: "onlineSpeechConfirmCancel"
                text: qsTr("取消")
                onClicked: dialog.close()
            }
            AppButton {
                objectName: "onlineSpeechConfirmAccept"
                variant: "primary"
                text: qsTr("改用在线语音")
                onClicked: dialog.confirmChoice()
            }
        }
    }
}
