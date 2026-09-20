import QtQuick
import QtQuick.Layouts
import MentorRecorder

// The notices above 链路. Each one appears only while it has something to say;
// with none of them visible the block takes no room and leaves no page gap.
//   calibrationCard ............ 本机 / 共享校准 while calibration is not IDLE
//   protocolProfileCard ........ 协议档案: calibrated profile, sharing, mismatch
//   midstream / silent banner .. capture running, nothing decoded
//   adapterPreferenceStaleNotice  the remembered card lost the game's traffic
//   captureReadinessNotice ..... maintainer only: 自动跟随 is off
ColumnLayout {
    id: notices

    signal openCalibrationRequested()

    readonly property var capture: App.captureStatus
    readonly property var counters: App.captureCounters
    readonly property var profile: App.protocolProfile
    readonly property string profileStatus: String(notices.capture.profile_status || "")
    readonly property string profileOrigin: String(notices.capture.profile_origin || "")

    // 本机校准的卡片只在校准还在进行时出现（契约状态不是 IDLE）。
    readonly property var sharedCalibration: App.calibration ? App.calibration.shared : null
    readonly property bool calibrationCardVisible: !!App.calibration && App.calibration.state !== "IDLE"
    // 校准卡片可见时由其提供「分享给其他玩家」，协议档案卡片让位，避免同屏出现两个相同按钮；
    // 校准卡片消失后，分享入口由协议档案卡片承担。
    readonly property bool offersShare: !!notices.sharedCalibration && notices.sharedCalibration.canShare
                                        && !notices.calibrationCardVisible
    readonly property bool calibratedProfile: notices.profileStatus === "VERIFIED"
        && (notices.profileOrigin === "LOCAL_CALIBRATION" || notices.profileOrigin === "SHARED_CALIBRATION")
    // The Collector knows the installed version with the game closed, so the
    // build is no longer unknown then; the version is still only 已安装 - the
    // launcher may patch over it before the next login.
    readonly property bool buildUnknown: !notices.capture.game_build || !notices.capture.region
                                         || notices.capture.region === "UNKNOWN"
    readonly property string buildPhrase: App.ffxivRunning ? qsTr("当前游戏版本")
                                                           : qsTr("已安装的游戏版本")
    // A known, non-verified profile while the game runs is the reason nothing is
    // recorded. While calibration runs its own card explains that instead, and
    // with the game closed 链路 already says it once - a second card there would
    // only nag about something the player cannot act on yet.
    readonly property bool profileProblem: App.ffxivRunning && notices.profileStatus.length > 0
                                           && notices.profileStatus !== "VERIFIED"
                                           && !notices.calibrationCardVisible
    readonly property bool profileCardVisible: notices.offersShare || notices.calibratedProfile
                                               || notices.profileProblem
    readonly property bool silentVisible: App.captureMidstreamSuspected || App.captureSilent
                                          || App.recording.silent

    /// The remembered adapter the Collector now reports as carrying no game
    /// traffic, and the one it recommends instead. The notice appears only when
    /// both are known.
    readonly property var staleAdapter: notices.findAdapter("preference_stale")
    readonly property var recommendedAdapter: notices.findAdapter("recommended")
    readonly property bool staleVisible: !!notices.staleAdapter && !!notices.recommendedAdapter

    readonly property bool followOn: !App.captureSettingsLoaded || !!App.captureSettings.follow_game
    readonly property bool readinessVisible: App.maintainerToolsVisible && !App.capturing

    visible: notices.calibrationCardVisible || notices.profileCardVisible || notices.silentVisible
             || notices.staleVisible || notices.readinessVisible
    spacing: 12

    function findAdapter(flag) {
        for (let index = 0; index < App.captureAdapters.length; ++index) {
            if (App.captureAdapters[index][flag])
                return App.captureAdapters[index]
        }
        return null
    }

    /// A name a player can recognise. The opaque adapter_id is the last resort.
    function adapterName(row) {
        if (!row)
            return Fmt.dash()
        return row.friendly_name || row.description || row.adapter_id || Fmt.dash()
    }

    function counter(key) {
        const value = notices.counters[key]
        return (value === undefined || value === null) ? Fmt.dash() : Fmt.count(value)
    }

    // Players never read a profile id or a status token here
    // (SharedCalibrationCardTests.verifyPlayerCopy).
    function profileHeadline() {
        const status = notices.profileStatus
        if (App.maintainerToolsVisible && status === "VERIFIED")
            return qsTr("已就绪：%1").arg(notices.profile.profile_id || notices.capture.profile_id
                                          || qsTr("已验证档案"))
        if (notices.profileOrigin === "LOCAL_CALIBRATION" && status === "VERIFIED")
            return qsTr("正在使用本机校准出来的档案")
        if (notices.profileOrigin === "SHARED_CALIBRATION" && status === "VERIFIED")
            return qsTr("正在使用其他玩家分享的校准")
        if (status === "VERIFIED")
            return App.ffxivRunning ? qsTr("档案与游戏版本匹配")
                                    : qsTr("档案与已安装的游戏版本匹配")
        if (notices.buildUnknown)
            return qsTr("尚未识别游戏版本或区服")
        if (status === "UNSUPPORTED_BUILD")
            return qsTr("%1没有可用档案").arg(notices.buildPhrase)
        if (status === "AMBIGUOUS")
            return qsTr("同一版本有两份档案，已全部拒绝")
        return qsTr("尚未匹配到档案")
    }

    function profileExplanation() {
        // 本机校准生成的档案与随包档案同等对待，但须向用户说明其来源。
        if (notices.profileOrigin === "LOCAL_CALIBRATION" && notices.profileStatus === "VERIFIED")
            return qsTr("游戏更新后，本软件在这台电脑上重新认出了记录所需的信息，记录照常生成。"
                        + "是否通关仍然不能自动判定，离开副本后在记录里补一下结果即可。")
        if (notices.profileOrigin === "SHARED_CALIBRATION" && notices.profileStatus === "VERIFIED")
            return qsTr("它先在这台电脑的流量里核实过，才用来记录，记录照常生成。"
                        + "是否通关仍然不能自动判定，离开副本后在记录里补一下结果即可。")
        if (notices.profileStatus === "VERIFIED")
            return qsTr("能自动记录指导者任务的匹配、进入和离开。是否通关目前不能自动判定：离开副本后记录会标为“待复核”，"
                        + "在记录里用“修正”填上结果。")
        const detail = notices.profile.message ? " " + notices.profile.message : ""
        const stale = App.protocolProfileStale ? qsTr("（以下档案详情为上次已知信息）") : ""
        if (notices.buildUnknown)
            return qsTr("无法确认适用档案，当前不会生成自动记录，可以先手动记录。") + stale + detail
        return qsTr("当前不会生成自动记录，可以先手动记录。") + stale + detail
    }

    // 对玩家可见，不置于维护者工具之后：游戏更新后此卡片即为全部说明。
    CalibrationCard {
        Layout.fillWidth: true
        // Same inset as the panels below, so every kicker starts on one line.
        horizontalPadding: 24
        verticalPadding: 18
        visible: notices.calibrationCardVisible
        onConfirmRequested: notices.openCalibrationRequested()
    }

    Card {
        objectName: "protocolProfileCard"
        Layout.fillWidth: true
        visible: notices.profileCardVisible
        horizontalPadding: 24
        verticalPadding: 14
        spacing: 6

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            CardKicker {
                Layout.alignment: Qt.AlignVCenter
                Layout.fillWidth: false
                text: qsTr("协议档案")
            }

            Text {
                objectName: "protocolProfileHeadline"
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignVCenter
                text: notices.profileHeadline()
                textFormat: Text.PlainText
                color: notices.profileStatus === "VERIFIED" ? Theme.green : Theme.orangeText
                font.pixelSize: Theme.fs(13)
                font.bold: true
                wrapMode: Text.WordWrap
            }
        }

        Text {
            objectName: "protocolProfileExplanation"
            Layout.fillWidth: true
            text: notices.profileExplanation()
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        // 共享校准：文案与动作均来自 App.calibration.shared，与校准卡片上的按钮
        // 同一路径；本进程不联网。
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 2
            visible: notices.offersShare
            spacing: 16

            Text {
                objectName: "protocolShareHint"
                Layout.fillWidth: true
                visible: notices.offersShare
                text: notices.sharedCalibration ? notices.sharedCalibration.shareHint : ""
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            AppButton {
                objectName: "protocolShareButton"
                visible: notices.offersShare
                variant: "primary"
                text: qsTr("分享给其他玩家")
                enabled: !!notices.sharedCalibration && !notices.sharedCalibration.busy
                onClicked: notices.sharedCalibration.share()
            }
        }
    }

    // docs/live-validation-guide.md section 6. Also for the Collector's own
    // silent_reason: a wrong adapter produces no packets and so no errors.
    Card {
        objectName: "captureSilentNotice"
        Layout.fillWidth: true
        visible: notices.silentVisible
        horizontalPadding: 24
        verticalPadding: 14
        spacing: 4
        borderColor: Theme.orange

        Text {
            objectName: "midstreamBannerTitle"
            Layout.fillWidth: true
            text: App.captureMidstreamSuspected
                  ? qsTr("抓包开始得太晚：这条连接不会解出任何报文")
                  : App.captureSilent
                  ? qsTr("捕获在运行，但至今没有解码出任何报文")
                  : qsTr("正在监听，但还没有收到游戏数据")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(15)
            font.bold: true
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            // The Collector's hint when it sent one, otherwise the sentence the
            // recording controller chose for this silent_reason.
            text: App.captureHealthText || App.recording.message
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            text: qsTr("已解码 %1 · 解码失败 %2")
                  .arg(notices.counter("messages_decoded"))
                  .arg(notices.counter("decode_errors"))
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            font.family: Theme.figureFamily
            font.weight: Theme.figureWeight(false)
            font.features: ({ "tnum": 1 })
        }
    }

    // $defs/CaptureAdapter.preference_stale - the 加速器 / VPN case. The button
    // writes the recommended card into CaptureSettings.adapter_id, which is what
    // the automatic capture reads.
    Card {
        objectName: "adapterPreferenceStaleNotice"
        Layout.fillWidth: true
        visible: notices.staleVisible
        horizontalPadding: 24
        verticalPadding: 14
        borderColor: Theme.orange

        RowLayout {
            Layout.fillWidth: true
            spacing: 16

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 4

                Text {
                    objectName: "adapterPreferenceStaleTitle"
                    Layout.fillWidth: true
                    text: qsTr("记住的网卡上已经没有游戏流量了")
                    color: Theme.orangeText
                    font.pixelSize: Theme.fs(15)
                    font.bold: true
                    wrapMode: Text.WordWrap
                }

                Text {
                    Layout.fillWidth: true
                    text: qsTr("「%1」上收不到游戏数据（常见于开着加速器或 VPN），现在承载游戏流量的是「%2」。")
                          .arg(notices.adapterName(notices.staleAdapter))
                          .arg(notices.adapterName(notices.recommendedAdapter))
                    textFormat: Text.PlainText
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12)
                    wrapMode: Text.WordWrap
                }

                Text {
                    Layout.fillWidth: true
                    visible: !App.captureSettingsSupported
                    text: qsTr("当前的采集服务版本还不能保存这个选择，请更新后再试。")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    wrapMode: Text.WordWrap
                }
            }

            AppButton {
                objectName: "useRecommendedAdapterButton"
                text: qsTr("使用推荐适配器")
                variant: "primary"
                enabled: App.captureSettingsSupported && App.captureSettingsLoaded
                onClicked: {
                    const recommended = notices.recommendedAdapter
                    if (!recommended)
                        return
                    App.updateCaptureSetting("adapter_id", recommended.adapter_id)
                    App.captureAdapterId = recommended.adapter_id
                }
            }
        }
    }

    // Nothing has to be done before login while follow-the-game is on. The Oodle
    // stream can only be decoded from the start of a connection, so only the
    // "off" case is a warning.
    Card {
        objectName: "captureReadinessNotice"
        Layout.fillWidth: true
        visible: notices.readinessVisible
        horizontalPadding: 24
        verticalPadding: 10
        borderColor: notices.followOn ? Theme.border : Theme.orange

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            Text {
                objectName: "captureReadinessText"
                Layout.fillWidth: true
                text: notices.followOn
                      ? qsTr("游戏启动后会自动开始监听，登录前不需要任何操作；只要本软件先于游戏运行即可。")
                      : qsTr("自动跟随已关闭：必须在登录游戏之前手动开始捕获，否则本次登录不会有任何记录。")
                textFormat: Text.PlainText
                color: notices.followOn ? Theme.textSecondary : Theme.orangeText
                font.pixelSize: Theme.fs(12)
                font.bold: !notices.followOn
                wrapMode: Text.WordWrap
            }

            AppButton {
                objectName: "enableFollowGameButton"
                visible: !notices.followOn
                compact: true
                variant: "primary"
                text: qsTr("开启自动跟随")
                enabled: App.captureSettingsSupported && App.captureSettingsLoaded
                onClicked: App.updateCaptureSetting("follow_game", true)
            }
        }
    }
}
