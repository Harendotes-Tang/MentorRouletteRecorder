import QtQuick
import MentorRecorder 1.0

// 链路 as data: FF14 进程 → Npcap → 适配器 → 协议档案, read off the contract and
// turned into the four nodes of the hero card and its one-line summary. No
// layout here, so the hero card and the page's copy (CaptureHeroCopy) read the
// same verdicts.
//   FF14 进程  capture.ffxiv_running / ffxiv_process_id, and while the game is
//              closed the installed capture.game_build the Collector read off disk
//   Npcap      GetStatus.npcap.version / status, capture.npcap_*
//   适配器     capture.adapter_id joined on ListCaptureAdapters; captureSettings.adapter_id
//   协议档案   capture.profile_status / profile_origin, App.calibration.state;
//              maintainers read the profile id and the raw status
// A node's tone is "ok", "warn", "bad" or "off"; the summary's is "ok", "wait"
// or "bad" (a waiting summary reads in the same grey as the nodes' sub lines).
QtObject {
    id: chain

    readonly property var capture: Runtime.app.captureStatus
    readonly property var npcap: Runtime.app.npcapInfo
    readonly property var profile: Runtime.app.protocolProfile
    readonly property bool maintainer: Runtime.app.maintainerToolsVisible

    readonly property bool gameRunning: !!chain.capture.ffxiv_running
    // The 记录对象 card's own verdict, so the two never disagree: a choice is
    // asked for only while a client is listed to choose from. With the only
    // client closed the list is empty and this is ordinary waiting.
    readonly property bool choosingGame: Runtime.app.gameSelection.selectionRequired
    readonly property int gameCount: Runtime.app.gameSelection.choices.length
    readonly property string npcapStatus: String(chain.npcap.status || "")
    // npcap_installed is required in every capture status, so without it - no
    // pipe yet, a Collector that refused to start or exited, a status read that
    // failed - nothing is known about Npcap and nothing is said about it
    // (review K2-1). CaptureHeroCopy reads the same verdict.
    readonly property bool statusKnown: Runtime.app.backendConnected
                                        && Runtime.app.captureStatus.npcap_installed !== undefined
    readonly property bool npcapReady: Runtime.app.npcapInstalled
                                       && (chain.npcapStatus === "" || chain.npcapStatus === "READY")
    readonly property string adapterId: String(chain.capture.adapter_id || "")
    readonly property var adapterRow: {
        for (let index = 0; index < Runtime.app.captureAdapters.length; ++index) {
            if (Runtime.app.captureAdapters[index].adapter_id === chain.adapterId)
                return Runtime.app.captureAdapters[index]
        }
        return null
    }
    readonly property bool adapterStale: {
        for (let index = 0; index < Runtime.app.captureAdapters.length; ++index) {
            if (Runtime.app.captureAdapters[index].preference_stale)
                return true
        }
        return false
    }
    readonly property string profileStatus: String(chain.capture.profile_status || "")
    readonly property string profileOrigin: String(chain.capture.profile_origin || "")
    readonly property bool profileReady: chain.profileStatus === "VERIFIED"
    readonly property string calibrationState: Runtime.app.calibration ? Runtime.app.calibration.state : "IDLE"
    // A shared profile records while calibration stays armed beside it, so a
    // verified profile is never "calibrating".
    readonly property bool calibrating: !chain.profileReady
        && ["WAITING", "OBSERVING", "READY", "BLOCKED"].indexOf(chain.calibrationState) >= 0
    // The Collector reads the installed version off disk, so a closed game no
    // longer means an unknown one: only a build it has never located leaves the
    // chain unable to say anything about the profile.
    readonly property bool regionUnknown: !chain.capture.region || chain.capture.region === "UNKNOWN"
    readonly property bool buildUnknown: !chain.capture.game_build || chain.regionUnknown
    // 已安装的游戏版本: the launcher may still patch over it, so it is never
    // called 当前 or 最新 while the game is closed.
    readonly property string installedVersion: Runtime.fmt.gameVersionLabel(chain.capture.game_build)
    // 共享校准 is fetching, verifying or waiting for consent: the calibration
    // row below says what happens next, and this summary must not talk past it.
    readonly property var sharedCalibration: Runtime.app.calibration ? Runtime.app.calibration.shared : null
    readonly property string sharedView: chain.sharedCalibration
                                         ? String(chain.sharedCalibration.view || "none") : "none"
    readonly property bool sharedPending: ["fetching", "verifying", "consent"]
                                          .indexOf(chain.sharedView) >= 0
    readonly property string errorCode: {
        const code = String(chain.capture.last_error_code || "")
        return code === "NONE" ? "" : code
    }
    readonly property bool silent: Runtime.app.captureMidstreamSuspected || Runtime.app.captureSilent
                                   || Runtime.app.recording.silent

    // -- 总结 ----------------------------------------------------------------
    // The first thing in the chain that stops a record, in the order the
    // chain is walked. Returns [sentence, tone]; tone is "ok", "wait" or "bad".
    function summary() {
        if (!chain.statusKnown)
            return [Runtime.app.backendConnected ? qsTr("正在获取自动记录状态…") : qsTr("正在连接自动记录服务…"), "wait"]
        if (Runtime.app.validationActive && String(Runtime.app.validationStatus.reason || "") === "WAITING_RESTART")
            return [qsTr("验证正在等待所选游戏重启"), "wait"]
        if (chain.choosingGame)
            return [Runtime.app.gameSelection.selectionMessage, "bad"]
        if (!chain.npcapReady)
            return Runtime.app.npcapInstalled
                   ? [qsTr("Npcap 暂时用不了 · 按上方说明处理后点“重新检测”"), "bad"]
                   : [qsTr("Npcap 未安装 · 安装前只能手动记录"), "bad"]
        // 游戏未运行. The chain is never whole here, but with the installed
        // version known the profile question already has a real answer, so it
        // is given instead of being deferred to the game's launch.
        if (!chain.gameRunning) {
            if (chain.buildUnknown)
                return [qsTr("等待游戏启动 · 启动后会检查能否自动记录"), "wait"]
            if (chain.profileStatus === "")
                return [qsTr("等待游戏启动 · 正在检查已安装的游戏版本"), "wait"]
            if (chain.profileReady)
                return [qsTr("等待游戏启动 · 已支持安装的游戏版本，启动后会自动记录"), "wait"]
            if (chain.calibrationState === "READY")
                return [qsTr("等待游戏启动 · 请先在「本机校准」中核对结果"), "bad"]
            if (chain.calibrationState === "BLOCKED")
                return [qsTr("等待游戏启动 · 校准遇到问题，请查看「本机校准」"), "bad"]
            if (chain.sharedPending)
                return [qsTr("等待游戏启动 · 请查看「共享校准」中的进度"), "bad"]
            if (chain.calibrating)
                return [qsTr("等待游戏启动 · 当前安装的游戏版本需要重新校准"), "bad"]
            if (chain.profileStatus === "AMBIGUOUS")
                return [qsTr("等待游戏启动 · 游戏版本支持文件有冲突，暂时不能自动记录"), "bad"]
            return [qsTr("等待游戏启动 · 当前安装的游戏版本暂不支持自动记录"), "bad"]
        }
        if (chain.calibrating) {
            if (chain.calibrationState === "READY")
                return [qsTr("请在「本机校准」中核对结果"), "bad"]
            if (chain.calibrationState === "BLOCKED")
                return [qsTr("校准遇到问题，请查看「本机校准」"), "bad"]
            // 适配器 就绪 is a claim about a running capture; armed-but-waiting
            // (WAITING) or an error code keeps to the profile alone.
            return [chain.adapterTone() === "ok" && chain.errorCode.length === 0
                    ? qsTr("游戏和网卡已连接 · 正在重新校准")
                    : qsTr("正在重新校准 · 进度见「本机校准」"), "bad"]
        }
        if (chain.profileStatus === "")
            return [qsTr("正在检查当前游戏版本能否自动记录…"), "wait"]
        if (!chain.profileReady) {
            // The same words as the 协议档案 card below.
            if (chain.regionUnknown && chain.capture.game_build)
                return [qsTr("尚未识别游戏区服 · 暂时不会自动记录"), "bad"]
            if (chain.buildUnknown)
                return [qsTr("尚未识别游戏版本或区服 · 暂时不会自动记录"), "bad"]
            if (chain.profileStatus === "UNSUPPORTED_BUILD")
                return [qsTr("当前游戏版本暂不支持自动记录"), "bad"]
            if (chain.profileStatus === "AMBIGUOUS")
                return [qsTr("游戏版本支持文件有冲突，暂时不能自动记录"), "bad"]
            return [qsTr("还没找到适用的游戏版本支持文件，暂时不能自动记录"), "bad"]
        }
        if (chain.adapterStale)
            return [qsTr("之前选择的网卡收不到游戏数据 · 请查看下方提示"), "bad"]
        if (chain.errorCode.length > 0)
            return [qsTr("暂时无法读取游戏数据 · %1").arg(Runtime.fmt.captureErrorLabel(chain.errorCode).replace(/。$/, "")), "bad"]
        if (chain.adapterId.length === 0 || !Runtime.app.capturing)
            return [qsTr("正在启动监听…"), "wait"]
        if (chain.silent)
            return [qsTr("已连接游戏，但还读不到游戏数据 · 请查看上方说明"), "bad"]
        // The hero card above already carries the controller's sentence.
        if (Runtime.app.recording.blocked)
            return [qsTr("暂时不会自动记录 · 见上方说明"), "bad"]
        return [qsTr("游戏、驱动、网卡和版本支持均已就绪"), "ok"]
    }
    readonly property var summaryLine: chain.summary()

    // -- 四个节点 ------------------------------------------------------------
    function npcapSub() {
        if (!Runtime.app.npcapInstalled)
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

    function adapterTone() {
        if (chain.adapterStale)
            return "warn"
        return chain.adapterId.length > 0 && Runtime.app.capturing ? "ok" : "off"
    }

    function profileValue() {
        if (chain.choosingGame)
            return qsTr("等待选择游戏")
        if (chain.maintainer)
            return chain.profile.profile_id || chain.capture.profile_id || qsTr("无")
        // Plain words a player can read, never an id.
        if (!chain.gameRunning && chain.buildUnknown)
            return qsTr("待游戏启动")
        if (chain.profileReady)
            return chain.profileOrigin === "LOCAL_CALIBRATION" ? qsTr("本机校准")
                 : chain.profileOrigin === "SHARED_CALIBRATION" ? qsTr("共享校准")
                 : qsTr("版本已支持")
        if (chain.calibrating)
            return chain.calibrationState === "READY" ? qsTr("待核对")
                 : chain.calibrationState === "BLOCKED" ? qsTr("需要处理")
                 // Nothing is being observed until the game runs.
                 : chain.gameRunning ? qsTr("校准中") : qsTr("待校准")
        if (chain.profileStatus === "")
            return qsTr("检查中")
        if (chain.buildUnknown)
            return chain.regionUnknown && chain.capture.game_build
                ? qsTr("未识别区服") : qsTr("未识别版本")
        if (chain.profileStatus === "UNSUPPORTED_BUILD")
            return qsTr("版本不支持")
        if (chain.profileStatus === "AMBIGUOUS")
            return qsTr("支持文件有冲突")
        return qsTr("未匹配")
    }

    function profileSub() {
        if (chain.choosingGame)
            return qsTr("选定游戏后检查对应版本")
        if (chain.maintainer)
            return qsTr("%1 · build %2").arg(Runtime.app.protocolProfileStatus)
                   .arg(chain.profile.game_build || chain.capture.game_build || Runtime.fmt.dash())
        if (!chain.gameRunning && chain.buildUnknown)
            return qsTr("启动游戏后自动检查")
        if (chain.profileReady)
            return chain.gameRunning ? qsTr("与游戏版本匹配") : qsTr("与已安装的游戏版本匹配")
        if (chain.calibrating)
            return chain.gameRunning ? qsTr("进度见下方本机校准") : qsTr("启动游戏后重新校准")
        if (chain.profileStatus === "")
            return qsTr("正在核对")
        if (chain.buildUnknown)
            return chain.regionUnknown && chain.capture.game_build
                ? qsTr("尚未识别游戏区服") : qsTr("尚未识别游戏版本或区服")
        if (chain.profileStatus === "AMBIGUOUS")
            return qsTr("游戏版本支持文件有冲突")
        return chain.gameRunning ? qsTr("当前版本暂时不会自动记录")
                                 : qsTr("已安装版本暂时不会自动记录")
    }

    function profileTone() {
        if (chain.choosingGame)
            return "off"
        if (chain.profileStatus === "" || (!chain.gameRunning && chain.buildUnknown))
            return "off"
        if (chain.profileReady)
            return "ok"
        if (chain.calibrating)
            return chain.calibrationState === "BLOCKED" ? "bad" : "warn"
        return "bad"
    }

    readonly property var nodes: [
        {
            key: "game", label: qsTr("FF14"),
            value: chain.choosingGame && chain.gameCount > 0 ? qsTr("待选择")
                   : chain.gameRunning ? "ffxiv_dx11.exe" : qsTr("未运行"),
            // With the game closed the installed version is what the profile is
            // matched against, and it is the one fact worth the line.
            sub: chain.choosingGame && chain.gameCount > 0 ? qsTr("检测到 %1 个客户端").arg(chain.gameCount)
                 : chain.gameRunning ? qsTr("PID %1").arg(chain.capture.ffxiv_process_id || Runtime.fmt.dash())
                 : chain.buildUnknown ? qsTr("启动游戏后自动检测")
                                      : qsTr("已安装版本 %1").arg(chain.installedVersion),
            tone: chain.gameRunning ? "ok" : "off"
        },
        {
            key: "npcap", label: qsTr("Npcap"),
            value: !chain.statusKnown ? Runtime.fmt.dash()
                   : Runtime.app.npcapInstalled
                   ? ((chain.npcap.version || chain.capture.npcap_version)
                      ? Runtime.fmt.npcapVersionLabel(chain.npcap.version || chain.capture.npcap_version)
                      : qsTr("已安装"))
                   : qsTr("未安装"),
            sub: chain.statusKnown ? chain.npcapSub() : qsTr("尚未读取"),
            tone: !chain.statusKnown ? "off" : chain.npcapReady ? "ok" : "bad"
        },
        {
            key: "adapter", label: qsTr("网卡"),
            value: chain.adapterValue(),
            sub: chain.adapterStale ? qsTr("之前选择的网卡收不到游戏数据")
                 : Runtime.app.captureSettings.adapter_id ? qsTr("手动指定") : qsTr("自动选择（有 FF14 连接）"),
            tone: chain.adapterTone()
        },
        {
            key: "profile", label: qsTr("版本支持"),
            value: chain.profileValue(),
            sub: chain.profileSub(),
            tone: chain.profileTone()
        }
    ]
}
