import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// ---------------------------------------------------------------------------
// 这个软件做什么、不做什么 (DEC-OODLE-01, docs/privacy-boundary.md §4.1, §8.2)
//
// An ordinary information window, opened from 设置 · 关于: nothing to tick,
// nothing to confirm, one 关闭. Until 2026-10-04 it was the first thing a new
// user had to confirm; the owner decided that a first start goes straight to
// the baseline question instead. The window must state that the default
// decompressor reads a copy of the game executable, and must name every kind of
// network request the Collector can send - the shared-calibration download (on
// by default), online speech (opt-in, docs/privacy-boundary.md §8.3) and the
// update check (on by default, docs/privacy-boundary.md §8.4) together with the
// installer download the user asks for (§8.6) - and how to turn each off.
//
// The live values (oodle_mode, reads_game_executable) come from GetStatus, so
// the page states what this machine does rather than what the default is.
//
// Only the notes scroll; the title and upper-right close button stay put,
// because the page is taller than a 720 px window.
// ---------------------------------------------------------------------------
Dialog {
    id: dialog
    objectName: "disclosureDialog"

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 640
    height: Math.min(implicitHeight, (dialog.parent ? dialog.parent.height : implicitHeight) - 32)
    padding: 20
    closePolicy: Popup.CloseOnEscape

    background: DialogFrame {}

    enter: Transition {
        NumberAnimation { property: "opacity"; from: 0; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
        NumberAnimation { property: "scale"; from: 0.97; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
    }
    exit: Transition {
        NumberAnimation { property: "opacity"; from: 1; to: 0; duration: Theme.motionFast }
    }

    function openDialog() {
        notesView.contentY = 0
        open()
    }

    contentItem: ColumnLayout {
        spacing: 12

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 12

                CardKicker { text: qsTr("隐私说明") }

                HeadingLabel {
                    Layout.fillWidth: true
                    text: qsTr("这个软件做什么、不做什么")
                    font.pixelSize: Theme.dialogTitleSize(24)
                    wrapMode: Text.WordWrap
                }
            }

            AppButton {
                objectName: "disclosureCloseButton"
                Layout.alignment: Qt.AlignTop
                Layout.preferredWidth: 28
                Layout.preferredHeight: 28
                text: ""
                iconName: "x"
                compact: true
                variant: "ghost"
                Accessible.name: qsTr("关闭")
                onClicked: dialog.close()
                Keys.onReturnPressed: clicked()
                Keys.onEnterPressed: clicked()
            }
        }

        Flickable {
            id: notesView
            objectName: "disclosureNotes"
            Layout.fillWidth: true
            Layout.fillHeight: true
            Layout.preferredHeight: notes.implicitHeight
            Layout.minimumHeight: Math.min(notes.implicitHeight, 160)
            contentWidth: width
            contentHeight: notes.implicitHeight
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {
                policy: notesView.contentHeight > notesView.height ? ScrollBar.AlwaysOn : ScrollBar.AlwaysOff
            }

            ColumnLayout {
                id: notes
                // Always reserve the gutter, as every scrolling page does: gating it on
                // contentHeight would make the width depend on the height it produces.
                width: notesView.width - Theme.scrollGutter
                spacing: 12

                Component {
                    id: noteDelegate

                    ColumnLayout {
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

                Text {
                    text: qsTr("注意事项")
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(15)
                    font.bold: true
                }

                Repeater {
                    model: [
                        {
                            title: qsTr("只被动监听网卡流量"),
                            body: qsTr("通过 Npcap 读取本机网卡上的 FF14 流量。监听时不会向游戏服务器发送数据，也不会修改游戏流量。")
                        },
                        {
                            title: qsTr("会读取磁盘上的游戏程序副本"),
                            body: qsTr("解读 FF14 流量需要游戏自带的 Oodle 解压功能。软件会把磁盘上的游戏程序复制到临时目录，只在自己的后台服务中加载、查找解压功能。游戏文件不会随软件分发。停止监听时会删除副本；文件被占用或软件被强制结束时，留待下次启动清理。当前：%1，读取游戏程序 = %2。")
                                  .arg(App.oodleMode ? Fmt.oodleModeLabel(App.oodleMode) : qsTr("未知"))
                                  .arg(App.readsGameExecutable ? qsTr("是") : qsTr("否"))
                        },
                        {
                            title: qsTr("不注入、不读取游戏进程内存"),
                            body: qsTr("不会读取或改写游戏运行时的内存，也不会向游戏中注入代码、创建线程或安装拦截程序。")
                        },
                        {
                            title: qsTr("游戏记录保存在本机"),
                            body: qsTr("记录保存在本机数据库，界面与后台服务只在本机通信。没有遥测或云同步，下列联网请求不会上传数据库。")
                        },
                        {
                            title: qsTr("后台服务仅为以下三类用途联网"),
                            body: qsTr("界面本身不直接联网。后台服务只为下面三类用途发送请求，没有遥测。")
                        },
                        {
                            title: qsTr("联网一：游戏更新后获取共享校准（默认开启，可关）"),
                            body: qsTr("游戏更新后，如果还不能自动记录，软件会从固定的公开 GitHub 仓库下载其他玩家分享的校准，"
                                       + "根据你的游戏情况检查可用后再使用。如果正在使用共享校准，或暂时根据排队和进本来记录，"
                                       + "软件还会检查同一份公开列表，确认当前校准是否已被撤回，以及有没有更完整的校准；"
                                       + "其他已有可用校准的情况下不会为此联网。请求不含账号、安装编号或设备身份信息，"
                                       + "GitHub 与 CDN 能看到请求来自哪个 IP 地址。不想要可以在「设置 → 通用」关掉"
                                       + "「游戏更新后获取其他玩家的共享校准」。")
                        },
                        {
                            title: qsTr("联网二：在线语音（默认关闭）"),
                            body: qsTr("在「设置 → 播报」的「语音引擎」选择在线语音并填写自己的密钥后，在线播报需要联网："
                                       + "后台服务把要播报的文字发给所选服务（微软 Azure 语音，或你填写地址的 OpenAI 兼容服务），再播放返回的声音。"
                                       + "文字可能含副本名、进度和你在模板里填写的内容。服务方可看到你的 IP，并按自己的隐私政策处理文字。密钥由 Windows 加密保存在本机，仅用于所选服务。选回本机语音后停止在线播报请求，密钥仍保留。")
                        },
                        {
                            title: qsTr("联网三：检查更新，以及按你的操作下载新版本"),
                            body: qsTr("默认在启动软件时及持续运行期间定期检查更新，从本项目公开发布页读取版本信息，有新版本时在总览提示。"
                                       + "也可点「检查更新」主动检查。"
                                       + "点「下载安装包」只下载安装包，并核对发布时公布的校验值；"
                                       + "通过后还需点「立即安装」启动安装程序，本软件随后关闭，由安装程序完成更新。"
                                       + "请求不含账号、安装编号或设备身份信息，"
                                       + "发布平台及其下载服务能看到你的 IP 地址。"
                                       + "软件不会自行下载或安装。可在「设置 → 通用 → 更新」关闭「检查新版本并提示」，"
                                       + "关闭后「检查更新」也不可用，正在进行的下载会取消，已准备好的安装包会删除。")
                        }
                    ]
                    delegate: noteDelegate
                }

                Text {
                    Layout.topMargin: 4
                    text: qsTr("运行顺序")
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(15)
                    font.bold: true
                }

                Repeater {
                    model: [
                        {
                            title: qsTr("1. 先打开本软件，再启动游戏"),
                            body: qsTr("软件会在游戏启动后自动监听。需要从登录前开始读取游戏流量，"
                                       + "登录后传送或换区也无法补上，因此请先打开本软件再登录游戏。")
                        },
                        {
                            title: qsTr("2. 已经登录了？登出到标题画面，再登录一次"),
                            body: qsTr("不用关闭游戏。「捕获诊断」页也会提示你重新登录。")
                        },
                        {
                            title: qsTr("3. 正常排指导者任务，未能自动判定的结果到“待复核”里填写"),
                            body: qsTr("匹配、进入副本和离开副本会自动记录。收到通关结算时自动记为通关；"
                                       + "未收到结算就离开副本时，记录标为「待复核」。"
                                       + "打开记录，点「修正」填写通关或离开。")
                        }
                    ]
                    delegate: noteDelegate
                }
            }
        }

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            color: Theme.border
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("此说明可随时在「设置 → 关于」中点「查看软件说明」重新打开。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }
}
