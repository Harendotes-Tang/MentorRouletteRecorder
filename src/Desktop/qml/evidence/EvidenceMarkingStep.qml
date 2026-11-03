pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// ③ 排一把随机，按事件标记: which roulette, the five markers as a timeline (the
// next one highlighted, earlier ones ticked or 漏按), what happened this round,
// and 停止采证 once back outside.
EvidenceStep {
    id: step

    required property EvidenceSession session

    number: 3
    title: step.session.stepTitles[2]

    // `.ev-rt`
    Flow {
        Layout.fillWidth: true
        spacing: 12
        FieldLabel { text: qsTr("随机类型"); height: rouletteKind.height; verticalAlignment: Text.AlignVCenter }
        SegmentedControl {
            id: rouletteKind
            objectName: "evidenceRouletteKind"
            width: Math.max(naturalWidth, 200)
            options: [
                { value: "mentor", label: qsTr("指导者随机") },
                { value: "other", label: qsTr("其他随机") }
            ]
            currentValue: step.session.rouletteType
            onActivated: function(value) { step.session.rouletteType = value }
        }
        StyledTextField {
            objectName: "evidenceRouletteOther"
            visible: step.session.rouletteType === "other"
            width: 200
            placeholderText: qsTr("注明类型，例：练级随机")
            text: step.session.rouletteOther
            onTextEdited: step.session.rouletteOther = text
        }
        Text {
            height: rouletteKind.height
            verticalAlignment: Text.AlignVCenter
            textFormat: Text.PlainText
            text: step.session.rouletteType === "mentor" ? qsTr("优先采指导者随机") : qsTr("没有导随资格时，用其他随机作对照")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }

    // `.ev-marks`: five markers joined by a line that turns green behind the ticked ones.
    RowLayout {
        objectName: "evidenceMarkerTimeline"
        Layout.fillWidth: true
        spacing: 0
        Accessible.role: Accessible.Grouping
        Accessible.name: qsTr("事件标记")

        Repeater {
            model: step.session.markerDefs
            delegate: RowLayout {
                id: markerCell
                required property var modelData
                required property int index
                readonly property var mark: step.session.marks[index]
                readonly property bool done: !!mark && mark !== "skip"
                readonly property bool skipped: mark === "skip"
                readonly property bool next: step.session.recording && index === step.session.nextMarkIndex
                readonly property bool previousDone: index > 0 && !!step.session.marks[index - 1] && step.session.marks[index - 1] !== "skip"
                spacing: 0
                Layout.fillWidth: true

                // `.ev-ml`
                Rectangle {
                    visible: markerCell.index > 0
                    Layout.fillWidth: true
                    Layout.minimumWidth: 6
                    Layout.alignment: Qt.AlignTop
                    Layout.topMargin: 23
                    implicitHeight: 2
                    color: markerCell.done && markerCell.previousDone ? Theme.green : Theme.border
                    Behavior on color { ColorAnimation { duration: Theme.motionControl } }
                }

                // `.ev-mk`
                AppButton {
                    id: markerButton
                    objectName: "templateObservationMarker_" + markerCell.modelData.code
                    variant: "ghost"
                    implicitWidth: 104
                    implicitHeight: markerColumn.implicitHeight + 14
                    enabled: step.session.markerEnabled(markerCell.index)
                    opacity: 1
                    onClicked: step.session.mark(markerCell.index)
                    Accessible.name: markerCell.modelData.label + " · " + markerCell.modelData.trigger
                    Accessible.description: markerCell.done ? Runtime.fmt.localTime(markerCell.mark.at)
                                            : markerCell.skipped ? qsTr("漏按") : ""
                    background: Rectangle {
                        radius: Theme.radiusM
                        color: markerCell.next ? Theme.accentMuted : "transparent"
                        border.width: 1
                        border.color: markerCell.next ? Theme.accent : "transparent"
                        Behavior on color { ColorAnimation { duration: Theme.motionControl } }
                    }
                    contentItem: ColumnLayout {
                        id: markerColumn
                        spacing: 4
                        // `.ev-mk-c`
                        Rectangle {
                            Layout.alignment: Qt.AlignHCenter
                            implicitWidth: 32
                            implicitHeight: 32
                            radius: 16
                            color: markerCell.done ? Theme.green : markerCell.next ? Theme.accent : Theme.surface
                            border.width: 2
                            border.color: markerCell.done ? Theme.green : markerCell.next ? Theme.accent
                                          : markerCell.skipped ? Theme.orange
                                          : markerButton.hovered && markerButton.enabled ? Theme.accent : Theme.neutral300
                            Behavior on color { ColorAnimation { duration: Theme.motionControl } }
                            // the 4 px accent halo of the next marker
                            Rectangle {
                                anchors.centerIn: parent
                                width: parent.width + 8
                                height: width
                                radius: width / 2
                                z: -1
                                visible: markerCell.next
                                color: Theme.accentMuted
                            }
                            Text {
                                anchors.centerIn: parent
                                textFormat: Text.PlainText
                                text: markerCell.done ? "✓" : String(markerCell.index + 1)
                                color: markerCell.done || markerCell.next ? Theme.badgeForeground
                                       : markerCell.skipped ? Theme.orangeText : Theme.textSecondary
                                font.pixelSize: Theme.fs(13)
                                font.family: markerCell.done ? Theme.bodyFamily : Theme.figureFamily
                                font.weight: Font.Bold
                            }
                        }
                        Text {
                            Layout.alignment: Qt.AlignHCenter
                            textFormat: Text.PlainText
                            text: markerCell.modelData.label
                            color: markerCell.done || markerCell.next || markerCell.skipped ? Theme.textPrimary : Theme.textSecondary
                            font.pixelSize: Theme.fs(13)
                            font.weight: Font.Bold
                        }
                        Text {
                            Layout.alignment: Qt.AlignHCenter
                            Layout.maximumWidth: 96
                            textFormat: Text.PlainText
                            horizontalAlignment: Text.AlignHCenter
                            text: markerCell.done ? Runtime.fmt.localTime(markerCell.mark.at)
                                  : markerCell.skipped ? qsTr("漏按") : markerCell.modelData.trigger
                            color: markerCell.skipped ? Theme.orangeText : markerCell.done ? Theme.textPrimary : Theme.textSecondary
                            font.pixelSize: Theme.fs(11)
                            font.family: markerCell.done ? Theme.figureFamily : Theme.bodyFamily
                            font.weight: markerCell.skipped ? Font.DemiBold : Font.Normal
                            wrapMode: Text.Wrap
                        }
                    }
                }
            }
        }
    }

    EvidenceHint {
        iconName: "activity"
        text: qsTr("事件发生后 3 秒内点。漏按的会自动记为「漏按」，不要事后补点。")
    }

    // The Collector's acknowledgement of the last marker.
    Text {
        Layout.fillWidth: true
        visible: Runtime.app.validationFeedback.length > 0
        textFormat: Text.PlainText
        text: Runtime.app.validationFeedback
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }
    // A refused marker or stop, said where it was pressed (review I4-1).
    Text {
        objectName: "evidenceMarkingError"
        Layout.fillWidth: true
        visible: Runtime.app.validationError.length > 0
        textFormat: Text.PlainText
        text: qsTr("采证操作失败：") + Runtime.app.validationError
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
        wrapMode: Text.Wrap
    }

    // `.ev-cond`
    Flow {
        objectName: "evidenceRoundChips"
        Layout.fillWidth: true
        spacing: 8
        FieldLabel { text: qsTr("这一轮的情况"); height: 22; verticalAlignment: Text.AlignVCenter }
        Repeater {
            model: step.session.chipDefs
            delegate: Chip {
                required property var modelData
                objectName: "evidenceRoundChip_" + modelData.key
                text: modelData.label
                checked: !!step.session.chips[modelData.key]
                onToggled: function(checked) { step.session.toggleChip(modelData.key, checked) }
            }
        }
        Text {
            height: 22
            verticalAlignment: Text.AlignVCenter
            textFormat: Text.PlainText
            text: qsTr("如实勾选，不要事后补点")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }

    // `.ev-acts`
    RowLayout {
        Layout.fillWidth: true
        spacing: 12
        AppButton {
            objectName: step.session.recording ? "templateObservationActionButton" : "evidenceStopButton"
            text: Runtime.app.captureCommandBusy ? qsTr("处理中…") : qsTr("停止采证")
            enabled: step.session.active && Runtime.app.templateObservationActionEnabled && !Runtime.app.captureCommandBusy
            onClicked: step.session.stop()
        }
        EvidenceHint { text: qsTr("离本回到外面再停止") }
    }
}
