import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 导入校准码：连不上 GitHub 时的兜底，粘贴其他玩家提供的校准码。
// 导入本身不联网，也不受"获取共享校准"开关影响；通过检查的码与下载所得一样，
// 须先在本机流量中核实。采集服务给出的中文说明原样显示，不作改写。
Dialog {
    id: dialog
    objectName: "sharedImportDialog"

    readonly property var shared: App.calibration ? App.calibration.shared : null
    property alias code: codeField.text
    property string messageText: ""
    // 每次打开自增，提交时记下当时的值：导入请求一旦上路就收不回来，回应到达时
    // 若窗口已经关掉或被重新打开，这条回应属于上一次导入，必须丢弃，不能把说明
    // 写进新一次的窗口里（审查第 9 条，与第 4 条同源）。只判"窗口是不是开着"
    // 并不能回答"还是不是同一次提交"，重开之后照样成立。
    property int openGeneration: 0
    property int submittedGeneration: 0
    readonly property bool busy: !!dialog.shared && dialog.shared.busy

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 520
    padding: 20
    // 请求在途时不接受 Esc，也不接受点击窗口外关闭——"导入"按钮早就用 busy
    // 禁用了，键盘这一路此前被漏掉。
    closePolicy: dialog.busy ? Popup.NoAutoClose : Popup.CloseOnEscape

    background: DialogFrame {}

    function openDialog() {
        codeField.text = ""
        dialog.messageText = ""
        ++dialog.openGeneration
        dialog.submittedGeneration = 0
        dialog.open()
        codeField.forceActiveFocus()
    }

    function submit() {
        if (!dialog.shared)
            return
        dialog.messageText = ""
        dialog.submittedGeneration = dialog.openGeneration
        dialog.shared.importCode(codeField.text)
    }

    /// 刚到的回应是否仍属于眼前这一次导入。
    function ownsReply() {
        return dialog.visible && dialog.submittedGeneration > 0
               && dialog.submittedGeneration === dialog.openGeneration
    }

    Connections {
        target: dialog.shared

        function onImportFinished(applied, message) {
            if (!dialog.ownsReply())
                return
            // 导入成功时同一句话会出现在底部提示里，卡片随后显示核实进度。
            if (applied)
                dialog.close()
            else
                dialog.messageText = message
        }
    }

    contentItem: ColumnLayout {
        spacing: 12

        HeadingLabel {
            Layout.fillWidth: true
            text: qsTr("导入校准码")
            font.pixelSize: Theme.dialogTitleSize(20)
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("把其他玩家发给你的校准码整段粘贴到下面。导入不联网：软件先检查它是不是这个游戏版本的，"
                       + "再和自动获取的一样在本机流量里核实，核实通过才会用来记录。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        StyledTextArea {
            id: codeField
            objectName: "sharedImportCodeField"
            Layout.fillWidth: true
            Layout.preferredHeight: 110
            placeholderText: qsTr("在这里粘贴校准码")
        }

        Text {
            objectName: "sharedImportMessage"
            Layout.fillWidth: true
            visible: dialog.messageText.length > 0
            text: dialog.messageText
            textFormat: Text.PlainText
            color: Theme.orangeText
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            Item { Layout.fillWidth: true }

            AppButton {
                text: qsTr("取消")
                onClicked: dialog.close()
            }

            AppButton {
                objectName: "sharedImportSubmit"
                variant: "primary"
                text: qsTr("导入")
                enabled: codeField.text.trim().length > 0 && !!dialog.shared && !dialog.busy
                onClicked: dialog.submit()
            }
        }
    }
}
