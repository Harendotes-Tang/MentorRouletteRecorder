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
    // The chosen name is saved as the Collector's roulette-name override, so each
    // value stays the CN game name; only the label in the box is translated.
    readonly property var rouletteChoices: [
        QT_TRANSLATE_NOOP("CalibrationDialog", "顶级迷宫"), QT_TRANSLATE_NOOP("CalibrationDialog", "满级迷宫"),
        QT_TRANSLATE_NOOP("CalibrationDialog", "拾级迷宫"), QT_TRANSLATE_NOOP("CalibrationDialog", "练级迷宫"),
        QT_TRANSLATE_NOOP("CalibrationDialog", "讨伐歼灭战"), QT_TRANSLATE_NOOP("CalibrationDialog", "主线任务"),
        QT_TRANSLATE_NOOP("CalibrationDialog", "行会令"), QT_TRANSLATE_NOOP("CalibrationDialog", "团队任务"),
        QT_TRANSLATE_NOOP("CalibrationDialog", "大型任务"), QT_TRANSLATE_NOOP("CalibrationDialog", "指导者任务"),
        QT_TRANSLATE_NOOP("CalibrationDialog", "纷争前线"), QT_TRANSLATE_NOOP("CalibrationDialog", "高难度任务")
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
    // 点了“错”、还没选出正确名称的行：核对并启用为何不能点，写在按钮旁边（审查 K2-3）。
    readonly property bool namePending: {
        const answers = dialog.verdicts
        for (const event of dialog.timeline) {
            const answer = event.requires_confirmation ? answers[event.event_id] : null
            if (answer && answer.verdict === "RELABEL" && !answer.roulette_name)
                return true
        }
        return false
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
            // 采集服务没能当场换上新档案（这一把正在进行，或还没开始监听）：它从下一把
            // 起才生效，必须如实告诉玩家，而不是让人以为这一把已按它记录（审查 CS3b-X2）。
            if (!boundInSession && typeof App !== "undefined" && App.showToast)
                App.showToast(qsTr("校准已保存，从下一次副本开始使用。本次副本的记录方式不变。"))
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
            text: qsTr("请检查下面的时间和任务是否与你实际游戏的情况一致。每条需要核对的内容，都要点“对”或“错”。"
                       + "随机任务名称不对时，点“错”后选出正确名称即可，校准进度会保留。"
                       + "如果你当时没有排本，选择“这个时间我没有排本”；"
                       + "其他无法改名的内容如果点“错”，本次校准也会重新开始。")
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
                            text: qsTr("你当时排的是")
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                        }

                        StyledComboBox {
                            objectName: "calibrationRelabelChoice_" + eventRow.modelData.event_id
                            Layout.preferredWidth: 190
                            // The game's own name in the interface language (Theme.rouletteName);
                            // one the bundled table lacks falls back to this dialog's translation.
                            model: dialog.rouletteChoices.map(function(name) {
                                const official = Theme.rouletteName(name)
                                return official !== name ? official : qsTranslate("CalibrationDialog", name)
                            }).concat([qsTr("这个时间我没有排本")])
                            // 刚点“错”、还没选名称时不预选任何一项：预选第一项看上去像已经选好，
                            // 而选中它又不会触发改名（审查 K2-3）。
                            displayText: currentIndex < 0 ? qsTr("请选择…") : currentText
                            currentIndex: {
                                const answer = dialog.verdicts[eventRow.modelData.event_id]
                                if (!answer || answer.verdict !== "RELABEL")
                                    return Theme.live(dialog.rouletteChoices.length)
                                return Theme.live(dialog.rouletteChoices.indexOf(answer.roulette_name))
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

            Text {
                objectName: "calibrationDialogNameHint"
                Layout.fillWidth: true
                visible: dialog.namePending
                text: qsTr("请在“你当时排的是”里选出实际排的随机任务。")
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            Item {
                Layout.fillWidth: true
                visible: !dialog.namePending
            }

            AppButton {
                objectName: "calibrationDialogLater"
                text: qsTr("以后再说")
                // Like Esc: not while the confirmation is out (review OL-10).
                enabled: !dialog.busy
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
