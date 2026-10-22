pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 职业统计: attempts by role group as a row of small columns, each topped by
// its count and footed by the role glyph; the name only with 文字标签.
ColumnLayout {
    id: root
    objectName: "floatingJobStatistics"

    required property var ov
    readonly property var stats: ov.controller.jobs
    readonly property bool ready: ov.controller.statisticsConnected && !stats.loading && stats.loadError.length === 0
    property var columns: []
    spacing: 0

    function reload() {
        if (!ready) { columns = []; return }
        const total = Number(stats.totalAttemptCount || 0)
        columns = stats.roleBreakdown()
            .filter(function(row) { return Number(row.attempt_count || 0) > 0 })
            .map(function(row) {
                return { group: row.role_group, count: Number(row.attempt_count || 0),
                         share: total > 0 ? Number(row.attempt_count || 0) / total : undefined,
                         color_token: row.color_token }
            })
    }
    Component.onCompleted: reload()
    Connections {
        target: root.stats
        function onCountChanged() { root.reload() }
        function onLoadingChanged() { root.reload() }
        function onLoadErrorChanged() { root.reload() }
    }
    Connections { target: root.ov.controller; function onAttemptsChanged() { root.reload() } }

    readonly property int maxCount: {
        let max = 1
        for (let i = 0; i < columns.length; ++i) max = Math.max(max, columns[i].count)
        return max
    }

    OverlayModuleLabel { ov: root.ov; text: qsTr("职能占比 · 尝试"); Layout.bottomMargin: 5 }

    RowLayout {
        objectName: "floatingJobState"
        Layout.fillWidth: true
        spacing: 4
        Repeater {
            model: root.columns
            delegate: ColumnLayout {
                id: column
                required property var modelData
                readonly property color tone: Theme.token(modelData.color_token || "neutral500")
                readonly property string groupName: Theme.roleGroupName(modelData.group)
                Layout.fillWidth: true
                Layout.minimumWidth: 0
                spacing: 3
                Text {
                    Layout.alignment: Qt.AlignHCenter
                    text: String(column.modelData.count)
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(11.5)
                    font.weight: Font.Bold
                    font.family: Theme.figureFamily
                    font.features: ({ "tnum": 1 })
                }
                Rectangle {
                    objectName: "floatingRoleBarTrack"
                    Layout.alignment: Qt.AlignHCenter
                    Layout.preferredWidth: Math.min(24, Math.max(10, column.width))
                    Layout.preferredHeight: 36
                    radius: Theme.radiusXs
                    color: Qt.rgba(column.tone.r, column.tone.g, column.tone.b, 0.14)
                    clip: true
                    Rectangle {
                        objectName: "floatingRoleBarFill"
                        // Animate the normalized value; transient layout heights
                        // must not become a long-lived pixel-height animation.
                        property real fillRatio: Math.max(0, Math.min(1, column.modelData.count / root.maxCount))
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.bottom: parent.bottom
                        height: Math.min(parent.height, Math.max(2, Math.round(parent.height * fillRatio)))
                        radius: parent.radius
                        color: column.tone
                        Behavior on fillRatio { NumberAnimation { duration: Theme.motion ? 400 : 0; easing.type: Easing.OutCubic } }
                    }
                }
                RoleIcon {
                    Layout.alignment: Qt.AlignHCenter
                    role: column.modelData.group
                    size: 16
                }
                Text {
                    Layout.fillWidth: true
                    visible: root.ov.labels
                    text: column.groupName
                    horizontalAlignment: Text.AlignHCenter
                    wrapMode: Text.Wrap
                    color: Theme.textSecondary
                    font.pixelSize: 10
                }
                HoverHandler { id: columnHover }
                OverlayToolTip {
                    visible: columnHover.hovered
                    text: qsTr("%1 · 尝试 %2 次（%3）").arg(column.groupName).arg(column.modelData.count)
                          .arg(Runtime.fmt.percent(column.modelData.share, 1))
                }
            }
        }
    }

    Text {
        objectName: "floatingJobMessage"
        visible: root.columns.length === 0
        text: !root.ov.controller.statisticsConnected ? qsTr("后台服务未连接")
            : root.stats.loading ? qsTr("读取中…")
            : root.stats.loadError.length > 0 ? root.stats.loadError : qsTr("暂无统计")
        textFormat: Text.PlainText
        color: root.stats.loadError.length > 0 ? Theme.orangeText : Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
        Layout.fillWidth: true
    }
}
