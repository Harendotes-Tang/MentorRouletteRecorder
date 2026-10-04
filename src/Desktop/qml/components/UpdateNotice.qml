import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 总览页顶部的新版本提示。
//
// 有没有新版本、安装程序和发布页在哪里，全部由后台进程判断并给出；这张卡片只把
// 结论说出来。「下载新版本」把安装程序的地址交给系统浏览器，由浏览器直接下载；
// 采集服务没有给出可用的安装程序地址时，改为打开发布页。「查看更新说明」打开发布页。
// 本软件自身不下载、不安装、也不替换任何文件，安装由用户自己运行。
// 「忽略此版本」只对这一个版本有效，更新到更新的版本后会再次提示。
Card {
    id: notice

    objectName: "updateNotice"

    Layout.fillWidth: true
    visible: App.update.updateAvailable && !App.update.dismissed

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

    Flow {
        objectName: "updateNoticeButtons"
        Layout.fillWidth: true
        Layout.topMargin: 2
        spacing: 8

        AppButton {
            objectName: "downloadInstallerButton"
            variant: "primary"
            text: qsTr("下载新版本")
            onClicked: App.update.openInstallerDownload()
        }

        AppButton {
            objectName: "dismissUpdateButton"
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
