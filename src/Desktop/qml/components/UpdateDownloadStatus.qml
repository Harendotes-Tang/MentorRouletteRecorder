import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 软件内下载新版本的进度与结论，总览页的更新提示、设置 · 通用与设置 · 关于三处共用
// （docs/privacy-boundary.md §8.6）。下载由后台进程完成，这里只显示它报告的状态：
// 下载中的进度（已知大小时为百分比，否则为已下载的兆字节数）、下载完成并核对无误后的
// 一句提示、失败时后台进程给出的原因，以及「立即安装」没有启动安装程序时的原因。
// 下载失败、或安装程序没有启动时，「在浏览器中下载」仍把安装程序的地址交给系统浏览器。
// 按钮本身（下载并安装 / 取消 / 立即安装 / 重试）在各处原有的位置。
ColumnLayout {
    id: area

    /// objectName 前缀，用来区分三处。
    property string namePrefix: "update"
    readonly property string phase: App.update.downloadPhase

    spacing: 6
    visible: statusLine.text.length > 0 || problemLine.visible || browserButton.visible

    StatBar {
        objectName: area.namePrefix + "DownloadProgress"
        Layout.fillWidth: true
        Layout.topMargin: 2
        // 大小未知时没有比例可画，只写已下载的兆字节数。
        visible: (area.phase === "downloading" && App.update.downloadProgress >= 0)
                 || area.phase === "verifying"
        ratio: area.phase === "verifying" ? 1 : Math.max(0, App.update.downloadProgress)
    }

    Text {
        id: statusLine

        objectName: area.namePrefix + "DownloadStatusText"
        Layout.fillWidth: true
        visible: text.length > 0
        text: App.update.downloadStatusText
        color: area.phase === "failed" ? Theme.orangeText : Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        lineHeight: 1.35
        wrapMode: Text.WordWrap
    }

    Text {
        id: problemLine

        objectName: area.namePrefix + "InstallProblemText"
        Layout.fillWidth: true
        visible: area.phase === "ready" && App.update.installProblem.length > 0
        text: App.update.installProblem
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        lineHeight: 1.35
        wrapMode: Text.WordWrap
    }

    AppButton {
        id: browserButton

        objectName: area.namePrefix + "BrowserDownloadButton"
        Layout.fillWidth: false
        visible: App.update.browserFallbackOffered
        compact: true
        variant: "ghost"
        text: qsTr("在浏览器中下载")
        onClicked: App.update.openInstallerDownload()
    }
}
