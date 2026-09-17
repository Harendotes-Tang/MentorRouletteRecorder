import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Item {
    id: page

    property int topLimit: 10
    property var rowsCache: []

    function reloadRows() {
        rowsCache = App.dungeons.topRows(topLimit === 0 ? 0 : topLimit)
    }

    Component.onCompleted: reloadRows()
    onTopLimitChanged: reloadRows()

    Connections {
        target: App.dungeons
        function onCountChanged() { page.reloadRows() }
    }

    Flickable {
        id: scroll
        anchors.fill: parent
        contentWidth: width
        contentHeight: contentColumn.implicitHeight
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        ScrollBar.vertical: ScrollBar {
            policy: scroll.contentHeight > scroll.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff
        }

        ColumnLayout {
            id: contentColumn
            width: scroll.width - Theme.scrollGutter
            spacing: 16

            PageHeader {
                title: qsTr("副本统计")
                // distinctCount is the server-side number of matching duties;
                // `count` is only what this page holds, truncated by paging and
                // by the Top-N selector above.
                subtitle: qsTr("%1 个副本 · %2 次").arg(App.dungeons.distinctCount)
                                                    .arg(App.dungeons.totalAttemptCount)

                SegmentedControl {
                    Layout.preferredWidth: 230
                    options: [
                        { value: 10, label: qsTr("前 10") },
                        { value: 20, label: qsTr("前 20") },
                        { value: 0, label: qsTr("全部") }
                    ]
                    currentValue: page.topLimit
                    onActivated: function(value) { page.topLimit = value }
                }
            }

            RowLayout {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignTop
                spacing: 12

                // ------------------------------------------------ 柱状图 --
                Card {
                    Layout.fillWidth: true
                    Layout.preferredWidth: 1
                    Layout.alignment: Qt.AlignTop
                    padding: 16
                    spacing: 6

                    CardKicker { text: qsTr("出现次数 · 点击柱状图筛选历史") }

                    Repeater {
                        model: page.rowsCache

                        delegate: RowLayout {
                            required property var modelData

                            Layout.fillWidth: true
                            Layout.preferredHeight: 22
                            spacing: 10

                            Text {
                                Layout.preferredWidth: 150
                                text: modelData.duty_name || qsTr("未知副本")
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                elide: Text.ElideRight
                            }

                            StatBar {
                                Layout.fillWidth: true
                                Layout.preferredHeight: 8
                                opacity: barHover.hovered ? 1.0 : 0.92
                                ratio: Number(modelData.attempt_count || 0)
                                       / Math.max(1, App.dungeons.maxAttemptCount)
                            }

                            Text {
                                Layout.preferredWidth: 66
                                text: qsTr("%1  %2%").arg(modelData.attempt_count || 0)
                                                      .arg(Math.round(1000 * Number(modelData.attempt_count || 0)
                                                                      / Math.max(1, App.dungeons.totalAttemptCount)) / 10)
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                font.weight: Theme.figureWeight(true)
                                font.family: Theme.figureFamily
                                font.features: ({ "tnum": 1 })
                            }

                            HoverHandler { id: barHover }
                            TapHandler {
                                onTapped: App.showHistoryForContent(modelData.content_id)
                            }
                        }
                    }
                }

                // -------------------------------------------------- 表格 --
                Card {
                    Layout.fillWidth: true
                    Layout.preferredWidth: 1
                    Layout.alignment: Qt.AlignTop
                    padding: 0
                    spacing: 0

                    RowLayout {
                        Layout.fillWidth: true
                        Layout.preferredHeight: 40
                        Layout.leftMargin: 16
                        Layout.rightMargin: 16
                        spacing: 8

                        Repeater {
                            model: [
                                { label: qsTr("副本"), width: 120, align: Text.AlignLeft },
                                { label: qsTr("类型"), width: 55, align: Text.AlignLeft },
                                { label: qsTr("次数"), width: 36, align: Text.AlignRight },
                                { label: qsTr("完成率"), width: 52, align: Text.AlignRight },
                                { label: qsTr("平均耗时"), width: 58, align: Text.AlignRight },
                                { label: qsTr("最近出现"), width: 68, align: Text.AlignRight }
                            ]

                            delegate: Text {
                                required property var modelData
                                Layout.preferredWidth: modelData.width
                                Layout.fillWidth: modelData.width === 120
                                text: modelData.label
                                // workbench `.table th`: t6, regular, text-3.
                                color: Theme.eorzea ? Theme.gold : Theme.textMuted
                                font.pixelSize: Theme.fs(11)
                                font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                                font.letterSpacing: Theme.eorzea ? 0.7 : 0
                                horizontalAlignment: modelData.align
                            }
                        }
                    }

                    Rectangle {
                        Layout.fillWidth: true
                        Layout.preferredHeight: 1
                        color: Theme.eorzea ? Theme.gold3 : Theme.border
                    }

                    Repeater {
                        model: page.rowsCache

                        delegate: Rectangle {
                            required property var modelData

                            Layout.fillWidth: true
                            Layout.preferredHeight: 51
                            color: rowHover.hovered
                                   ? (Theme.eorzea ? Theme.fill : Theme.insetBackground)
                                   : "transparent"

                            HoverHandler { id: rowHover }
                            TapHandler { onTapped: App.showHistoryForContent(modelData.content_id) }

                            RowLayout {
                                anchors.fill: parent
                                anchors.leftMargin: 16
                                anchors.rightMargin: 16
                                spacing: 8

                                RowLayout {
                                    Layout.fillWidth: true
                                    Layout.preferredWidth: 120
                                    spacing: 8

                                    CategoryIcon {
                                        category: modelData.duty_category || ""
                                        size: 24
                                    }

                                    ColumnLayout {
                                        Layout.fillWidth: true
                                        spacing: 0

                                        Text {
                                            Layout.fillWidth: true
                                            text: modelData.duty_name || qsTr("未知副本")
                                            color: Theme.textPrimary
                                            font.pixelSize: Theme.fs(12)
                                            font.bold: true
                                            elide: Text.ElideRight
                                        }

                                        Text {
                                            Layout.fillWidth: true
                                            text: modelData.duty_meta || Fmt.dash()
                                            color: Theme.textSecondary
                                            font.pixelSize: Theme.fs(10)
                                            font.family: Theme.figureFamily
                                            font.weight: Theme.figureWeight(false)
                                            font.features: ({ "tnum": 1 })
                                            elide: Text.ElideRight
                                        }
                                    }
                                }

                                Text {
                                    Layout.preferredWidth: 55
                                    text: modelData.duty_category || qsTr("未识别")
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(11)
                                    elide: Text.ElideRight
                                }

                                Text {
                                    Layout.preferredWidth: 36
                                    text: String(modelData.attempt_count || 0)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }

                                Text {
                                    Layout.preferredWidth: 52
                                    text: Fmt.percent(modelData.completion_rate)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }

                                Text {
                                    Layout.preferredWidth: 58
                                    text: Fmt.duration(modelData.avg_duration_ms)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }

                                Text {
                                    Layout.preferredWidth: 68
                                    text: modelData.last_seen_utc
                                          ? Fmt.localDate(modelData.last_seen_utc).substring(5) : "—"
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(11)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
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
                }
            }
        }
    }
}
