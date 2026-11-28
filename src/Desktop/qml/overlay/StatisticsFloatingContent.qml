pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// The float's modules in the order of 自定义, a hairline between them.
ColumnLayout {
    id: content

    required property var ov
    required property var community
    signal dutyCommunityRequested()

    readonly property var dutyNotesDuty: ov.entered && ov.run && community ? community.service.dutyIdentity(ov.run) : null
    readonly property string dutyNotesRunKey: ov.run && dutyNotesDuty
        ? [ov.run.run_id, dutyNotesDuty.content_id, dutyNotesDuty.region].join("/") : ""
    property string dutyNotesPhase: ""
    onDutyNotesRunKeyChanged: dutyNotesPhase = ""

    spacing: 0

    function available(key) {
        if (key === "current_duty" || key === "elapsed") return content.ov.inRun
        if (key === "duty_notes") return content.ov.inRun && content.ov.entered && !!content.community
        if (key === "pending_work") return content.ov.pendingCount > 0
        return true
    }
    readonly property var shownModules: {
        Theme.trackBindingDependency(content.ov.inRun)
        Theme.trackBindingDependency(content.ov.entered)
        Theme.trackBindingDependency(content.ov.pendingCount)
        return Runtime.app.statisticsOverlay.modules.filter(function(key) { return content.available(key) })
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
                sourceComponent: section.modelData === "attempts" ? attemptsModule
                               : section.modelData === "progress" ? progressModule
                               : section.modelData === "current_duty" ? dutyModule
                               : section.modelData === "elapsed" ? elapsedModule
                               : section.modelData === "duty_notes" ? dutyNotesModule
                               : section.modelData === "dungeons" ? dungeonsModule
                               : section.modelData === "pending_work" ? pendingModule : jobsModule
            }
        }
    }

    // Said of the modules that are hidden: the run modules come with a duty,
    // 待处理记录 with a record to deal with (audit 2026-11-10 L3-5).
    readonly property bool hidesRunModules: {
        const selected = Runtime.app.statisticsOverlay.modules
        return selected.indexOf("current_duty") >= 0 || selected.indexOf("elapsed") >= 0
            || selected.indexOf("duty_notes") >= 0
    }
    readonly property bool hidesPending: Runtime.app.statisticsOverlay.modules.indexOf("pending_work") >= 0
    Text {
        objectName: "floatingEmptyState"
        visible: content.shownModules.length === 0
        Layout.fillWidth: true
        text: Runtime.app.statisticsOverlay.modules.length === 0
              ? qsTr("还没有选择内容，点标题栏的“自定义”添加。")
              : !content.hidesRunModules ? qsTr("有待处理记录时显示。")
              : content.hidesPending ? qsTr("所选内容进本后或有待处理记录时显示。")
              : qsTr("所选内容进本后显示。")
        wrapMode: Text.WordWrap
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
    }

    Component { id: attemptsModule; OverlayAttemptsModule { ov: content.ov } }
    Component { id: progressModule; OverlayProgressModule { ov: content.ov } }
    Component { id: dutyModule; OverlayCurrentDutyModule { ov: content.ov } }
    Component { id: elapsedModule; OverlayElapsedModule { ov: content.ov } }
    Component { id: dungeonsModule; OverlayDutyStatsModule { ov: content.ov } }
    Component { id: jobsModule; OverlayRolesModule { ov: content.ov } }
    Component { id: pendingModule; OverlayPendingModule { ov: content.ov } }
    Component {
        id: dutyNotesModule
        OverlayDutyNotesModule {
            community: content.community
            duty: content.dutyNotesDuty
            phaseKey: content.dutyNotesPhase
            automaticStage: content.ov.dutyPhase
            onPhaseSelected: key => content.dutyNotesPhase = key
            onManageRequested: content.dutyCommunityRequested()
        }
    }
}
