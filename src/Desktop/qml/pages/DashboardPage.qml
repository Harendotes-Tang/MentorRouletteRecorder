import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

ScrollView {
    id: page

    readonly property int resultTotal: {
        let total = 0
        for (let index = 0; index < App.resultBuckets.length; ++index)
            total += Number(App.resultBuckets[index].count || 0)
        return total
    }

    signal openManualRequested()
    signal openCaptureRequested()
    signal openSettingsRequested()
    // Jump to 历史记录 filtered to the runs awaiting review.
    signal openPendingReviewRequested()
    // Opens ReflectionDialog for `run` with the given kicker.
    signal reflectRequested(var run, string kicker)
    // Jumps to the history page with `run` selected on the 心得 tab.
    signal openRunHistoryRequested(var run)

    // The newest run still waiting for a result. The controller only fetches it
    // while something is actually pending, so an empty map is the normal case.
    readonly property var pendingRun: (typeof App !== "undefined" && App.pendingReviewRun)
                                      ? App.pendingReviewRun : ({})
    readonly property bool pendingRunReady: !!(pendingRun && pendingRun.run_id)
    readonly property string pendingRunDuty: pendingRunReady && pendingRun.duty_name
                                             ? pendingRun.duty_name : qsTr("未知副本")

    readonly property var reflectionSummary: (typeof App !== "undefined" && App.reflectionSummary)
                                             ? App.reflectionSummary : ({})
    readonly property var recentReflections: reflectionSummary.recent
                                             ? reflectionSummary.recent : []
    // True once GetReflectionSummary has answered; until then the counts are
    // unknown and render as a dash rather than a 0 this process invented.
    readonly property bool reflectionSummaryLoaded: reflectionSummary.reflection_count !== undefined
    readonly property string reflectionCountText: reflectionSummaryLoaded
        ? String(Number(reflectionSummary.reflection_count)) : Fmt.dash()
    readonly property string pendingReflectionCountText: reflectionSummaryLoaded
        ? String(Number(reflectionSummary.pending_completed_count)) : Fmt.dash()

    function backfillReflection() {
        if (!page.reflectionSummaryLoaded) {
            if (typeof App !== "undefined" && App.showToast)
                App.showToast(qsTr("笔记数据尚未加载，请稍后再试"))
            return
        }
        const pending = page.reflectionSummary.next_pending
        if (pending && pending.run_id)
            page.reflectRequested(pending, qsTr("补录笔记"))
        else if (typeof App !== "undefined" && App.showToast)
            App.showToast(qsTr("所有通关记录都已写过笔记"))
    }

    clip: true
    contentWidth: availableWidth
    ScrollBar.horizontal.policy: ScrollBar.AlwaysOff

    ColumnLayout {
        width: page.availableWidth
        spacing: 20

        PageHeader {
            title: qsTr("总览")
            subtitle: Fmt.dateWithWeekday(new Date().toISOString())
        }

        // The Collector found a newer release. Notify only: nothing is
        // downloaded or installed by this software.
        UpdateNotice {}

        Flow {
            Layout.fillWidth: true
            spacing: 8
            AppButton {
                text: qsTr("新增记录")
                onClicked: page.openManualRequested()
            }

            AppButton {
                visible: App.maintainerToolsVisible
                text: App.captureActionLabel
                variant: "primary"
                enabled: App.captureActionEnabled
                onClicked: App.toggleCapture()
            }
        }

        // Capture started after the client logged in: nothing decodes while the
        // status stays a green RUNNING (docs/live-validation-guide.md section 6).
        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: visible ? midstreamRow.implicitHeight + 24 : 0
            visible: App.captureMidstreamSuspected || App.captureSilent
            radius: Theme.radiusM
            color: "transparent"
            border.width: 1
            border.color: Theme.orange

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
                        text: App.captureMidstreamSuspected
                              ? qsTr("抓包开始得太晚，本次连接不会产生任何记录")
                              : qsTr("捕获在运行，但没有解码出任何报文")
                        color: Theme.orangeText
                        font.pixelSize: Theme.fs(14)
                        font.bold: true
                    }

                    Text {
                        Layout.fillWidth: true
                        text: App.captureHealthText
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }
                }

                AppButton {
                    variant: "primary"
                    text: qsTr("查看抓包状态")
                    onClicked: page.openCaptureRequested()
                }
            }
        }

        // Crash recovery closed these runs without evidence; their results remain
        // a guess until the user confirms them.
        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: visible ? pendingRow.implicitHeight + 24 : 0
            visible: App.pendingReviewCount > 0
            radius: Theme.radiusM
            color: "transparent"
            border.width: 1
            border.color: Theme.orange

            RowLayout {
                id: pendingRow
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
                        objectName: "pendingReviewBannerTitle"
                        text: qsTr("%1 条记录待复核").arg(App.pendingReviewCount)
                        color: Theme.orangeText
                        font.pixelSize: Theme.fs(14)
                        font.bold: true
                    }

                    Text {
                        Layout.fillWidth: true
                        text: qsTr("这些导随的结果还没有确认：有的是程序异常退出后由崩溃恢复关闭的，"
                                   + "有的是当前协议档案只看到离开副本、还不能判定是否通关。"
                                   + "确认“通关”后这一次才计入导随次数；每次确认都会作为一条新的修订记入审计。")
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }

                    // The newest pending run is resolved here directly, without
                    // going through 历史记录.
                    RowLayout {
                        Layout.fillWidth: true
                        Layout.topMargin: 4
                        visible: page.pendingRunReady
                        spacing: 8

                        Text {
                            text: qsTr("最近一条：%1").arg(page.pendingRunDuty)
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                        }

                        AppButton {
                            objectName: "pendingReviewCompletedButton"
                            variant: "primary"
                            implicitHeight: 26
                            text: qsTr("通关")
                            onClicked: App.resolveRunResult(page.pendingRun.run_id, -1,
                                                            "COMPLETED",
                                                            qsTr("用户确认通关"))
                        }

                        AppButton {
                            objectName: "pendingReviewLeftButton"
                            implicitHeight: 26
                            text: qsTr("未通关")
                            onClicked: App.resolveRunResult(page.pendingRun.run_id, -1,
                                                            "LEFT_OR_ABANDONED",
                                                            qsTr("用户确认未通关"))
                        }

                        Item { Layout.fillWidth: true }
                    }
                }

                AppButton {
                    variant: "primary"
                    text: qsTr("去复核")
                    onClicked: page.openPendingReviewRequested()
                }
            }
        }

        Card {
            Layout.fillWidth: true
            visible: App.maintainerToolsVisible && (App.validationActive
                     || (!App.capturing
                         && (App.validationState === "COMPLETED"
                             || App.validationState === "FAILED"
                             || (App.protocolProfileStatus !== "VERIFIED"
                                 && App.validationStatusLoaded))))
            padding: 16
            spacing: 6

            RowLayout {
                Layout.fillWidth: true
                Text {
                    Layout.fillWidth: true
                    text: App.captureModeStatusText
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
                text: App.validationNotice
                color: Theme.orangeText
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            Text {
                Layout.fillWidth: true
                visible: App.validationError.length > 0
                text: qsTr("验证操作失败：") + App.validationError
                color: Theme.red
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
        }

        // linear-gradient(90deg,rgba(232,148,74,.22),rgba(232,148,74,.06))
        // over a 1 px orange frame - the panel stays readable in both themes.
        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: visible ? 68 : 0
            visible: !App.npcapInstalled
            radius: Theme.radiusM
            border.width: 1
            border.color: Theme.orange

            gradient: Gradient {
                orientation: Gradient.Horizontal
                GradientStop {
                    position: 0.0
                    color: Theme.eorzea ? "#38e8944a" : Theme.orangeBackground
                }
                GradientStop {
                    position: 1.0
                    color: Theme.eorzea ? "#0fe8944a" : Theme.orangeBackground
                }
            }

            RowLayout {
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
                Layout.preferredWidth: 300
                Layout.fillHeight: true
                padding: 20

                CardKicker {
                    text: qsTr("成就进度 · %1 次").arg(App.goalCount)
                }

                Item {
                    Layout.fillWidth: true
                    Layout.preferredHeight: 208

                    ProgressRing {
                        width: 200
                        height: 200
                        anchors.centerIn: parent
                        value: Math.max(0, App.baselineCount + (App.dashboard.completed_count || 0))
                        maximum: Math.max(1, App.goalCount)
                        label: String(Math.max(0, App.baselineCount + (App.dashboard.completed_count || 0)))
                        detail: Fmt.percent(Math.min(1, Math.max(0, value / maximum)), 1)
                    }
                }

                RowLayout {
                    Layout.alignment: Qt.AlignHCenter
                    spacing: 5

                    Text {
                        text: qsTr("还差")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                    }

                    Text {
                        text: String(Math.max(0, App.goalCount - App.baselineCount
                                             - (App.dashboard.completed_count || 0)))
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(18)
                        font.weight: Theme.figureWeight(true)
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                    }

                    Text {
                        text: qsTr("次")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                    }
                }

                Item { Layout.fillHeight: true }

                RowLayout {
                    Layout.alignment: Qt.AlignHCenter
                    spacing: 5

                    Text {
                        text: qsTr("含安装前基数 %1 次 ·").arg(App.baselineCount)
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
                    Layout.fillWidth: true
                    Layout.preferredHeight: 204
                    columns: 4
                    columnSpacing: 12
                    rowSpacing: 12

                    Repeater {
                        model: App.statCards

                        delegate: StatCard {
                            required property var modelData

                            Layout.fillWidth: true
                            Layout.preferredHeight: 96
                            padding: 16
                            title: modelData.k || ""
                            value: modelData.v || Fmt.dash()
                        }
                    }
                }

                Card {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    padding: 16

                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 12

                        CardKicker { text: qsTr("当前导随") }

                        Tag {
                            // 依据排本申请推断的档案在提交申请时即进入该状态，而非队伍
                            // 匹配成功时，因此此类档案下不得显示“已匹配”。
                            text: App.currentRunState === "ENTERED_DUTY" ? qsTr("副本进行中")
                                : App.currentRunState === "MENTOR_MATCHED"
                                  ? (App.calibration && App.calibration.provisional
                                     ? qsTr("已排本，等待进本") : qsTr("已匹配，等待进本"))
                                : qsTr("空闲")
                            variant: App.currentRunState === "ENTERED_DUTY" ? "blue" : "neutral"
                        }

                        Item { Layout.fillWidth: true }
                    }

                    // Why the card is empty: it separates "nothing happened" from
                    // "nothing is listening".
                    Text {
                        objectName: "currentRunCaptureStatus"
                        Layout.fillWidth: true
                        visible: !App.currentRun.run
                        text: App.maintainerToolsVisible ? App.captureModeStatusText : App.recording.message
                        color: App.recording.attention ? Theme.orangeText : Theme.textSecondary
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
                                { label: qsTr("匹配时间"), value: App.currentRun.run ? Fmt.localTime(App.currentRun.run.matched_at_utc) : "—" },
                                { label: qsTr("当前副本"), value: App.currentRun.run ? (App.currentRun.run.duty_name || "—") : "—" },
                                // Always shown: a run whose job the software has not read yet says so,
                                // instead of the column disappearing and reappearing.
                                { label: qsTr("当前职业"), kind: "job",
                                  value: App.currentRun.run ? (App.currentRun.run.job_id > 0 ? App.currentRun.run.job_name : qsTr("未知")) : "—" },
                                { label: qsTr("已进行"), value: App.currentRun.run ? Fmt.duration(App.liveElapsedMs) : "—" },
                                { label: qsTr("进本时间"), value: App.currentRun.run ? Fmt.localTime(App.currentRun.run.entered_at_utc) : "—" },
                                { label: qsTr("检测置信"),
                                  value: App.currentRun.run
                                         ? Fmt.runConfidenceLabel(App.currentRun.run)
                                         : Fmt.dash() }
                            ]

                            delegate: ColumnLayout {
                                id: runCell
                                required property var modelData

                                readonly property bool isJob: modelData.kind === "job"
                                readonly property bool hasJob: liveJobId !== null
                                                               && liveJobId !== undefined
                                readonly property var liveJobId: App.currentRun.run
                                                                 ? App.currentRun.run.job_id : null

                                objectName: isJob ? "currentRunJob" : ""
                                Layout.fillWidth: true
                                spacing: 4

                                Text {
                                    text: modelData.label
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(11)
                                }

                                Text {
                                    Layout.fillWidth: true
                                    visible: !parent.isJob
                                    text: modelData.value
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
                                        jobId: liveJobId
                                        size: 20
                                    }

                                    Text {
                                        text: modelData.value
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(13)
                                        font.bold: true
                                        elide: Text.ElideRight
                                    }

                                    RoleIcon {
                                        Layout.fillWidth: true
                                        visible: runCell.hasJob
                                        role: Jobs.roleGroup(liveJobId)
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
                        text: String(App.completedLast7Days)
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
                        Layout.preferredWidth: 116
                        visible: App.trendMode === "day"
                        options: [
                            { value: 7, label: qsTr("7 天") },
                            { value: 30, label: qsTr("30 天") }
                        ]
                        currentValue: App.trendWindowDays
                        onActivated: function(value) { App.setTrendWindowDays(value) }
                    }

                    SegmentedControl {
                        Layout.preferredWidth: 116
                        options: [
                            { value: "day", label: qsTr("日") },
                            { value: "week", label: qsTr("周") },
                            { value: "month", label: qsTr("月") }
                        ]
                        currentValue: App.trendMode
                        onActivated: function(value) { App.setTrendMode(value) }
                    }
                }

                // Buckets come from GetDashboardStats.trend: the Collector
                // aggregates them in SQL over every matching row, not just the
                // current page. Boundaries are UTC; the labels show the same
                // instants in local time (docs/statistics-definitions.md 12.1).
                TrendChart {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    buckets: App.trendBuckets
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
                    buckets: App.resultBuckets
                }

                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 5

                    Repeater {
                        model: App.resultBuckets

                        delegate: RowLayout {
                            required property var modelData
                            Layout.fillWidth: true
                            spacing: 8

                            Rectangle {
                                width: 10
                                height: 10
                                radius: 3
                                color: Theme.token(modelData.color_token || "")
                            }

                            Text {
                                Layout.fillWidth: true
                                text: modelData.label || ""
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                            }

                            Text {
                                text: String(modelData.count || 0)
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
                    text: qsTr("%1 条笔记 · %2 次通关未记录")
                          .arg(page.reflectionCountText).arg(page.pendingReflectionCountText)
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    font.family: Theme.figureFamily
                    font.weight: Theme.figureWeight(false)
                    font.features: ({ "tnum": 1 })
                }

                Item { Layout.fillWidth: true }

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
                text: qsTr("还没有笔记。通关后会弹出记录窗口，也可以在历史记录中补录。")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
        }
    }
}
