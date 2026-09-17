import QtQuick
import QtQuick.Layouts
import MentorRecorder

// 链路：FF14 进程 → Npcap → 适配器 → 协议档案.
//
// A title row (kicker, one line saying whether the chain is whole or what blocks
// it first, the two rescan buttons) over four equal columns separated by 1 px
// dividers. Each column: a dot (green ready / grey waiting / orange problem) and
// its name, a 16 px figure value, a small sub line.
//   FF14 进程  capture.ffxiv_running / ffxiv_process_id
//   Npcap      GetStatus.npcap.version / status, capture.npcap_*
//   适配器     capture.adapter_id joined on ListCaptureAdapters; captureSettings.adapter_id
//   协议档案   capture.profile_status / profile_origin, App.calibration.state;
//              maintainers read the profile id and the raw status
Card {
    id: chain
    objectName: "captureChainPanel"

    readonly property var capture: App.captureStatus
    readonly property var npcap: App.npcapInfo
    readonly property var profile: App.protocolProfile
    readonly property bool maintainer: App.maintainerToolsVisible

    readonly property bool gameRunning: !!chain.capture.ffxiv_running
    readonly property string npcapStatus: String(chain.npcap.status || "")
    readonly property bool npcapReady: App.npcapInstalled
                                       && (chain.npcapStatus === "" || chain.npcapStatus === "READY")
    readonly property string adapterId: String(chain.capture.adapter_id || "")
    readonly property var adapterRow: {
        for (let index = 0; index < App.captureAdapters.length; ++index) {
            if (App.captureAdapters[index].adapter_id === chain.adapterId)
                return App.captureAdapters[index]
        }
        return null
    }
    readonly property bool adapterStale: {
        for (let index = 0; index < App.captureAdapters.length; ++index) {
            if (App.captureAdapters[index].preference_stale)
                return true
        }
        return false
    }
    readonly property string profileStatus: String(chain.capture.profile_status || "")
    readonly property string profileOrigin: String(chain.capture.profile_origin || "")
    readonly property bool profileReady: chain.profileStatus === "VERIFIED"
    readonly property string calibrationState: App.calibration ? App.calibration.state : "IDLE"
    // A shared profile records while calibration stays armed beside it, so a
    // verified profile is never "calibrating".
    readonly property bool calibrating: !chain.profileReady
        && ["WAITING", "OBSERVING", "READY", "BLOCKED"].indexOf(chain.calibrationState) >= 0
    readonly property bool buildUnknown: !chain.capture.game_build || !chain.capture.region
                                         || chain.capture.region === "UNKNOWN"
    readonly property string errorCode: {
        const code = String(chain.capture.last_error_code || "")
        return code === "NONE" ? "" : code
    }
    readonly property bool silent: App.captureMidstreamSuspected || App.captureSilent
                                   || App.recording.silent

    // -- 标题行 ------------------------------------------------------------
    // The first thing in the chain that stops a record, in the order the
    // chain is walked. Returns [sentence, tone]; tone is "ok", "wait" or "bad".
    function summary() {
        if (!chain.npcapReady)
            return App.npcapInstalled
                   ? [qsTr("Npcap 暂时用不了 · 按上方说明处理后点“重新检测”"), "bad"]
                   : [qsTr("Npcap 未安装 · 安装前只能手动记录"), "bad"]
        if (!chain.gameRunning)
            return [qsTr("等待游戏启动 · 档案按版本匹配，游戏启动后才知道能否记录"), "wait"]
        if (chain.calibrating) {
            if (chain.calibrationState === "READY")
                return [qsTr("校准完成，等你核对 · 见上方校准卡片"), "bad"]
            if (chain.calibrationState === "BLOCKED")
                return [qsTr("本机校准无法继续 · 见上方校准卡片"), "bad"]
            return [qsTr("游戏更新后正在重新校准 · 进度见上方校准卡片"), "bad"]
        }
        if (chain.profileStatus === "")
            return [qsTr("正在核对协议档案…"), "wait"]
        if (!chain.profileReady) {
            // The same words as the 协议档案 notice above.
            if (chain.buildUnknown)
                return [qsTr("尚未识别游戏版本或区服 · 暂时不会自动记录"), "bad"]
            if (chain.profileStatus === "UNSUPPORTED_BUILD")
                return [qsTr("当前游戏版本没有可用档案 · 暂时不会自动记录"), "bad"]
            if (chain.profileStatus === "AMBIGUOUS")
                return [qsTr("同一版本有两份档案，已全部拒绝 · 暂时不会自动记录"), "bad"]
            return [qsTr("尚未匹配到档案 · 暂时不会自动记录"), "bad"]
        }
        if (chain.adapterStale)
            return [qsTr("记住的网卡上没有游戏流量 · 见上方提示"), "bad"]
        if (chain.errorCode.length > 0)
            return [qsTr("采集受阻 · %1").arg(Fmt.captureErrorLabel(chain.errorCode).replace(/。$/, "")), "bad"]
        if (chain.adapterId.length === 0 || !App.capturing)
            return [qsTr("正在启动监听…"), "wait"]
        if (chain.silent)
            return [qsTr("链路已就绪，但还没有解析出游戏数据 · 见上方提示"), "bad"]
        if (App.recording.blocked)
            return [App.recording.message, "bad"]
        return [qsTr("FF14 → Npcap → 适配器 → 协议档案 全部就绪"), "ok"]
    }
    readonly property var summaryLine: chain.summary()

    // -- 四列 ----------------------------------------------------------------
    function npcapSub() {
        if (!App.npcapInstalled)
            return qsTr("驱动缺失")
        switch (chain.npcapStatus) {
        case "READY": return qsTr("WinPcap 兼容模式")
        case "NOT_WINPCAP_COMPATIBLE": return qsTr("未启用 WinPcap 兼容模式")
        case "NPCAP_ADMIN_ONLY": return qsTr("仅限管理员使用")
        case "LOAD_FAILED": return qsTr("驱动文件无法加载")
        case "": return qsTr("已就绪")
        default: return qsTr("暂时用不了")
        }
    }

    function adapterValue() {
        if (chain.adapterId.length === 0)
            return qsTr("未选择")
        const row = chain.adapterRow
        const name = (row && (row.friendly_name || row.description)) || chain.capture.adapter_description
        if (name)
            return name
        // The opaque id is a maintainer's detail.
        return chain.maintainer ? chain.adapterId : qsTr("已选择")
    }

    function adapterState() {
        if (chain.adapterStale)
            return "bad"
        return chain.adapterId.length > 0 && App.capturing ? "ok" : "wait"
    }

    function profileValue() {
        if (chain.maintainer)
            return chain.profile.profile_id || chain.capture.profile_id || qsTr("无")
        // Plain words a player can read, never an id.
        if (!chain.gameRunning)
            return qsTr("待游戏启动")
        if (chain.profileReady)
            return chain.profileOrigin === "LOCAL_CALIBRATION" ? qsTr("本机校准")
                 : chain.profileOrigin === "SHARED_CALIBRATION" ? qsTr("共享校准")
                 : qsTr("档案匹配")
        if (chain.calibrating)
            return chain.calibrationState === "READY" ? qsTr("待核对")
                 : chain.calibrationState === "BLOCKED" ? qsTr("需诊断") : qsTr("校准中")
        if (chain.profileStatus === "")
            return qsTr("检查中")
        if (chain.buildUnknown)
            return qsTr("未识别版本")
        if (chain.profileStatus === "UNSUPPORTED_BUILD")
            return qsTr("版本不支持")
        if (chain.profileStatus === "AMBIGUOUS")
            return qsTr("档案冲突")
        return qsTr("未匹配")
    }

    function profileSub() {
        if (chain.maintainer)
            return qsTr("%1 · build %2").arg(App.protocolProfileStatus)
                   .arg(chain.profile.game_build || chain.capture.game_build || Fmt.dash())
        if (!chain.gameRunning)
            return qsTr("待游戏启动后校验")
        if (chain.profileReady)
            return qsTr("与游戏版本匹配")
        if (chain.calibrating)
            return qsTr("游戏更新后重新校准")
        if (chain.profileStatus === "")
            return qsTr("正在核对")
        if (chain.buildUnknown)
            return qsTr("尚未识别游戏版本或区服")
        if (chain.profileStatus === "AMBIGUOUS")
            return qsTr("同一版本有两份档案")
        return qsTr("当前版本暂时不会自动记录")
    }

    function profileState() {
        if (!chain.gameRunning || chain.profileStatus === "")
            return "wait"
        return chain.profileReady ? "ok" : "bad"
    }

    function dotColor(tone) {
        return tone === "ok" ? Theme.green : tone === "bad" ? Theme.orange : Theme.neutral400
    }

    readonly property var nodes: [
        {
            key: "game", label: qsTr("FF14 进程"),
            value: chain.gameRunning ? "ffxiv_dx11.exe" : qsTr("未运行"),
            sub: chain.gameRunning ? qsTr("PID %1").arg(chain.capture.ffxiv_process_id || Fmt.dash())
                                   : qsTr("启动游戏后自动检测"),
            tone: chain.gameRunning ? "ok" : "wait"
        },
        {
            key: "npcap", label: qsTr("Npcap"),
            value: App.npcapInstalled
                   ? ((chain.npcap.version || chain.capture.npcap_version)
                      ? "v" + (chain.npcap.version || chain.capture.npcap_version) : qsTr("已安装"))
                   : qsTr("未安装"),
            sub: chain.npcapSub(),
            tone: chain.npcapReady ? "ok" : "bad"
        },
        {
            key: "adapter", label: qsTr("适配器"),
            value: chain.adapterValue(),
            sub: chain.adapterStale ? qsTr("记住的网卡上没有游戏流量")
                 : App.captureSettings.adapter_id ? qsTr("手动指定") : qsTr("自动选择（有 FF14 连接）"),
            tone: chain.adapterState()
        },
        {
            key: "profile", label: qsTr("协议档案"),
            value: chain.profileValue(),
            sub: chain.profileSub(),
            tone: chain.profileState()
        }
    ]

    horizontalPadding: 24
    verticalPadding: 18
    spacing: 14

    RowLayout {
        Layout.fillWidth: true
        spacing: 12

        CardKicker {
            Layout.alignment: Qt.AlignVCenter
            Layout.fillWidth: false
            text: qsTr("链路")
        }

        Text {
            objectName: "captureChainSummary"
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignVCenter
            text: chain.summaryLine[0]
            textFormat: Text.PlainText
            color: chain.summaryLine[1] === "bad" ? Theme.orangeText : Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            elide: Text.ElideRight
        }

        AppButton {
            objectName: "rescanGameButton"
            variant: "ghost"
            text: qsTr("重扫 FF14")
            onClicked: App.rescanGame()
        }

        AppButton {
            objectName: "rescanAdaptersButton"
            variant: "ghost"
            text: qsTr("重扫适配器")
            onClicked: App.rescanAdapters()
        }
    }

    // grid-template-columns:repeat(4,1fr); cells padding:0 18px with a divider
    // between them - the first one starts flush with the kicker. The row gets
    // its height from the tallest cell.
    RowLayout {
        Layout.fillWidth: true
        spacing: 0

        Repeater {
            model: chain.nodes

            delegate: Item {
                id: cell
                required property var modelData
                required property int index

                objectName: "captureChain_" + modelData.key
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                Layout.alignment: Qt.AlignTop
                implicitHeight: cellColumn.implicitHeight

                Rectangle {
                    anchors.left: parent.left
                    anchors.top: parent.top
                    anchors.bottom: parent.bottom
                    width: 1
                    visible: cell.index > 0
                    color: Theme.border
                }

                ColumnLayout {
                    id: cellColumn
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.leftMargin: cell.index > 0 ? 18 : 0
                    anchors.rightMargin: 18
                    spacing: 6

                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 8

                        Rectangle {
                            Layout.alignment: Qt.AlignVCenter
                            implicitWidth: 8
                            implicitHeight: 8
                            radius: 4
                            color: chain.dotColor(cell.modelData.tone)
                        }

                        Text {
                            Layout.fillWidth: true
                            text: cell.modelData.label
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(11)
                            elide: Text.ElideRight
                        }
                    }

                    Text {
                        objectName: "captureChainValue_" + cell.modelData.key
                        Layout.fillWidth: true
                        text: cell.modelData.value
                        textFormat: Text.PlainText
                        color: cell.modelData.tone === "bad" && cell.modelData.key === "npcap"
                               ? Theme.orangeText : Theme.textPrimary
                        font.family: Theme.figureFamily
                        font.pixelSize: Theme.fs(16)
                        font.weight: Theme.figureWeight(true)
                        font.features: ({ "tnum": 1 })
                        elide: Text.ElideRight
                    }

                    Text {
                        objectName: "captureChainSub_" + cell.modelData.key
                        Layout.fillWidth: true
                        text: cell.modelData.sub
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                        elide: Text.ElideRight
                    }
                }
            }
        }
    }
}
