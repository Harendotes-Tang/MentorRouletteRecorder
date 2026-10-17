import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
import "../components/Lucide.js" as Lucide

Dialog {
    id: dialog
    objectName: "releaseNotesDialog"
    property var notesController: null
    property int resetToken: 0
    property bool languageNavigationPending: false
    signal languageSettingsRequested()
    readonly property var entries: notesController ? notesController.entries : []
    readonly property var highlights: entries.filter(function(entry) { return entry.highlight })
    readonly property var sections: {
        const groups = []
        for (const entry of entries) {
            if (entry.highlight) continue
            let group = groups.find(function(candidate) { return candidate.category === entry.category })
            if (!group) {
                group = { category: entry.category, label: categoryLabel(entry.category, entry.section), entries: [] }
                groups.push(group)
            }
            group.entries.push(entry)
        }
        return groups
    }
    readonly property string countsText: {
        const labels = []
        const seen = []
        for (const entry of entries) {
            if (seen.indexOf(entry.category) >= 0) continue
            seen.push(entry.category)
            const count = notesController.counts[entry.category] || 0
            labels.push(qsTr("%1 %2 项").arg(categoryLabel(entry.category, entry.section)).arg(count))
        }
        return labels.join(" · ")
    }

    function categoryLabel(category, source) {
        switch (category) {
        case "added": return qsTr("新增")
        case "fixed": return qsTr("修复")
        case "improved": return qsTr("改进")
        default: return source || qsTr("更新")
        }
    }
    function goToLanguageSettings() {
        languageNavigationPending = true
        dialog.close()
    }
    function ensureItemVisible(item) {
        const top = item.mapToItem(notesView.contentItem, 0, 0).y
        const bottom = top + item.height
        if (top < notesView.contentY || item.height > notesView.height)
            notesView.contentY = Math.max(0, top)
        else if (bottom > notesView.contentY + notesView.height)
            notesView.contentY = Math.min(notesView.contentHeight - notesView.height, bottom - notesView.height)
    }

    modal: true
    anchors.centerIn: Overlay.overlay
    width: Math.min(600, (parent ? parent.width : 648) - 48)
    height: Math.min(680, (parent ? parent.height : 728) - 48)
    padding: 0
    closePolicy: Popup.CloseOnEscape
    background: DialogFrame { opaque: true }
    Overlay.modal: Rectangle { color: Theme.modalScrim(dialog.palette.shadow) }

    onOpened: {
        languageNavigationPending = false
        ++resetToken
        notesView.contentY = 0
        acknowledgeButton.forceActiveFocus(Qt.PopupFocusReason)
    }
    onClosed: {
        if (notesController) notesController.acknowledge()
        if (languageNavigationPending) {
            languageNavigationPending = false
            dialog.languageSettingsRequested()
        }
    }

    contentItem: ColumnLayout {
        spacing: 0
        Item {
            Layout.fillWidth: true
            implicitHeight: headerRow.implicitHeight + 36
            RowLayout {
                id: headerRow
                x: 24
                y: 18
                width: parent.width - 48
                spacing: 14
                Rectangle {
                    Layout.alignment: Qt.AlignTop
                    width: 40
                    height: 40
                    radius: Theme.radiusS
                    color: Theme.accentMuted
                    Image {
                        anchors.centerIn: parent
                        width: 22
                        height: 22
                        source: Lucide.source("book-open-text", Theme.accentStrong)
                        sourceSize: Qt.size(width * Screen.devicePixelRatio, height * Screen.devicePixelRatio)
                    }
                }
                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 6
                    HeadingLabel {
                        Layout.fillWidth: true
                        text: qsTr("本次更新")
                        font.pixelSize: Theme.fs(12)
                    }
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 10
                        HeadingLabel {
                            objectName: "releaseNotesVersion"
                            text: dialog.notesController ? dialog.notesController.version : ""
                            font.pixelSize: Theme.fs(24)
                            color: Theme.textPrimary
                            wrapMode: Text.WrapAnywhere
                            Layout.maximumWidth: headerRow.width - 170
                        }
                        Label {
                            text: dialog.notesController && dialog.notesController.prerelease ? qsTr("测试版") : qsTr("正式版")
                            font.pixelSize: Theme.fs(11)
                            color: Theme.orangeText
                            padding: 4
                            background: Rectangle {
                                color: Theme.orangeBackground
                                radius: Theme.radiusXxs
                                border.width: 1
                                border.color: Qt.rgba(Theme.orange.r, Theme.orange.g, Theme.orange.b, 0.3)
                            }
                        }
                        Item { Layout.fillWidth: true }
                    }
                    Label {
                        objectName: "releaseNotesCounts"
                        Layout.fillWidth: true
                        text: dialog.countsText
                        wrapMode: Text.Wrap
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                    }
                }
                AppButton {
                    objectName: "releaseNotesClose"
                    Layout.alignment: Qt.AlignTop
                    Layout.preferredWidth: 28
                    text: ""
                    iconName: "x"
                    compact: true
                    variant: "ghost"
                    Accessible.name: qsTr("关闭")
                    onClicked: dialog.close()
                    Keys.onReturnPressed: clicked()
                    Keys.onEnterPressed: clicked()
                }
            }
        }
        Rectangle { Layout.fillWidth: true; height: 1; color: Theme.border }
        Flickable {
            id: notesView
            objectName: "releaseNotesScroll"
            Layout.fillWidth: true
            Layout.fillHeight: true
            contentWidth: width
            contentHeight: notesColumn.implicitHeight + 40
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {
                policy: notesView.contentHeight > notesView.height ? ScrollBar.AlwaysOn : ScrollBar.AlwaysOff
            }
            ColumnLayout {
                id: notesColumn
                x: 24
                y: 20
                width: notesView.width - 48 - Theme.scrollGutter
                spacing: 20
                Repeater {
                    model: dialog.highlights
                    ReleaseNotesEntry {
                        required property var modelData
                        Layout.fillWidth: true
                        entry: modelData
                        resetToken: dialog.resetToken
                        onLanguageSettingsRequested: dialog.goToLanguageSettings()
                        onEnsureVisibleRequested: function(target) { dialog.ensureItemVisible(target) }
                    }
                }
                Repeater {
                    model: dialog.sections
                    ColumnLayout {
                        required property var modelData
                        Layout.fillWidth: true
                        spacing: 0
                        Label {
                            Layout.fillWidth: true
                            text: modelData.label
                            textFormat: Text.PlainText
                            color: modelData.category === "fixed" ? Theme.green : Theme.accentStrong
                            font.pixelSize: Theme.fs(12)
                            font.bold: true
                            bottomPadding: 8
                        }
                        Repeater {
                            model: modelData.entries
                            ReleaseNotesEntry {
                                required property var modelData
                                Layout.fillWidth: true
                                entry: modelData
                                resetToken: dialog.resetToken
                                onLanguageSettingsRequested: dialog.goToLanguageSettings()
                                onEnsureVisibleRequested: function(target) { dialog.ensureItemVisible(target) }
                            }
                        }
                    }
                }
            }
        }
        Rectangle { Layout.fillWidth: true; height: 1; color: Theme.border }
        Item {
            Layout.fillWidth: true
            implicitHeight: footerRow.implicitHeight + 24
            RowLayout {
                id: footerRow
                x: 24
                y: 12
                width: parent.width - 48
                spacing: 16
                Label {
                    Layout.fillWidth: true
                    text: qsTr("之后可以在 设置 › 关于 里再次查看。")
                    wrapMode: Text.Wrap
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                }
                AppButton {
                    id: acknowledgeButton
                    objectName: "releaseNotesAcknowledge"
                    text: qsTr("我知道了")
                    variant: "primary"
                    onClicked: dialog.close()
                    Keys.onReturnPressed: clicked()
                    Keys.onEnterPressed: clicked()
                }
            }
        }
    }
}
