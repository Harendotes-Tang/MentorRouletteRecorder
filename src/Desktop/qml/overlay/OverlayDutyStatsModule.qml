pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// 副本统计: clears by duty category as one stacked bar with an icon legend,
// then the most-played duties ranked by attempts (副本排行 in 自定义).
ColumnLayout {
    id: root
    objectName: "floatingDungeonStatistics"

    required property var ov
    readonly property var stats: ov.controller.dungeons
    readonly property bool ready: ov.controller.statisticsConnected && !stats.loading && stats.loadError.length === 0
    property var buckets: []
    property var rows: []
    spacing: 0

    readonly property int clears: Number(stats.totalCompletedCount || 0)
    readonly property int attempts: Number(stats.totalAttemptCount || 0)
    readonly property int limit: root.ov.controller.rowLimit
    readonly property bool allRows: limit >= root.ov.controller.allRows
    readonly property string totalTip: qsTr("通关 %1 · 尝试 %2 · 通关率 %3")
        .arg(clears).arg(attempts).arg(Runtime.fmt.percent(attempts > 0 ? clears / attempts : undefined, 1))

    function reload() {
        if (!ready) { buckets = []; rows = []; return }
        buckets = stats.categoryBreakdown().filter(function(row) { return Number(row.completed_count || 0) > 0 })
        const all = stats.topRows(0).filter(function(row) { return Number(row.attempt_count || 0) > 0 })
        rows = allRows ? all : all.slice(0, limit)
    }
    Component.onCompleted: reload()
    Connections {
        target: root.stats
        function onCountChanged() { root.reload() }
        function onLoadingChanged() { root.reload() }
        function onLoadErrorChanged() { root.reload() }
    }
    Connections {
        target: root.ov.controller
        function onRowLimitChanged() { root.reload() }
        function onAttemptsChanged() { root.reload() }
    }
    onLimitChanged: reload()

    OverlayModuleLabel { ov: root.ov; text: qsTr("副本统计 · 按类型通关"); Layout.bottomMargin: 5 }

    // The stacked bar: `.stk`, 1 px gaps, segments by completed count.
    RowLayout {
        Layout.fillWidth: true
        spacing: 8

        OverlayIcon { source: Lucide.source("swords", color); color: Theme.textSecondary; Layout.alignment: Qt.AlignVCenter }

        Rectangle {
            id: stack
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignVCenter
            implicitHeight: 10
            radius: Theme.eorzea ? Theme.radiusXs : 5
            color: Theme.insetBackgroundStrong
            clip: true
            readonly property real total: Math.max(1, root.clears)
            Row {
                anchors.fill: parent
                spacing: 1
                Repeater {
                    model: root.buckets
                    delegate: Rectangle {
                        id: categorySegment
                        required property var modelData
                        height: stack.height
                        width: Math.max(3, Math.round((stack.width - (root.buckets.length - 1)) * Number(modelData.completed_count || 0) / stack.total))
                        color: Theme.token(modelData.color_token || "neutral500")
                        HoverHandler { id: segmentHover }
                        OverlayToolTip {
                            visible: segmentHover.hovered
                            text: qsTr("%1 · 通关 %2 次（%3）").arg(categorySegment.modelData.label).arg(Number(categorySegment.modelData.completed_count || 0))
                                  .arg(Runtime.fmt.percent(categorySegment.modelData.share, 1))
                        }
                    }
                }
            }
        }

        Text {
            objectName: "floatingDungeonState"
            Layout.alignment: Qt.AlignVCenter
            text: !root.ov.controller.statisticsConnected ? qsTr("后台服务未连接")
                : root.stats.loading ? qsTr("读取中…")
                : root.stats.loadError.length > 0 ? root.stats.loadError : "%1/%2".arg(root.clears).arg(root.attempts)
            textFormat: Text.PlainText
            color: root.stats.loadError.length > 0 ? Theme.orangeText : Theme.textSecondary
            font.pixelSize: Theme.fs(11.5)
            font.family: Theme.figureFamily
            font.features: ({ "tnum": 1 })
            Layout.maximumWidth: 120
            elide: Text.ElideRight
            HoverHandler { id: totalHover }
            OverlayToolTip { visible: totalHover.hovered && root.ready; text: root.totalTip }
        }
    }

    // `.lg`: the legend of game category icons, wrapping; names only with 文字标签.
    Flow {
        Layout.fillWidth: true
        Layout.leftMargin: 22
        Layout.topMargin: 7
        spacing: 11
        Repeater {
            model: root.buckets
            delegate: Row {
                id: categoryLegend
                required property var modelData
                spacing: 4
                CategoryIcon {
                    anchors.verticalCenter: parent.verticalCenter
                    category: root.ov.categoryForLabel(categoryLegend.modelData.label)
                    size: 18
                }
                Text {
                    anchors.verticalCenter: parent.verticalCenter
                    visible: root.ov.labels
                    text: categoryLegend.modelData.label || ""
                    textFormat: Text.PlainText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11.5)
                    font.weight: Font.Medium
                }
                Text {
                    anchors.verticalCenter: parent.verticalCenter
                    text: String(Number(categoryLegend.modelData.completed_count || 0))
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12)
                    font.weight: Font.Bold
                    font.family: Theme.figureFamily
                    font.features: ({ "tnum": 1 })
                }
                HoverHandler { id: legendHover }
                OverlayToolTip {
                    visible: legendHover.hovered
                    text: qsTr("%1 · 通关 %2 次（%3）").arg(categoryLegend.modelData.label).arg(Number(categoryLegend.modelData.completed_count || 0))
                          .arg(Runtime.fmt.percent(categoryLegend.modelData.share, 1))
                }
            }
        }
    }

    OverlayModuleLabel {
        ov: root.ov
        Layout.topMargin: 9
        text: root.allRows ? qsTr("出现次数 · 全部") : qsTr("出现次数 · 前 %1").arg(root.limit)
        visible: root.ov.labels && root.limit > 0 && root.rows.length > 0
    }

    // `.rk`: the ranking rows; 全部 scrolls inside 186 px.
    ScrollView {
        id: ranking
        visible: root.limit > 0 && root.rows.length > 0
        Layout.fillWidth: true
        Layout.topMargin: root.ov.labels ? 5 : 9
        Layout.preferredHeight: root.allRows ? Math.min(186, rankColumn.implicitHeight) : rankColumn.implicitHeight
        clip: true
        contentWidth: availableWidth
        ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
        ScrollBar.vertical.policy: root.allRows && rankColumn.implicitHeight > 186 ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff
        ColumnLayout {
            id: rankColumn
            width: ranking.availableWidth
            spacing: 3
            Repeater {
                model: root.rows
                delegate: Item {
                    id: rankRow
                    required property var modelData
                    readonly property color tone: root.ov.categoryColor(modelData.duty_category)
                    readonly property string dutyName: Theme.dutyName(modelData) || qsTr("未识别")
                    Layout.fillWidth: true
                    implicitHeight: 20
                    Rectangle {
                        anchors.left: parent.left
                        anchors.top: parent.top
                        anchors.bottom: parent.bottom
                        width: Math.max(2, parent.width * Math.min(1, Number(rankRow.modelData.attempt_count || 0) / Math.max(1, root.stats.maxAttemptCount)))
                        radius: Theme.radiusXs
                        color: Qt.rgba(rankRow.tone.r, rankRow.tone.g, rankRow.tone.b, Theme.dark ? 0.21 : 0.17)
                        Rectangle { width: 2; height: parent.height; color: rankRow.tone }
                    }
                    Text {
                        anchors.left: parent.left
                        anchors.right: count.left
                        anchors.verticalCenter: parent.verticalCenter
                        anchors.leftMargin: 8
                        anchors.rightMargin: 6
                        text: rankRow.dutyName
                        textFormat: Text.PlainText
                        elide: Text.ElideRight
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                    }
                    Text {
                        id: count
                        anchors.right: parent.right
                        anchors.rightMargin: 6
                        anchors.verticalCenter: parent.verticalCenter
                        text: String(Number(rankRow.modelData.attempt_count || 0))
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                        font.weight: Font.Bold
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                    }
                    HoverHandler { id: rowHover }
                    OverlayToolTip {
                        visible: rowHover.hovered
                        text: qsTr("%1 · %2 次 · %3").arg(rankRow.dutyName).arg(Number(rankRow.modelData.attempt_count || 0))
                              .arg(Theme.categoryName(rankRow.modelData.duty_category) || qsTr("未识别"))
                    }
                }
            }
        }
    }

    Text {
        visible: root.rows.length === 0 && root.buckets.length === 0 && root.ready
        text: qsTr("暂无统计")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
    }
}
