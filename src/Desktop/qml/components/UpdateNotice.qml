import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 总览页顶部的新版本提示。
//
// 有没有新版本、安装程序和发布页在哪里，全部由后台进程判断并给出；这张卡片只把
// 结论说出来。主按钮随下载的状态变化（docs/privacy-boundary.md §8.6）：「下载并安装」
// 请后台进程下载安装程序并核对发布时公布的校验值；下载中是「取消」；下载完成后是
// 「立即安装」，由本软件再次核对后启动安装程序并退出；失败后是「重试」，旁边另有
// 「在浏览器中下载」。不支持软件内下载的后台进程仍是原来的「下载新版本」，由系统
// 浏览器下载。「查看更新说明」打开发布页。
// 「忽略此版本」只对这一个版本有效，更新到更新的版本后会再次提示；已经开始的下载
// 不会因此从总览页消失。
Card {
    id: notice

    objectName: "updateNotice"

    Layout.fillWidth: true
    visible: App.update.updateAvailable && (!App.update.dismissed || App.update.downloadEngaged)

    CardKicker {
        Layout.fillWidth: true
        text: qsTr("更新")
    }

    Text {
        objectName: "updateNoticeHeadline"
        Layout.fillWidth: true
        text: App.update.headline
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(14)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    Text {
        objectName: "updateNoticeDetail"
        Layout.fillWidth: true
        text: App.update.detail
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        lineHeight: 1.35
        wrapMode: Text.WordWrap
    }

    UpdateDownloadStatus {
        namePrefix: "updateNotice"
        Layout.fillWidth: true
    }

    Flow {
        objectName: "updateNoticeButtons"
        Layout.fillWidth: true
        Layout.topMargin: 2
        spacing: 8

        AppButton {
            objectName: "downloadInstallerButton"
            readonly property bool cancels: App.update.downloadPhase === "downloading"
                                            || App.update.downloadPhase === "verifying"
            visible: text.length > 0
            variant: cancels ? "secondary" : "primary"
            text: App.update.downloadActionText
            enabled: App.update.downloadActionEnabled
            onClicked: App.update.downloadAction()
        }

        AppButton {
            objectName: "dismissUpdateButton"
            visible: !App.update.downloadEngaged
            text: qsTr("忽略此版本")
            onClicked: App.update.dismiss()
        }

        AppButton {
            objectName: "openReleasePageButton"
            variant: "ghost"
            text: qsTr("查看更新说明")
            onClicked: App.update.openReleasePage()
        }
    }
}
