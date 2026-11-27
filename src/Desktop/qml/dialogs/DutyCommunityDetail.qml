pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// The right pane of 副本提醒与导随宏 while reading an entry: what it is for, its text with the
// macro's line numbers, and the actions on this one entry.
ColumnLayout {
    id: pane

    required property var store
    readonly property var row: store.current
    readonly property var item: row ? row.content : null
    readonly property bool macro: !!item && item.type === "macro"
    readonly property var lines: item ? item.body.replace(/\r\n/g, "\n").replace(/\s+$/, "").split("\n") : []
    readonly property bool installed: !!item && store.remote && store.isInstalled(item.id)
    // While entries are ticked, 导出 and 投稿 here take them, as the list's bar does: the owner
    // ticked two, pressed this 投稿 and sent the open entry alone (2026-11-23).
    readonly property int ticked: store.checkedIds.length
    readonly property var actionIds: ticked > 0 ? store.checkedIds : item ? [item.id] : []
    // 撤回发布 takes, of the same entries, those this library published that are still live.
    readonly property var withdrawIds: actionIds.filter(id => store.withdrawable(id))
    // The public copy of an entry published from here, for the line that says it is public.
    readonly property var publication: item && !store.remote ? store.ownPublication(item.id) : null
    // An installed copy whose public entry its author withdrew: the copy stays, and says so.
    readonly property bool sourceWithdrawn: !store.remote && !publication && !!sourceItem && sourceItem.status === "withdrawn"
    // An installed copy whose public entry is gone from the loaded library: a maintainer removed it,
    // or it was withdrawn 30 days ago (tools/shared-calibration/community_publish.py, purge).
    readonly property bool sourceRemoved: !store.remote && !publication && !!row && !!row.upstream && !sourceItem
        && store.publicRows.length > 0
    readonly property var sourceItem: store.source
    readonly property bool sourceNewer: !store.remote && !!sourceItem && !!row && !!row.upstream
        && sourceItem.status !== "withdrawn" && sourceItem.revision > row.upstream.revision
    readonly property var meta: {
        if (!item) return []
        const author = item.author || (row.upstream && row.upstream.author)
        const license = item.license || (row.upstream && row.upstream.license)
        const used = !store.remote && store.recent[item.id]
        // Only what is filled in takes a place (Design: 没填的「职业」「游戏版本」不显示).
        const pairs = [
            [qsTr("副本"), store.dutyLabel(item)],
            [qsTr("BOSS / 阶段"), store.stageLabel(item)],
            [qsTr("职业"), store.jobsLabel(item.role)],
            [qsTr("游戏版本"), store.versionLabel(item.game_version)],
            [qsTr("区服"), store.regionLabel(item.region)],
            [qsTr("语言"), store.languageLabel(item.language)],
            // Free text of up to 240 characters wraps instead of being cut (audit 2026-11-26 RE-8).
            [qsTr("标签"), item.tags.join(qsTr("、")), "", true],
            // The stored text where there is one: it is published with the entry.
            [qsTr("来源"), store.remote || item.source ? item.source : (row.upstream ? qsTr("共享库") : qsTr("自己写的")),
             "", true],
            [qsTr("作者"), author ? "@" + author.login : ""],
            [qsTr("状态"), item.status ? store.statusText(item.status) : "",
             item.status === "verified" ? qsTr("维护者核对过内容和适用范围")
             : item.status === "community" ? qsTr("还没有人核对过，用之前自己先看一遍") : ""],
            [qsTr("许可"), store.licenseCode(license), store.licenseDetail(license)],
            [qsTr("共享库"), sourceRemoved ? qsTr("已移除") : sourceWithdrawn ? store.statusText("withdrawn") : !publication ? ""
                : publication.status === "withdrawn" ? store.statusText("withdrawn") : qsTr("已发布"),
             sourceRemoved ? qsTr("共享库里已经没有这一条（作者撤回 30 天后或维护者删除后会移除），你的本地副本照常保留")
             : sourceWithdrawn ? qsTr("作者已从共享库撤回这一条，别人不能再导入；你的本地副本照常保留")
             : publication ? qsTr("以 @%1 的名义公开，只有这个 GitHub 账号能撤回").arg(publication.author.login) : ""],
            [qsTr("投稿"), item.issue ? "#" + item.issue : ""],
            [qsTr("更新于"), day(item.updated_at)],
            [qsTr("上次使用"), used ? Qt.formatDateTime(new Date(used), "MM-dd HH:mm") : ""]
        ]
        return pairs.filter(pair => pair[1]).map(pair => ({ key: pair[0], value: String(pair[1]), tip: pair[2] || "",
                                                            wrap: pair[3] === true }))
    }
    spacing: 0

    /// A time as the day it falls on in this computer's time zone; other text as it is.
    function day(value) {
        const time = new Date(value)
        return value && !isNaN(time.getTime()) ? Qt.formatDate(time, "yyyy-MM-dd") : String(value || "")
    }
    function escaped(text) {
        return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/ /g, "&nbsp;")
    }
    /// One macro line as StyledText: the command and the <placeholders> in their own colours.
    /// Everything the player wrote is escaped first, so it can never act as markup.
    function lineMarkup(line) {
        const parts = /^(\/[^\s<]+)(.*)$/.exec(line)
        const command = parts ? '<font color="' + Theme.blue + '"><b>' + escaped(parts[1]) + "</b></font>" : ""
        const rest = (parts ? parts[2] : line).split(/(<[^<>\s]+>)/).map(piece =>
            /^<[^<>\s]+>$/.test(piece) ? '<font color="' + Theme.teal + '">' + escaped(piece) + "</font>" : escaped(piece))
        return command + rest.join("")
    }

    Text {
        renderType: Theme.bodyRenderType
        visible: !pane.item
        Layout.fillWidth: true
        Layout.fillHeight: true
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        text: qsTr("在左边选一条查看")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(13)
    }

    ScrollView {
        id: scroller
        visible: !!pane.item
        Layout.fillWidth: true
        Layout.fillHeight: true
        contentWidth: availableWidth
        clip: true

        ColumnLayout {
            width: scroller.availableWidth
            spacing: 14

            RowLayout {
                Layout.fillWidth: true
                Layout.leftMargin: 24
                Layout.rightMargin: 24
                Layout.topMargin: 18
                spacing: 10
                ContentTypeTag { Layout.alignment: Qt.AlignTop; Layout.topMargin: 3; type: pane.item ? pane.item.type : "macro" }
                Text {
                    objectName: "dutyCommunityDetailTitle"
                    renderType: Theme.bodyRenderType
                    Layout.fillWidth: true
                    textFormat: Text.PlainText
                    wrapMode: Text.Wrap
                    text: pane.item ? pane.item.title : ""
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(18)
                    font.bold: true
                }
                AppButton {
                    objectName: "dutyCommunityDetailFavorite"
                    visible: !pane.store.remote
                    compact: true
                    variant: "ghost"
                    text: pane.row && pane.row.favorite ? "★ " + qsTr("已收藏") : "☆ " + qsTr("收藏")
                    onClicked: pane.store.toggleFavorite(pane.row)
                }
            }

            Flow {
                Layout.fillWidth: true
                Layout.leftMargin: 24
                Layout.rightMargin: 24
                spacing: 10
                Repeater {
                    model: pane.meta
                    delegate: Column {
                        id: metaCell
                        required property var modelData
                        rightPadding: 18
                        spacing: 2
                        HoverHandler { id: over }
                        AppToolTip { visible: over.hovered && metaCell.modelData.tip.length > 0; text: metaCell.modelData.tip }
                        Text {
                            renderType: Theme.bodyRenderType
                            text: parent.modelData.key
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(11)
                        }
                        Text {
                            renderType: Theme.bodyRenderType
                            textFormat: Text.PlainText
                            width: Math.min(implicitWidth, 300)
                            wrapMode: parent.modelData.wrap ? Text.Wrap : Text.NoWrap
                            elide: parent.modelData.wrap ? Text.ElideNone : Text.ElideRight
                            text: parent.modelData.value
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(13)
                            font.weight: Font.DemiBold
                        }
                    }
                }
            }

            InsetBox {
                objectName: "dutyCommunityBody"
                Layout.fillWidth: true
                Layout.leftMargin: 24
                Layout.rightMargin: 24
                implicitHeight: bodyColumn.implicitHeight
                border.width: 1

                ColumnLayout {
                    id: bodyColumn
                    anchors.left: parent.left
                    anchors.right: parent.right
                    spacing: 0

                    RowLayout {
                        Layout.fillWidth: true
                        Layout.leftMargin: 12
                        Layout.rightMargin: 12
                        Layout.preferredHeight: 32
                        Text {
                            renderType: Theme.bodyRenderType
                            Layout.fillWidth: true
                            text: pane.macro ? qsTr("宏内容") : qsTr("提醒内容")
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                        }
                        Text {
                            objectName: "dutyCommunityLineCount"
                            renderType: Theme.bodyRenderType
                            visible: pane.macro
                            text: qsTr("%1 / 15 行").arg(pane.lines.length)
                            color: pane.lines.length > 15 ? Theme.red : Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                            font.weight: pane.lines.length > 15 ? Font.DemiBold : Font.Normal
                        }
                    }
                    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

                    // A reminder is prose; a macro is a numbered listing in the mono face.
                    TextEdit {
                        visible: !pane.macro
                        Layout.fillWidth: true
                        Layout.margins: 12
                        readOnly: true
                        selectByMouse: true
                        textFormat: TextEdit.PlainText
                        wrapMode: TextEdit.Wrap
                        text: pane.item && !pane.macro ? pane.item.body : ""
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(13)
                    }
                    ColumnLayout {
                        visible: pane.macro
                        Layout.fillWidth: true
                        Layout.topMargin: 10
                        Layout.bottomMargin: 10
                        spacing: 4
                        Repeater {
                            model: pane.macro ? pane.lines : []
                            delegate: RowLayout {
                                id: line
                                required property string modelData
                                required property int index
                                Layout.fillWidth: true
                                spacing: 12
                                Text {
                                    Layout.preferredWidth: 28
                                    Layout.alignment: Qt.AlignTop
                                    horizontalAlignment: Text.AlignRight
                                    text: String(line.index + 1)
                                    color: Theme.textMuted
                                    font.family: Theme.monoFamily
                                    font.pixelSize: 12
                                }
                                Text {
                                    Layout.fillWidth: true
                                    Layout.rightMargin: 14
                                    textFormat: Text.StyledText
                                    wrapMode: Text.WrapAnywhere
                                    text: pane.lineMarkup(line.modelData)
                                    color: Theme.textPrimary
                                    font.family: Theme.monoFamily
                                    font.pixelSize: 13
                                }
                            }
                        }
                    }
                }
            }

            Text {
                renderType: Theme.bodyRenderType
                visible: pane.macro
                Layout.fillWidth: true
                Layout.leftMargin: 24
                Layout.rightMargin: 24
                wrapMode: Text.Wrap
                text: qsTr("复制后粘贴到游戏的「用户宏」里；一个宏最多 15 行。")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            // What the design keeps out of the footer: links to the other kind, and the
            // entry's relation to the public version it came from.
            Flow {
                Layout.fillWidth: true
                Layout.leftMargin: 24
                Layout.rightMargin: 24
                Layout.bottomMargin: 18
                spacing: 6

                Repeater {
                    // A link to an entry the public library no longer lists (withdrawn) is left out.
                    model: !pane.item ? [] : pane.store.remote
                        ? pane.item.links.filter(link => pane.store.rows.some(row => row.content.id === link)) : pane.item.links
                    delegate: AppButton {
                        required property string modelData
                        readonly property var target: pane.store.rows.find(row => row.content.id === modelData)
                        compact: true
                        text: qsTr("关联项") + " · " + (target ? target.content.title : modelData)
                        enabled: !!target
                        onClicked: pane.store.openLinked(modelData)
                    }
                }
                AppButton {
                    objectName: "dutyCommunityMakeMacro"
                    compact: true
                    visible: !pane.store.remote && !!pane.item && !pane.macro
                    text: qsTr("从提醒生成宏")
                    onClicked: pane.store.editNew("macro", pane.item)
                }
                AppButton {
                    compact: true
                    visible: !pane.store.remote && !!pane.row && !!pane.row.upstream
                    text: qsTr("另存个人版本")
                    onClicked: pane.store.forkCurrent()
                }
                AppButton {
                    compact: true
                    variant: "primary"
                    visible: pane.sourceNewer
                    text: qsTr("对比来源更新")
                    onClicked: pane.store.compareSource()
                }
                AppButton {
                    objectName: "dutyCommunityWithdraw"
                    compact: true
                    visible: pane.withdrawIds.length > 0
                    text: pane.ticked > 0 ? qsTr("从共享库撤回 %1 项").arg(pane.withdrawIds.length)
                                          : qsTr("从共享库撤回")
                    onClicked: pane.store.withdrawRows(pane.withdrawIds)
                }
            }
        }
    }

    Rectangle { visible: !!pane.item; Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
    RowLayout {
        visible: !!pane.item
        Layout.fillWidth: true
        Layout.leftMargin: 24
        Layout.rightMargin: 24
        Layout.topMargin: 12
        Layout.bottomMargin: 12
        spacing: 8

        AppButton {
            objectName: "dutyCommunityCopy"
            variant: "primary"
            readonly property string copyMode: pane.macro ? "macro" : "reminder"
            readonly property bool copied: !!pane.item && pane.store.copiedKey === pane.item.id + copyMode
            iconName: copied ? "check" : "copy"
            text: copied ? qsTr("已复制") : qsTr("复制")
            onClicked: pane.store.copyBody(copyMode)
        }
        AppButton {
            objectName: "dutyCommunityCopyChat"
            visible: pane.macro && pane.lines.length === 1
            readonly property bool copied: !!pane.item && pane.store.copiedKey === pane.item.id + "chat"
            text: copied ? qsTr("已复制") : qsTr("复制队聊／团聊消息")
            onClicked: pane.store.copyBody("chat")
        }
        AppButton {
            objectName: "dutyCommunityEdit"
            visible: !pane.store.remote && !pane.store.confirmingDelete
            iconName: "notebook-pen"
            text: qsTr("编辑")
            onClicked: pane.store.startEdit(pane.row)
        }
        AppButton {
            objectName: "dutyCommunityInstall"
            visible: pane.store.remote && !pane.installed
            enabled: !!pane.item && pane.item.status !== "withdrawn"
            iconName: "file-input"
            text: qsTr("导入到本地")
            onClicked: pane.store.installAll([pane.item.id])
        }
        Text {
            renderType: Theme.bodyRenderType
            visible: pane.installed
            text: "✓ " + qsTr("已在本地")
            color: Theme.green
            font.pixelSize: Theme.fs(12)
        }
        AppButton {
            visible: pane.installed
            variant: "ghost"
            text: qsTr("在本地查看")
            onClicked: {
                const id = pane.item.id
                const local = pane.store.localRows.find(row => row.content.id === id || (row.upstream && row.upstream.id === id))
                pane.store.showLocal(local ? local.content.id : "")
            }
        }
        Item { Layout.fillWidth: true }

        Text {
            renderType: Theme.bodyRenderType
            visible: pane.store.confirmingDelete
            text: qsTr("删除后不能恢复，确定删除？")
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(13)
        }
        AppButton {
            objectName: "dutyCommunityConfirmDelete"
            visible: pane.store.confirmingDelete
            text: qsTr("确定删除")
            onClicked: pane.store.removeCurrent()
        }
        AppButton {
            visible: pane.store.confirmingDelete
            variant: "ghost"
            text: qsTr("取消")
            onClicked: pane.store.confirmingDelete = false
        }
        AppButton {
            objectName: "dutyCommunityExport"
            visible: !pane.store.remote && !pane.store.confirmingDelete
            variant: "ghost"
            iconName: "file-down"
            text: pane.ticked > 0 ? qsTr("导出 %1 项").arg(pane.ticked) : qsTr("导出")
            onClicked: pane.store.exportRows(pane.actionIds)
        }
        AppButton {
            objectName: "dutyCommunitySubmit"
            visible: !pane.store.remote && !pane.store.confirmingDelete
            variant: "ghost"
            iconName: "send"
            text: pane.ticked > 0 ? qsTr("投稿 %1 项").arg(pane.ticked) : qsTr("投稿")
            onClicked: pane.store.submitRows(pane.actionIds)
        }
        AppButton {
            objectName: "dutyCommunityDelete"
            visible: !pane.store.remote && !pane.store.confirmingDelete
            variant: "ghost"
            text: qsTr("删除")
            onClicked: pane.store.confirmingDelete = true
        }
    }
}
