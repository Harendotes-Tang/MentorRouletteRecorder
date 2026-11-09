pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "Lucide.js" as Lucide

HistoryFilterButton {
    id: control
    property var facets: null
    property string preset: "all"
    property bool updating: false
    // Compatibility with the page's atomic filter edit guard; single inputs do not stage a mouse range.
    property bool selectionPending: false
    property alias startField: fromInput
    property alias endField: toInput
    property alias startText: fromInput.text
    property alias endText: toInput.text
    readonly property bool inverted: fromInput.valid && toInput.valid
                                    && fromInput.text.length > 0 && toInput.text.length > 0
                                    && fromInput.text.trim() > toInput.text.trim()
    readonly property bool valid: fromInput.valid && toInput.valid && !inverted
    readonly property var options: [
        { key: "all", label: qsTr("全部时间") }, { key: "today", label: qsTr("今天") },
        { key: "last7", label: qsTr("近 7 天") }, { key: "last30", label: qsTr("近 30 天") },
        { key: "month", label: qsTr("本月") }, { key: "custom", label: qsTr("自定义") }
    ]
    iconName: "calendar"
    selected: preset !== "all"
    invalid: !valid
    text: invalid ? qsTr("日期有误") : options.find(row => row.key === preset).label
    menuWidth: 292
    Accessible.name: qsTr("历史日期范围") + " · " + text
    ToolTip.visible: hovered && invalid
    ToolTip.text: qsTr("日期尚未应用，请检查格式与起止顺序")
    Connections {
        target: control.menu
        function onClosed() {
            fromInput.closeCalendar()
            toInput.closeCalendar()
            if (control.visible && control.windowActive)
                control.forceActiveFocus(Qt.PopupFocusReason)
        }
    }

    function choose(key) {
        preset = key
        if (key === "custom") {
            fromInput.forceActiveFocus(Qt.TabFocusReason)
            return
        }
        if (!facets) return
        const range = facets.dateRange(key)
        updating = true
        fromInput.text = range.start || ""
        toInput.text = range.end || ""
        updating = false
        menu.close()
    }
    function syncPreset() {
        if (!fromInput.text && !toInput.text) { preset = "all"; return }
        if (facets) {
            for (const option of options) {
                if (option.key === "custom" || option.key === "all") continue
                const range = facets.dateRange(option.key)
                if (range.start === fromInput.text && range.end === toInput.text) {
                    preset = option.key
                    return
                }
            }
        }
        preset = "custom"
    }
    function countText(key) {
        if (key === "custom") return ""
        if (!facets || facets.loading) return "…"
        return facets.dateCounts[key] === undefined ? "—" : String(facets.dateCounts[key])
    }
    menuContent: ColumnLayout {
        spacing: 2
        RowLayout {
            Layout.fillWidth: true
            Layout.bottomMargin: 6
            Text { renderType: Theme.bodyRenderType; text: qsTr("日期"); color: Theme.textPrimary; font.bold: true; font.pixelSize: Theme.fs(13) }
            Text { renderType: Theme.bodyRenderType; text: qsTr("按匹配时间"); color: Theme.textMuted; font.pixelSize: Theme.fs(12) }
            Item { Layout.fillWidth: true }
        }
        Repeater {
            model: control.options
            RadioButton {
                id: choice
                required property var modelData
                objectName: "historyDatePreset_" + modelData.key
                Layout.fillWidth: true
                implicitHeight: 32
                checked: control.preset === modelData.key
                text: modelData.label
                focusPolicy: Qt.StrongFocus
                onClicked: control.choose(modelData.key)
                indicator: Rectangle {
                    x: 1; y: (choice.height - height) / 2
                    width: 18; height: 18; radius: 9
                    color: choice.down ? Theme.fill : Theme.insetBackground
                    border.width: choice.visualFocus ? 2 : 1
                    border.color: choice.checked || choice.visualFocus ? Theme.accent : Theme.textSecondary
                    Rectangle { anchors.centerIn: parent; width: 8; height: 8; radius: 4; color: Theme.accent; visible: choice.checked }
                }
                contentItem: RowLayout {
                    spacing: 8
                    Text { renderType: Theme.bodyRenderType; Layout.leftMargin: 26; Layout.fillWidth: true; text: choice.text; color: Theme.textPrimary; font.pixelSize: Theme.fs(13) }
                    Text { renderType: Theme.bodyRenderType; text: control.countText(choice.modelData.key); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                }
                background: Rectangle { radius: 2; color: choice.activeFocus || choice.hovered ? Theme.fill : "transparent" }
            }
        }
        RowLayout {
            visible: control.preset === "custom"
            Layout.topMargin: 8
            Layout.fillWidth: true
            spacing: 6
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 4
                Text { renderType: Theme.bodyRenderType; text: qsTr("开始日期"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                DateField {
                    id: fromInput
                    objectName: "fromDateField"
                    Layout.fillWidth: true
                    Layout.preferredWidth: 116
                    Accessible.name: qsTr("开始日期")
                    onTextChanged: if (!control.updating) control.preset = "custom"
                }
            }
            Image {
                Layout.topMargin: 20
                Layout.preferredWidth: 12; Layout.preferredHeight: 12
                sourceSize: Qt.size(24, 24)
                source: Lucide.source("chevron-right", Theme.textSecondary)
            }
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 4
                Text { renderType: Theme.bodyRenderType; text: qsTr("结束日期"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                DateField {
                    id: toInput
                    objectName: "toDateField"
                    Layout.fillWidth: true
                    Layout.preferredWidth: 116
                    Accessible.name: qsTr("结束日期")
                    onTextChanged: if (!control.updating) control.preset = "custom"
                }
            }
        }
        Text { renderType: Theme.bodyRenderType;
            visible: control.preset === "custom" && (!fromInput.valid || !toInput.valid || control.inverted)
            Layout.fillWidth: true
            Layout.topMargin: 4
            wrapMode: Text.Wrap
            text: control.inverted ? qsTr("开始日期不能晚于结束日期") : qsTr("请输入完整日期：年-月-日")
            color: Theme.red; font.pixelSize: Theme.fs(12)
        }
        Text { renderType: Theme.bodyRenderType;
            visible: !!control.facets && control.facets.error.length > 0
            Layout.fillWidth: true
            wrapMode: Text.Wrap
            text: control.facets ? control.facets.error : ""
            color: Theme.red; font.pixelSize: Theme.fs(12)
        }
        AppButton {
            visible: !!control.facets && control.facets.error.length > 0
            text: qsTr("重试")
            onClicked: control.facets.refresh()
        }
    }
}
