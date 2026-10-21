import QtQuick
import QtQuick.Controls
import MentorRecorder

// Both ordinary settings and metadata evidence use the Collector's existing
// override. Selection alone never starts a capture or an evidence session.
StyledComboBox {
    id: control

    property bool interactionAllowed: true
    readonly property var regionChoices: [
        { value: null, label: qsTr("自动（按安装路径）") },
        { value: "CN", label: qsTr("国服 CN") },
        { value: "GLOBAL", label: qsTr("国际服 GLOBAL") }
    ]

    function regionIndex(value) {
        const selectedValue = value === undefined ? null : value
        for (let index = 0; index < control.regionChoices.length; ++index) {
            if (control.regionChoices[index].value === selectedValue)
                return index
        }
        return 0
    }

    model: control.regionChoices
    textRole: "label"
    valueRole: "value"
    keepsRowAcrossLanguages: false
    enabled: App.captureSettingsSupported && App.captureSettingsLoaded
             && control.interactionAllowed
    Binding {
        target: control
        property: "currentIndex"
        value: control.regionIndex(App.captureSettings.region_override)
        restoreMode: Binding.RestoreNone
    }
    onActivated: function(index) {
        if (index >= 0 && index < control.regionChoices.length)
            App.updateCaptureSetting("region_override", control.regionChoices[index].value)
    }
    Accessible.name: qsTr("游戏区服")
    Accessible.description: qsTr("自动按安装路径识别；无法识别时，请选择实际使用的国服或国际服。")
}
