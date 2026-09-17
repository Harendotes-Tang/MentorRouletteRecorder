import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 设置 · 播报: the voice (engine, rate, volume), the 在线语音 panel while an
// online voice is selected, and the five templates. A local voice never reaches
// the Collector; an online one is synthesized by it
// (docs/ui-design.md §4.5, docs/privacy-boundary.md §8.3).
ColumnLayout {
    id: tab

    Layout.fillWidth: true
    spacing: 16

    // The page is also loaded by tests with a Tts stub that may predate the
    // voice list, so every newer member is read defensively.
    readonly property var voiceRows: (typeof Tts !== "undefined" && Tts && Tts.voices)
                                     ? Tts.voices : []
    readonly property string voiceIdValue: (typeof Tts !== "undefined" && Tts && Tts.voiceId)
                                           ? Tts.voiceId : ""
    readonly property int localVoiceCount: {
        let count = 0
        for (let i = 0; i < tab.voiceRows.length; ++i) {
            if (tab.voiceRows[i].group !== "online")
                ++count
        }
        return count
    }
    readonly property bool engineReady: typeof Tts !== "undefined" && !!Tts && Tts.available
                                        && tab.localVoiceCount > 0
    // App.speech: the Collector's online-speech settings (never a key).
    readonly property var speech: (typeof App !== "undefined" && App && App.speech) ? App.speech : null
    readonly property bool onlineOffered: !!tab.speech && tab.speech.loaded && tab.speech.supported
    readonly property bool onlineSelected: tab.onlineOffered && tab.isOnline(tab.voiceIdValue)
    readonly property bool onlineConfirmed: typeof Settings !== "undefined" && !!Settings
                                            && Settings.ttsOnlineConfirmed === true
    readonly property string onlineHost: tab.onlineSelected && tab.speech.configured
                                         && tab.speech.provider === tab.speech.providerOfVoiceId(tab.voiceIdValue)
                                         ? tab.speech.targetHost : ""

    function isOnline(id) {
        return typeof id === "string" && (id.indexOf("azure:") === 0 || id.indexOf("openai:") === 0)
    }

    function labelOfVoice(id) {
        const index = tab.indexOfVoice(id)
        return index >= 0 ? tab.voiceRows[index].label : id
    }

    function chooseVoice(id) {
        if (tab.isOnline(id) && !tab.onlineConfirmed) {
            confirmDialog.ask(id, tab.labelOfVoice(id))
            return
        }
        Tts.setVoice(id)
    }

    function indexOfVoice(id) {
        for (let i = 0; i < tab.voiceRows.length; ++i) {
            if (tab.voiceRows[i].id === id)
                return i
        }
        return -1
    }

    // --------------------------------------------------------- 本地语音 --
    SettingsPanel {
        objectName: "speechSettingsCard"

        SettingsRow {
            objectName: "speechHeaderRow"
            // The kicker sits inside the row, as in the prototype; 「本地」 only
            // while the voice really is local.
            label: tab.onlineSelected ? qsTr("语音播报") : qsTr("本地语音播报")
            labelIsKicker: true
            description: !tab.onlineSelected ? qsTr("使用系统语音引擎，不联网")
                         : tab.onlineHost.length > 0 ? qsTr("播报文字会发送到 %1").arg(tab.onlineHost)
                         : qsTr("在线语音 · 尚未配置")
            descriptionColor: tab.onlineSelected && tab.onlineHost.length === 0
                              ? Theme.orangeText : Theme.textSecondary

            AppButton {
                objectName: "ttsPreviewButton"
                text: qsTr("试听")
                // 结束 is the line a real 国服 导随 reaches, so it is the one
                // 试听 plays.
                onClicked: Tts.preview("finished")
            }
            ToggleSwitch {
                objectName: "ttsEnabledSwitch"
                checked: Settings.ttsEnabled
                onToggled: function(value) { Settings.ttsEnabled = value }
            }
        }

        SettingsRow {
            label: qsTr("语音引擎")
            // "已安装 · Windows 自带" once there is a voice list; until then, or
            // when the engine failed, the engine's own status line. An online
            // voice states what it needs instead.
            description: tab.onlineSelected ? qsTr("在线 · 需配置密钥")
                         : tab.engineReady ? qsTr("已安装 · Windows 自带")
                         : (typeof Tts !== "undefined" && Tts ? Tts.statusText : "")
            descriptionColor: tab.onlineSelected || tab.engineReady
                              || (typeof Tts !== "undefined" && Tts && Tts.available)
                              ? Theme.textSecondary : Theme.orangeText

            StyledComboBox {
                id: voiceCombo

                objectName: "ttsVoiceCombo"
                Layout.preferredWidth: 330
                enabled: tab.voiceRows.length > 0
                model: tab.voiceRows
                textRole: "label"
                valueRole: "id"
                // 两组：本机语音在前，在线语音在后。
                sectionRole: tab.localVoiceCount < tab.voiceRows.length ? "group" : ""
                sectionLabel: function(value) {
                    return value === "online" ? qsTr("在线语音 · 需配置密钥")
                                              : qsTr("本机语音 · 已安装 · Windows 自带")
                }
                displayText: tab.voiceRows.length > 0 ? currentText : qsTr("没有可选的本机语音")

                // Selecting writes currentIndex and would destroy an inline
                // binding; as in SliderRow, this keeps the box on the voice the
                // service reports.
                Binding {
                    target: voiceCombo
                    property: "currentIndex"
                    value: tab.indexOfVoice(tab.voiceIdValue)
                    restoreMode: Binding.RestoreNone
                }

                onActivated: function(index) {
                    if (index >= 0 && index < tab.voiceRows.length)
                        tab.chooseVoice(tab.voiceRows[index].id)
                }
            }
        }

        SettingsRow {
            label: qsTr("语速")

            SliderRow {
                Layout.preferredWidth: 216
                Layout.fillWidth: false
                from: 50
                to: 200
                stepSize: 5
                suffix: "%"
                value: Settings.ttsRate
                onMoved: function(value) { Settings.ttsRate = value }
            }
        }

        SettingsRow {
            showDivider: false
            label: qsTr("音量")

            SliderRow {
                Layout.preferredWidth: 216
                Layout.fillWidth: false
                from: 0
                to: 100
                stepSize: 5
                value: Settings.ttsVolume
                onMoved: function(value) { Settings.ttsVolume = value }
            }
        }
    }

    // 在线语音: created only while an online voice is selected and the Collector
    // offers online speech.
    Loader {
        id: onlineSlot

        objectName: "onlineSpeechSlot"
        Layout.fillWidth: true
        visible: active
        active: tab.onlineSelected
        sourceComponent: OnlineSpeechPanel {
            speech: tab.speech
            voiceId: tab.voiceIdValue
            onVoiceChosen: function(id) { Tts.setVoice(id) }
        }
    }

    OnlineSpeechConfirmDialog {
        id: confirmDialog

        parent: Overlay.overlay
        anchors.centerIn: parent
        onConfirmed: function(id) {
            Settings.ttsOnlineConfirmed = true
            Tts.setVoice(id)
        }
        // The box goes back to the voice still in use.
        onCancelled: voiceCombo.currentIndex = tab.indexOfVoice(tab.voiceIdValue)
    }

    // --mock-open-speech-confirm: ask about the fixture's voice once its row exists.
    Timer {
        readonly property string wanted: typeof ForceSpeechConfirm !== "undefined" && ForceSpeechConfirm
                                         ? ForceSpeechConfirm : ""
        interval: 150
        repeat: true
        running: wanted.length > 0
        onTriggered: {
            if (tab.indexOfVoice(wanted) < 0)
                return
            running = false
            voiceCombo.currentIndex = tab.indexOfVoice(wanted)
            confirmDialog.ask(wanted, tab.labelOfVoice(wanted))
        }
    }

    // --------------------------------------------------------- 播报模板 --
    SettingsPanel {
        objectName: "speechTemplatesCard"
        freeLayout: true

        RowLayout {
            Layout.fillWidth: true
            spacing: 10

            CardKicker {
                Layout.fillWidth: false
                text: qsTr("播报模板")
            }
            Text {
                text: qsTr("可用变量：")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }
            Text {
                Layout.fillWidth: true
                text: "{duty} {progress} {remaining}"
                color: Theme.textSecondary
                font.family: Theme.monoFamily
                font.pixelSize: Theme.fs(12)
                elide: Text.ElideRight
            }
        }

        GridLayout {
            Layout.fillWidth: true
            columns: 2
            columnSpacing: 12
            rowSpacing: 12

            TemplateField {
                label: qsTr("匹配成功")
                value: Settings.templateMatched
                onCommitted: function(text) { Settings.templateMatched = text }
            }
            TemplateField {
                label: qsTr("进入副本")
                value: Settings.templateEntered
                onCommitted: function(text) { Settings.templateEntered = text }
            }
            TemplateField {
                label: qsTr("通关")
                value: Settings.templateCompleted
                onCommitted: function(text) { Settings.templateCompleted = text }
            }
            TemplateField {
                label: qsTr("异常结束")
                value: Settings.templateAborted
                onCommitted: function(text) { Settings.templateAborted = text }
            }
            // 第 5 个模板（§3.1）：国服实际播报的即为此条，试听亦播报此条。
            TemplateField {
                Layout.columnSpan: 2
                label: qsTr("结束待确认")
                note: qsTr("国服目前看不到通关判定，一局导随打完只会停在“待确认”，"
                           + "这条就是那时播报的话。")
                value: Settings.templateFinished
                onCommitted: function(text) { Settings.templateFinished = text }
            }
        }
    }

    // `.field`: label above an input, an optional note under it.
    component TemplateField: ColumnLayout {
        id: field

        property string label: ""
        property string note: ""
        property string value: ""
        signal committed(string text)

        Layout.fillWidth: true
        Layout.preferredWidth: 1
        spacing: 4

        FieldLabel { text: field.label }
        StyledTextField {
            Layout.fillWidth: true
            text: field.value
            onEditingFinished: field.committed(text)
            // A template longer than the field would open scrolled to its end;
            // show the beginning unless the user is typing in it.
            onTextChanged: if (!activeFocus) Qt.callLater(function() { cursorPosition = 0 })
            onActiveFocusChanged: if (!activeFocus) cursorPosition = 0
        }
        Text {
            Layout.fillWidth: true
            visible: field.note.length > 0
            text: field.note
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }
}
