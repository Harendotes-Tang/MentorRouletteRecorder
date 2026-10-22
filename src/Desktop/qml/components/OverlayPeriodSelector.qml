pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// All period statistics share this range; achievement and live modules stay cumulative/live.
Rectangle {
    id: root

    required property var controller
    objectName: "floatingAttemptPeriods"
    implicitHeight: optionRow.implicitHeight + 4
    radius: Theme.radiusS
    color: Theme.fill
    Accessible.role: Accessible.Grouping
    Accessible.name: qsTr("统计时间范围")

    function selectNeighbor(index, direction) {
        const next = (index + direction + options.count) % options.count
        const button = options.itemAt(next)
        if (!button) return
        controller.attemptPeriod = options.model[next].key
        button.forceActiveFocus(Qt.TabFocusReason)
    }

    RowLayout {
        id: optionRow
        anchors.fill: parent
        anchors.margins: 2
        spacing: 2

        Repeater {
            id: options
            model: root.controller.attemptPeriodOptions

            delegate: Button {
                id: option
                required property var modelData
                required property int index
                objectName: "floatingAttemptPeriod_" + modelData.key
                Layout.fillWidth: true
                Layout.fillHeight: true
                Layout.preferredWidth: 1
                Layout.minimumWidth: 0
                implicitHeight: Math.max(28, label.implicitHeight + 10)
                leftPadding: 3
                rightPadding: 3
                topPadding: 5
                bottomPadding: 5
                text: modelData.title
                checkable: true
                autoExclusive: true
                checked: root.controller.attemptPeriod === modelData.key
                focusPolicy: Qt.TabFocus
                hoverEnabled: true
                Accessible.role: Accessible.RadioButton
                Accessible.name: text
                Keys.onLeftPressed: function(event) { root.selectNeighbor(index, -1); event.accepted = true }
                Keys.onRightPressed: function(event) { root.selectNeighbor(index, 1); event.accepted = true }
                onClicked: root.controller.attemptPeriod = modelData.key

                background: Rectangle {
                    radius: Theme.radiusXs
                    color: option.checked ? (Theme.dark ? Theme.fillStrong : Theme.surface)
                                          : (option.hovered ? Theme.fillStrong : Theme.clear(Theme.fillStrong))
                    border.width: option.activeFocus ? 2 : (option.checked ? 1 : 0)
                    border.color: option.activeFocus ? (Theme.eorzea ? Theme.gold2 : Theme.accent) : Theme.border
                }
                contentItem: Text {
                    id: label
                    objectName: option.objectName + "Label"
                    text: option.text
                    textFormat: Text.PlainText
                    wrapMode: Text.WordWrap
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment: Text.AlignVCenter
                    color: option.checked ? (Theme.eorzea ? Theme.gold2 : Theme.accent) : Theme.textSecondary
                    font.family: Theme.bodyFamily
                    font.pixelSize: Theme.fs(11)
                    font.weight: option.checked ? Font.DemiBold : Font.Normal
                }
            }
        }
    }
}
