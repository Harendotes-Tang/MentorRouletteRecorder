import QtQuick
import QtQuick.Layouts
import MentorRecorder

// One recent 导随心得 on the dashboard: duty, mood tag, date and the first two
// lines of the entry.
InsetBox {
    id: root

    // A `{run, reflection}` entry of App.reflectionSummary.recent.
    property var entry: ({})

    readonly property var runData: entry && entry.run ? entry.run : ({})
    readonly property var reflection: entry && entry.reflection ? entry.reflection : ({})

    signal activated()

    implicitHeight: body.implicitHeight + 20
    color: hover.hovered && Theme.eorzea
           ? Theme.insetBackgroundStrong
           : (Theme.eorzea ? Theme.insetBackground : Theme.contentBackground)

    HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
    TapHandler { onTapped: root.activated() }

    ColumnLayout {
        id: body

        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.leftMargin: 12
        anchors.rightMargin: 12
        anchors.topMargin: 10
        spacing: 5

        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            Text {
                Layout.fillWidth: true
                text: root.runData.duty_name || qsTr("未知副本")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                font.bold: true
                elide: Text.ElideRight
            }

            Tag {
                text: Theme.moodLabel(root.reflection.mood)
                variant: Theme.moodVariant(root.reflection.mood)
            }

            Text {
                text: Fmt.localDate(root.runData.matched_at_utc)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                font.family: Theme.figureFamily
                font.weight: Theme.figureWeight(false)
                font.features: ({ "tnum": 1 })
            }
        }

        Text {
            Layout.fillWidth: true
            text: root.reflection.text || ""
            color: Theme.textPrimary
            opacity: 0.8
            font.pixelSize: Theme.fs(12)
            lineHeight: 1.45
            lineHeightMode: Text.ProportionalHeight
            wrapMode: Text.WordWrap
            maximumLineCount: 2
            elide: Text.ElideRight
        }
    }
}
