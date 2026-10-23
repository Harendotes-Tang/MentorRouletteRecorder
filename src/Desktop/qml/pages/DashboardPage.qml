pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

ScrollView {
    id: page
    objectName: "dashboardPage"
    readonly property var visibleResultBuckets: Runtime.app.resultBuckets.filter(bucket =>
        bucket.result !== "UNKNOWN" || Number(bucket.count || 0) !== 0)
    readonly property bool recoveryBlocked: !!Runtime.app.captureStatus.path_recovery
        && Runtime.app.captureStatus.path_recovery.state === "RECOVERING"
        && !Runtime.app.recording.recovering && Runtime.app.recording.blocked

    readonly property int resultTotal: {
        let total = 0
        for (let index = 0; index < Runtime.app.resultBuckets.length; ++index)
            total += Number(Runtime.app.resultBuckets[index].count || 0)
        return total
    }

    // The Collector's own figures, never baseline + completed_count: a COMPLETED
    // run that does not count towards the goal is in completed_count only
    // (docs/statistics-definitions.md; review OK-1).
    readonly property int achievementProgress: Math.max(0, Number(Runtime.app.dashboard.achievement_progress || 0))
    readonly property int achievementRemaining: Runtime.app.dashboard.remaining !== undefined
        ? Math.max(0, Number(Runtime.app.dashboard.remaining))
        : Math.max(0, Runtime.app.goalCount - page.achievementProgress)

    signal openManualRequested()
    signal openCaptureRequested()
    signal openSettingsRequested()
    // Opens ReflectionDialog for `run` with the given kicker.
    signal reflectRequested(var run, string kicker)
    // Jumps to the history page with `run` selected on the 心得 tab.
    signal openRunHistoryRequested(var run)
    signal openReflectionsRequested()

    readonly property var reflectionSummary: (typeof Runtime.app !== "undefined" && Runtime.app.reflectionSummary)
                                             ? Runtime.app.reflectionSummary : ({})
    readonly property var recentReflections: reflectionSummary.recent
                                             ? reflectionSummary.recent : []
    // True once GetReflectionSummary has answered; until then the counts are
    // unknown and render as a dash rather than a 0 this process invented.
    readonly property bool reflectionSummaryLoaded: reflectionSummary.reflection_count !== undefined
    readonly property string reflectionCountText: reflectionSummaryLoaded
        ? String(Number(reflectionSummary.reflection_count)) : Runtime.fmt.dash()
    readonly property string pendingReflectionCountText: reflectionSummaryLoaded
        ? String(Number(reflectionSummary.pending_completed_count)) : Runtime.fmt.dash()

    function backfillReflection() {
        if (!page.reflectionSummaryLoaded) {
            if (typeof Runtime.app !== "undefined" && Runtime.app.showToast)
                Runtime.app.showToast(qsTr("笔记数据尚未加载，请稍后再试"))
            return
        }
        const pending = page.reflectionSummary.next_pending
        if (pending && pending.run_id)
            page.reflectRequested(pending, qsTr("补录笔记"))
        else if (typeof Runtime.app !== "undefined" && Runtime.app.showToast)
            Runtime.app.showToast(qsTr("所有通关记录都已写过笔记"))
    }

    // 还差 N 次: one sentence for the translators; the figure between its two
    // halves is drawn in its own, larger type.
    readonly property var remainingParts: qsTr("还差 %1 次").split("%1")

    // The header's date. The app lives in the tray for days, so the date is
    // read again every minute instead of once at start (review OK-9), and after
    // a language switch: the weekday is a word.
    property string todayText: Theme.live(Runtime.fmt.dateWithWeekday(new Date().toISOString()))

    Timer {
        objectName: "dashboardDateTimer"
        interval: 60 * 1000
        repeat: true
        running: true
        // A fresh binding, evaluated now and again on the next language switch.
        onTriggered: page.todayText = Qt.binding(function() {
            return Theme.live(Runtime.fmt.dateWithWeekday(new Date().toISOString()))
        })
    }

    clip: true
    contentWidth: availableWidth
    ScrollBar.horizontal.policy: ScrollBar.AlwaysOff

    ColumnLayout {
        width: page.availableWidth
        spacing: 20

        PageHeader {
            objectName: "dashboardHeader"
            title: qsTr("总览")
            subtitle: page.todayText
        }

        // The Collector found a newer release. Notify only: nothing is
        // downloaded or installed by this software.
        UpdateNotice {}

        GameSelectionPanel {}

        Flow {
            Layout.fillWidth: true
            spacing: 8
            AppButton {
                text: qsTr("新增记录")
                onClicked: page.openManualRequested()
            }

            AppButton {
                visible: Runtime.app.maintainerToolsVisible
                text: Runtime.app.captureActionLabel
                variant: "primary"
                enabled: Runtime.app.captureActionEnabled
                onClicked: Runtime.app.toggleCapture()
            }
        }

        // Capture started after the client logged in: nothing decodes while the
        // status stays a green RUNNING (docs/live-validation-guide.md section 6).
        Rectangle {
            objectName: "dashboardCaptureHealth"
            Layout.fillWidth: true
            Layout.preferredHeight: visible ? midstreamRow.implicitHeight + 24 : 0
            visible: !page.recoveryBlocked && (Runtime.app.recording.recovering
                     || Runtime.app.captureMidstreamSuspected || Runtime.app.captureSilent)
            radius: Theme.radiusM
            color: "transparent"
            border.width: 1
            border.color: Runtime.app.recording.recovering ? Theme.border : Theme.orange

            RowLayout {
                id: midstreamRow
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                anchors.leftMargin: 16
                anchors.rightMargin: 12
                spacing: 16

                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 3

                    Text {
                        objectName: "dashboardCaptureHealthTitle"
                        Layout.fillWidth: true
                        text: Runtime.app.recording.recovering ? qsTr("正在自动寻找游戏网络路径")
                              : Runtime.app.captureMidstreamSuspected
                              ? qsTr("可能错过了连接开头，暂时无法确认自动记录")
                              : qsTr("正在接收网络数据，但尚未识别出游戏消息")
                        color: Runtime.app.recording.recovering ? Theme.textPrimary : Theme.orangeText
                        font.pixelSize: Theme.fs(14)
                        font.bold: true
                        wrapMode: Text.WordWrap
                    }

                    Text {
                        objectName: "dashboardCaptureHealthText"
                        Layout.fillWidth: true
                        text: Runtime.app.recording.recovering
                              ? Runtime.app.recording.message : Runtime.app.captureHealthText
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }
                }

                AppButton {
                    objectName: "dashboardCaptureDiagnosticsButton"
                    visible: !Runtime.app.recording.recovering
                    variant: "primary"
                    text: qsTr("查看捕获诊断")
                    onClicked: page.openCaptureRequested()
                }
            }
        }

        // All task kinds share RecoveryController's unique-run projection and editor.
        Card {
            objectName: "dashboardPendingWorkCard"
            Layout.fillWidth: true
            visible: Runtime.app.recovery.count > 0 || Runtime.app.recovery.error.length > 0
            PendingWorkContent {
                Layout.fillWidth: true
                limit: 1
                prominentHeader: true
            }
        }

        Card {
            Layout.fillWidth: true
            visible: Runtime.app.maintainerToolsVisible && (Runtime.app.validationActive
                     || (!Runtime.app.capturing
                         && (Runtime.app.validationState === "COMPLETED"
                             || Runtime.app.validationState === "FAILED"
                             || (Runtime.app.protocolProfileStatus !== "VERIFIED"
                                 && Runtime.app.validationStatusLoaded))))
            padding: 16
            spacing: 6

            RowLayout {
                Layout.fillWidth: true
                Text {
                    Layout.fillWidth: true
                    text: Runtime.app.captureModeStatusText
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(14)
                    font.bold: true
                    wrapMode: Text.WordWrap
                }
                AppButton {
                    text: qsTr("查看验证状态")
                    onClicked: page.openCaptureRequested()
                }
            }

            Text {
                Layout.fillWidth: true
                text: Runtime.app.validationNotice
                color: Theme.orangeText
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            Text {
                Layout.fillWidth: true
                visible: Runtime.app.validationError.length > 0
                text: qsTr("验证操作失败：") + Runtime.app.validationError
                color: Theme.red
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
        }

        // linear-gradient(90deg,rgba(232,148,74,.22),rgba(232,148,74,.06))
        // over a 1 px orange frame - the panel stays readable in both themes.
        // The tint is derived from Theme.orange rather than written out, so the
        // light 艾欧泽亚 skin gets its own darker gilded value instead of the dark
        // skin's (2026-09-21 audit, finding 21).
        Rectangle {
            objectName: "npcapMissingBanner"
            Layout.fillWidth: true
            // 68 px, or taller when a longer translation of the text below
            // takes its second line.
            Layout.preferredHeight: visible ? Math.max(68, npcapRow.implicitHeight + 20) : 0
            // Only on the Collector's own answer: without a capture status (no
            // pipe, a failed read) nothing is known about Npcap (review K2-1).
            visible: Runtime.app.backendConnected && Runtime.app.captureStatus.npcap_installed !== undefined
                     && !Runtime.app.npcapInstalled
            radius: Theme.radiusM
            border.width: 1
            border.color: Theme.orange

            gradient: Gradient {
                orientation: Gradient.Horizontal
                GradientStop {
                    position: 0.0
                    color: Theme.eorzea
                           ? Qt.rgba(Theme.orange.r, Theme.orange.g, Theme.orange.b, 0.22)
                           : Theme.orangeBackground
                }
                GradientStop {
                    position: 1.0
                    color: Theme.eorzea
                           ? Qt.rgba(Theme.orange.r, Theme.orange.g, Theme.orange.b, 0.06)
                           : Theme.orangeBackground
                }
            }

            RowLayout {
                id: npcapRow

                anchors.fill: parent
                anchors.leftMargin: 16
                anchors.rightMargin: 12
                spacing: 16

                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 2

                    Text {
                        text: qsTr("未检测到 Npcap，自动记录已停用")
                        color: Theme.textPrimary
                        font.family: Theme.headingFamilyFor(text)
                        font.pixelSize: Theme.fs(14)
                        font.bold: true
                    }

                    Text {
                        Layout.fillWidth: true
                        text: qsTr("程序不包含 Npcap。请从官方站点安装 Npcap（勾选 WinPcap 兼容模式），安装后点击“重新检测”。手动记录与统计功能不受影响。")
                        color: Theme.eorzea ? Theme.textPrimary : Theme.textSecondary
                        opacity: Theme.dimOpacity(0.9)
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                        maximumLineCount: 2
                        elide: Text.ElideRight
                    }
                }

                AppButton {
                    variant: "primary"
                    text: qsTr("查看安装说明")
                    onClicked: page.openCaptureRequested()
                }
            }
        }

        RowLayout {
            Layout.fillWidth: true
            Layout.preferredHeight: 340
            spacing: 12

            Card {
                objectName: "achievementCard"
                Layout.preferredWidth: 300
                Layout.fillHeight: true
                padding: 20

                CardKicker {
                    text: qsTr("成就进度 · %1 次").arg(Runtime.app.goalCount)
                }

                Item {
                    Layout.fillWidth: true
                    Layout.preferredHeight: 208

                    ProgressRing {
                        objectName: "achievementRing"
                        width: 200
                        height: 200
                        anchors.centerIn: parent
                        value: page.achievementProgress
                        maximum: Math.max(1, Runtime.app.goalCount)
                        label: String(page.achievementProgress)
                        detail: Runtime.fmt.percent(Math.max(0, value / maximum), 1)
                    }

                    AchievementConfetti {
                        id: achievementConfetti
                        objectName: "achievementConfetti"
                        anchors.fill: parent
                        z: 1
                    }
                    Connections {
                        target: Runtime.app
                        function onAchievementMilestoneReached(completedLaps) {
                            achievementConfetti.burst()
                        }
                    }
                }

                RowLayout {
                    Layout.alignment: Qt.AlignHCenter
                    spacing: 5

                    Text {
                        text: page.remainingParts[0].trim()
                        visible: text.length > 0
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                    }

                    Text {
                        objectName: "achievementRemaining"
                        text: String(page.achievementRemaining)
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(18)
                        font.weight: Theme.figureWeight(true)
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                    }

                    Text {
                        text: (page.remainingParts[1] || "").trim()
                        visible: text.length > 0
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                    }
                }

                Item { Layout.fillHeight: true }

                RowLayout {
                    Layout.alignment: Qt.AlignHCenter
                    spacing: 5

                    Text {
                        text: qsTr("含起始次数 %1 次").arg(Runtime.app.baselineCount) + " ·"
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                    }

                    Text {
                        text: qsTr("修改")
                        color: Theme.accent
                        font.pixelSize: Theme.fs(11)

                        TapHandler { onTapped: page.openSettingsRequested() }
                    }
                }
            }

            ColumnLayout {
                Layout.fillWidth: true
                Layout.fillHeight: true
                spacing: 12

                GridLayout {
                    // Two rows for the eight prototype cards; a ninth (中途加入 or 未知结果,
                    // each shown only when non-zero) adds a row rather than overflowing.
                    readonly property int cardRows: Math.max(1, Math.ceil(Runtime.app.statCards.length / 4))
                    Layout.fillWidth: true
                    Layout.preferredHeight: cardRows * 96 + (cardRows - 1) * 12
                    columns: 4
                    columnSpacing: 12
                    rowSpacing: 12

                    Repeater {
                        objectName: "dashboardStatCards"
                        model: Runtime.app.statCards

                        delegate: StatCard {
                            required property var modelData
                            required property int index
                            objectName: "dashboardStatCard" + index

                            Layout.fillWidth: true
                            Layout.preferredHeight: 96
                            padding: 16
                            title: modelData.k || ""
                            value: modelData.v || Runtime.fmt.dash()
                        }
                    }
                }

                Card {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    // The row above has a preferred height, not a fixed one: with a two-line
                    // notice and the value grid the card needs more than its share, and the
                    // row must grow rather than let the values spill below the border
                    // (owner's report, 2026-10-09).
                    Layout.minimumHeight: implicitHeight
                    padding: 16

                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 12

                        CardKicker { text: qsTr("当前导随") }

                        Tag {
                            // 依据排本申请推断的档案在提交申请时即进入该状态，而非队伍
                            // 匹配成功时，因此此类档案下不得显示“已匹配”。
                            text: Runtime.app.currentRunState === "ENTERED_DUTY" ? qsTr("副本进行中")
                                : Runtime.app.currentRunState === "MENTOR_MATCHED"
                                  ? (Runtime.app.calibration && Runtime.app.calibration.provisional
                                     ? qsTr("已排本，等待进本") : qsTr("已匹配，等待进本"))
                                : qsTr("空闲")
                            variant: Runtime.app.currentRunState === "ENTERED_DUTY" ? "blue" : "neutral"
                        }

                        Item { Layout.fillWidth: true }
                    }

                    // Why the card is empty: it separates "nothing happened" from
                    // "nothing is listening".
                    Text {
                        objectName: "currentRunCaptureStatus"
                        Layout.fillWidth: true
                        visible: !Runtime.app.currentRun.run
                        text: Runtime.app.maintainerToolsVisible ? Runtime.app.captureModeStatusText : Runtime.app.recording.message
                        color: Runtime.app.recording.attention ? Theme.orangeText : Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                        // The card sits in a fixed-height row: a long status (patch-day
                        // calibration) may take two lines, but must never push the grid
                        // below into the labels.
                        maximumLineCount: 2
                        elide: Text.ElideRight
                    }

                    GridLayout {
                        Layout.fillWidth: true
                        columns: 6
                        columnSpacing: 16
                        rowSpacing: 4

                        Repeater {
                            model: [
                                { label: qsTr("匹配时间"), value: Runtime.app.currentRun.run ? Runtime.fmt.localTime(Runtime.app.currentRun.run.matched_at_utc) : "—" },
                                // The only value that may be shortened: a narrow window
                                // (or a longer translation) elides the duty name, never
                                // a time or the confidence.
                                { label: qsTr("当前副本"), shrinks: true, value: Runtime.app.currentRun.run ? (Theme.dutyName(Runtime.app.currentRun.run) || "—") : "—" },
                                // Always shown: a run whose job the software has not read yet says so,
                                // instead of the column disappearing and reappearing.
                                { label: qsTr("当前职业"), kind: "job",
                                  value: Runtime.app.currentRun.run ? (Runtime.app.currentRun.run.job_id > 0 ? Theme.jobName(Runtime.app.currentRun.run) : qsTr("未知")) : "—" },
                                { label: qsTr("已进行"), value: Runtime.app.currentRun.run ? Runtime.fmt.duration(Runtime.app.liveElapsedMs) : "—" },
                                { label: qsTr("进本时间"), value: Runtime.app.currentRun.run ? Runtime.fmt.localTime(Runtime.app.currentRun.run.entered_at_utc) : "—" },
                                { label: qsTr("识别可信度"),
                                  value: Runtime.app.currentRun.run
                                         ? Theme.live(Runtime.fmt.runConfidenceLabel(Runtime.app.currentRun.run))
                                         : Runtime.fmt.dash() }
                            ]

                            delegate: ColumnLayout {
                                id: runCell
                                required property var modelData

                                readonly property bool isJob: modelData.kind === "job"
                                readonly property bool hasJob: liveJobId !== null
                                                               && liveJobId !== undefined
                                readonly property var liveJobId: Runtime.app.currentRun.run
                                                                 ? Runtime.app.currentRun.run.job_id : null

                                objectName: isJob ? "currentRunJob" : ""
                                Layout.fillWidth: true
                                spacing: 4

                                Text {
                                    text: runCell.modelData.label
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(11)
                                }

                                Text {
                                    Layout.fillWidth: true
                                    Layout.minimumWidth: runCell.modelData.shrinks === true ? 0 : implicitWidth
                                    visible: !parent.isJob
                                    text: runCell.modelData.value
                                    textFormat: Text.PlainText
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(13)
                                    font.bold: true
                                    elide: Text.ElideRight
                                }

                                RowLayout {
                                    Layout.fillWidth: true
                                    visible: parent.isJob
                                    spacing: 6

                                    // No run, no icons: an idle card shows a dash, not a
                                    // question-mark badge and an empty role glyph.
                                    JobIcon {
                                        visible: runCell.hasJob
                                        jobId: runCell.liveJobId
                                        size: 20
                                    }

                                    Text {
                                        text: runCell.modelData.value
                                        textFormat: Text.PlainText
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(13)
                                        font.bold: true
                                        elide: Text.ElideRight
                                    }

                                    RoleIcon {
                                        Layout.fillWidth: true
                                        visible: runCell.hasJob
                                        role: Runtime.jobs.roleGroup(runCell.liveJobId)
                                        size: 18
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        RowLayout {
            Layout.fillWidth: true
            Layout.preferredHeight: 224
            spacing: 12

            Card {
                Layout.fillWidth: true
                Layout.fillHeight: true
                padding: 16

                RowLayout {
                    Layout.fillWidth: true
                    spacing: 16

                    CardKicker { text: qsTr("完成趋势") }

                    Text {
                        text: String(Runtime.app.completedLast7Days)
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(13)
                        font.weight: Theme.figureWeight(true)
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                    }

                    Text {
                        text: qsTr("近 7 天")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                    }

                    Item { Layout.fillWidth: true }

                    // A window, not a granularity: it decides how many of the day
                    // buckets already in hand are drawn. The week and month series
                    // are always shown whole.
                    SegmentedControl {
                        Layout.preferredWidth: Math.max(116, naturalWidth)
                        visible: Runtime.app.trendMode === "day"
                        options: [
                            { value: 7, label: qsTr("7 天") },
                            { value: 30, label: qsTr("30 天") }
                        ]
                        currentValue: Runtime.app.trendWindowDays
                        onActivated: function(value) { Runtime.app.setTrendWindowDays(value) }
                    }

                    SegmentedControl {
                        Layout.preferredWidth: Math.max(116, naturalWidth)
                        options: [
                            { value: "day", label: qsTr("日") },
                            { value: "week", label: qsTr("周") },
                            { value: "month", label: qsTr("月") }
                        ]
                        currentValue: Runtime.app.trendMode
                        onActivated: function(value) { Runtime.app.setTrendMode(value) }
                    }
                }

                // Buckets come from GetDashboardStats.trend: the Collector
                // aggregates them in SQL over every matching row, not just the
                // current page. Boundaries are UTC; the labels show the same
                // instants in local time (docs/statistics-definitions.md 12.1).
                TrendChart {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    buckets: Runtime.app.trendBuckets
                }
            }

            Card {
                Layout.preferredWidth: 360
                Layout.fillHeight: true
                padding: 16

                CardKicker {
                    text: qsTr("结果分布 · %1 条").arg(page.resultTotal)
                }

                StackedResultBar {
                    Layout.fillWidth: true
                    buckets: Runtime.app.resultBuckets
                }

                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 5

                    Repeater {
                        objectName: "dashboardResultLegend"
                        model: page.visibleResultBuckets

                        delegate: RowLayout {
                            id: resultLegendRow
                            required property var modelData
                            required property int index
                            objectName: "dashboardResultLegendRow" + index
                            Layout.fillWidth: true
                            spacing: 8

                            Rectangle {
                                Layout.minimumWidth: 10
                                Layout.maximumWidth: 10
                                Layout.minimumHeight: 10
                                Layout.maximumHeight: 10
                                radius: 3
                                color: Theme.token(resultLegendRow.modelData.color_token || "")
                            }

                            Text {
                                Layout.fillWidth: true
                                text: resultLegendRow.modelData.label || ""
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                            }

                            Text {
                                text: String(resultLegendRow.modelData.count || 0)
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                font.weight: Theme.figureWeight(true)
                                font.family: Theme.figureFamily
                                font.features: ({ "tnum": 1 })
                                horizontalAlignment: Text.AlignRight
                                Layout.preferredWidth: 28
                            }
                        }
                    }
                }
            }
        }

        // ------------------------------------------------- 导随心得 --
        Card {
            Layout.fillWidth: true
            padding: 16
            spacing: 10

            RowLayout {
                Layout.fillWidth: true
                spacing: 12

                CardKicker { text: qsTr("导随笔记") }

                Text {
                    text: qsTr("%1 条笔记 · %2 次通关未写笔记")
                          .arg(page.reflectionCountText).arg(page.pendingReflectionCountText)
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    font.family: Theme.figureFamily
                    font.weight: Theme.figureWeight(false)
                    font.features: ({ "tnum": 1 })
                }

                Item { Layout.fillWidth: true }

                AppButton {
                    objectName: "openReflectionLibraryButton"
                    text: qsTr("全部心得")
                    onClicked: page.openReflectionsRequested()
                }
                AppButton {
                    text: qsTr("补录笔记")
                    onClicked: page.backfillReflection()
                }
            }

            RowLayout {
                Layout.fillWidth: true
                visible: page.recentReflections.length > 0
                spacing: 10

                Repeater {
                    model: page.recentReflections

                    delegate: ReflectionCard {
                        required property var modelData

                        Layout.fillWidth: true
                        Layout.preferredWidth: 1
                        entry: modelData
                        onActivated: page.openRunHistoryRequested(modelData.run)
                    }
                }
            }

            Text {
                Layout.fillWidth: true
                visible: page.recentReflections.length === 0
                text: qsTr("还没有笔记。可以在历史记录中补录，也可以开启“通关后自动弹出”提醒。")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
        }
    }
}
