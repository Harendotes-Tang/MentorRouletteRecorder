import QtQuick
import MentorRecorder

// The sentence that says what 本机校准 means for the player right now, and the
// line under it. The capture page's hero card speaks it (CaptureHeroCopy); the
// calibration card speaks it itself only when it stands alone. Both instantiate
// this object, so the two never disagree. Reads App only.
// 这里不出现任何编号、偏移或英文状态词。
QtObject {
    id: copy

    readonly property var controller: App.calibration
    // Item.state is a QML built-in, so the contract state travels under this name.
    readonly property string calibrationState: copy.controller ? copy.controller.state : "IDLE"
    readonly property bool active: copy.calibrationState !== "IDLE"
    readonly property bool ready: copy.calibrationState === "READY"
    readonly property bool blocked: copy.calibrationState === "BLOCKED"
    readonly property bool done: copy.calibrationState === "DONE"
    // 已按本机档案记录，但依据是"申请了哪个随机任务"而非服务器匹配报文，后台仍在
    // 查找该报文；此时须明确说明当前已可正常记录。
    readonly property bool provisional: copy.controller ? copy.controller.provisional : false
    readonly property var shared: copy.controller ? copy.controller.shared : null
    readonly property bool sharedInUse: !!copy.shared && copy.shared.inUse
    // 共享档案已在记录时，本机校准仍处于观察状态，此时不得提示"正在重新校准、
    // 期间不会生成记录"。
    readonly property bool sharedRecording: copy.sharedInUse && !copy.provisional
    // 已按登录时的核实开始记录，排本与进本仍在核对（plan §18.4）：不得说成"本机已核实"。
    readonly property bool sharedAuditPending: !!copy.shared && copy.shared.auditPending
    // 按排本推断的共享校准先征求一次同意，同意之前不记录。
    readonly property bool sharedConsent: !!copy.shared && copy.shared.view === "consent"
    readonly property var captureStatus: App.captureStatus || ({})
    // 核对请求旁边已有档案在记录（按排本推断的或已完成但缺职业的本机档案、共享档案）：
    // 这次核对只是让之后的记录更准、更全，记录本身并不等它（审查 CS3b-X1）。
    readonly property bool recordingBesideOffer: copy.ready
        && (copy.sharedInUse || copy.captureStatus.profile_status === "VERIFIED"
            || !!(copy.captureStatus.calibration && copy.captureStatus.calibration.local_profile_id))
    readonly property int confirmCount: copy.controller ? copy.controller.confirmCount : 0
    readonly property bool gameRunning: App.ffxivRunning
    // Something is being recorded while calibration goes on.
    readonly property bool recording: copy.sharedRecording || copy.done || copy.provisional
                                      || copy.recordingBesideOffer

    // The LED and badge tone of this state. With the game closed nothing happens
    // either way, which is the grey "off".
    readonly property string tone: !copy.gameRunning ? "off"
                                 : copy.recording ? "ok"
                                 : copy.blocked ? "bad"
                                 : "warn"

    // The page badge, in the words the sidebar uses for the same state.
    readonly property string badge: !copy.gameRunning ? qsTr("等待游戏启动")
                                  : copy.recording ? qsTr("自动监听中")
                                  : copy.ready ? qsTr("校准待核对")
                                  : copy.blocked ? qsTr("无法自动记录")
                                  : qsTr("校准中")

    // A profile that records - shared, provisional or just calibrated - while
    // the game is closed: nothing records now, so the sentence says what will.
    readonly property bool recordingLater: !copy.gameRunning
        && (copy.sharedRecording || copy.done || copy.provisional)

    function headline() {
        if (copy.recordingLater)
            return qsTr("启动游戏后会自动记录")
        if (copy.sharedRecording)
            return copy.sharedAuditPending
                ? qsTr("正在用其他玩家分享的校准记录，排本和进本还在核对。")
                : qsTr("正在用其他玩家分享的校准自动记录。")
        if (copy.ready)
            return copy.recordingBesideOffer
                ? qsTr("记录照常进行。再核对 %1 件事，之后的记录会更准确。").arg(copy.confirmCount)
                : qsTr("还差最后一步：核对 %1 件事，就能开始自动记录。").arg(copy.confirmCount)
        if (copy.blocked)
            return qsTr("校准遇到了问题，暂时不能自动记录。")
        if (copy.done)
            return qsTr("校准完成，已经可以正常记录")
        if (copy.provisional)
            return qsTr("已经可以自动记录，后台仍在完善校准。")
        if (copy.sharedConsent)
            return qsTr("共享校准已通过检查，同意使用后就能开始记录。")
        // 游戏没在运行时什么也观察不到，此时不应催促玩家「去打一把」。
        if (!copy.gameRunning)
            return qsTr("启动游戏后会重新校准")
        return qsTr("游戏更新了，正在重新校准")
    }

    function explanation() {
        if (copy.recordingLater) {
            const source = copy.sharedRecording
                ? (copy.sharedAuditPending ? qsTr("共享校准已通过登录检查，之后还会继续核对排本和进本。启动游戏后会自动继续，无需操作。")
                                           : qsTr("其他玩家分享的校准已在你的游戏中通过检查。启动游戏后会自动继续，无需操作。"))
                : copy.provisional ? qsTr("基础校准已完成，之后还会在后台继续完善。启动游戏后会自动继续，无需操作。")
                : qsTr("这台电脑已经完成校准。启动游戏后会自动继续，无需操作。")
            return source
        }
        if (copy.sharedRecording || copy.done)
            return qsTr("识别到通关后会自动填写结果。如果没有识别到，请在离开副本后打开记录补上结果。")
        // 按排本推断的说法与共享校准的同意提示用同一个短语（CalibrationController.queueInferenceText），
        // 写在整句里，方便翻译。
        if (copy.provisional)
            return qsTr("现在根据你申请的随机任务和实际进入的副本来记录。想帮助完善校准，可以自己申请并完成两种不同的随机任务。有新的内容需要核对时，会再提醒你。")
        if (copy.ready)
            return copy.recordingBesideOffer
                ? qsTr("在「本机校准」中点「核对并启用」，检查软件识别的排本和进出副本时间。核对前仍会照常记录。")
                : qsTr("在「本机校准」中点「核对并启用」，检查软件识别的排本和进出副本时间。全部核对后才能开始自动记录。")
        // The Collector's blockers are listed, once, under 下一步 in the calibration row.
        if (copy.blocked)
            return qsTr("请按「本机校准」中的「下一步」提示处理。")
        if (copy.sharedConsent)
            return qsTr("请先阅读「共享校准」中的说明，再点「同意，开始记录」。也可以在「其他方式」中选择「改为本机校准」，照常游戏来完成校准。")
        if (!copy.gameRunning)
            return qsTr("当前游戏版本还需要校准。启动游戏后，照常完成随机任务，再按提示核对。校准完成前暂时不能自动记录。")
        return qsTr("软件需要重新识别排本、匹配和进出副本。先照常完成一次随机任务，再按提示核对。校准完成前暂时不能自动记录。")
    }
}
