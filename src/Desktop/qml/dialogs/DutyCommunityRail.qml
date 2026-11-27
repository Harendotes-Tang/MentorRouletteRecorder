pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// The left column of 副本提醒与导随宏, 副本目录: it sets the range the list shows. The fixed
// ranges come first (全部 / 当前副本 / 收藏 / 最近用过 / 通用), then the duties that have entries
// by category. Every number follows the search and the filters, so a search shows where its
// hits are. Under it, on the public tab, the network state, refresh and the switch.
Rectangle {
    id: rail

    required property var store
    color: Theme.eorzea ? Theme.insetBackground : Theme.windowBackground

    ColumnLayout {
        anchors.fill: parent
        spacing: 0

        StyledTextField {
            id: find
            objectName: "dutyCommunityFindDuty"
            Layout.fillWidth: true
            Layout.margins: 10
            Layout.bottomMargin: 6
            leftPadding: 30
            placeholderText: qsTr("找副本")
            Accessible.name: placeholderText
            text: rail.store.railQuery
            onTextEdited: rail.store.railQuery = text
            Image {
                x: 10
                anchors.verticalCenter: parent.verticalCenter
                width: 13
                height: 13
                source: Lucide.source("search", Theme.textSecondary)
                sourceSize: Qt.size(26, 26)
            }
        }

        ListView {
            id: nav
            objectName: "dutyCommunityRail"
            Layout.fillWidth: true
            Layout.fillHeight: true
            leftMargin: 6
            rightMargin: 6
            bottomMargin: 10
            spacing: 1
            clip: true
            model: rail.store.railEntries
            ScrollBar.vertical: ScrollBar {}

            delegate: Item {
                id: line
                required property var modelData
                readonly property var entry: modelData
                readonly property bool isItem: entry.kind === "item"
                readonly property bool isGroup: entry.kind === "group"
                width: nav.width - nav.leftMargin - nav.rightMargin
                height: entry.kind === "head" ? 36 : entry.kind === "none" ? 48
                      : isItem && entry.sub ? 44 : isItem && entry.level === 2 ? 28 : 30

                Text {
                    renderType: Theme.bodyRenderType
                    visible: line.entry.kind === "head" || line.entry.kind === "none"
                    x: 8
                    anchors.bottom: parent.bottom
                    anchors.bottomMargin: line.entry.kind === "head" ? 4 : 12
                    text: line.entry.kind === "head" ? line.entry.label : qsTr("没有匹配的副本")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    font.weight: line.entry.kind === "head" ? Font.DemiBold : Font.Normal
                }

                ItemDelegate {
                    id: pick
                    objectName: "dutyCommunityScope"
                    visible: line.isItem || line.isGroup
                    anchors.fill: parent
                    enabled: !line.isItem || !line.entry.off
                    padding: 0
                    focusPolicy: Qt.StrongFocus
                    Accessible.name: (line.entry.label || "") + (line.isItem && line.entry.sub ? " " + line.entry.sub : "")
                    AppToolTip { visible: pick.hovered && !!line.entry.tip; text: line.entry.tip || "" }
                    onClicked: {
                        if (line.isGroup) rail.store.toggleRailGroup(line.entry.category, true)
                        rail.store.pickScope(line.entry.key)
                    }
                    background: Rectangle {
                        radius: Theme.radiusS
                        opacity: pick.enabled ? 1 : 0.5
                        color: line.entry.on ? Theme.accentMuted : pick.hovered || pick.activeFocus ? Theme.fill : "transparent"
                        Rectangle {
                            visible: !!line.entry.on
                            width: 2
                            height: parent.height
                            color: Theme.eorzea ? Theme.gold : Theme.accent
                        }
                    }
                    contentItem: RowLayout {
                        spacing: 8
                        opacity: pick.enabled ? 1 : 0.5
                        // A category opens and closes on its caret; its name picks the whole category.
                        Button {
                            id: caret
                            objectName: "dutyCommunityScopeCaret"
                            visible: line.isGroup
                            Layout.preferredWidth: 24
                            Layout.fillHeight: true
                            padding: 0
                            focusPolicy: Qt.StrongFocus
                            Accessible.name: (line.entry.open ? qsTr("收起「%1」") : qsTr("展开「%1」")).arg(line.entry.label || "")
                            onClicked: rail.store.toggleRailGroup(line.entry.category, !line.entry.open)
                            background: Item {}
                            contentItem: Item {
                                Image {
                                    anchors.centerIn: parent
                                    width: 12
                                    height: 12
                                    source: Lucide.source(line.entry.open ? "chevron-down" : "chevron-right",
                                                          caret.hovered ? Theme.textPrimary : Theme.textSecondary)
                                    sourceSize: Qt.size(24, 24)
                                }
                            }
                        }
                        Image {
                            visible: line.isItem && !!line.entry.icon
                            Layout.leftMargin: 8
                            Layout.preferredWidth: 14
                            Layout.preferredHeight: 14
                            source: line.isItem && line.entry.icon
                                    ? Lucide.source(line.entry.icon, line.entry.on ? Theme.accent : Theme.textSecondary) : ""
                            sourceSize: Qt.size(28, 28)
                        }
                        ColumnLayout {
                            Layout.fillWidth: true
                            Layout.leftMargin: line.isItem && !line.entry.icon ? 24 : 0
                            spacing: 0
                            Text {
                                renderType: Theme.bodyRenderType
                                Layout.fillWidth: true
                                textFormat: Text.PlainText
                                elide: Text.ElideRight
                                text: line.entry.label || ""
                                color: line.isItem && line.entry.count === 0 && !line.entry.on && !line.entry.off
                                       ? Theme.textSecondary : Theme.textPrimary
                                font.pixelSize: Theme.fs(line.isItem && line.entry.level === 2 ? 12 : 13)
                                font.weight: line.entry.on ? Font.DemiBold : Font.Normal
                            }
                            Text {
                                renderType: Theme.bodyRenderType
                                visible: line.isItem && !!line.entry.sub
                                Layout.fillWidth: true
                                textFormat: Text.PlainText
                                elide: Text.ElideRight
                                text: line.isItem ? line.entry.sub : ""
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(11)
                            }
                        }
                        Image {
                            visible: line.isItem && line.entry.here
                            Layout.preferredWidth: 11
                            Layout.preferredHeight: 11
                            source: Lucide.source("target", Theme.accent)
                            sourceSize: Qt.size(22, 22)
                            Accessible.name: qsTr("当前副本")
                        }
                        Text {
                            renderType: Theme.bodyRenderType
                            visible: !(line.isItem && line.entry.off)
                            Layout.rightMargin: 8
                            text: Number(line.entry.count || 0).toLocaleString(Qt.locale("en_US"), "f", 0)
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                        }
                    }
                }
            }
        }

        Rectangle { visible: net.visible; Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
        ColumnLayout {
            id: net
            visible: rail.store.remote
            Layout.fillWidth: true
            Layout.leftMargin: 12
            Layout.rightMargin: 8
            Layout.topMargin: 8
            Layout.bottomMargin: 8
            spacing: 4

            RowLayout {
                Layout.fillWidth: true
                spacing: 6
                StatusLed { tone: rail.store.refreshing ? "warn" : rail.store.online ? "ok" : "off" }
                Text {
                    objectName: "dutyCommunityNetwork"
                    renderType: Theme.bodyRenderType
                    Layout.fillWidth: true
                    wrapMode: Text.Wrap
                    maximumLineCount: 3
                    elide: Text.ElideRight
                    text: rail.store.refreshing ? qsTr("正在刷新…")
                        : rail.store.online ? (rail.store.libraryStatus && rail.store.libraryStatus !== "ready"
                                               ? rail.store.statusText(rail.store.libraryStatus) : qsTr("已联网"))
                        : rail.store.publicRows.length > 0 ? qsTr("联网已关闭 · 显示缓存") : qsTr("联网已关闭")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                }
                AppButton {
                    objectName: "dutyCommunityRefresh"
                    compact: true
                    variant: "ghost"
                    implicitWidth: implicitHeight
                    iconName: "refresh-cw"
                    Accessible.name: qsTr("刷新公共库")
                    tip: qsTr("刷新公共库")
                    enabled: rail.store.online && !rail.store.refreshing
                    onClicked: rail.store.loadPublic("refresh")
                }
            }
            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                Text {
                    renderType: Theme.bodyRenderType
                    Layout.fillWidth: true
                    wrapMode: Text.Wrap
                    text: qsTr("允许公共库联网")
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12)
                }
                ToggleSwitch {
                    objectName: "dutyCommunityOnline"
                    accessibleName: qsTr("允许公共库联网")
                    checked: rail.store.online
                    onToggled: value => value ? rail.store.goOnline() : rail.store.loadPublic("online", { enabled: false })
                }
            }
        }
    }
}
