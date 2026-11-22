pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// 职业 of a macro in the 副本提醒与导随宏 editor (Design artifact, 新建宏 · 职业): the field opens a
// panel of the battle jobs by 职能, any number chosen, searchable by name or abbreviation. A
// whole 职能 is chosen by its heading and kept as the group (store.normalizeJobs), so jobs the
// game adds later count too. None chosen means every job.
StyledComboBox {
    id: control

    required property var store
    /// Chosen codes: 职能 (TANK, ...) and job abbreviations (PLD, ...).
    property var selection: []
    signal selectionEdited(var selection)

    // Each job's code, with its group's when the whole group is chosen.
    function chosen(job) {
        const group = store.jobGroups.find(entry => entry.jobs.some(row => row.abbreviation === job.abbreviation))
        return selection.includes(job.abbreviation) || (!!group && selection.includes(group.code))
    }
    function toggleJob(job, group) {
        let codes
        if (selection.includes(group.code))
            codes = selection.filter(code => code !== group.code)
                .concat(group.jobs.map(row => row.abbreviation).filter(code => code !== job.abbreviation))
        else
            codes = selection.includes(job.abbreviation) ? selection.filter(code => code !== job.abbreviation)
                                                         : selection.concat([job.abbreviation])
        selectionEdited(store.normalizeJobs(codes))
    }
    function groupState(group) {
        const count = group.jobs.filter(job => chosen(job)).length
        return count === 0 ? "none" : count === group.jobs.length ? "all" : "some"
    }
    function toggleGroup(group) {
        const own = group.jobs.map(job => job.abbreviation)
        const rest = selection.filter(code => code !== group.code && !own.includes(code))
        selectionEdited(store.normalizeJobs(groupState(group) === "all" ? rest : rest.concat([group.code])))
    }
    function toggle(code) {
        const group = store.jobGroups.find(entry => entry.code === code)
        if (group)
            return toggleGroup(group)
        for (const entry of store.jobGroups) {
            const job = entry.jobs.find(row => row.abbreviation === code)
            if (job)
                return toggleJob(job, entry)
        }
    }
    readonly property int chosenCount: store.jobGroups.reduce(
        (total, group) => total + group.jobs.filter(job => chosen(job)).length, 0)

    model: []
    keepsRowAcrossLanguages: false
    displayText: store.jobsSummary(selection, 2)
    Accessible.name: qsTr("职业：%1").arg(displayText)

    // A tick box drawn as the panel's: empty, a dash for part of a 职能, or a tick.
    component Tick: Rectangle {
        property string mark: "none"
        implicitWidth: 16
        implicitHeight: 16
        radius: 4
        color: mark === "none" ? Theme.insetBackground : (Theme.eorzea ? Theme.gold : Theme.accent)
        border.width: mark === "none" ? 1 : 0
        border.color: Theme.textSecondary
        Image {
            anchors.centerIn: parent
            width: 12
            height: 12
            visible: parent.mark !== "none"
            source: Lucide.source(parent.mark === "all" ? "check" : "minus", Theme.surface)
            sourceSize: Qt.size(24, 24)
        }
    }

    popup: Popup {
        id: panel
        x: control.width - width
        y: control.height + 4
        width: 560
        padding: 12
        onAboutToShow: search.text = ""
        onOpened: search.forceActiveFocus()
        background: Rectangle {
            radius: Theme.radiusS
            color: Theme.surface
            border.width: 1
            border.color: Theme.eorzea ? Theme.gold3 : Theme.border
        }

        contentItem: ColumnLayout {
            spacing: 8

            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                Text {
                    renderType: Theme.bodyRenderType
                    text: qsTr("职业")
                    color: Theme.textPrimary
                    font.bold: true
                    font.pixelSize: Theme.fs(13)
                }
                Text {
                    renderType: Theme.bodyRenderType
                    Layout.fillWidth: true
                    text: control.chosenCount > 0 ? qsTr("已选 %1 项").arg(control.chosenCount) : qsTr("按职能分组")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                }
                AppButton {
                    compact: true
                    variant: "ghost"
                    text: qsTr("清空")
                    enabled: control.selection.length > 0
                    onClicked: control.selectionEdited([])
                }
            }

            StyledTextField {
                id: search
                objectName: "dutyCommunityJobSearch"
                Layout.fillWidth: true
                placeholderText: qsTr("搜索职业或缩写")
                Accessible.name: placeholderText
            }

            ScrollView {
                id: listView
                Layout.fillWidth: true
                Layout.preferredHeight: Math.min(groupsColumn.implicitHeight, 300)
                contentWidth: availableWidth
                clip: true

                ColumnLayout {
                    id: groupsColumn
                    width: listView.availableWidth
                    spacing: 6

                    Repeater {
                        id: groupRepeater
                        // Search by job name, abbreviation or 职能.
                        model: {
                            const term = search.text.trim().toLowerCase()
                            return control.store.jobGroups.map(group => ({
                                group: group,
                                jobs: group.jobs.filter(job => !term || group.label.toLowerCase().includes(term)
                                    || [Theme.jobName(job), job.job_name, job.abbreviation]
                                        .some(value => String(value).toLowerCase().includes(term)))
                            })).filter(entry => entry.jobs.length > 0)
                        }

                        delegate: ColumnLayout {
                            id: section
                            required property var modelData
                            readonly property var group: modelData.group
                            Layout.fillWidth: true
                            spacing: 2

                            Button {
                                id: groupHead
                                objectName: "dutyCommunityJobGroup_" + section.group.code
                                Layout.fillWidth: true
                                padding: 4
                                focusPolicy: Qt.StrongFocus
                                Accessible.name: qsTr("全选「%1」").arg(section.group.label)
                                onClicked: control.toggleGroup(section.group)
                                background: Rectangle {
                                    radius: Theme.radiusXs
                                    color: groupHead.hovered || groupHead.activeFocus ? Theme.fill : "transparent"
                                }
                                contentItem: Row {
                                    spacing: 8
                                    Tick {
                                        anchors.verticalCenter: parent.verticalCenter
                                        mark: control.groupState(section.group)
                                    }
                                    Text {
                                        renderType: Theme.bodyRenderType
                                        anchors.verticalCenter: parent.verticalCenter
                                        text: section.group.label
                                        color: Theme.token(section.group.token)
                                        font.pixelSize: Theme.fs(12)
                                        font.weight: Font.DemiBold
                                    }
                                }
                            }
                            GridLayout {
                                Layout.fillWidth: true
                                Layout.leftMargin: 20
                                columns: 3
                                columnSpacing: 4
                                rowSpacing: 2
                                Repeater {
                                    model: section.modelData.jobs
                                    delegate: Button {
                                        id: option
                                        required property var modelData
                                        objectName: "dutyCommunityJob_" + modelData.abbreviation
                                        Layout.fillWidth: true
                                        Layout.preferredWidth: 1
                                        padding: 4
                                        focusPolicy: Qt.StrongFocus
                                        Accessible.role: Accessible.CheckBox
                                        Accessible.name: Theme.jobName(modelData)
                                        Accessible.checked: control.chosen(modelData)
                                        onClicked: control.toggleJob(modelData, section.group)
                                        background: Rectangle {
                                            radius: Theme.radiusXs
                                            color: option.hovered || option.activeFocus ? Theme.fill : "transparent"
                                        }
                                        contentItem: Row {
                                            spacing: 6
                                            Tick {
                                                anchors.verticalCenter: parent.verticalCenter
                                                mark: control.chosen(option.modelData) ? "all" : "none"
                                            }
                                            Rectangle {
                                                anchors.verticalCenter: parent.verticalCenter
                                                width: badge.implicitWidth + 8
                                                height: 16
                                                radius: 3
                                                color: Theme.token(section.group.token)
                                                Text {
                                                    id: badge
                                                    anchors.centerIn: parent
                                                    text: option.modelData.abbreviation
                                                    color: Theme.badgeForeground
                                                    font.pixelSize: 9
                                                    font.weight: Font.Black
                                                }
                                            }
                                            Text {
                                                renderType: Theme.bodyRenderType
                                                anchors.verticalCenter: parent.verticalCenter
                                                text: Theme.jobName(option.modelData)
                                                textFormat: Text.PlainText
                                                color: Theme.textPrimary
                                                font.pixelSize: Theme.fs(12)
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    Text {
                        renderType: Theme.bodyRenderType
                        visible: groupRepeater.count === 0
                        Layout.fillWidth: true
                        Layout.margins: 6
                        text: qsTr("没有匹配的职业")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                    }
                }
            }

            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                Text {
                    renderType: Theme.bodyRenderType
                    Layout.fillWidth: true
                    wrapMode: Text.Wrap
                    text: qsTr("选整个职能时，以后新增的职业也算在内")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                }
                AppButton {
                    objectName: "dutyCommunityJobDone"
                    compact: true
                    variant: "ghost"
                    text: qsTr("完成")
                    onClicked: panel.close()
                }
            }
        }
    }
}
