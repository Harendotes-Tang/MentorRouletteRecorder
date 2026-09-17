import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 维护者工具 at the bottom of 捕获诊断, shown only with
// App.maintainerToolsVisible:
//   CaptureValidationEvidenceCard  桌面验证取证
//   CandidateValidationCard ...... 候选验证 and the research whitelist
//   网络适配器表 .................. ListCaptureAdapters, IPv4 already masked to /24
//   捕获链路诊断 .................. the twelve CapCell metrics, raw tokens included
//   LIVE_CAPTURE_STATUS block .... compile-time claims about this build
ColumnLayout {
    id: section
    objectName: "captureMaintainerSection"

    readonly property var capture: App.captureStatus
    readonly property var counters: App.captureCounters
    readonly property var npcap: App.npcapInfo
    readonly property var game: App.gameInfo
    readonly property var profile: App.protocolProfile

    spacing: 16

    /// One counter, or an em dash when the Collector did not report it.
    function counter(key) {
        const value = section.counters[key]
        return (value === undefined || value === null) ? Fmt.dash() : Fmt.count(value)
    }

    function rateText() {
        const value = section.counters.message_rate_per_second
        if (value === undefined || value === null)
            return Fmt.dash()
        return qsTr("%1 条/秒").arg(Number(value).toFixed(1))
    }

    function queueText() {
        const depth = section.counters.queue_depth
        const capacity = section.counters.queue_capacity
        if (depth === undefined || capacity === undefined)
            return qsTr("队列 — · 丢包 %1").arg(section.counter("packets_dropped"))
        return qsTr("队列 %1 / %2 · 丢包 %3")
               .arg(depth).arg(capacity).arg(section.counter("packets_dropped"))
    }

    function parseRateText() {
        const rate = section.counters.parse_success_rate
        return (rate === undefined || rate === null) ? Fmt.dash() : Fmt.percent(rate)
    }

    function lastValidEventText() {
        const at = section.counters.last_valid_event_at_utc
        return at ? Fmt.localTime(at) : Fmt.dash()
    }

    function stateLabel() {
        switch (String(section.capture.state || "")) {
        case "RUNNING": return qsTr("监听中")
        case "DEGRADED": return qsTr("降级监听")
        case "STARTING": return qsTr("启动中")
        case "STOPPING": return qsTr("停止中")
        case "FAILED": return qsTr("失败")
        case "STOPPED": return qsTr("已停止")
        default: return Fmt.dash()
        }
    }

    function uptimeText() {
        if (!section.capture.started_at_utc)
            return qsTr("未开始")
        return qsTr("自 %1").arg(Fmt.localTime(section.capture.started_at_utc))
    }

    function boolText(value) {
        if (value === undefined || value === null)
            return Fmt.dash()
        return value ? qsTr("是") : qsTr("否")
    }

    // -- section heading ------------------------------------------------------
    ColumnLayout {
        Layout.fillWidth: true
        Layout.topMargin: 8
        spacing: 8

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            CardKicker {
                Layout.alignment: Qt.AlignVCenter
                Layout.fillWidth: false
                text: qsTr("维护者工具")
            }

            Text {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignVCenter
                text: qsTr("以维护模式启动时才出现 · 含契约字段名与原始令牌")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                elide: Text.ElideRight
            }
        }

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            color: Theme.eorzea ? Theme.gold3 : Theme.border
        }
    }

    CaptureValidationEvidenceCard {
        Layout.fillWidth: true
    }

    CandidateValidationCard {
        Layout.fillWidth: true
    }

    // ------------------------------------------------- 适配器表 --
    Card {
        objectName: "captureAdapterTable"
        Layout.fillWidth: true
        padding: 20
        spacing: 8

        RowLayout {
            Layout.fillWidth: true
            CardKicker { text: qsTr("网络适配器（ListCaptureAdapters）") }
            Item { Layout.fillWidth: true }
            Text {
                text: qsTr("IPv4 已由采集器掩码到 /24，桌面端不持有完整地址")
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
            }
        }

        Repeater {
            model: App.captureAdapters

            delegate: RowLayout {
                required property var modelData

                Layout.fillWidth: true
                spacing: 10

                Rectangle {
                    implicitWidth: 8
                    implicitHeight: 8
                    radius: 4
                    color: modelData.is_up ? Theme.green : Theme.neutral400
                }

                Text {
                    Layout.preferredWidth: 150
                    text: modelData.friendly_name || modelData.adapter_id
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12)
                    font.bold: true
                    elide: Text.ElideRight
                }

                Text {
                    Layout.fillWidth: true
                    text: modelData.description || ""
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    elide: Text.ElideRight
                }

                Text {
                    text: (modelData.ipv4_addresses || []).join(", ") || qsTr("无 IPv4")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    font.family: Theme.figureFamily
                    font.weight: Theme.figureWeight(false)
                    font.features: ({ "tnum": 1 })
                }

                Tag {
                    visible: modelData.is_loopback
                    text: qsTr("回环")
                }

                Tag {
                    visible: modelData.recommended
                    text: qsTr("推荐")
                    variant: "ink"
                }
            }
        }

        Text {
            Layout.fillWidth: true
            visible: App.captureAdapters.length === 0
            text: qsTr("未枚举到网络适配器。点击链路面板上的“重扫适配器”重试。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
        }
    }

    // ------------------------------------------------ 原指标格 --
    CardKicker {
        Layout.fillWidth: true
        text: qsTr("捕获链路诊断（不含独立验证取证计数）")
    }

    GridLayout {
        objectName: "captureMetricGrid"
        Layout.fillWidth: true
        columns: 4
        columnSpacing: 12
        rowSpacing: 12

        CapCell {
            kicker: qsTr("Npcap")
            value: section.capture.npcap_installed
                   ? "v" + (section.capture.npcap_version || "?") : qsTr("未安装")
            sub: section.npcap.status
                 ? qsTr("%1 · WinPcap 兼容 %2 · 仅管理员 %3")
                   .arg(section.npcap.status)
                   .arg(section.boolText(section.npcap.winpcap_compatible))
                   .arg(section.boolText(section.npcap.admin_only))
                 : (section.capture.npcap_installed ? qsTr("已就绪") : qsTr("驱动缺失"))
            valueColor: section.capture.npcap_installed ? Theme.textPrimary : Theme.orange
        }

        CapCell {
            kicker: qsTr("适配器")
            value: section.capture.adapter_id || qsTr("未选择")
            sub: qsTr("可用 %1 个 · 未开始捕获时不占用任何网卡").arg(App.captureAdapters.length)
        }

        CapCell {
            kicker: qsTr("FF14 PID")
            value: section.capture.ffxiv_running
                   ? String(section.capture.ffxiv_process_id || 0) : qsTr("未运行")
            sub: qsTr("实例 %1 · build %2 · region %3")
                 .arg(section.game.instance_count !== undefined ? section.game.instance_count : 0)
                 .arg(section.capture.game_build || Fmt.dash())
                 .arg(section.capture.region || "UNKNOWN")
            valueColor: section.capture.ffxiv_running ? Theme.textPrimary : Theme.neutral400
        }

        CapCell {
            kicker: qsTr("捕获状态")
            // "监听中" is a claim about seeing traffic. When nothing has been
            // decoded it is replaced rather than qualified.
            value: (App.captureMidstreamSuspected || App.captureSilent)
                   ? qsTr("无有效报文") : section.stateLabel()
            sub: (App.captureMidstreamSuspected || App.captureSilent)
                 ? qsTr("需回到标题画面重新登录") : section.uptimeText()
            valueColor: (App.captureMidstreamSuspected || App.captureSilent)
                        ? Theme.orange : (App.capturing ? Theme.green : Theme.neutral400)
        }

        CapCell {
            kicker: qsTr("链路已观察报文")
            value: section.counter("packets_observed")
            sub: section.queueText()
        }

        CapCell {
            kicker: qsTr("消息速率")
            value: section.rateText()
            sub: qsTr("解码 %1 · 解码失败 %2 · 连接 %3")
                 .arg(section.counter("messages_decoded"))
                 .arg(section.counter("decode_errors"))
                 .arg(section.counter("connection_count"))
        }

        CapCell {
            kicker: qsTr("解析成功率")
            value: section.parseRateText()
            sub: qsTr("成功 %1 · 失败 %2 · 去重 %3 · 与记录无关 %4")
                 .arg(section.counter("parse_ok_count"))
                 .arg(section.counter("parse_fail_count"))
                 .arg(section.counter("duplicate_count"))
                 .arg(section.counter("ignored_count"))
        }

        CapCell {
            kicker: qsTr("最近有效事件")
            value: section.lastValidEventText()
            sub: section.counters.last_valid_event_kind
                 ? qsTr("kind %1").arg(section.counters.last_valid_event_kind)
                 : qsTr("解析器最近一次产出语义事件的时间")
        }

        CapCell {
            kicker: qsTr("最近错误码")
            value: section.capture.last_error_code || qsTr("无")
            sub: section.capture.last_error_code === "UNAVAILABLE"
                 ? qsTr("本机不具备开始捕获的前提条件")
                 : qsTr("来自 CaptureStatus.last_error_code")
            valueColor: section.capture.last_error_code ? Theme.orange : Theme.textPrimary
        }

        CapCell {
            kicker: qsTr("协议档案")
            value: section.profile.profile_id || qsTr("无")
            sub: qsTr("status %1 · build %2")
                 .arg(App.protocolProfileStatus)
                 .arg(section.profile.game_build || Fmt.dash())
            valueColor: App.protocolProfileStatus === "VERIFIED" ? Theme.textPrimary : Theme.orange
        }

        CapCell {
            kicker: qsTr("Oodle 签名")
            value: section.capture.oodle_signature_source || Fmt.dash()
            sub: qsTr("档案 %1 · 状态 %2")
                 .arg(section.capture.oodle_profile_id || Fmt.dash())
                 .arg(section.capture.oodle_profile_status || Fmt.dash())
            valueColor: section.capture.oodle_profile_status === "VERIFIED"
                        ? Theme.textPrimary : Theme.orange
        }

        CapCell {
            kicker: qsTr("边界常量")
            // Never invented: an absent monitor_type is an unreported fact,
            // not evidence that WinPCap mode is in use.
            value: section.capture.monitor_type || Fmt.dash()
            sub: qsTr("注入式 hook %1 · 读取游戏可执行文件 %2")
                 .arg(section.boolText(section.capture.injected_hook_enabled))
                 .arg(section.boolText(App.readsGameExecutable))
        }
    }

    // These lines are a claim about this build and must never be softened
    // (docs/live-validation-guide.md section 0 states what
    // VERIFIED_POP_TO_EXIT covers).
    InsetBox {
        objectName: "liveCaptureStatusBox"
        Layout.fillWidth: true
        Layout.preferredHeight: statusText.implicitHeight + 20

        Text {
            id: statusText
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            anchors.leftMargin: 12
            anchors.rightMargin: 12
            text: "LIVE_CAPTURE_STATUS = " + App.liveCaptureStatus
                  + "\nPROTOCOL_PROFILE_STATUS = " + App.protocolProfileStatus
                  + "\nOODLE_MODE = " + (App.oodleMode || Fmt.dash())
                  + "\nREADS_GAME_EXECUTABLE = " + (App.readsGameExecutable ? "true" : "false")
                  + "\nPUBLIC_DISTRIBUTION_READY = " + (App.publicDistributionReady ? "true" : "false")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            font.family: Theme.monoFamily
            lineHeight: 1.5
        }
    }
}
