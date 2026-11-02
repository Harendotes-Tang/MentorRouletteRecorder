pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// ---------------------------------------------------------------------------
// 新区服采证 (design: 三皮肤整合版 › 新区服采证): the player's sub-page under
// 捕获诊断 › 工具 › 帮助适配其他区服. A back link, the title with the round's
// state, then two columns: the five steps on the left (only the current one
// open) and 本轮状态 on the right. EvidenceSession turns the Collector's
// $defs/CaptureValidationStatus into the step the player is on; this file only
// lays it out. A round records metadata only and never a formal run.
// ---------------------------------------------------------------------------
ColumnLayout {
    id: page
    objectName: "templateObservationGuideCard"

    signal backRequested()
    signal openDataSettingsRequested()

    readonly property EvidenceSession session: EvidenceSession {}
    // Tests read the adapter rows the page offers.
    readonly property var adapterChoices: session.adapterChoices
    readonly property int sideWidth: 272
    readonly property bool twoColumns: width >= 760

    spacing: 14

    // `.ev-back`
    AppButton {
        objectName: "evidenceBackButton"
        Layout.leftMargin: -6
        Layout.bottomMargin: -6
        variant: "ghost"
        compact: true
        iconName: "chevron-left"
        text: qsTr("捕获诊断")
        onClicked: page.backRequested()
    }

    PageHeader {
        title: qsTr("新区服采证")
        subtitle: qsTr("帮助适配其他区服 · 不新增正式记录")
        StatusTag {
            objectName: "evidenceStatusBadge"
            Layout.bottomMargin: 4
            text: page.session.badgeText
            tone: page.session.badgeTone
            labelObjectName: "evidenceStatusBadgeText"
        }
    }

    // `.ev-grid`: minmax(0,1fr) 272px
    GridLayout {
        Layout.fillWidth: true
        columns: page.twoColumns ? 2 : 1
        columnSpacing: 14
        rowSpacing: 14

        ColumnLayout {
            objectName: "evidenceSteps"
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignTop
            spacing: 8

            EvidenceSetupStep {
                session: page.session
                status: page.session.stepStatus(1)
                open: page.session.stepOpen(1)
                summary: page.session.summary(1)
                onToggleRequested: page.session.toggleStep(1)
                onOpenDataSettingsRequested: page.openDataSettingsRequested()
            }
            EvidenceReadinessStep {
                session: page.session
                status: page.session.stepStatus(2)
                open: page.session.stepOpen(2)
                summary: page.session.summary(2)
                onToggleRequested: page.session.toggleStep(2)
            }
            EvidenceMarkingStep {
                session: page.session
                status: page.session.stepStatus(3)
                open: page.session.stepOpen(3)
                summary: page.session.summary(3)
                onToggleRequested: page.session.toggleStep(3)
            }
            EvidenceSaveStep {
                session: page.session
                status: page.session.stepStatus(4)
                open: page.session.stepOpen(4)
                summary: page.session.summary(4)
                onToggleRequested: page.session.toggleStep(4)
            }
            EvidenceReportStep {
                session: page.session
                status: page.session.stepStatus(5)
                open: page.session.stepOpen(5)
                onToggleRequested: page.session.toggleStep(5)
            }
        }

        EvidenceSidebar {
            session: page.session
            Layout.alignment: Qt.AlignTop
            Layout.fillWidth: !page.twoColumns
            Layout.preferredWidth: page.twoColumns ? page.sideWidth : -1
            Layout.minimumWidth: page.twoColumns ? page.sideWidth : 0
            Layout.maximumWidth: page.twoColumns ? page.sideWidth : 16777215
        }
    }
}
