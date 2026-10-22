pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0
import "components/Lucide.js" as Lucide

ApplicationWindow {
    id: window

    width: 1280
    height: 800
    minimumWidth: 1100
    minimumHeight: 720
    visible: true
    color: Theme.contentBackground
    title: qsTr("FF14 导随记录器")

    // Frameless: the 40 px chrome bar below is the title bar (drag, double
    // click to maximise) and carries its own minimise / maximise / close
    // controls; the edges resize through the platform (startSystemResize).
    flags: Qt.Window | Qt.FramelessWindowHint | Qt.WindowMinimizeButtonHint
           | Qt.WindowMaximizeButtonHint | Qt.WindowCloseButtonHint
    readonly property bool maximized: window.visibility === Window.Maximized
                                      || window.visibility === Window.FullScreen
    // For icons rendered through a data: URL (components/Lucide.js): request them
    // at device pixels so the strokes stay crisp on a scaled display.
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1
    StatisticsFloatingWindow { id: statisticsFloat }

    function presentRecordingAlert() {
        if (Runtime.app.maintainerToolsVisible || !window.visible || window.visibility === Window.Minimized) return
        if (Runtime.app.recording.pendingAlert && Runtime.app.recording.blocked && !recordingAlert.opened)
            recordingAlert.open()
    }
    onVisibilityChanged: Qt.callLater(window.presentRecordingAlert)
    // A note editor left open in this window when it goes to the tray would
    // hold every automatic popup back; it is put away like 稍后补录, its draft
    // kept (review L2-10).
    onVisibleChanged: {
        if (!window.visible && reflectionDialog.visible && !reflectionDialog.standalone && !reflectionDialog.busy)
            reflectionDialog.defer()
    }
    Connections {
        target: Runtime.app.recording
        function onChanged() {
            if (!Runtime.app.recording.blocked) recordingAlert.close()
            Qt.callLater(window.presentRecordingAlert)
        }
    }
    Dialog {
        id: recordingAlert
        objectName: "automaticRecordingAlert"
        anchors.centerIn: parent
        width: Math.min(520, window.width - 48)
        modal: true
        Overlay.modal: Rectangle { color: Theme.modalScrim(recordingAlert.palette.shadow) }
        title: qsTr("无法自动记录")
        padding: 24
        background: DialogFrame {}
        header: Label {
            text: recordingAlert.title
            color: Theme.textPrimary
            font.pixelSize: Theme.dialogTitleSize(18)
            font.bold: true
            padding: 24
        }
        closePolicy: Popup.NoAutoClose
        contentItem: ColumnLayout {
            spacing: 16
            Label {
                Layout.fillWidth: true
                text: Runtime.app.recording.message
                color: Theme.textPrimary
                textFormat: Text.PlainText
                wrapMode: Text.WordWrap
            }
            RowLayout {
                Layout.alignment: Qt.AlignRight
                AppButton {
                    text: qsTr("查看诊断")
                    onClicked: { Runtime.app.recording.acknowledge(); recordingAlert.close(); Runtime.app.navigate(4) }
                }
                AppButton {
                    objectName: "automaticRecordingAcknowledge"
                    text: qsTr("我知道了")
                    variant: "primary"
                    onClicked: { Runtime.app.recording.acknowledge(); recordingAlert.close() }
                }
            }
        }
    }

    function toggleMaximized() {
        if (window.maximized)
            window.showNormal()
        else
            window.showMaximized()
    }

    readonly property bool suppressOnboarding: typeof Runtime.suppressOnboarding !== "undefined" ? Runtime.suppressOnboarding : false
    // 每次启动只调度一次；首次引导及业务弹窗优先，普通截图保持原画面。
    property bool releaseNotesWaiting: false
    readonly property var releaseNotesController: typeof Runtime.releaseNotes !== "undefined" ? Runtime.releaseNotes : null
    function presentReleaseNotes() {
        if (!releaseNotesWaiting || !window.visible || window.visibility === Window.Minimized)
            return
        if (baselineDialog.visible || recordingAlert.visible)
            return
        const children = Overlay.overlay.children
        for (let i = 0; i < children.length; ++i) {
            if (children[i].visible) return
        }
        releaseNotesWaiting = false
        releaseNotesDialog.open()
    }
    function openCurrentReleaseNotes() {
        if (!releaseNotesController || releaseNotesController.text.length === 0)
            return
        releaseNotesWaiting = false
        releaseNotesDialog.open()
    }
    Timer {
        interval: 250
        repeat: true
        running: window.releaseNotesWaiting
        onTriggered: window.presentReleaseNotes()
    }
    readonly property bool forceBaselineDialog: typeof Runtime.forceBaselineDialog !== "undefined" ? Runtime.forceBaselineDialog : false
    readonly property bool forceDisclosure: typeof Runtime.forceDisclosure !== "undefined" ? Runtime.forceDisclosure : false
    readonly property bool forceOpenDetail: typeof Runtime.forceOpenDetail !== "undefined" ? Runtime.forceOpenDetail : false
    readonly property bool forceOpenEdit: typeof Runtime.forceOpenEdit !== "undefined" ? Runtime.forceOpenEdit : false
    readonly property bool forceOpenReflection: typeof Runtime.forceOpenReflection !== "undefined" ? Runtime.forceOpenReflection : false
    readonly property string forceDetailTab: typeof Runtime.forceDetailTab !== "undefined" ? Runtime.forceDetailTab : ""

    property string reasonAction: ""
    property string reasonDialogError: ""
    property bool reasonSubmitting: false
    // The run the 原因 dialog's request was sent for. A reply names only its
    // kind and run (a failure not even that), so the dialog takes a success
    // only for its own action and run, and a failure only while its request is
    // out: a 通关 answered in 本次导随结果 meanwhile is not its reply (review
    // OK-6 / OL-5). Both are taken when the dialog opens: the action is sent
    // for the record and the revision the user was looking at, never for a
    // revision that arrived while the reason was typed (review L1-3).
    property string reasonRunId: ""
    property int reasonRevision: -1

    // npcap_installed is in every capture status; without one (no pipe, a
    // failed read) the side panel claims nothing about Npcap (review K2-1).
    readonly property bool npcapKnown: Runtime.app.backendConnected && Runtime.app.captureStatus.npcap_installed !== undefined

    // Colour of the title bar's Collector dot; the state comes from
    // CollectorProcess through AppController.
    function collectorColor() {
        switch (Runtime.app.collectorState) {
        case "running":
        case "reused": return Theme.green
        case "starting": return Theme.accent
        case "missing":
        case "exited": return Theme.orange
        default: return Theme.neutral400
        }
    }

    // "监听中" only while something is actually being decoded: a capture started
    // after the client logged in stays RUNNING and decodes nothing
    // (docs/live-validation-guide.md section 6).
    function captureBadgeText() {
        if (Runtime.app.maintainerToolsVisible) return Runtime.app.captureModeCompactText
        return Runtime.app.recording.state === "listening" ? qsTr("自动监听中")
             : Runtime.app.recording.silent ? qsTr("读不到游戏数据")
             : Runtime.app.recording.state === "waiting" ? qsTr("等待游戏")
             : Runtime.app.recording.state === "choosing_game" ? qsTr("等待选择游戏")
             : Runtime.app.recording.state === "calibrating" ? qsTr("校准中")
             : Runtime.app.recording.state === "calibration_ready" ? qsTr("待核对")
             : Runtime.app.recording.state === "calibration_blocked" ? qsTr("无法自动记录")
             : Runtime.app.recording.blocked ? qsTr("无法自动记录") : qsTr("检查中")
    }

    // Green claims that something is being recorded, so it is reserved for the
    // listening state; states needing attention are amber, the rest neutral.
    function captureBadgeColor() {
        return Runtime.app.recording.attention ? Theme.orange
             : Runtime.app.recording.state === "listening" ? Theme.green : Theme.neutral400
    }

    function openCreateDialog() {
        editDialog.openForCreate()
    }

    function openCorrectDialog() {
        if (!Runtime.app.hasSelection)
            return
        editDialog.openForRun(Runtime.app.selectedRun)
    }

    // Used by the dashboard's 导随心得 panel and the history detail panel. The
    // automatic prompt after a COMPLETED run goes through openForPrompt, which
    // tells the controller whether it really opened.
    function openReflection(run, kicker) {
        if (reflectionDialog.visible || reflectionDialog.busy) return
        prepareReflection(false, false)
        reflectionDialog.openForRun(run, kicker || "")
    }

    function prepareReflection(independent, automatic) {
        recoveryNotice.hide()
        reflectionDialog.standalone = independent
        reflectionDialog.automaticOpen = automatic
        reflectionDialog.automaticKind = ""
        reflectionDialog.parent = independent ? recoveryReminder.contentItem : Overlay.overlay
        if (independent) recoveryReminder.prepare(!automatic)
    }

    function offerReminder(run, kind) {
        if (!Runtime.app.recovery.identityVerified) return
        const mode = typeof Runtime.settings !== "undefined" && Runtime.settings.promptPresentation
                     ? Runtime.settings.promptPresentation : "window"
        // 独立小窗 always opens the popup; 浮窗提示 opens it beside the float
        // while the float is up and otherwise only keeps the prompt in 待处理,
        // as 仅保留待办 always does (review L2-8).
        const silent = mode === "floating" ? !statisticsFloat.visible : mode !== "window"
        if (silent) {
            if (kind === "result") Runtime.app.resultConfirmationDeferred(run.run_id)
            else Runtime.app.reflectionPromptDeferred(run.run_id)
            return
        }
        if (reflectionDialog.visible || reflectionDialog.busy) return
        prepareReflection(true, true)
        if (kind === "result") reflectionDialog.openForResult(run)
        else reflectionDialog.openForPrompt(run, qsTr("刚刚完成"))
    }

    /// Bring this window forward. showNormal() only restores a hidden or
    /// minimised window; a maximised one stays maximised (review K1-6).
    function bringToFront() {
        if (!window.visible)
            window.show()
        else if (window.visibility === Window.Minimized)
            window.showNormal()
        window.raise()
        window.requestActivate()
    }

    /// The editor is open, possibly for another record: show where it is
    /// rather than doing nothing (review K1-7).
    function raiseEditorHost() {
        const host = reflectionDialog.parent ? reflectionDialog.parent.Window.window : null
        if (host === window) {
            window.bringToFront()
        } else if (host) {
            host.show()
            host.raise()
            host.requestActivate()
        }
    }

    Connections {
        target: Runtime.app.recovery
        function onScopeChanging() { recoveryInbox.close(); recoveryNotice.hide() }
        function onInboxRequested() {
            if (reflectionDialog.visible || reflectionDialog.busy) {
                window.raiseEditorHost()
                return
            }
            window.bringToFront()
            recoveryInbox.open()
            Runtime.app.recovery.refresh()
        }
        function onOpenRequested(run, kind) {
            // The popup's pager: the open window takes the record it asked for.
            if (reflectionDialog.visible && reflectionDialog.pendingSwitchId.length > 0
                && reflectionDialog.pendingSwitchId === run.run_id) {
                reflectionDialog.switchTo(run, kind)
                return
            }
            if (reflectionDialog.visible || reflectionDialog.busy) {
                window.raiseEditorHost()
                return
            }
            window.prepareReflection(true, false)
            if (kind === "result") reflectionDialog.openForResult(run)
            else reflectionDialog.openForRun(run, qsTr("继续处理"))
            // Manual recovery is independent from automatic queue acknowledgments.
            reflectionDialog.automaticKind = ""
        }
    }

    function openShare(run) {
        reflectionShareDialog.openForRun(run)
    }

    function openImportDialog() {
        importDialog.openDialog()
    }

    Connections {
        target: Runtime.app
        function onCurrentPageChanged() {
            if (typeof Runtime.reflections !== "undefined")
                Runtime.reflections.setActive(Runtime.app.currentPage === 7)
        }
        function onReflectionSaved(runId) {
            if (typeof Runtime.reflections !== "undefined")
                Runtime.reflections.reload()
        }
    }

    // Every reason-carrying action shares one dialog: 软删除 / 恢复 / 撤销修正 /
    // 确认复核. All four are refused by the Collector without a reason
    // (ERR_REASON_REQUIRED), so none of them may have a one-click path.
    function reasonDialogTitle(actionName) {
        switch (actionName) {
        case "restore": return qsTr("恢复记录")
        case "undo": return qsTr("撤销上一次修正")
        case "review": return qsTr("确认待复核记录")
        default: return qsTr("删除记录")
        }
    }

    function reasonDialogHint(actionName) {
        switch (actionName) {
        case "restore":
            return qsTr("恢复后，这条记录会重新显示在历史记录中，并按原有规则参与统计。")
        case "undo":
            return qsTr("撤销后，上次修正的内容会恢复为修改前的值。此次撤销也会留在修改记录中，"
                        + "既有的修正记录不会被删除。")
        case "review":
            return qsTr("确认表示你已核对这条记录。结果不会被改写，"
                        + "只会取消“待复核”标记，并记入修改记录。"
                        + "要改成“通关”或“离开”，请用“手动修正”填写结果。")
        default:
            return qsTr("删除后，这条记录会移到“已删除”，不再计入默认统计。永久删除前仍可恢复；自动清理期限可在设置中查看。")
        }
    }

    function reasonDialogConfirm(actionName) {
        switch (actionName) {
        case "restore": return qsTr("确认恢复")
        case "undo": return qsTr("确认撤销")
        case "review": return qsTr("确认已复核")
        default: return qsTr("确认删除")
        }
    }

    function openReasonDialog(actionName) {
        if (!Runtime.app.hasSelection)
            return
        reasonAction = actionName
        reasonDialogError = ""
        reasonSubmitting = false
        reasonRunId = Runtime.app.selectedRun.run_id || ""
        reasonRevision = Runtime.app.selectedRun.revision !== undefined ? Number(Runtime.app.selectedRun.revision) : -1
        reasonField.text = ""
        reasonDialog.title = window.reasonDialogTitle(actionName)
        reasonDialog.open()
    }

    Connections {
        target: Runtime.app

        // Both dialogs stay open until the Collector answers, so its refusal is
        // shown in the form the user is looking at; the toast only repeats it.
        // A refusal names the kind and the run of the request it answers, so a
        // form takes only its own - not one from 本次导随结果 or 设置 meanwhile
        // (review DT3-X1).
        function onMutationFailed(code, message, kind, runId, neverSent) {
            const text = Runtime.app.errorText(message, code)
            if (reasonDialog.visible && window.reasonSubmitting
                && kind === window.reasonAction && runId === window.reasonRunId) {
                window.reasonDialogError = text
                window.reasonSubmitting = false
            }
            if (editDialog.visible && (kind === "create" || kind === "correct")) {
                // The run, the code and whether it was sent first: the wizard
                // reads them when the text arrives.
                editDialog.externalErrorRunId = runId || ""
                editDialog.externalErrorCode = code
                editDialog.externalErrorNeverSent = neverSent === true
                editDialog.externalErrorText = text
            }
            // The guide sends only while no other baseline save is out, and nothing
            // else can send one while it is open: a baseline refusal now is its own.
            // It stays open with what was typed (audit 2026-10-03, DT6-X1).
            if (baselineDialog.saving && kind === "baseline") {
                baselineDialog.saving = false
                baselineDialog.errorText = text
            }
        }

        // Only its own accepted save ends the guide, and only then is the first
        // run answered.
        function onBaselineSaved(goal, baseline) {
            if (!baselineDialog.saving)
                return
            baselineDialog.saving = false
            Runtime.app.completeFirstRun()
            baselineDialog.close()
        }

        function onMutationSucceeded(kind, runId, revision, auditEventId) {
            if (kind === "create" || kind === "correct") {
                // 回应带着它属于哪一条记录，交由对话框核对后才生效：迟到的回应
                // 不能把用户此刻正在填的另一张表单静默关掉（审查第 4 条）。
                if (!editDialog.acceptSubmission(runId))
                    return
                if (kind === "create")
                    Runtime.app.navigate(1)
                return
            }
            // The 原因 actions answer with their own name as the kind.
            if (!reasonDialog.visible || !window.reasonSubmitting
                || kind !== window.reasonAction || runId !== window.reasonRunId)
                return
            window.reasonSubmitting = false
            reasonDialog.close()
        }
    }

    // DUTY_RESULT arrived and the run has no reflection yet: ask for one now.
    Connections {
        target: Runtime.app
        ignoreUnknownSignals: true

        function onReflectionPromptRequested(run) {
            window.offerReminder(run, "reflection")
        }
        function onResultConfirmationRequested(run) { window.offerReminder(run, "result") }
    }

    Rectangle {
        id: shell

        anchors.fill: parent
        radius: 0
        border.width: 0
        clip: true

        color: Theme.contentBackground

        // The two radial washes over --color-bg, painted on a Canvas because
        // Rectangle.gradient is linear only. The ellipses come from scaling the
        // context; the stops copy the CSS (colour at the centre, transparent
        // at 70 %).
        Canvas {
            id: backdropWash
            objectName: "backdropWash"
            anchors.fill: parent

            readonly property color topWash: Theme.washTop
            readonly property color bottomWash: Theme.washBottom
            readonly property real bottomRadiusX: Theme.washBottomRadiusX
            readonly property real bottomRadiusY: Theme.washBottomRadiusY
            onTopWashChanged: requestPaint()
            onBottomWashChanged: requestPaint()
            onBottomRadiusXChanged: requestPaint()
            onBottomRadiusYChanged: requestPaint()
            onWidthChanged: requestPaint()
            onHeightChanged: requestPaint()

            function rgba(color, alpha) {
                return "rgba(" + Math.round(color.r * 255) + "," + Math.round(color.g * 255) + ","
                       + Math.round(color.b * 255) + "," + alpha + ")"
            }

            function wash(ctx, cx, cy, rx, ry, color) {
                ctx.save()
                ctx.translate(cx, cy)
                ctx.scale(1, ry / rx)
                const gradient = ctx.createRadialGradient(0, 0, 0, 0, 0, rx)
                gradient.addColorStop(0, rgba(color, color.a))
                gradient.addColorStop(0.7, rgba(color, 0))
                ctx.fillStyle = gradient
                const stretch = rx / ry
                ctx.fillRect(-cx, -cy * stretch, width, height * stretch)
                ctx.restore()
            }

            onPaint: {
                const ctx = getContext("2d")
                ctx.reset()
                ctx.clearRect(0, 0, width, height)
                wash(ctx, width * 0.5, -height * 0.2, 1000, 600, topWash)
                wash(ctx, width, height, bottomRadiusX, bottomRadiusY, bottomWash)
            }
        }


        // The recording notice: a card floating over the page rather than a strip
        // in the window chrome, so it reads as a message from the software.
        Rectangle {
            id: recordingNotice
            objectName: "automaticRecordingBanner"

            readonly property string message: Runtime.app.recording.message
            // Dismissal lasts until the software has something else to say. The
            // serial does not move on a language switch, which re-renders the
            // same message in other words; the text still matches a message that
            // comes back unchanged later.
            property string dismissed: ""
            property int dismissedSerial: -1
            readonly property bool dismissedNow: recordingNotice.dismissedSerial === Runtime.app.recording.messageSerial
                                                 || recordingNotice.dismissed === recordingNotice.message

            visible: !Runtime.app.maintainerToolsVisible
                     && (Runtime.app.recording.attention || Runtime.app.recording.retryAvailable)
                     && !recordingNotice.dismissedNow
            // Bottom right, clear of the page title and of the sidebar: the notice
            // must not cover the heading of the page it is talking about.
            anchors.right: parent.right
            anchors.bottom: parent.bottom
            anchors.rightMargin: 20
            anchors.bottomMargin: 20
            width: Math.min(720, parent.width - 260)
            height: noticeRow.implicitHeight + 24
            radius: Theme.radiusM
            color: Theme.surface
            border.color: Theme.orange
            border.width: 1
            z: 50

            RowLayout {
                id: noticeRow
                anchors.fill: parent
                anchors.margins: 12
                spacing: 10

                Text {
                    Layout.fillWidth: true
                    Layout.alignment: Qt.AlignVCenter
                    text: recordingNotice.message
                    textFormat: Text.PlainText
                    color: Theme.orangeText
                    wrapMode: Text.WordWrap
                    font.pixelSize: Theme.fs(12)
                }
                AppButton {
                    objectName: "calibrationBannerConfirmButton"
                    visible: Runtime.app.calibration.state === "READY"
                    variant: "primary"
                    text: qsTr("核对并启用")
                    onClicked: calibrationDialog.openDialog()
                }
                AppButton {
                    visible: Runtime.app.recording.retryAvailable
                    text: qsTr("重试自动记录")
                    onClicked: Runtime.app.recording.retry()
                }
                AppButton { text: qsTr("查看诊断"); onClicked: Runtime.app.navigate(4) }
                AppButton {
                    objectName: "automaticRecordingNoticeDismiss"
                    text: qsTr("收起")
                    onClicked: {
                        recordingNotice.dismissed = recordingNotice.message
                        recordingNotice.dismissedSerial = Runtime.app.recording.messageSerial
                    }
                }
            }
        }

        // Resize grips of the frameless window: 6 px on each edge and corner,
        // handed to the platform so Windows snapping keeps working.
        Repeater {
            model: [
                { e: Qt.LeftEdge, x: 0, y: 6, w: 6, h: -12, c: Qt.SizeHorCursor },
                { e: Qt.RightEdge, x: -6, y: 6, w: 6, h: -12, c: Qt.SizeHorCursor },
                { e: Qt.TopEdge, x: 6, y: 0, w: -12, h: 6, c: Qt.SizeVerCursor },
                { e: Qt.BottomEdge, x: 6, y: -6, w: -12, h: 6, c: Qt.SizeVerCursor },
                { e: Qt.TopEdge | Qt.LeftEdge, x: 0, y: 0, w: 6, h: 6, c: Qt.SizeFDiagCursor },
                { e: Qt.BottomEdge | Qt.RightEdge, x: -6, y: -6, w: 6, h: 6, c: Qt.SizeFDiagCursor },
                { e: Qt.TopEdge | Qt.RightEdge, x: -6, y: 0, w: 6, h: 6, c: Qt.SizeBDiagCursor },
                { e: Qt.BottomEdge | Qt.LeftEdge, x: 0, y: -6, w: 6, h: 6, c: Qt.SizeBDiagCursor }
            ]

            delegate: MouseArea {
                required property var modelData
                z: 1000
                visible: !window.maximized
                x: modelData.x < 0 ? shell.width + modelData.x : modelData.x
                y: modelData.y < 0 ? shell.height + modelData.y : modelData.y
                width: modelData.w < 0 ? shell.width + modelData.w : modelData.w
                height: modelData.h < 0 ? shell.height + modelData.h : modelData.h
                cursorShape: modelData.c
                acceptedButtons: Qt.LeftButton
                onPressed: (mouse) => {
                    mouse.accepted = true
                    window.startSystemResize(modelData.e)
                }
            }
        }

        ColumnLayout {
            id: shellColumn
            anchors.fill: parent
            spacing: 0

            // Frameless window: the title bar is drawn here; the simulated window controls follow below.
            Rectangle {
                Layout.fillWidth: true
                Layout.preferredHeight: 40
                border.width: 0

                gradient: Gradient {
                    GradientStop { position: 0.0; color: Theme.titlebarTop }
                    GradientStop { position: 1.0; color: Theme.titlebarBottom }
                }

                Rectangle {
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: 1
                    color: Theme.eorzea ? Theme.gold3 : Theme.border
                }

                // Harendotes: the flame -> old gold line of its panels, along
                // the bar's lower edge.
                Rectangle {
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: 1
                    visible: Theme.harendotes
                    gradient: Gradient {
                        orientation: Gradient.Horizontal
                        GradientStop { position: 0.0; color: Theme.accent }
                        GradientStop { position: 0.45; color: Theme.oldGold }
                        GradientStop { position: 1.0; color: Theme.clear(Theme.oldGold) }
                    }
                }

                // Title-bar behaviour of the frameless window. Sits under the
                // row so the buttons keep priority.
                MouseArea {
                    anchors.fill: parent
                    acceptedButtons: Qt.LeftButton
                    onPressed: (mouse) => {
                        mouse.accepted = true
                        window.startSystemMove()
                    }
                    onDoubleClicked: window.toggleMaximized()
                }

                RowLayout {
                    anchors.fill: parent
                    anchors.leftMargin: 14
                    anchors.rightMargin: 8
                    spacing: 12

                    // The gilded diamond of the prototype's title bar.
                    Item {
                        Layout.preferredWidth: 14
                        Layout.preferredHeight: 14
                        visible: Theme.eorzea

                        Rectangle {
                            anchors.centerIn: parent
                            width: 11
                            height: 11
                            rotation: 45
                            color: "transparent"
                            border.width: 1.5
                            border.color: Theme.titlebarMark

                            Rectangle {
                                anchors.centerIn: parent
                                width: 5
                                height: 5
                                color: Theme.titlebarMark
                            }
                        }
                    }

                    RowLayout {
                        spacing: 8

                        Text {
                            text: qsTr("导随记录器")
                            color: Theme.titlebarBrand
                            font.family: Theme.headingFamilyFor(text)
                            font.pixelSize: Theme.fs(12)
                            font.bold: true
                            // letter-spacing: .1em in the prototype's title bar.
                            font.letterSpacing: Theme.eorzea ? 1.2 : 0
                        }

                        // 版本号常驻标题栏：确认所用版本无需查看安装包或导出诊断报告。
                        Text {
                            objectName: "titleBarVersion"
                            text: "v" + Runtime.app.appVersion
                            color: Theme.titlebarTextSecondary
                            font.pixelSize: Theme.fs(11)
                            font.letterSpacing: 0.3
                        }
                    }

                    Item {
                        Layout.fillWidth: true
                    }

                    // Collector status: the state dot, then the full lifecycle
                    // sentence including the restart countdown after a crash.
                    Rectangle {
                        Layout.preferredHeight: 8
                        Layout.preferredWidth: 8
                        radius: Theme.eorzea ? Theme.radiusXxs : 4
                        color: window.collectorColor()
                    }

                    Text {
                        text: Runtime.app.collectorStatusText
                        color: Theme.titlebarTextSecondary
                        font.pixelSize: Theme.fs(11)
                        elide: Text.ElideRight
                        Layout.maximumWidth: 420
                    }

                    AppButton {
                        id: themeToggleButton
                        objectName: "themeToggleButton"
                        compact: true
                        variant: "inverse"
                        text: Runtime.app.dark ? qsTr("浅色") : qsTr("深色")
                        iconName: Runtime.app.dark ? "sun" : "moon"
                        onClicked: window.revealThemeChange(function() { Runtime.app.toggleTheme() },
                                                            themeToggleButton)
                    }

                    // Window controls (frameless window).
                    AppButton {
                        objectName: "statisticsFloatingWindowButton"
                        compact: true
                        variant: Runtime.app.statisticsOverlay.visible ? "primary" : "secondary"
                        text: Runtime.app.statisticsOverlay.locked ? qsTr("解锁浮窗") : qsTr("统计浮窗")
                        iconName: Runtime.app.statisticsOverlay.locked ? "lock-open" : "chart-column"
                        onClicked: {
                            if (Runtime.app.statisticsOverlay.locked) Runtime.app.statisticsOverlay.unlock()
                            else Runtime.app.statisticsOverlay.toggle()
                        }
                    }

                    Row {
                        spacing: 2
                        Layout.leftMargin: 6

                        Repeater {
                            model: [
                                { glyph: "\u2500", action: "min" },
                                { glyph: "\u2610", action: "max" },
                                { glyph: "\u2715", action: "close" }
                            ]

                            delegate: Rectangle {
                                id: control
                                required property var modelData
                                readonly property bool isClose: modelData.action === "close"
                                width: 36
                                height: 28
                                radius: 6
                                color: controlArea.containsMouse
                                       ? (isClose ? "#e81123" : Theme.titlebarFill)
                                       : "transparent"

                                Text {
                                    anchors.centerIn: parent
                                    text: control.modelData.action === "max" && window.maximized
                                          ? "\u2750" : control.modelData.glyph
                                    font.pixelSize: control.isClose ? 13 : 12
                                    color: controlArea.containsMouse && control.isClose
                                           ? "#ffffff" : Theme.titlebarText
                                }

                                MouseArea {
                                    id: controlArea
                                    anchors.fill: parent
                                    hoverEnabled: true
                                    onClicked: {
                                        if (control.modelData.action === "min")
                                            window.showMinimized()
                                        else if (control.modelData.action === "max")
                                            window.toggleMaximized()
                                        else
                                            window.close()
                                    }
                                }
                            }
                        }
                    }
                }
            }

            RowLayout {
                Layout.fillWidth: true
                Layout.fillHeight: true
                spacing: 0

                Rectangle {
                    id: sidebar

                    Layout.preferredWidth: 200
                    Layout.fillHeight: true
                    border.width: 0

                    gradient: Gradient {
                        GradientStop { position: 0.0; color: Theme.chromeTop }
                        GradientStop { position: 1.0; color: Theme.chromeBottom }
                    }

                    ColumnLayout {
                        anchors.fill: parent
                        anchors.margins: 10
                        spacing: 8

                        ColumnLayout {
                            Layout.fillWidth: true
                            Layout.topMargin: 10
                            Layout.leftMargin: 6
                            Layout.rightMargin: 6
                            spacing: 2

                            CardKicker { text: qsTr("成就追踪") }

                            HeadingLabel {
                                Layout.topMargin: 2
                                text: qsTr("导随\n记录器")
                                font.pixelSize: Theme.fs(22)
                            }

                            Rectangle {
                                Layout.fillWidth: true
                                Layout.topMargin: 12
                                Layout.preferredHeight: 1

                                gradient: Gradient {
                                    orientation: Gradient.Horizontal
                                    GradientStop {
                                        position: 0.0
                                        color: Theme.eorzea ? Theme.ruleAccent : Theme.border
                                    }
                                    GradientStop {
                                        position: 1.0
                                        color: "transparent"
                                    }
                                }
                            }
                        }

                        ColumnLayout {
                            Layout.fillWidth: true
                            Layout.topMargin: 6
                            spacing: 2

                            Repeater {
                                // Lucide icons (components/Lucide.js) where the
                                // prototype had 01-06.
                                model: [
                                    { index: 0, icon: "layout-dashboard", label: qsTr("总览") },
                                    { index: 1, icon: "history", label: qsTr("历史记录") },
                                    { index: 7, icon: "notebook-pen", label: qsTr("全部心得") },
                                    { index: 2, icon: "swords", label: qsTr("副本统计") },
                                    { index: 3, icon: "chart-column", label: qsTr("职业统计") },
                                    { index: 4, icon: "activity", label: qsTr("捕获诊断") },
                                    { index: 5, icon: "settings", label: qsTr("设置") },
                                    { index: 6, icon: "clipboard-check", label: qsTr("对照核对") }
                                ]

                                delegate: NavItem {
                                    required property var modelData

                                    visible: modelData.index !== 6 || Runtime.app.maintainerToolsVisible
                                    Layout.fillWidth: true
                                    iconName: modelData.icon
                                    label: modelData.label
                                    current: modelData.index === Runtime.app.currentPage
                                    onClicked: Runtime.app.navigate(modelData.index)
                                }
                            }
                        }

                        Item {
                            Layout.fillHeight: true
                        }

                        // .status-panel: its own tint and border so it reads
                        // as a plate on the chrome rather than a nav card.
                        Card {
                            Layout.fillWidth: true
                            padding: 12
                            spacing: 7
                            flat: true
                            // Eorzea keeps the plate bare; harendotes frames it
                            // like its cards (the flame -> gold diagonal frame).
                            decorated: Theme.harendotes
                            fillColor: Theme.statusPanelBackground
                            borderColor: Theme.statusPanelBorder
                            borderWidth: 1

                            Repeater {
                                // Three rows only: FF14 / Npcap / 捕获. Collector, 协议 and
                                // 当前场次 belong to the capture page and the dashboard's
                                // 当前导随 card.
                                model: [
                                    { icon: "gamepad-2", key: qsTr("FF14"), value: Runtime.app.gameSelection.selectionRequired && Runtime.app.gameSelection.choices.length > 0 ? qsTr("待选择") : Runtime.app.ffxivRunning ? qsTr("运行中") : qsTr("未运行"), color: Runtime.app.gameSelection.selectionRequired && Runtime.app.gameSelection.choices.length > 0 ? Theme.orange : Runtime.app.ffxivRunning ? Theme.green : Theme.neutral400 },
                                    // Without a capture status nothing is known about Npcap (review K2-1).
                                    { icon: "network", key: qsTr("Npcap"), value: !window.npcapKnown ? Runtime.fmt.dash() : Runtime.app.npcapInstalled ? qsTr("就绪") : qsTr("未安装"), color: !window.npcapKnown ? Theme.neutral400 : Runtime.app.npcapInstalled ? Theme.green : Theme.orange },
                                    { icon: "radio", key: qsTr("捕获"), value: window.captureBadgeText(), color: window.captureBadgeColor() }
                                ]

                                delegate: RowLayout {
                                    id: statusRow
                                    required property var modelData

                                    Layout.fillWidth: true
                                    // Tighter than the prototype's 8: the icon column
                                    // must not cost the value its room (无法自动记录).
                                    spacing: 6

                                    // The row's Lucide icon is the status light: drawn in
                                    // the state colour.
                                    Image {
                                        objectName: "statusIcon"
                                        visible: Lucide.has(statusRow.modelData.icon)
                                        Layout.preferredWidth: 14
                                        Layout.preferredHeight: 14
                                        Layout.alignment: Qt.AlignVCenter
                                        sourceSize: Qt.size(Math.round(14 * window.pixelRatio), Math.round(14 * window.pixelRatio))
                                        source: visible ? Lucide.source(statusRow.modelData.icon, statusRow.modelData.color) : ""
                                        fillMode: Image.PreserveAspectFit
                                        smooth: true
                                    }

                                    Text {
                                        // Room for 4 CJK characters at 11 px (当前场次).
                                        Layout.preferredWidth: 46
                                        text: statusRow.modelData.key
                                        color: Theme.textSecondary
                                        font.pixelSize: Theme.fs(11)
                                        elide: Text.ElideRight
                                    }

                                    // A long state in a wordier language (English
                                    // "Can't record automatically") takes a second
                                    // line rather than an ellipsis.
                                    Text {
                                        Layout.fillWidth: true
                                        text: statusRow.modelData.value
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(11)
                                        font.bold: true
                                        wrapMode: Text.WordWrap
                                        maximumLineCount: 2
                                        elide: Text.ElideRight
                                    }
                                }
                            }
                        }
                    }
                }

                Rectangle {
                    Layout.preferredWidth: 1
                    Layout.fillHeight: true
                    color: Theme.eorzea ? Theme.gold3 : Theme.border
                }

                Item {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    clip: true

                    Item {
                        anchors.fill: parent
                        anchors.leftMargin: 28
                        anchors.rightMargin: 28
                        anchors.topMargin: 22
                        anchors.bottomMargin: 32

                        PageHost {
                            index: 0
                            anchors.fill: parent

                        DashboardPage {
                            anchors.fill: parent
                            onOpenManualRequested: window.openCreateDialog()
                            onOpenCaptureRequested: Runtime.app.navigate(4)
                            // 成就卡片上的「修改」：要改的是安装前已完成次数，所以打开
                            // 设置 · 成就，光标放进那个输入框。
                            onOpenSettingsRequested: {
                                Runtime.app.navigate(5)
                                settingsPage.editBaseline()
                            }
                            onOpenReflectionsRequested: Runtime.app.navigate(7)
                            onReflectRequested: function(run, kicker) {
                                window.openReflection(run, kicker)
                            }
                            onOpenRunHistoryRequested: function(run) {
                                historyPage.detailTab = "refl"
                                if (Runtime.app.openHistoryForRun)
                                    Runtime.app.openHistoryForRun(run)
                                else {
                                    Runtime.app.navigate(1)
                                    Runtime.app.selectRun(run)
                                }
                            }
                        }
                        }

                        PageHost {
                            index: 1
                            anchors.fill: parent

                        HistoryPage {
                            id: historyPage

                            anchors.fill: parent
                            // The notice floats over the page's bottom-right corner,
                            // where the pagination row is. Whatever of the page lies
                            // below the notice's top edge is kept clear of rows, so a
                            // full page and its pager stay visible while it shows.
                            reservedBottom: {
                                if (!recordingNotice.visible)
                                    return 0
                                // mapFromItem is not a dependency; these are.
                                Theme.trackBindingDependency(recordingNotice.height)
                                Theme.trackBindingDependency(historyPage.height)
                                Theme.trackBindingDependency(window.height)
                                Theme.trackBindingDependency(window.width)
                                return Math.max(0, historyPage.height
                                                   - historyPage.mapFromItem(recordingNotice, 0, 0).y)
                            }
                            onOpenManualRequested: window.openCreateDialog()
                            onOpenImportRequested: window.openImportDialog()
                            onOpenCorrectRequested: window.openCorrectDialog()
                            onOpenDeleteRequested: window.openReasonDialog("delete")
                            onOpenRestoreRequested: window.openReasonDialog("restore")
                            onOpenUndoRequested: window.openReasonDialog("undo")
                            onOpenReviewRequested: window.openReasonDialog("review")
                            onReflectRequested: function(run) {
                                window.openReflection(run, "")
                            }
                            onShareRequested: function(run) { window.openShare(run) }
                        }
                        }

                        PageHost {
                            index: 2
                            anchors.fill: parent

                        DungeonsPage {
                            anchors.fill: parent
                        }
                        }

                        PageHost {
                            index: 3
                            anchors.fill: parent

                        JobsPage {
                            anchors.fill: parent
                        }
                        }

                        PageHost {
                            index: 4
                            anchors.fill: parent

                        CapturePage {
                            anchors.fill: parent
                            onOpenCalibrationRequested: calibrationDialog.openDialog()
                            onOpenRegionSettingsRequested: {
                                Runtime.app.navigate(5)
                                settingsPage.openRegionSettings()
                            }
                        }
                        }

                        PageHost {
                            index: 6
                            anchors.fill: parent

                        CandidateReviewPage {
                            anchors.fill: parent
                        }
                        }

                        PageHost {
                            index: 7
                            anchors.fill: parent
                            ReflectionsPage {
                                id: reflectionsPage
                                anchors.fill: parent
                                reservedBottom: {
                                    if (!recordingNotice.visible) return 0
                                    Theme.trackBindingDependency(recordingNotice.height)
                                    Theme.trackBindingDependency(reflectionsPage.height)
                                    Theme.trackBindingDependency(window.height)
                                    Theme.trackBindingDependency(window.width)
                                    return Math.max(0, reflectionsPage.height
                                        - reflectionsPage.mapFromItem(recordingNotice, 0, 0).y)
                                }
                                onReflectRequested: function(run) { window.openReflection(run, "") }
                                onShareRequested: function(run) { window.openShare(run) }
                                onShareBatchRequested: function(runs) { reflectionShareDialog.openForRuns(runs) }
                                onOpenRunRequested: function(run) {
                                    Runtime.app.openHistoryForRun(run)
                                    historyPage.detailTab = "refl"
                                }
                            }
                        }

                        PageHost {
                            index: 5
                            anchors.fill: parent

                        SettingsPage {
                            id: settingsPage

                            anchors.fill: parent
                            onOpenBaselineRequested: baselineDialog.openDialog(true)
                            onOpenDisclosureRequested: disclosureDialog.openDialog()
                            onOpenImportRequested: window.openImportDialog()
                            releaseNotesAvailable: window.releaseNotesController !== null
                                && window.releaseNotesController.text.length > 0
                            onOpenReleaseNotesRequested: window.openCurrentReleaseNotes()
                        }
                        }
                    }
                }
            }
        }
    }

    // The circular 深色 / 浅色 switch over the whole shell (docs/ui-design.md 动效).
    ThemeReveal {
        id: themeReveal
        objectName: "themeReveal"
        anchors.fill: parent
        source: shell
    }

    /// Runs `change` (a theme switch) behind the circular reveal, centred on
    /// `origin` - the control that asked for it.
    function revealThemeChange(change, origin) {
        const point = origin
                ? origin.mapToItem(themeReveal, origin.width / 2, origin.height / 2)
                : Qt.point(themeReveal.width - 40, 20)
        themeReveal.run(change, point.x, point.y)
    }

    EditRunDialog {
        id: editDialog
        objectName: "editRunDialog"
        anchors.centerIn: Overlay.overlay
        // Correcting a record needs the full duty and battle-job catalogues, not only
        // what this player has already recorded: the record worth correcting is
        // usually the one whose duty the software could not name.
        dutyOptions: Runtime.app.dutyCatalogOptions
        jobOptions: Runtime.app.battleJobOptions
        onCreateRequested: function(fields, reason) {
            Runtime.app.createManualRun(fields, reason)
        }
        onCorrectRequested: function(changes, reason) {
            Runtime.app.correctSelectedRun(changes, reason, editDialog.runData ? editDialog.runData.run_id : "",
                                   editDialog.runData ? editDialog.runData.revision : -1)
        }
    }

    ReflectionDialog {
        id: reflectionDialog
        objectName: "reflectionDialog"
        parent: Overlay.overlay
        anchors.centerIn: parent
        managedPresentation: true
        onReviewRequested: function(run) {
            window.bringToFront()
            // openHistoryForRun never toggles an already selected record off,
            // which selectRun does (review K1-3 / L1-2).
            Runtime.app.openHistoryForRun(run)
            Qt.callLater(function() { window.openReasonDialog("review") })
        }
        onHostClosing: recoveryReminder.hide()
        onDismissing: function(runId, completed) {
            // Only the popup is a reminder put away; the editor in the main
            // window just closes (review K1-4 / L2-5). A record found deleted
            // with no draft left nothing in 待处理 to reopen (review RV3-3).
            if (completed || !runId || !reflectionDialog.standalone) return
            const pending = Runtime.app.recovery ? Runtime.app.recovery.items : []
            let kept = false
            for (let i = 0; i < pending.length && !kept; ++i)
                kept = (pending[i].runId || pending[i].run_id) === runId
            if (!kept) return
            recoveryNotice.runId = runId
            recoveryNotice.x = recoveryReminder.x
            recoveryNotice.y = recoveryReminder.y
            recoveryNotice.show()
            recoveryNoticeTimer.restart()
        }
    }

    RecoveryReminderWindow { id: recoveryReminder; editor: reflectionDialog; overlayWindow: statisticsFloat }
    RecoveryInbox { id: recoveryInbox; anchors.centerIn: parent }
    Window {
        id: recoveryNotice
        objectName: "recoveryNoticeWindow"
        property string runId: ""
        transientParent: null
        flags: Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint
        visible: false
        // `.ov-toast`: a pill where the popup was - 已收起到待处理, with a way back.
        width: Math.min(420, noticeContent.implicitWidth + 24)
        height: noticeContent.implicitHeight + 16
        color: "transparent"
        title: qsTr("导随记录器 · 已收起提醒")
        Rectangle {
            anchors.fill: parent
            radius: height / 2
            color: Qt.rgba(Theme.surface.r, Theme.surface.g, Theme.surface.b, 0.94)
            border.width: 1
            border.color: Theme.eorzea ? Theme.gold3 : Theme.borderStrong
        }
        RowLayout {
            id: noticeContent
            anchors.fill: parent
            anchors.leftMargin: 14
            anchors.rightMargin: 6
            anchors.topMargin: 8
            anchors.bottomMargin: 8
            spacing: 8
            OverlayIcon { name: "crest"; color: Theme.eorzea ? Theme.gold2 : Theme.accent; Layout.alignment: Qt.AlignVCenter }
            Text {
                Layout.fillWidth: true
                Layout.maximumWidth: 260
                text: Runtime.app.recovery.error.length > 0 ? Runtime.app.recovery.error : qsTr("已收起到待处理，记录和草稿已保留")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12.5)
                font.weight: Font.DemiBold
                wrapMode: Text.WordWrap
            }
            AppButton {
                objectName: "reopenDeferredButton"
                text: qsTr("重新打开")
                variant: "ghost"
                compact: true
                onClicked: { recoveryNotice.hide(); Runtime.app.recovery.requestOpen(recoveryNotice.runId) }
            }
            OverlayIconButton { iconName: "close"; tip: qsTr("关闭"); onClicked: recoveryNotice.hide() }
        }
    }
    Timer { id: recoveryNoticeTimer; interval: 6000; onTriggered: recoveryNotice.hide() }

    ReflectionShareDialog {
        id: reflectionShareDialog
        anchors.centerIn: Overlay.overlay
    }

    ImportRecordsDialog {
        id: importDialog
        objectName: "importRecordsDialog"
        anchors.centerIn: Overlay.overlay
    }

    CalibrationDialog {
        id: calibrationDialog
        anchors.centerIn: Overlay.overlay
    }

    // 这个软件做什么、不做什么: an information window opened from 设置 · 关于. It
    // is no step of the first start (owner's decision, 2026-10-04).
    DisclosureDialog {
        id: disclosureDialog
        anchors.centerIn: Overlay.overlay
    }

    ReleaseNotesDialog {
        id: releaseNotesDialog
        notesController: window.releaseNotesController
        onLanguageSettingsRequested: {
            Runtime.app.navigate(5)
            settingsPage.openLanguageSettings()
        }
    }

    CalibrationEvidenceDialog {
        evidenceController: typeof Runtime.calibrationEvidence !== "undefined" ? Runtime.calibrationEvidence : null
    }

    BaselineDialog {
        id: baselineDialog
        objectName: "baselineDialog"
        anchors.centerIn: Overlay.overlay
        goalCount: Runtime.app.goalCount
        baselineCount: Runtime.app.baselineCount
        recordedCount: Runtime.app.dashboard.achievement_progress !== undefined
                       ? Math.max(0, Number(Runtime.app.dashboard.achievement_progress) - Runtime.app.baselineCount) : 0
        settingsLoaded: Runtime.app.achievementSettingsLoaded
        busy: Runtime.app.baselineSaving
        // The goal typed in the dialog travels with the signal: writing it into
        // goalCount would cut that binding, and a guide reopened after 设置 · 目标
        // changed the goal would offer, and save, the old one (review OL-1).
        // The guide stays open until the answer (onMutationFailed / onBaselineSaved
        // above): a save that fails must not complete the first run (DT6-X1).
        onSaved: function(baseline, goal, reason) {
            Runtime.app.updateAchievementBaseline(goal, baseline, reason)
        }
        onSkipped: function(goal, reason) {
            Runtime.app.updateAchievementBaseline(goal, 0, reason)
        }
    }

    Dialog {
        id: reasonDialog
        objectName: "reasonDialog"
        modal: true
        Overlay.modal: Rectangle { color: Theme.modalScrim(reasonDialog.palette.shadow) }
        width: 460
        padding: 20
        anchors.centerIn: Overlay.overlay
        // While its request is out the window cannot be dismissed, so the reply
        // lands on the form that sent it; a stray click outside never discards a
        // half-written reason either.
        closePolicy: window.reasonSubmitting ? Popup.NoAutoClose : Popup.CloseOnEscape

        background: DialogFrame {}

        contentItem: ColumnLayout {
            spacing: 12

            HeadingLabel {
                Layout.fillWidth: true
                text: window.reasonDialogTitle(window.reasonAction)
                font.pixelSize: Theme.dialogTitleSize(20)
            }

            Text {
                Layout.fillWidth: true
                text: window.reasonDialogHint(window.reasonAction)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(13)
                wrapMode: Text.WordWrap
            }

            FieldLabel {
                text: qsTr("原因")
            }

            StyledTextArea {
                id: reasonField
                objectName: "reasonField"
                Layout.fillWidth: true
                Layout.preferredHeight: 100
                placeholderText: qsTr("请填写原因，方便以后查看这次修改。")
            }

            Text {
                Layout.fillWidth: true
                text: window.reasonDialogError
                visible: text.length > 0
                color: Theme.red
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            Rectangle {
                Layout.fillWidth: true
                Layout.preferredHeight: 1
                Layout.topMargin: 4
                color: Theme.border
            }

            RowLayout {
                Layout.fillWidth: true

                AppButton {
                    objectName: "reasonCancelButton"
                    text: qsTr("取消")
                    enabled: !window.reasonSubmitting
                    onClicked: reasonDialog.close()
                }

                Item { Layout.fillWidth: true }

                AppButton {
                    objectName: "reasonConfirmButton"
                    variant: "primary"
                    enabled: !window.reasonSubmitting
                    text: window.reasonSubmitting
                          ? qsTr("提交中…")
                          : window.reasonDialogConfirm(window.reasonAction)
                    onClicked: {
                        if (!reasonField.text.trim()) {
                            window.reasonDialogError = qsTr("请先填写这次操作的原因。")
                            return
                        }
                        // The request goes to the selected record; without one
                        // nothing would answer and the locked window would stay.
                        if (!Runtime.app.hasSelection || !Runtime.app.selectedRun.run_id) {
                            window.reasonDialogError = qsTr("没有选中的记录，请关闭后重新选择。")
                            return
                        }
                        window.reasonSubmitting = true
                        const reason = reasonField.text.trim()
                        const runId = window.reasonRunId
                        const revision = window.reasonRevision
                        switch (window.reasonAction) {
                        case "restore": Runtime.app.restoreSelectedRun(reason, runId, revision); break
                        case "undo": Runtime.app.undoSelectedRunRevision(reason, runId, revision); break
                        case "review": Runtime.app.confirmSelectedRunReview(reason, runId, revision); break
                        default: Runtime.app.softDeleteSelectedRun(reason, runId, revision); break
                        }
                    }
                }
            }
        }
    }

    Toast {
        anchors.left: parent.left
        anchors.bottom: parent.bottom
        anchors.margins: 24
        message: Runtime.app.toastMessage
    }

    // Screenshot helpers: --mock-open-detail / --mock-open-edit select the first
    // history row once the mock data has arrived, so the panel and the dialog are
    // captured without a human clicking.
    Timer {
        id: screenshotSetup
        interval: 220
        repeat: true
        running: window.forceOpenDetail
        onTriggered: {
            const run = Runtime.app.runs.runAt(0)
            if (!run || !run.run_id)
                return
            running = false
            Runtime.app.navigate(1)
            Runtime.app.selectRun(run)
            if (window.forceDetailTab.length > 0)
                historyPage.detailTab = window.forceDetailTab
            if (window.forceOpenEdit)
                Qt.callLater(function() {
                    window.openCorrectDialog()
                    editDialog.goToStep(window.forceWizardStep)
                })
            if (window.forceOpenReflection)
                Qt.callLater(function() { window.openReflection(run, qsTr("补录笔记")) })
        }
    }

    // --mock-open-create [--mock-wizard-step N]: the 新增遗漏记录 wizard, once
    // the 最近打过 query had time to answer.
    readonly property bool forceOpenCreate: typeof Runtime.forceOpenCreate !== "undefined" ? Runtime.forceOpenCreate : false
    readonly property int forceWizardStep: typeof Runtime.forceWizardStep !== "undefined" ? Runtime.forceWizardStep : 1

    Timer {
        interval: 220
        running: window.forceOpenCreate
        onTriggered: {
            window.openCreateDialog()
            editDialog.goToStep(window.forceWizardStep)
        }
    }

    Component.onCompleted: {
        const previewNotes = typeof Runtime.forceReleaseNotes !== "undefined" && Runtime.forceReleaseNotes
        const suppressNotes = typeof Runtime.suppressReleaseNotes !== "undefined" && Runtime.suppressReleaseNotes
        releaseNotesWaiting = releaseNotesController !== null
            && releaseNotesController.text.length > 0
            && (previewNotes || (!suppressNotes && releaseNotesController.pending))
        if (typeof Runtime.reflections !== "undefined")
            Runtime.reflections.setActive(Runtime.app.currentPage === 7)
        if (typeof Runtime.forceOpenImport !== "undefined" && Runtime.forceOpenImport) {
            Qt.callLater(window.openImportDialog)
            return
        }
        if (typeof Runtime.forceOpenShare !== "undefined" && Runtime.forceOpenShare) {
            shareSetup.start()
            return
        }
        // --show-disclosure opens the explanation for a screenshot.
        if (window.forceDisclosure) {
            disclosureDialog.openDialog()
            return
        }
        // A first start goes straight to the baseline question; --mock-first-run
        // asks for it on any run.
        if (window.forceBaselineDialog || (Runtime.app.firstRun && !window.suppressOnboarding))
            baselineDialog.openDialog()
    }

    Timer {
        id: shareSetup
        interval: 150
        repeat: true
        running: false
        onTriggered: {
            const recent = Runtime.app.reflectionSummary.recent || []
            if (recent.length === 0) return
            stop()
            const run = Object.assign({}, recent[0].run)
            // GetReflectionSummary stores its entry separately from the run.
            run.reflection = recent[0].reflection
            window.openShare(run)
        }
    }
}
