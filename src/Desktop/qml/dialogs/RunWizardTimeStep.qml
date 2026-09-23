import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// Step 3 of the record wizard (EditRunDialog): 日期与时间, 原因, 备注, the
// before/after table and the error banner. Every refusal, local or from the
// Collector, is about something on this step, so the dialog brings it up.
//
// Only the view lives here; the state is the dialog's (`wizard`).
ColumnLayout {
    id: step

    required property var wizard

    spacing: 14

    GridLayout {
        Layout.fillWidth: true
        columns: 3
        columnSpacing: 12
        rowSpacing: 12

        ColumnLayout {
            Layout.columnSpan: 3
            Layout.maximumWidth: 220
            Layout.fillWidth: true
            spacing: 4
            FieldLabel { text: qsTr("日期") }
            // The calendar behind the icon, or a typed yyyy-MM-dd (DateField).
            DateField {
                id: matchedDateField
                objectName: "matchedDateField"
                Layout.fillWidth: true
                Binding {
                    target: matchedDateField
                    property: "text"
                    value: step.wizard.matchedDate
                    restoreMode: Binding.RestoreNone
                }
                color: RunForm.isValidDate(text) ? Theme.textPrimary : Theme.red
                onTextChanged: step.wizard.setMatchedDate(text)
            }
        }

        ColumnLayout {
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            spacing: 4
            RowLayout {
                spacing: 4
                FieldLabel { text: qsTr("匹配时间") }
                Text { text: "*"; color: Theme.accent; font.pixelSize: Theme.fs(12); font.bold: true }
            }
            // Digits typed as 2130 become 21:30; the clock icon opens the wheels (TimeField).
            TimeField {
                id: matchedTimeField
                objectName: "matchedTimeField"
                Layout.fillWidth: true
                Binding {
                    target: matchedTimeField
                    property: "text"
                    value: step.wizard.matchedTime
                    restoreMode: Binding.RestoreNone
                }
                onTextChanged: step.wizard.matchedTime = text
            }
        }

        ColumnLayout {
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            spacing: 4
            FieldLabel { text: qsTr("进本时间") }
            TimeField {
                id: enteredTimeField
                objectName: "enteredTimeField"
                Layout.fillWidth: true
                Binding {
                    target: enteredTimeField
                    property: "text"
                    value: step.wizard.enteredTime
                    restoreMode: Binding.RestoreNone
                }
                onTextChanged: step.wizard.enteredTime = text
            }
        }

        ColumnLayout {
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            spacing: 4
            FieldLabel { text: qsTr("结束时间") }
            TimeField {
                id: endedTimeField
                objectName: "endedTimeField"
                Layout.fillWidth: true
                Binding {
                    target: endedTimeField
                    property: "text"
                    value: step.wizard.endedTime
                    restoreMode: Binding.RestoreNone
                }
                onTextChanged: step.wizard.endedTime = text
            }
        }

        Text {
            visible: step.wizard.dayFieldsVisible
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            Layout.alignment: Qt.AlignBottom
            Layout.bottomMargin: 7
            text: qsTr("跨天的记录分别填写日期")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        ColumnLayout {
            visible: step.wizard.dayFieldsVisible
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            spacing: 4
            FieldLabel { text: qsTr("进本日期") }
            DateField {
                id: enteredDateField
                objectName: "enteredDateField"
                Layout.fillWidth: true
                Binding {
                    target: enteredDateField
                    property: "text"
                    value: step.wizard.enteredDate
                    restoreMode: Binding.RestoreNone
                }
                color: !step.wizard.enteredTime.trim() || RunForm.isValidDate(text)
                       ? Theme.textPrimary : Theme.red
                onTextChanged: step.wizard.enteredDate = text
            }
        }

        ColumnLayout {
            visible: step.wizard.dayFieldsVisible
            Layout.fillWidth: true
            Layout.preferredWidth: 1
            spacing: 4
            FieldLabel { text: qsTr("结束日期") }
            DateField {
                id: endedDateField
                objectName: "endedDateField"
                Layout.fillWidth: true
                Binding {
                    target: endedDateField
                    property: "text"
                    value: step.wizard.endedDate
                    restoreMode: Binding.RestoreNone
                }
                color: !step.wizard.endedTime.trim() || RunForm.isValidDate(text)
                       ? Theme.textPrimary : Theme.red
                onTextChanged: step.wizard.endedDate = text
            }
        }
    }

    RowLayout {
        Layout.fillWidth: true
        Layout.topMargin: -4
        spacing: 8

        Text {
            objectName: "timeHint"
            Layout.fillWidth: true
            text: step.wizard.resultCode === "CANCELLED_BEFORE_ENTRY"
                  ? qsTr("进本前取消：进本与结束时间可留空。")
                  : step.wizard.resultCode === "COMPLETED"
                    ? qsTr("通关记录需要进本与结束时间；耗时 = 结束 − 进本。")
                    : qsTr("结束时间可留空；不记进本时间时不计入平均耗时。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        AppButton {
            visible: !step.wizard.dayFieldsVisible
            variant: "ghost"
            compact: true
            text: qsTr("跨天？")
            onClicked: step.wizard.dayFieldsOpen = true
        }
    }

    Rectangle {
        Layout.fillWidth: true
        visible: step.wizard.resultCode !== "CANCELLED_BEFORE_ENTRY" && !step.wizard.enteredTime.trim()
        implicitHeight: entryHelp.implicitHeight + 20
        color: Theme.insetBackground
        radius: Theme.radiusS
        border.width: 1
        border.color: Theme.eorzea ? Theme.gold3 : Theme.orange

        RowLayout {
            id: entryHelp

            anchors.left: parent.left
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            anchors.leftMargin: 12
            anchors.rightMargin: 12
            spacing: 10

            Text {
                Layout.fillWidth: true
                text: qsTr("还差进本时间。记得的话填在上面；不记得可按匹配时间估算，并自动注明。估算时间不计入平均耗时。")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
            AppButton {
                objectName: "estimateEntryButton"
                compact: true
                text: qsTr("估算进本")
                enabled: !!step.wizard.buildUtc(step.wizard.matchedDate, step.wizard.matchedTime)
                onClicked: step.wizard.estimateEntryFromMatch()
            }
        }
    }

    ColumnLayout {
        Layout.fillWidth: true
        spacing: 4
        RowLayout {
            spacing: 4
            FieldLabel { text: step.wizard.reasonLabel }
            Text { text: "*"; color: Theme.accent; font.pixelSize: Theme.fs(12); font.bold: true }
        }
        StyledTextField {
            objectName: "reasonField"
            Layout.fillWidth: true
            text: step.wizard.reasonText
            placeholderText: qsTr("例如：程序未运行时手动补录；网络中断但实际已通关")
            onTextChanged: step.wizard.reasonText = text
        }
    }

    ColumnLayout {
        Layout.fillWidth: true
        spacing: 4
        FieldLabel { text: qsTr("备注") }
        StyledTextField {
            objectName: "noteField"
            Layout.fillWidth: true
            text: step.wizard.noteText
            placeholderText: qsTr("例如：程序未运行时手动补录")
            onTextChanged: step.wizard.noteText = text
        }

        // 备注图片: staged in the dialog, copied into the install directory's
        // note-images folder only when the record is saved (EditRunDialog).
        NoteImageStrip {
            objectName: "noteImageStrip"
            Layout.fillWidth: true
            Layout.topMargin: 4
            editable: !!step.wizard.imageStore
            rows: step.wizard.noteImageRows
            hintText: step.wizard.imageStore
                      ? qsTr("图片保存在软件安装目录：%1").arg(step.wizard.imageStore.rootDirectoryNative)
                      : ""
            onAddRequested: step.wizard.pickNoteImage()
            onRemoveRequested: function(row) { step.wizard.unstageNoteImage(row) }
        }
    }

    // ----------------------------------------- 修改前后对比 --
    ColumnLayout {
        objectName: "diffSection"
        Layout.fillWidth: true
        visible: step.wizard.editMode
        spacing: 8

        Rectangle { Layout.fillWidth: true; Layout.preferredHeight: 1; color: Theme.border }

        CardKicker { text: qsTr("修改前后对比") }

        // grid-template-columns: 110px 1fr 1fr, which a GridLayout cannot
        // express: it distributes free space in proportion to the preferred
        // widths, collapsing both value columns to a few pixels. The widths are
        // therefore derived from the table width, and the parent rectangle
        // shows through the 1 px gaps as the grid lines.
        Rectangle {
            id: diffTable

            readonly property real keyColumnWidth: 110
            readonly property real valueColumnWidth:
                Math.max(60, (width - 2 - keyColumnWidth - 2) / 2)

            Layout.fillWidth: true
            visible: step.wizard.diffRows.length > 0
            radius: Theme.radiusS
            color: Theme.border
            clip: true
            implicitHeight: diffTableColumn.implicitHeight + 2

            Column {
                id: diffTableColumn

                x: 1
                y: 1
                width: parent.width - 2
                spacing: 1

                Repeater {
                    model: step.wizard.diffTableRows

                    delegate: Row {
                        id: diffTableRow

                        required property var modelData

                        /// Every cell is as tall as the tallest of the row's
                        /// three texts, so a long note wraps instead of being
                        /// clipped.
                        readonly property real cellHeight:
                            Math.max(26,
                                     Math.max(keyCell.implicitHeight,
                                              Math.max(beforeCell.implicitHeight,
                                                       afterCell.implicitHeight)) + 10)

                        width: diffTableColumn.width
                        spacing: 1

                        Rectangle {
                            width: diffTable.keyColumnWidth
                            height: diffTableRow.cellHeight
                            color: Theme.surface

                            Text {
                                id: keyCell

                                x: 8
                                width: parent.width - 16
                                anchors.verticalCenter: parent.verticalCenter
                                text: diffTableRow.modelData.k
                                color: diffTableRow.modelData.head
                                       ? Theme.textSecondary : Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                wrapMode: Text.WordWrap
                                maximumLineCount: 3
                                elide: Text.ElideRight
                            }
                        }

                        Rectangle {
                            width: diffTable.valueColumnWidth
                            height: diffTableRow.cellHeight
                            color: Theme.surface

                            Text {
                                id: beforeCell

                                x: 8
                                width: parent.width - 16
                                anchors.verticalCenter: parent.verticalCenter
                                text: diffTableRow.modelData.a
                                color: diffTableRow.modelData.head
                                       ? Theme.textSecondary
                                       : Theme.dimColor(Theme.textPrimary)
                                opacity: diffTableRow.modelData.head ? 1 : Theme.dimOpacity(0.6)
                                font.pixelSize: Theme.fs(12)
                                font.strikeout: !diffTableRow.modelData.head
                                wrapMode: Text.WordWrap
                                maximumLineCount: 3
                                elide: Text.ElideRight
                            }
                        }

                        Rectangle {
                            width: diffTableColumn.width - diffTable.keyColumnWidth
                                   - diffTable.valueColumnWidth - 2
                            height: diffTableRow.cellHeight
                            color: Theme.surface

                            Text {
                                id: afterCell

                                x: 8
                                width: parent.width - 16
                                anchors.verticalCenter: parent.verticalCenter
                                text: diffTableRow.modelData.b
                                color: diffTableRow.modelData.head
                                       ? Theme.textSecondary : Theme.accent
                                font.pixelSize: Theme.fs(12)
                                font.bold: !diffTableRow.modelData.head
                                wrapMode: Text.WordWrap
                                maximumLineCount: 3
                                elide: Text.ElideRight
                            }
                        }
                    }
                }
            }
        }

        Text {
            objectName: "noChangesText"
            Layout.fillWidth: true
            visible: step.wizard.diffRows.length === 0
            text: qsTr("尚未修改任何字段。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }

    Rectangle {
        objectName: "errorBanner"
        Layout.fillWidth: true
        visible: step.wizard.errorText.length > 0
        radius: Theme.radiusS
        color: Theme.redBackground
        border.width: 1
        border.color: Theme.red
        implicitHeight: errorLabel.implicitHeight + 16

        Text {
            id: errorLabel
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.margins: 12
            anchors.verticalCenter: parent.verticalCenter
            text: step.wizard.errorText
            color: Theme.red
            font.pixelSize: Theme.fs(13)
            font.bold: true
            wrapMode: Text.WordWrap
        }
    }
}
