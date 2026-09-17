import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 解析：成功率 in large figures, then 消息速率 / 失败 · 去重 / 最近有效事件, then the
// most recent parser refusals.
//
// Counters come from App.captureCounters, which holds only what the Collector
// sent: without parse counters the panel says so and prints no number at all.
// 最近有效事件 is last_valid_event_at_utc in local time plus the Chinese name of
// last_valid_event_kind (Fmt.eventKindLabel); an unknown kind leaves the time.
// A refusal row shows its code in mono and a Chinese sentence; maintainers also
// read the direction, the opcode and the Collector's text.
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
        const kind = Fmt.eventKindLabel(String(parse.counters.last_valid_event_kind || ""))
        return kind.length > 0 ? Fmt.localTime(at) + " " + kind : Fmt.localTime(at)
    }

    function failureMessage(row) {
        if (!parse.maintainer)
            return Fmt.parserErrorLabel(String(row.code || ""))
        return [String(row.direction || ""), String(row.opcode || "")].join(" ").trim()
               + " · " + String(row.message || "")
    }

    horizontalPadding: 24
    verticalPadding: 18
    spacing: 14

    RowLayout {
        Layout.fillWidth: true
        spacing: 12

        CardKicker {
            Layout.alignment: Qt.AlignVCenter
            Layout.fillWidth: false
            text: qsTr("解析")
        }

        Text {
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignVCenter
            text: qsTr("失败即忽略，不改变导随状态、不创建记录")
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
              ? qsTr("采集器未报告解析计数（未连接，或对端是更早的版本）。此处不显示任何数字。")
              : qsTr("还没有拿到解析统计：采集服务没有连接，或它的版本还不会报告这些数字。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    // grid-template-columns:auto 1fr 1fr 1fr; gap:20px; align-items:end
    RowLayout {
        Layout.fillWidth: true
        visible: parse.statsAvailable
        spacing: 20

        ColumnLayout {
            Layout.alignment: Qt.AlignBottom
            Layout.minimumWidth: implicitWidth
            spacing: 2

            Text {
                text: qsTr("成功率")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
            }

            Text {
                objectName: "captureParseSuccessRate"
                text: parse.successRateText()
                color: Theme.eorzea ? Theme.gold2 : Theme.accent
                // A size workbench.css leaves alone, so it is not mapped by Theme.fs.
                font.pixelSize: 34
                font.family: Theme.figureFamily
                font.weight: Theme.figureWeight(true)
                font.features: ({ "tnum": 1 })
                lineHeight: 1.0
            }
        }

        Repeater {
            model: [
                { key: "rate", label: qsTr("消息速率"), value: parse.messageRateText() },
                { key: "failures", label: qsTr("失败 · 去重"),
                  value: parse.counter("parse_fail_count") + " · " + parse.counter("duplicate_count") },
                { key: "lastEvent", label: qsTr("最近有效事件"), value: parse.lastValidEventText() }
            ]

            delegate: ColumnLayout {
                required property var modelData

                Layout.fillWidth: true
                Layout.preferredWidth: implicitWidth
                Layout.alignment: Qt.AlignBottom
                Layout.bottomMargin: 4
                spacing: 2

                Text {
                    Layout.fillWidth: true
                    text: modelData.label
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    elide: Text.ElideRight
                }

                Text {
                    objectName: "captureParseValue_" + modelData.key
                    Layout.fillWidth: true
                    text: modelData.value
                    textFormat: Text.PlainText
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(16)
                    font.family: Theme.figureFamily
                    font.weight: Theme.figureWeight(true)
                    font.features: ({ "tnum": 1 })
                    wrapMode: Text.WordWrap
                }
            }
        }
    }

    // 最近失败: border-top divider, then rows of 64 px time · 140 px code · text.
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
            Layout.bottomMargin: 4
            text: qsTr("最近失败")
            color: Theme.textSecondary
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

                    Text {
                        Layout.preferredWidth: 140
                        Layout.alignment: Qt.AlignTop
                        Layout.topMargin: 1
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
                        color: Theme.textPrimary
                        opacity: 0.8
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
            Layout.topMargin: 8
            visible: parse.statsAvailable && App.parserErrors.length === 0
            text: qsTr("最近没有解析失败记录。")
            color: Theme.textSecondary
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
            text: qsTr("以上为模拟后端的示例数据，不是真实解析失败。")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }
}
