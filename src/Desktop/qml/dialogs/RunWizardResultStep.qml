import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// Step 1 of the record wizard (EditRunDialog): 结果, whether it counts towards
// the achievement, and what saving will do to the progress.
//
// Only the view lives here; the state is the dialog's (`wizard`).
ColumnLayout {
    id: step

    required property var wizard

    spacing: 14

    GridLayout {
        Layout.fillWidth: true
        columns: width < 480 ? 2 : 3
        columnSpacing: 10
        rowSpacing: 10

        Repeater {
            model: step.wizard.resultOptions

            delegate: PickSurface {
                id: resultCard

                required property var modelData

                objectName: "resultPick_" + modelData.value
                Layout.fillWidth: true
                Layout.fillHeight: true
                Layout.preferredWidth: 1
                implicitHeight: resultCardColumn.implicitHeight + 26
                selected: step.wizard.resultCode === modelData.value
                accessibleName: modelData.label + "，" + modelData.desc
                onPicked: step.wizard.pickResult(modelData.value)

                ColumnLayout {
                    id: resultCardColumn

                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.leftMargin: 14
                    anchors.rightMargin: 14
                    anchors.topMargin: 14
                    spacing: 6

                    RowLayout {
                        spacing: 8
                        Rectangle {
                            implicitWidth: 10
                            implicitHeight: 10
                            radius: 5
                            color: Theme.token(Fmt.resultColorToken(resultCard.modelData.value))
                        }
                        Text {
                            text: resultCard.modelData.label
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(14)
                            font.bold: true
                        }
                    }

                    Text {
                        Layout.fillWidth: true
                        text: resultCard.modelData.desc
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }

                    // The raw result token is maintainer material.
                    Text {
                        visible: App.maintainerToolsVisible === true
                        text: resultCard.modelData.value
                        color: Theme.textMuted
                        font.family: Theme.monoFamily
                        font.pixelSize: 10
                    }
                }
            }
        }
    }

    Rectangle {
        Layout.fillWidth: true
        implicitHeight: goalRow.implicitHeight + 24
        radius: Theme.radiusS
        color: Theme.insetBackground
        border.width: 1
        border.color: Theme.eorzea ? Theme.insetBorder : Theme.border

        RowLayout {
            id: goalRow

            anchors.left: parent.left
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            anchors.leftMargin: 14
            anchors.rightMargin: 14
            spacing: 12

            ToggleSwitch {
                objectName: "goalToggle"
                checked: step.wizard.contributesToGoal
                enabled: step.wizard.resultCode === "COMPLETED"
                activeFocusOnTab: enabled
                Accessible.role: Accessible.CheckBox
                Accessible.name: qsTr("这是指导者任务，计入成就进度")
                Accessible.checkable: true
                Accessible.checked: checked
                Accessible.onToggleAction: toggled(!checked)
                Keys.onSpacePressed: toggled(!checked)
                Keys.onReturnPressed: toggled(!checked)
                onToggled: function(value) { step.wizard.contributesToGoal = value }
                Rectangle {
                    anchors.fill: parent
                    anchors.margins: -3
                    radius: Theme.radiusS
                    color: "transparent"
                    border.width: 2
                    border.color: Theme.eorzea ? Theme.gold2 : Theme.accent
                    visible: parent.activeFocus
                }
            }

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 2
                Text {
                    Layout.fillWidth: true
                    text: qsTr("这是指导者任务，计入成就进度")
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(13)
                    font.weight: Font.DemiBold
                    wrapMode: Text.WordWrap
                }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("非导随进入的副本请关闭")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    wrapMode: Text.WordWrap
                }
            }

            Text {
                objectName: "progressPreview"
                Layout.maximumWidth: 200
                text: step.wizard.progressTitle
                color: step.wizard.progressDelta > 0 ? Theme.green : Theme.headingColor
                font.pixelSize: Theme.fs(14)
                font.bold: true
                horizontalAlignment: Text.AlignRight
                wrapMode: Text.WordWrap
                Accessible.role: Accessible.StaticText
                Accessible.name: text
            }
        }
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 10

        Text {
            Layout.fillWidth: true
            text: step.wizard.progressExplanation
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        AppButton {
            objectName: "markCompletedButton"
            visible: step.wizard.resultCode !== "COMPLETED" || !step.wizard.contributesToGoal
            compact: true
            text: qsTr("已通关，计入进度")
            onClicked: step.wizard.markCompleted()
        }
    }
}
