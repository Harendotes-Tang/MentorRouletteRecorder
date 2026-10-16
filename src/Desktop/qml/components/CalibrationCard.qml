import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 本机校准（面向玩家，非维护者工具）：the prototype's calibration row, 本机校准
// (wide) beside 共享校准 (narrow). The sentence that says what the state means
// (CalibrationCopy) is spoken by the page's hero card; the card says it itself
// only when it stands alone (headlineInHero false), so a card loaded on its own
// still reads completely. The wide panel: a five-step progress line, the
// evidence times, the Collector's next step, the two buttons.
// 这里不出现任何编号、偏移或英文状态词。
ColumnLayout {
    id: card
    objectName: "calibrationCard"
    spacing: 12

    // The page's hero card already says the headline and its explanation.
    property bool headlineInHero: false

    signal confirmRequested()

    readonly property CalibrationCopy copy: CalibrationCopy {}
    readonly property var controller: App.calibration
    readonly property string calibrationState: card.copy.calibrationState
    readonly property var progress: card.controller ? card.controller.progress : ({})
    readonly property bool ready: card.copy.ready
    readonly property bool blocked: card.copy.blocked
    readonly property bool done: card.copy.done
    readonly property bool provisional: card.copy.provisional
    readonly property var shared: card.copy.shared
    readonly property bool sharedInUse: card.copy.sharedInUse
    readonly property bool sharedRecording: card.copy.sharedRecording
    readonly property bool sharedAuditPending: card.copy.sharedAuditPending
    readonly property bool recordingBesideOffer: card.copy.recordingBesideOffer
    // 抓包无输出时不再派发副本指示：此时打本也不会被记录，原因由本页的链路卡片说明。
    // blocked 与 silent 均表示当前无法记录，MIDSTREAM（接入已有连接）走 blocked 分支，
    // 故两者都要判断。
    readonly property bool captureSilent:
        App.recording ? (App.recording.silent || App.recording.blocked) : false

    // Evidence survives a new game connection. Compare wall clocks because the
    // session-relative clock starts over when the Collector reconnects.
    function latestEventTime(kind) {
        var events = card.controller ? card.controller.events : []
        var latest = null
        for (var i = 0; i < events.length; ++i) {
            if (events[i].kind !== kind || !events[i].at_utc)
                continue
            var at = new Date(events[i].at_utc)
            if (!isNaN(at.getTime()) && (latest === null || at > latest))
                latest = at
        }
        return latest
    }

    readonly property var lastLoginAt: latestEventTime("login")
    readonly property var lastDutyEntryAt: latestEventTime("duty_enter")
    readonly property bool reloggedSinceEntry: !!card.progress.duty_entry_seen
        && !card.progress.duty_exit_seen && card.lastLoginAt !== null
        && card.lastDutyEntryAt !== null && card.lastLoginAt > card.lastDutyEntryAt

    function evidenceTimes() {
        var lines = []
        if (card.lastLoginAt !== null)
            lines.push(qsTr("最近检测到登录：%1").arg(
                Qt.formatDateTime(card.lastLoginAt, "yyyy-MM-dd HH:mm:ss")))
        if (card.lastDutyEntryAt !== null)
            lines.push(qsTr("最近检测到进本：%1").arg(
                Qt.formatDateTime(card.lastDutyEntryAt, "yyyy-MM-dd HH:mm:ss")))
        return lines.join("\n")
    }

    function blockerMessage(sentence, index) {
        // Older Collectors describe a saved incomplete chain as a current duty.
        // Clarify only that known sentence; unrelated failure guidance stays intact.
        // The sentence is recognised by its catalogue key, which reads the same in
        // every interface language; a Collector that sends no key is recognised by
        // its Chinese, which is then what is on screen.
        const keys = card.controller && card.controller.blockerKeys ? card.controller.blockerKeys : []
        const key = index >= 0 && index < keys.length ? keys[index] : ""
        const finishDuty = key.length > 0 ? key === "calibration.blocker.finish_duty"
                                          : sentence === "打完这把副本，离开后再回来。"
        if (!finishDuty)
            return sentence
        return card.reloggedSinceEntry
            ? qsTr("你已重新登录，之前的校准进度还在。软件还没识别到上次副本的离开时间，"
                   + "之后正常进出副本时会继续校准。")
            : qsTr("软件还没识别到这次副本的离开时间。如果你已经出本，照常游戏即可，"
                   + "之后进出副本时会继续校准。")
    }

    // One step per piece of evidence. 契约中 progress 可以缺席（未测量），此时按"还没见到"。
    // nearKey 表示观察到相近证据但尚不成立，nearText 说明差在哪一步；这两个诊断字段
    // 均可缺席（旧 Collector），缺席时按“还没见到”显示。
    // 一次副本结束后各步不应全部显示“还没见到”：进本一步有了 duty_zone_seen
    // 即显示“已看到，还没能和这次排本对上”。
    // 职业报文对档案是可选的：未识别不影响校准完成，只是记录中职业为“未知”。
    // 措辞不使用“还没见到”——报文通常已收到，只是没有认出来。
    readonly property var progressRows: [
        { key: "finder_request_seen", label: qsTr("排本"), nearKey: "", nearText: "",
          seenText: qsTr("已识别"), unseenText: qsTr("等待识别") },
        { key: "pop_seen", label: card.provisional ? qsTr("匹配成功") : qsTr("匹配弹窗"),
          nearKey: "pop_shape_seen",
          nearText: card.provisional ? qsTr("继续识别中，记录照常进行") : qsTr("等待进本后确认"),
          seenText: qsTr("已识别"), unseenText: qsTr("等待识别") },
        { key: "duty_entry_seen", label: qsTr("进本"), nearKey: "duty_zone_seen",
          nearText: qsTr("已检测到进本，还没对上排队记录"),
          seenText: qsTr("已识别"), unseenText: qsTr("等待识别") },
        { key: "duty_exit_seen", label: qsTr("出本"), nearKey: "", nearText: "",
          seenText: qsTr("已识别"), unseenText: qsTr("等待识别") },
        { key: "job_seen", label: qsTr("职业"), nearKey: "", nearText: "",
          seenText: qsTr("已识别"), unseenText: qsTr("暂未识别，职业先记为未知") }
    ]
    // The four pieces of evidence a calibration needs; 职业 is optional and
    // counted on its own step, never in the fraction.
    readonly property var requiredKeys: ["finder_request_seen", "pop_seen", "duty_entry_seen", "duty_exit_seen"]
    readonly property int seenCount: {
        let count = 0
        for (let index = 0; index < card.requiredKeys.length; ++index) {
            if (card.progress[card.requiredKeys[index]])
                ++count
        }
        return count
    }
    readonly property bool progressMeasured: Object.keys(card.progress || ({})).length > 0

    // -- the headline, when the card stands alone ----------------------------
    ColumnLayout {
        Layout.fillWidth: true
        visible: !card.headlineInHero
        spacing: 4

        Text {
            objectName: "calibrationHeadline"
            Layout.fillWidth: true
            text: card.copy.headline()
            textFormat: Text.PlainText
            color: card.copy.recording ? Theme.green : Theme.textPrimary
            font.pixelSize: Theme.fs(14)
            font.bold: true
            wrapMode: Text.WordWrap
        }

        Text {
            objectName: "calibrationExplanation"
            Layout.fillWidth: true
            // 共享档案记录时，共享校准一节已给出说明；阻塞时「下一步」原样列出采集服务的句子。
            visible: !card.blocked && !card.sharedRecording
            text: card.copy.explanation()
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
    }

    // -- the row: 本机校准 (2) | 共享校准 (1); stacked when the card is narrow --
    GridLayout {
        id: row
        Layout.fillWidth: true
        readonly property bool twoColumns: card.width >= 640
        // The wide panel takes two thirds, the narrow one the rest; whichever
        // stands alone fills the row.
        readonly property int wideWidth: Math.round((card.width - row.columnSpacing) * 2 / 3)
        readonly property int narrowWidth: card.width - row.columnSpacing - row.wideWidth
        columns: row.twoColumns ? 2 : 1
        columnSpacing: 12
        rowSpacing: 12

        Card {
            id: local
            objectName: "calibrationLocalPanel"
            // 共享档案记录时，本机校准只在后台观察，不向玩家摊开进度。
            visible: !card.sharedRecording
            Layout.fillWidth: true
            Layout.preferredWidth: row.twoColumns ? row.wideWidth : card.width
            Layout.alignment: Qt.AlignTop
            horizontalPadding: 20
            verticalPadding: 16
            spacing: 12

            RowLayout {
                Layout.fillWidth: true
                spacing: 12

                CardKicker {
                    Layout.fillWidth: false
                    Layout.alignment: Qt.AlignTop
                    text: qsTr("本机校准")
                }

                Text {
                    objectName: "calibrationProgressContext"
                    Layout.fillWidth: true
                    text: card.progressMeasured
                          ? qsTr("已识别 %1 / %2 项 · 进度会保留，显示的是已识别的步骤，不是当前游戏状态")
                            .arg(card.seenCount).arg(card.requiredKeys.length)
                          : qsTr("退出游戏或重启软件不会清空进度。以下是已识别的步骤，不是当前游戏状态。")
                    textFormat: Text.PlainText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    wrapMode: Text.WordWrap
                }
            }

            // The five steps joined by lines; a line turns green once the step
            // before it is seen. Too narrow for a line of five, the steps stack.
            GridLayout {
                id: stepper
                objectName: "calibrationStepper"
                Layout.fillWidth: true
                readonly property bool vertical: width < 400
                columns: stepper.vertical ? 1 : 9
                rowSpacing: stepper.vertical ? 6 : 0
                columnSpacing: 0

                Repeater {
                    model: 9

                    delegate: Item {
                        id: cell
                        required property int index

                        readonly property bool isLine: (cell.index % 2) === 1
                        readonly property var step: cell.isLine ? null : card.progressRows[cell.index / 2]
                        readonly property bool seen: !!cell.step && !!card.progress[cell.step.key]
                        readonly property bool near: !!cell.step && !cell.seen
                                                     && cell.step.nearKey.length > 0
                                                     && !!card.progress[cell.step.nearKey]
                        readonly property bool previousSeen: cell.isLine
                            && !!card.progress[card.progressRows[(cell.index - 1) / 2].key]

                        objectName: cell.step ? "calibrationProgress_" + cell.step.key : "calibrationStepLine"
                        visible: !(cell.isLine && stepper.vertical)
                        Layout.fillWidth: cell.isLine || stepper.vertical
                        Layout.preferredWidth: cell.isLine ? 16 : (stepper.vertical ? -1 : 92)
                        Layout.minimumWidth: cell.isLine ? 8 : (stepper.vertical ? 0 : 72)
                        Layout.alignment: Qt.AlignTop
                        implicitWidth: cell.isLine ? 16 : 92
                        implicitHeight: cell.isLine ? 24 : stepBody.implicitHeight

                        Rectangle {
                            visible: cell.isLine
                            anchors.left: parent.left
                            anchors.right: parent.right
                            y: 11
                            height: 2
                            color: cell.previousSeen ? Theme.green : Theme.border
                        }

                        GridLayout {
                            id: stepBody
                            visible: !cell.isLine
                            anchors.left: parent.left
                            anchors.right: parent.right
                            anchors.top: parent.top
                            columns: stepper.vertical ? 3 : 1
                            rowSpacing: 4
                            columnSpacing: 8

                            Rectangle {
                                Layout.alignment: stepper.vertical ? (Qt.AlignLeft | Qt.AlignVCenter) : Qt.AlignHCenter
                                implicitWidth: 24
                                implicitHeight: 24
                                radius: 12
                                border.width: 2
                                border.color: cell.seen ? Theme.green
                                              : cell.near ? Theme.orange : Theme.neutral300
                                color: cell.seen ? Theme.green
                                       : cell.near ? Theme.orangeBackground : Theme.surface
                                Behavior on color { ColorAnimation { duration: Theme.motionControl } }

                                Text {
                                    anchors.centerIn: parent
                                    text: cell.seen ? "✓" : (cell.near ? "!" : "")
                                    color: cell.seen ? Theme.badgeForeground : Theme.orangeText
                                    font.pixelSize: 13
                                    font.bold: true
                                }
                            }

                            Text {
                                objectName: cell.step ? "calibrationProgressLabel_" + cell.step.key : ""
                                Layout.fillWidth: !stepper.vertical
                                Layout.alignment: stepper.vertical ? Qt.AlignVCenter : Qt.AlignHCenter
                                horizontalAlignment: stepper.vertical ? Text.AlignLeft : Text.AlignHCenter
                                text: cell.step ? cell.step.label : ""
                                textFormat: Text.PlainText
                                color: cell.seen || cell.near ? Theme.textPrimary : Theme.textSecondary
                                font.pixelSize: Theme.fs(13)
                                font.weight: cell.seen || cell.near ? Font.DemiBold : Font.Normal
                                wrapMode: Text.WordWrap
                            }

                            Text {
                                objectName: cell.step ? "calibrationProgressText_" + cell.step.key : ""
                                Layout.fillWidth: true
                                Layout.alignment: Qt.AlignVCenter
                                horizontalAlignment: stepper.vertical ? Text.AlignLeft : Text.AlignHCenter
                                text: !cell.step ? ""
                                      : cell.seen ? cell.step.seenText
                                      : cell.near ? cell.step.nearText
                                      : cell.step.unseenText
                                textFormat: Text.PlainText
                                color: cell.near ? Theme.orangeText : Theme.textSecondary
                                font.pixelSize: Theme.fs(12)
                                wrapMode: Text.WordWrap
                                Accessible.role: Accessible.StaticText
                                Accessible.name: cell.step ? cell.step.label + " · " + text : ""
                            }
                        }
                    }
                }
            }

            Text {
                objectName: "calibrationEvidenceTimes"
                Layout.fillWidth: true
                visible: text.length > 0
                text: card.evidenceTimes()
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            // 上面五步是累计证据，这里是接下来的操作。兼容旧 Collector 的未完成证据链提示，
            // 避免把保留的进本误述为玩家仍在副本中；其他阻塞说明原样显示。
            RowLayout {
                objectName: "calibrationBlockers"
                Layout.fillWidth: true
                spacing: 12
                visible: card.captureSilent || (card.controller && card.controller.blockers.length > 0)

                Text {
                    Layout.preferredWidth: 84
                    Layout.alignment: Qt.AlignTop
                    text: card.sharedRecording || card.recordingBesideOffer ? qsTr("接下来") : qsTr("下一步")
                    color: Theme.eorzea ? Theme.textSecondary : Theme.textMuted
                    font.pixelSize: Theme.fs(12)
                }

                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 4

                    Repeater {
                        model: card.captureSilent
                               ? [qsTr("暂时读不到游戏数据，无法继续校准，也不能自动记录。"
                                       + "请先按本页上方的连接状态提示处理。")]
                               : (card.controller ? card.controller.blockers : [])

                        delegate: Text {
                            id: blockerText
                            required property string modelData
                            required property int index

                            objectName: "calibrationBlockerText"
                            Layout.fillWidth: true
                            // The silent-capture line is this card's own, not a blocker.
                            text: card.blockerMessage(blockerText.modelData,
                                                      card.captureSilent ? -1 : blockerText.index)
                            textFormat: Text.PlainText
                            // A capture that decodes nothing is a fault; every other sentence
                            // is the next thing to do, in the text colour.
                            color: card.captureSilent ? Theme.orangeText : Theme.textPrimary
                            font.pixelSize: Theme.fs(12)
                            font.weight: card.captureSilent ? Font.Normal : Font.DemiBold
                            wrapMode: Text.WordWrap
                        }
                    }
                }
            }

            Text {
                objectName: "calibrationError"
                Layout.fillWidth: true
                visible: card.controller && card.controller.error.length > 0
                text: card.controller ? card.controller.error : ""
                textFormat: Text.PlainText
                color: Theme.orangeText
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            RowLayout {
                Layout.fillWidth: true
                visible: card.ready || !card.done
                spacing: 8

                AppButton {
                    objectName: "calibrationConfirmButton"
                    visible: card.ready
                    variant: "primary"
                    text: qsTr("核对并启用")
                    enabled: !!card.controller && !card.controller.busy
                    onClicked: card.confirmRequested()
                }

                AppButton {
                    objectName: "calibrationDiscardButton"
                    visible: !card.done
                    text: qsTr("清空校准进度")
                    enabled: !!card.controller && !card.controller.busy
                    onClicked: card.controller.discard()
                    Accessible.description: qsTr("清空尚未完成的校准进度，从头开始。已保存的记录和正在使用的校准不受影响。")
                }

                Item { Layout.fillWidth: true }
            }
        }

        SharedCalibrationSection {
            objectName: "calibrationSharedSection"
            Layout.fillWidth: true
            Layout.preferredWidth: row.twoColumns ? row.narrowWidth : card.width
            Layout.alignment: Qt.AlignTop
            headlineShownByCard: card.sharedRecording
            // 共享档案记录时本机校准面板不出现，采集服务说明「何时结束、对不上会怎样」的句子
            // 改在这里显示。
            retainingLines: card.sharedRecording && card.controller ? card.controller.blockers : []
        }
    }
}
