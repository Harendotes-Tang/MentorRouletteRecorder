pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// 副本统计: clears by duty category as one stacked bar with an icon legend.
ColumnLayout {
    id: root
    objectName: "floatingDungeonStatistics"

    required property var ov
    readonly property var stats: ov.controller.dungeons
    readonly property bool ready: ov.controller.statisticsConnected && !stats.loading && stats.loadError.length === 0
    property var buckets: []
    spacing: 0

    readonly property int clears: Number(stats.totalCompletedCount || 0)
    readonly property int attempts: Number(stats.totalAttemptCount || 0)
    readonly property string totalTip: qsTr("通关 %1 · 尝试 %2 · 通关率 %3")
        .arg(clears).arg(attempts).arg(Runtime.fmt.percent(attempts > 0 ? clears / attempts : undefined, 1))

    function reload() {
        buckets = ready
            ? stats.categoryBreakdown().filter(function(row) { return Number(row.completed_count || 0) > 0 })
            : []
    }
    Component.onCompleted: reload()
    Connections {
        target: root.stats
        function onCountChanged() { root.reload() }
    }
    onReadyChanged: reload()

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
            readonly property real total: Math.max(1, root.clears)
            readonly property int gap: 2
            // Where each segment starts and ends. Every edge is put on a device
            // pixel, so at 125 % or 150 % scaling the gaps are all the same width;
            // the last segment ends at the bar's own edge and the two ends take
            // the bar's rounding (2026-11-19: uneven gaps and a square end made
            // the segments look as if they were not on one line).
            // A category too small for its share keeps a 3 px floor; the floors are
            // set aside first and the rest of the bar is shared by the others, so a
            // dominant first segment never pushes the last one off (audit 2026-11-20 L3-9).
            readonly property int segmentFloor: 3
            readonly property var edges: {
                const out = []
                const n = root.buckets.length
                const room = Math.max(0, width - gap * (n - 1))
                const ratio = Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1
                const snap = function(v) { return Math.round(v * ratio) / ratio }
                const counts = root.buckets.map(function(row) { return Number(row.completed_count || 0) })
                let floored = 0
                let sharedCount = 0
                for (let i = 0; i < n; ++i) {
                    if (room * counts[i] / total < segmentFloor) floored += 1
                    else sharedCount += counts[i]
                }
                const shared = Math.max(0, room - segmentFloor * floored)
                let x = 0
                for (let i = 0; i < n; ++i) {
                    const small = room * counts[i] / total < segmentFloor || sharedCount <= 0
                    const span = small ? segmentFloor : shared * counts[i] / sharedCount
                    const end = i === n - 1 ? width : Math.min(width, x + span)
                    out.push({ from: snap(x), to: snap(Math.max(end, x)) })
                    x = end + gap
                }
                return out
            }
            Item {
                anchors.fill: parent
                Repeater {
                    model: root.buckets
                    delegate: Rectangle {
                        id: categorySegment
                        required property var modelData
                        required property int index
                        readonly property var edge: index < stack.edges.length ? stack.edges[index] : ({ from: 0, to: 0 })
                        objectName: "floatingCategorySegment"
                        x: edge.from
                        height: stack.height
                        width: Math.max(0, edge.to - edge.from)
                        topLeftRadius: index === 0 ? stack.radius : 0
                        bottomLeftRadius: topLeftRadius
                        topRightRadius: index === root.buckets.length - 1 ? stack.radius : 0
                        bottomRightRadius: topRightRadius
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

    Text {
        visible: root.attempts === 0 && root.ready
        text: qsTr("暂无统计")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
    }
}
