import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// `.lbl`: a module's caption. Off by default; the 文字标签 switch shows it.
Text {
    required property var ov
    Layout.fillWidth: true
    visible: ov.labels && text.length > 0
    textFormat: Text.PlainText
    elide: Text.ElideRight
    color: Theme.textSecondary
    font.pixelSize: Theme.fs(10.5)
    font.weight: Font.DemiBold
    font.letterSpacing: 0.6
}
