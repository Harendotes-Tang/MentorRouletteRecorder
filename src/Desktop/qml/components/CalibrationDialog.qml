import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 核对对话框：展示推断出来的时间线，一行一句话（时间 + 这一刻发生了什么）。
// 需要核对的每一行给"对 / 错"两个按钮，全部点过才能启用。带轮盘名的行点"错"记为
// RELABEL（改名，不作废）；只有"这个时间我根本没排本"记为 WRONG，令本次校准作废。
// 行文里只有时间、副本名和轮盘名，没有编号，也没有英文状态词。
Dialog {
    id: dialog
    objectName: "calibrationDialog"

    readonly property var controller: App.calibration
    // 事件 id → { verdict: "CORRECT" / "WRONG" / "RELABEL", roulette_name: "…" }。
    // 每次都整体替换，绑定才会重算。
    property var verdicts: ({})
    property string noticeText: ""
    // 每次打开自增，提交时记下当时的值：核对请求一旦上路就收不回来，回应到达时
    // 若窗口已经关掉或被重新打开，这条回应属于上一轮核对，必须丢弃，不能把新开
    // 的窗口关掉（审查第 9 条，与第 4 条同源）。
    property int openGeneration: 0
    property int submittedGeneration: 0
    readonly property bool busy: !!dialog.controller && dialog.controller.busy

    // 玩家点"错"后用于指认这一把实际排的随机任务。
    //
    // 名称写死于此而非取自 Collector 的名称表：需要用到该功能，正是因为那张表可能
    // 有误，用有误的表填充改正用的下拉框将无法得到正确答案。此处为游戏内的任务列表。
    readonly property var rouletteChoices: [
        qsTr("顶级迷宫"), qsTr("满级迷宫"), qsTr("拾级迷宫"), qsTr("练级迷宫"),
        qsTr("讨伐歼灭战"), qsTr("主线任务"), qsTr("行会令"), qsTr("团队任务"),
        qsTr("大型任务"), qsTr("指导者任务"), qsTr("纷争前线"), qsTr("高难度任务")
    ]

    readonly property var timeline: dialog.controller ? dialog.controller.events : []
    readonly property bool allAnswered: {
        const answers = dialog.verdicts
        let pending = 0
        for (const event of dialog.timeline) {
            if (!event.requires_confirmation)
                continue
            ++pending
            const answer = answers[event.event_id]
            if (!answer || !answer.verdict)
                return false
            if (answer.verdict === "RELABEL" && !answer.roulette_name)
                return false
        }
        return pending > 0
    }

    function answerOf(eventId) {
        const answer = dialog.verdicts[eventId]
        return answer && answer.verdict ? answer.verdict : ""
    }

    modal: true
    // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }
    width: 560
    // 时间线可能比窗口还高（一上午的登录、换区与多次排本）：对话框不超出窗口，
    // 时间线在内部滚动，底部两个按钮始终可见。
    height: Math.min(implicitHeight, (dialog.parent ? dialog.parent.height : implicitHeight) - 32)
    padding: 20
    // 请求在途时不接受 Esc，也不接受点击窗口外关闭——"核对并启用"按钮早就用
    // busy 禁用了，键盘这一路此前被漏掉。
    closePolicy: dialog.busy ? Popup.NoAutoClose : Popup.CloseOnEscape

    background: DialogFrame {}

    function openDialog() {
        dialog.verdicts = ({})
        dialog.noticeText = ""
        ++dialog.openGeneration
        dialog.submittedGeneration = 0
        timelineView.contentY = 0
        dialog.open()
    }

    function setVerdict(eventId, verdict, rouletteName) {
        const next = {}
        for (const key in dialog.verdicts)
            next[key] = dialog.verdicts[key]
        next[eventId] = { verdict: verdict, roulette_name: rouletteName || "" }
        dialog.verdicts = next
    }

    function submit() {
        if (!dialog.allAnswered)
            return
        dialog.noticeText = ""
        dialog.submittedGeneration = dialog.openGeneration
        dialog.controller.confirm(dialog.verdicts)
    }

    /// 刚到的回应是否仍属于眼前这一轮核对。
    function ownsReply() {
        return dialog.visible && dialog.submittedGeneration > 0
               && dialog.submittedGeneration === dialog.openGeneration
    }

    Connections {
        target: dialog.controller

        function onConfirmed(profileId, boundInSession) {
            if (!dialog.ownsReply())
                return
            dialog.noticeText = ""
            dialog.close()
        }
        function onRejected(message) {
            if (!dialog.ownsReply())
                return
            // 关闭前先留下说明：同一句话会继续显示在捕获页的卡片上。
            dialog.noticeText = message
            dialog.close()
        }
    }

    contentItem: ColumnLayout {
        spacing: 12

        HeadingLabel {
            Layout.fillWidth: true
            text: qsTr("核对这几件事")
            font.pixelSize: Theme.dialogTitleSize(20)
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("下面是本软件从这次游戏里认出来的时间线。和你实际打的那一把对得上就点“对”。"
                       + "对不上就点“错”，接着会问你这一把实际排的是哪个随机任务——"
                       + "只是名字写错了的话，选一下就好，校准不会作废；"
                       + "只有你说“这个时间我根本没排本”，这次校准才会作废重来。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Flickable {
            id: timelineView
            objectName: "calibrationDialogTimelineView"
            Layout.fillWidth: true
            Layout.fillHeight: true
            Layout.preferredHeight: timelineRows.implicitHeight
            Layout.minimumHeight: Math.min(timelineRows.implicitHeight, 160)
            contentWidth: width
            contentHeight: timelineRows.implicitHeight
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {
                policy: timelineView.contentHeight > timelineView.height ? ScrollBar.AlwaysOn : ScrollBar.AlwaysOff
            }

        ColumnLayout {
            id: timelineRows
            objectName: "calibrationDialogTimeline"
            // 与各滚动页一致，始终预留滚动条的位置：宽度不随高度变化。
            width: timelineView.width - Theme.scrollGutter
            spacing: 6

            Repeater {
                model: dialog.timeline

                delegate: ColumnLayout {
                    id: eventRow
                    required property var modelData

                    readonly property bool asks: !!eventRow.modelData.requires_confirmation
                    readonly property string answer: dialog.answerOf(eventRow.modelData.event_id)
                    // 只有带随机任务编号的行才能改名字：没有编号就没有地方安放这个改正。
                    readonly property bool nameable:
                        eventRow.asks && eventRow.modelData.roulette_id !== undefined
                        && eventRow.modelData.roulette_id !== null

                    objectName: eventRow.asks
                                ? "calibrationVerdict_" + eventRow.modelData.event_id
                                : "calibrationEvent_" + eventRow.modelData.event_id
                    Layout.fillWidth: true
                    spacing: 4

                    RowLayout {
                    Layout.fillWidth: true
                    spacing: 10

                    Text {
                        text: eventRow.modelData.time_text || ""
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                    }

                    Text {
                        Layout.fillWidth: true
                        text: eventRow.modelData.label || ""
                        textFormat: Text.PlainText
                        // 登录与普通换区仅作时间参照，无需玩家判断，故用次要色。
                        color: eventRow.asks ? Theme.textPrimary : Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                        wrapMode: Text.WordWrap
                    }

                    AppButton {
                        objectName: "calibrationVerdictCorrect_" + eventRow.modelData.event_id
                        visible: eventRow.asks
                        compact: true
                        variant: eventRow.answer === "CORRECT" ? "primary" : "secondary"
                        text: qsTr("对")
                        onClicked: dialog.setVerdict(eventRow.modelData.event_id, "CORRECT")
                    }

                    AppButton {
                        objectName: "calibrationVerdictWrong_" + eventRow.modelData.event_id
                        visible: eventRow.asks
                        compact: true
                        variant: eventRow.answer === "WRONG" || eventRow.answer === "RELABEL"
                                 ? "primary" : "secondary"
                        text: qsTr("错")
                        onClicked: dialog.setVerdict(
                            eventRow.modelData.event_id,
                            eventRow.nameable ? "RELABEL" : "WRONG")
                    }
                    }

                    // 点"错"后追问实际排的是哪一项：选名称仅为改名，校准继续；
                    // 选最后一项才否定该条。
                    RowLayout {
                        objectName: "calibrationRelabel_" + eventRow.modelData.event_id
                        Layout.fillWidth: true
                        Layout.leftMargin: 46
                        spacing: 8
                        visible: eventRow.nameable
                                 && (eventRow.answer === "RELABEL" || eventRow.answer === "WRONG")

                        Text {
                            text: qsTr("那这一把实际排的是")
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                        }

                        StyledComboBox {
                            objectName: "calibrationRelabelChoice_" + eventRow.modelData.event_id
                            Layout.preferredWidth: 190
                            model: dialog.rouletteChoices.concat(
                                [qsTr("这个时间我根本没排本")])
                            currentIndex: {
                                const answer = dialog.verdicts[eventRow.modelData.event_id]
                                if (!answer || answer.verdict !== "RELABEL")
                                    return dialog.rouletteChoices.length
                                const at = dialog.rouletteChoices.indexOf(answer.roulette_name)
                                return at < 0 ? 0 : at
                            }
                            onActivated: (index) => {
                                if (index >= dialog.rouletteChoices.length)
                                    dialog.setVerdict(eventRow.modelData.event_id, "WRONG")
                                else
                                    dialog.setVerdict(eventRow.modelData.event_id, "RELABEL",
                                                      dialog.rouletteChoices[index])
                            }
                        }
                    }
                }
            }
        }
        }

        Text {
            objectName: "calibrationDialogNotice"
            Layout.fillWidth: true
            visible: dialog.noticeText.length > 0
                     || (!!dialog.controller && dialog.controller.error.length > 0)
            text: dialog.noticeText.length > 0
                  ? dialog.noticeText
                  : (dialog.controller ? dialog.controller.error : "")
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
                text: qsTr("以后再说")
                onClicked: dialog.close()
            }

            AppButton {
                objectName: "calibrationDialogConfirm"
                variant: "primary"
                text: qsTr("核对并启用")
                enabled: dialog.allAnswered && !!dialog.controller && !dialog.busy
                onClicked: dialog.submit()
            }
        }
    }
}
