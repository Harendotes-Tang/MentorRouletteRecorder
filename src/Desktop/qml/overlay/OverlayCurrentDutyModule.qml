import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 当前副本: the run in progress as one line (job, category, name, state).
// Only on screen during a run; 已匹配 shows until the duty is entered.
ColumnLayout {
    id: root

    required property var ov
    spacing: 0

    OverlayModuleLabel { ov: root.ov; text: qsTr("当前副本"); Layout.bottomMargin: 5 }

    OverlayRunRow {
        objectName: "floatingCurrentDuty"
        Layout.fillWidth: true
        Layout.minimumHeight: 28
        ov: root.ov
        run: root.ov.run
        name: root.ov.matched ? qsTr("已匹配") : root.ov.runName
        status: root.ov.matched ? "matched" : root.ov.entered ? "live"
              : root.ov.completed ? "done" : root.ov.ended ? "ended" : ""
    }

    HoverHandler { id: hover }
    OverlayToolTip { visible: hover.hovered && root.ov.runTip.length > 0; text: root.ov.runTip }
}
