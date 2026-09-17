import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 总览页顶部的新版本提示。
//
// 有没有新版本、下载页在哪里，全部由后台进程判断并给出；这张卡片只把结论说出来。
// 「打开下载页」把地址交给系统浏览器，下载和安装由用户自己完成；本软件不会替换
// 任何文件。「忽略此版本」只对这一个版本有效，更新到更新的版本后会再次提示。
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
            objectName: "openReleasePageButton"
            variant: "primary"
            text: qsTr("打开下载页")
            onClicked: App.update.openReleasePage()
        }

        AppButton {
            objectName: "dismissUpdateButton"
            text: qsTr("忽略此版本")
            onClicked: App.update.dismiss()
        }
    }
}
