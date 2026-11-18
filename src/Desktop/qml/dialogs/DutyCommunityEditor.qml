pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

Dialog {
    id: editor
    required property var rows
    required property var service
    property var row: null
    signal saved(var value)
    modal: true
    anchors.centerIn: parent
    width: Math.min(680, parent.width)
    title: row && row.content.type === "macro" ? qsTr("编辑宏") : qsTr("编辑提醒")
    standardButtons: Dialog.Save | Dialog.Cancel

    function edit(value) {
        row = JSON.parse(JSON.stringify(value))
        const item = row.content
        titleField.text = item.title
        bodyField.text = item.body
        duty.value = item.content_id
        findDuty.clear()
        region.currentIndex = region.model.indexOf(item.region)
        language.currentIndex = language.model.indexOf(item.language)
        boss.text = item.boss
        phase.text = item.phase
        role.text = item.role
        version.text = item.game_version
        tags.text = item.tags.join(", ")
        source.text = item.source
        order.value = row.position
        favorite.checked = row.favorite
        open()
    }

    contentItem: ScrollView {
        implicitHeight: Math.min(490, editor.parent.height - 100)
        contentWidth: availableWidth
        clip: true
        ColumnLayout {
            width: parent.width
            TextField { id: titleField; Layout.fillWidth: true; placeholderText: qsTr("标题"); maximumLength: 120 }
            TextArea {
                id: bodyField
                Layout.fillWidth: true
                Layout.preferredHeight: 120
                placeholderText: qsTr("正文")
                textFormat: TextEdit.PlainText
                wrapMode: TextEdit.Wrap
            }
            Flow {
                Layout.fillWidth: true
                spacing: 6
                Label { text: qsTr("副本 ID（0 = 通用）"); padding: 6 }
                SpinBox { id: duty; from: 0; to: 65535; editable: true }
                ComboBox { id: region; model: ["ALL", "CN", "TC", "GLOBAL"] }
                ComboBox { id: language; model: ["zh-Hans", "zh-Hant", "en", "ja"] }
            }
            RowLayout {
                Layout.fillWidth: true
                TextField {
                    id: findDuty
                    placeholderText: qsTr("搜索副本名称")
                }
                ComboBox {
                    Layout.fillWidth: true
                    textRole: "label"
                    valueRole: "content_id"
                    visible: count > 0
                    currentIndex: -1
                    model: editor.service.findDuties(region.currentText, findDuty.text)
                    onActivated: duty.value = currentValue
                }
            }
            Label {
                visible: duty.value > 0
                textFormat: Text.PlainText
                text: Theme.dutyName({ content_id: duty.value, region: region.currentText })
            }
            Flow {
                Layout.fillWidth: true
                spacing: 6
                TextField { id: boss; placeholderText: qsTr("BOSS"); maximumLength: 80 }
                TextField { id: phase; placeholderText: qsTr("阶段"); maximumLength: 80 }
                TextField { id: role; placeholderText: qsTr("角色"); maximumLength: 80 }
                TextField { id: version; placeholderText: qsTr("适用游戏版本"); maximumLength: 80 }
            }
            TextField { id: tags; Layout.fillWidth: true; placeholderText: qsTr("标签，以逗号分隔") }
            TextField { id: source; Layout.fillWidth: true; placeholderText: qsTr("来源"); maximumLength: 240 }
            RowLayout {
                Label { text: qsTr("显示顺序") }
                SpinBox { id: order; from: -9999; to: 9999; editable: true }
                CheckBox { id: favorite; text: qsTr("收藏") }
            }
            Label { text: qsTr("关联项") }
            Repeater {
                model: editor.row ? editor.rows.filter(row => row.content.type !== editor.row.content.type) : []
                delegate: CheckBox {
                    required property var modelData
                    text: modelData.content.title
                    checked: editor.row.content.links.includes(modelData.content.id)
                    onToggled: {
                        const item = editor.row.content
                        item.links = checked ? item.links.concat(modelData.content.id)
                                             : item.links.filter(id => id !== modelData.content.id)
                    }
                }
            }
        }
    }

    onAccepted: {
        const item = row.content
        const updated = Object.assign({}, item, {
            title: titleField.text, body: bodyField.text === item.body.replace(/\r\n/g, "\n") ? item.body : bodyField.text,
            content_id: duty.value, region: region.currentText, language: language.currentText,
            boss: boss.text, phase: phase.text, role: role.text, game_version: version.text,
            tags: tags.text.split(",").map(tag => tag.trim()).filter(tag => tag), source: source.text
        })
        saved({ content: updated, revision: row.revision, position: order.value, favorite: favorite.checked })
    }
}
