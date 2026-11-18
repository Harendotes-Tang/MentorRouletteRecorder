pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// The right pane of 副本提醒与导随宏 while creating or editing an entry. The form sits where the
// entry is read, so nothing opens on top of the list.
ColumnLayout {
    id: editor

    required property var store
    property var row: null
    property string type: "macro"
    property int dutyId: 0
    property bool titleMissing: false
    property bool bodyMissing: false
    readonly property bool macro: type === "macro"
    readonly property bool fresh: !!row && row.revision === 0
    readonly property int lineCount: bodyField.text.trim().length
        ? bodyField.text.replace(/\s+$/, "").split("\n").length : 0
    readonly property var regionKeys: ["ALL", "CN", "TC", "GLOBAL"]
    readonly property string regionKey: regionKeys[Math.max(0, region.currentIndex)]
    signal saved(var value)
    signal cancelled()
    spacing: 0

    function edit(value) {
        row = JSON.parse(JSON.stringify(value))
        const item = row.content
        type = item.type
        dutyId = item.content_id
        titleMissing = false
        bodyMissing = false
        titleField.text = item.title
        bodyField.text = item.body
        findDuty.clear()
        region.currentIndex = Math.max(0, regionKeys.indexOf(item.region))
        language.currentIndex = Math.max(0, store.languageKeys.indexOf(item.language))
        boss.text = item.boss
        phase.text = item.phase
        role.text = item.role
        version.text = item.game_version
        tags.text = item.tags.join(", ")
        source.text = item.source
        order.text = String(row.position)
        favorite.checked = row.favorite
        titleField.forceActiveFocus()
    }

    function save() {
        titleMissing = titleField.text.trim().length === 0
        bodyMissing = bodyField.text.trim().length === 0
        if (titleMissing || bodyMissing) return
        const item = row.content
        const updated = Object.assign({}, item, {
            type: type, title: titleField.text,
            body: bodyField.text === item.body.replace(/\r\n/g, "\n") ? item.body : bodyField.text,
            content_id: dutyId, region: regionKey, language: store.languageKeys[language.currentIndex],
            boss: boss.text, phase: phase.text, role: role.text, game_version: version.text,
            tags: tags.text.split(",").map(tag => tag.trim()).filter(tag => tag), source: source.text
        })
        saved({ content: updated, revision: row.revision, position: Number(order.text) || 0, favorite: favorite.checked })
    }

    component Caption: Text {
        renderType: Theme.bodyRenderType
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
    }
    component Problem: Text {
        renderType: Theme.bodyRenderType
        color: Theme.red
        font.pixelSize: Theme.fs(12)
    }

    ScrollView {
        id: scroller
        Layout.fillWidth: true
        Layout.fillHeight: true
        contentWidth: availableWidth
        clip: true

        GridLayout {
            width: scroller.availableWidth - 48
            x: 24
            columns: 4
            columnSpacing: 12
            rowSpacing: 4

            Text {
                objectName: "dutyCommunityEditorHeading"
                renderType: Theme.bodyRenderType
                Layout.columnSpan: 4
                Layout.topMargin: 18
                Layout.bottomMargin: 10
                text: editor.fresh ? (editor.macro ? qsTr("新建宏") : qsTr("新建提醒"))
                                   : (editor.macro ? qsTr("编辑宏") : qsTr("编辑提醒"))
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(18)
                font.bold: true
            }

            Caption { Layout.columnSpan: 2; text: qsTr("类型") }
            Caption { Layout.columnSpan: 2; text: qsTr("副本") }
            SegmentedControl {
                objectName: "dutyCommunityEditorType"
                Layout.columnSpan: 2
                Layout.alignment: Qt.AlignLeft
                implicitWidth: naturalWidth
                // The kind is fixed once the entry exists or links to the other kind.
                enabled: editor.fresh && !!editor.row && editor.row.content.links.length === 0
                options: [{ value: "macro", label: qsTr("宏") }, { value: "reminder", label: qsTr("提醒") }]
                currentValue: editor.type
                onActivated: value => editor.type = value
            }
            RowLayout {
                Layout.columnSpan: 2
                Layout.fillWidth: true
                spacing: 6
                Text {
                    objectName: "dutyCommunityEditorDuty"
                    renderType: Theme.bodyRenderType
                    Layout.fillWidth: true
                    textFormat: Text.PlainText
                    elide: Text.ElideRight
                    text: editor.dutyId > 0
                          ? (Theme.dutyName({ content_id: editor.dutyId, region: editor.regionKey }) || String(editor.dutyId))
                            + (editor.store.inDuty && editor.dutyId === editor.store.duty.content_id ? " · " + qsTr("当前副本") : "")
                          : qsTr("通用")
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(13)
                    font.weight: Font.DemiBold
                }
                AppButton {
                    compact: true
                    variant: "ghost"
                    visible: editor.dutyId > 0
                    text: qsTr("改为通用")
                    onClicked: editor.dutyId = 0
                }
            }

            Item { Layout.columnSpan: 2; implicitHeight: 1 }
            RowLayout {
                Layout.columnSpan: 2
                Layout.fillWidth: true
                Layout.bottomMargin: 8
                spacing: 6
                StyledTextField {
                    id: findDuty
                    Layout.fillWidth: true
                    Layout.preferredWidth: 1
                    placeholderText: qsTr("搜索副本名称")
                    Accessible.name: placeholderText
                }
                StyledComboBox {
                    id: dutyChoices
                    Layout.fillWidth: true
                    Layout.preferredWidth: 1
                    visible: count > 0
                    textRole: "label"
                    valueRole: "content_id"
                    keepsRowAcrossLanguages: false
                    currentIndex: -1
                    displayText: currentIndex < 0 ? qsTr("选择副本") : currentText
                    model: editor.store.service.findDuties(editor.regionKey, findDuty.text)
                    onActivated: editor.dutyId = currentValue
                }
            }

            Caption { Layout.columnSpan: 4; text: qsTr("标题") }
            StyledTextField {
                id: titleField
                objectName: "dutyCommunityEditorTitle"
                Layout.columnSpan: 4
                Layout.fillWidth: true
                maximumLength: 120
                placeholderText: qsTr("起个一眼能认出来的名字")
                onTextEdited: editor.titleMissing = false
            }
            Problem { Layout.columnSpan: 4; visible: editor.titleMissing; text: qsTr("请填写标题") }

            Caption { Layout.topMargin: 8; text: qsTr("BOSS（可不填）") }
            Caption { Layout.topMargin: 8; text: qsTr("阶段（可不填）") }
            Caption { Layout.topMargin: 8; text: qsTr("区服") }
            Caption { Layout.topMargin: 8; text: qsTr("语言") }
            StyledTextField { id: boss; Layout.fillWidth: true; Layout.preferredWidth: 1; maximumLength: 80; placeholderText: qsTr("例如：BOSS 2、尾王") }
            StyledTextField { id: phase; Layout.fillWidth: true; Layout.preferredWidth: 1; maximumLength: 80; placeholderText: qsTr("例如：转场") }
            StyledComboBox {
                id: region
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                keepsRowAcrossLanguages: false
                model: editor.regionKeys.map(key => editor.store.regionLabel(key))
            }
            StyledComboBox {
                id: language
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                keepsRowAcrossLanguages: false
                model: editor.store.languageKeys.map(key => editor.store.languageLabel(key))
            }

            Caption { Layout.columnSpan: 4; Layout.topMargin: 8; text: editor.macro ? qsTr("宏内容") : qsTr("提醒内容") }
            ScrollView {
                Layout.columnSpan: 4
                Layout.fillWidth: true
                Layout.preferredHeight: 190
                StyledTextArea {
                    id: bodyField
                    objectName: "dutyCommunityEditorBody"
                    textFormat: TextEdit.PlainText
                    font.family: editor.macro ? Theme.monoFamily : Theme.bodyFamily
                    placeholderText: editor.macro ? qsTr("一行一条指令，例如：/p 大家好 <se.1>") : qsTr("写下这个副本要注意的事")
                    onTextChanged: editor.bodyMissing = false
                }
            }
            RowLayout {
                Layout.columnSpan: 4
                Layout.fillWidth: true
                Problem { visible: editor.bodyMissing; text: qsTr("请填写正文") }
                Caption {
                    visible: !editor.bodyMissing && editor.macro
                    Layout.fillWidth: true
                    font.pixelSize: Theme.fs(12)
                    text: editor.lineCount > 15 ? qsTr("超过 15 行了，游戏里要拆成两个宏。") : qsTr("游戏里一个宏最多 15 行。")
                }
                Item { Layout.fillWidth: true }
                Caption {
                    objectName: "dutyCommunityEditorLines"
                    visible: editor.macro
                    font.pixelSize: Theme.fs(12)
                    font.weight: editor.lineCount > 15 ? Font.DemiBold : Font.Normal
                    color: editor.lineCount > 15 ? Theme.red : Theme.textSecondary
                    text: qsTr("%1 / 15 行").arg(editor.lineCount)
                }
            }

            Caption { Layout.topMargin: 8; text: qsTr("角色") }
            Caption { Layout.topMargin: 8; text: qsTr("适用游戏版本") }
            Caption { Layout.topMargin: 8; text: qsTr("显示顺序") }
            Item { implicitHeight: 1 }
            StyledTextField { id: role; Layout.fillWidth: true; Layout.preferredWidth: 1; maximumLength: 80 }
            StyledTextField { id: version; Layout.fillWidth: true; Layout.preferredWidth: 1; maximumLength: 80 }
            StyledTextField {
                id: order
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                inputMethodHints: Qt.ImhFormattedNumbersOnly
                validator: IntValidator { bottom: -9999; top: 9999 }
            }
            SelectionCheckBox { id: favorite; text: qsTr("收藏") }

            Caption { Layout.columnSpan: 2; Layout.topMargin: 8; text: qsTr("标签，以逗号分隔") }
            Caption { Layout.columnSpan: 2; Layout.topMargin: 8; text: qsTr("来源") }
            StyledTextField { id: tags; Layout.columnSpan: 2; Layout.fillWidth: true; Layout.preferredWidth: 1 }
            StyledTextField { id: source; Layout.columnSpan: 2; Layout.fillWidth: true; Layout.preferredWidth: 1; maximumLength: 240 }

            Caption {
                Layout.columnSpan: 4
                Layout.topMargin: 8
                visible: links.count > 0
                text: editor.macro ? qsTr("关联的提醒") : qsTr("关联的宏")
            }
            Flow {
                Layout.columnSpan: 4
                Layout.fillWidth: true
                Layout.bottomMargin: 18
                spacing: 12
                Repeater {
                    id: links
                    model: editor.row ? editor.store.localRows.filter(entry => entry.content.type !== editor.type
                                                                               && entry.content.id !== editor.row.content.id) : []
                    delegate: SelectionCheckBox {
                        required property var modelData
                        text: modelData.content.title
                        checked: editor.row.content.links.includes(modelData.content.id)
                        onClicked: {
                            // A new row object, so everything bound to the links follows.
                            const item = editor.row.content
                            const linked = checked ? item.links.concat(modelData.content.id)
                                                   : item.links.filter(id => id !== modelData.content.id)
                            editor.row = Object.assign({}, editor.row, { content: Object.assign({}, item, { links: linked }) })
                        }
                    }
                }
            }
        }
    }

    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
    RowLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 24
        Layout.rightMargin: 24
        Layout.topMargin: 12
        Layout.bottomMargin: 12
        spacing: 8
        AppButton { objectName: "dutyCommunityEditorSave"; variant: "primary"; text: qsTr("保存"); onClicked: editor.save() }
        AppButton { objectName: "dutyCommunityEditorCancel"; text: qsTr("取消"); onClicked: editor.cancelled() }
        Text {
            renderType: Theme.bodyRenderType
            Layout.fillWidth: true
            horizontalAlignment: Text.AlignRight
            elide: Text.ElideRight
            text: qsTr("只保存在这台电脑上，投稿之后才会公开")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }
}
