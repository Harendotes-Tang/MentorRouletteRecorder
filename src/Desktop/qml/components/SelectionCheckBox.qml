import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// Selection controls share the import workflow's theme-aware local Lucide tick.
CheckBox {
    id: control
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1
    font.pixelSize: Theme.fs(13)
    indicator: Rectangle {
        implicitWidth: 18
        implicitHeight: 18
        x: control.text ? (control.mirrored ? control.width - width - control.rightPadding : control.leftPadding)
                        : control.leftPadding + (control.availableWidth - width) / 2
        y: control.topPadding + (control.availableHeight - height) / 2
        radius: 4
        color: control.down ? Theme.fill : Theme.insetBackground
        border.width: control.visualFocus ? 2 : 1
        border.color: control.visualFocus || control.checked ? Theme.accent : Theme.textSecondary
        opacity: control.enabled ? 1 : 0.6
        Image {
            anchors.centerIn: parent
            width: 14
            height: 14
            visible: control.checked
            sourceSize: Qt.size(Math.ceil(width * control.pixelRatio),
                                Math.ceil(height * control.pixelRatio))
            fillMode: Image.PreserveAspectFit
            source: Lucide.source("check", control.enabled ? Theme.accent : Theme.textMuted)
        }
    }
    contentItem: Text { renderType: Theme.bodyRenderType;
        textFormat: Text.PlainText
        text: control.text
        font: control.font
        color: control.enabled ? Theme.textPrimary : Theme.textMuted
        leftPadding: control.indicator && !control.mirrored ? control.indicator.width + control.spacing : 0
        rightPadding: control.indicator && control.mirrored ? control.indicator.width + control.spacing : 0
        verticalAlignment: Text.AlignVCenter
    }
}
