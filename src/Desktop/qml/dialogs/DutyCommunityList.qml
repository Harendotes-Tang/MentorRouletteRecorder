pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// The left column of 副本提醒与导随宏: the filtered entries, a tick box per row, and - only
// once something is ticked - the actions that take several entries at once. On the public tab
// entries that read alike fold under the first one (store.listRows).
ColumnLayout {
    id: pane

    required property var store
    // 全选 takes the rows on screen: a folded group's other entries stay out until it is opened.
    readonly property var ids: store.listRows.map(entry => entry.row.content.id)
    readonly property bool allChecked: ids.length > 0 && ids.every(id => store.basket.includes(id))
    spacing: 0

    RowLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 14
        Layout.rightMargin: 14
        Layout.preferredHeight: 36
        spacing: 8

        SelectionCheckBox {
            objectName: "dutyCommunitySelectAll"
            enabled: pane.ids.length > 0
            tristate: true
            checkState: pane.allChecked ? Qt.Checked
                      : pane.store.checkedIds.length > 0 ? Qt.PartiallyChecked : Qt.Unchecked
            nextCheckState: () => Qt.Unchecked
            Accessible.name: qsTr("全选")
            onClicked: pane.store.basket = pane.allChecked || pane.store.checkedIds.length > 0 ? [] : pane.ids
        }
        Text {
            objectName: "dutyCommunityCount"
            renderType: Theme.bodyRenderType
            text: pane.store.filtered.length === pane.store.rows.length
                  ? qsTr("%1 项").arg(pane.store.filtered.length)
                  : qsTr("%1 / %2 项").arg(pane.store.filtered.length).arg(pane.store.rows.length)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
        Item { Layout.fillWidth: true }
        Text {
            renderType: Theme.bodyRenderType
            visible: pane.store.dutyOnly && pane.store.inDuty
            text: qsTr("当前副本在前")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }
    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

    ListView {
        id: listing
        objectName: "dutyCommunityRows"
        Layout.fillWidth: true
        Layout.fillHeight: true
        topMargin: 6
        bottomMargin: 6
        spacing: 2
        clip: true
        model: pane.store.listRows
        ScrollBar.vertical: ScrollBar {}

        delegate: ItemDelegate {
            id: entry
            required property var modelData
            readonly property var row: modelData.row
            readonly property var item: row.content
            readonly property bool chosen: pane.store.currentId === item.id
            readonly property string author: pane.store.remote && item.author ? "@" + item.author.login : ""
            // An opened group's other entries sit indented under the first.
            x: modelData.member ? 26 : 6
            width: listing.width - x - 6
            padding: 8
            leftPadding: 8
            focusPolicy: Qt.StrongFocus
            Accessible.name: item.title
            onClicked: pane.store.select(item.id)

            background: Rectangle {
                radius: Theme.radiusS
                color: entry.chosen ? Theme.accentMuted : entry.hovered || entry.activeFocus ? Theme.fill : "transparent"
                Rectangle {
                    visible: entry.chosen
                    width: 2
                    height: parent.height
                    color: Theme.eorzea ? Theme.gold : Theme.accent
                }
            }
            contentItem: RowLayout {
                spacing: 8

                SelectionCheckBox {
                    checked: pane.store.basket.includes(entry.item.id)
                    Accessible.name: qsTr("选择「%1」").arg(entry.item.title)
                    onClicked: pane.store.toggleChecked(entry.item.id)
                }
                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 3
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 8
                        ContentTypeTag { type: entry.item.type }
                        Text {
                            renderType: Theme.bodyRenderType
                            Layout.fillWidth: true
                            textFormat: Text.PlainText
                            elide: Text.ElideRight
                            text: entry.item.title
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(13)
                            font.weight: Font.DemiBold
                        }
                    }
                    Text {
                        renderType: Theme.bodyRenderType
                        Layout.fillWidth: true
                        textFormat: Text.PlainText
                        elide: Text.ElideRight
                        text: [pane.store.dutyLabel(entry.item), pane.store.stageLabel(entry.item), entry.author]
                              .filter(part => part).join(" · ")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                    }
                    AppButton {
                        objectName: "dutyCommunitySimilarToggle"
                        visible: entry.modelData.folded > 0
                        compact: true
                        variant: "ghost"
                        iconName: pane.store.openGroups[entry.modelData.group] ? "chevron-down" : "chevron-right"
                        text: pane.store.openGroups[entry.modelData.group]
                              ? qsTr("收起相似") : qsTr("另有 %1 条相似").arg(entry.modelData.folded)
                        ToolTip.visible: hovered
                        ToolTip.text: qsTr("正文几乎一样的条目收在这里，只是提示音、空格或个别字不同。")
                        // The list gets a new model; keep it where it was rather than back at the top.
                        onClicked: {
                            const y = listing.contentY
                            pane.store.toggleGroup(entry.modelData.group)
                            Qt.callLater(() => listing.contentY = Math.max(listing.originY,
                                Math.min(y, listing.originY + listing.contentHeight - listing.height)))
                        }
                    }
                }
                Button {
                    id: star
                    visible: !pane.store.remote
                             && (entry.row.favorite || entry.hovered || entry.chosen || activeFocus)
                    implicitWidth: 26
                    implicitHeight: 26
                    padding: 0
                    focusPolicy: Qt.StrongFocus
                    Accessible.name: entry.row.favorite ? qsTr("取消收藏") : qsTr("收藏")
                    ToolTip.visible: hovered
                    ToolTip.text: entry.row.favorite ? qsTr("取消收藏") : qsTr("收藏")
                    onClicked: pane.store.toggleFavorite(entry.row)
                    background: Rectangle {
                        radius: Theme.radiusXs
                        color: star.hovered || star.activeFocus ? Theme.fillStrong : "transparent"
                    }
                    contentItem: Text {
                        renderType: Theme.bodyRenderType
                        horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                        text: entry.row.favorite ? "★" : "☆"
                        color: entry.row.favorite ? Theme.yellow : Theme.textSecondary
                        font.pixelSize: 15
                    }
                }
                Text {
                    renderType: Theme.bodyRenderType
                    visible: pane.store.remote && pane.store.isInstalled(entry.item.id)
                    text: "✓ " + qsTr("已在本地")
                    color: Theme.green
                    font.pixelSize: Theme.fs(12)
                }
            }
        }

        ColumnLayout {
            objectName: "dutyCommunityNoMatch"
            visible: listing.count === 0
            x: 18
            y: 22
            width: listing.width - 36
            spacing: 10
            Text {
                renderType: Theme.bodyRenderType
                Layout.fillWidth: true
                wrapMode: Text.Wrap
                text: qsTr("没有匹配的内容")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(13)
            }
            RowLayout {
                spacing: 6
                AppButton { compact: true; visible: pane.store.filterActive; text: qsTr("清除筛选"); onClicked: pane.store.resetFilters() }
                AppButton {
                    compact: true
                    variant: "ghost"
                    visible: !pane.store.remote
                    text: qsTr("去公共共享库找找")
                    onClicked: pane.store.tab = "public"
                }
            }
        }
    }

    Rectangle { visible: bulk.visible; Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
    Rectangle {
        id: bulk
        objectName: "dutyCommunityBulk"
        visible: pane.store.checkedIds.length > 0 && pane.store.mode === "view"
        Layout.fillWidth: true
        implicitHeight: 44
        color: Theme.accentMuted

        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 16
            anchors.rightMargin: 8
            spacing: 6
            Text {
                renderType: Theme.bodyRenderType
                Layout.fillWidth: true
                text: qsTr("已选 %1 项").arg(pane.store.checkedIds.length)
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                font.weight: Font.DemiBold
            }
            AppButton {
                objectName: "dutyCommunityBulkExport"
                compact: true
                visible: !pane.store.remote
                iconName: "file-down"
                text: qsTr("导出")
                onClicked: pane.store.exportRows(pane.store.checkedIds)
            }
            AppButton {
                objectName: "dutyCommunityBulkSubmit"
                compact: true
                visible: !pane.store.remote
                iconName: "arrow-up"
                text: qsTr("投稿")
                onClicked: pane.store.submitRows(pane.store.checkedIds)
            }
            AppButton {
                objectName: "dutyCommunityBulkInstall"
                compact: true
                visible: pane.store.remote
                iconName: "file-input"
                text: qsTr("导入到本地")
                onClicked: pane.store.installAll(pane.store.checkedIds)
            }
            AppButton {
                compact: true
                variant: "ghost"
                implicitWidth: implicitHeight
                iconName: "x"
                Accessible.name: qsTr("取消选择")
                ToolTip.visible: hovered
                ToolTip.text: qsTr("取消选择")
                onClicked: pane.store.basket = []
            }
        }
    }
}
