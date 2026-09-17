import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

Item {
    id: page

    property var rowsCache: []
    property var rolesCache: []

    readonly property int unknownCount: {
        for (let index = 0; index < rowsCache.length; ++index) {
            if (rowsCache[index].job_id === null || rowsCache[index].job_id === undefined)
                return Number(rowsCache[index].attempt_count || 0)
        }
        return 0
    }

    function reloadRows() {
        rowsCache = App.jobs.topRows(0)
        rolesCache = App.jobs.roleBreakdown()
        donut.requestPaint()
    }

    Component.onCompleted: reloadRows()

    Connections {
        target: App.jobs
        function onCountChanged() { page.reloadRows() }
    }

    Connections {
        target: Theme
        function onDarkChanged() { donut.requestPaint() }
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
                title: qsTr("职业统计")
                subtitle: qsTr("未知职业 %1 次").arg(page.unknownCount)
            }

            RowLayout {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignTop
                spacing: 12

                // ------------------------------------------------ 职业占比 --
                Card {
                    Layout.preferredWidth: 320
                    Layout.alignment: Qt.AlignTop
                    padding: 20

                    CardKicker { text: qsTr("职业占比") }

                    Canvas {
                        id: donut

                        Layout.preferredWidth: 200
                        Layout.preferredHeight: 200
                        Layout.alignment: Qt.AlignHCenter

                        onPaint: {
                            const context = getContext("2d")
                            context.reset()
                            let total = 0
                            for (let index = 0; index < page.rolesCache.length; ++index)
                                total += Number(page.rolesCache[index].attempt_count || 0)
                            if (total <= 0)
                                return
                            let start = -Math.PI / 2
                            context.lineWidth = 28
                            context.lineCap = "butt"
                            for (let index = 0; index < page.rolesCache.length; ++index) {
                                const row = page.rolesCache[index]
                                const span = Math.PI * 2 * Number(row.attempt_count || 0) / total
                                if (span <= 0)
                                    continue
                                context.beginPath()
                                context.strokeStyle = Theme.token(row.color_token || "neutral500")
                                context.arc(100, 100, 80, start, start + span)
                                context.stroke()
                                start += span
                            }
                        }
                    }

                    ColumnLayout {
                        Layout.fillWidth: true
                        spacing: 6

                        Repeater {
                            model: page.rolesCache

                            delegate: RowLayout {
                                required property var modelData

                                Layout.fillWidth: true
                                Layout.preferredHeight: 22
                                spacing: 8

                                // The donut uses the palette colours; the legend
                                // keeps a thin accent beside the game glyph to
                                // tie the two together.
                                Rectangle {
                                    Layout.preferredWidth: 3
                                    Layout.preferredHeight: 16
                                    radius: 1.5
                                    color: Theme.token(modelData.color_token || "neutral500")
                                }

                                RoleIcon {
                                    role: modelData.role_group || qsTr("未知")
                                    size: 20
                                }

                                Text {
                                    Layout.fillWidth: true
                                    text: modelData.role_group || qsTr("未知")
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                }

                                Text {
                                    Layout.preferredWidth: 44
                                    text: qsTr("%1%").arg(Math.round(1000 * Number(modelData.attempt_count || 0)
                                                                  / Math.max(1, App.jobs.totalAttemptCount)) / 10)
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(12)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }

                                Text {
                                    Layout.preferredWidth: 28
                                    text: String(modelData.attempt_count || 0)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.weight: Theme.figureWeight(true)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.features: ({ "tnum": 1 })
                                }
                            }
                        }
                    }
                }

                // ---------------------------------------------------- 表格 --
                Card {
                    Layout.fillWidth: true
                    Layout.alignment: Qt.AlignTop
                    Layout.minimumWidth: 0
                    padding: 0
                    spacing: 0

                    RowLayout {
                        Layout.fillWidth: true
                        Layout.preferredHeight: 40
                        Layout.leftMargin: 16
                        Layout.rightMargin: 16
                        spacing: 10

                        Text {
                            Layout.fillWidth: true
                            Layout.preferredWidth: 150
                            text: qsTr("职业")
                            // workbench `.table th`: t6, regular, text-3.
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                        }

                        Text {
                            Layout.preferredWidth: 84
                            text: qsTr("角色")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                        }

                        Text {
                            Layout.preferredWidth: 220
                            text: qsTr("次数")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                        }

                        Text {
                            Layout.preferredWidth: 58
                            text: qsTr("完成率")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                            horizontalAlignment: Text.AlignRight
                        }

                        Text {
                            Layout.preferredWidth: 68
                            text: qsTr("平均耗时")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                            horizontalAlignment: Text.AlignRight
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
                            Layout.preferredHeight: 58
                            color: rowHover.hovered
                                   ? (Theme.eorzea ? Theme.fill : Theme.insetBackground)
                                   : "transparent"

                            HoverHandler { id: rowHover }
                            TapHandler { onTapped: App.showHistoryForJob(modelData.job_id) }

                            RowLayout {
                                anchors.fill: parent
                                anchors.leftMargin: 16
                                anchors.rightMargin: 16
                                spacing: 10

                                RowLayout {
                                    Layout.fillWidth: true
                                    Layout.preferredWidth: 150
                                    spacing: 8

                                    JobIcon {
                                        jobId: modelData.job_id
                                        size: 26
                                    }

                                    Text {
                                        Layout.fillWidth: true
                                        text: modelData.job_name || qsTr("未知")
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(12)
                                        font.bold: true
                                        elide: Text.ElideRight
                                    }
                                }

                                RoleIcon {
                                    Layout.preferredWidth: 84
                                    // The fine-grained group of the legend
                                    // (坦克 / 治疗 / 近战 / 远程物理 / 魔法 /
                                    // 未知), never the coarse DPS bucket.
                                    role: Jobs.roleGroup(modelData.job_id)
                                    size: 20
                                    showLabel: true
                                    labelPixelSize: 11
                                }

                                RowLayout {
                                    Layout.preferredWidth: 220
                                    spacing: 8

                                    StatBar {
                                        Layout.fillWidth: true
                                        Layout.preferredHeight: 8
                                        opacity: rowHover.hovered ? 1.0 : 0.92
                                        ratio: Number(modelData.attempt_count || 0)
                                               / Math.max(1, App.jobs.maxAttemptCount)
                                    }

                                    Text {
                                        Layout.preferredWidth: 28
                                        text: String(modelData.attempt_count || 0)
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(12)
                                        font.weight: Theme.figureWeight(true)
                                        font.family: Theme.figureFamily
                                        font.features: ({ "tnum": 1 })
                                        horizontalAlignment: Text.AlignRight
                                    }
                                }

                                Text {
                                    Layout.preferredWidth: 58
                                    text: Fmt.percent(modelData.completion_rate)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }

                                Text {
                                    Layout.preferredWidth: 68
                                    text: Fmt.duration(modelData.avg_duration_ms)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                    horizontalAlignment: Text.AlignRight
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
