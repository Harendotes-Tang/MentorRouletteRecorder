import QtQuick
import QtQuick.Controls
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
    // 本机校准出来的档案生效后，校准卡片连同「清空进度并重新观察」「导入校准码」一起消失，
    // 玩家再无入口可用。「重新校准」只在这种情况下出现：别人分享的校准由「不用共享的，
    // 我自己校准」停用，随包档案不是本机的猜测，没有档案时也无从停用。
    readonly property bool offersRecalibrate: notices.profileStatus === "VERIFIED"
                                              && notices.profileOrigin === "LOCAL_CALIBRATION"
    // 「重新校准」的退路：上次停用的那份本机校准还在磁盘上。采集服务只在没有本机档案
    // 生效时报 true，因此这两个按钮天然互斥，这里再显式排除一次。
    readonly property bool offersRestore: !!App.calibration && App.calibration.retiredLocalProfileAvailable
                                          && !notices.offersRecalibrate
    // 停用档案会把正在进行的记录按停止捕获收尾，等于让玩家白打这一把。
    readonly property bool runInFlight: App.currentRunState === "MENTOR_MATCHED"
                                        || App.currentRunState === "ENTERED_DUTY"
    // A known, non-verified profile while the game runs is the reason nothing is
    // recorded. While calibration runs its own card explains that instead.
    readonly property bool profileProblem: App.ffxivRunning && notices.profileStatus.length > 0
                                           && notices.profileStatus !== "VERIFIED"
                                           && !notices.calibrationCardVisible
    // 停用本机校准之后校准卡片会重新出现，于是 profileProblem 为假；若不把退路算进来，
    // 协议档案卡就会整张隐藏，「恢复上一份本机校准」也随之不可达——正是本功能要堵的洞。
    readonly property bool profileCardVisible: notices.offersShare || notices.calibratedProfile
                                               || notices.profileProblem || notices.offersRestore
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
            return qsTr("档案与游戏版本匹配")
        if (!notices.capture.game_build || !notices.capture.region || notices.capture.region === "UNKNOWN")
            return qsTr("尚未识别游戏版本或区服")
        if (status === "UNSUPPORTED_BUILD")
            return qsTr("当前游戏版本没有可用档案")
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
        if (!notices.capture.game_build || !notices.capture.region || notices.capture.region === "UNKNOWN")
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

        // 本机校准的档案认错了报文时，这里是唯一的退路：停用它，软件回到观察状态，
        // 之后可以重新校准，也可以导入其他玩家的校准码。
        // 停用之后，同一行改为提供反向的退路：把上次停用的那一份换回来。
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 2
            visible: notices.offersRecalibrate || notices.offersRestore
            spacing: 16

            Text {
                objectName: "protocolRecalibrateHint"
                Layout.fillWidth: true
                visible: notices.runInFlight
                text: qsTr("副本进行中，结束后再试")
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            Item { Layout.fillWidth: true; visible: !notices.runInFlight }

            AppButton {
                objectName: "protocolRestoreButton"
                visible: notices.offersRestore
                text: qsTr("恢复上一份本机校准")
                enabled: !notices.runInFlight
                         && (!App.calibration || !App.calibration.busy)
                onClicked: restoreDialog.open()
            }

            AppButton {
                objectName: "protocolRecalibrateButton"
                visible: notices.offersRecalibrate
                text: qsTr("重新校准")
                enabled: !notices.runInFlight
                         && (!App.calibration || !App.calibration.busy)
                onClicked: recalibrateDialog.open()
            }
        }

        // 采集服务拒绝时，它给的句子本身就是写给玩家看的，原样显示。
        // 校准卡片此时可能并不在场，这一行是唯一能说明原因的地方。
        Text {
            objectName: "protocolCalibrationError"
            Layout.fillWidth: true
            visible: (notices.offersRecalibrate || notices.offersRestore)
                     && !!App.calibration && App.calibration.error.length > 0
            text: App.calibration ? App.calibration.error : ""
            textFormat: Text.PlainText
            color: Theme.orangeText
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
    }

    // 停用一份还在记录的档案，先把代价说清楚再问一次。
    Dialog {
        id: recalibrateDialog
        objectName: "protocolRecalibrateDialog"
        parent: Overlay.overlay
        anchors.centerIn: parent
        modal: true
        Overlay.modal: Rectangle { color: Theme.modalScrim(recalibrateDialog.palette.shadow) }
        width: 460
        padding: 20
        closePolicy: Popup.CloseOnEscape

        background: DialogFrame {}

        contentItem: ColumnLayout {
            spacing: 12

            HeadingLabel {
                Layout.fillWidth: true
                text: qsTr("重新校准这一版游戏？")
                font.pixelSize: Theme.dialogTitleSize(20)
            }

            Text {
                Layout.fillWidth: true
                text: qsTr("现在这份本机校准会停用（文件会保留，不会删除），软件回到观察状态："
                           + "期间不会生成记录，直到重新校准完成，或导入了其他玩家的校准码。"
                           + "之前的记录不受影响。")
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            RowLayout {
                Layout.fillWidth: true
                spacing: 8

                Item { Layout.fillWidth: true }

                AppButton {
                    text: qsTr("取消")
                    onClicked: recalibrateDialog.close()
                }

                AppButton {
                    objectName: "protocolRecalibrateConfirm"
                    variant: "primary"
                    text: qsTr("停用并重新校准")
                    enabled: !!App.calibration && !App.calibration.busy
                    onClicked: {
                        App.calibration.recalibrate()
                        recalibrateDialog.close()
                    }
                }
            }
        }
    }

    // 换回上次停用的那一份，同样先说清楚会发生什么。
    Dialog {
        id: restoreDialog
        objectName: "protocolRestoreDialog"
        parent: Overlay.overlay
        anchors.centerIn: parent
        modal: true
        Overlay.modal: Rectangle { color: Theme.modalScrim(restoreDialog.palette.shadow) }
        width: 460
        padding: 20
        closePolicy: Popup.CloseOnEscape

        background: DialogFrame {}

        contentItem: ColumnLayout {
            spacing: 12

            HeadingLabel {
                Layout.fillWidth: true
                text: qsTr("恢复上一份本机校准？")
                font.pixelSize: Theme.dialogTitleSize(20)
            }

            Text {
                Layout.fillWidth: true
                text: qsTr("软件会停用现在这份校准，换回你上次停用的那一份本机校准，并立刻用它记录。"
                           + "之前的记录不受影响。")
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                wrapMode: Text.WordWrap
            }

            RowLayout {
                Layout.fillWidth: true
                spacing: 8

                Item { Layout.fillWidth: true }

                AppButton {
                    text: qsTr("取消")
                    onClicked: restoreDialog.close()
                }

                AppButton {
                    objectName: "protocolRestoreConfirm"
                    variant: "primary"
                    text: qsTr("恢复")
                    enabled: !!App.calibration && !App.calibration.busy
                    onClicked: {
                        App.calibration.restoreLocalProfile()
                        restoreDialog.close()
                    }
                }
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
