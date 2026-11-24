pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// The tool row of 副本提醒与导随宏: search, the four filter capsules (类型 / 职业 / 区服 / 语言)
// and, for the local list, 导入 and 新建. Which duty is the directory's business and the network
// state sits under it, so this row only holds what finds things (Design: 条目很多时).
RowLayout {
    id: bar

    required property var store
    spacing: 8

    function focusSearch() { search.forceActiveFocus(); search.selectAll() }

    StyledTextField {
        id: search
        objectName: "dutyCommunitySearch"
        Layout.fillWidth: true
        Layout.minimumWidth: 180
        Layout.maximumWidth: 400
        leftPadding: 30
        placeholderText: qsTr("搜索标题、正文、副本或作者")
        Accessible.name: placeholderText
        text: bar.store.query
        onTextEdited: bar.store.query = text
        Image {
            x: 10
            anchors.verticalCenter: parent.verticalCenter
            width: 14
            height: 14
            source: Lucide.source("search", Theme.textSecondary)
            sourceSize: Qt.size(28, 28)
        }
    }
    MultiFilterButton {
        objectName: "dutyCommunityTypeFilter"
        iconName: "clipboard-copy"
        title: qsTr("类型")
        options: bar.store.typeOptions
        selection: bar.store.typeFilter
        onSelectionEdited: value => bar.store.setFilter("type", value)
    }
    MultiFilterButton {
        objectName: "dutyCommunityJobFilter"
        iconName: "users"
        title: qsTr("职业")
        options: bar.store.jobOptions
        selection: bar.store.jobFilter
        searchable: true
        searchPlaceholder: qsTr("搜索职业名称或缩写")
        emptyText: qsTr("没有匹配的职业")
        onSelectionEdited: value => bar.store.setFilter("job", value)
    }
    MultiFilterButton {
        objectName: "dutyCommunityRegionFilter"
        iconName: "globe"
        title: qsTr("区服")
        options: bar.store.regionOptions
        selection: bar.store.regionFilter
        ToolTip.visible: hovered && bar.store.preset && bar.store.regionFilter.length > 0
        ToolTip.text: qsTr("公共库先只显示你的区服和界面语言的内容，在这里取消勾选就能看到其他的")
        onSelectionEdited: value => bar.store.setFilter("region", value)
    }
    MultiFilterButton {
        objectName: "dutyCommunityLanguageFilter"
        iconName: "languages"
        title: qsTr("语言")
        options: bar.store.languageOptions
        selection: bar.store.languageFilter
        ToolTip.visible: hovered && bar.store.preset && bar.store.languageFilter.length > 0
        ToolTip.text: qsTr("公共库先只显示你的区服和界面语言的内容，在这里取消勾选就能看到其他的")
        onSelectionEdited: value => bar.store.setFilter("language", value)
    }
    AppButton {
        objectName: "dutyCommunityResetFilters"
        visible: bar.store.resettable
        variant: "ghost"
        iconName: "filter-x"
        text: qsTr("清除筛选")
        onClicked: bar.store.resetFilters()
    }
    Item { Layout.fillWidth: true }

    ActionMenuButton {
        objectName: "dutyCommunityImportMenu"
        visible: !bar.store.remote
        text: qsTr("导入")
        groups: [{ head: "", items: [
            { key: "public", icon: "globe", label: qsTr("从公共共享库挑选"), sub: qsTr("到「公共共享库」里勾选，再导入到本地") },
            { key: "file", icon: "file-input", label: qsTr("导入文件…"), sub: qsTr("选一个之前导出的 .json 文件") }
        ] }]
        onPicked: key => key === "public" ? bar.store.tab = "public" : bar.store.importFile()
    }
    ActionMenuButton {
        objectName: "dutyCommunityNewMenu"
        visible: !bar.store.remote
        variant: "primary"
        text: qsTr("新建")
        menuWidth: 340
        groups: [
            { head: "", items: [
                { key: "macro", icon: "clipboard-copy", label: qsTr("宏"), sub: qsTr("一行一条指令，复制到游戏里用") },
                { key: "reminder", icon: "bell", label: qsTr("提醒"), sub: qsTr("记下这个副本要注意的事") }
            ] },
            { head: qsTr("从常用宏示例开始"), items: bar.store.examples.map(example => (
                { key: "example", icon: "copy", label: example.title, sub: bar.store.firstLine(example.body), example: example })) }
        ]
        onPicked: (key, item) => key === "example" ? bar.store.editExample(item.example) : bar.store.editNew(key, null)
    }
}
