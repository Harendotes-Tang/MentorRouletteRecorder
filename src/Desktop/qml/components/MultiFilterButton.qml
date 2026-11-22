pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// A filter capsule with a multi-select panel. Every option carries the number of entries it
// would leave with the other filters applied, so an empty choice is visible before it is made.
HistoryFilterButton {
    id: control

    property string title: ""
    /// [{ value, label, sub, count }]
    property var options: []
    property var selection: []
    property bool searchable: false
    property string searchPlaceholder: ""
    property string emptyText: ""
    signal selectionEdited(var selection)

    readonly property int shownLimit: 12
    readonly property var chosenLabels: options.filter(option => selection.includes(option.value))
                                               .map(option => option.label)
    function summary(labels) {
        const separator = qsTr("、")
        return labels.length <= 2 ? labels.join(separator)
                                  : labels.slice(0, 2).join(separator) + " +" + (labels.length - 2)
    }
    function toggle(value) {
        selectionEdited(selection.includes(value) ? selection.filter(entry => entry !== value)
                                                  : selection.concat([value]))
    }

    selected: selection.length > 0
    text: selected ? qsTr("%1：%2").arg(title).arg(summary(chosenLabels)) : title
    menuWidth: 288
    Accessible.name: text

    menuContent: ColumnLayout {
        spacing: 2

        RowLayout {
            Layout.fillWidth: true
            Layout.bottomMargin: 4
            spacing: 8
            Text {
                renderType: Theme.bodyRenderType
                text: control.title
                color: Theme.textPrimary
                font.bold: true
                font.pixelSize: Theme.fs(13)
            }
            Text {
                renderType: Theme.bodyRenderType
                Layout.fillWidth: true
                text: control.selected ? qsTr("已选 %1 项").arg(control.selection.length) : qsTr("可多选")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }
            AppButton {
                compact: true
                variant: "ghost"
                text: qsTr("清空")
                enabled: control.selected
                onClicked: control.selectionEdited([])
            }
        }

        StyledTextField {
            id: finder
            visible: control.searchable
            Layout.fillWidth: true
            Layout.bottomMargin: 4
            placeholderText: control.searchPlaceholder
            Accessible.name: control.searchPlaceholder
        }

        Repeater {
            id: choices
            model: {
                const term = control.searchable ? finder.text.trim().toLowerCase() : ""
                return control.options.filter(option => !term || option.label.toLowerCase().includes(term))
                                      .slice(0, control.shownLimit)
            }

            delegate: RowLayout {
                id: choice
                required property var modelData
                readonly property bool chosen: control.selection.includes(modelData.value)
                Layout.fillWidth: true
                spacing: 8

                SelectionCheckBox {
                    Layout.fillWidth: true
                    implicitHeight: 30
                    focusPolicy: Qt.StrongFocus
                    text: choice.modelData.sub ? choice.modelData.label + " · " + choice.modelData.sub
                                               : choice.modelData.label
                    checked: choice.chosen
                    onClicked: control.toggle(choice.modelData.value)
                }
                Text {
                    renderType: Theme.bodyRenderType
                    text: String(choice.modelData.count)
                    color: choice.modelData.count > 0 || choice.chosen ? Theme.textSecondary : Theme.textMuted
                    font.pixelSize: Theme.fs(12)
                }
            }
        }

        Text {
            renderType: Theme.bodyRenderType
            visible: choices.count === 0
            Layout.fillWidth: true
            Layout.topMargin: 4
            wrapMode: Text.Wrap
            text: control.emptyText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }
}
