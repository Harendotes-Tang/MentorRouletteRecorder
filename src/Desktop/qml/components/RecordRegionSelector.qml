import QtQuick
import MentorRecorder 1.0

StyledComboBox {
    id: control
    property string selection: ""
    property bool includeAll: true
    signal regionPicked(string region)
    readonly property var choices: {
        const rows = includeAll ? [{ value: "", label: qsTr("全部区服") }] : []
        for (const region of ["CN", "TC", "GLOBAL", "UNKNOWN"])
            rows.push({ value: region, label: Theme.live(Runtime.fmt.regionLabel(region)) })
        return rows
    }
    function indexOfRegion(region) {
        for (let index = 0; index < choices.length; ++index)
            if (choices[index].value === region) return index
        return 0
    }
    model: choices
    textRole: "label"
    valueRole: "value"
    currentIndex: indexOfRegion(selection)
    onActivated: function(index) { control.regionPicked(control.choices[index].value) }
    Accessible.name: qsTr("记录区服")
}
