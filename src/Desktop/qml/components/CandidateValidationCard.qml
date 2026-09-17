import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 候选档案验证与“对照核对要保存哪些报文”。
// 报文按用途分组，显示候选档案声明的中文 label（$defs/CandidateHypothesis，由
// Collector 随 CaptureStatus 下发）。十六进制 opcode 仅在“高级”中出现：一般用户
// 按“选用推荐”即可，不需要理解任何编号。
Card {
    id: card
    objectName: "candidateValidationCard"
    padding: 16
    spacing: 10
    readonly property var controller: App.candidates
    readonly property bool candidateEnabled: !!App.captureSettings.candidate_validation_enabled
    readonly property var whitelist: App.captureSettings.research_payload_opcodes || []
    readonly property var hypotheses: App.captureStatus.candidate_hypotheses || []
    readonly property var eligible: hypotheses.filter(function(h) { return !!h.research_eligible })
    // 推荐集：排本 + 副本查找器两组，加上"副本与职业识别"组（区域初始化 136 字节、
    // 职业信息 16 字节，用来核对副本名与职业的字段偏移）。进本／换区那组只是时间锚点，
    // 默认不保存负载。
    readonly property var recommended: eligible
        .filter(function(h) { return h.group === "queue" || h.group === "finder" || h.group === "identity" })
        .map(function(h) { return h.opcode })
    readonly property bool recommendedSelected: recommended.length > 0
        && recommended.every(function(op) { return card.whitelist.indexOf(op) >= 0 })
    readonly property var groupTitles: [
        { key: "queue", title: qsTr("排本") },
        { key: "finder", title: qsTr("副本查找器（弹窗）") },
        { key: "identity", title: qsTr("副本与职业识别") },
        { key: "zone_load", title: qsTr("进本／换区（仅作时间锚点）") }
    ]
    readonly property var groupedEligible: {
        const known = {}
        const result = []
        for (const group of card.groupTitles) {
            known[group.key] = true
            const items = card.eligible.filter(function(h) { return h.group === group.key })
            if (items.length > 0)
                result.push({ title: group.title, items: items })
        }
        const other = card.eligible.filter(function(h) { return !known[h.group] })
        if (other.length > 0)
            result.push({ title: qsTr("其他"), items: other })
        return result
    }
    // 白名单里当前档案没有描述的项：手动输入的，或来自别的客户端版本。
    readonly property var customEntries: whitelist.filter(function(op) {
        return !card.hypotheses.some(function(h) { return h.opcode === op })
    })
    readonly property bool editable: card.candidateEnabled && !card.controller.candidateSettingsBusy
    property bool advanced: false
    property string inputError: ""

    function toggleOpcode(opcode) {
        let next = whitelist.slice()
        const index = next.indexOf(opcode)
        if (index >= 0) next.splice(index, 1)
        else next.push(opcode)
        inputError = ""
        controller.setResearchOpcodes(next)
    }
    function selectRecommended() {
        let next = whitelist.slice()
        for (const opcode of recommended)
            if (next.indexOf(opcode) < 0) next.push(opcode)
        inputError = ""
        controller.setResearchOpcodes(next)
    }
    function addOpcode() {
        const value = opcodeInput.text.trim().toLowerCase()
        if (!/^0x[0-9a-f]{4}$/.test(value)) {
            inputError = qsTr("请输入 0x 加四位十六进制，例如 0x0323。")
            return
        }
        if (whitelist.indexOf(value) >= 0) {
            inputError = qsTr("该报文已在保存列表中。")
            return
        }
        inputError = ""
        controller.setResearchOpcodes(whitelist.concat([value]))
    }

    RowLayout {
        Layout.fillWidth: true
        CardKicker { text: qsTr("候选档案验证") }
        Tag { text: "CANDIDATE"; variant: "accent" }
        Item { Layout.fillWidth: true }
        AppButton {
            objectName: "candidateEnableButton"
            text: card.controller.candidateSettingsBusy ? qsTr("正在保存…")
                  : card.candidateEnabled ? qsTr("已开启 · 点击关闭") : qsTr("开启候选验证")
            variant: card.candidateEnabled ? "primary" : "secondary"
            enabled: App.captureSettingsSupported && !card.controller.candidateSettingsBusy
            onClicked: card.controller.setCandidateEnabled(!card.candidateEnabled)
            Accessible.description: qsTr("候选观测不计入正式统计；VERIFIED 解析可独立生成正式记录。运行中更改可能需要停止并重新开始捕获。")
        }
    }
    Text {
        Layout.fillWidth: true
        text: qsTr("档案：%1　·　保留观测：%2 条")
              .arg(App.captureStatus.candidate_profile_id || qsTr("尚未匹配"))
              .arg(App.captureStatus.candidate_observation_count || 0)
        textFormat: Text.PlainText
        color: Theme.textPrimary
        font.pixelSize: Theme.fs(13)
        wrapMode: Text.Wrap
    }
    Text {
        Layout.fillWidth: true
        text: qsTr("仅供事后核对，不计入正式记录、统计、成就与播报。更改模式后，请停止并重新开始捕获。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    Rectangle { Layout.fillWidth: true; Layout.preferredHeight: 1; color: Theme.border }

    // ---- 对照核对要保存的报文 -------------------------------------------------
    RowLayout {
        Layout.fillWidth: true
        spacing: 8
        FieldLabel { text: qsTr("对照核对要保存的报文") }
        Text {
            objectName: "researchWhitelistSummary"
            Layout.fillWidth: true
            text: card.whitelist.length === 0
                  ? qsTr("尚未选择 · 不保存任何报文内容")
                  : qsTr("已选 %1 项").arg(card.whitelist.length)
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            elide: Text.ElideRight
        }
        AppButton {
            objectName: "selectRecommendedWhitelist"
            compact: true
            variant: card.recommendedSelected ? "secondary" : "primary"
            text: card.recommendedSelected
                  ? qsTr("已选用推荐（%1 项）").arg(card.recommended.length)
                  : qsTr("选用推荐（排本 + 弹窗 + 识别，%1 项）").arg(card.recommended.length)
            enabled: card.editable && card.recommended.length > 0 && !card.recommendedSelected
            onClicked: card.selectRecommended()
            Accessible.description: qsTr("勾选排本、副本查找器与副本及职业识别组里可以保存的报文；进本／换区那组默认不保存。")
        }
        AppButton {
            objectName: "clearResearchWhitelist"
            compact: true
            text: qsTr("全部清除")
            enabled: card.whitelist.length > 0 && !card.controller.candidateSettingsBusy
            onClicked: {
                card.inputError = ""
                card.controller.setResearchOpcodes([])
            }
            Accessible.description: qsTr("验证关闭时也可清除；既有证据仍保留。")
        }
    }
    Text {
        objectName: "researchPayloadPolicyText"
        Layout.fillWidth: true
        text: qsTr("勾选后仅保存这些报文的完整负载，每条最多 512 字节；超出上限不保存负载，不截取前缀。未勾选的报文不保存内容。"
                   + "一般按“选用推荐”即可，不需要理解报文编号。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }
    Text {
        Layout.fillWidth: true
        visible: !card.candidateEnabled
        text: qsTr("先在右上角开启候选验证，才能更改保存列表。")
        color: Theme.textMuted
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }
    Text {
        Layout.fillWidth: true
        visible: card.eligible.length === 0
        text: qsTr("当前没有匹配的候选档案，无法按用途列出报文（需要国服 2026.08.05 客户端）。")
        color: Theme.textMuted
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    Repeater {
        model: card.groupedEligible
        delegate: ColumnLayout {
            required property var modelData
            Layout.fillWidth: true
            spacing: 4
            Text {
                text: modelData.title
                color: Theme.textMuted
                font.pixelSize: Theme.fs(11)
                font.bold: true
            }
            Flow {
                Layout.fillWidth: true
                spacing: 6
                Repeater {
                    model: modelData.items
                    delegate: AppButton {
                        required property var modelData
                        readonly property string opcode: modelData.opcode
                        readonly property string label: modelData.label || modelData.name || modelData.opcode
                        readonly property bool selected: card.whitelist.indexOf(opcode) >= 0
                        objectName: "researchOpcode_" + opcode
                        compact: true
                        text: (selected ? "✓ " : "") + label + (card.advanced ? "  " + opcode : "")
                        variant: selected ? "primary" : "secondary"
                        enabled: card.editable
                        Accessible.name: label + " " + opcode
                                         + (selected ? qsTr("，已选择，点击移除") : qsTr("，未选择，点击加入"))
                        onClicked: card.toggleOpcode(opcode)
                    }
                }
            }
        }
    }

    ColumnLayout {
        Layout.fillWidth: true
        visible: card.customEntries.length > 0
        spacing: 4
        Text {
            text: qsTr("手动加入")
            color: Theme.textMuted
            font.pixelSize: Theme.fs(11)
            font.bold: true
        }
        Flow {
            Layout.fillWidth: true
            spacing: 6
            Repeater {
                model: card.customEntries
                delegate: AppButton {
                    required property string modelData
                    objectName: "researchOpcode_" + modelData
                    compact: true
                    text: "✓ " + modelData
                    variant: "primary"
                    enabled: card.editable
                    Accessible.name: modelData + qsTr("，已选择，点击移除")
                    onClicked: card.toggleOpcode(modelData)
                }
            }
        }
    }

    RowLayout {
        Layout.fillWidth: true
        AppButton {
            objectName: "researchAdvancedToggle"
            variant: "ghost"
            compact: true
            text: card.advanced ? qsTr("收起高级选项") : qsTr("高级：按编号手动加入…")
            onClicked: card.advanced = !card.advanced
        }
        Item { Layout.fillWidth: true }
        AppButton { text: qsTr("对照核对"); onClicked: App.navigate(6) }
        AppButton {
            text: card.controller.exporting ? qsTr("正在导出…") : qsTr("导出证据")
            enabled: !card.controller.exporting
            onClicked: card.controller.exportEvidence()
        }
    }
    RowLayout {
        Layout.fillWidth: true
        visible: card.advanced
        StyledTextField {
            id: opcodeInput
            objectName: "researchOpcodeInput"
            Layout.preferredWidth: 170
            maximumLength: 6
            placeholderText: "0x0000"
            enabled: card.editable
            Accessible.name: qsTr("按编号新增要保存的报文")
            onAccepted: card.addOpcode()
        }
        AppButton {
            text: qsTr("加入")
            enabled: opcodeInput.enabled && opcodeInput.text.length > 0
            onClicked: card.addOpcode()
        }
        Text {
            objectName: "researchPayloadEligibilityText"
            Layout.fillWidth: true
            text: qsTr("只接受候选档案已声明、不混淆且长度上限不超过 512 字节的报文；其余会被拒绝。")
            color: Theme.textMuted
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }
    Text {
        objectName: "researchPayloadRetentionText"
        Layout.fillWidth: true
        text: qsTr("每条完整负载 ≤512 字节，最多 20,000 条／30 天。关闭不会抹除既有证据；专用导出会附带这些负载，请只交给维护者。")
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }
    Text {
        Layout.fillWidth: true
        visible: text.length > 0
        text: card.inputError || card.controller.candidateSettingsError || card.controller.exportError
        textFormat: Text.PlainText
        color: Theme.red
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WrapAnywhere
    }
    StyledTextField {
        Layout.fillWidth: true
        visible: !!card.controller.exportResult.target_path
        text: card.controller.exportResult.target_path || ""
        readOnly: true
        Accessible.name: qsTr("已导出证据路径，SHA-256 边车位于同目录")
    }
}
