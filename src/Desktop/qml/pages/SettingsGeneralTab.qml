import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 设置 · 通用: 语言 / 启动与托盘 / 记录 / 校准 / 更新 / 外观.
ColumnLayout {
    id: tab

    Layout.fillWidth: true
    spacing: 16

    // Guarded: the two settings arrive with the reflection feature.
    readonly property bool reflectPromptEnabled: (typeof Settings !== "undefined"
                                                  && Settings.reflectPrompt !== undefined)
                                                 ? Settings.reflectPrompt : true
    readonly property bool confirmPromptEnabled: (typeof Settings !== "undefined"
                                                  && Settings.confirmPrompt !== undefined)
                                                 ? Settings.confirmPrompt : true
    // The style on screen, not the persisted one: --mock-ui-style pins Theme
    // without touching desktop.ini, and the segment must agree with the skin.
    readonly property string uiStyleValue: Theme.uiStyle
    // The application's LanguageManager; a test scene without one shows no card.
    readonly property var lang: typeof Lang !== "undefined" ? Lang : null

    // One place decides how an unsupported Collector reads, so a switch that can
    // do nothing never looks like one that can.
    function captureSettingDescription(text) {
        if (!App.captureSettingsSupported)
            return qsTr("当前后台服务不支持这项设置，暂时无法修改。")
        return text
    }

    // ------------------------------------------------------------ 语言 --
    // The kicker keeps "Language" beside the translated word, so a player who
    // landed in a language they cannot read still finds the way back.
    SettingsPanel {
        objectName: "languageSettingsCard"
        visible: tab.lang !== null
        kicker: tab.lang && tab.lang.code === "en" ? "Language" : qsTr("语言") + " · Language"

        SettingsRow {
            showDivider: false
            label: qsTr("界面语言")
            description: qsTr("界面文字和播报语音一起切换")

            SegmentedControl {
                objectName: "uiLanguageSettingControl"
                Layout.preferredWidth: 340
                options: tab.lang ? tab.lang.languages.map(function(language) {
                    return {
                        value: language.code,
                        label: language.label,
                        family: tab.lang.displayFamily(language.code)
                    }
                }) : []
                currentValue: tab.lang ? tab.lang.code : ""
                onActivated: function(value) { tab.lang.setLanguage(value) }
            }
        }
    }

    // ------------------------------------------------------ 启动与托盘 --
    SettingsPanel {
        objectName: "generalSettingsCard"
        kicker: qsTr("启动与托盘")

        SettingToggleRow {
            label: qsTr("开机启动")
            description: Settings.autostartWritable
                         ? qsTr("登录 Windows 后自动启动，显示在系统托盘中")
                         : qsTr("当前系统不支持开机启动")
            toggleEnabled: Settings.autostartWritable
            checked: Settings.autostart
            onToggled: function(value) { Settings.autostart = value }
        }

        // Owned by the Collector, not by desktop.ini: the Collector watches for
        // the game and starts capturing. Maintainers only.
        SettingToggleRow {
            objectName: "followGameToggle"
            visible: App.maintainerToolsVisible
            label: qsTr("随 FF14 自动开始捕获")
            description: tab.captureSettingDescription(
                qsTr("默认开启。检测到游戏启动后开始监听，游戏退出后停止。"
                     + "请先打开本软件，再登录游戏，才能正常自动记录。"))
            toggleEnabled: App.captureSettingsSupported && App.captureSettingsLoaded
            checked: !!App.captureSettings.follow_game
            onToggled: function(value) {
                App.updateCaptureSetting("follow_game", value)
            }
        }

        SettingToggleRow {
            showDivider: false
            label: qsTr("关闭时最小化到托盘")
            description: qsTr("关闭窗口后仍在后台运行；可从托盘菜单选择「显示」或「退出」")
            checked: Settings.minimizeToTray
            onToggled: function(value) { Settings.minimizeToTray = value }
        }
    }

    // ------------------------------------------------------------ 记录 --
    SettingsPanel {
        kicker: qsTr("记录")

        SettingToggleRow {
            label: qsTr("结束后询问本次结果")
            description: qsTr("未收到通关结算就离开副本时，软件会询问本次结果。"
                              + "关闭后可在总览的「待复核」中确认。")
            checked: tab.confirmPromptEnabled
            onToggled: function(value) {
                if (typeof Settings !== "undefined" && Settings.confirmPrompt !== undefined)
                    Settings.confirmPrompt = value
            }
        }

        SettingToggleRow {
            showDivider: false
            label: qsTr("通关后弹出笔记窗口")
            description: qsTr("通关后弹出窗口记笔记；也可稍后到历史记录补写。")
            checked: tab.reflectPromptEnabled
            onToggled: function(value) {
                if (typeof Settings !== "undefined" && Settings.reflectPrompt !== undefined)
                    Settings.reflectPrompt = value
            }
        }
    }

    // ------------------------------------------------------------ 校准 --
    // Not in the prototype: both switches are player-facing and belong where a
    // player looks.
    SettingsPanel {
        kicker: qsTr("校准")

        // 玩家可见：补丁日关闭该开关即等同整晚静默，故不属于维护者开关。
        SettingToggleRow {
            objectName: "autoCalibrationToggle"
            label: qsTr("游戏更新后自动校准")
            description: tab.captureSettingDescription(
                qsTr("游戏更新后，软件可能需要重新识别排本和进出副本。开启后照常完成随机任务，按提示核对即可。"
                     + "校准完成前暂时不能自动记录。关闭后，游戏更新时可能需要等待软件更新才能恢复自动记录。"))
            toggleEnabled: App.captureSettingsSupported && App.captureSettingsLoaded
            checked: !App.captureSettingsLoaded
                     || App.captureSettings.auto_calibration_enabled !== false
            onToggled: function(value) {
                App.updateCaptureSetting("auto_calibration_enabled", value)
            }
        }

        // 玩家可见（docs/privacy-boundary.md §8.2）：默认开启的联网请求，开关须显见。
        // 另一类为在线语音（§8.3），默认关闭，仅在「播报」中选择在线语音后出现。
        SettingToggleRow {
            objectName: "sharedCalibrationToggle"
            showDivider: captureSettingsErrorText.visible
            label: qsTr("游戏更新后获取其他玩家的共享校准")
            description: tab.captureSettingDescription(
                qsTr("默认开启。自动校准开启时，软件会联网查找其他玩家分享的校准，检查可用后再使用。"
                     + "请求不含账号或设备身份信息，不会上传你的游戏数据。关闭后仍可手动导入校准码。"))
            toggleEnabled: App.captureSettingsSupported && App.captureSettingsLoaded
            checked: !App.captureSettingsLoaded
                     || App.captureSettings.shared_calibration_enabled !== false
            onToggled: function(value) {
                App.updateCaptureSetting("shared_calibration_enabled", value)
            }
        }

        Text {
            id: captureSettingsErrorText

            objectName: "captureSettingsErrorText"
            Layout.fillWidth: true
            Layout.topMargin: 10
            Layout.bottomMargin: 4
            visible: App.captureSettingsError.length > 0
            text: App.captureSettingsError
            color: Theme.orangeText
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }

    // ------------------------------------------------------------ 更新 --
    // 玩家可见（docs/privacy-boundary.md §8.4、§8.6）：默认开启的联网请求，开关须显见。
    // 检查只提示；下载只在用户点「下载并安装」后由后台进程进行，安装只在用户再点
    // 「立即安装」后开始。判断在后台进程，界面只显示结论。
    SettingsPanel {
        kicker: qsTr("更新")

        SettingToggleRow {
            objectName: "updateCheckToggle"
            // The row below it is always there now: the button lives in it.
            showDivider: true
            label: qsTr("检查新版本并提示")
            description: tab.captureSettingDescription(
                qsTr("默认开启，每天最多自动联网检查一次，有新版本时在总览提示；关闭后「检查更新」也不可用，并会取消下载、删除已准备好的安装包。"
                     + "请求不含账号"
                     + "或设备身份信息。"
                     + "点「下载并安装」只下载安装包并校验，准备好后还需点「立即安装」启动安装。"
                     + "软件不会自行下载或安装新版本。"))
            toggleEnabled: App.captureSettingsSupported && App.captureSettingsLoaded
            checked: !App.captureSettingsLoaded
                     || App.captureSettings.update_check_enabled !== false
            onToggled: function(value) {
                App.updateCaptureSetting("update_check_enabled", value)
            }
        }

        // 最近一次检查的小字，右侧是用户主动触发的那一次检查。
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 10
            Layout.bottomMargin: 4
            spacing: 12

            Text {
                id: updateCheckStatusText

                objectName: "updateCheckStatusText"
                Layout.fillWidth: true
                visible: App.update.available && App.update.lastCheckedAtUtc.length > 0
                // 版本号只有采集器读到时才写出来，不在这里编一个「未知」。
                text: App.update.latestVersion.length > 0
                      ? qsTr("最近检查：%1 · 最新版本 %2")
                        .arg(Fmt.localTime(App.update.lastCheckedAtUtc))
                        .arg(App.update.latestVersion)
                      : qsTr("最近检查：%1").arg(Fmt.localTime(App.update.lastCheckedAtUtc))
                color: Theme.textMuted
                font.pixelSize: Theme.fs(11)
                wrapMode: Text.WordWrap
            }

            // 有新版本时，旁边另有一个不那么显眼的「查看更新说明」，打开发布页。
            AppButton {
                objectName: "updateReleaseNotesButton"
                Layout.fillWidth: false
                Layout.alignment: Qt.AlignVCenter
                visible: App.update.updateAvailable
                compact: true
                variant: "ghost"
                text: qsTr("查看更新说明")
                onClicked: App.update.openReleasePage()
            }

            // 一个位置，两种用途：没有可下载的新版本时是「检查更新」；有新版本时随
            // 下载的状态变成「下载并安装」「取消」「立即安装」或「重试」
            // （privacy-boundary.md §8.6）。不支持软件内下载的后台进程仍是「下载新版本」：
            // 把安装程序的地址交给系统浏览器，由浏览器下载。
            AppButton {
                objectName: "checkUpdateNowButton"
                readonly property bool downloadPlace: App.update.downloadPhase.length > 0
                Layout.fillWidth: false
                Layout.alignment: Qt.AlignVCenter
                compact: true
                text: downloadPlace ? App.update.downloadActionText : qsTr("检查更新")
                // 检查：开关关闭或正在检查时不可用（privacy-boundary.md §8.4）。
                // 下载：请求在途或正在校验时不可用。
                enabled: downloadPlace ? App.update.downloadActionEnabled : App.update.canCheck
                onClicked: {
                    if (downloadPlace)
                        App.update.downloadAction()
                    else
                        App.update.checkNow()
                }
            }
        }

        UpdateDownloadStatus {
            namePrefix: "settingsUpdate"
            Layout.fillWidth: true
            Layout.topMargin: 4
            Layout.bottomMargin: 6
        }

        // 维护者工具：重新下载已发布的最新正式版，即使它不比当前版本新。
        RowLayout {
            Layout.fillWidth: true
            Layout.bottomMargin: 4
            visible: App.maintainerToolsVisible && App.update.downloadSupported
            spacing: 12

            Text {
                Layout.fillWidth: true
                text: qsTr("下载最新正式版安装包，即使当前已是同一版本或更高版本。")
                color: Theme.textMuted
                font.pixelSize: Theme.fs(11)
                wrapMode: Text.WordWrap
            }

            AppButton {
                objectName: "reinstallLatestButton"
                Layout.fillWidth: false
                Layout.alignment: Qt.AlignVCenter
                compact: true
                variant: "ghost"
                text: qsTr("重新下载最新正式版")
                enabled: App.update.canReinstall
                onClicked: App.update.reinstallLatest()
            }
        }
    }

    // ------------------------------------------------------------ 外观 --
    SettingsPanel {
        objectName: "appearanceSettingsCard"
        kicker: qsTr("外观")

        SettingsRow {
            label: qsTr("界面风格")
            description: qsTr("经典：圆角卡片 · 艾欧泽亚：游戏窗口配色 · Harendotes：夜色与橙焰")

            SegmentedControl {
                objectName: "uiStyleSettingControl"
                Layout.preferredWidth: 280
                options: [
                    { value: "classic", label: qsTr("经典") },
                    { value: "eorzea", label: qsTr("艾欧泽亚") },
                    { value: "harendotes", label: "Harendotes" }
                ]
                currentValue: tab.uiStyleValue
                onActivated: function(value) {
                    if (typeof Settings !== "undefined" && Settings.uiStyle !== undefined)
                        Settings.uiStyle = value
                }
            }
        }

        SettingsRow {
            label: qsTr("主题")

            SegmentedControl {
                id: themeModeControl
                objectName: "themeModeSettingControl"
                Layout.preferredWidth: 250
                options: [
                    { value: "dark", label: qsTr("深色") },
                    { value: "light", label: qsTr("浅色") },
                    { value: "system", label: qsTr("跟随系统") }
                ]
                currentValue: App.themeMode
                // The shell's circular reveal, from the option that was clicked;
                // a host without one (tests) switches immediately.
                onActivated: function(value) {
                    const change = function() { App.setThemeMode(value) }
                    const shell = ApplicationWindow.window
                    if (shell && typeof shell.revealThemeChange === "function")
                        shell.revealThemeChange(change, themeModeControl.activatedOption)
                    else
                        change()
                }
            }
        }

        // The factor is injected into QT_SCALE_FACTOR before QApplication exists
        // (main.cpp), the only way Qt applies it to every window and font
        // consistently, so it takes effect only after a restart.
        SettingsRow {
            showDivider: false
            label: qsTr("UI 缩放")
            description: qsTr("重启后生效")

            SliderRow {
                Layout.preferredWidth: 216
                Layout.fillWidth: false
                from: 100
                to: 175
                stepSize: 25
                suffix: "%"
                value: App.uiScale
                onMoved: function(value) { App.setUiScale(value) }
            }
        }
    }
}
