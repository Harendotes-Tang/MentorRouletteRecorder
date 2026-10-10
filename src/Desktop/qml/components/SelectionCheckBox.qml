import QtQuick
import QtQuick.Controls
import MentorRecorder

// Selection controls share the import workflow's theme-aware local Lucide tick.
CheckBox {
    id: control
    font.pixelSize: Theme.fs(12)
    indicator: Rectangle {
        implicitWidth: 22
        implicitHeight: 22
        x: control.text ? (control.mirrored ? control.width - width - control.rightPadding : control.leftPadding)
                        : control.leftPadding + (control.availableWidth - width) / 2
        y: control.topPadding + (control.availableHeight - height) / 2
        radius: Theme.radiusS
        color: control.down ? Theme.fill : Theme.insetBackground
        border.width: control.visualFocus ? 2 : 1
        border.color: control.visualFocus || control.checked ? Theme.accent : Theme.textSecondary
        opacity: control.enabled ? 1 : 0.6
        Image {
            anchors.centerIn: parent
            width: 18
            height: 14
            visible: control.checked
            // Cropped check path from the existing ISC-licensed clipboard-check.svg.
            source: "data:image/svg+xml;utf8," + encodeURIComponent(
                '<svg xmlns="http://www.w3.org/2000/svg" viewBox="8 11 8 6" fill="none" stroke="'
                + String(control.enabled ? Theme.textPrimary : Theme.textMuted)
                + '" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m9 14 2 2 4-4"/></svg>')
        }
    }
    contentItem: Text {
        textFormat: Text.PlainText
        text: control.text
        font: control.font
        color: control.enabled ? Theme.textPrimary : Theme.textMuted
        leftPadding: control.indicator && !control.mirrored ? control.indicator.width + control.spacing : 0
        rightPadding: control.indicator && control.mirrored ? control.indicator.width + control.spacing : 0
        verticalAlignment: Text.AlignVCenter
    }
}
