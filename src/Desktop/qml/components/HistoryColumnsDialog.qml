pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0

// 自定义列 (docs/ui-design.md §4.2): one check box per column, in the table's
// order, hidden columns included. The last shown column cannot be unchecked,
// so the table never loses every column. 恢复默认 puts the shown columns, their
// order and their widths back; 完成 closes. Order and widths themselves are
// dragged on the header.
Popup {
    id: dialog

    /// [{ key, label }] in the current order (HistoryPage.allColumns).
    required property var columns
    /// App.historyColumns.
    required property var controller
    readonly property var shown: controller.visible

    parent: Overlay.overlay
    modal: true
    focus: true
    width: Math.min(380, (parent ? parent.width : 380) - 24)
    x: parent ? Math.round((parent.width - width) / 2) : 0
    y: parent ? Math.round((parent.height - height) / 2) : 0
    padding: 20
    background: DialogFrame {}
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }

    contentItem: ColumnLayout {
        spacing: 12

        HeadingLabel { text: qsTr("自定义列") }

        Text {
            Layout.fillWidth: true
            text: qsTr("勾选要显示的列，至少保留一列。按住列标题左右拖动可以调整顺序；拖动标题右侧的边缘可以调整列宽，双击边缘恢复该列的默认宽度。")
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }

        GridLayout {
            Layout.fillWidth: true
            columns: 2
            rowSpacing: 2
            columnSpacing: 12

            Repeater {
                model: dialog.columns

                delegate: SelectionCheckBox {
                    required property var modelData
                    objectName: "historyColumnToggle_" + modelData.key
                    Layout.fillWidth: true
                    text: modelData.label
                    checked: dialog.shown.indexOf(modelData.key) >= 0
                    // The last shown column stays.
                    enabled: !checked || dialog.shown.length > 1
                    Accessible.name: modelData.label
                    onClicked: dialog.controller.setVisible(modelData.key, checked)
                }
            }
        }

        Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            AppButton {
                objectName: "historyColumnsReset"
                variant: "ghost"
                text: qsTr("恢复默认")
                enabled: dialog.controller.customised
                onClicked: dialog.controller.reset()
            }
            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "historyColumnsDone"
                variant: "primary"
                text: qsTr("完成")
                onClicked: dialog.close()
            }
        }
    }
}
