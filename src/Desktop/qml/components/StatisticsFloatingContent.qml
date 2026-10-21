import QtQuick
import QtQuick.Layouts
import MentorRecorder

// The float's modules in the order of 自定义, a hairline between them. 当前副本
// and 副本用时 appear only during a run, 待处理记录 only while something waits.
ColumnLayout {
    id: content

    required property var ov
    spacing: 0

    function available(key) {
        if (key === "current_duty" || key === "elapsed") return content.ov.inRun
        if (key === "pending_work") return content.ov.pendingCount > 0
        return true
    }
    readonly property var shownModules: {
        void content.ov.inRun
        void content.ov.pendingCount
        return App.statisticsOverlay.modules.filter(function(key) { return content.available(key) })
    }

    Repeater {
        id: modules
        objectName: "floatingModuleRepeater"
        model: content.shownModules
        delegate: ColumnLayout {
            id: section
            required property string modelData
            required property int index
            Layout.fillWidth: true
            spacing: 0

            Rectangle {
                visible: section.index > 0
                Layout.fillWidth: true
                Layout.topMargin: 10
                Layout.bottomMargin: 10
                implicitHeight: 1
                color: Theme.border
            }
            Loader {
                Layout.fillWidth: true
                objectName: "floatingModule_" + section.modelData
                sourceComponent: section.modelData === "progress" ? progressModule
                               : section.modelData === "current_duty" ? dutyModule
                               : section.modelData === "elapsed" ? elapsedModule
                               : section.modelData === "dungeons" ? dungeonsModule
                               : section.modelData === "pending_work" ? pendingModule : jobsModule
            }
        }
    }

    Text {
        visible: content.shownModules.length === 0
        Layout.fillWidth: true
        text: App.statisticsOverlay.modules.length === 0
              ? qsTr("还没有选择内容，点标题栏的“自定义”添加。")
              : qsTr("所选内容进本后显示。")
        wrapMode: Text.WordWrap
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
    }

    Component { id: progressModule; OverlayProgressModule { ov: content.ov } }
    Component { id: dutyModule; OverlayCurrentDutyModule { ov: content.ov } }
    Component { id: elapsedModule; OverlayElapsedModule { ov: content.ov } }
    Component { id: dungeonsModule; OverlayDutyStatsModule { ov: content.ov } }
    Component { id: jobsModule; OverlayRolesModule { ov: content.ov } }
    Component { id: pendingModule; OverlayPendingModule { ov: content.ov } }
}
