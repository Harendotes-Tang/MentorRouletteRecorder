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
    readonly property bool candidateEnabled: !!Runtime.app.captureSettings.candidate_validation_enabled
    // A candidate profile for exactly this build, and nothing that records it.
    readonly property bool exactBuild: hypotheses.length > 0 && !!status.game_build
                                       && status.profile_status === "UNSUPPORTED_BUILD"
    readonly property var exportResult: controller.exportResult || ({})
    readonly property bool exported: !candidateEnabled && !!exportResult.target_path
    readonly property bool available: exactBuild || candidateEnabled || exported
    readonly property bool busy: controller.candidateSettingsBusy || controller.exporting || controller.finishing

    // "intro" | "running" | "exported"
    readonly property string phase: candidateEnabled ? "running" : exported ? "exported" : "intro"

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
        if (!Runtime.app.captureSettingsSupported)
            return qsTr("暂时读不到软件的设置，请稍后再试。")
        return ""
    }
    readonly property bool canStart: phase !== "running" && consent && blockedReason.length === 0 && !busy

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
        card.tasks = [false, false, false, false]
        card.controller.startRound(card.hypotheses)
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 8
        CardKicker { text: qsTr("第二轮：确定任务、副本和职业的位置") }
        Item { Layout.fillWidth: true }
        StatusTag {
            objectName: "candidateRoundBadge"
            text: card.phase === "running" ? (Runtime.app.capturing ? qsTr("正在记录") : qsTr("等待游戏启动"))
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
        visible: card.phase === "intro"
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
        visible: card.phase !== "running" && card.blockedReason.length > 0
        textFormat: Text.PlainText
        text: card.blockedReason
        color: Theme.orangeText
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.Wrap
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
        text: card.controller.candidateSettingsError || card.controller.exportError
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.Wrap
    }

    Flow {
        Layout.fillWidth: true
        spacing: 12
        AppButton {
            objectName: "candidateRoundStartButton"
            visible: card.phase === "intro"
            variant: "primary"
            text: qsTr("开始")
            enabled: card.canStart
            onClicked: card.start()
        }
        // The round was switched off but its file could not be written: write it again.
        AppButton {
            objectName: "candidateRoundExportAgainButton"
            visible: card.phase === "intro" && card.controller.exportError.length > 0
            text: card.controller.exporting ? qsTr("正在导出") : qsTr("重新导出")
            enabled: !card.busy
            onClicked: card.controller.exportEvidence()
        }
        AppButton {
            objectName: "candidateRoundFinishButton"
            visible: card.phase === "running"
            variant: card.tasksDone === card.taskDefs.length ? "primary" : "secondary"
            text: card.busy ? qsTr("正在导出") : qsTr("结束并导出")
            enabled: !card.busy
            onClicked: card.controller.finishRound()
        }
        AppButton {
            objectName: "candidateRoundOpenFolderButton"
            visible: card.phase === "exported"
            variant: "primary"
            text: qsTr("打开所在文件夹")
            onClicked: card.controller.openExportFolder()
        }
    }
}
