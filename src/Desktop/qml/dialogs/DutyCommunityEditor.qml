pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "DutyCommunityMatch.js" as Match

// The form of 副本提醒与导随宏 (Design artifact, 新建宏): it takes the whole dialog. On the left the
// type, the title and the body; on the right one column, 适用范围, where nothing is required and
// nothing filled in means everywhere. Ctrl + S saves; Esc, 取消 or the dialog's 「← 副本提醒与导随宏」
// go back to the list unsaved. What the form does not show (tags, source, links, display order,
// favourite) is saved as the entry has it.
ColumnLayout {
    id: editor
    objectName: "dutyCommunityEditor"

    required property var store
    property var row: null
    property string type: "macro"
    property int dutyId: 0
    property bool titleMissing: false
    property bool bodyMissing: false
    property bool stageTooLong: false
    property bool stageInvalid: false
    /// What the form found wrong with the title or the body before sending (the Collector's
    /// limits), and why the Collector refused the last save; "" when nothing.
    property string titleProblem: ""
    property string bodyProblem: ""
    property string refusal: ""
    /// The version range as chosen: "" is 不限.
    property string versionFrom: ""
    property string versionTo: ""
    /// What BOSS / 阶段, the version range and the jobs showed when the form opened; left alone,
    /// the entry keeps what it had (boss and phase apart, a range as it was written).
    property string loadedStage: ""
    property string loadedFrom: ""
    property string loadedTo: ""
    property string loadedJobs: ""
    /// The entry's 游戏版本 and 职业 as stored, kept while their fields stay as they opened. Text
    /// written by hand (no range, no jobs) opens as empty fields, so it is shown under them and
    /// can be cleared, which empties what is kept.
    property string keptVersion: ""
    property string keptRole: ""
    /// 职业 of a macro: 职能 and job codes, none for every job.
    property var jobs: []
    readonly property bool macro: type === "macro"
    readonly property bool fresh: !!row && row.revision === 0
    readonly property string heading: fresh ? (macro ? qsTr("新建宏") : qsTr("新建提醒"))
                                            : (macro ? qsTr("编辑宏") : qsTr("编辑提醒"))
    readonly property var regionKeys: ["ALL", "CN", "TC", "GLOBAL"]
    readonly property string regionKey: regionKeys[Math.max(0, region.currentIndex)]
    readonly property bool versionChanged: versionFrom !== loadedFrom || versionTo !== loadedTo
    // Told at once (Design: 开始晚于结束时马上提示); a backwards range already stored does not stop
    // other edits.
    readonly property bool versionBackwards: versionChanged && versionFrom !== "" && versionTo !== ""
                                             && parseFloat(versionFrom) > parseFloat(versionTo)
    readonly property bool versionByHand: keptVersion !== "" && store.versionRange(keptVersion) === null
    readonly property bool versionByHandShown: editor.versionByHand && !versionChanged
    readonly property bool roleByHand: keptRole !== "" && store.jobCodes(keptRole) === null
    readonly property int currentDuty: store.inDuty ? Number(store.duty.content_id) : 0
    readonly property string dutyText: store.dutyLabel({ content_id: dutyId, region: regionKey })
        + (dutyId > 0 && dutyId === currentDuty ? " · " + qsTr("当前副本") : "")
    // 不限, the patches, and whatever the entry already names.
    readonly property var versionOptions: {
        const listed = store.gameVersions.slice()
        for (const value of [loadedFrom, loadedTo])
            if (value && !listed.includes(value))
                listed.push(value)
        listed.sort((a, b) => parseFloat(b) - parseFloat(a))
        return [{ value: "", label: qsTr("不限") }].concat(listed.map(value => ({ value: value, label: value })))
    }
    // BOSS / 阶段 already written, this duty's first (Design: 从已有内容里给建议).
    readonly property var stageSuggestions: {
        const rows = store.localRows.filter(entry => entry.content.content_id === dutyId)
            .concat(store.localRows.filter(entry => entry.content.content_id !== dutyId))
        const out = []
        for (const entry of rows) {
            const label = store.stageLabel(entry.content)
            if (label && !out.includes(label))
                out.push(label)
        }
        return out
    }
    // 插入 (Design: 常用指令和占位符): commands start a line, placeholders go at the cursor.
    readonly property var insertItems: [
        { key: "party", code: "/p", command: true, label: qsTr("小队频道"), sub: qsTr("这一行发到小队频道") },
        { key: "echo", code: "/e", command: true, label: qsTr("只给自己看"), sub: qsTr("这一行只有你自己看得到") },
        { key: "sound", code: "<se.1>", command: false, label: qsTr("提示音"), sub: qsTr("数字 1–16 换不同的声音") },
        { key: "wait", code: "<wait.3>", command: false, label: qsTr("等几秒"), sub: qsTr("这一行发完，等 3 秒再发下一行") },
        { key: "target", code: "<t>", command: false, label: qsTr("当前目标"), sub: qsTr("换成你选中的目标的名字") }
    ]
    signal saved(var value)
    signal cancelled()
    spacing: 0

    // Esc leaves from anywhere in the form; an open list or panel takes it first and closes.
    // Ctrl + S is the dialog's (DutyCommunityDialog), as a shortcut of the popup itself.
    Keys.onEscapePressed: cancelled()

    // 通用 and the duty the player is in first, then every duty of the server by type and level
    // (Design: 其余按类型分组), narrowed to `term`. Built while the list is open only: a server
    // has some 850 duties.
    function dutyOptions(term) {
        const order = store.categoryOrder
        const rank = category => order.indexOf(category) < 0 ? order.length : order.indexOf(category)
        const rows = Runtime.app.dutyChoicesForRegion(regionKey === "ALL" ? "CN" : regionKey)
        const here = rows.find(duty => Number(duty.content_id) === currentDuty)
        const top = [{ value: 0, label: qsTr("通用"), group: "" }]
        if (here)
            top.push({ value: currentDuty, label: Theme.dutyChoiceLabel(here) + " · " + qsTr("当前副本"), group: "" })
        // An entry's duty the server's list does not hold is still offered, so it is never lost.
        if (dutyId > 0 && dutyId !== currentDuty && !rows.some(duty => Number(duty.content_id) === dutyId))
            top.push({ value: dutyId, label: dutyText, group: "" })
        const rest = rows.filter(duty => Number(duty.content_id) !== currentDuty).sort((a, b) =>
            rank(a.duty_category) - rank(b.duty_category)
            || String(a.duty_category).localeCompare(String(b.duty_category))
            || (a.duty_level || 0) - (b.duty_level || 0) || Number(a.content_id) - Number(b.content_id))
        const all = top.concat(rest.map(duty => ({ value: Number(duty.content_id), label: Theme.dutyChoiceLabel(duty),
                                                   group: Theme.categoryName(duty.duty_category) })))
        const wanted = term.trim().toLowerCase()
        return wanted ? all.filter(entry => entry.label.toLowerCase().includes(wanted) || String(entry.value) === wanted)
                      : all
    }

    function edit(value) {
        row = JSON.parse(JSON.stringify(value))
        const item = row.content
        type = item.type
        dutyId = item.content_id
        titleMissing = false
        bodyMissing = false
        stageTooLong = false
        stageInvalid = false
        titleProblem = ""
        bodyProblem = ""
        refusal = ""
        titleField.text = item.title
        body.text = item.body
        body.used = false
        region.currentIndex = Math.max(0, regionKeys.indexOf(item.region))
        language.currentIndex = Math.max(0, store.languageKeys.indexOf(item.language))
        // Boss and phase share one field, joined as the list and the detail show them.
        loadedStage = store.stageLabel(item)
        stage.text = loadedStage
        keptVersion = item.game_version
        const range = store.versionRange(item.game_version) || { from: "", to: "" }
        loadedFrom = range.from
        loadedTo = range.to
        versionFrom = range.from
        versionTo = range.to
        Qt.callLater(showVersions)
        keptRole = item.role
        jobs = store.jobCodes(item.role) || []
        loadedJobs = jobs.join(",")
        titleField.forceActiveFocus()
    }
    function showVersions() {
        fromBox.currentIndex = Math.max(0, fromBox.indexOfValue(versionFrom))
        toBox.currentIndex = Math.max(0, toBox.indexOfValue(versionTo))
    }

    // Only the title and the body are required (Design: 必填只有两项); a problem puts the cursor
    // where it is.
    function save() {
        refusal = ""
        titleMissing = titleField.text.trim().length === 0
        bodyMissing = body.text.trim().length === 0
        // The Collector holds a field to the character rule only when its value changes, so an
        // entry saved or published before the rule can still be edited (audit 2026-11-26 RC-6).
        const titleChanged = titleField.text !== row.content.title
        titleProblem = !titleChanged || Match.validText(titleField.text, false)
            ? "" : qsTr("标题里有看不见的特殊字符，请删掉后重新输入")
        bodyProblem = bodyLimitProblem(body.text, body.text !== row.content.body.replace(/\r\n/g, "\n"))
        // An edited BOSS / 阶段 becomes the boss, which the Collector holds to 80 characters.
        const stageChanged = stage.text !== loadedStage
        stageTooLong = stageChanged && stage.text.trim().length > 80
        stageInvalid = stageChanged && !Match.validText(stage.text.trim(), false)
        if (titleMissing || titleProblem)
            titleField.forceActiveFocus()
        else if (bodyMissing || bodyProblem)
            body.area.forceActiveFocus()
        else if (versionBackwards)
            fromBox.forceActiveFocus()
        else if (stageTooLong || stageInvalid)
            stage.forceActiveFocus()
        if (titleMissing || titleProblem || bodyMissing || bodyProblem || versionBackwards || stageTooLong || stageInvalid)
            return
        const item = row.content
        // Only a macro chooses jobs; a reminder keeps whatever the entry had.
        const jobsChanged = macro && jobs.join(",") !== loadedJobs
        const updated = Object.assign({}, item, {
            type: type, title: titleField.text,
            body: body.text === item.body.replace(/\r\n/g, "\n") ? item.body : body.text,
            content_id: dutyId, region: regionKey, language: store.languageKeys[language.currentIndex],
            boss: stageChanged ? stage.text.trim() : item.boss, phase: stageChanged ? "" : item.phase,
            game_version: versionChanged ? store.versionValue(versionFrom, versionTo) : keptVersion,
            role: jobsChanged ? jobs.join(",") : keptRole
        })
        saved({ content: updated, revision: row.revision, position: row.position, favorite: row.favorite })
    }
    /// What the Collector would refuse in a body (CommunityContent): over 1600 characters, a
    /// character no field may hold (in a changed body), or a macro line over 180 UTF-8 bytes;
    /// "" when it fits.
    function bodyLimitProblem(text, changed) {
        if (text.length > 1600)
            return qsTr("正文最多 1600 字，现在有 %1 字").arg(text.length)
        if (changed && !Match.validText(text, true))
            return qsTr("正文里有看不见的特殊字符，请删掉后重新输入")
        if (!macro)
            return ""
        const lines = text.replace(/\r\n/g, "\n").split("\n")
        const index = lines.findIndex(line => Match.utf8Bytes(line) > 180)
        return index < 0 ? ""
            : qsTr("第 %1 行太长：游戏里一行宏最多 180 个字节，大约 60 个汉字").arg(index + 1)
    }

    component Caption: Text {
        renderType: Theme.bodyRenderType
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
    }
    component Problem: Text {
        renderType: Theme.bodyRenderType
        color: Theme.red
        font.pixelSize: Theme.fs(12)
    }
    // A field of 适用范围: its name over its control.
    component Field: ColumnLayout {
        property alias label: fieldLabel.text
        Layout.fillWidth: true
        spacing: 4
        Caption { id: fieldLabel }
    }
    // What the entry had written by hand where the form now offers a choice, and a way to drop it.
    component ByHand: RowLayout {
        id: byHand
        property string value: ""
        signal cleared()
        Layout.fillWidth: true
        spacing: 4
        Caption {
            Layout.fillWidth: true
            elide: Text.ElideRight
            textFormat: Text.PlainText
            text: qsTr("原来写的是「%1」").arg(byHand.value)
        }
        AppButton {
            compact: true
            variant: "ghost"
            text: qsTr("清除")
            onClicked: byHand.cleared()
        }
    }

    RowLayout {
        Layout.fillWidth: true
        Layout.fillHeight: true
        spacing: 0

        // What the entry says.
        ColumnLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            Layout.topMargin: 18
            Layout.bottomMargin: 16
            spacing: 0

            RowLayout {
                Layout.fillWidth: true
                spacing: 12
                ColumnLayout {
                    Layout.alignment: Qt.AlignTop
                    spacing: 4
                    Caption { text: qsTr("类型") }
                    SegmentedControl {
                        objectName: "dutyCommunityEditorType"
                        implicitWidth: naturalWidth
                        // The kind is fixed once the entry exists or links to the other kind.
                        enabled: editor.fresh && !!editor.row && editor.row.content.links.length === 0
                        options: [{ value: "macro", label: qsTr("宏"), icon: "clipboard-copy" },
                                  { value: "reminder", label: qsTr("提醒"), icon: "bell" }]
                        currentValue: editor.type
                        onActivated: value => editor.type = value
                    }
                }
                ColumnLayout {
                    Layout.fillWidth: true
                    Layout.alignment: Qt.AlignTop
                    spacing: 4
                    Caption { text: qsTr("标题") }
                    StyledTextField {
                        id: titleField
                        objectName: "dutyCommunityEditorTitle"
                        Layout.fillWidth: true
                        maximumLength: 120
                        placeholderText: qsTr("起个一眼能认出来的名字")
                        Accessible.name: qsTr("标题")
                        onTextEdited: { editor.titleMissing = false; editor.titleProblem = "" }
                    }
                    Problem {
                        objectName: "dutyCommunityEditorTitleProblem"
                        visible: editor.titleMissing || editor.titleProblem !== ""
                        text: editor.titleMissing ? qsTr("请填写标题") : editor.titleProblem
                    }
                }
            }

            RowLayout {
                Layout.fillWidth: true
                Layout.topMargin: 14
                Layout.bottomMargin: 6
                Layout.minimumHeight: 28
                spacing: 8
                Caption {
                    Layout.fillWidth: true
                    Layout.alignment: Qt.AlignBottom
                    text: editor.macro ? qsTr("宏内容") : qsTr("提醒内容")
                }
                ActionMenuButton {
                    objectName: "dutyCommunityEditorInsert"
                    visible: editor.macro
                    variant: "ghost"
                    compact: true
                    text: qsTr("插入")
                    menuWidth: 320
                    groups: [{ head: "", items: editor.insertItems }]
                    onPicked: (key, item) => body.insert(item.code, item.command)
                }
            }
            DutyCommunityBodyEdit {
                id: body
                objectName: "dutyCommunityEditorBodyEdit"
                Layout.fillWidth: true
                Layout.fillHeight: true
                Layout.minimumHeight: 120
                macro: editor.macro
                problem: editor.bodyMissing
                placeholderText: editor.macro ? qsTr("一行一条指令，例如：/p 大家好 <se.1>") : qsTr("写下这个副本要注意的事")
                Connections {
                    target: body.area
                    function onTextChanged() { editor.bodyMissing = false; editor.bodyProblem = "" }
                }
            }
            RowLayout {
                Layout.fillWidth: true
                Layout.topMargin: 6
                spacing: 12
                Problem {
                    objectName: "dutyCommunityEditorBodyProblem"
                    visible: editor.bodyMissing || editor.bodyProblem !== ""
                    text: editor.bodyMissing ? qsTr("请填写正文") : editor.bodyProblem
                }
                Caption {
                    visible: !editor.bodyMissing && editor.bodyProblem === "" && editor.macro
                    Layout.fillWidth: true
                    text: body.lineCount > 15 ? qsTr("超过 15 行了，游戏里要拆成两个宏。") : qsTr("游戏里一个宏最多 15 行。")
                }
                Item { Layout.fillWidth: true }
                Caption {
                    objectName: "dutyCommunityEditorLines"
                    visible: editor.macro
                    font.weight: body.lineCount > 15 ? Font.DemiBold : Font.Normal
                    color: body.lineCount > 15 ? Theme.red : Theme.textSecondary
                    text: qsTr("%1 / 15 行").arg(body.lineCount)
                }
            }
        }

        // 适用范围: where the entry applies. Nothing here is required.
        Rectangle {
            objectName: "dutyCommunityEditorScope"
            Layout.preferredWidth: 300
            Layout.fillHeight: true
            color: Theme.eorzea ? Theme.insetBackground : Theme.windowBackground

            Rectangle {
                width: 1
                height: parent.height
                color: Theme.border
            }

            ColumnLayout {
                anchors.fill: parent
                anchors.leftMargin: 20
                anchors.rightMargin: 20
                anchors.topMargin: 18
                anchors.bottomMargin: 16
                spacing: 12

                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 3
                    Text {
                        renderType: Theme.bodyRenderType
                        text: qsTr("适用范围")
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(13)
                        font.bold: true
                    }
                    Text {
                        renderType: Theme.bodyRenderType
                        Layout.fillWidth: true
                        wrapMode: Text.Wrap
                        text: qsTr("都可以不填，不填就是都适用。")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                    }
                }

                // The list is long, so its panel opens with a search box.
                Field {
                    label: qsTr("副本")
                    StyledComboBox {
                        id: dutyBox
                        objectName: "dutyCommunityEditorDuty"
                        /// The chosen duty; 0 is 通用.
                        readonly property int value: editor.dutyId
                        function choose(value) {
                            editor.dutyId = value
                            popup.close()
                        }
                        Layout.fillWidth: true
                        Accessible.name: qsTr("副本")
                        model: []
                        keepsRowAcrossLanguages: false
                        displayText: editor.dutyText

                        popup: Popup {
                            id: dutyPanel
                            x: dutyBox.width - width
                            y: dutyBox.height + 4
                            width: Math.max(dutyBox.width, 320)
                            padding: 6
                            onAboutToShow: dutySearch.text = ""
                            onOpened: {
                                dutySearch.forceActiveFocus()
                                dutyList.positionViewAtIndex(Math.max(0, dutyList.model.findIndex(
                                    entry => entry.value === editor.dutyId)), ListView.Contain)
                            }
                            background: Rectangle {
                                radius: Theme.radiusS
                                color: Theme.surface
                                border.width: 1
                                border.color: Theme.eorzea ? Theme.gold3 : Theme.border
                            }
                            contentItem: ColumnLayout {
                                spacing: 4
                                StyledTextField {
                                    id: dutySearch
                                    objectName: "dutyCommunityEditorDutySearch"
                                    Layout.fillWidth: true
                                    placeholderText: qsTr("搜索副本名称")
                                    Accessible.name: placeholderText
                                    // Enter takes the first duty found.
                                    onAccepted: if (dutyList.count > 0) dutyBox.choose(dutyList.model[0].value)
                                }
                                ListView {
                                    id: dutyList
                                    Layout.fillWidth: true
                                    Layout.preferredHeight: Math.min(contentHeight, 320)
                                    clip: true
                                    model: dutyPanel.visible ? editor.dutyOptions(dutySearch.text) : []
                                    ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
                                    delegate: Column {
                                        id: dutyEntry
                                        required property var modelData
                                        required property int index
                                        // A type's name heads its first duty.
                                        readonly property bool heads: modelData.group !== ""
                                            && (index === 0 || dutyList.model[index - 1].group !== modelData.group)
                                        width: ListView.view ? ListView.view.width : dutyBox.width
                                        Text {
                                            renderType: Theme.bodyRenderType
                                            visible: dutyEntry.heads
                                            width: parent.width
                                            topPadding: 6
                                            bottomPadding: 3
                                            leftPadding: 8
                                            text: dutyEntry.modelData.group
                                            textFormat: Text.PlainText
                                            color: Theme.textMuted
                                            font.pixelSize: Theme.fs(11)
                                            font.weight: Font.DemiBold
                                        }
                                        ItemDelegate {
                                            id: dutyRow
                                            objectName: "dutyCommunityEditorDutyRow"
                                            width: parent.width
                                            height: 32
                                            highlighted: dutyEntry.modelData.value === editor.dutyId
                                            background: Rectangle {
                                                radius: Theme.radiusS
                                                color: dutyRow.highlighted || dutyRow.hovered ? Theme.fill : "transparent"
                                            }
                                            contentItem: Text {
                                                renderType: Theme.bodyRenderType
                                                text: dutyEntry.modelData.label
                                                textFormat: Text.PlainText
                                                color: Theme.textPrimary
                                                font.pixelSize: Theme.fs(13)
                                                verticalAlignment: Text.AlignVCenter
                                                elide: Text.ElideRight
                                            }
                                            onClicked: dutyBox.choose(dutyEntry.modelData.value)
                                        }
                                    }
                                }
                                Text {
                                    renderType: Theme.bodyRenderType
                                    visible: dutyList.count === 0
                                    Layout.fillWidth: true
                                    Layout.margins: 6
                                    text: qsTr("没有找到这个副本")
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(12)
                                }
                            }
                        }
                    }
                }

                Field {
                    label: qsTr("BOSS / 阶段")
                    StyledTextField {
                        id: stage
                        objectName: "dutyCommunityEditorStage"
                        readonly property var hints: {
                            const term = text.trim().toLowerCase()
                            return editor.stageSuggestions.filter(value => value !== text
                                && value.toLowerCase().includes(term)).slice(0, 8)
                        }
                        Layout.fillWidth: true
                        // An entry whose boss and phase together run longer still opens whole; an
                        // edit longer than the boss may be is refused under the field.
                        maximumLength: Math.max(80, editor.loadedStage.length)
                        placeholderText: qsTr("例如：BOSS 2、尾王、转场")
                        Accessible.name: qsTr("BOSS / 阶段")
                        onTextEdited: {
                            editor.stageTooLong = false
                            editor.stageInvalid = false
                            if (hints.length > 0) stageHints.open()
                            else stageHints.close()
                        }
                        onActiveFocusChanged: {
                            if (activeFocus && hints.length > 0) stageHints.open()
                            else if (!activeFocus) stageHints.close()
                        }

                        // What other entries already say; typing goes on in the field.
                        Popup {
                            id: stageHints
                            y: stage.height + 4
                            width: stage.width
                            padding: 4
                            closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutsideParent
                            background: Rectangle {
                                radius: Theme.radiusS
                                color: Theme.surface
                                border.width: 1
                                border.color: Theme.eorzea ? Theme.gold3 : Theme.border
                            }
                            contentItem: Column {
                                Repeater {
                                    model: stage.hints
                                    delegate: ItemDelegate {
                                        id: hint
                                        required property string modelData
                                        width: stageHints.availableWidth
                                        height: 30
                                        focusPolicy: Qt.NoFocus
                                        background: Rectangle {
                                            radius: Theme.radiusXs
                                            color: hint.hovered ? Theme.fill : "transparent"
                                        }
                                        contentItem: Text {
                                            renderType: Theme.bodyRenderType
                                            text: hint.modelData
                                            textFormat: Text.PlainText
                                            color: Theme.textPrimary
                                            font.pixelSize: Theme.fs(13)
                                            verticalAlignment: Text.AlignVCenter
                                            elide: Text.ElideRight
                                        }
                                        onClicked: {
                                            stage.text = hint.modelData
                                            editor.stageTooLong = false
                                            editor.stageInvalid = false
                                            stageHints.close()
                                        }
                                    }
                                }
                            }
                        }
                    }
                    Problem {
                        objectName: "dutyCommunityEditorStageProblem"
                        visible: editor.stageTooLong || editor.stageInvalid
                        text: editor.stageTooLong ? qsTr("BOSS / 阶段最多 80 个字")
                                                  : qsTr("BOSS / 阶段里有看不见的特殊字符，请删掉后重新输入")
                    }
                }

                // 职业 is a macro's; a reminder keeps whatever the entry had.
                Field {
                    label: qsTr("职业")
                    visible: editor.macro
                    DutyCommunityJobPicker {
                        id: jobPicker
                        objectName: "dutyCommunityEditorJobs"
                        Layout.fillWidth: true
                        store: editor.store
                        selection: editor.jobs
                        onSelectionEdited: chosen => editor.jobs = chosen
                    }
                    ByHand {
                        objectName: "dutyCommunityEditorRoleByHand"
                        visible: editor.roleByHand && editor.jobs.length === 0
                        value: editor.keptRole
                        onCleared: editor.keptRole = ""
                    }
                }

                // A range of the patches the game has had: 不限 → 不限 is every version.
                Field {
                    label: qsTr("游戏版本")
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 6
                        StyledComboBox {
                            id: fromBox
                            objectName: "dutyCommunityEditorVersionFrom"
                            Layout.fillWidth: true
                            Layout.preferredWidth: 1
                            Accessible.name: qsTr("开始版本")
                            textRole: "label"
                            valueRole: "value"
                            model: editor.versionOptions
                            onActivated: editor.versionFrom = currentValue
                        }
                        Text {
                            renderType: Theme.bodyRenderType
                            text: "→"
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(13)
                        }
                        StyledComboBox {
                            id: toBox
                            objectName: "dutyCommunityEditorVersionTo"
                            Layout.fillWidth: true
                            Layout.preferredWidth: 1
                            Accessible.name: qsTr("结束版本")
                            textRole: "label"
                            valueRole: "value"
                            model: editor.versionOptions
                            onActivated: editor.versionTo = currentValue
                        }
                    }
                    Problem {
                        objectName: "dutyCommunityEditorVersionProblem"
                        visible: editor.versionBackwards
                        text: qsTr("开始版本不能晚于结束版本")
                    }
                    ByHand {
                        objectName: "dutyCommunityEditorVersionByHand"
                        visible: editor.versionByHandShown
                        value: editor.keptVersion
                        onCleared: editor.keptVersion = ""
                    }
                }

                Field {
                    label: qsTr("区服")
                    StyledComboBox {
                        id: region
                        objectName: "dutyCommunityEditorRegion"
                        Layout.fillWidth: true
                        Accessible.name: qsTr("区服")
                        model: editor.regionKeys.map(key => editor.store.regionLabel(key))
                    }
                }

                Field {
                    label: qsTr("语言")
                    StyledComboBox {
                        id: language
                        Layout.fillWidth: true
                        Accessible.name: qsTr("语言")
                        model: editor.store.languageKeys.map(key => editor.store.languageLabel(key))
                    }
                }

                Item { Layout.fillHeight: true }
            }
        }
    }

    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
    RowLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 24
        Layout.rightMargin: 24
        Layout.topMargin: 12
        Layout.bottomMargin: 12
        spacing: 8
        AppButton {
            objectName: "dutyCommunityEditorSave"
            variant: "primary"
            text: qsTr("保存")
            tip: "Ctrl + S"
            onClicked: editor.save()
        }
        AppButton { objectName: "dutyCommunityEditorCancel"; text: qsTr("取消"); onClicked: editor.cancelled() }
        // A save the Collector refused comes back to the form with the reason here.
        Problem {
            objectName: "dutyCommunityEditorRefusal"
            visible: editor.refusal !== ""
            Layout.fillWidth: true
            horizontalAlignment: Text.AlignRight
            wrapMode: Text.Wrap
            textFormat: Text.PlainText
            text: editor.refusal
        }
        Text {
            renderType: Theme.bodyRenderType
            visible: editor.refusal === ""
            Layout.fillWidth: true
            horizontalAlignment: Text.AlignRight
            elide: Text.ElideRight
            text: qsTr("只保存在这台电脑上，投稿之后才会公开")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }
}
