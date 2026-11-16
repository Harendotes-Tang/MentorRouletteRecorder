pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

Item {
    id: page

    property var rowsCache: []
    property var rolesCache: []
    // The 中途加入 column: 58 px holds the four CJK characters; the English
    // header ("Joined late") is wider and takes the room it needs.
    readonly property int joinedColumnWidth: Math.max(58, Math.ceil(joinedHeaderMeasure.implicitWidth))
    // The table's columns. 职业 is the only flexible one; every other column
    // pins its minimum to its width (a layout item may otherwise shrink to 0),
    // so header and rows always agree on where each column starts. The 次数
    // bar takes 180 px when there is room (the prototype's 34 % at 1280) and
    // otherwise whatever is left after the job name's 150 px, down to 48 px
    // beside the count: at the 1100 px window minimum the bar gives way first,
    // then the name, and the fixed columns never spill past the card.
    readonly property int jobColumnWidth: 150
    readonly property int roleColumnWidth: 84
    readonly property int rateColumnWidth: 58
    readonly property int durationColumnWidth: 68
    readonly property int tableColumnSpacing: 10
    readonly property int runsColumnWidth: Math.max(48, Math.min(180,
        contentColumn.width - 320 - 12 - 32 - 5 * tableColumnSpacing - jobColumnWidth - roleColumnWidth
        - joinedColumnWidth - rateColumnWidth - durationColumnWidth))

    readonly property int unknownCount: {
        const unknown = Theme.jobLabel(null, "未知")
        let count = 0
        for (let index = 0; index < rowsCache.length; ++index) {
            if ((Theme.jobName(rowsCache[index]) || unknown) === unknown)
                count += Number(rowsCache[index].attempt_count || 0)
        }
        return count
    }

    // Never shown: the 中途加入 header in the header row's font, so the
    // column can be as wide as its own title.
    Text {
        id: joinedHeaderMeasure
        visible: false
        text: qsTr("中途加入")
        textFormat: Text.PlainText
        font.pixelSize: Theme.fs(11)
        font.weight: Theme.eorzea ? Font.Bold : Font.Normal
        font.letterSpacing: Theme.eorzea ? 0.7 : 0
    }

    function reloadRows() {
        const unknown = Theme.jobLabel(null, "未知")
        rowsCache = Runtime.app.jobs.topRows(0).filter(row => Number(row.attempt_count || 0) !== 0
            || (Theme.jobName(row) || unknown) !== unknown)
        rolesCache = Runtime.app.jobs.roleBreakdown().filter(row => row.role_group !== "未知"
            || Number(row.attempt_count || 0) !== 0)
    }

    // The role ring and its legend (ShareDonut): the groups in the model's
    // order, each in its palette colour with the game's role glyph. 未知 is the
    // neutral remainder.
    readonly property var roleItems: rolesCache.map(row => ({
        key: row.role_group,
        label: Theme.roleGroupName(row.role_group),
        color: Theme.token(row.color_token || "neutral500"),
        count: Number(row.attempt_count || 0),
        linkable: page.hasHistory(row),
        quiet: row.role_group === "未知",
        role: row.role_group
    }))

    function hasHistory(row) {
        const filter = row.history_filter
        return filter !== undefined && filter !== null && Object.keys(filter).length > 0
    }

    function showHistoryForRole(roleGroup) {
        for (const row of rolesCache) {
            if (row.role_group === roleGroup && hasHistory(row)) {
                Runtime.app.showHistoryForJobs(row.history_filter)
                return
            }
        }
    }

    Component.onCompleted: reloadRows()

    Connections {
        target: Runtime.app.jobs
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
                objectName: "jobsHeader"
                title: qsTr("职业统计")
                RecordRegionSelector {
                    objectName: "statisticsRegionFilter"
                    Layout.preferredWidth: 150
                    selection: Runtime.app.recordRegion
                    onRegionPicked: function(region) { Runtime.app.recordRegion = region }
                }
                subtitle: Runtime.app.jobs.loadError.length > 0
                          ? qsTr("读取失败")
                          : page.unknownCount !== 0 ? qsTr("未知职业 %1 次").arg(page.unknownCount) : ""
            }

            Text {
                objectName: "jobsLoadError"
                Layout.fillWidth: true
                visible: Runtime.app.jobs.loadError.length > 0
                text: qsTr("职业统计读取失败，下方没有统计结果：%1").arg(Runtime.app.jobs.loadError)
                textFormat: Text.PlainText
                wrapMode: Text.WordWrap
                color: Theme.orangeText
                font.pixelSize: Theme.fs(13)
            }

            RowLayout {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignTop
                spacing: 12

                // ------------------------------------------------ 职能占比 --
                Card {
                    id: roleCard
                    objectName: "jobsRoleCard"
                    Layout.preferredWidth: 320
                    Layout.alignment: Qt.AlignTop
                    padding: 20
                    spacing: 12

                    Connections {
                        target: roleCard.pageHost
                        enabled: roleCard.pageHost !== null
                        function onEntered() { donut.playEntrance() }
                    }

                    ShareDonutHead {
                        Layout.fillWidth: true
                        title: qsTr("职能占比")
                        hintVisible: donut.linkable
                    }

                    // The ring's colours are bound through roleItems, so a theme
                    // or a style switch rebuilds its segments (review OK-9).
                    ShareDonut {
                        id: donut
                        objectName: "jobsDonut"
                        Layout.fillWidth: true
                        stacked: true
                        title: qsTr("职能占比")
                        items: page.roleItems
                        caption: qsTr("导随次数")
                        emptyText: qsTr("还没有导随记录")
                        onActivated: function(key) { page.showHistoryForRole(key) }
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
                        spacing: page.tableColumnSpacing

                        Text {
                            objectName: "jobsTableHeaderJob"
                            Layout.fillWidth: true
                            Layout.preferredWidth: page.jobColumnWidth
                            text: qsTr("职业")
                            // workbench `.table th`: t6, regular, text-3.
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                        }

                        Text {
                            Layout.preferredWidth: page.roleColumnWidth
                            Layout.minimumWidth: page.roleColumnWidth
                            text: qsTr("职能")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                        }

                        Text {
                            objectName: "jobsTableHeaderRuns"
                            Layout.preferredWidth: page.runsColumnWidth
                            Layout.minimumWidth: page.runsColumnWidth
                            text: qsTr("次数")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                        }

                        Text {
                            objectName: "jobsTableHeaderJoined"
                            Layout.preferredWidth: page.joinedColumnWidth
                            Layout.minimumWidth: page.joinedColumnWidth
                            text: qsTr("中途加入")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                            horizontalAlignment: Text.AlignRight
                        }

                        Text {
                            Layout.preferredWidth: page.rateColumnWidth
                            Layout.minimumWidth: page.rateColumnWidth
                            text: qsTr("完成率")
                            color: Theme.eorzea ? Theme.gold : Theme.textMuted
                            font.pixelSize: Theme.fs(11)
                            font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                            font.letterSpacing: Theme.eorzea ? 0.7 : 0
                            horizontalAlignment: Text.AlignRight
                        }

                        Text {
                            Layout.preferredWidth: page.durationColumnWidth
                            Layout.minimumWidth: page.durationColumnWidth
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
                        objectName: "jobsTableRows"
                        model: page.rowsCache

                        delegate: Rectangle {
                            id: jobRow
                            required property var modelData
                            required property int index
                            objectName: "jobsTableRow" + index

                            Layout.fillWidth: true
                            Layout.preferredHeight: 58
                            color: rowHover.hovered
                                   ? (Theme.eorzea ? Theme.fill : Theme.insetBackground)
                                   : "transparent"

                            HoverHandler { id: rowHover }
                            TapHandler { onTapped: Runtime.app.showHistoryForJob(jobRow.modelData.job_id) }

                            RowLayout {
                                anchors.fill: parent
                                anchors.leftMargin: 16
                                anchors.rightMargin: 16
                                spacing: page.tableColumnSpacing

                                RowLayout {
                                    objectName: "jobRowJob"
                                    Layout.fillWidth: true
                                    Layout.preferredWidth: page.jobColumnWidth
                                    spacing: 8

                                    JobIcon {
                                        jobId: jobRow.modelData.job_id
                                        size: 26
                                    }

                                    Text {
                                        Layout.fillWidth: true
                                        text: Theme.jobName(jobRow.modelData) || qsTr("未知")
                                        textFormat: Text.PlainText
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(12)
                                        font.bold: true
                                        elide: Text.ElideRight
                                    }
                                }

                                RoleIcon {
                                    objectName: "jobRowRole"
                                    // RoleIcon is a layout and would fill by default, unlike the header Text.
                                    Layout.fillWidth: false
                                    Layout.preferredWidth: page.roleColumnWidth
                                    Layout.minimumWidth: page.roleColumnWidth
                                    // The fine-grained group of the legend
                                    // (坦克 / 治疗 / 近战 / 远程物理 / 魔法 /
                                    // 未知), never the coarse DPS bucket.
                                    role: jobRow.modelData.role_group || "未知"
                                    size: 20
                                    showLabel: true
                                    labelPixelSize: 11
                                }

                                RowLayout {
                                    objectName: "jobRowRuns"
                                    // A nested layout fills by default, unlike the header Text above it.
                                    Layout.fillWidth: false
                                    Layout.preferredWidth: page.runsColumnWidth
                                    Layout.minimumWidth: page.runsColumnWidth
                                    spacing: 8

                                    StatBar {
                                        Layout.fillWidth: true
                                        Layout.preferredHeight: 8
                                        opacity: rowHover.hovered ? 1.0 : 0.92
                                        ratio: Number(jobRow.modelData.attempt_count || 0)
                                               / Math.max(1, Runtime.app.jobs.maxAttemptCount)
                                    }

                                    Text {
                                        Layout.preferredWidth: 28
                                        text: String(jobRow.modelData.attempt_count || 0)
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(12)
                                        font.weight: Theme.figureWeight(true)
                                        font.family: Theme.figureFamily
                                        font.features: ({ "tnum": 1 })
                                        horizontalAlignment: Text.AlignRight
                                    }
                                }

                                // 中途加入 (docs/statistics-definitions.md §12.2); an
                                // older Collector sends no such field, which reads
                                // as 0 like a job that never joined late.
                                Text {
                                    objectName: "jobRowJoinedInProgress"
                                    Layout.preferredWidth: page.joinedColumnWidth
                                    Layout.minimumWidth: page.joinedColumnWidth
                                    text: String(jobRow.modelData.joined_in_progress_count || 0)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }

                                Text {
                                    Layout.preferredWidth: page.rateColumnWidth
                                    Layout.minimumWidth: page.rateColumnWidth
                                    text: Runtime.fmt.percent(jobRow.modelData.completion_rate)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    horizontalAlignment: Text.AlignRight
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }

                                Text {
                                    Layout.preferredWidth: page.durationColumnWidth
                                    Layout.minimumWidth: page.durationColumnWidth
                                    text: Runtime.fmt.duration(jobRow.modelData.avg_duration_ms)
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
