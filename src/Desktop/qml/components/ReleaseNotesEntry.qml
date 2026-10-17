import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
import "Lucide.js" as Lucide

Item {
    id: entryRoot
    required property var entry
    property int resetToken: 0
    property bool expanded: false
    readonly property bool highlighted: entry.highlight === true
    readonly property var languages: typeof Lang !== "undefined" ? Lang.languages : []
    readonly property int inset: highlighted ? 18 : 0
    signal languageSettingsRequested()
    signal ensureVisibleRequested(Item target)

    objectName: "releaseNotesEntry_" + entry.id
    implicitHeight: column.implicitHeight + (highlighted ? 36 : 26)
    onResetTokenChanged: expanded = false

    Rectangle {
        anchors.fill: parent
        visible: entryRoot.highlighted
        color: Theme.eorzea ? Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, 0.045)
                            : Theme.accentMuted
        radius: Theme.radiusM
        border.width: 1
        border.color: Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, 0.35)
    }
    Rectangle {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        visible: !entryRoot.highlighted
        color: Theme.border
    }
    ColumnLayout {
        id: column
        x: entryRoot.inset
        y: entryRoot.highlighted ? 18 : 12
        width: entryRoot.width - entryRoot.inset * 2
        spacing: 8

        Label {
            visible: entryRoot.highlighted
            text: qsTr("重点")
            color: Theme.accentStrong
            font.pixelSize: Theme.fs(11)
            font.bold: true
        }
        RowLayout {
            Layout.fillWidth: true
            spacing: 8
            Label {
                objectName: "releaseNotesTag_" + entryRoot.entry.id
                visible: !entryRoot.highlighted && entryRoot.entry.tag.length > 0
                text: entryRoot.entry.tag
                textFormat: Text.PlainText
                font.pixelSize: Theme.fs(11)
                color: Theme.textSecondary
                leftPadding: 5
                rightPadding: 5
                topPadding: 2
                bottomPadding: 2
                background: Rectangle { color: Theme.fill; radius: Theme.radiusXxs }
                Layout.alignment: Qt.AlignBaseline
            }
            Label {
                objectName: "releaseNotesTitle_" + entryRoot.entry.id
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignBaseline
                text: entryRoot.entry.title
                textFormat: Text.PlainText
                wrapMode: Text.Wrap
                font.pixelSize: Theme.fs(entryRoot.highlighted ? 15 : 13)
                font.bold: true
                color: Theme.textPrimary
            }
            AppButton {
                objectName: entryRoot.highlighted ? "" : "releaseNotesDetails_" + entryRoot.entry.id
                visible: !entryRoot.highlighted
                Layout.alignment: Qt.AlignBaseline
                text: entryRoot.expanded ? qsTr("收起") : qsTr("详细")
                variant: "ghost"
                compact: true
                implicitWidth: contentItem.implicitWidth + 34
                Accessible.name: text + " · " + entryRoot.entry.title
                Accessible.description: entryRoot.expanded ? qsTr("收起完整说明") : qsTr("展开完整说明")
                onClicked: entryRoot.expanded = !entryRoot.expanded
                Keys.onReturnPressed: clicked()
                Keys.onEnterPressed: clicked()
                onActiveFocusChanged: if (activeFocus) entryRoot.ensureVisibleRequested(this)
                Image {
                    anchors.right: parent.right
                    anchors.rightMargin: 4
                    anchors.verticalCenter: parent.verticalCenter
                    width: 10
                    height: 10
                    rotation: entryRoot.expanded ? 270 : 90
                    source: Lucide.source("chevron-right", Theme.accentStrong)
                    sourceSize: Qt.size(width * Screen.devicePixelRatio, height * Screen.devicePixelRatio)
                }
            }
        }
        Label {
            objectName: "releaseNotesSummary_" + entryRoot.entry.id
            Layout.fillWidth: true
            text: entryRoot.entry.summary
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            lineHeight: 1.4
        }
        Flow {
            Layout.fillWidth: true
            visible: entryRoot.highlighted && entryRoot.entry.action === "language-settings"
            spacing: 6
            Repeater {
                model: entryRoot.languages
                Label {
                    required property var modelData
                    text: modelData.label
                    font.family: typeof Lang !== "undefined" ? Lang.displayFamily(modelData.code) : Theme.bodyFamily
                    font.pixelSize: Theme.fs(11)
                    color: Theme.textPrimary
                    leftPadding: 8
                    rightPadding: 8
                    topPadding: 4
                    bottomPadding: 4
                    background: Rectangle {
                        color: Theme.surface
                        border.width: 1
                        border.color: Theme.border
                        radius: Theme.radiusXxs
                    }
                }
            }
        }
        RowLayout {
            visible: entryRoot.highlighted
            spacing: 8
            AppButton {
                objectName: "releaseNotesLanguageSettings"
                visible: entryRoot.entry.action === "language-settings"
                text: qsTr("去设置")
                variant: "primary"
                compact: true
                onClicked: entryRoot.languageSettingsRequested()
                Keys.onReturnPressed: clicked()
                Keys.onEnterPressed: clicked()
                onActiveFocusChanged: if (activeFocus) entryRoot.ensureVisibleRequested(this)
            }
            AppButton {
                objectName: entryRoot.highlighted ? "releaseNotesDetails_" + entryRoot.entry.id : ""
                text: entryRoot.expanded ? qsTr("收起") : qsTr("详细")
                variant: "ghost"
                compact: true
                implicitWidth: contentItem.implicitWidth + 34
                Accessible.name: text + " · " + entryRoot.entry.title
                Accessible.description: entryRoot.expanded ? qsTr("收起完整说明") : qsTr("展开完整说明")
                onClicked: entryRoot.expanded = !entryRoot.expanded
                Keys.onReturnPressed: clicked()
                Keys.onEnterPressed: clicked()
                onActiveFocusChanged: if (activeFocus) entryRoot.ensureVisibleRequested(this)
                Image {
                    anchors.right: parent.right
                    anchors.rightMargin: 4
                    anchors.verticalCenter: parent.verticalCenter
                    width: 10
                    height: 10
                    rotation: entryRoot.expanded ? 270 : 90
                    source: Lucide.source("chevron-right", Theme.accentStrong)
                    sourceSize: Qt.size(width * Screen.devicePixelRatio, height * Screen.devicePixelRatio)
                }
            }
        }
        TextEdit {
            objectName: "releaseNotesDetail_" + entryRoot.entry.id
            Layout.fillWidth: true
            Layout.preferredHeight: implicitHeight
            visible: entryRoot.expanded
            text: entryRoot.entry.detail
            textFormat: TextEdit.PlainText
            readOnly: true
            selectByMouse: true
            activeFocusOnTab: visible
            wrapMode: TextEdit.Wrap
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(13)
            onActiveFocusChanged: if (activeFocus) entryRoot.ensureVisibleRequested(this)
        }
    }
}
