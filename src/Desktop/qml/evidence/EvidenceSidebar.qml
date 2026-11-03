pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// `.ev-side`: 本轮状态 (the state, the clock against the 2 h limit, region and
// adapter, the four counters) and what the round promises.
ColumnLayout {
    id: side

    required property EvidenceSession session

    spacing: 10

    Card {
        objectName: "evidenceStatusCard"
        Layout.fillWidth: true
        horizontalPadding: 16
        verticalPadding: 14
        spacing: 10

        CardKicker { text: qsTr("本轮状态") }

        // `.ev-big`
        RowLayout {
            spacing: 9
            EvidencePulse {
                objectName: "evidenceStatusPulse"
                tone: side.session.active ? side.session.badgeTone : "off"
            }
            Text {
                objectName: "evidenceStatusHeadline"
                textFormat: Text.PlainText
                text: side.session.badgeText
                color: side.session.badgeTone === "off" ? Theme.textSecondary : Theme.toneColor(side.session.badgeTone)
                font.pixelSize: Theme.fs(17)
                font.weight: Font.Bold
                Behavior on color { ColorAnimation { duration: Theme.motionControl } }
            }
        }

        // `.ev-timer`
        RowLayout {
            spacing: 6
            Text {
                objectName: "evidenceElapsed"
                textFormat: Text.PlainText
                text: side.session.elapsedText
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(24)
                font.family: Theme.figureFamily
                font.weight: Theme.figureWeight(true)
                font.features: ({ "tnum": 1 })
            }
            Text {
                Layout.alignment: Qt.AlignBaseline
                textFormat: Text.PlainText
                text: "/ " + side.session.durationText(side.session.maxSeconds)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                font.family: Theme.figureFamily
            }
        }
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 6
            radius: 3
            color: Theme.insetBackgroundStrong
            Rectangle {
                width: parent.width * Math.min(1, side.session.elapsedSeconds / side.session.maxSeconds)
                height: parent.height
                radius: 3
                gradient: Gradient {
                    orientation: Gradient.Horizontal
                    GradientStop { position: 0.0; color: Theme.barFillStart }
                    GradientStop { position: 1.0; color: Theme.barFillEnd }
                }
            }
        }
        Text {
            Layout.fillWidth: true
            textFormat: Text.PlainText
            text: side.session.blank ? qsTr("还没开始")
                  : side.session.regionLabel + " · " + side.session.adapterLabel
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            elide: Text.ElideRight
        }

        Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

        // `.ev-cnts`
        GridLayout {
            Layout.fillWidth: true
            columns: 2
            columnSpacing: 12
            rowSpacing: 10
            Repeater {
                model: [
                    { key: "message_count", label: qsTr("报文"), tone: side.session.stalled ? "bad" : "",
                      rate: side.session.recording && !side.session.anomaly && side.session.messageRate > 0
                            ? qsTr("+%1 / 秒").arg(Math.round(side.session.messageRate)) : "" },
                    { key: "marker_count", label: qsTr("已写入标记"), tone: "", rate: "" },
                    { key: "decode_error_count", label: qsTr("解码失败"),
                      tone: (side.session.decodeErrors || 0) > 0 ? (side.session.decodeStorm ? "bad" : "warn") : "", rate: "" },
                    { key: "queue_dropped", label: qsTr("队列丢弃"), tone: (side.session.queueDropped || 0) > 0 ? "warn" : "", rate: "" }
                ]
                delegate: ColumnLayout {
                    id: counter
                    required property var modelData
                    Layout.fillWidth: true
                    spacing: 2
                    Text {
                        textFormat: Text.PlainText
                        text: counter.modelData.label
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                    }
                    Text {
                        objectName: "evidenceCounter_" + counter.modelData.key
                        textFormat: Text.PlainText
                        text: side.session.blank ? Runtime.fmt.dash()
                              : side.session.countText(side.session.counter(counter.modelData.key))
                        color: counter.modelData.tone === "bad" ? Theme.red
                               : counter.modelData.tone === "warn" ? Theme.orangeText : Theme.textPrimary
                        font.pixelSize: Theme.fs(18)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(true)
                        font.features: ({ "tnum": 1 })
                    }
                    Text {
                        visible: counter.modelData.rate.length > 0
                        textFormat: Text.PlainText
                        text: counter.modelData.rate
                        color: Theme.green
                        font.pixelSize: Theme.fs(11)
                        font.family: Theme.figureFamily
                    }
                }
            }
        }
    }

    Card {
        objectName: "evidencePromiseCard"
        Layout.fillWidth: true
        horizontalPadding: 16
        verticalPadding: 14
        spacing: 10

        Repeater {
            model: [
                { icon: "clipboard-check", text: qsTr("仅验证，不自动记录") },
                { icon: "hard-drive", text: qsTr("取证文件只存在本机") },
                { icon: "cloud-off", text: qsTr("不会自动上传") },
                { icon: "clock", text: qsTr("单轮最长 2 小时，含等待时间") }
            ]
            delegate: RowLayout {
                id: promise
                required property var modelData
                Layout.fillWidth: true
                spacing: 9
                EvidenceIcon { name: promise.modelData.icon; color: Theme.green; size: 15 }
                Text {
                    Layout.fillWidth: true
                    textFormat: Text.PlainText
                    text: promise.modelData.text
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12.5)
                    wrapMode: Text.Wrap
                }
            }
        }
    }
}
