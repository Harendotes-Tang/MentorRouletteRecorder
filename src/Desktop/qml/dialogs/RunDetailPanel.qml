import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// The 380 px overlay panel of the prototype: header, four tabs, action bar.
Rectangle {
    id: root

    property var runData: ({})
    property var revisions: []
    property string activeTab: "info"

    signal tabChanged(string tab)
    signal closeRequested()
    signal correctRequested()
    signal deleteRequested()
    signal restoreRequested()
    // 撤销最近一次修正（UndoRevision），确认待复核（CorrectRun）。
    signal undoRequested()
    signal reviewRequested()
    // 补录笔记 / 编辑笔记 for this run.
    signal reflectRequested()

    readonly property var reflection: runData && runData.reflection ? runData.reflection : null
    readonly property bool pendingReview: !!(runData && runData.pending_review)
    // The highest revision in the list; gates the undo button below.
    readonly property int newestRevision: {
        let newest = 0
        for (let i = 0; i < root.revisions.length; ++i)
            newest = Math.max(newest, Number(root.revisions[i].revision || 0))
        return newest
    }

    /// Raw database tokens (result codes, column names, NULL, source enums) are
    /// maintainer material: a player sees only the Chinese label for the same
    /// fact, and identifier-only rows disappear.
    readonly property bool showRawTokens: App.maintainerToolsVisible

    readonly property var infoFields: {
        const fields = [
            { k: qsTr("版本 / 等级"),
              v: runData.duty_expansion
                 ? qsTr("%1 · %2级").arg(runData.duty_expansion).arg(runData.duty_level || 0)
                 : Fmt.dash() },
            { k: qsTr("结果"), v: Fmt.resultLabel(runData.result || "UNKNOWN") },
            // Rendered by the delegate as job icon + name + role icon + label;
            // `v` stays filled so the field degrades to plain text when the
            // catalogue is empty.
            { k: qsTr("职业"),
              kind: "job",
              v: (runData.job_name || qsTr("未知"))
                 + (runData.job_id
                    ? " (" + Jobs.roleGroup(runData.job_id) + ")"
                    : " · " + qsTr("未能识别职业")) },
            { k: qsTr("匹配时间"), v: Fmt.localTime(runData.matched_at_utc) },
            { k: qsTr("进本时间"), v: Fmt.localTime(runData.entered_at_utc) },
            { k: qsTr("结束时间"), v: Fmt.localTime(runData.ended_at_utc) },
            { k: qsTr("耗时"), v: Fmt.duration(runData.duration_ms) }
        ]

        if (root.showRawTokens) {
            fields.push({ k: qsTr("content_id / territory_id"),
                          v: (runData.content_id !== undefined && runData.content_id !== null
                              ? String(runData.content_id) : "NULL")
                             + " / "
                             + (runData.territory_id !== undefined && runData.territory_id !== null
                                ? String(runData.territory_id) : Fmt.dash()) })
        }

        fields.push({ k: qsTr("来源"),
                      v: root.showRawTokens
                         ? (runData.source || "AUTO_NETWORK")
                         : Fmt.sourceLabel(runData.source || "AUTO_NETWORK") })
        fields.push({ k: qsTr("计入进度"), v: runData.contributes_to_goal ? qsTr("是") : qsTr("否") })
        fields.push({ k: qsTr("检测置信"),
                      v: root.showRawTokens
                         ? (runData.detection_confidence || Fmt.dash())
                         : Fmt.confidenceLabel(runData.detection_confidence || "") })
        fields.push({ k: qsTr("待复核"),
                      v: runData.pending_review
                         ? qsTr("是 · 结果未经确认（崩溃恢复，或档案尚不能判定是否通关）")
                         : qsTr("否") })

        if (root.showRawTokens) {
            fields.push({ k: qsTr("协议 profile"), v: runData.protocol_profile_id || Fmt.dash() })
            fields.push({ k: qsTr("排队时间"), v: qsTr("NULL（无可靠事件）") })
        }

        return fields
    }

    radius: Theme.radiusM
    color: Theme.surface
    border.width: 1
    border.color: Theme.eorzea ? Theme.gold3 : Theme.border

    PanelDecoration {}

    ColumnLayout {
        anchors.fill: parent
        spacing: 0

        // ------------------------------------------------------- header --
        RowLayout {
            Layout.fillWidth: true
            Layout.margins: 16
            Layout.bottomMargin: 12
            spacing: 12

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 2

                RowLayout {
                    Layout.fillWidth: true
                    spacing: 8

                    CardKicker {
                        text: qsTr("%1 · 修订 %2").arg(Fmt.localDate(root.runData.matched_at_utc))
                                                  .arg(root.runData.revision || 1)
                    }

                    Tag {
                        visible: root.pendingReview
                        text: qsTr("待复核")
                        variant: "danger"
                    }

                    Item { Layout.fillWidth: true }
                }

                HeadingLabel {
                    Layout.fillWidth: true
                    text: root.runData.duty_name || qsTr("未知副本")
                    font.pixelSize: Theme.fs(19)
                    wrapMode: Text.WordWrap
                }

                Text {
                    Layout.fillWidth: true
                    text: root.runData.run_id || qsTr("未选择")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    elide: Text.ElideMiddle
                }
            }

            AppButton {
                Layout.alignment: Qt.AlignTop
                Layout.preferredWidth: 28
                Layout.preferredHeight: 28
                compact: true
                implicitWidth: 28
                implicitHeight: 28
                text: "×"
                onClicked: root.closeRequested()
            }
        }

        Rectangle { Layout.fillWidth: true; Layout.preferredHeight: 1; color: Theme.border }

        // --------------------------------------------------------- tabs --
        RowLayout {
            Layout.fillWidth: true
            spacing: 0

            Repeater {
                model: [
                    { value: "info", label: qsTr("详情") },
                    { value: "events", label: qsTr("事件摘要") },
                    { value: "revs", label: qsTr("修正历史") },
                    { value: "refl", label: qsTr("笔记") }
                ]

                delegate: Rectangle {
                    id: tabItem

                    required property var modelData

                    readonly property bool current: modelData.value === root.activeTab

                    Layout.preferredHeight: 36
                    Layout.fillWidth: true
                    // Classic: the navi look of workbench.css (accent-100 plate, accent text).
                    color: tabItem.current && !Theme.eorzea ? Theme.accentMuted : "transparent"
                    radius: Theme.radiusS

                    Rectangle {
                        anchors.fill: parent
                        radius: parent.radius
                        visible: Theme.eorzea && tabItem.current
                        gradient: Gradient {
                            orientation: Gradient.Horizontal
                            GradientStop { position: 0.0; color: Theme.navActiveStart }
                            GradientStop { position: 1.0; color: Theme.navActiveEnd }
                        }
                    }

                    Rectangle {
                        anchors.left: parent.left
                        anchors.top: parent.top
                        anchors.bottom: parent.bottom
                        width: 2
                        visible: Theme.eorzea && tabItem.current
                        color: Theme.gold2
                    }

                    Text {
                        anchors.centerIn: parent
                        text: tabItem.modelData.label
                        color: tabItem.current
                               ? (Theme.eorzea ? Theme.headingColor : Theme.accent)
                               : Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                        font.weight: Theme.eorzea ? Font.Bold
                                                  : (tabItem.current ? Font.DemiBold : Font.Medium)
                    }

                    TapHandler { onTapped: root.tabChanged(tabItem.modelData.value) }
                }
            }
        }

        Rectangle { Layout.fillWidth: true; Layout.preferredHeight: 1; color: Theme.border }

        // --------------------------------------------------------- body --
        Flickable {
            id: body

            Layout.fillWidth: true
            Layout.fillHeight: true
            clip: true
            contentWidth: width
            contentHeight: bodyLoader.implicitHeight
            boundsBehavior: Flickable.StopAtBounds
            ScrollBar.vertical: ScrollBar {
                policy: body.contentHeight > body.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff
            }

            Loader {
                id: bodyLoader
                width: body.width
                sourceComponent: {
                    switch (root.activeTab) {
                    case "revs":
                        return revisionsPane
                    case "events":
                        // Loaded on demand: GetRunEvents is a per-run query and
                        // opening the panel must not pay for a tab nobody looked at.
                        Qt.callLater(function() { App.refreshRunEvents() })
                        return eventsPane
                    case "refl":
                        return reflectionPane
                    default:
                        return infoPane
                    }
                }
            }
        }

        Rectangle { Layout.fillWidth: true; Layout.preferredHeight: 1; color: Theme.border }

        // ------------------------------------------------------- actions --
        RowLayout {
            Layout.fillWidth: true
            Layout.margins: 16
            spacing: 8

            AppButton {
                text: qsTr("手动修正")
                variant: root.pendingReview ? "secondary" : "primary"
                onClicked: root.correctRequested()
            }

            AppButton {
                objectName: "confirmReviewButton"
                visible: root.pendingReview
                variant: "primary"
                text: qsTr("确认")
                onClicked: root.reviewRequested()
            }

            AppButton {
                visible: !!root.runData.soft_deleted
                text: qsTr("恢复记录")
                onClicked: root.restoreRequested()
            }

            AppButton {
                visible: !root.runData.soft_deleted
                text: qsTr("软删除")
                onClicked: root.deleteRequested()
            }

            Item { Layout.fillWidth: true }
        }
    }

    // -------------------------------------------------------------- 详情 --
    Component {
        id: infoPane

        ColumnLayout {
            spacing: 12

            GridLayout {
                Layout.fillWidth: true
                Layout.margins: 16
                Layout.bottomMargin: 0
                columns: 2
                columnSpacing: 16
                rowSpacing: 12

                Repeater {
                    model: root.infoFields

                    delegate: ColumnLayout {
                        required property var modelData

                        readonly property bool isJob: modelData.kind === "job"

                        Layout.fillWidth: true
                        Layout.preferredWidth: 1
                        // The job row carries two icons and two labels; give it
                        // the full 380 px panel width instead of half of it.
                        Layout.columnSpan: isJob ? 2 : 1
                        spacing: 2

                        Text {
                            Layout.fillWidth: true
                            text: modelData.k
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(11)
                            elide: Text.ElideRight
                        }

                        Text {
                            Layout.fillWidth: true
                            visible: !parent.isJob
                            text: modelData.v
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(13)
                            font.weight: Theme.figureWeight(true)
                            font.family: Theme.figureFamily
                            font.features: ({ "tnum": 1 })
                            wrapMode: Text.WrapAnywhere
                        }

                        RowLayout {
                            Layout.fillWidth: true
                            Layout.topMargin: 2
                            visible: parent.isJob
                            spacing: 7

                            JobIcon {
                                jobId: root.runData.job_id
                                size: 22
                            }

                            Text {
                                text: root.runData.job_name || qsTr("未知")
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(13)
                                font.bold: true
                                elide: Text.ElideRight
                            }

                            // Unknown jobs fall back to the All-Rounder plate
                            // and spell the detection state out.
                            RoleIcon {
                                Layout.leftMargin: 4
                                Layout.fillWidth: true
                                role: root.runData.job_id ? Jobs.roleGroup(root.runData.job_id)
                                                          : qsTr("未知")
                                size: 20
                                showLabel: true
                                labelPixelSize: 12
                                labelColor: Theme.textSecondary
                                labelText: root.runData.job_id
                                           ? Jobs.roleGroup(root.runData.job_id)
                                           : qsTr("未知") + " · " + qsTr("未能识别职业")
                            }
                        }
                    }
                }
            }

            ColumnLayout {
                Layout.fillWidth: true
                Layout.margins: 16
                Layout.topMargin: 0
                spacing: 2
                visible: !!root.runData.note

                Text {
                    text: qsTr("备注")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                }
                Text {
                    Layout.fillWidth: true
                    text: root.runData.note || ""
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(13)
                    wrapMode: Text.WordWrap
                }
            }
        }
    }

    // ---------------------------------------------------------- 事件摘要 --
    //
    // GetRunEvents rows: the sanitized trail the Collector writes to
    // run_events - kind, time, direction, opcode, a hash prefix and the parsed
    // fields. No payload bytes exist anywhere in this path, so the tab is shown
    // unconditionally.
    //
    // 方向 / opcode / 哈希前缀 / profile id are wire vocabulary and appear only
    // under 维护者工具; everyone else gets the event's name and time plus one
    // sentence on what is and is not kept.
    Component {
        id: eventsPane

        ColumnLayout {
            spacing: 12

            ColumnLayout {
                Layout.fillWidth: true
                Layout.margins: 16
                spacing: 10

                RowLayout {
                    Layout.fillWidth: true
                    CardKicker {
                        text: App.maintainerToolsVisible ? qsTr("原始事件摘要")
                                                         : qsTr("这次记录发生了什么")
                    }
                    Item { Layout.fillWidth: true }
                    Text {
                        text: App.runEventsState === "ready"
                              ? qsTr("%1 条").arg(App.selectedRunEvents.length) : ""
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(false)
                        font.features: ({ "tnum": 1 })
                    }
                }

                InsetBox {
                    Layout.fillWidth: true
                    visible: App.runEventsState !== "ready"
                             || App.selectedRunEvents.length === 0
                    implicitHeight: emptyText.implicitHeight + 24

                    Text {
                        id: emptyText
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.margins: 12
                        anchors.verticalCenter: parent.verticalCenter
                        text: {
                            if (root.runData.manually_created)
                                return qsTr("手动记录，没有网络事件。")
                            switch (App.runEventsState) {
                            case "loading": return qsTr("正在读取事件摘要…")
                            case "unsupported":
                            case "error": return App.runEventsMessage
                            case "ready": return qsTr("这条记录没有留下事件行。")
                            default: return qsTr("打开此页签即会读取。")
                            }
                        }
                        color: App.runEventsState === "error" ? Theme.red : Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }
                }

                Repeater {
                    model: App.runEventsState === "ready" ? App.selectedRunEvents : []

                    delegate: InsetBox {
                        required property var modelData

                        Layout.fillWidth: true
                        implicitHeight: eventColumn.implicitHeight + 20

                        ColumnLayout {
                            id: eventColumn
                            anchors.left: parent.left
                            anchors.right: parent.right
                            anchors.top: parent.top
                            anchors.margins: 10
                            spacing: 3

                            RowLayout {
                                Layout.fillWidth: true
                                spacing: 8

                                Text {
                                    text: modelData.event_type || Fmt.dash()
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.bold: true
                                }

                                Tag {
                                    visible: !!modelData.parser_status
                                             && modelData.parser_status !== "OK"
                                    text: modelData.parser_status || ""
                                    variant: "danger"
                                }

                                Item { Layout.fillWidth: true }

                                Text {
                                    text: Fmt.localTime(modelData.observed_at_utc)
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(12)
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }
                            }

                            Text {
                                Layout.fillWidth: true
                                visible: App.maintainerToolsVisible
                                text: (modelData.direction || Fmt.dash())
                                      + " \u00b7 " + (modelData.opcode || Fmt.dash())
                                      + " \u00b7 " + qsTr("哈希 ")
                                      + (modelData.payload_hash || Fmt.dash())
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(11)
                                font.family: Theme.monoFamily
                                wrapMode: Text.WrapAnywhere
                            }

                            Text {
                                Layout.fillWidth: true
                                visible: text.length > 0
                                text: {
                                    const parsed = modelData.parsed
                                    if (!parsed)
                                        return ""
                                    const parts = []
                                    for (const key in parsed) {
                                        const value = parsed[key]
                                        parts.push(key + "=" + (value === null || value === undefined
                                                                ? Fmt.dash() : value))
                                    }
                                    return parts.join(" \u00b7 ")
                                }
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(11)
                                wrapMode: Text.WordWrap
                            }

                            Text {
                                Layout.fillWidth: true
                                visible: App.maintainerToolsVisible
                                         && !!modelData.protocol_profile_id
                                text: "profile " + (modelData.protocol_profile_id || "")
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(10)
                                elide: Text.ElideRight
                            }
                        }
                    }
                }

                Text {
                    Layout.fillWidth: true
                    text: App.maintainerToolsVisible
                          ? qsTr("默认不保存完整报文，仅保留操作码、方向、哈希与解析字段。")
                          : qsTr("这里只留下每件事的名称和发生时间，不保存任何游戏内容。")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    wrapMode: Text.WordWrap
                }

                Text {
                    Layout.fillWidth: true
                    visible: App.usingMockData && App.runEventsState === "ready"
                             && App.selectedRunEvents.length > 0
                    text: qsTr("以上为模拟后端合成的示例事件，不是真实抓包结果。")
                    color: Theme.orangeText
                    font.pixelSize: Theme.fs(11)
                    wrapMode: Text.WordWrap
                }
            }
        }
    }

    // -------------------------------------------------------------- 心得 --
    Component {
        id: reflectionPane

        ColumnLayout {
            spacing: 10

            ColumnLayout {
                Layout.fillWidth: true
                Layout.margins: 16
                spacing: 10

                RowLayout {
                    Layout.fillWidth: true
                    visible: !!root.reflection
                    spacing: 8

                    Tag {
                        text: Theme.moodLabel(root.reflection ? root.reflection.mood : "")
                        variant: Theme.moodVariant(root.reflection ? root.reflection.mood : "")
                    }

                    Text {
                        Layout.fillWidth: true
                        text: root.reflection
                              ? Fmt.localDateTime(root.reflection.updated_at_utc) : ""
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(false)
                        font.features: ({ "tnum": 1 })
                    }
                }

                Text {
                    Layout.fillWidth: true
                    visible: !!root.reflection
                    text: root.reflection ? (root.reflection.text || "") : ""
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(13)
                    lineHeight: 1.7
                    lineHeightMode: Text.ProportionalHeight
                    wrapMode: Text.WordWrap
                }

                InsetBox {
                    Layout.fillWidth: true
                    visible: !root.reflection
                    implicitHeight: noReflText.implicitHeight + 24

                    Text {
                        id: noReflText
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.margins: 12
                        anchors.verticalCenter: parent.verticalCenter
                        text: qsTr("这次导随还没有写笔记。")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        wrapMode: Text.WordWrap
                    }
                }

                AppButton {
                    Layout.alignment: Qt.AlignLeft
                    variant: root.reflection ? "secondary" : "primary"
                    text: root.reflection ? qsTr("编辑笔记") : qsTr("补录笔记")
                    onClicked: root.reflectRequested()
                }
            }
        }
    }

    // ---------------------------------------------------------- 修正历史 --
    Component {
        id: revisionsPane

        ColumnLayout {
            spacing: 12

            ColumnLayout {
                Layout.fillWidth: true
                Layout.margins: 16
                spacing: 12

                Repeater {
                    model: root.revisions

                    delegate: InsetBox {
                        id: revisionBox

                        required property var modelData

                        // Only the newest revision can be undone, and never
                        // revision 1: the Collector answers that with
                        // ERR_UNDO_NOT_ALLOWED, so the button is not offered.
                        readonly property bool undoable:
                            Number(modelData.revision || 0) === root.newestRevision
                            && root.newestRevision > 1
                            && App.selectedRunCanUndo

                        Layout.fillWidth: true
                        implicitHeight: revColumn.implicitHeight + 20

                        ColumnLayout {
                            id: revColumn
                            anchors.left: parent.left
                            anchors.right: parent.right
                            anchors.top: parent.top
                            anchors.margins: 10
                            spacing: 4

                            RowLayout {
                                Layout.fillWidth: true
                                spacing: 8

                                Text {
                                    text: root.showRawTokens
                                          ? qsTr("rev %1 \u2192 %2")
                                            .arg(Math.max(0, Number(revisionBox.modelData.revision || 1) - 1))
                                            .arg(revisionBox.modelData.revision || 1)
                                          : qsTr("\u7b2c %1 \u6b21\u4fee\u6539")
                                            .arg(revisionBox.modelData.revision || 1)
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12)
                                    font.weight: Theme.figureWeight(true)
                                    font.family: Theme.figureFamily
                                    font.features: ({ "tnum": 1 })
                                }

                                Item { Layout.fillWidth: true }

                                Text {
                                    text: Fmt.localDateTime(revisionBox.modelData.changed_at_utc)
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(12)
                                    font.family: Theme.figureFamily
                                    font.weight: Theme.figureWeight(false)
                                    font.features: ({ "tnum": 1 })
                                }
                            }

                            Text {
                                Layout.fillWidth: true
                                text: revisionBox.modelData.change_kind || Fmt.dash()
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(12)
                                wrapMode: Text.WordWrap
                            }

                            // Per-field diff from RunRevision.changes[].
                            Repeater {
                                model: revisionBox.modelData.changes || []

                                delegate: RowLayout {
                                    required property var modelData

                                    Layout.fillWidth: true
                                    Layout.leftMargin: 2
                                    spacing: 6

                                    Text {
                                        Layout.preferredWidth: 74
                                        text: Fmt.fieldLabel(modelData.field || "")
                                        color: Theme.textSecondary
                                        font.pixelSize: Theme.fs(11)
                                        elide: Text.ElideRight
                                    }

                                    Text {
                                        Layout.fillWidth: true
                                        Layout.preferredWidth: 1
                                        text: Fmt.revisionValue(modelData.old_value)
                                        color: Theme.textSecondary
                                        font.pixelSize: Theme.fs(11)
                                        font.strikeout: true
                                        elide: Text.ElideRight
                                    }

                                    Text {
                                        text: "\u2192"
                                        color: Theme.textSecondary
                                        font.pixelSize: Theme.fs(11)
                                    }

                                    Text {
                                        Layout.fillWidth: true
                                        Layout.preferredWidth: 1
                                        text: Fmt.revisionValue(modelData.new_value)
                                        color: Theme.textPrimary
                                        font.pixelSize: Theme.fs(11)
                                        font.bold: true
                                        elide: Text.ElideRight
                                    }
                                }
                            }

                            Text {
                                Layout.fillWidth: true
                                text: qsTr("原因：%1 \u00b7 %2")
                                      .arg(revisionBox.modelData.reason || qsTr("无原因"))
                                      .arg(revisionBox.modelData.actor || "USER")
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(12)
                                wrapMode: Text.WordWrap
                            }

                            RowLayout {
                                Layout.fillWidth: true
                                Layout.topMargin: 2
                                visible: revisionBox.undoable

                                AppButton {
                                    objectName: "undoRevisionButton"
                                    compact: true
                                    text: qsTr("撤销")
                                    onClicked: root.undoRequested()
                                }

                                Text {
                                    Layout.fillWidth: true
                                    text: qsTr("回放原值并追加一条新修订")
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(11)
                                    wrapMode: Text.WordWrap
                                }
                            }
                        }
                    }
                }

                InsetBox {
                    Layout.fillWidth: true
                    visible: root.revisions.length === 0
                    implicitHeight: 44

                    Text {
                        anchors.centerIn: parent
                        text: qsTr("尚无修正记录。")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                    }
                }

                Text {
                    Layout.fillWidth: true
                    text: root.showRawTokens
                          ? qsTr("修订记录只增不改；撤销同样作为一条新修订写入。")
                          : qsTr("修改记录只增不改；撤销也会作为一次新的修改记下来。")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    wrapMode: Text.WordWrap
                }
            }
        }
    }
}
