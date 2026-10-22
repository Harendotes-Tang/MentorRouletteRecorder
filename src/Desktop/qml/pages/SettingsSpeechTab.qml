pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// 设置 · 播报: the voice (engine, rate, volume), the 在线语音 panel while an
// online voice is selected, and the five templates. A local voice never reaches
// the Collector; an online one is synthesized by it
// (docs/ui-design.md §4.5, docs/privacy-boundary.md §8.3).
ColumnLayout {
    id: tab

    Layout.fillWidth: true
    spacing: 16

    /// 「在通用里更改」: the language is chosen on the 通用 tab.
    signal openGeneralRequested()

    // 播报跟随界面语言 (LanguageManager). A test scene without one shows
    // neither the language row nor a language in the template title.
    readonly property var lang: typeof Runtime.lang !== "undefined" ? Runtime.lang : null
    readonly property string languageName: tab.lang ? tab.lang.nativeName : ""
    readonly property bool languageVoiceMissing: typeof Runtime.tts !== "undefined" && !!Runtime.tts
                                                 && Runtime.tts.languageVoiceMissing === true

    // The page is also loaded by tests with a Tts stub that may predate the
    // voice list, so every newer member is read defensively.
    readonly property var voiceRows: (typeof Runtime.tts !== "undefined" && Runtime.tts && Runtime.tts.voices)
                                     ? Runtime.tts.voices : []
    readonly property string voiceIdValue: (typeof Runtime.tts !== "undefined" && Runtime.tts && Runtime.tts.voiceId)
                                           ? Runtime.tts.voiceId : ""
    readonly property int localVoiceCount: {
        let count = 0
        for (let i = 0; i < tab.voiceRows.length; ++i) {
            if (tab.voiceRows[i].group !== "online")
                ++count
        }
        return count
    }
    readonly property bool engineReady: typeof Runtime.tts !== "undefined" && !!Runtime.tts && Runtime.tts.available
                                        && tab.localVoiceCount > 0
    // App.speech: the Collector's online-speech settings (never a key).
    readonly property var speech: (typeof Runtime.app !== "undefined" && Runtime.app && Runtime.app.speech) ? Runtime.app.speech : null
    readonly property bool onlineOffered: !!tab.speech && tab.speech.loaded && tab.speech.supported
    readonly property bool onlineSelected: tab.onlineOffered && tab.isOnline(tab.voiceIdValue)
    readonly property bool onlineConfirmed: typeof Runtime.settings !== "undefined" && !!Runtime.settings
                                            && Runtime.settings.ttsOnlineConfirmed === true
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
        Runtime.tts.setVoice(id)
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
            description: !tab.onlineSelected ? qsTr("使用本机语音，无需联网")
                         : tab.onlineHost.length > 0 ? qsTr("播报文字会发送到 %1").arg(tab.onlineHost)
                         : qsTr("在线语音 · 尚未配置")
            descriptionColor: tab.onlineSelected && tab.onlineHost.length === 0
                              ? Theme.orangeText : Theme.textSecondary

            AppButton {
                objectName: "ttsPreviewButton"
                text: qsTr("试听")
                // 通关 is the line a cleared 导随 reaches now that the clear is
                // seen, so it is the one 试听 plays.
                onClicked: Runtime.tts.preview("completed")
            }
            ToggleSwitch {
                objectName: "ttsEnabledSwitch"
                accessibleName: tab.onlineSelected ? qsTr("语音播报") : qsTr("本地语音播报")
                checked: Runtime.settings.ttsEnabled
                onToggled: function(value) { Runtime.settings.ttsEnabled = value }
            }
        }

        // 播报语言: follows the interface language; changed on the 通用 tab.
        SettingsRow {
            objectName: "speechLanguageRow"
            visible: tab.lang !== null
            label: qsTr("播报语言")
            description: qsTr("跟随界面语言，语音和播报模板都按这个语言来")

            Tag {
                objectName: "speechLanguageTag"
                text: tab.languageName
            }
            AppButton {
                objectName: "speechLanguageChangeButton"
                variant: "ghost"
                compact: true
                text: qsTr("在通用里更改")
                onClicked: tab.openGeneralRequested()
            }
        }

        SettingsRow {
            id: voiceRow

            label: qsTr("语音引擎")
            // "已安装 · Windows 自带" once there is a voice list; until then, or
            // when the engine failed, the engine's own status line. An online
            // voice states what it needs instead.
            // A machine without a voice for the interface language reads the
            // lines with another language's voice; it says so and offers the
            // Windows page where one is added.
            readonly property bool voiceMissing: !tab.onlineSelected && tab.engineReady
                                                 && tab.languageVoiceMissing
            description: tab.onlineSelected ? qsTr("在线 · 需配置密钥")
                         : voiceMissing ? qsTr("这台电脑还没有%1语音，暂用其他语言的语音播报").arg(tab.languageName)
                         : tab.engineReady ? qsTr("已安装 · Windows 自带")
                         : (typeof Runtime.tts !== "undefined" && Runtime.tts ? Runtime.tts.statusText : "")
            descriptionColor: !voiceMissing && (tab.onlineSelected || tab.engineReady
                              || (typeof Runtime.tts !== "undefined" && Runtime.tts && Runtime.tts.available))
                              ? Theme.textSecondary : Theme.orangeText

            AppButton {
                objectName: "openSpeechSettingsButton"
                visible: voiceRow.voiceMissing
                variant: "ghost"
                compact: true
                text: qsTr("打开 Windows 语音设置")
                onClicked: Runtime.tts.openSpeechSettings()
            }

            StyledComboBox {
                id: voiceCombo

                objectName: "ttsVoiceCombo"
                // A switch chooses another voice (the new language's); the
                // Binding below follows the service, so no row is put back.
                keepsRowAcrossLanguages: false
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
                value: Runtime.settings.ttsRate
                onMoved: function(value) { Runtime.settings.ttsRate = value }
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
                value: Runtime.settings.ttsVolume
                onMoved: function(value) { Runtime.settings.ttsVolume = value }
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
            onVoiceChosen: function(id) { Runtime.tts.setVoice(id) }
        }
    }

    QtObject {
        readonly property OnlineSpeechConfirmDialog onlineConfirmation: OnlineSpeechConfirmDialog {
            id: confirmDialog

            parent: Overlay.overlay
            anchors.centerIn: parent
            onConfirmed: function(id) {
                Runtime.settings.ttsOnlineConfirmed = true
                Runtime.tts.setVoice(id)
            }
            // The box goes back to the voice still in use.
            onCancelled: voiceCombo.currentIndex = tab.indexOfVoice(tab.voiceIdValue)
        }
    }

    // --mock-open-speech-confirm: ask about the fixture's voice once its row exists.
    Timer {
        readonly property string wanted: typeof Runtime.forceSpeechConfirm !== "undefined" && Runtime.forceSpeechConfirm
                                         ? Runtime.forceSpeechConfirm : ""
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
                text: tab.languageName.length > 0 ? qsTr("播报模板 · %1").arg(tab.languageName)
                                                  : qsTr("播报模板")
            }
            Text {
                text: qsTr("可用占位符：")
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
                value: Runtime.settings.templateMatched
                onCommitted: function(text) { Runtime.settings.templateMatched = text }
            }
            TemplateField {
                label: qsTr("进入副本")
                value: Runtime.settings.templateEntered
                onCommitted: function(text) { Runtime.settings.templateEntered = text }
            }
            TemplateField {
                label: qsTr("通关")
                value: Runtime.settings.templateCompleted
                onCommitted: function(text) { Runtime.settings.templateCompleted = text }
            }
            TemplateField {
                label: qsTr("异常结束")
                value: Runtime.settings.templateAborted
                onCommitted: function(text) { Runtime.settings.templateAborted = text }
            }
            // 第 5 个模板（§3.1）：离开副本前没有收到通关结算时播报此条。
            TemplateField {
                Layout.columnSpan: 2
                label: qsTr("结束待确认")
                note: qsTr("收到通关结算时，记录记为「通关」，播报上面的「通关」；"
                           + "未收到结算就离开副本时，记录为「待确认」，播报这一条。")
                value: Runtime.settings.templateFinished
                onCommitted: function(text) { Runtime.settings.templateFinished = text }
            }
        }

        // 恢复默认模板: the interface language's five lines only.
        AppButton {
            objectName: "resetTemplatesButton"
            Layout.alignment: Qt.AlignRight
            variant: "ghost"
            compact: true
            text: qsTr("恢复默认模板")
            enabled: Runtime.settings.templatesCustomised === true
            onClicked: {
                Runtime.settings.resetTemplates()
                if (typeof Runtime.app !== "undefined" && Runtime.app && Runtime.app.showToast) {
                    Runtime.app.showToast(tab.languageName.length > 0
                                  ? qsTr("已恢复%1的默认播报模板").arg(tab.languageName)
                                  : qsTr("已恢复默认播报模板"))
                }
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
