import QtQuick
import MentorRecorder

// What the hero card says: one headline, one line under it, at most one detail
// line, a tone and the page badge, for the state the page is in. The order of
// precedence follows AutomaticRecordingController::project(): a missing Npcap,
// a game to choose, a capture that decodes nothing, 本机校准, the closed game,
// then the ordinary blocked / checking / listening states.
// Every sentence is written for the player: no opcode, no token, no id.
QtObject {
    id: copy

    // The CaptureChainModel the hero card reads; the waiting texts use its verdicts.
    property var chain: null
    readonly property CalibrationCopy calibration: CalibrationCopy {}

    readonly property var npcap: App.npcapInfo
    readonly property string npcapStatus: String(copy.npcap.status || "")
    readonly property bool npcapNotInstalled: !App.npcapInstalled || copy.npcapStatus === ""
                                              || copy.npcapStatus === "NOT_INSTALLED"
    // Installed is not enough: NOT_WINPCAP_COMPATIBLE, NPCAP_ADMIN_ONLY and
    // LOAD_FAILED are installed drivers this Collector still cannot open.
    readonly property bool npcapProblem: !App.npcapInstalled
                                         || (copy.npcapStatus !== "" && copy.npcapStatus !== "READY")
    readonly property bool choosingGame: App.gameSelection.selectionRequired
    readonly property bool gameRunning: App.ffxivRunning
    readonly property bool silent: App.captureMidstreamSuspected || App.captureSilent
                                   || App.recording.silent
    readonly property string recordingState: App.recording.state

    // 本机校准 speaks only while the recording controller agrees: the game is
    // closed (nothing else can be said), the controller projects a calibration
    // state, or calibration has produced a recording profile and the capture is
    // listening. A capture error or an unconfirmed setting outranks a finished
    // calibration, exactly as in project().
    readonly property bool calibrationSpeaks: copy.calibration.active
        && (!copy.gameRunning || App.recording.calibrating
            || (copy.calibration.recording && copy.recordingState === "listening"))

    // broken | choosing | silent | calibration | waiting | blocked | normal | checking
    readonly property string kind: copy.npcapProblem ? "broken"
        : copy.choosingGame ? "choosing"
        : (copy.silent && copy.gameRunning) ? "silent"
        : copy.calibrationSpeaks ? "calibration"
        : !copy.gameRunning ? "waiting"
        : (App.recording.blocked || copy.recordingState === "blocked") ? "blocked"
        : copy.recordingState === "listening" ? "normal"
        : "checking"

    readonly property string tone: {
        switch (copy.kind) {
        case "broken":
        case "choosing":
        case "silent":
            return "warn"
        case "calibration":
            return copy.calibration.tone
        case "blocked":
            return "bad"
        case "normal":
            return "ok"
        default:
            return "off"
        }
    }

    // The page badge, in the words the sidebar uses for the same state.
    readonly property string badge: {
        switch (copy.kind) {
        case "broken":
            return qsTr("需手动记录")
        case "choosing":
            return qsTr("等待选择游戏")
        case "silent":
            return qsTr("暂时读不到游戏数据")
        case "calibration":
            return copy.calibration.badge
        case "waiting":
            return qsTr("等待游戏启动")
        case "blocked":
            return qsTr("无法自动记录")
        case "normal":
            return qsTr("自动监听中")
        default:
            return qsTr("检查中")
        }
    }

    function headline() {
        switch (copy.kind) {
        case "broken":
            return copy.npcapNotInstalled ? qsTr("未安装 Npcap，自动记录已停用")
                                          : qsTr("Npcap 暂时用不了，自动记录已停用")
        case "choosing":
            return qsTr("等待选择游戏")
        case "silent":
            return App.captureMidstreamSuspected ? qsTr("已连接游戏，但还读不到游戏数据")
                 : App.captureSilent ? qsTr("正在读取游戏数据，但还没有识别成功")
                 : qsTr("正在监听，但还没有收到游戏数据")
        case "calibration":
            return copy.calibration.headline()
        case "waiting":
            return qsTr("等待游戏启动")
        case "blocked":
            return qsTr("暂时不会自动记录")
        case "normal":
            return qsTr("自动记录已开启，正在读取游戏数据")
        default:
            return qsTr("正在检查能否自动记录")
        }
    }

    function text() {
        switch (copy.kind) {
        case "broken":
            return copy.npcapExplanation()
        case "choosing":
            // The reason is on the 记录对象 card and in the chain summary.
            return qsTr("在下方「记录对象」里点选要记录的游戏窗口。")
        case "silent":
            // The Collector's hint when it sent one, otherwise the sentence the
            // recording controller chose for this silent_reason.
            return App.captureHealthText || App.recording.message
        case "calibration":
            return copy.calibration.explanation()
        case "waiting":
            return copy.waitingText()
        case "blocked":
            return copy.blockedText()
        case "normal":
            return qsTr("进本和出本会自动记录。识别到通关后会自动填写结果；如果没有识别到，请在离开副本后打开记录补上结果。")
        default:
            return App.recording.message
        }
    }

    // A third, figure-bearing line: the decode counters while nothing decodes.
    function detail() {
        if (copy.kind !== "silent")
            return ""
        return qsTr("成功读取 %1 条 · 读取失败 %2 次")
               .arg(copy.counter("messages_decoded")).arg(copy.counter("decode_errors"))
    }

    function counter(key) {
        const value = App.captureCounters[key]
        return (value === undefined || value === null) ? Fmt.dash() : Fmt.count(value)
    }

    // GetStatus.npcap.status (CaptureWire.StatusExtras), in words.
    function npcapReason() {
        switch (copy.npcapStatus) {
        case "NOT_WINPCAP_COMPATIBLE": return qsTr("Npcap 没有以 WinPcap 兼容模式安装。")
        case "NPCAP_ADMIN_ONLY": return qsTr("Npcap 被设为仅限管理员使用，而本软件没有以管理员身份运行。")
        case "LOAD_FAILED": return qsTr("Npcap 已登记，但驱动文件无法加载。")
        default: return qsTr("Npcap 暂时用不了。")
        }
    }

    // The fixed sentence covers a missing driver. For a driver that is present
    // but unusable, the Collector's own install_hint names the exact fix, so the
    // UI and --capture-doctor never disagree.
    function npcapExplanation() {
        const base = qsTr("自动记录需要 Npcap 驱动，请自行下载安装。安装前仍可手动记录、查看统计、导出和备份。")
        if (copy.npcapNotInstalled)
            return base
        // install_hint_ref renders the hint in the interface language
        // (contracts/collector-texts.md); its Chinese otherwise.
        const hint = copy.npcap.install_hint
            ? Fmt.collectorText(copy.npcap.install_hint_ref, copy.npcap.install_hint)
            : App.adapterInfo.install_hint
            ? Fmt.collectorText(App.adapterInfo.install_hint_ref, App.adapterInfo.install_hint)
            : ""
        return copy.npcapReason() + (hint.length > 0 ? hint : base)
    }

    // 游戏未运行: with the installed version known the profile question already
    // has an answer; the version itself is never named here.
    function waitingText() {
        const chain = copy.chain
        if (!chain || chain.buildUnknown)
            return qsTr("启动游戏后会自动检查游戏版本，确认能否自动记录。无需手动操作。")
        if (chain.profileStatus === "")
            return qsTr("正在检查已安装的游戏版本。启动游戏后会自动继续。")
        if (chain.profileReady)
            return qsTr("已支持安装在这台电脑上的游戏版本。启动游戏后会自动记录。")
        if (chain.profileStatus === "AMBIGUOUS")
            return qsTr("当前游戏版本的支持文件有冲突，暂时不能自动记录。请更新软件或导出诊断报告。")
        return qsTr("当前安装的游戏版本暂不支持自动记录。启动游戏后会再次检查。")
    }

    // The recording controller's sentence is already written for the player.
    // Its closing "请查看诊断" is pointless on the diagnostics page itself, and
    // the raw code it appends for an error this build has no wording for
    // (「（代码 ERR_…）」) never reaches a player's page.
    function blockedText() {
        let text = String(App.recording.message || "")
        text = text.replace(/（代码 [^）]*）/g, "")
        // The same bracket in another interface language, e.g. "(code ERR_…)".
        text = text.replace(/\s*[（(][^（）()]*\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+[）)]/g, "")
        text = text.replace(/[，,]?\s*(?:请|并)?查看诊断。?\s*$/, "").trim()
        if (text.length > 0 && !/[。！？.!?]$/.test(text))
            //: The full stop that closes a sentence.
            text += qsTr("。")
        return text
    }
}
