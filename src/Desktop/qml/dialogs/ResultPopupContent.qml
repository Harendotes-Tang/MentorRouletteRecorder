import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 本次结果待确认 as the compact popup beside the statistics float
// (docs/ui-design.md §4.9): a 30 px header (when the run ended, the queue
// pager, the prompt bell, ⓘ, 收起), the run on one line, then either the
// question with its three answers or the result already recorded, and the
// 心得 row - three faces and a one-line field that grows while writing.
// The logic stays in ReflectionDialog; this is only its standalone face.
ColumnLayout {
    id: popup

    required property var dialog
    readonly property var run: dialog.runData || ({})
    readonly property bool asking: dialog.askingResult && !dialog.answered
    readonly property bool promptEnabled: dialog.askingResult ? dialog.promptEnabled : true
    readonly property string result: String(run.result || "")
    readonly property bool cleared: result === "COMPLETED"
    /// 「已计入」 only on the Collector's own word that this run counts (review L2-1).
    readonly property bool counted: cleared && run.contributes_to_goal === true
    readonly property bool wasLeft: result === "LEFT_OR_ABANDONED"
    // Opened from 待处理 with more waiting: page through them, until one is answered.
    readonly property bool hasQueue: dialog.standalone && !dialog.automaticOpen && dialog.queueCount > 1
                                     && dialog.queueIndex >= 0 && !dialog.answered
    readonly property color moodColor: dialog.mood === "good" ? Theme.green : dialog.mood === "bad" ? Theme.red : Theme.blue
    readonly property string whenText: {
        if (dialog.automaticOpen) return qsTr("刚刚结束")
        const iso = String(run.ended_at_utc || run.matched_at_utc || "")
        const date = new Date(iso)
        return isNaN(date.getTime()) ? Fmt.dash() : Qt.formatDateTime(date, "MM-dd HH:mm")
    }

    spacing: 0

    OverlayData { id: ov }

    // ---------------------------------------------------------------- 标题栏 --
    Item {
        Layout.fillWidth: true
        Layout.preferredHeight: 30

        DragHandler {
            target: null
            onActiveChanged: if (active && popup.dialog.parent && popup.dialog.parent.Window.window)
                                 popup.dialog.parent.Window.window.startSystemMove()
        }

        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 9
            anchors.rightMargin: 3
            spacing: 6

            OverlayIcon { name: "crest"; color: ov.accent; Layout.alignment: Qt.AlignVCenter }
            OverlayIcon { name: "clock"; color: Theme.textSecondary; Layout.alignment: Qt.AlignVCenter }
            Text {
                Layout.leftMargin: -1
                text: popup.whenText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11.5)
                font.weight: Font.DemiBold
                font.family: Theme.figureFamily
                font.features: ({ "tnum": 1 })
            }
            RowLayout {
                visible: popup.hasQueue
                Layout.leftMargin: 2
                spacing: 1
                OverlayIconButton {
                    objectName: "popupPreviousButton"
                    iconName: "chevron-left"
                    buttonSize: 20
                    tip: qsTr("上一条")
                    enabled: popup.dialog.queueIndex > 0 && !popup.dialog.busy
                    onClicked: popup.dialog.pageQueue(-1)
                }
                Text {
                    text: (popup.dialog.queueIndex + 1) + " / " + popup.dialog.queueCount
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    font.weight: Font.Bold
                    font.family: Theme.figureFamily
                    font.features: ({ "tnum": 1 })
                }
                OverlayIconButton {
                    objectName: "popupNextButton"
                    iconName: "chevron-right"
                    buttonSize: 20
                    tip: qsTr("下一条")
                    enabled: popup.dialog.queueIndex < popup.dialog.queueCount - 1 && !popup.dialog.busy
                    onClicked: popup.dialog.pageQueue(1)
                }
            }
            Item { Layout.fillWidth: true }

            // The bell: whether the next duty asks again.
            OverlayIconButton {
                id: bell
                objectName: "popupPromptToggle"
                readonly property bool resultKind: popup.dialog.resultFlow
                readonly property bool promptOn: resultKind ? popup.dialog.confirmEnabled : popup.dialog.promptEnabled
                iconName: promptOn ? "bell" : "bell-off"
                on: promptOn
                tip: resultKind ? (promptOn ? qsTr("结束后自动询问：开") : qsTr("结束后自动询问：关"))
                                : (promptOn ? qsTr("通关后提醒记笔记：开") : qsTr("通关后提醒记笔记：关"))
                onClicked: {
                    if (typeof Settings === "undefined") return
                    if (resultKind) { if (Settings.confirmPrompt !== undefined) Settings.confirmPrompt = !promptOn }
                    else if (Settings.reflectPrompt !== undefined) Settings.reflectPrompt = !promptOn
                }
            }
            OverlayIconButton {
                iconName: "info"
                tip: qsTr("只有确认「通关」才计入导随次数。\n收起后草稿保留，可从浮窗「待处理」继续。")
            }
            OverlayIconButton {
                objectName: "deferReflectionButton"
                iconName: "collapse"
                tip: qsTr("收起到待处理，稍后再确认")
                enabled: !popup.dialog.busy
                onClicked: popup.dialog.defer()
            }
        }
    }
    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

    // ---------------------------------------------------------------- 内容 --
    ColumnLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 14
        Layout.rightMargin: 14
        Layout.topMargin: 12
        Layout.bottomMargin: 14
        spacing: 11

        Text {
            Layout.fillWidth: true
            visible: popup.dialog.unavailable
            text: qsTr("这条记录已删除或暂时无法读取。草稿仍可复制，当前不能提交。")
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
        Text {
            Layout.fillWidth: true
            visible: !!popup.dialog.recovery && popup.dialog.recovery.error.length > 0
            text: popup.dialog.recovery ? popup.dialog.recovery.error : ""
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        // `.pp-run`: job, category, duty and how long it took.
        RowLayout {
            Layout.fillWidth: true
            spacing: 8
            OverlayRunRow {
                Layout.fillWidth: true
                ov: ov
                run: popup.run
                name: popup.dialog.dutyText
                nameSize: 14
            }
            RowLayout {
                spacing: 4
                Layout.alignment: Qt.AlignVCenter
                OverlayIcon { name: "timer"; color: Theme.textSecondary }
                Text {
                    text: Fmt.duration(popup.run.duration_ms)
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12.5)
                    font.weight: Font.Bold
                    font.family: Theme.figureFamily
                    font.features: ({ "tnum": 1 })
                }
            }
            HoverHandler { id: runHover }
            OverlayToolTip {
                visible: runHover.hovered
                text: [popup.dialog.dutyText, Theme.categoryName(popup.run.duty_category) || qsTr("未识别"),
                       Theme.jobName(popup.run) || qsTr("未知")].join(" · ")
            }
        }

        // 本次职业: only when the run has none; the answer is sent with it.
        RowLayout {
            Layout.fillWidth: true
            visible: popup.dialog.needsJob
            spacing: 8
            Text { text: qsTr("本次职业"); color: Theme.textSecondary; font.pixelSize: Theme.fs(11.5) }
            StyledComboBox {
                id: jobCombo
                objectName: "completedJobComboCompact"
                Layout.fillWidth: true
                enabled: !popup.dialog.busy
                model: popup.dialog.jobOptions
                textRole: "label"
                showJobIcons: true
                Binding { target: jobCombo; property: "currentIndex"; value: popup.dialog.jobIndex; restoreMode: Binding.RestoreNone }
                onActivated: popup.dialog.jobIndex = currentIndex
            }
        }

        // The draft and the saved note disagree: the player picks one.
        ColumnLayout {
            Layout.fillWidth: true
            visible: popup.dialog.conflictUnresolved
            spacing: 6
            Text {
                Layout.fillWidth: true
                text: qsTr("已保存的笔记有变化。下面保留了旧草稿，请选择要继续编辑的内容。最新笔记：\n%1")
                      .arg(popup.dialog.baselineText || qsTr("（空）"))
                textFormat: Text.PlainText
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }
            RowLayout {
                spacing: 6
                AppButton {
                    objectName: "keepRecoveryDraftButton"
                    compact: true
                    text: qsTr("继续使用草稿")
                    onClicked: { popup.dialog.conflictUnresolved = false; popup.dialog.persistDraft(true) }
                }
                AppButton {
                    objectName: "useLatestNoteButton"
                    compact: true
                    text: qsTr("使用最新笔记")
                    onClicked: popup.dialog.useLatestNote()
                }
            }
        }

        // `.pp-q` + `.pp-acts`: the question and its three answers.
        ColumnLayout {
            Layout.fillWidth: true
            visible: popup.asking
            spacing: 11
            Text {
                Layout.fillWidth: true
                text: qsTr("通关了吗？")
                color: Theme.headingColor
                font.family: Theme.headingFamilyFor(text)
                font.pixelSize: Theme.fs(21)
                font.bold: true
                font.letterSpacing: Theme.eorzea ? 0.4 : 0
                wrapMode: Text.WordWrap
            }
            RowLayout {
                Layout.fillWidth: true
                spacing: 7
                AppButton {
                    objectName: "confirmCompletedButton"
                    Layout.fillWidth: true
                    Layout.preferredWidth: 3
                    Layout.preferredHeight: 40
                    variant: "primary"
                    overlayIcon: "check"
                    text: qsTr("通关 +1")
                    enabled: !popup.dialog.busy && !popup.dialog.conflictUnresolved && !popup.dialog.unavailable
                    onClicked: popup.dialog.resolveWith("COMPLETED", QT_TRANSLATE_NOOP("SystemReason", "用户确认通关"))
                    HoverHandler { id: yesHover }
                    OverlayToolTip { visible: yesHover.hovered; text: qsTr("确认通关，计入导随次数") }
                }
                AppButton {
                    objectName: "confirmLeftButton"
                    Layout.fillWidth: true
                    Layout.preferredWidth: 2
                    Layout.preferredHeight: 40
                    overlayIcon: "leave"
                    text: qsTr("退出")
                    enabled: !popup.dialog.busy && !popup.dialog.conflictUnresolved && !popup.dialog.unavailable
                    onClicked: popup.dialog.resolveWith("LEFT_OR_ABANDONED", QT_TRANSLATE_NOOP("SystemReason", "用户确认退出或放弃"))
                    HoverHandler { id: leftHover }
                    OverlayToolTip { visible: leftHover.hovered; text: qsTr("退出 / 放弃 · 不计入导随次数") }
                }
                AppButton {
                    objectName: "laterResultButton"
                    Layout.preferredHeight: 40
                    variant: "ghost"
                    overlayIcon: "clock"
                    text: qsTr("稍后")
                    enabled: !popup.dialog.resolving
                    onClicked: popup.dialog.defer()
                    HoverHandler { id: laterHover }
                    OverlayToolTip { visible: laterHover.hovered; text: qsTr("收起到待处理，稍后再确认") }
                }
            }
        }

        // `.pp-res`: what was recorded, once it is known.
        Rectangle {
            objectName: "popupResultBox"
            Layout.fillWidth: true
            visible: !popup.asking
            implicitHeight: resultRow.implicitHeight + 16
            radius: Theme.radiusS
            color: popup.cleared ? Qt.rgba(Theme.green.r, Theme.green.g, Theme.green.b, 0.14) : Theme.insetBackground
            border.width: 1
            border.color: popup.cleared ? Qt.rgba(Theme.green.r, Theme.green.g, Theme.green.b, 0.45) : Theme.insetBorder
            RowLayout {
                id: resultRow
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                anchors.leftMargin: 10
                anchors.rightMargin: 10
                spacing: 8
                Rectangle {
                    Layout.preferredWidth: 20
                    Layout.preferredHeight: 20
                    radius: 10
                    color: popup.cleared ? Theme.green : popup.wasLeft ? ov.warn : Theme.neutral500
                    OverlayIcon {
                        anchors.centerIn: parent
                        name: popup.cleared ? "check" : popup.wasLeft ? "leave" : "info"
                        color: Theme.badgeForeground
                        size: 12
                    }
                }
                Text {
                    Layout.fillWidth: true
                    text: popup.counted ? qsTr("已计入导随次数")
                        : popup.wasLeft ? qsTr("已记为退出 / 放弃，不计入")
                        : Fmt.resultLabel(popup.result || "UNKNOWN")
                    textFormat: Text.PlainText
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12.5)
                    font.weight: Font.Bold
                    wrapMode: Text.WordWrap
                }
                RowLayout {
                    // Two Collector figures one apart, or none: a read that missed
                    // this run, or caught another one too, prints no arithmetic.
                    visible: popup.counted && popup.dialog.progressFrom >= 0
                             && popup.dialog.progressTo === popup.dialog.progressFrom + 1
                    spacing: 3
                    Text {
                        text: String(popup.dialog.progressFrom) + " →"
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                    }
                    Text {
                        text: String(popup.dialog.progressTo)
                        color: ov.accent
                        font.family: Theme.numFamily
                        font.pixelSize: Theme.fs(15)
                        font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                        font.features: ({ "tnum": 1 })
                    }
                }
            }
        }

        // `.pp-note`: 这次感觉 and the note.
        RowLayout {
            Layout.fillWidth: true
            visible: popup.promptEnabled
            spacing: 8

            RowLayout {
                spacing: 4
                Layout.alignment: Qt.AlignTop
                Layout.topMargin: 1
                Repeater {
                    model: [{ mood: "good", icon: "face-good", tone: Theme.green, label: qsTr("顺利") },
                            { mood: "ok", icon: "face-ok", tone: Theme.blue, label: qsTr("一般") },
                            { mood: "bad", icon: "face-bad", tone: Theme.red, label: qsTr("糟心") }]
                    delegate: AbstractButton {
                        id: face
                        required property var modelData
                        readonly property bool current: popup.dialog.mood === modelData.mood
                        objectName: "popupMood_" + modelData.mood
                        implicitWidth: 32
                        implicitHeight: 32
                        hoverEnabled: true
                        enabled: !popup.dialog.busy
                        Accessible.name: modelData.label
                        onClicked: popup.dialog.mood = modelData.mood
                        background: Rectangle {
                            radius: 16
                            color: face.current ? Qt.rgba(face.modelData.tone.r, face.modelData.tone.g, face.modelData.tone.b, 0.18) : Theme.insetBackground
                            border.width: 1
                            border.color: face.current ? face.modelData.tone : (face.hovered ? (Theme.eorzea ? Theme.gold3 : Theme.neutral400) : Theme.insetBorder)
                            Rectangle {
                                anchors.fill: parent
                                anchors.margins: -2
                                radius: 18
                                visible: face.current
                                color: "transparent"
                                border.width: 2
                                border.color: Qt.rgba(face.modelData.tone.r, face.modelData.tone.g, face.modelData.tone.b, 0.25)
                            }
                        }
                        contentItem: Item {
                            OverlayIcon {
                                anchors.centerIn: parent
                                name: face.modelData.icon
                                color: face.current ? face.modelData.tone : Theme.textSecondary
                                size: 20
                            }
                        }
                        scale: face.hovered && Theme.motion ? 1.06 : 1
                        Behavior on scale { NumberAnimation { duration: Theme.motionFast } }
                        OverlayToolTip { visible: face.hovered; text: face.modelData.label }
                    }
                }
            }

            StyledTextArea {
                id: noteArea
                objectName: "reflectionTextArea"
                Layout.fillWidth: true
                Layout.minimumWidth: 0
                Layout.preferredHeight: activeFocus || text.length > 0 ? Math.max(34, Math.min(96, contentHeight + 14)) : 34
                Behavior on Layout.preferredHeight { NumberAnimation { duration: Theme.motionFast } }
                readOnly: popup.dialog.busy || popup.dialog.pendingSwitchId.length > 0
                placeholderText: qsTr("写一句心得（可选）")
                font.pixelSize: Theme.fs(12.5)
                topPadding: 6
                bottomPadding: 6
                leftPadding: 9
                rightPadding: 9
                // One text, two editors: the dialog's logic reads its own. The face is
                // created after the text was set, so it starts from the editor's text.
                Component.onCompleted: text = popup.dialog.noteEditor.text
                onTextChanged: if (popup.dialog.noteEditor.text !== text) popup.dialog.noteEditor.text = text
                Connections {
                    target: popup.dialog.noteEditor
                    function onTextChanged() { if (noteArea.text !== popup.dialog.noteEditor.text) noteArea.text = popup.dialog.noteEditor.text }
                }
            }
        }

        Text {
            Layout.fillWidth: true
            visible: popup.dialog.errorText.length > 0
            text: popup.dialog.errorText
            color: Theme.red
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        // `.pp-foot`: 完成 once the result is on screen.
        RowLayout {
            Layout.fillWidth: true
            visible: !popup.asking
            // A run kept for review with its result already recorded (crash
            // recovery, a withdrawn calibration) is settled in the history page;
            // without this the item could never leave 待处理 (review K1-2).
            AppButton {
                objectName: "reviewRecoveredRecordButtonCompact"
                visible: popup.run.pending_review === true && popup.result.length > 0
                         && popup.result !== "UNKNOWN" && !popup.dialog.unavailable
                variant: "ghost"
                compact: true
                text: qsTr("在历史记录中复核")
                enabled: !popup.dialog.busy
                onClicked: popup.dialog.requestReview()
            }
            Item { Layout.fillWidth: true }
            AppButton {
                objectName: "saveReflectionButton"
                Layout.alignment: Qt.AlignRight
                variant: "primary"
                compact: !Theme.eorzea
                enabled: !popup.dialog.busy && !popup.dialog.conflictUnresolved && !popup.dialog.unavailable
                         && !(typeof App !== "undefined" && App.reflectionSaving === true)
                text: popup.dialog.submitting ? qsTr("保存中…") : qsTr("完成")
                onClicked: popup.dialog.finish()
            }
        }
    }
}
