pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

ColumnLayout {
    id: root
    objectName: "floatingDutyNotes"

    required property var community
    required property var duty
    required property string phaseKey
    required property string automaticStage
    signal phaseSelected(string key)
    signal manageRequested()

    readonly property var groups: community && duty ? community.overlayGroups(duty) : []
    readonly property var phases: groups.filter(group => !alwaysShown(group))
    readonly property int phaseIndex: Math.max(0, phases.findIndex(group => group.key === phaseKey))
    readonly property var livePhase: community
        && ["trash1", "boss1", "trash2", "boss2", "trash3", "boss3"].includes(automaticStage)
        ? community.overlayPhase(automaticStage) : null
    readonly property bool automatic: automaticStage === "unknown" || !!livePhase
    readonly property var currentPhase: automatic ? livePhase : (phases[phaseIndex] || null)
    readonly property string phaseTitle: {
        if (!currentPhase) return qsTr("当前阶段待识别")
        const name = bossName()
        return name ? qsTr("%1 · %2").arg(currentPhase.label).arg(name) : currentPhase.label
    }

    spacing: 6

    function bossName() {
        if (!duty || (currentPhase.code !== "boss" && currentPhase.code !== "last")) return ""
        const number = currentPhase.code === "last" ? 3 : currentPhase.n
        // Duty catalog IDs and encounter order, cross-checked against the two recorded runs.
        switch (Number(duty.content_id)) {
        case 32:
            return [qsTr("独爪妖禽"), qsTr("魔导炮艇"), qsTr("尘世幻龙")][number - 1] || ""
        case 676:
            return [qsTr("得到宽恕的失调"), qsTr("得到宽恕的泰丝琳"), qsTr("斐利亚")][number - 1] || ""
        }
        return ""
    }

    function alwaysShown(group) {
        return ["all", "none", "raw", "general"].includes(group.code)
    }
    function belongsToCurrentPhase(group) {
        if (!currentPhase) return false
        if (group.key === currentPhase.key) return true
        if (!automatic) return false
        if (automaticStage === "trash1" && group.code === "open") return true
        if (automaticStage === "boss3" && group.code === "last") return true
        return currentPhase.code === "trash" && group.key === "p:trash"
    }
    function selectStep(step) {
        const next = phases[phaseIndex + step]
        if (next) phaseSelected(next.key)
    }

    component NotesButton: AppButton {
        compact: true
        visualScale: 1
        opacity: 1
    }
    component PhaseButton: NotesButton {
        variant: "ghost"
        implicitWidth: 26
        focusPolicy: Qt.NoFocus
        Accessible.name: tip
    }

    Text {
        Layout.fillWidth: true
        text: qsTr("提醒与宏")
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(12)
        font.weight: Font.DemiBold
    }

    RowLayout {
        Layout.fillWidth: true
        visible: !!root.currentPhase || (root.automatic && root.groups.length > 0)
        spacing: 4

        PhaseButton {
            objectName: "floatingDutyNotesPrevious"
            visible: !root.automatic
            iconName: "chevron-left"
            tip: qsTr("上一段")
            enabled: root.phaseIndex > 0
            onClicked: root.selectStep(-1)
        }
        Text {
            objectName: "floatingDutyNotesPhase"
            Layout.fillWidth: true
            horizontalAlignment: Text.AlignHCenter
            text: root.phaseTitle
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(12)
            font.weight: Font.DemiBold
        }
        PhaseButton {
            objectName: "floatingDutyNotesNext"
            visible: !root.automatic
            iconName: "chevron-right"
            tip: qsTr("下一段")
            enabled: root.phaseIndex + 1 < root.phases.length
            onClicked: root.selectStep(1)
        }
    }

    Repeater {
        model: root.groups
        delegate: ColumnLayout {
            id: section
            required property var modelData
            readonly property bool pinned: root.alwaysShown(modelData)
            readonly property bool open: pinned || root.belongsToCurrentPhase(modelData)

            objectName: "floatingDutyNotesGroup_" + modelData.key
            visible: open
            Layout.fillWidth: true
            spacing: 4

            Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
            Text {
                visible: section.pinned
                Layout.fillWidth: true
                text: section.modelData.label
                textFormat: Text.PlainText
                elide: Text.ElideRight
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                font.weight: Font.DemiBold
            }
            Repeater {
                model: section.open ? section.modelData.items : []
                delegate: ColumnLayout {
                    id: entry
                    required property var modelData
                    required property int index
                    readonly property var row: modelData.row
                    readonly property var note: row.content
                    readonly property bool macro: note.type === "macro"
                    property bool expanded: false

                    Layout.fillWidth: true
                    spacing: 2

                    Rectangle {
                        visible: entry.index > 0
                        Layout.fillWidth: true
                        Layout.topMargin: 3
                        implicitHeight: 1
                        color: Theme.border
                    }
                    RowLayout {
                        Layout.fillWidth: true
                        visible: entry.macro
                        spacing: 4

                        Text {
                            Layout.fillWidth: true
                            text: entry.note.title
                            textFormat: Text.PlainText
                            wrapMode: Text.Wrap
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(11)
                            font.weight: Font.DemiBold
                        }
                        NotesButton {
                            objectName: "floatingDutyNotesCopy_" + entry.note.id
                            text: root.community.copiedKey === entry.note.id + "macro" ? qsTr("已复制") : qsTr("复制")
                            onClicked: root.community.copyLocalMacro(entry.note.id)
                        }
                    }
                    Text {
                        id: body
                        objectName: "floatingDutyNotesBody_" + entry.note.id
                        Layout.fillWidth: true
                        text: entry.note.body
                        textFormat: Text.PlainText
                        wrapMode: Text.Wrap
                        maximumLineCount: entry.expanded ? 2147483647 : 3
                        elide: entry.expanded ? Text.ElideNone : Text.ElideRight
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(11)
                    }
                    NotesButton {
                        objectName: "floatingDutyNotesExpand_" + entry.note.id
                        visible: entry.expanded || body.truncated
                        variant: "ghost"
                        text: entry.expanded ? qsTr("收起") : qsTr("展开全文")
                        onClicked: entry.expanded = !entry.expanded
                    }
                }
            }
        }
    }

    Text {
        objectName: "floatingDutyNotesStageEmpty"
        visible: root.automatic && !!root.currentPhase && root.groups.length > 0
            && !root.phases.some(group => root.belongsToCurrentPhase(group))
        Layout.fillWidth: true
        text: qsTr("当前阶段暂无提醒或宏。")
        wrapMode: Text.WordWrap
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
    }

    Text {
        objectName: "floatingDutyNotesEmpty"
        visible: root.groups.length === 0
        Layout.fillWidth: true
        text: qsTr("当前副本暂无提醒或宏。")
        wrapMode: Text.WordWrap
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
    }
    NotesButton {
        objectName: "floatingDutyNotesManage"
        visible: root.groups.length === 0
        text: qsTr("打开提醒与宏")
        onClicked: root.manageRequested()
    }
}
