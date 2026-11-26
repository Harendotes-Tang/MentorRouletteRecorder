pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// A button that opens a short menu of actions, each with a one-line explanation.
AppButton {
    id: control

    /// [{ head, items: [{ key, icon, label, sub, code }] }]; `code`, when given, is shown as
    /// typed in a small monospace chip in place of the icon (the macro editor's 插入 menu).
    property var groups: []
    property real menuWidth: 288
    readonly property alias menu: popup
    signal picked(string key, var item)

    // The chevron sits after the label, so the button can still lead with an icon of its own.
    trailingIconName: "chevron-down"
    Accessible.role: Accessible.ButtonMenu
    onClicked: popup.visible ? popup.close() : popup.open()
    onVisibleChanged: if (!visible) popup.close()

    Popup {
        id: popup
        x: control.width - width
        y: control.height + 5
        width: control.menuWidth
        padding: 4
        focus: true
        closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutsideParent
        background: Rectangle {
            radius: Theme.radiusS
            color: Theme.surface
            border.color: Theme.eorzea ? Theme.gold3 : Theme.border
            border.width: 1
        }

        contentItem: ColumnLayout {
            spacing: 0

            Repeater {
                model: control.groups

                delegate: ColumnLayout {
                    id: group
                    required property var modelData
                    required property int index
                    Layout.fillWidth: true
                    spacing: 0

                    Rectangle {
                        visible: group.index > 0
                        Layout.fillWidth: true
                        Layout.topMargin: 4
                        Layout.bottomMargin: 4
                        implicitHeight: 1
                        color: Theme.border
                    }
                    Text {
                        renderType: Theme.bodyRenderType
                        visible: !!group.modelData.head
                        Layout.fillWidth: true
                        Layout.margins: 8
                        Layout.bottomMargin: 4
                        text: group.modelData.head || ""
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        font.weight: Font.DemiBold
                    }

                    Repeater {
                        model: group.modelData.items

                        delegate: Button {
                            id: entry
                            required property var modelData
                            Layout.fillWidth: true
                            padding: 8
                            focusPolicy: Qt.StrongFocus
                            Accessible.name: modelData.label
                            onClicked: {
                                popup.close()
                                control.picked(modelData.key, modelData)
                            }
                            background: Rectangle {
                                radius: Theme.radiusXs
                                color: entry.hovered || entry.activeFocus ? Theme.fill : "transparent"
                            }
                            contentItem: RowLayout {
                                spacing: 10
                                Image {
                                    visible: !entry.modelData.code
                                    Layout.alignment: Qt.AlignTop
                                    Layout.topMargin: 2
                                    Layout.preferredWidth: 14
                                    Layout.preferredHeight: 14
                                    source: Lucide.source(entry.modelData.icon || "", Theme.textSecondary)
                                    sourceSize: Qt.size(28, 28)
                                }
                                Rectangle {
                                    visible: !!entry.modelData.code
                                    Layout.alignment: Qt.AlignTop
                                    Layout.preferredWidth: 76
                                    implicitHeight: codeText.implicitHeight + 2
                                    radius: Theme.radiusXs
                                    color: Theme.fill
                                    Text {
                                        id: codeText
                                        anchors.centerIn: parent
                                        textFormat: Text.PlainText
                                        text: entry.modelData.code || ""
                                        color: Theme.blue
                                        font.family: Theme.monoFamily
                                        font.pixelSize: Theme.fs(12)
                                    }
                                }
                                ColumnLayout {
                                    Layout.fillWidth: true
                                    spacing: 1
                                    Text {
                                        renderType: Theme.bodyRenderType
                                        Layout.fillWidth: true
                                        textFormat: Text.PlainText
                                        elide: Text.ElideRight
                                        text: entry.modelData.label
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(13)
                                    }
                                    Text {
                                        renderType: Theme.bodyRenderType
                                        visible: !!entry.modelData.sub
                                        Layout.fillWidth: true
                                        textFormat: Text.PlainText
                                        elide: Text.ElideRight
                                        text: entry.modelData.sub || ""
                                        color: Theme.textSecondary
                                        font.pixelSize: Theme.fs(12)
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
