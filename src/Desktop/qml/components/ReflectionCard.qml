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
                textFormat: Text.PlainText
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
                textFormat: Text.PlainText
                text: root.runData.matched_at_utc ? Fmt.localDate(root.runData.matched_at_utc)
                      : root.runData.import_metadata && root.runData.import_metadata.source_recorded_at_utc
                        ? qsTr("原站 %1").arg(Fmt.localDate(root.runData.import_metadata.source_recorded_at_utc))
                      : root.runData.import_metadata && root.runData.import_metadata.source_recorded_at
                        ? qsTr("原站 %1").arg(root.runData.import_metadata.source_recorded_at.substring(0, 10))
                      : qsTr("时间未知")
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
            // Stored/imported text is literal: AutoText can fetch HTML image URLs.
            textFormat: Text.PlainText
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
