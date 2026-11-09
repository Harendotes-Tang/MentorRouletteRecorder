pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

HistoryFilterButton {
    id: control
    property var facets: null
    property string selection: ""
    signal regionPicked(string region)
    signal helpRequested()
    readonly property var choices: [
        { key: "", label: qsTr("全部区服") }, { key: "CN", label: qsTr("国服 CN") },
        { key: "TC", label: qsTr("繁中服 TC") }, { key: "GLOBAL", label: qsTr("国际服 GLOBAL") }
    ]
    iconName: "globe"
    selected: selection.length > 0
    text: {
        const choice = choices.find(row => row.key === selection)
        return choice ? choice.label : Theme.live(Runtime.fmt.regionLabel(selection))
    }
    menuWidth: 264
    Accessible.name: qsTr("记录区服") + " · " + text
    function countText(key) {
        if (!facets || facets.loading) return "…"
        const value = facets.regionCounts[key || "all"]
        return value === undefined ? "—" : String(value)
    }
    menuContent: ColumnLayout {
        spacing: 2
        Text { Layout.bottomMargin: 6; text: qsTr("区服"); color: Theme.textPrimary; font.bold: true; font.pixelSize: Theme.fs(13) }
        Repeater {
            model: control.choices
            RadioButton {
                id: choice
                required property var modelData
                objectName: "historyRegionChoice_" + (modelData.key || "all")
                Layout.fillWidth: true
                implicitHeight: 32
                checked: control.selection === modelData.key
                text: modelData.label
                focusPolicy: Qt.StrongFocus
                onClicked: { control.regionPicked(modelData.key); control.menu.close() }
                indicator: Rectangle {
                    x: 1; y: (choice.height - height) / 2
                    width: 15; height: 15; radius: 8; color: "transparent"
                    border.color: choice.checked || choice.activeFocus ? control.filterAccent : Theme.textSecondary
                    Rectangle { anchors.centerIn: parent; width: 7; height: 7; radius: 4; color: control.filterAccent; visible: choice.checked }
                }
                contentItem: RowLayout {
                    spacing: 8
                    Text { Layout.leftMargin: 24; Layout.fillWidth: true; text: choice.text; color: Theme.textPrimary; font.pixelSize: Theme.fs(13); font.bold: choice.checked }
                    Text { text: control.countText(choice.modelData.key); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                }
                background: Rectangle { radius: 2; color: choice.activeFocus || choice.hovered ? Theme.fill : "transparent" }
            }
        }
        Rectangle { Layout.fillWidth: true; Layout.topMargin: 8; Layout.bottomMargin: 5; implicitHeight: 1; color: Theme.neutral300 }
        RowLayout {
            Layout.fillWidth: true
            Text { Layout.fillWidth: true; text: qsTr("未适配区服可协助采证"); color: Theme.textMuted; font.pixelSize: Theme.fs(11) }
            Button {
                id: helpButton
                objectName: "historyRegionHelp"
                text: qsTr("帮助适配")
                focusPolicy: Qt.StrongFocus
                padding: 4
                contentItem: Text { text: helpButton.text; color: control.filterAccent; font.pixelSize: Theme.fs(12) }
                background: Rectangle { color: helpButton.hovered || helpButton.activeFocus ? control.filterTint : "transparent"; radius: 2 }
                onClicked: { control.menu.close(); control.helpRequested() }
            }
        }
        Text {
            visible: !!control.facets && control.facets.error.length > 0
            Layout.fillWidth: true; wrapMode: Text.Wrap
            text: control.facets ? control.facets.error : ""
            color: Theme.red; font.pixelSize: Theme.fs(12)
        }
        AppButton { visible: !!control.facets && control.facets.error.length > 0; text: qsTr("重试"); onClicked: control.facets.refresh() }
    }
}
