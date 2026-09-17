import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 本机校准卡片（面向玩家，非维护者工具）：游戏更新后说明当前为何没有记录、
// 以及还需要做什么。五行进度、Collector 给的阻塞说明、两个按钮。
// 这里不出现任何编号、偏移或英文状态词。
Card {
    id: card
    objectName: "calibrationCard"
    padding: 16
    spacing: 10

    readonly property var controller: App.calibration
    // Item.state 为 QML 内置属性，契约状态改用此名承载。
    readonly property string calibrationState: card.controller ? card.controller.state : "IDLE"
    readonly property var progress: card.controller ? card.controller.progress : ({})
    readonly property bool ready: card.calibrationState === "READY"
    readonly property bool blocked: card.calibrationState === "BLOCKED"
    readonly property bool done: card.calibrationState === "DONE"
    // 已按本机档案记录，但依据是"申请了哪个随机任务"而非服务器匹配报文，后台仍在
    // 查找该报文；卡片须明确说明当前已可正常记录。
    readonly property bool provisional: card.controller ? card.controller.provisional : false
    // 共享档案已在记录时，本机校准仍处于观察状态，此时不得提示"正在重新校准、
    // 期间不会生成记录"。
    readonly property var shared: card.controller ? card.controller.shared : null
    readonly property bool sharedInUse: !!card.shared && card.shared.inUse
    readonly property bool sharedRecording: card.sharedInUse && !card.provisional
    // 已按登录时的核实开始记录，排本与进本仍在核对（plan §18.4）：标题不得说成"本机已核实"，
    // 说明由共享校准一节的灰字给出。旧采集服务不报这一项，按"未报告"处理。
    readonly property bool sharedAuditPending: !!card.shared && card.shared.auditPending
    // 抓包无输出时不再派发副本指示：此时打本也不会被记录，原因由本页另一张卡片说明。
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
            lines.push(qsTr("最近识别登录：%1").arg(
                Qt.formatDateTime(card.lastLoginAt, "yyyy-MM-dd HH:mm:ss")))
        if (card.lastDutyEntryAt !== null)
            lines.push(qsTr("已采到的进本：%1").arg(
                Qt.formatDateTime(card.lastDutyEntryAt, "yyyy-MM-dd HH:mm:ss")))
        return lines.join("\n")
    }

    function blockerMessage(sentence) {
        // Older Collectors describe a saved incomplete chain as a current duty.
        // Clarify only that known sentence; unrelated failure guidance stays intact.
        if (sentence !== "打完这把副本，离开后再回来。")
            return sentence
        return card.reloggedSinceEntry
            ? qsTr("已识别重新登录，之前的进度已保留。上次进本还缺少对应的出本证据；"
                   + "之后正常进出副本时会继续校准。")
            : qsTr("仍缺少与进本对应的出本证据。如果你已经离开副本，照常游戏即可；"
                   + "之后进出副本时会继续校准。")
    }

    signal confirmRequested()

    function headline() {
        if (card.sharedRecording)
            return card.sharedAuditPending
                ? qsTr("已使用其他玩家分享的校准（登录时已在本机核实），正在自动记录。")
                : qsTr("已使用其他玩家分享的校准（本机已核实），正在自动记录。")
        if (card.ready)
            return qsTr("校准完成，核对 %1 件事就能开始自动记录。").arg(card.controller.confirmCount)
        if (card.blocked)
            return qsTr("本机校准无法继续，当前不会生成记录。")
        if (card.done)
            return qsTr("已按本机校准的档案开始自动记录。")
        if (card.provisional)
            return qsTr("已经可以正常记录导随了，软件还在后台找更准的判定依据。")
        return qsTr("游戏更新到了新版本，本软件正在重新校准："
                    + "正常打一把随机任务（进本、打完出本）就好，期间不会生成记录。")
    }

    // 每行表示一项证据是否已观察到。契约中 progress 可以缺席（未测量），此时显示 –。
    // 文案统一为"名词在前、状态在后"，以免与下方的阻塞说明冲突。
    // nearKey 表示观察到相近证据但尚不成立，nearText 说明差在哪一步；这两个诊断字段
    // 均可缺席（旧 Collector），缺席时按“还没见到”显示。
    // 一次副本结束后各行不应全部显示“还没见到”：进本一行有了 duty_zone_seen
    // 即显示“看到了，只是还没能和排本对上”。
    readonly property var progressRows: [
        { key: "finder_request_seen", label: qsTr("排本"), nearKey: "", nearText: "" },
        { key: "pop_seen", label: card.provisional ? qsTr("匹配报文") : qsTr("匹配弹窗"),
          nearKey: "pop_shape_seen",
          nearText: card.provisional ? qsTr(" · 还在找，不影响记录")
                                     : qsTr(" · 尚未确认，等待进本核对") },
        { key: "duty_entry_seen", label: qsTr("进本"), nearKey: "duty_zone_seen",
          nearText: qsTr(" · 已看到，还没能和这次排本对上") },
        { key: "duty_exit_seen", label: qsTr("出本"), nearKey: "", nearText: "" },
        // 职业报文对档案是可选的：未识别不影响校准完成，只是记录中职业为“未知”。
        // 措辞不使用“还没见到”——报文通常已收到，只是没有认出来。
        { key: "job_seen", label: qsTr("职业"), nearKey: "", nearText: "",
          seenText: qsTr(" · 已认出"), unseenText: qsTr(" · 还没认出来，先记为未知") }
    ]

    CardKicker { text: card.sharedInUse ? qsTr("共享校准") : qsTr("本机校准") }

    Text {
        objectName: "calibrationHeadline"
        Layout.fillWidth: true
        text: card.headline()
        textFormat: Text.PlainText
        color: card.done || card.sharedRecording ? Theme.green : Theme.textPrimary
        font.pixelSize: Theme.fs(14)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    Text {
        objectName: "calibrationExplanation"
        Layout.fillWidth: true
        // 共享档案记录时，下方"接下来做什么"已给出说明。
        visible: !card.blocked && !card.sharedRecording
        // 按排本推断的说法与共享校准的同意提示共用同一句（CalibrationController.queueInferenceText）。
        text: card.done
              ? qsTr("这份档案是在这台电脑上校准出来的，和随软件附带的档案一样用于记录。")
              : card.provisional
                ? qsTr("现在%1。照常游戏即可；打两把不同的随机任务，软件就有机会换成更准的判定，到时会再请你核对一次。")
                  .arg(card.controller ? card.controller.queueInferenceText : "")
                : qsTr("校准期间照常游戏即可，不用改设置、不用放文件、也不用重新登录。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    Rectangle {
        Layout.fillWidth: true
        Layout.preferredHeight: 1
        visible: !card.sharedRecording
        color: Theme.border
    }

    Text {
        objectName: "calibrationProgressContext"
        Layout.fillWidth: true
        visible: !card.sharedRecording
        text: qsTr("累计校准进度 · 退出游戏、重新登录或重启软件后会保留，不代表你当前仍在副本中。")
        textFormat: Text.PlainText
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    ColumnLayout {
        Layout.fillWidth: true
        visible: !card.sharedRecording
        spacing: 4

        Repeater {
            model: card.progressRows

            delegate: RowLayout {
                id: progressRow
                required property var modelData

                readonly property bool seen: !!card.progress[progressRow.modelData.key]
                readonly property bool near: !progressRow.seen
                                             && progressRow.modelData.nearKey.length > 0
                                             && !!card.progress[progressRow.modelData.nearKey]

                objectName: "calibrationProgress_" + progressRow.modelData.key
                Layout.fillWidth: true
                spacing: 8

                Text {
                    text: progressRow.seen ? "✓" : (progressRow.near ? "!" : "–")
                    color: progressRow.seen ? Theme.green : (progressRow.near ? Theme.orangeText : Theme.neutral400)
                    font.pixelSize: Theme.fs(13)
                    font.bold: true
                }
                Text {
                    objectName: "calibrationProgressText_" + progressRow.modelData.key
                    Layout.fillWidth: true
                    text: progressRow.modelData.label
                          + (progressRow.seen
                             ? (progressRow.modelData.seenText || qsTr(" · 已看到"))
                             : (progressRow.near
                                ? progressRow.modelData.nearText
                                : (progressRow.modelData.unseenText || qsTr(" · 还没见到"))))
                    textFormat: Text.PlainText
                    color: progressRow.seen
                           ? Theme.textPrimary
                           : (progressRow.near ? Theme.textPrimary : Theme.textSecondary)
                    font.pixelSize: Theme.fs(13)
                    wrapMode: Text.WordWrap
                }
            }
        }
    }

    Text {
        objectName: "calibrationEvidenceTimes"
        Layout.fillWidth: true
        visible: text.length > 0 && !card.sharedRecording
        text: card.evidenceTimes()
        textFormat: Text.PlainText
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    // 上面五行是累计证据，这里是接下来的操作。兼容旧 Collector 的未完成证据链提示，
    // 避免把保留的进本误述为玩家仍在副本中；其他阻塞说明原样显示。
    ColumnLayout {
        objectName: "calibrationBlockers"
        Layout.fillWidth: true
        spacing: 4
        visible: card.captureSilent || (card.controller && card.controller.blockers.length > 0)

        Text {
            text: qsTr("接下来做什么")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
        }

        Repeater {
            model: card.captureSilent
                   ? [qsTr("现在抓包一条报文都解不出来，这一页上另有一条提示说明了原因。"
                           + "先把那一条解决掉；在那之前打副本不会被看到，校准也不会有进展。")]
                   : (card.controller ? card.controller.blockers : [])

            delegate: Text {
                id: blockerText
                required property string modelData

                objectName: "calibrationBlockerText"
                Layout.fillWidth: true
                text: card.blockerMessage(blockerText.modelData)
                textFormat: Text.PlainText
                // 共享档案正在记录时，此句说明后台仍在核对，并非故障。
                color: card.sharedRecording ? Theme.textSecondary : Theme.orangeText
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
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
        visible: card.ready || (!card.done && !card.sharedRecording)
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
            // 共享档案记录时需撤下的是共享档案，由下方「不用共享的，我自己校准」负责。
            visible: !card.done && !card.sharedRecording
            text: qsTr("清空进度并重新观察")
            enabled: !!card.controller && !card.controller.busy
            onClicked: card.controller.discard()
            Accessible.description: qsTr("丢掉本机校准到目前为止看到的全部内容（不影响已经生成的记录），"
                                         + "从头开始观察。之后照常打一把随机任务即可。")
        }

        Item { Layout.fillWidth: true }
    }

    SharedCalibrationSection {
        objectName: "calibrationSharedSection"
        Layout.fillWidth: true
        headlineShownByCard: card.sharedRecording
    }
}
