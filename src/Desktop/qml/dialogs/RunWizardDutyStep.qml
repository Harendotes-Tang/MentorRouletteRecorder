import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// Step 2 of the record wizard (EditRunDialog): 副本 and 职业.
//
// Only the view lives here; the option lists, the filters and the selection
// belong to the dialog (`wizard`), so every step reads one set of state.
ColumnLayout {
    id: step

    required property var wizard

    spacing: 16

    /// Scrolls the duty list to the chosen row, or to the top when the current
    /// filters hide it.
    function showSelection() {
        const rows = step.wizard.visibleDuties
        for (let i = 0; i < rows.length; ++i) {
            if (rows[i].option_index === step.wizard.dutyIndex) {
                dutyList.positionViewAtIndex(i, ListView.Contain)
                return
            }
        }
        dutyList.positionViewAtBeginning()
    }

    // ------------------------------------------------------------ 副本 --
    ColumnLayout {
        Layout.fillWidth: true
        spacing: 10

        RowLayout {
            Layout.fillWidth: true
            spacing: 10

            CardKicker {
                Layout.fillWidth: false
                text: qsTr("副本")
            }

            Tag {
                objectName: "selectedDutyTag"
                visible: step.wizard.dutyIndex > 0
                variant: "ink"
                text: step.wizard.dutyIndex > 0 ? step.wizard.selectedDutySummary() : ""
            }

            AppButton {
                objectName: "changeDutyButton"
                visible: step.wizard.dutyIndex > 0
                variant: "ghost"
                compact: true
                text: qsTr("更换")
                onClicked: {
                    step.wizard.pickDuty(0)
                    searchField.forceActiveFocus()
                }
            }

            Text {
                visible: step.wizard.dutyIndex === 0
                text: qsTr("未选择 · 可留作")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            AppButton {
                objectName: "unknownDutyButton"
                visible: step.wizard.dutyIndex === 0
                variant: "ghost"
                compact: true
                text: qsTr("未知副本")
                onClicked: step.wizard.pickDuty(0)
            }

            Item { Layout.fillWidth: true }
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            SegmentedControl {
                objectName: "partyFilter"
                Layout.preferredWidth: 300
                Layout.minimumWidth: 260
                options: step.wizard.partyOptions
                currentValue: step.wizard.partyFilter
                onActivated: function(value) { step.wizard.partyFilter = value }
            }

            StyledTextField {
                id: searchField

                objectName: "dutySearchField"
                Layout.fillWidth: true
                text: step.wizard.dutyQuery
                placeholderText: qsTr("搜索副本名 / 等级 / 版本，如 “灯塔” “90” “7.2”")
                Accessible.name: qsTr("搜索副本")
                onTextChanged: step.wizard.dutyQuery = text
            }
        }

        Flow {
            Layout.fillWidth: true
            spacing: 6

            Repeater {
                model: step.wizard.levelOptions

                delegate: PickChip {
                    required property var modelData

                    objectName: "levelChip_" + (modelData.value || "all")
                    text: modelData.label
                    checked: step.wizard.levelFilter === modelData.value
                    onPicked: step.wizard.levelFilter = modelData.value
                }
            }

            Item {
                visible: step.wizard.showDifficultyFilter
                width: 9
                height: Theme.eorzea ? 26 : 22

                Rectangle {
                    anchors.centerIn: parent
                    width: 1
                    height: 16
                    color: Theme.border
                }
            }

            Repeater {
                model: step.wizard.showDifficultyFilter ? step.wizard.difficultyOptions : []

                delegate: PickChip {
                    required property var modelData

                    objectName: "difficultyChip_" + (modelData.value || "all")
                    text: modelData.label
                    checked: step.wizard.difficultyFilter === modelData.value
                    onPicked: step.wizard.difficultyFilter = modelData.value
                }
            }
        }

        Flow {
            objectName: "recentDuties"
            Layout.fillWidth: true
            visible: step.wizard.dutyQuery.trim().length === 0
                     && step.wizard.recentDutyOptions.length > 0
            spacing: 6

            Text {
                height: Theme.eorzea ? 26 : 22
                verticalAlignment: Text.AlignVCenter
                rightPadding: 2
                text: qsTr("最近打过")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            Repeater {
                model: step.wizard.recentDutyOptions

                delegate: PickChip {
                    required property var modelData

                    objectName: "recentDuty_" + modelData.content_id
                    text: modelData.duty_name || ""
                    checked: step.wizard.dutyIndex === modelData.option_index
                    onPicked: step.wizard.pickDuty(modelData.option_index)
                }
            }
        }

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: dutyList.count === 0 ? 58 : Math.min(210, dutyList.contentHeight + 2)
            radius: Theme.radiusS
            color: Theme.insetBackground
            border.width: 1
            border.color: Theme.eorzea ? Theme.insetBorder : Theme.border
            clip: true

            ListView {
                id: dutyList

                /// Keeps the rows clear of the scroll bar.
                readonly property real gutter: contentHeight > height + 0.5 ? 10 : 0

                objectName: "dutyList"
                anchors.fill: parent
                anchors.margins: 1
                clip: true
                boundsBehavior: Flickable.StopAtBounds
                model: step.wizard.visibleDuties
                ScrollBar.vertical: ScrollBar {
                    policy: dutyList.contentHeight > dutyList.height + 0.5
                            ? ScrollBar.AlwaysOn : ScrollBar.AlwaysOff
                }

                delegate: Rectangle {
                    id: dutyRow

                    required property var modelData
                    required property int index
                    readonly property bool chosen: modelData.option_index === step.wizard.dutyIndex
                    readonly property bool hasLevel: typeof modelData.duty_level === "number"
                                                     && modelData.duty_level > 0

                    objectName: "dutyRow_" + (modelData.preservesRunDuty ? "run" : modelData.content_id)
                    width: dutyList.width
                    height: 36
                    color: chosen ? Theme.accentMuted
                                  : (rowHover.hovered ? Theme.fill : Theme.clear(Theme.fill))

                    Accessible.role: Accessible.RadioButton
                    Accessible.name: modelData.duty_name || ""
                    Accessible.checkable: true
                    Accessible.checked: chosen
                    Accessible.onPressAction: step.wizard.pickDuty(modelData.option_index)

                    Rectangle {
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.bottom: parent.bottom
                        height: 1
                        color: Theme.border
                    }

                    Rectangle {
                        visible: dutyRow.chosen
                        width: 2
                        height: parent.height
                        color: Theme.eorzea ? Theme.gold : Theme.accent
                    }

                    RowLayout {
                        anchors.fill: parent
                        anchors.leftMargin: 12
                        anchors.rightMargin: 12 + dutyList.gutter
                        spacing: 12

                        Rectangle {
                            implicitWidth: 20
                            implicitHeight: 20
                            radius: Theme.radiusXs
                            color: Theme.insetBackgroundStrong
                            border.width: 1
                            border.color: Theme.eorzea ? Theme.insetBorder : Theme.border

                            Text {
                                anchors.centerIn: parent
                                text: step.wizard.partyIcon(dutyRow.modelData.party_size)
                                color: Theme.eorzea ? Theme.gold2 : Theme.accent
                                font.family: Theme.figureFamily
                                font.pixelSize: 10
                                font.bold: true
                            }
                        }

                        Text {
                            Layout.fillWidth: true
                            text: dutyRow.modelData.duty_name || ""
                            color: dutyRow.chosen
                                   ? (Theme.eorzea ? Theme.gold2 : Theme.accent)
                                   : Theme.textPrimary
                            font.pixelSize: Theme.fs(13)
                            font.weight: Font.DemiBold
                            elide: Text.ElideRight
                        }

                        Tag {
                            visible: !!dutyRow.modelData.difficulty
                                     && dutyRow.modelData.difficulty !== "普通"
                            variant: "accent"
                            text: dutyRow.modelData.difficulty || ""
                        }

                        Text {
                            visible: dutyRow.hasLevel
                            text: dutyRow.hasLevel ? qsTr("%1级").arg(dutyRow.modelData.duty_level) : ""
                            color: Theme.textSecondary
                            font.family: Theme.figureFamily
                            font.weight: Theme.figureWeight(false)
                            font.pixelSize: Theme.fs(12)
                        }

                        Text {
                            Layout.preferredWidth: 28
                            horizontalAlignment: Text.AlignRight
                            text: dutyRow.modelData.version || ""
                            color: Theme.textMuted
                            font.family: Theme.figureFamily
                            font.pixelSize: Theme.fs(11)
                        }
                    }

                    HoverHandler {
                        id: rowHover
                        cursorShape: Qt.PointingHandCursor
                    }

                    TapHandler {
                        onTapped: step.wizard.pickDuty(dutyRow.modelData.option_index)
                    }
                }
            }

            Text {
                objectName: "dutyListEmpty"
                anchors.centerIn: parent
                visible: dutyList.count === 0
                text: qsTr("没有匹配的副本，试试放宽等级或类型")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }
        }

        Text {
            objectName: "dutyCount"
            text: qsTr("共 %1 个副本").arg(step.wizard.visibleDuties.length)
            color: Theme.textMuted
            font.pixelSize: Theme.fs(11)
        }
    }

    // ------------------------------------------------------------ 职业 --
    ColumnLayout {
        Layout.fillWidth: true
        spacing: 10

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            Layout.bottomMargin: 4
            color: Theme.border
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 10

            CardKicker {
                Layout.fillWidth: false
                text: qsTr("职业")
            }

            PickChip {
                objectName: "jobUnknownChip"
                text: qsTr("未知")
                checked: step.wizard.jobIndex === 0
                onPicked: step.wizard.pickJob(0)
            }

            Item { Layout.fillWidth: true }
        }

        Repeater {
            model: step.wizard.jobGroups

            delegate: RowLayout {
                id: jobGroupRow

                required property var modelData
                readonly property color roleColor: Theme.token(modelData.token)

                Layout.fillWidth: true
                spacing: 14

                Text {
                    Layout.preferredWidth: 56
                    Layout.alignment: Qt.AlignTop
                    Layout.topMargin: 7
                    text: jobGroupRow.modelData.role
                    color: jobGroupRow.roleColor
                    font.pixelSize: Theme.fs(11)
                    font.weight: Font.DemiBold
                }

                Flow {
                    Layout.fillWidth: true
                    spacing: 6

                    Repeater {
                        model: jobGroupRow.modelData.jobs

                        delegate: PickSurface {
                            id: jobChip

                            required property var modelData
                            readonly property bool hasIcon: typeof Jobs !== "undefined"
                                                            && Jobs.framedIcon(modelData.job_id) !== ""

                            objectName: "jobPick_" + modelData.job_id
                            width: jobChipRow.implicitWidth + 14
                            height: 30
                            selected: step.wizard.jobIndex === modelData.option_index
                            accessibleName: modelData.job_name
                            onPicked: step.wizard.pickJob(modelData.option_index)

                            Row {
                                id: jobChipRow

                                x: 4
                                anchors.verticalCenter: parent.verticalCenter
                                spacing: 7

                                Item {
                                    width: 22
                                    height: 22

                                    JobIcon {
                                        visible: jobChip.hasIcon
                                        jobId: jobChip.modelData.job_id
                                        size: 22
                                    }

                                    Rectangle {
                                        visible: !jobChip.hasIcon
                                        anchors.fill: parent
                                        radius: Theme.radiusXs
                                        color: jobGroupRow.roleColor

                                        Text {
                                            anchors.centerIn: parent
                                            text: jobChip.modelData.abbreviation
                                            color: Theme.badgeForeground
                                            font.pixelSize: 9
                                            font.weight: Font.Black
                                        }
                                    }
                                }

                                Text {
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: jobChip.modelData.job_name
                                    color: jobChip.selected
                                           ? (Theme.eorzea ? Theme.gold2 : Theme.accent)
                                           : Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.weight: jobChip.selected ? Font.DemiBold : Font.Normal
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
