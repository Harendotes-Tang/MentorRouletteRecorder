pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// The two tool rows of 副本提醒与导随宏: search and filter capsules, then the two switches and,
// at the right end, what the tab needs - 导入 / 新建 for the local list, the network state for
// the public library.
ColumnLayout {
    id: bar

    required property var store
    spacing: 8

    // A capsule that is a switch: it says what it limits the list to while it is on.
    component TogglePill: Button {
        id: pill
        property string iconName: ""
        property string value: ""
        checkable: true
        implicitHeight: Theme.eorzea ? 32 : 30
        leftPadding: 10
        rightPadding: 10
        focusPolicy: Qt.StrongFocus
        Accessible.role: Accessible.CheckBox
        Accessible.name: text + (checked && value ? " " + value : "")
        background: Rectangle {
            radius: Theme.radiusS
            color: pill.checked ? Theme.accentMuted
                 : Theme.eorzea ? (Theme.dark ? "#47000000" : "#8cffffff") : Theme.surface
            border.width: 1
            border.color: pill.checked || pill.activeFocus ? (Theme.eorzea ? Theme.gold : Theme.accent)
                        : pill.hovered ? Theme.neutral400
                        : Theme.eorzea ? Theme.border : Theme.neutral300
        }
        contentItem: RowLayout {
            spacing: 6
            Image {
                Layout.preferredWidth: 13
                Layout.preferredHeight: 13
                source: Lucide.source(pill.iconName, pill.checked ? Theme.accent : Theme.textSecondary)
                sourceSize: Qt.size(26, 26)
            }
            Text {
                renderType: Theme.bodyRenderType
                text: pill.checked && pill.value ? qsTr("%1：").arg(pill.text) : pill.text
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                font.weight: pill.checked ? Font.DemiBold : Font.Normal
            }
            Text {
                renderType: Theme.bodyRenderType
                visible: pill.checked && pill.value.length > 0
                Layout.maximumWidth: 220
                elide: Text.ElideRight
                textFormat: Text.PlainText
                text: pill.value
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
            }
        }
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 8

        StyledTextField {
            id: search
            objectName: "dutyCommunitySearch"
            Layout.fillWidth: true
            Layout.minimumWidth: 180
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
            onSelectionEdited: value => bar.store.typeFilter = value
        }
        MultiFilterButton {
            objectName: "dutyCommunityRegionFilter"
            iconName: "globe"
            title: qsTr("区服")
            options: bar.store.regionOptions
            selection: bar.store.regionFilter
            onSelectionEdited: value => bar.store.regionFilter = value
        }
        MultiFilterButton {
            objectName: "dutyCommunityLanguageFilter"
            iconName: "notebook-pen"
            title: qsTr("语言")
            options: bar.store.languageOptions
            selection: bar.store.languageFilter
            onSelectionEdited: value => bar.store.languageFilter = value
        }
        MultiFilterButton {
            objectName: "dutyCommunityStageFilter"
            iconName: "swords"
            title: qsTr("BOSS / 阶段")
            options: bar.store.stageOptions
            selection: bar.store.stageFilter
            searchable: bar.store.stageOptions.length > 6
            searchPlaceholder: qsTr("搜索 BOSS 或阶段")
            emptyText: bar.store.stageOptions.length > 0 ? qsTr("没有匹配的 BOSS 或阶段")
                                                         : qsTr("这里的内容还没有标 BOSS 或阶段")
            onSelectionEdited: value => bar.store.stageFilter = value
        }
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 8

        TogglePill {
            objectName: "dutyCommunityDutyOnly"
            iconName: "target"
            text: qsTr("当前副本与通用")
            value: bar.store.inDuty ? bar.store.currentDutyName : qsTr("不在副本中，只看通用")
            checked: bar.store.dutyOnly
            ToolTip.visible: hovered
            ToolTip.text: bar.store.inDuty ? qsTr("只看%1和通用的内容").arg(bar.store.currentDutyName)
                                           : qsTr("现在不在副本里，打开后只显示通用的内容")
            onClicked: bar.store.dutyOnly = !bar.store.dutyOnly
        }
        TogglePill {
            objectName: "dutyCommunityFavoriteOnly"
            visible: !bar.store.remote
            iconName: "medal"
            text: qsTr("只看收藏")
            checked: bar.store.favoriteOnly
            onClicked: bar.store.favoriteOnly = !bar.store.favoriteOnly
        }
        AppButton {
            objectName: "dutyCommunityResetFilters"
            visible: bar.store.filterActive
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

        StatusLed {
            visible: bar.store.remote
            tone: bar.store.refreshing ? "warn" : bar.store.online ? "ok" : "off"
        }
        Text {
            objectName: "dutyCommunityNetwork"
            renderType: Theme.bodyRenderType
            visible: bar.store.remote
            text: bar.store.refreshing ? qsTr("正在刷新…")
                : bar.store.online ? (bar.store.libraryStatus && bar.store.libraryStatus !== "ready"
                                      ? bar.store.statusText(bar.store.libraryStatus) : qsTr("已联网"))
                : bar.store.publicRows.length > 0 ? qsTr("联网已关闭 · 显示缓存") : qsTr("联网已关闭")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            Layout.maximumWidth: 320
            elide: Text.ElideRight
        }
        Text {
            renderType: Theme.bodyRenderType
            visible: bar.store.remote
            Layout.leftMargin: 6
            text: qsTr("允许公共库联网")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
        }
        ToggleSwitch {
            objectName: "dutyCommunityOnline"
            visible: bar.store.remote
            accessibleName: qsTr("允许公共库联网")
            checked: bar.store.online
            onToggled: value => value ? bar.store.goOnline() : bar.store.loadPublic("online", { enabled: false })
        }
        AppButton {
            objectName: "dutyCommunityRefresh"
            visible: bar.store.remote
            text: qsTr("刷新公共库")
            enabled: bar.store.online && !bar.store.refreshing
            onClicked: bar.store.loadPublic("refresh")
        }
    }
}
