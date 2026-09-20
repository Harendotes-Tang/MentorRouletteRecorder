import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// ---------------------------------------------------------------------------
// 首次运行说明 (DEC-OODLE-01, docs/privacy-boundary.md §4.1, §8.2)
//
// Shown before the baseline dialog on a first run, and re-openable from the
// settings page. The UI must state that the default decompressor reads a copy
// of the game executable, and must name every kind of network request the
// Collector can send - the shared-calibration download (on by default), online
// speech (opt-in, docs/privacy-boundary.md §8.3) and the update check (on by
// default, docs/privacy-boundary.md §8.4) - together with how to turn each off.
// Online speech does not raise the disclosure version: it is opt-in and asks
// for its own confirmation when chosen. The update check does, because the
// previous text ruled it out in so many words.
//
// The live values (oodle_mode, reads_game_executable) come from GetStatus, so
// the page states what this machine does rather than what the default is.
//
// Only the notes scroll; the acknowledgement stays put, because the page is
// taller than a 720 px window and a button below the edge cannot be pressed.
// ---------------------------------------------------------------------------
Dialog {
    id: dialog
    objectName: "disclosureDialog"

    signal acknowledged()

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 640
    height: Math.min(implicitHeight, (dialog.parent ? dialog.parent.height : implicitHeight) - 32)
    padding: 20
    closePolicy: Popup.NoAutoClose

    background: DialogFrame {}

    enter: Transition {
        NumberAnimation { property: "opacity"; from: 0; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
        NumberAnimation { property: "scale"; from: 0.97; to: 1; duration: Theme.motionMedium; easing.type: Easing.OutCubic }
    }
    exit: Transition {
        NumberAnimation { property: "opacity"; from: 1; to: 0; duration: Theme.motionFast }
    }

    property bool understood: false

    function openDialog() {
        understood = false
        notesView.contentY = 0
        open()
    }

    contentItem: ColumnLayout {
        spacing: 12

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

                CardKicker { text: qsTr("首次运行说明 · 请先读完") }

                HeadingLabel {
                    Layout.fillWidth: true
                    text: qsTr("这个软件做什么、不做什么")
                    font.pixelSize: Theme.dialogTitleSize(24)
                    wrapMode: Text.WordWrap
                }

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
                            body: qsTr("通过 Npcap 驱动被动读取本机网卡上的 FF14 流量。不发送任何数据包、不修改流量、不与游戏服务器交互。")
                        },
                        {
                            title: qsTr("会读取游戏可执行文件的一份副本"),
                            body: qsTr("FF14 的流量使用 Oodle 压缩，解压需要游戏自带的解压函数。本软件把磁盘上的游戏可执行文件复制到临时目录，只在自己的进程里加载这份副本做特征扫描；不复制也不分发游戏二进制的任何部分。副本由本软件负责删除：停止监听时删，删不掉的（文件仍被占用、软件被强制结束）登记在清单里、下次启动时删。当前：%1，读取游戏可执行文件 = %2。")
                                  .arg(App.oodleMode || qsTr("未知"))
                                  .arg(App.readsGameExecutable ? qsTr("是") : qsTr("否"))
                        },
                        {
                            title: qsTr("不注入、不读取游戏进程内存"),
                            body: qsTr("不打开游戏进程句柄、不读写它的内存、不创建远程线程、不安装任何注入式 hook。")
                        },
                        {
                            title: qsTr("数据只留在本机"),
                            body: qsTr("记录写入本机数据库，桌面端与后台进程只通过本地管道通信。没有遥测、没有云同步，从不上传任何数据。")
                        },
                        {
                            title: qsTr("只有三种联网，都由后台进程发出"),
                            body: qsTr("界面本身从不联网。后台进程只会发出下面三种请求，除此之外没有遥测，也不上传任何数据。")
                        },
                        {
                            title: qsTr("联网一：游戏更新后获取共享校准（默认开启，可关）"),
                            body: qsTr("游戏更新后本机没有可用档案时，后台进程会从一个固定的公开 GitHub 仓库只读下载其他玩家分享的校准，"
                                       + "先在本机流量里核实，通过才用来记录。正在使用的校准本身来自其他玩家、或者只是按排本推断出来的时候，"
                                       + "登录后还会再读一次同一份公开列表，用来发现它已被撤回、或者有了更准的一份；"
                                       + "其余情况下已经有可用档案就不会联网。请求里不带账号、安装编号或任何能认出你的信息，"
                                       + "GitHub 与 CDN 能看到请求来自哪个 IP 地址。不想要可以在「设置 → 通用」关掉"
                                       + "「游戏更新后获取其他玩家的共享校准」。")
                        },
                        {
                            title: qsTr("联网二：在线语音（默认关闭）"),
                            body: qsTr("只有你在「设置 → 播报」把语音引擎选成在线语音、并填了自己的密钥之后才会发生：每次播报时，"
                                       + "后台进程把这一句要念的话（可能含副本名与进度数字）发给你选的语音服务（微软 Azure 语音，或你填写地址的 OpenAI 兼容服务），"
                                       + "取回读音在本机播放。密钥只保存在本机并由 Windows 加密。改回本机语音就不再发送。")
                        },
                        {
                            title: qsTr("联网三：检查新版本（默认开启，可关）"),
                            body: qsTr("每天最多一次，后台进程从本项目公开的 GitHub 发布页读取一个只含版本号的小文件，"
                                       + "有新版本时在总览页提示一句。请求里不带账号、安装编号或任何能认出你的信息，"
                                       + "GitHub 与 CDN 能看到请求来自哪个 IP 地址。本软件从不自动下载、也从不安装任何东西："
                                       + "要不要更新、什么时候更新都由你自己决定。不想要可以在「设置 → 通用 → 更新」关掉"
                                       + "「检查新版本并提示」。")
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
                            body: qsTr("游戏一启动就会自动开始监听，登录前不需要任何操作。国服登录后传送、换区都不重连，"
                                       + "而解压必须从连接开头跟踪，所以登录之后才打开本软件是来不及的。")
                        },
                        {
                            title: qsTr("2. 已经登录了？登出到标题画面，再登录一次"),
                            body: qsTr("不用关闭游戏。这种情况下捕获诊断页也会提示。")
                        },
                        {
                            title: qsTr("3. 正常排指导者任务，打完后到“待复核”里填结果"),
                            body: qsTr("匹配、进入副本、离开副本会自动记录。是否通关目前还不能自动判定：离开副本后记录会标为“待复核”，"
                                       + "打开记录用“修正”填上通关或离开即可。")
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

        RowLayout {
            Layout.fillWidth: true
            spacing: 10

            ToggleSwitch {
                id: understoodSwitch
                checked: dialog.understood
                onToggled: function(value) { dialog.understood = value }
            }

            Text {
                Layout.fillWidth: true
                text: qsTr("我已阅读并理解上述说明，特别是「会读取游戏可执行文件的一份副本」这一条。")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 10

            Text {
                Layout.fillWidth: true
                text: qsTr("此说明可随时在「设置 → 隐私与边界」重新打开。")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                wrapMode: Text.WordWrap
            }

            AppButton {
                variant: "primary"
                text: qsTr("我已了解")
                enabled: dialog.understood
                onClicked: dialog.acknowledged()
            }
        }
    }
}
