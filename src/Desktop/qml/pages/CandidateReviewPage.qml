import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 对照核对：按时间列出本次捕获中发生的事件，供玩家对照自身经历或 ACT 时间线
// 逐条判定“对 / 错 / 不确定”。
//
// 每行为一个事件而非一条报文：登录、排本登记、弹窗、进入副本、离开副本、换区。
// 进入／离开副本为推断：弹窗后的第一次换区视为进本，下一次视为离开；无弹窗时
// 仅记为“换区”，不作推断。报文级明细置于页面底部，供维护者查看。
Item {
    id: page
    objectName: "candidateReviewPage"
    readonly property var controller: App.candidates
    readonly property var hypotheses: App.captureStatus.candidate_hypotheses || []
    property var notes: ({})
    property var submittedNotes: ({})
    property bool showPackets: false
    readonly property var sessionChoices: [{ session_id: "", label: qsTr("全部会话") }].concat(controller.sessions)

    function localTime(value) {
        if (!value) return qsTr("未配对")
        const date = new Date(value)
        return isNaN(date.getTime()) ? qsTr("时间未知") : Qt.formatDateTime(date, "MM-dd hh:mm:ss")
    }
    function clock(value) {
        if (!value) return ""
        const date = new Date(value)
        return isNaN(date.getTime()) ? qsTr("时间未知") : Qt.formatDateTime(date, "hh:mm:ss")
    }
    function verdictLabel(verdict) {
        return verdict === "CORRECT" ? qsTr("对") : verdict === "WRONG" ? qsTr("错")
             : verdict === "UNSURE" ? qsTr("不确定") : qsTr("待核对")
    }
    function hypothesisLabel(name) {
        if (name === "ZONE_LOAD") return qsTr("换区（进本／离开／传送／登录）")
        if (name === "QUEUE_WINDOW_SAMPLE") return qsTr("排本窗口采样")
        if (name === "DUTY_WINDOW_SAMPLE") return qsTr("副本内采样（用于定位通关报文）")
        if (name === "QUEUE_CANCELLATION") return qsTr("副本查找器操作（旧名）")
        for (const h of page.hypotheses)
            if (h.name === name) return h.label || name
        return name
    }
    function eventTitle(event) {
        switch (event.kind) {
        case "login": return qsTr("登录进入游戏")
        case "queue": return qsTr("排本登记")
        case "finder_action": return qsTr("副本查找器操作（第 0 字节 = 随机任务编号）")
        case "finder": return event.count > 1
                       ? qsTr("副本查找器状态更新（%1 条）").arg(event.count)
                       : qsTr("副本查找器状态更新")
        case "duty_enter": return qsTr("进入副本")
        case "duty_leave": return qsTr("离开副本")
        default: return qsTr("换区（传送或其他）")
        }
    }
    function eventHint(event) {
        switch (event.kind) {
        case "login": return qsTr("这条连接上的第一次区域加载。")
        case "queue": return qsTr("候选报文：你在这个时刻点了“开始排本”吗？")
        case "finder_action": return qsTr("候选报文：这个时刻你对副本查找器做了什么（取消、确认进入、其他）？请写在备注里。")
        case "finder": return qsTr("候选报文：这个时刻是弹窗、点了进入、还是别的（比如刚打完本）？请写在备注里。")
        case "duty_enter": return event.pair_at_utc
                           ? qsTr("推断：确认进入之后的第一次换区。到 %1 离开，共 %2。")
                             .arg(page.clock(event.pair_at_utc)).arg(page.spanText(event.at_utc, event.pair_at_utc))
                           : qsTr("推断：确认进入之后的第一次换区，尚未看到离开。")
        case "duty_leave": return qsTr("推断：进本之后的下一次换区。对照 ACT 的最后一场战斗结束时间。")
        default: return qsTr("前面没有副本查找器的操作，所以只当作普通换区，不猜是不是副本。")
        }
    }
    function spanText(from, to) {
        const a = new Date(from), b = new Date(to)
        if (isNaN(a.getTime()) || isNaN(b.getTime())) return qsTr("时长未知")
        const seconds = Math.max(0, Math.round((b - a) / 1000))
        return qsTr("%1 分 %2 秒").arg(Math.floor(seconds / 60)).arg(seconds % 60)
    }
    function eventColor(kind) {
        return kind === "duty_enter" || kind === "duty_leave" ? Theme.orange
             : kind === "finder" ? Theme.blue
             : kind === "queue" || kind === "finder_action" ? (Theme.eorzea ? Theme.gold : Theme.accent)
             : kind === "login" ? Theme.green : Theme.textSecondary
    }
    function setNote(id, value) {
        const next = Object.assign({}, notes)
        next[id] = value
        notes = next
    }
    function submit(id, existingNote, verdict) {
        const note = notes[id] !== undefined ? notes[id] : (existingNote || "")
        const pending = Object.assign({}, submittedNotes)
        pending[id] = note
        submittedNotes = pending
        controller.review(id, verdict, note)
    }
    Connections {
        target: page.controller
        function onReviewFinished(id, ok, error) {
            if (ok && page.notes[id] === page.submittedNotes[id]) {
                const next = Object.assign({}, page.notes)
                delete next[id]
                page.notes = next
            }
        }
    }

    // One "对 / 错 / 不确定" strip, shared by events and packets.
    component VerdictRow: RowLayout {
        id: strip
        required property string observationId
        required property var existingNote
        required property var verdict
        required property string subject
        Layout.fillWidth: true
        StyledTextField {
            objectName: "candidateReviewNote"
            Layout.fillWidth: true
            maximumLength: 2000
            placeholderText: qsTr("备注（可选）：当时排的什么本、有没有弹窗…")
            text: page.notes[strip.observationId] !== undefined
                  ? page.notes[strip.observationId] : (strip.existingNote || "")
            onTextEdited: page.setNote(strip.observationId, text)
            Accessible.name: qsTr("核对备注，最多 2000 字")
        }
        Repeater {
            model: [{ value: "CORRECT", label: qsTr("对") }, { value: "WRONG", label: qsTr("错") }, { value: "UNSURE", label: qsTr("不确定") }]
            delegate: AppButton {
                required property var modelData
                compact: true
                text: modelData.label
                variant: strip.verdict === modelData.value ? "primary" : "secondary"
                enabled: page.controller.reviewBusyId.length === 0
                onClicked: page.submit(strip.observationId, strip.existingNote, modelData.value)
                Accessible.name: qsTr("核对 %1：%2").arg(strip.subject).arg(modelData.label)
            }
        }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 10
        PageHeader { title: qsTr("对照核对"); subtitle: qsTr("候选观测 · 不计入正式记录") }
        RowLayout {
            Layout.fillWidth: true
            StyledComboBox {
                id: sessionPicker
                objectName: "candidateSessionPicker"
                Layout.fillWidth: true
                model: page.sessionChoices
                textRole: "label"
                valueRole: "session_id"
                Accessible.name: qsTr("筛选捕获会话")
                Binding {
                    target: sessionPicker
                    property: "currentIndex"
                    value: {
                        for (let i = 0; i < page.sessionChoices.length; ++i)
                            if (page.sessionChoices[i].session_id === page.controller.sessionId) return i
                        return 0
                    }
                    restoreMode: Binding.RestoreNone
                }
                onActivated: function(index) { page.controller.setSessionId(page.sessionChoices[index].session_id) }
            }
            AppButton {
                text: page.controller.loading ? qsTr("读取中…") : qsTr("刷新")
                enabled: !page.controller.loading && !page.controller.indexLoading
                onClicked: page.controller.refresh()
            }
            AppButton {
                text: page.controller.exporting ? qsTr("导出中…") : qsTr("导出证据")
                enabled: !page.controller.exporting
                onClicked: page.controller.exportEvidence()
            }
        }
        Text {
            Layout.fillWidth: true
            text: page.controller.hasNewObservations
                  ? qsTr("有新观测；点“刷新”更新时间线。未提交的备注会保留。")
                  : qsTr("按时间倒序列出这次捕获里发生的事。对照你的实际经历或 ACT 的时间线，逐条点“对 / 错 / 不确定”。"
                         + "“进入 / 离开副本”是推断（确认进入后的第一次换区算进本），其余换区不猜。")
            color: page.controller.hasNewObservations ? Theme.orangeText : Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
        Text {
            Layout.fillWidth: true
            visible: text.length > 0
            text: page.controller.error || page.controller.reviewError || page.controller.exportError
            textFormat: Text.PlainText
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WrapAnywhere
        }
        StyledTextField {
            Layout.fillWidth: true
            visible: !!page.controller.exportResult.target_path
            text: page.controller.exportResult.target_path || ""
            readOnly: true
            Accessible.name: qsTr("已导出证据路径，含原始负载时请仅交给维护者")
        }

        // ---- 事件时间线 ---------------------------------------------------
        ListView {
            id: events
            objectName: "candidateEventTimeline"
            Layout.fillWidth: true
            Layout.fillHeight: true
            clip: true
            model: page.controller.timeline
            spacing: 8
            ScrollBar.vertical: ScrollBar {}
            delegate: Rectangle {
                id: eventRow
                required property var modelData
                readonly property bool duty: modelData.kind === "duty_enter" || modelData.kind === "duty_leave"
                width: events.width - 12
                height: eventBody.implicitHeight + 24
                radius: Theme.radiusS
                color: Theme.surface
                border.color: duty ? Theme.orange : Theme.border
                border.width: duty ? 2 : 1
                ColumnLayout {
                    id: eventBody
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.margins: 12
                    spacing: 6
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 10
                        Text {
                            text: page.localTime(eventRow.modelData.at_utc)
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(14)
                            font.weight: Theme.figureWeight(true)
                            font.family: Theme.figureFamily
                            font.features: ({ "tnum": 1 })
                        }
                        Text {
                            Layout.fillWidth: true
                            text: page.eventTitle(eventRow.modelData)
                            textFormat: Text.PlainText
                            color: page.eventColor(eventRow.modelData.kind)
                            font.pixelSize: Theme.fs(14)
                            font.bold: true
                            elide: Text.ElideRight
                        }
                        Tag {
                            visible: eventRow.modelData.inferred
                            text: qsTr("推断")
                            variant: "neutral"
                        }
                        Tag {
                            text: page.verdictLabel(eventRow.modelData.review_verdict)
                            variant: eventRow.modelData.review_verdict ? "accent" : "neutral"
                        }
                    }
                    Text {
                        Layout.fillWidth: true
                        text: page.eventHint(eventRow.modelData)
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }
                    VerdictRow {
                        observationId: eventRow.modelData.observation_id
                        existingNote: eventRow.modelData.review_note
                        verdict: eventRow.modelData.review_verdict
                        subject: page.eventTitle(eventRow.modelData)
                    }
                }
            }
            ColumnLayout {
                anchors.centerIn: parent
                width: Math.min(parent.width, 460)
                visible: events.count === 0
                spacing: 12
                Text {
                    Layout.fillWidth: true
                    text: page.controller.indexLoading
                          ? qsTr("正在读取全部观测… 已读 %1 条").arg(page.controller.indexLoadedCount)
                          : page.controller.loading ? qsTr("正在读取候选观测…")
                          : page.controller.total > 0
                            ? (page.controller.indexMessage || qsTr("尚未建立时间线，请点“刷新”。"))
                            : qsTr("这次还没有任何观测。\n在捕获诊断页开启候选验证，先启动本软件再登录游戏，打一次本再回来看。")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(14)
                    horizontalAlignment: Text.AlignHCenter
                    wrapMode: Text.WordWrap
                }
                AppButton {
                    Layout.alignment: Qt.AlignHCenter
                    visible: !page.controller.loading && page.controller.total === 0
                    text: qsTr("前往捕获诊断")
                    onClicked: App.navigate(4)
                }
            }
        }

        // ---- 报文明细（维护者用，默认收起） --------------------------------
        RowLayout {
            Layout.fillWidth: true
            AppButton {
                objectName: "candidatePacketsToggle"
                variant: "ghost"
                compact: true
                text: page.showPackets
                      ? qsTr("收起报文明细")
                      : qsTr("报文明细（维护者用）· 共 %1 条").arg(page.controller.total)
                onClicked: page.showPackets = !page.showPackets
            }
            Item { Layout.fillWidth: true }
            AppButton {
                visible: page.showPackets
                compact: true
                text: qsTr("上一页")
                enabled: page.controller.page > 1 && !page.controller.loading
                onClicked: page.controller.loadPage(page.controller.page - 1)
            }
            Text {
                visible: page.showPackets
                text: qsTr("%1 / %2").arg(page.controller.page).arg(Math.max(1, Math.ceil(page.controller.total / page.controller.pageSize)))
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
            }
            AppButton {
                visible: page.showPackets
                compact: true
                text: qsTr("下一页")
                enabled: page.controller.page * page.controller.pageSize < page.controller.total && !page.controller.loading
                onClicked: page.controller.loadPage(page.controller.page + 1)
            }
        }
        ListView {
            id: timeline
            objectName: "candidateTimeline"
            Layout.fillWidth: true
            Layout.preferredHeight: page.showPackets ? Math.min(360, contentHeight) : 0
            visible: page.showPackets
            clip: true
            model: page.controller.rows
            spacing: 6
            ScrollBar.vertical: ScrollBar {}
            delegate: Rectangle {
                id: observationRow
                required property var modelData
                readonly property bool anchor: modelData.hypothesis_name === "ZONE_LOAD"
                width: timeline.width - 12
                height: rowBody.implicitHeight + 20
                radius: Theme.radiusS
                color: Theme.surfaceMuted
                border.color: Theme.border
                border.width: 1
                ColumnLayout {
                    id: rowBody
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.margins: 10
                    spacing: 4
                    RowLayout {
                        Layout.fillWidth: true
                        Text {
                            text: page.localTime(observationRow.modelData.observed_at_utc)
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(12)
                            font.bold: true
                        }
                        Text {
                            Layout.fillWidth: true
                            text: page.hypothesisLabel(observationRow.modelData.hypothesis_name)
                            textFormat: Text.PlainText
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(12)
                            font.bold: true
                            elide: Text.ElideRight
                        }
                        Tag { text: page.verdictLabel(observationRow.modelData.review_verdict); variant: "neutral" }
                    }
                    Text {
                        Layout.fillWidth: true
                        text: observationRow.anchor
                            ? qsTr("%1 · 簇 %2 → %3 · 连接 %4")
                              .arg(observationRow.modelData.hypothesis_name)
                              .arg(page.localTime(observationRow.modelData.first_observed_at_utc))
                              .arg(page.localTime(observationRow.modelData.last_observed_at_utc))
                              .arg(observationRow.modelData.connection_tag)
                            : qsTr("%1 · %2 · 0x%3 · %4 字节 · %5 · 连接 %6%7")
                              .arg(observationRow.modelData.hypothesis_name)
                              .arg(observationRow.modelData.direction)
                              .arg(Number(observationRow.modelData.opcode).toString(16).padStart(4, "0"))
                              .arg(observationRow.modelData.payload_length)
                              .arg(observationRow.modelData.group_name || qsTr("未分组"))
                              .arg(observationRow.modelData.connection_tag)
                              .arg((observationRow.modelData.occurrences || 1) > 1
                                   ? qsTr(" · 出现 %1 次").arg(observationRow.modelData.occurrences) : "")
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                        wrapMode: Text.Wrap
                    }
                    VerdictRow {
                        observationId: observationRow.modelData.observation_id
                        existingNote: observationRow.modelData.review_note
                        verdict: observationRow.modelData.review_verdict
                        subject: observationRow.modelData.hypothesis_name
                    }
                }
            }
        }
    }
}
