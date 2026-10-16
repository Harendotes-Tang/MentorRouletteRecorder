import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 解析：成功率 in large figures with a sparkline of its last readings, then
// 消息速率 / 失败 · 去重 / 最近有效事件, then the most recent parser refusals.
//
// Counters come from App.captureCounters, which holds only what the Collector
// sent: without parse counters the panel says so and prints no number at all.
// 最近有效事件 is last_valid_event_at_utc in local time plus the Chinese name of
// last_valid_event_kind (Fmt.eventKindLabel); an unknown kind leaves the time.
// A refusal row shows a Chinese sentence; maintainers read its code in mono, the
// direction, the opcode and the Collector's text instead.
Card {
    id: parse
    objectName: "captureParsePanel"

    readonly property var counters: App.captureCounters
    readonly property bool maintainer: App.maintainerToolsVisible
    readonly property bool statsAvailable: App.parserStatsAvailable
    readonly property int playerRowLimit: 5
    // recent_parser_errors is oldest first; the panel reads newest first.
    readonly property var failures: {
        const rows = (App.parserErrors || []).slice().reverse()
        return parse.maintainer ? rows : rows.slice(0, parse.playerRowLimit)
    }
    readonly property int hiddenFailures: (App.parserErrors || []).length - parse.failures.length

    // The sparkline: the success rate as it was reported, one reading at most
    // every five seconds, the latest twelve. Only values the Collector sent are
    // kept, so the bars never show a rate this page made up; a new capture
    // session, or counters without a rate, start the line over.
    readonly property int sparkLength: 12
    readonly property int sparkIntervalMs: 5000
    property var sparkSamples: []
    property double sparkLastAt: 0
    property string sparkSession: ""

    function recordSample() {
        // Read App directly: this runs from App's own signal, before or after
        // the `counters` binding has caught up.
        const rate = App.captureCounters.parse_success_rate
        const session = String(App.captureStatus.capture_session_id || "")
        if (session !== parse.sparkSession || rate === undefined || rate === null) {
            parse.sparkSamples = []
            parse.sparkLastAt = 0
            parse.sparkSession = session
        }
        if (rate === undefined || rate === null)
            return
        const now = Date.now()
        if (parse.sparkSamples.length > 0 && now - parse.sparkLastAt < parse.sparkIntervalMs)
            return
        const next = parse.sparkSamples.slice(1 - parse.sparkLength)
        next.push(Number(rate))
        parse.sparkSamples = next
        parse.sparkLastAt = now
    }

    Connections {
        target: App
        function onStatusChanged() { parse.recordSample() }
    }
    Component.onCompleted: parse.recordSample()

    function counter(key) {
        const value = parse.counters[key]
        return (value === undefined || value === null) ? Fmt.dash() : Fmt.count(value)
    }

    function successRateText() {
        const rate = parse.counters.parse_success_rate
        return (rate === undefined || rate === null) ? Fmt.dash() : Fmt.percent(rate)
    }

    function messageRateText() {
        const value = parse.counters.message_rate_per_second
        if (value === undefined || value === null)
            return Fmt.dash()
        return qsTr("%1 / s").arg(Number(value).toFixed(1))
    }

    function lastValidEventText() {
        const at = parse.counters.last_valid_event_at_utc
        if (!at)
            return Fmt.dash()
        const kind = Theme.live(Fmt.eventKindLabel(String(parse.counters.last_valid_event_kind || "")))
        return kind.length > 0 ? Fmt.localTime(at) + " " + kind : Fmt.localTime(at)
    }

    function failureMessage(row) {
        if (!parse.maintainer)
            return Theme.live(Fmt.parserErrorLabel(String(row.code || "")))
        return [String(row.direction || ""), String(row.opcode || "")].join(" ").trim()
               + " · " + String(row.message || "")
    }

    horizontalPadding: 20
    verticalPadding: 16
    spacing: 12

    RowLayout {
        Layout.fillWidth: true
        spacing: 12

        CardKicker {
            Layout.alignment: Qt.AlignVCenter
            Layout.fillWidth: false
            text: qsTr("数据读取")
        }

        Text {
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignVCenter
            text: qsTr("无法识别的数据会跳过，不会据此新增或修改记录")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            elide: Text.ElideRight
        }
    }

    Text {
        objectName: "captureParseUnavailable"
        Layout.fillWidth: true
        visible: !parse.statsAvailable
        text: parse.maintainer
              ? qsTr("采集器未报告数据读取计数（未连接，或对端是更早的版本）。此处不显示任何数字。")
              : qsTr("暂时没有读取统计。后台服务可能尚未连接，或需要更新软件。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    // dg-metrics: grid-template-columns:repeat(3,1fr) 1.5fr; gap:12px 20px; align-items:end
    RowLayout {
        Layout.fillWidth: true
        visible: parse.statsAvailable
        spacing: 20

        ColumnLayout {
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            Layout.horizontalStretchFactor: 2
            Layout.alignment: Qt.AlignBottom
            spacing: 2

            Text {
                text: qsTr("成功率")
                color: Theme.eorzea ? Theme.textSecondary : Theme.textMuted
                font.pixelSize: Theme.fs(11)
            }

            // dg-big: the heading face in gold (eorzea), Plex Mono in the text
            // colour (classic). Neither size is one Theme.fs maps.
            Text {
                objectName: "captureParseSuccessRate"
                text: parse.successRateText()
                color: Theme.eorzea ? Theme.gold2 : Theme.textPrimary
                font.pixelSize: Theme.eorzea ? 30 : 24
                font.family: Theme.numFamily
                font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                font.features: ({ "tnum": 1 })
                lineHeight: 1.1
            }

            // dg-spark: 3 px bars, 2 px apart, 18 px tall; the latest one in the
            // full accent, the earlier ones at 35 %.
            Row {
                objectName: "captureParseSparkline"
                Layout.topMargin: 2
                visible: parse.sparkSamples.length >= 2
                spacing: 2

                Repeater {
                    model: parse.sparkSamples

                    delegate: Item {
                        id: bar
                        required property double modelData
                        required property int index

                        width: 3
                        height: 18

                        Rectangle {
                            anchors.bottom: parent.bottom
                            width: parent.width
                            height: Math.max(2, Math.round(Math.min(1, Math.max(0, bar.modelData)) * 18))
                            radius: 1.5
                            color: bar.index === parse.sparkSamples.length - 1
                                   ? Theme.accent
                                   : Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, 0.35)
                        }
                    }
                }
            }
        }

        Repeater {
            model: [
                { key: "rate", label: qsTr("读取速度"), value: parse.messageRateText(), figure: true, stretch: 2 },
                { key: "failures", label: qsTr("失败 · 重复"),
                  value: parse.counter("parse_fail_count") + " · " + parse.counter("duplicate_count"),
                  figure: true, stretch: 2 },
                { key: "lastEvent", label: qsTr("最近识别的操作"), value: parse.lastValidEventText(),
                  figure: false, stretch: 3 }
            ]

            delegate: ColumnLayout {
                id: metric
                required property var modelData

                Layout.fillWidth: true
                Layout.preferredWidth: 1
                Layout.horizontalStretchFactor: metric.modelData.stretch
                Layout.alignment: Qt.AlignBottom
                Layout.bottomMargin: 4
                spacing: 2

                Text {
                    Layout.fillWidth: true
                    text: metric.modelData.label
                    color: Theme.eorzea ? Theme.textSecondary : Theme.textMuted
                    font.pixelSize: Theme.fs(11)
                    elide: Text.ElideRight
                }

                Text {
                    objectName: "captureParseValue_" + metric.modelData.key
                    Layout.fillWidth: true
                    text: metric.modelData.value
                    textFormat: Text.PlainText
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(16)
                    font.family: metric.modelData.figure ? Theme.figureFamily : Theme.bodyFamily
                    font.weight: metric.modelData.figure ? Theme.figureWeight(true) : Font.DemiBold
                    font.features: ({ "tnum": 1 })
                    wrapMode: Text.WordWrap
                }
            }
        }
    }

    // 最近失败: border-top divider, then rows of 64 px time · 150 px code · text.
    ColumnLayout {
        objectName: "captureParseFailures"
        Layout.fillWidth: true
        visible: parse.statsAvailable || App.parserErrors.length > 0
        spacing: 0

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            color: Theme.border
        }

        Text {
            Layout.topMargin: 10
            Layout.bottomMargin: 2
            text: qsTr("最近未识别的数据")
            color: Theme.eorzea ? Theme.textSecondary : Theme.textMuted
            font.pixelSize: Theme.fs(11)
        }

        Repeater {
            model: parse.failures

            delegate: Item {
                id: failureRow
                required property var modelData

                objectName: "captureParseFailureRow"
                Layout.fillWidth: true
                implicitHeight: failureLayout.implicitHeight + 14

                RowLayout {
                    id: failureLayout
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.topMargin: 7
                    spacing: 10

                    Text {
                        Layout.preferredWidth: 64
                        Layout.alignment: Qt.AlignTop
                        text: failureRow.modelData.at_utc ? Fmt.localTime(failureRow.modelData.at_utc) : Fmt.dash()
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(false)
                        font.features: ({ "tnum": 1 })
                    }

                    // The refusal code is a token: maintainers read it, a player
                    // reads the sentence beside it (review OK-8).
                    Text {
                        Layout.preferredWidth: 150
                        Layout.alignment: Qt.AlignTop
                        Layout.topMargin: 1
                        visible: parse.maintainer
                        text: String(failureRow.modelData.code || "")
                        textFormat: Text.PlainText
                        color: Theme.red
                        font.pixelSize: 11
                        font.family: Theme.monoFamily
                        font.bold: true
                        elide: Text.ElideRight
                    }

                    Text {
                        objectName: "captureParseFailureText"
                        Layout.fillWidth: true
                        Layout.alignment: Qt.AlignTop
                        text: parse.failureMessage(failureRow.modelData)
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }
                }

                Rectangle {
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: 1
                    color: Theme.border
                }
            }
        }

        Text {
            Layout.fillWidth: true
            Layout.topMargin: 6
            visible: parse.statsAvailable && App.parserErrors.length === 0
            text: qsTr("最近没有数据读取失败记录。")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            Layout.topMargin: 8
            visible: parse.hiddenFailures > 0
            text: qsTr("另有 %1 条更早的失败未列出，都不会生成记录。").arg(parse.hiddenFailures)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            Layout.topMargin: 8
            visible: App.usingMockData && App.parserErrors.length > 0
            text: qsTr("以上为模拟后端的示例数据，不是真实数据读取失败。")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }
}
