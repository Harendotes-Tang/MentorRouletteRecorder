import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// One run on a line: the job badge, the duty-category badge, the duty name and
// a state mark. Shared by the float's 当前副本 module, the mini bar and the
// result popup; `compact` is the mini bar's 20 px job badge.
RowLayout {
    id: root

    required property var ov
    property var run: null
    property string name: ""
    /// "" | "live" | "done" | "ended" | "matched"
    property string status: ""
    property bool compact: false
    property real nameSize: 13.5
    property bool showCategory: true
    property bool categoryArt: false

    readonly property string category: run ? String(run.duty_category || "") : ""

    spacing: 8

    JobIcon {
        Layout.alignment: Qt.AlignVCenter
        jobId: root.run ? root.run.job_id : null
        size: root.compact ? 20 : 28
    }

    OverlayTypeBadge {
        Layout.alignment: Qt.AlignVCenter
        visible: root.showCategory && !root.categoryArt && root.status !== "matched"
        text: root.ov.categoryBadge(root.category)
        tone: root.ov.categoryColor(root.category)
    }

    CategoryIcon {
        objectName: "reminderCategoryIcon"
        Layout.alignment: Qt.AlignVCenter
        visible: root.showCategory && root.categoryArt && root.status !== "matched"
        category: root.category
        size: 20
        Accessible.name: Theme.categoryName(root.category)
        HoverHandler { id: categoryHover }
        OverlayToolTip { visible: categoryHover.hovered; text: Theme.categoryName(root.category) }
    }

    Text {
        Layout.fillWidth: true
        Layout.minimumWidth: 0
        text: root.name
        textFormat: Text.PlainText
        elide: Text.ElideRight
        color: root.status === "matched" ? Theme.textSecondary : Theme.textPrimary
        font.pixelSize: Theme.fs(root.nameSize)
        font.weight: root.status === "matched" ? Font.DemiBold : Font.Bold
    }

    OverlayStatusIcon {
        Layout.alignment: Qt.AlignVCenter
        visible: root.status.length > 0
        ov: root.ov
        kind: root.status.length > 0 ? root.status : "live"
    }
}
