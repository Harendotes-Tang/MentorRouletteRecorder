pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// One composition card of 副本统计: a ring of completion shares with its legend
// (ShareDonut). Raw counts come from DungeonStatsModel, never from table Top-N.
// Only this view's optional selection changes the denominator; raw buckets stay
// intact. A bucket with a `history_filter` opens its runs (`bucketActivated`).
Card {
    id: root

    property string title: ""
    property string detail: ""
    property var buckets: []
    property var hiddenBucketKeys: []
    /// Largest share first, as the type chart is read; 未识别 stays last.
    property bool sortByCount: false
    readonly property var selectedBuckets: {
        const selected = buckets.filter(bucket => hiddenBucketKeys.indexOf(bucket.key) < 0)
        let total = 0
        for (const bucket of selected)
            total += Number(bucket.completed_count || 0)
        return selected.map(bucket => Object.assign({}, bucket, {
            share: total > 0 ? Number(bucket.completed_count || 0) / total : null
        }))
    }
    readonly property bool selectionFiltered: selectedBuckets.length !== buckets.length
    readonly property var visibleBuckets: selectedBuckets.filter(bucket =>
        !bucket.is_unknown || Number(bucket.completed_count || 0) !== 0)
    readonly property real completedTotal: {
        let total = 0
        for (const bucket of selectedBuckets)
            total += Number(bucket.completed_count || 0)
        return total
    }
    readonly property bool stacked: width < 380
    readonly property string totalCaption: selectionFiltered
        ? qsTr("所选通关 %1 次").arg(completedTotal)
        : qsTr("全部通关 %1 次").arg(completedTotal)
    /// The bucket picked out by the pointer or the keyboard, "" when none.
    readonly property alias highlightedKey: donut.hotKey

    signal bucketActivated(var bucket)

    // The ring and the legend read the same list: the selected buckets in the
    // model's order, or by count when asked. Array.sort is stable, so equal
    // counts keep the model's order.
    readonly property var chartItems: {
        const ordered = visibleBuckets.map((bucket, position) => ({ bucket: bucket, position: position }))
        if (sortByCount) {
            ordered.sort((a, b) => (a.bucket.is_unknown ? 1 : 0) - (b.bucket.is_unknown ? 1 : 0)
                         || Number(b.bucket.completed_count || 0) - Number(a.bucket.completed_count || 0)
                         || a.position - b.position)
        }
        return ordered.map(entry => ({
            key: entry.bucket.key,
            label: entry.bucket.label || qsTr("未识别"),
            color: Theme.token(entry.bucket.color_token || "neutral500"),
            count: Number(entry.bucket.completed_count || 0),
            linkable: root.hasHistory(entry.bucket),
            quiet: entry.bucket.is_unknown === true || entry.bucket.color_token === "dutyOther"
        }))
    }

    function hasHistory(bucket) {
        const filter = bucket.history_filter
        return filter !== undefined && filter !== null && Object.keys(filter).length > 0
    }

    function activate(key) {
        for (const bucket of visibleBuckets) {
            if (bucket.key === key && hasHistory(bucket)) {
                root.bucketActivated(bucket)
                return
            }
        }
    }

    padding: 16
    spacing: 12

    Accessible.role: Accessible.StaticText
    Accessible.name: title + " · " + totalCaption

    Connections {
        target: root.pageHost
        enabled: root.pageHost !== null
        function onEntered() { donut.playEntrance() }
    }

    ShareDonutHead {
        Layout.fillWidth: true
        title: root.title
        hintVisible: donut.linkable
    }

    ShareDonut {
        id: donut
        objectName: "completionPieDonut"
        Layout.fillWidth: true
        stacked: root.stacked
        title: root.title
        items: root.chartItems
        caption: root.selectionFiltered ? qsTr("所选通关") : qsTr("通关次数")
        emptyText: root.selectedBuckets.length === 0 ? qsTr("未选择分类")
                   : root.selectionFiltered ? qsTr("所选分类暂无通关") : qsTr("暂无通关")
        note: root.detail
        onActivated: function(key) { root.activate(key) }
    }
}
