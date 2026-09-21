import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
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

    function presentRecordingAlert() {
        if (App.maintainerToolsVisible || !window.visible || window.visibility === Window.Minimized) return
        if (App.recording.pendingAlert && App.recording.blocked && !recordingAlert.opened)
            recordingAlert.open()
    }
    onVisibilityChanged: Qt.callLater(window.presentRecordingAlert)
    Connections {
        target: App.recording
        function onChanged() {
            if (!App.recording.blocked) recordingAlert.close()
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
                text: App.recording.message
                color: Theme.textPrimary
                textFormat: Text.PlainText
                wrapMode: Text.WordWrap
            }
            RowLayout {
                Layout.alignment: Qt.AlignRight
                AppButton {
                    text: qsTr("查看诊断")
                    onClicked: { App.recording.acknowledge(); recordingAlert.close(); App.navigate(4) }
                }
                AppButton {
                    objectName: "automaticRecordingAcknowledge"
                    text: qsTr("我知道了")
                    variant: "primary"
                    onClicked: { App.recording.acknowledge(); recordingAlert.close() }
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

    readonly property bool suppressOnboarding: typeof SuppressOnboarding !== "undefined" ? SuppressOnboarding : false
    readonly property bool forceBaselineDialog: typeof ForceBaselineDialog !== "undefined" ? ForceBaselineDialog : false
    readonly property bool forceDisclosure: typeof ForceDisclosure !== "undefined" ? ForceDisclosure : false
    readonly property bool forceOpenDetail: typeof ForceOpenDetail !== "undefined" ? ForceOpenDetail : false
    readonly property bool forceOpenEdit: typeof ForceOpenEdit !== "undefined" ? ForceOpenEdit : false
    readonly property bool forceOpenReflection: typeof ForceOpenReflection !== "undefined" ? ForceOpenReflection : false
    readonly property string forceDetailTab: typeof ForceDetailTab !== "undefined" ? ForceDetailTab : ""

    property string reasonAction: ""
    property string reasonDialogError: ""
    property bool reasonSubmitting: false

    // Colour of the title bar's Collector dot; the state comes from
    // CollectorProcess through AppController.
    function collectorColor() {
        switch (App.collectorState) {
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
        if (App.maintainerToolsVisible) return App.captureModeCompactText
        return App.recording.state === "listening" ? qsTr("自动监听中")
             : App.recording.silent ? qsTr("监听中·收不到数据")
             : App.recording.state === "waiting" ? qsTr("等待游戏")
             : App.recording.state === "calibrating" ? qsTr("校准中")
             : App.recording.state === "calibration_ready" ? qsTr("待核对")
             : App.recording.state === "calibration_blocked" ? qsTr("无法自动记录")
             : App.recording.blocked ? qsTr("无法自动记录") : qsTr("检查中")
    }

    // Green claims that something is being recorded, so it is reserved for the
    // listening state; states needing attention are amber, the rest neutral.
    function captureBadgeColor() {
        return App.recording.attention ? Theme.orange
             : App.recording.state === "listening" ? Theme.green : Theme.neutral400
    }

    function openCreateDialog() {
        editDialog.openForCreate()
    }

    function openCorrectDialog() {
        if (!App.hasSelection)
            return
        editDialog.openForRun(App.selectedRun)
    }

    // Used by the dashboard's 导随心得 panel, the history detail panel and the
    // automatic prompt after a COMPLETED run.
    function openReflection(run, kicker) {
        reflectionDialog.openForRun(run, kicker || "")
    }

    // Every reason-carrying action shares one dialog: 软删除 / 恢复 / 撤销修正 /
    // 确认复核. All four are refused by the Collector without a reason
    // (ERR_REASON_REQUIRED), so none of them may have a one-click path.
    function reasonDialogTitle(actionName) {
        switch (actionName) {
        case "restore": return qsTr("恢复记录")
        case "undo": return qsTr("撤销上一次修正")
        case "review": return qsTr("确认待复核记录")
        default: return qsTr("软删除记录")
        }
    }

    function reasonDialogHint(actionName) {
        switch (actionName) {
        case "restore":
            return qsTr("恢复会重新回到统计与历史列表中。")
        case "undo":
            return qsTr("撤销会把上一次修正的字段回放为原值，并作为一条新的修订追加；"
                        + "既有的修正记录不会被删除。")
        case "review":
            return qsTr("确认表示你已核对这条记录。结果不会被改写，"
                        + "只是取消“待复核”标记，并作为一条新的修订记入审计。"
                        + "要改成“通关”或“离开”，请用“修正”填写结果。")
        default:
            return qsTr("软删除不会物理移除记录，但会从默认统计中排除。")
        }
    }

    function reasonDialogConfirm(actionName) {
        switch (actionName) {
        case "restore": return qsTr("确认恢复")
        case "undo": return qsTr("确认撤销")
        case "review": return qsTr("确认已复核")
        default: return qsTr("确认软删除")
        }
    }

    function openReasonDialog(actionName) {
        if (!App.hasSelection)
            return
        reasonAction = actionName
        reasonDialogError = ""
        reasonSubmitting = false
        reasonField.text = ""
        reasonDialog.title = window.reasonDialogTitle(actionName)
        reasonDialog.open()
    }

    Connections {
        target: App

        // Both dialogs stay open until the Collector answers, so its refusal is
        // shown in the form the user is looking at; the toast only repeats it.
        function onMutationFailed(code, message) {
            const text = (message && message.length > 0 ? message : code)
                         + " (" + code + ")"
            if (reasonDialog.visible) {
                reasonDialogError = text
                reasonSubmitting = false
            } else if (editDialog.visible) {
                editDialog.externalErrorText = text
            }
        }

        function onMutationSucceeded(kind, runId, revision, auditEventId) {
            if (kind === "create" || kind === "correct") {
                // 回应带着它属于哪一条记录，交由对话框核对后才生效：迟到的回应
                // 不能把用户此刻正在填的另一张表单静默关掉（审查第 4 条）。
                if (!editDialog.acceptSubmission(runId))
                    return
                if (kind === "create")
                    App.navigate(1)
                return
            }
            reasonSubmitting = false
            reasonDialog.close()
        }
    }

    // DUTY_RESULT arrived and the run has no reflection yet: ask for one now.
    Connections {
        target: App
        ignoreUnknownSignals: true

        function onReflectionPromptRequested(run) {
            window.openReflection(run, qsTr("刚刚完成"))
        }
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
            onTopWashChanged: requestPaint()
            onBottomWashChanged: requestPaint()
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
                wash(ctx, width, height, 600, 400, bottomWash)
            }
        }


        // The recording notice: a card floating over the page rather than a strip
        // in the window chrome, so it reads as a message from the software.
        Rectangle {
            id: recordingNotice
            objectName: "automaticRecordingBanner"

            readonly property string message: App.recording.message
            // Dismissal lasts until the software has something else to say.
            property string dismissed: ""

            visible: !App.maintainerToolsVisible
                     && (App.recording.attention || App.recording.retryAvailable)
                     && recordingNotice.dismissed !== recordingNotice.message
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
                    visible: App.calibration.state === "READY"
                    variant: "primary"
                    text: qsTr("核对并启用")
                    onClicked: calibrationDialog.openDialog()
                }
                AppButton {
                    visible: App.recording.retryAvailable
                    text: qsTr("重试自动记录")
                    onClicked: App.recording.retry()
                }
                AppButton { text: qsTr("查看诊断"); onClicked: App.navigate(4) }
                AppButton {
                    objectName: "automaticRecordingNoticeDismiss"
                    text: qsTr("收起")
                    onClicked: recordingNotice.dismissed = recordingNotice.message
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
                            text: "v" + App.appVersion
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
                        text: App.collectorStatusText
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
                        text: App.dark ? qsTr("浅色") : qsTr("深色")
                        iconName: App.dark ? "sun" : "moon"
                        onClicked: window.revealThemeChange(function() { App.toggleTheme() },
                                                            themeToggleButton)
                    }

                    // Window controls (frameless window).
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
                                    { index: 2, icon: "swords", label: qsTr("副本统计") },
                                    { index: 3, icon: "users", label: qsTr("职业统计") },
                                    { index: 4, icon: "activity", label: qsTr("捕获诊断") },
                                    { index: 5, icon: "settings", label: qsTr("设置") },
                                    { index: 6, icon: "clipboard-check", label: qsTr("对照核对") }
                                ]

                                delegate: NavItem {
                                    required property var modelData

                                    visible: modelData.index !== 6 || App.maintainerToolsVisible
                                    Layout.fillWidth: true
                                    iconName: modelData.icon
                                    label: modelData.label
                                    current: modelData.index === App.currentPage
                                    onClicked: App.navigate(modelData.index)
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
                                    { icon: "gamepad-2", key: qsTr("FF14"), value: App.ffxivRunning ? qsTr("运行中") : qsTr("未运行"), color: App.ffxivRunning ? Theme.green : Theme.neutral400 },
                                    { icon: "network", key: qsTr("Npcap"), value: App.npcapInstalled ? qsTr("就绪") : qsTr("未安装"), color: App.npcapInstalled ? Theme.green : Theme.orange },
                                    { icon: "radio", key: qsTr("捕获"), value: window.captureBadgeText(), color: window.captureBadgeColor() }
                                ]

                                delegate: RowLayout {
                                    required property var modelData

                                    Layout.fillWidth: true
                                    // Tighter than the prototype's 8: the icon column
                                    // must not cost the value its room (无法自动记录).
                                    spacing: 6

                                    // The row's Lucide icon is the status light: drawn in
                                    // the state colour.
                                    Image {
                                        objectName: "statusIcon"
                                        visible: Lucide.has(modelData.icon)
                                        Layout.preferredWidth: 14
                                        Layout.preferredHeight: 14
                                        Layout.alignment: Qt.AlignVCenter
                                        sourceSize: Qt.size(Math.round(14 * window.pixelRatio), Math.round(14 * window.pixelRatio))
                                        source: visible ? Lucide.source(modelData.icon, modelData.color) : ""
                                        fillMode: Image.PreserveAspectFit
                                        smooth: true
                                    }

                                    Text {
                                        // Room for 4 CJK characters at 11 px (当前场次).
                                        Layout.preferredWidth: 46
                                        text: modelData.key
                                        color: Theme.textSecondary
                                        font.pixelSize: Theme.fs(11)
                                        elide: Text.ElideRight
                                    }

                                    Text {
                                        Layout.fillWidth: true
                                        text: modelData.value
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(11)
                                        font.bold: true
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
                            onOpenCaptureRequested: App.navigate(4)
                            onOpenSettingsRequested: App.navigate(5)
                            onOpenPendingReviewRequested: App.showPendingReview()
                            onReflectRequested: function(run, kicker) {
                                window.openReflection(run, kicker)
                            }
                            onOpenRunHistoryRequested: function(run) {
                                historyPage.detailTab = "refl"
                                if (App.openHistoryForRun)
                                    App.openHistoryForRun(run)
                                else {
                                    App.navigate(1)
                                    App.selectRun(run)
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
                            onOpenManualRequested: window.openCreateDialog()
                            onOpenCorrectRequested: window.openCorrectDialog()
                            onOpenDeleteRequested: window.openReasonDialog("delete")
                            onOpenRestoreRequested: window.openReasonDialog("restore")
                            onOpenUndoRequested: window.openReasonDialog("undo")
                            onOpenReviewRequested: window.openReasonDialog("review")
                            onReflectRequested: function(run) {
                                window.openReflection(run, "")
                            }
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
                            index: 5
                            anchors.fill: parent

                        SettingsPage {
                            anchors.fill: parent
                            onOpenBaselineRequested: baselineDialog.openDialog()
                            onOpenDisclosureRequested: {
                                App.reopenDisclosure()
                                disclosureDialog.openDialog()
                            }
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
        anchors.centerIn: Overlay.overlay
        // Correcting a record needs the full duty and battle-job catalogues, not only
        // what this player has already recorded: the record worth correcting is
        // usually the one whose duty the software could not name.
        dutyOptions: App.dutyCatalogOptions
        jobOptions: App.battleJobOptions
        onCreateRequested: function(fields, reason) {
            App.createManualRun(fields, reason)
        }
        onCorrectRequested: function(changes, reason) {
            App.correctSelectedRun(changes, reason, editDialog.runData ? editDialog.runData.run_id : "")
        }
    }

    ReflectionDialog {
        id: reflectionDialog
        anchors.centerIn: Overlay.overlay
    }

    CalibrationDialog {
        id: calibrationDialog
        anchors.centerIn: Overlay.overlay
    }

    // The disclosure comes first on a first run: the baseline question is not
    // asked before the user has been told what the software does.
    DisclosureDialog {
        id: disclosureDialog
        anchors.centerIn: Overlay.overlay
        onAcknowledged: {
            App.acceptDisclosure()
            close()
            if (App.firstRun && !window.suppressOnboarding)
                baselineDialog.openDialog()
        }
    }

    BaselineDialog {
        id: baselineDialog
        anchors.centerIn: Overlay.overlay
        goalCount: App.goalCount
        baselineCount: App.baselineCount
        onSaved: function(baseline, reason) {
            App.updateAchievementBaseline(goalCount, baseline, reason)
            App.completeFirstRun()
            close()
        }
        onSkipped: function(reason) {
            App.updateAchievementBaseline(goalCount, 0, reason)
            App.completeFirstRun()
            close()
        }
    }

    Dialog {
        id: reasonDialog
        modal: true
        Overlay.modal: Rectangle { color: Theme.modalScrim(reasonDialog.palette.shadow) }
        width: 460
        padding: 20
        anchors.centerIn: Overlay.overlay

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
                Layout.fillWidth: true
                Layout.preferredHeight: 100
                placeholderText: qsTr("说明这次操作的原因（必填）。")
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
                    text: qsTr("取消")
                    onClicked: reasonDialog.close()
                }

                Item { Layout.fillWidth: true }

                AppButton {
                    variant: "primary"
                    enabled: !window.reasonSubmitting
                    text: window.reasonSubmitting
                          ? qsTr("提交中…")
                          : window.reasonDialogConfirm(window.reasonAction)
                    onClicked: {
                        if (!reasonField.text.trim()) {
                            window.reasonDialogError = qsTr("必须填写操作原因。")
                            return
                        }
                        window.reasonSubmitting = true
                        const reason = reasonField.text.trim()
                        switch (window.reasonAction) {
                        case "restore": App.restoreSelectedRun(reason); break
                        case "undo": App.undoSelectedRunRevision(reason); break
                        case "review": App.confirmSelectedRunReview(reason); break
                        default: App.softDeleteSelectedRun(reason); break
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
        message: App.toastMessage
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
            const run = App.runs.runAt(0)
            if (!run || !run.run_id)
                return
            running = false
            App.navigate(1)
            App.selectRun(run)
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
    readonly property bool forceOpenCreate: typeof ForceOpenCreate !== "undefined" ? ForceOpenCreate : false
    readonly property int forceWizardStep: typeof ForceWizardStep !== "undefined" ? ForceWizardStep : 1

    Timer {
        interval: 220
        running: window.forceOpenCreate
        onTriggered: {
            window.openCreateDialog()
            editDialog.goToStep(window.forceWizardStep)
        }
    }

    Component.onCompleted: {
        if (window.forceDisclosure
            || (!App.disclosureAcknowledged && !suppressOnboarding)) {
            disclosureDialog.openDialog()
            return
        }
        if (window.forceBaselineDialog || (App.firstRun && !suppressOnboarding))
            baselineDialog.openDialog()
    }
}
