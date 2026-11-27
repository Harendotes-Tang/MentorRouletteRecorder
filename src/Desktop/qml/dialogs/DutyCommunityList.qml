pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide
import "DutyCommunityScope.js" as Scope

// The middle column of 副本提醒与导随宏: the entries of the range the directory chose
// (store.listEntries) - grouped by BOSS / 阶段 inside a duty, by duty inside a category, flat
// elsewhere - with a tick box per row and, only once something is ticked, the actions that
// take several entries at once. The header holds 全选, the count, the order, the row height
// and 全部收起. On the public tab entries that read alike fold under the first one.
ColumnLayout {
    id: pane

    required property var store
    // 全选 takes the whole range, the rows of folded groups and of 「还有 N 项」 included; only
    // entries folded as similar stay out until their group is opened.
    readonly property var ids: store.listRows.map(entry => entry.row.content.id)
    readonly property var ticked: new Set(store.basket)
    readonly property bool allChecked: ids.length > 0 && ids.every(id => ticked.has(id))
    readonly property color hitColor: Theme.eorzea ? Theme.gold : Theme.accent
    spacing: 0

    function count(value) { return Number(value).toLocaleString(Qt.locale("en_US"), "f", 0) }
    /// The second line of a row: where it belongs, who wrote it and how it starts, or the line
    /// the search hit. StyledText; every piece is escaped.
    function subline(entry) {
        const row = entry.row, item = row.content, term = store.term, parts = []
        const add = text => { if (text) parts.push(Scope.highlight(text, term, hitColor)) }
        if (entry.showDuty) add(store.dutyLabel(item))
        if (entry.showStage) add(store.stageLabel(item))
        add(store.jobsLabel(item.role))
        if (store.remote && item.author) add("@" + item.author.login)
        if (store.sortKey === "used" && store.recent[item.id])
            add(Qt.formatDateTime(new Date(store.recent[item.id]), "MM-dd HH:mm"))
        const hit = term && !String(item.title).toLowerCase().includes(term) ? Scope.snippet(item.body, term) : ""
        if (hit) add(hit)
        else if (!entry.showDuty) add(store.firstLine(item.body))
        return parts.join(" · ")
    }

    RowLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 14
        Layout.rightMargin: 8
        Layout.preferredHeight: 36
        spacing: 6

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
            Layout.leftMargin: 2
            text: pane.store.filterActive && pane.store.scopeRows.length !== pane.store.scopeTotal
                  ? qsTr("%1 / %2 项").arg(pane.count(pane.store.scopeRows.length)).arg(pane.count(pane.store.scopeTotal))
                  : qsTr("%1 项").arg(pane.count(pane.store.scopeRows.length))
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
        Item { Layout.fillWidth: true }
        ActionMenuButton {
            objectName: "dutyCommunitySort"
            compact: true
            variant: "ghost"
            trailingIconName: "arrow-up-down"
            // 最近用过 is its own order.
            enabled: pane.store.activeScope !== "recent"
            text: (pane.store.sortOptions.find(option => option.value === pane.store.sortKey)
                   || pane.store.sortOptions[0]).label
            menuWidth: 200
            Accessible.name: qsTr("排序")
            groups: [{ head: qsTr("排序"), items: pane.store.sortOptions.map(option => (
                { key: option.value, icon: option.value === pane.store.sortKey ? "check" : "minus", label: option.label, sub: "" })) }]
            onPicked: key => pane.store.setSort(key)
        }
        AppButton {
            objectName: "dutyCommunityDense"
            compact: true
            variant: pane.store.dense ? "secondary" : "ghost"
            implicitWidth: implicitHeight
            iconName: "rows-3"
            Accessible.name: qsTr("紧凑行高")
            tip: qsTr("紧凑行高")
            onClicked: pane.store.setDense(!pane.store.dense)
        }
        AppButton {
            objectName: "dutyCommunityFoldAll"
            visible: pane.store.listGrouped
            compact: true
            variant: "ghost"
            implicitWidth: implicitHeight
            iconName: pane.store.anyGroupOpen ? "chevrons-down-up" : "chevrons-up-down"
            Accessible.name: pane.store.anyGroupOpen ? qsTr("全部收起") : qsTr("全部展开")
            tip: Accessible.name
            onClicked: pane.store.foldAll()
        }
    }
    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

    // A group's heading: a band across the list; a click folds or opens the group.
    component GroupHead: Button {
        id: head
        property var entry: ({})
        implicitHeight: 30
        padding: 0
        focusPolicy: Qt.StrongFocus
        Accessible.name: (entry.label || "") + " " + (entry.count || 0)
        onClicked: pane.store.toggleListGroup(entry.key)
        background: Rectangle {
            color: Qt.tint(Theme.surface, Qt.rgba(Theme.textPrimary.r, Theme.textPrimary.g, Theme.textPrimary.b,
                                                    head.hovered || head.activeFocus ? 0.11 : 0.07))
            Rectangle { width: parent.width; height: 1; color: Theme.border }
            Rectangle { width: parent.width; height: 1; anchors.bottom: parent.bottom; color: Theme.border }
        }
        contentItem: RowLayout {
            spacing: 8
            Image {
                Layout.leftMargin: 13
                Layout.preferredWidth: 12
                Layout.preferredHeight: 12
                source: Lucide.source(head.entry.open ? "chevron-down" : "chevron-right", Theme.textSecondary)
                sourceSize: Qt.size(24, 24)
            }
            Text {
                renderType: Theme.bodyRenderType
                Layout.fillWidth: true
                textFormat: Text.PlainText
                elide: Text.ElideRight
                text: head.entry.label || ""
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                font.weight: Font.DemiBold
            }
            Text {
                renderType: Theme.bodyRenderType
                Layout.rightMargin: 14
                text: pane.count(head.entry.count || 0)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }
        }
    }

    Item {
        Layout.fillWidth: true
        Layout.fillHeight: true

        ListView {
            id: listing
            objectName: "dutyCommunityRows"
            anchors.fill: parent
            bottomMargin: 6
            clip: true
            ScrollBar.vertical: ScrollBar {}

            // A new range, search, filter or order starts at the top. Anything else that
            // rebuilds the rows - folding a group, 显示更多, a star, a copy - leaves the list
            // where it was: a new model would otherwise put it back at the top. An entry asked
            // for by name (after saving, 在本地查看) is scrolled into view.
            readonly property string viewKey: [pane.store.tab, pane.store.activeScope, pane.store.term, pane.store.sortKey,
                                               JSON.stringify(pane.store.filters)].join("|")
            property string shownKey: ""
            function refresh() {
                const stay = shownKey === viewKey, y = contentY - originY
                shownKey = viewKey
                model = pane.store.listEntries
                if (stay) contentY = originY + Math.max(0, Math.min(y, contentHeight - height + bottomMargin))
                else positionViewAtBeginning()
            }
            Component.onCompleted: refresh()
            Connections {
                target: pane.store
                function onListEntriesChanged() { listing.refresh() }
                function onRevealTickChanged() {
                    Qt.callLater(() => {
                        const at = pane.store.listEntries.findIndex(entry => entry.kind === "row"
                                                                           && entry.row.content.id === pane.store.selectedId)
                        if (at >= 0) listing.positionViewAtIndex(at, ListView.Contain)
                    })
                }
            }

            delegate: Item {
                id: line
                required property var modelData
                required property int index
                readonly property var entry: modelData
                readonly property bool isRow: entry.kind === "row"
                width: listing.width
                height: entry.kind === "head" ? 30 + (index > 0 ? 4 : 0)
                      : isRow ? card.implicitHeight + (pane.store.dense ? 0 : 2) + (index === 0 ? 6 : 0)
                      : tail.implicitHeight + 8

                GroupHead {
                    objectName: "dutyCommunityGroupHead"
                    visible: line.entry.kind === "head"
                    width: parent.width
                    anchors.bottom: parent.bottom
                    entry: line.entry
                }

                ItemDelegate {
                    id: card
                    objectName: "dutyCommunityRow"
                    visible: line.isRow
                    readonly property var row: line.isRow ? line.entry.row : null
                    readonly property var item: row ? row.content : ({ id: "", title: "", type: "macro" })
                    readonly property bool chosen: line.isRow && pane.store.currentId === item.id
                    // An opened group's other entries sit indented under the first.
                    x: line.isRow && line.entry.member ? 26 : 6
                    y: line.index === 0 ? 6 : 0
                    width: parent.width - x - 6
                    topPadding: pane.store.dense ? 3 : 8
                    bottomPadding: pane.store.dense ? 3 : 8
                    leftPadding: 8
                    rightPadding: 6
                    focusPolicy: Qt.StrongFocus
                    Accessible.name: item.title
                    onClicked: pane.store.select(item.id)

                    background: Rectangle {
                        radius: Theme.radiusS
                        color: card.chosen ? Theme.accentMuted : card.hovered || card.activeFocus ? Theme.fill : "transparent"
                        Rectangle {
                            visible: card.chosen
                            width: 2
                            height: parent.height
                            color: Theme.eorzea ? Theme.gold : Theme.accent
                        }
                    }
                    contentItem: RowLayout {
                        spacing: 8

                        SelectionCheckBox {
                            checked: pane.ticked.has(card.item.id)
                            Accessible.name: qsTr("选择「%1」").arg(card.item.title)
                            onClicked: pane.store.toggleChecked(card.item.id)
                        }
                        // Two lines; one in the compact height, where the tag keeps its icon only.
                        GridLayout {
                            Layout.fillWidth: true
                            columns: pane.store.dense ? 3 : 1
                            rowSpacing: 3
                            columnSpacing: 10
                            RowLayout {
                                Layout.fillWidth: !pane.store.dense
                                Layout.maximumWidth: pane.store.dense ? 200 : -1
                                spacing: 8
                                ContentTypeTag { type: card.item.type; iconOnly: pane.store.dense }
                                Text {
                                    objectName: "dutyCommunityRowTitle"
                                    renderType: Theme.bodyRenderType
                                    Layout.fillWidth: true
                                    textFormat: Text.StyledText
                                    elide: Text.ElideRight
                                    text: Scope.highlight(card.item.title, pane.store.term, pane.hitColor)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(13)
                                    font.weight: Font.DemiBold
                                }
                                Image {
                                    visible: pane.store.remote && card.item.status === "verified"
                                    Layout.preferredWidth: 13
                                    Layout.preferredHeight: 13
                                    source: Lucide.source("shield-check", Theme.green)
                                    sourceSize: Qt.size(26, 26)
                                    Accessible.name: qsTr("已核验")
                                }
                            }
                            Text {
                                objectName: "dutyCommunityRowSub"
                                renderType: Theme.bodyRenderType
                                Layout.fillWidth: true
                                textFormat: Text.StyledText
                                elide: Text.ElideRight
                                text: line.isRow ? pane.subline(line.entry) : ""
                                visible: text.length > 0
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(12)
                            }
                            AppButton {
                                objectName: "dutyCommunitySimilarToggle"
                                visible: line.isRow && line.entry.folded > 0
                                compact: true
                                variant: "ghost"
                                iconName: line.isRow && pane.store.openGroups[line.entry.group] ? "chevron-down" : "chevron-right"
                                text: line.isRow && pane.store.openGroups[line.entry.group]
                                      ? qsTr("收起相似") : qsTr("另有 %1 条相似").arg(line.isRow ? line.entry.folded : 0)
                                tip: qsTr("正文几乎一样的条目收在这里，只是提示音、空格或个别字不同。")
                                onClicked: pane.store.toggleGroup(line.entry.group)
                            }
                        }
                        Button {
                            id: star
                            visible: !pane.store.remote && !!card.row
                                     && (card.row.favorite || card.hovered || card.chosen || activeFocus)
                            implicitWidth: pane.store.dense ? 22 : 26
                            implicitHeight: pane.store.dense ? 22 : 26
                            padding: 0
                            focusPolicy: Qt.StrongFocus
                            readonly property bool on: !!card.row && card.row.favorite === true
                            Accessible.name: on ? qsTr("取消收藏") : qsTr("收藏")
                            AppToolTip { visible: star.hovered; text: star.on ? qsTr("取消收藏") : qsTr("收藏") }
                            onClicked: pane.store.toggleFavorite(card.row)
                            background: Rectangle {
                                radius: Theme.radiusXs
                                color: star.hovered || star.activeFocus ? Theme.fillStrong : "transparent"
                            }
                            contentItem: Text {
                                renderType: Theme.bodyRenderType
                                horizontalAlignment: Text.AlignHCenter
                                verticalAlignment: Text.AlignVCenter
                                text: star.on ? "★" : "☆"
                                color: star.on ? Theme.yellow : Theme.textSecondary
                                font.pixelSize: 15
                            }
                        }
                        Text {
                            renderType: Theme.bodyRenderType
                            visible: pane.store.remote && pane.store.isInstalled(card.item.id)
                            text: pane.store.dense ? "✓" : "✓ " + qsTr("已在本地")
                            color: Theme.green
                            font.pixelSize: Theme.fs(12)
                            Accessible.name: qsTr("已在本地")
                        }
                    }
                }

                // What a group, the filters or the range leave out, with the way to it.
                RowLayout {
                    id: tail
                    visible: !line.isRow && line.entry.kind !== "head"
                    x: 16
                    y: 4
                    width: parent.width - 26
                    spacing: 8
                    Text {
                        renderType: Theme.bodyRenderType
                        Layout.fillWidth: true
                        elide: Text.ElideRight
                        text: line.entry.kind === "more" ? (line.entry.go ? "" : qsTr("还有 %1 项").arg(pane.count(line.entry.rest)))
                            : line.entry.kind === "hint" ? qsTr("另有 %1 项是其它区服或语言的").arg(pane.count(pane.store.hiddenByPlace))
                            : qsTr("「全部」里共有 %1 项匹配").arg(pane.count(pane.store.scopeCounts.all))
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                    }
                    AppButton {
                        objectName: line.entry.kind === "more" ? "dutyCommunityShowMore"
                                  : line.entry.kind === "hint" ? "dutyCommunityShowEveryPlace" : "dutyCommunitySeeAll"
                        compact: true
                        variant: "ghost"
                        text: line.entry.kind === "more"
                              ? (line.entry.go ? qsTr("查看全部 %1 项").arg(pane.count(line.entry.count)) : qsTr("显示更多"))
                              : line.entry.kind === "hint" ? qsTr("也显示出来") : qsTr("去看看")
                        onClicked: line.entry.kind === "more" ? pane.store.showMore(line.entry)
                                 : line.entry.kind === "hint" ? pane.store.showEveryPlace() : pane.store.pickScope("all")
                    }
                }
            }

            ColumnLayout {
                objectName: "dutyCommunityNoMatch"
                visible: pane.store.scopeRows.length === 0
                x: 18
                y: 22
                width: listing.width - 36
                spacing: 10
                Text {
                    objectName: "dutyCommunityEmptyText"
                    renderType: Theme.bodyRenderType
                    Layout.fillWidth: true
                    wrapMode: Text.Wrap
                    text: pane.store.emptyText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(13)
                }
                Flow {
                    Layout.fillWidth: true
                    spacing: 6
                    AppButton { compact: true; visible: pane.store.resettable; text: qsTr("清除筛选"); onClicked: pane.store.resetFilters() }
                    AppButton {
                        compact: true
                        visible: pane.store.activeScope !== "all" && pane.store.scopeCounts.all > 0
                        text: qsTr("在「全部」里看（%1）").arg(pane.count(pane.store.scopeCounts.all))
                        onClicked: pane.store.pickScope("all")
                    }
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

        // The heading of the group at the top of the view stays there while its rows scroll.
        GroupHead {
            id: pinned
            readonly property int topIndex: listing.contentHeight > 0 ? listing.indexAt(10, listing.contentY + 1) : -1
            readonly property var held: {
                const entries = pane.store.listEntries
                if (topIndex < 0 || topIndex >= entries.length || !pane.store.listGrouped) return null
                const top = entries[topIndex]
                if (top.kind === "head") return null
                return entries.find(entry => entry.kind === "head" && entry.key === top.key) || null
            }
            visible: !!held
            width: listing.width
            entry: held || ({})
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
                text: qsTr("已选 %1 项").arg(pane.count(pane.store.checkedIds.length))
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
                iconName: "send"
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
                tip: qsTr("取消选择")
                onClicked: pane.store.basket = []
            }
        }
    }
}
