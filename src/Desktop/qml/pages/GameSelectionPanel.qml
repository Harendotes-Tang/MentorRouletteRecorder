import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Card {
    id: panel
    objectName: "gameSelectionPanel"
    readonly property var selection: App.gameSelection
    readonly property bool waitingForRestart: App.validationActive
        && String(App.validationStatus.reason || "") === "WAITING_RESTART"
    Layout.fillWidth: true
    visible: selection.selectionRequired || selection.currentLabel.length > 0 || selection.picking
    horizontalPadding: 24
    verticalPadding: 18
    spacing: 10

    CardKicker { text: qsTr("记录对象") }

    Text {
        objectName: "gameSelectionStatusText"
        Layout.fillWidth: true
        text: panel.waitingForRestart ? qsTr("验证正在等待所选游戏重启")
              : panel.selection.selectionRequired ? panel.selection.selectionMessage
              : qsTr("已锁定：%1").arg(panel.selection.currentLabel)
        textFormat: Text.PlainText
        wrapMode: Text.WordWrap
        color: panel.selection.selectionRequired ? Theme.orangeText : Theme.textPrimary
        font.pixelSize: Theme.fs(14)
        Accessible.role: Accessible.StaticText
        Accessible.name: text
    }

    Text {
        Layout.fillWidth: true
        text: qsTr("切换前台窗口不影响记录。更换记录对象会结束当前采集；进行中的导随将按中断处理。")
        textFormat: Text.PlainText
        wrapMode: Text.WordWrap
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
    }

    Flow {
        Layout.fillWidth: true
        spacing: 8
        AppButton {
            objectName: "pickGameWindowButton"
            text: panel.selection.picking ? qsTr("取消选择") : qsTr("点选游戏窗口")
            variant: "primary"
            enabled: App.backendConnected && !panel.selection.busy && !App.validationActive
            onClicked: panel.selection.picking ? panel.selection.cancelPick() : panel.selection.beginPick()
        }
        StyledComboBox {
            id: gameList
            objectName: "gameWindowList"
            width: Math.min(260, panel.width - 48)
            model: panel.selection.choices
            textRole: "label"
            currentIndex: -1
            displayText: currentIndex < 0 ? qsTr("也可从列表选择游戏") : currentText
            enabled: !panel.selection.busy && !panel.selection.picking && !App.validationActive
            Accessible.name: qsTr("要记录的游戏窗口")
            onModelChanged: currentIndex = -1
        }
        AppButton {
            objectName: "selectListedGameButton"
            text: qsTr("记录此窗口")
            enabled: App.backendConnected && gameList.currentIndex >= 0
                     && !panel.selection.busy && !panel.selection.picking && !App.validationActive
            onClicked: panel.selection.select(gameList.currentIndex)
        }
    }

    Text {
        Layout.fillWidth: true
        visible: panel.selection.message.length > 0 || App.validationActive
        text: panel.waitingForRestart
              ? qsTr("重新启动同一安装目录的游戏，验证会自动接续；原本打开的其他游戏不会被选中。")
              : App.validationActive ? qsTr("请先停止采集验证，再选择游戏窗口。") : panel.selection.message
        textFormat: Text.PlainText
        wrapMode: Text.WordWrap
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        Accessible.role: Accessible.StaticText
        Accessible.name: text
    }
}
