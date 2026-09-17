import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 设置 · 播报 · 在线语音 (docs/privacy-boundary.md §8.3). Shown only while an
// online voice is selected.
//
// The form is a draft until 保存: the service segment, the address fields and
// the key are sent together, and only then does the selected voice follow.
// The key field is never pre-filled - the Collector returns only has_key, never
// the key itself - and is emptied the moment 保存 is pressed.
SettingsPanel {
    id: panel

    objectName: "onlineSpeechCard"

    /// App.speech.
    property var speech: null
    /// The online voice in use (Tts.voiceId).
    property string voiceId: ""
    /// 保存 succeeded; the page selects this voice.
    signal voiceChosen(string id)

    readonly property bool ready: !!panel.speech && panel.speech.loaded && panel.speech.supported
    readonly property string selectedProvider: panel.ready ? panel.speech.providerOfVoiceId(panel.voiceId) : ""
    property string draftProvider: selectedProvider
    readonly property bool azureDraft: panel.draftProvider !== "openai_compatible"
    /// The Collector has this service in force (it may still lack a key).
    readonly property bool draftInForce: panel.ready && panel.speech.provider === panel.draftProvider
    readonly property bool keyStored: panel.draftInForce && panel.speech.hasKey
    readonly property bool selectedConfigured: panel.ready && panel.speech.configured
                                               && panel.speech.provider === panel.selectedProvider
    readonly property bool busy: panel.ready && panel.speech.busy
    property string pendingVoice: ""

    // Read from voiceId itself: a handler of voiceIdChanged may run before
    // the selectedProvider binding has caught up.
    function currentProvider() {
        return panel.ready ? panel.speech.providerOfVoiceId(panel.voiceId) : ""
    }

    function azureVoiceName() {
        if (panel.currentProvider() === "azure")
            return panel.speech.voiceNameOf(panel.voiceId)
        if (panel.speech.provider === "azure" && panel.speech.voice)
            return panel.speech.voice
        const voices = panel.speech.azureVoices
        return voices.length > 0 ? voices[0].name : "zh-CN-XiaoxiaoNeural"
    }

    function openaiVoiceName() {
        if (panel.currentProvider() === "openai_compatible")
            return panel.speech.voiceNameOf(panel.voiceId)
        if (panel.speech.provider === "openai_compatible" && panel.speech.voice)
            return panel.speech.voice
        return "alloy"
    }

    function resetDraft() {
        if (!panel.ready)
            return
        panel.draftProvider = panel.currentProvider()
        regionField.text = panel.speech.azureRegion || ""
        urlField.text = panel.speech.openaiBaseUrl || ""
        modelField.text = panel.speech.openaiModel || ""
        voiceField.text = panel.openaiVoiceName()
        keyField.text = ""
    }

    function save() {
        if (!panel.ready || panel.busy)
            return
        // Out of the field before anything else happens with it.
        const key = keyField.text
        keyField.text = ""
        let fields
        if (panel.azureDraft) {
            fields = { provider: "azure", azure_region: regionField.text.trim().toLowerCase(),
                       voice: panel.azureVoiceName() }
        } else {
            fields = { provider: "openai_compatible", openai_base_url: urlField.text.trim(),
                       openai_model: modelField.text.trim(),
                       voice: voiceField.text.trim().length > 0 ? voiceField.text.trim() : "alloy" }
        }
        if (key.trim().length > 0)
            fields.api_key = key
        panel.pendingVoice = panel.speech.voiceIdFor(fields.provider, fields.voice)
        panel.speech.save(fields)
    }

    onVoiceIdChanged: resetDraft()
    onReadyChanged: resetDraft()
    Component.onCompleted: resetDraft()

    Connections {
        target: panel.speech

        function onSettingsReplaced() {
            // A save in flight keeps what was typed until it is answered.
            if (!panel.busy)
                panel.resetDraft()
        }

        function onSaveFinished(ok) {
            const voice = panel.pendingVoice
            panel.pendingVoice = ""
            if (ok && voice.length > 0)
                panel.voiceChosen(voice)
        }
    }

    SettingsRow {
        label: qsTr("在线语音")
        labelIsKicker: true
        description: qsTr("把要播报的这一句话交给你选的语音服务念出来。请求由后台的采集器发出，界面只播放取回的本机声音文件。")

        Tag {
            objectName: "onlineSpeechStatusTag"
            text: panel.selectedConfigured ? qsTr("已配置") : qsTr("尚未配置")
            variant: panel.selectedConfigured ? "blue" : "accent"
        }
    }

    SettingsRow {
        label: qsTr("服务")

        SegmentedControl {
            objectName: "onlineSpeechProvider"
            Layout.preferredWidth: 240
            options: [
                { value: "azure", label: qsTr("Azure") },
                { value: "openai_compatible", label: qsTr("OpenAI 兼容") }
            ]
            currentValue: panel.draftProvider
            onActivated: function(value) { panel.draftProvider = value }
        }
    }

    SettingsRow {
        visible: panel.azureDraft
        label: qsTr("区域")
        description: qsTr("Azure 语音资源所在的区域")

        StyledTextField {
            id: regionField
            objectName: "onlineSpeechRegionField"
            Layout.preferredWidth: 330
            placeholderText: qsTr("例如 eastasia")
        }
    }

    SettingsRow {
        visible: !panel.azureDraft
        label: qsTr("地址")
        description: qsTr("以 https:// 开头，到 /v1 为止")

        StyledTextField {
            id: urlField
            objectName: "onlineSpeechUrlField"
            Layout.preferredWidth: 330
            placeholderText: "https://…/v1"
        }
    }

    SettingsRow {
        visible: !panel.azureDraft
        label: qsTr("模型")

        StyledTextField {
            id: modelField
            objectName: "onlineSpeechModelField"
            Layout.preferredWidth: 330
            placeholderText: qsTr("例如 gpt-4o-mini-tts")
        }
    }

    SettingsRow {
        visible: !panel.azureDraft
        label: qsTr("音色")
        description: qsTr("按服务的说明填写，默认 alloy")

        StyledTextField {
            id: voiceField
            objectName: "onlineSpeechVoiceField"
            Layout.preferredWidth: 330
            placeholderText: "alloy"
        }
    }

    SettingsRow {
        label: qsTr("密钥")
        description: panel.keyStored ? qsTr("已保存密钥") : qsTr("未保存密钥")
        descriptionColor: panel.keyStored ? Theme.textSecondary : Theme.orangeText

        StyledTextField {
            id: keyField
            objectName: "onlineSpeechKeyField"
            Layout.preferredWidth: 256
            echoMode: TextInput.Password
            passwordCharacter: "•"
            inputMethodHints: Qt.ImhSensitiveData | Qt.ImhNoPredictiveText | Qt.ImhNoAutoUppercase
            placeholderText: panel.keyStored ? qsTr("留空则保留已保存的密钥") : qsTr("粘贴你的密钥")
        }
        AppButton {
            objectName: "onlineSpeechClearKeyButton"
            text: qsTr("清除")
            enabled: panel.keyStored && !panel.busy
            onClicked: panel.speech.clearKey()
        }
    }

    ColumnLayout {
        Layout.fillWidth: true
        Layout.topMargin: 12
        Layout.bottomMargin: 8
        spacing: 8

        Text {
            objectName: "onlineSpeechNote"
            Layout.fillWidth: true
            text: qsTr("开启后播报文字会发送到 %1；密钥只保存在本机，并由 Windows 加密。")
                  .arg(panel.ready ? panel.speech.describeTarget(panel.draftProvider, regionField.text, urlField.text) : "")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            objectName: "onlineSpeechRebindHint"
            Layout.fillWidth: true
            visible: panel.ready && panel.speech.hasKey && !panel.draftInForce
            text: qsTr("换成另一个服务后，之前保存的密钥会被删除，需要为新服务重新填写。")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 10

            Text {
                objectName: "onlineSpeechResultText"
                Layout.fillWidth: true
                visible: text.length > 0
                text: panel.ready ? panel.speech.resultText : ""
                color: !panel.ready ? Theme.textSecondary
                       : panel.speech.resultState === "error" ? Theme.orangeText
                       : panel.speech.resultState === "ok" ? Theme.green
                       : Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
            Item {
                Layout.fillWidth: true
                visible: !(panel.ready && panel.speech.resultText.length > 0)
            }
            AppButton {
                objectName: "onlineSpeechTestButton"
                text: panel.busy && panel.speech.resultText === qsTr("正在测试…") ? qsTr("测试中…") : qsTr("测试")
                enabled: panel.selectedConfigured && !panel.busy
                onClicked: panel.speech.test()
            }
            AppButton {
                objectName: "onlineSpeechSaveButton"
                variant: "primary"
                text: qsTr("保存")
                enabled: panel.ready && !panel.busy
                onClicked: panel.save()
            }
        }
    }
}
