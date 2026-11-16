import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// `.dz-head`: a share chart's title, and to its right what a click does. The
// hint drops under the title when the two do not fit on one line.
GridLayout {
    id: root

    property string title: ""
    /// Shown while the chart has something to click.
    property bool hintVisible: true

    readonly property bool oneLine: titleMeasure.implicitWidth + hint.implicitWidth + 12 + (Theme.eorzea ? 16 : 0) <= width

    columns: oneLine ? 2 : 1
    columnSpacing: 12
    rowSpacing: 4

    CardKicker {
        objectName: "compositionTitle"
        Layout.fillWidth: true
        text: root.title
    }

    Text {
        id: hint
        objectName: "compositionHint"
        visible: root.hintVisible
        text: qsTr("点一项查看对应记录")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(11)
    }

    // Never shown: the title at the kicker's size, to know whether the hint
    // fits beside it.
    Text {
        id: titleMeasure
        visible: false
        text: root.title
        textFormat: Text.PlainText
        font.pixelSize: Theme.eorzea ? 12 : 13
        font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
        font.letterSpacing: Theme.eorzea ? 0.9 : 0
    }
}
