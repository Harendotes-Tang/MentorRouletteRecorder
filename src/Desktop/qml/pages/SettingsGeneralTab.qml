import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 设置 · 通用: 启动与托盘 / 记录 / 校准 / 外观.
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
    readonly property string uiStyleValue: Theme.eorzea ? "eorzea" : "classic"

    // One place decides how an unsupported Collector reads, so a switch that can
    // do nothing never looks like one that can.
    function captureSettingDescription(text) {
        if (!App.captureSettingsSupported)
            return qsTr("当前采集器不支持该设置，开关已停用。")
        return text
    }

    // ------------------------------------------------------ 启动与托盘 --
    SettingsPanel {
        objectName: "generalSettingsCard"
        kicker: qsTr("启动与托盘")

        SettingToggleRow {
            label: qsTr("开机启动")
            description: Settings.autostartWritable
                         ? qsTr("随 Windows 登录启动到托盘")
                         : qsTr("当前平台不支持自动启动项")
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
                qsTr("检测到游戏进程即自动开始监听，游戏退出后停止。默认开启："
                     + "解压只能从连接开头跟踪，监听必须先于登录，这样就不用记着手动开始。"))
            toggleEnabled: App.captureSettingsSupported && App.captureSettingsLoaded
            checked: !!App.captureSettings.follow_game
            onToggled: function(value) {
                App.updateCaptureSetting("follow_game", value)
            }
        }

        SettingToggleRow {
            showDivider: false
            label: qsTr("关闭时最小化到托盘")
            description: qsTr("托盘菜单：显示 / 退出")
            checked: Settings.minimizeToTray
            onToggled: function(value) { Settings.minimizeToTray = value }
        }
    }

    // ------------------------------------------------------------ 记录 --
    SettingsPanel {
        kicker: qsTr("记录")

        SettingToggleRow {
            label: qsTr("结束后询问本次结果")
            description: qsTr("这次导随只有你知道有没有通关，程序不会替你判定；"
                              + "关掉后可以在总览的待复核里补确认")
            checked: tab.confirmPromptEnabled
            onToggled: function(value) {
                if (typeof Settings !== "undefined" && Settings.confirmPrompt !== undefined)
                    Settings.confirmPrompt = value
            }
        }

        SettingToggleRow {
            showDivider: false
            label: qsTr("通关后弹出笔记窗口")
            description: qsTr("确认结果后接着记一句；也可稍后在历史记录补录")
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
                qsTr("游戏更新后原有档案会失效。开启后本软件会在你正常打一把随机任务时重新校准，"
                     + "核对无误即可继续自动记录；关闭则要等新版本的档案随软件更新。"))
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
                qsTr("默认开启的联网请求：游戏更新后本机没有可用档案时，从公开的 GitHub 仓库下载其他玩家分享的校准，"
                     + "请求里不带账号、安装编号或任何能认出你的信息，也从不上传。关掉后仍可手动导入校准码。"))
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
    // 玩家可见（docs/privacy-boundary.md §8.4）：默认开启的联网请求，开关须显见。
    // 只提示，不下载、不安装；判断在后台进程，界面只显示结论。
    SettingsPanel {
        kicker: qsTr("更新")

        SettingToggleRow {
            objectName: "updateCheckToggle"
            showDivider: updateCheckStatusText.visible
            label: qsTr("检查新版本并提示")
            description: tab.captureSettingDescription(
                qsTr("默认开启的联网请求：每天最多一次，从本项目的 GitHub 发布页读取一个只含版本号的小文件，"
                     + "有新版本时在总览页提示。请求不带账号、安装编号或任何可识别信息；"
                     + "本软件从不自动下载或安装任何东西。"))
            toggleEnabled: App.captureSettingsSupported && App.captureSettingsLoaded
            checked: !App.captureSettingsLoaded
                     || App.captureSettings.update_check_enabled !== false
            onToggled: function(value) {
                App.updateCaptureSetting("update_check_enabled", value)
            }
        }

        Text {
            id: updateCheckStatusText

            objectName: "updateCheckStatusText"
            Layout.fillWidth: true
            Layout.topMargin: 10
            Layout.bottomMargin: 4
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
    }

    // ------------------------------------------------------------ 外观 --
    SettingsPanel {
        objectName: "appearanceSettingsCard"
        kicker: qsTr("外观")

        SettingsRow {
            label: qsTr("界面风格")
            description: qsTr("经典：圆角卡片 · 艾欧泽亚：金边石板面板")

            SegmentedControl {
                objectName: "uiStyleSettingControl"
                Layout.preferredWidth: 180
                options: [
                    { value: "classic", label: qsTr("经典") },
                    { value: "eorzea", label: qsTr("艾欧泽亚") }
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
