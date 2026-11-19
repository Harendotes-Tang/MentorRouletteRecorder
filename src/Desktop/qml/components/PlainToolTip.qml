import QtQuick
import QtQuick.Controls

// A tool tip for text that did not fit where it is shown: plain text, wrapped,
// never interpreted as markup (file names, Collector sentences).
ToolTip {
    id: tip
    contentItem: Text {
        text: tip.text
        textFormat: Text.PlainText
        font: tip.font
        wrapMode: Text.Wrap
        color: tip.palette.toolTipText
    }
}
