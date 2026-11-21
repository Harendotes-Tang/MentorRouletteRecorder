pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// ---------------------------------------------------------------------------
// 第二轮采证 (2026-11-21): the player's way into the candidate validation lane,
// without the maintainer tools. Shown on the 新区服采证 page only when the
// Collector reports a candidate profile for this exact client build while no
// profile records for it - candidate_hypotheses non-empty, game_build known,
// profile_status UNSUPPORTED_BUILD - or while the round is on.
//
// The round is CandidateReviewController's (Runtime.app.candidates): 开始 turns
// candidate validation on with every eligible hypothesis whitelisted in one
// UpdateCaptureSettings, the Collector's follow-the-game capture records each
// launch of the game (the restart of step ③ included), 结束并导出 switches it off
// and writes the evidence file and its .sha256. Unlike a 新区服采证 round it keeps
// the content of the messages it names, so the disclosure has to be accepted
// first (docs/privacy-boundary.md §5.1.1). Nothing is uploaded.
// ---------------------------------------------------------------------------
Card {
    id: card
    objectName: "candidateRoundCard"
    padding: 16
    spacing: 10

    readonly property var controller: Runtime.app.candidates
    readonly property var status: Runtime.app.captureStatus
    readonly property var hypotheses: status.candidate_hypotheses || []
    readonly property string offeredProfileId: hypotheses.length > 0 ? String(hypotheses[0].profile_id || "") : ""
    onOfferedProfileIdChanged: consent = false
    readonly property bool candidateEnabled: !!Runtime.app.captureSettings.candidate_validation_enabled
    // A candidate profile for exactly this build, and nothing that records it.
    readonly property bool exactBuild: offeredProfileId.length > 0 && !!status.game_build
                                       && status.profile_status === "UNSUPPORTED_BUILD"
    readonly property var exportResult: controller.roundExportResult || ({})
    readonly property bool exported: !!exportResult.target_path
    readonly property bool recoverable: controller.roundRecoverable
    readonly property bool available: exactBuild || candidateEnabled || exported || controller.finishPending || recoverable
    readonly property bool busy: controller.candidateSettingsBusy || controller.exporting || controller.finishing
    readonly property bool settingsKnown: controller.roundSettingsKnown
    readonly property bool scopedRoundsSupported: controller.scopedRoundsSupported
    readonly property string playerRoundId: controller.roundId
    property string formRoundId: ""
    onPlayerRoundIdChanged: {
        if (!playerRoundId || playerRoundId === formRoundId)
            return
        formRoundId = playerRoundId
        card.tasks = [false, false, false, false]
        card.edits = ({})
    }

    readonly property string phase: controller.finishPending ? "retry"
                                    : exported ? "exported"
                                    : candidateEnabled ? (controller.roundId ? "running" : "unscoped")
                                    : recoverable ? "recover" : "intro"

    property bool consent: false
    // The four things to do, ticked by the tester: instructions, not detected states.
    readonly property var taskDefs: [
        qsTr("排一个不是指导者任务的随机任务，等 5 秒后取消"),
        qsTr("切换一次职业"),
        qsTr("完全退出游戏，再重新启动并登录"),
        qsTr("排指导者任务，完成后离开副本")
    ]
    property var tasks: [false, false, false, false]
    function toggleTask(index) {
        const copy = card.tasks.slice()
        copy[index] = !copy[index]
        card.tasks = copy
    }
    readonly property int tasksDone: tasks.filter(function(done) { return done }).length

    // What the software read at the candidates' positions (CaptureStatus.candidate_round_guesses,
    // which may arrive independently of the round settings). Only this round's guesses can
    // fill the form. A field follows its guess until the tester edits it.
    readonly property var guesses: {
        const values = status.candidate_round_guesses
        return values && playerRoundId && values.candidate_round_id === playerRoundId ? values : null
    }
    readonly property var answerDefs: [
        { key: "first_roulette", label: qsTr("步骤 1 排的任务") },
        { key: "job_before", label: qsTr("换职业前的职业") },
        { key: "job_after", label: qsTr("换职业后的职业") },
        { key: "duty", label: qsTr("步骤 4 进的副本") }
    ]
    property var edits: ({})
    function isEdited(key) { return card.edits[key] !== undefined }
    function setEdit(key, text) {
        const copy = Object.assign({}, card.edits)
        copy[key] = text
        card.edits = copy
    }
    // The guess in the interface language, the Collector's reference name failing that, its
    // number failing both; "" before anything was read.
    function guessText(key) {
        const guess = card.guesses ? card.guesses[key] : null
        if (!guess || guess.id === undefined || guess.id === null)
            return ""
        const recorded = guess.name || ""
        let name = ""
        if (key === "first_roulette") {
            name = Theme.rouletteLabel(guess.id, recorded)
        } else if (key === "duty") {
            if (recorded.length > 0 || (guess.content_id !== undefined && guess.content_id !== null))
                name = Theme.dutyLabel(guess.content_id, recorded)
        } else {
            name = Theme.jobLabel(guess.id, recorded) || Theme.catalogJobName(guess.id, "")
        }
        return name || qsTr("编号 %1").arg(guess.id)
    }
    function answerText(key) { return card.isEdited(key) ? card.edits[key] : card.guessText(key) }
    function roundAnswers() {
        const answers = {}
        for (let index = 0; index < card.answerDefs.length; ++index) {
            const key = card.answerDefs[index].key
            const text = String(card.answerText(key)).trim()
            if (text.length > 0)
                answers[key] = text
        }
        return answers
    }
    function finish() {
        card.controller.roundAnswers = card.roundAnswers()
        card.controller.finishRound()
    }

    // Why 开始 cannot be pressed, in one line; "" when it can (the consent box aside).
    readonly property string blockedReason: {
        if (Runtime.app.validationActive)
            return qsTr("新区服采证正在进行，结束后才能开始这一轮。")
        if (Runtime.app.capturing)
            return qsTr("软件正在记录，不能同时开始这一轮。请先退出游戏。")
        if (Runtime.app.ffxivRunning)
            return qsTr("游戏正在运行：先完全退出游戏，再开始。")
        if (!Runtime.app.npcapInstalled)
            return qsTr("未安装 Npcap，暂时不能开始。")
        if (!Runtime.app.captureSettingsSupported || !settingsKnown)
            return qsTr("暂时读不到软件的设置，请稍后再试。")
        if (!scopedRoundsSupported)
            return qsTr("当前后台服务无法按轮次限定证据，请更新软件后重试。")
        return ""
    }
    readonly property bool showStart: !candidateEnabled && exactBuild
                                       && (phase === "intro" || phase === "exported" || phase === "recover")
    readonly property bool canStart: showStart && consent && blockedReason.length === 0 && !busy

    readonly property var names: hypotheses.map(function(h) { return h.name })
    readonly property int seenCount: {
        const seen = controller.seenHypotheses || []
        let count = 0
        for (let index = 0; index < seen.length; ++index) {
            if (card.names.indexOf(seen[index]) >= 0)
                ++count
        }
        return count
    }

    function baseName(path) {
        const parts = String(path || "").split(/[\\/]/)
        return parts[parts.length - 1]
    }
    // The files to send, as the Collector wrote them.
    readonly property var exportFiles: exported
        ? [card.baseName(exportResult.target_path), card.baseName(exportResult.sha256_path)] : []
    readonly property string exportFolder: {
        const path = String(exportResult.target_path || "")
        const cut = Math.max(path.lastIndexOf("\\"), path.lastIndexOf("/"))
        return cut > 0 ? path.slice(0, cut) : ""
    }

    function start() {
        if (!card.canStart)
            return
        card.controller.startRound(card.hypotheses)
        card.consent = false
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 8
        CardKicker { text: qsTr("第二轮：确定任务、副本和职业的位置") }
        Item { Layout.fillWidth: true }
        StatusTag {
            objectName: "candidateRoundBadge"
            text: card.phase === "running" ? (Runtime.app.capturing ? qsTr("正在记录") : qsTr("等待游戏启动"))
                  : card.phase === "retry" || card.phase === "unscoped" || card.phase === "recover" ? qsTr("待处理")
                  : card.phase === "exported" ? qsTr("已导出") : qsTr("尚未开始")
            tone: card.phase === "running" ? (Runtime.app.capturing ? "ok" : "warn")
                  : card.phase === "exported" ? "ok" : "off"
        }
    }
    EvidenceHint {
        text: qsTr("这个游戏版本已经有一份候选档案，列出了可能带有任务编号、副本和职业的几种报文。"
                   + "这一轮记下它们的内容，维护者据此确定具体位置，之后才能为这个区服正式自动记录。只需要做一轮。")
    }

    // ---- before the round ----------------------------------------------------
    InsetBox {
        objectName: "candidateRoundDisclosure"
        Layout.fillWidth: true
        visible: card.showStart
        implicitHeight: disclosure.implicitHeight + 24
        ColumnLayout {
            id: disclosure
            anchors.fill: parent
            anchors.margins: 12
            spacing: 8
            Text {
                Layout.fillWidth: true
                textFormat: Text.PlainText
                text: qsTr("这一轮会记录少数几种报文的内容，用来确定任务编号、副本和职业在报文里的位置。"
                           + "范围只限于这个游戏版本的候选档案里列出的报文；其中个别候选可能并不是我们以为的那一种，"
                           + "里面可能包含与你的角色有关的信息。文件只保存在这台电脑上，不会自动上传，由你决定是否发给维护者。")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                lineHeight: 1.35
                wrapMode: Text.Wrap
            }
            Text {
                Layout.fillWidth: true
                textFormat: Text.PlainText
                text: qsTr("导出的文件只包含这一轮的证据。")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                wrapMode: Text.Wrap
            }
            SelectionCheckBox {
                objectName: "candidateRoundConsent"
                Layout.fillWidth: true
                text: qsTr("我已了解并同意这一轮记录这些报文的内容")
                checked: card.consent
                enabled: !card.busy
                onToggled: card.consent = checked
            }
        }
    }
    Text {
        objectName: "candidateRoundBlockedReason"
        Layout.fillWidth: true
        visible: card.showStart && card.blockedReason.length > 0
        textFormat: Text.PlainText
        text: card.blockedReason
        color: Theme.orangeText
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.Wrap
    }

    EvidenceHint {
        visible: card.phase === "unscoped"
        text: qsTr("这次采证没有可确认的轮次，无法导出。可先结束这次采证，再开始新一轮。")
    }
    EvidenceHint {
        visible: card.phase === "retry" && (!card.settingsKnown || !card.scopedRoundsSupported)
        text: card.settingsKnown
              ? qsTr("当前后台服务无法按轮次限定证据，请更新软件后重试。")
              : qsTr("重新连接后台服务后，可继续结束并导出这一轮。")
    }

    EvidenceHint {
        visible: card.phase === "recover"
        text: qsTr("最近一轮已经结束，可以导出这轮证据，也可以开始新一轮。")
    }

    // ---- during the round ----------------------------------------------------
    EvidenceHint {
        visible: card.phase === "running"
        text: qsTr("现在启动游戏并登录角色，再依次完成下面四件事，每完成一件就勾上。"
                   + "游戏退出后软件会自动接着记录，重新启动游戏后不需要再点开始。")
    }
    ColumnLayout {
        Layout.fillWidth: true
        visible: card.phase === "running"
        spacing: 4
        Repeater {
            model: card.taskDefs
            delegate: SelectionCheckBox {
                required property string modelData
                required property int index
                objectName: "candidateRoundTask_" + (index + 1)
                Layout.fillWidth: true
                text: (index + 1) + ". " + modelData
                checked: !!card.tasks[index]
                onToggled: card.toggleTask(index)
            }
        }
    }
    InsetBox {
        objectName: "candidateRoundGuesses"
        Layout.fillWidth: true
        visible: card.phase === "running"
        implicitHeight: guessColumn.implicitHeight + 24
        ColumnLayout {
            id: guessColumn
            anchors.fill: parent
            anchors.margins: 12
            spacing: 8
            Text {
                Layout.fillWidth: true
                textFormat: Text.PlainText
                text: qsTr("软件识别到的情况")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                font.weight: Font.DemiBold
                wrapMode: Text.Wrap
            }
            EvidenceHint {
                text: qsTr("这些是软件按候选位置读到的，只是猜测。请对照游戏里的实际情况，不对或空着的直接改成实际名称。")
            }
            Repeater {
                model: card.answerDefs
                delegate: ColumnLayout {
                    id: answerRow
                    required property var modelData
                    readonly property string key: modelData.key
                    Layout.fillWidth: true
                    spacing: 4
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 8
                        FieldLabel {
                            Layout.fillWidth: true
                            text: answerRow.modelData.label
                        }
                        Tag {
                            objectName: "candidateRoundEdited_" + answerRow.key
                            visible: card.isEdited(answerRow.key)
                            text: qsTr("已修改")
                            variant: "accent"
                        }
                    }
                    StyledTextField {
                        objectName: "candidateRoundAnswer_" + answerRow.key
                        Layout.fillWidth: true
                        maximumLength: 100
                        text: card.answerText(answerRow.key)
                        placeholderText: qsTr("还没有识别到，可以自己填")
                        onTextEdited: card.setEdit(answerRow.key, text)
                    }
                }
            }
        }
    }
    Text {
        objectName: "candidateRoundCounts"
        Layout.fillWidth: true
        visible: card.phase === "running"
        textFormat: Text.PlainText
        text: qsTr("已看到 %1 / %2 种报文 · 共记下 %3 条")
              .arg(card.seenCount).arg(card.hypotheses.length)
              .arg(Number(card.status.candidate_observation_count || 0))
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.Wrap
    }

    // ---- after the round -----------------------------------------------------
    ColumnLayout {
        Layout.fillWidth: true
        visible: card.phase === "exported"
        spacing: 4
        EvidenceHint {
            text: qsTr("已导出。文件只保存在这台电脑上，没有上传。请把下面两个文件私下发给维护者：")
        }
        Text {
            objectName: "candidateRoundFolder"
            Layout.fillWidth: true
            textFormat: Text.PlainText
            text: Runtime.fmt.foldUserPath(card.exportFolder)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            font.family: Theme.monoFamily
            wrapMode: Text.WrapAnywhere
        }
        Repeater {
            model: card.exportFiles
            delegate: Text {
                required property string modelData
                required property int index
                objectName: "candidateRoundFile_" + index
                Layout.fillWidth: true
                textFormat: Text.PlainText
                text: modelData
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                font.family: Theme.monoFamily
                wrapMode: Text.WrapAnywhere
            }
        }
        EvidenceHint {
            text: qsTr("发给维护者以后可以删除这两个文件；软件自己记下的内容最多保留 30 天。")
        }
    }

    Text {
        Layout.fillWidth: true
        visible: text.length > 0
        textFormat: Text.PlainText
        text: card.controller.candidateSettingsError || card.controller.roundExportError
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.Wrap
    }

    Flow {
        Layout.fillWidth: true
        spacing: 12
        AppButton {
            objectName: "candidateRoundStartButton"
            visible: card.showStart
            variant: card.phase === "intro" ? "primary" : "secondary"
            text: card.phase === "intro" ? qsTr("开始") : qsTr("开始新一轮")
            enabled: card.canStart
            onClicked: card.start()
        }
        AppButton {
            objectName: "candidateRoundExportAgainButton"
            visible: card.phase === "retry" || card.phase === "recover"
            text: card.busy ? qsTr("正在导出")
                  : card.phase === "retry" ? qsTr("重试结束并导出") : qsTr("导出这一轮")
            enabled: card.settingsKnown && card.scopedRoundsSupported && !card.busy
            onClicked: card.controller.finishRound()
        }
        AppButton {
            objectName: "candidateRoundDiscardFinishButton"
            visible: card.controller.canDiscardRoundFinish
            text: qsTr("放弃本次导出")
            enabled: !card.busy
            onClicked: card.controller.discardRoundFinish()
        }
        AppButton {
            objectName: "candidateRoundStopUnscopedButton"
            visible: card.phase === "unscoped"
            text: qsTr("结束采证")
            enabled: card.settingsKnown && !card.busy
            onClicked: card.controller.stopUnscopedRound()
        }
        AppButton {
            objectName: "candidateRoundFinishButton"
            visible: card.phase === "running"
            variant: card.tasksDone === card.taskDefs.length ? "primary" : "secondary"
            text: card.busy ? qsTr("正在导出") : qsTr("结束并导出")
            enabled: card.settingsKnown && card.scopedRoundsSupported && !card.busy
            onClicked: card.finish()
        }
        AppButton {
            objectName: "candidateRoundOpenFolderButton"
            visible: card.phase === "exported"
            variant: "primary"
            text: qsTr("打开所在文件夹")
            onClicked: card.controller.openRoundExportFolder()
        }
    }
}
