import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 设置 · 数据: 数据库位置、自动备份、诊断日志保留天数、立即备份与完整性校验。
// The desktop never opens the database; every action here is a Collector
// message (BackupDatabase, CheckDatabaseIntegrity, UpdateCaptureSettings).
ColumnLayout {
    id: tab

    Layout.fillWidth: true
    spacing: 16

    readonly property var integrity: App.integrityCheckResult || ({})
    signal openImportRequested()
    Component.onCompleted: App.refreshHistoryRetentionSettings()

    function captureSettingDescription(text) {
        if (!App.captureSettingsSupported)
            return qsTr("当前后台服务不支持这项设置，暂时无法修改。")
        return text
    }

    function integrityColor() {
        switch (tab.integrity.state) {
        case "passed":
            return Theme.green
        case "failed":
            return Theme.red
        default:
            return Theme.orangeText
        }
    }

    SettingsPanel {
        objectName: "dataSettingsCard"
        kicker: qsTr("数据库")

        Item {
            Layout.fillWidth: true
            implicitHeight: pathRow.implicitHeight + 18

            RowLayout {
                id: pathRow

                anchors.left: parent.left
                anchors.right: parent.right
                anchors.top: parent.top
                anchors.topMargin: 6
                spacing: 8

                StyledTextField {
                    objectName: "databasePathField"
                    Layout.fillWidth: true
                    readOnly: true
                    font.family: Theme.monoFamily
                    font.pixelSize: Theme.fs(12)
                    text: App.collectorStatus.database_path
                          || "%LOCALAPPDATA%\\MentorRecorder\\mentor_recorder.db"
                }
                AppButton {
                    text: qsTr("打开目录")
                    onClicked: App.openDatabaseFolder()
                }
            }

            Rectangle {
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.bottom: parent.bottom
                height: 1
                color: Theme.border
            }
        }

        SettingToggleRow {
            label: qsTr("自动备份")
            description: qsTr("软件运行且未在导随中时，每天备份一次，保留最近 14 份")
            checked: Settings.autoBackup
            onToggled: function(value) { Settings.autoBackup = value }
        }

        // The Collector owns the log files, so it owns the retention: the
        // desktop must not persist this number in its own INI, where nothing
        // would read it and no log would ever be deleted.
        SettingsRow {
            showDivider: false
            label: qsTr("诊断日志保留天数")
            description: tab.captureSettingDescription(
                App.captureSettingsLoaded
                ? qsTr("可设为 1–90 天，当前保留 %1 天")
                  .arg(App.captureSettings.log_retention_days !== undefined
                       ? App.captureSettings.log_retention_days : Fmt.dash())
                : qsTr("可设为 1–90 天，正在读取当前设置…"))

            StyledTextField {
                objectName: "logRetentionField"
                Layout.preferredWidth: 90
                enabled: App.captureSettingsSupported && App.captureSettingsLoaded
                text: App.captureSettings.log_retention_days !== undefined
                      ? String(App.captureSettings.log_retention_days) : ""
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 1; top: 90 }
                onEditingFinished: {
                    const days = Number(text)
                    if (Number.isFinite(days) && days >= 1 && days <= 90)
                        App.updateCaptureSetting("log_retention_days", days)
                }
            }
        }

        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 1
            color: Theme.border
        }

        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 14
            spacing: 8

            AppButton {
                objectName: "backupNowButton"
                text: qsTr("立即备份")
                onClicked: App.backupDatabase()
            }
            AppButton {
                objectName: "importRecordsButton"
                text: qsTr("导入记录")
                iconName: "file-input"
                onClicked: tab.openImportRequested()
            }
            AppButton {
                objectName: "integrityCheckButton"
                text: App.integrityCheckRunning ? qsTr("正在检查…") : qsTr("检查数据")
                enabled: !App.integrityCheckRunning
                onClicked: App.checkDatabaseIntegrity()
            }
            Item { Layout.fillWidth: true }
        }

        Text {
            objectName: "integrityCheckResultText"
            Layout.fillWidth: true
            Layout.topMargin: 8
            visible: text.length > 0
            text: tab.integrity.text || ""
            color: tab.integrityColor()
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            Layout.topMargin: 8
            Layout.bottomMargin: 6
            // BackupDatabase runs PRAGMA integrity_check too, and the backup
            // toast repeats its result verbatim.
            text: qsTr("点「检查数据」可检查记录数据库是否损坏，软件启动和备份时也会检查。"
                       + "「自动备份」和「立即备份」都只备份数据库，不含备注图片，图片需另行备份。备份结果会显示数据完整性检查的结论。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }

    SettingsPanel {
        objectName: "historyRetentionCard"
        kicker: qsTr("已删除")
        SettingsRow {
            label: qsTr("删除记录保留期")
            description: qsTr("删除记录会先移到「已删除」，可以恢复。默认保留 30 天，到期永久删除记录、心得和备注图片。旧版本中已删除的记录从升级日起算。")
            StyledComboBox {
                id: retentionMode
                objectName: "historyRetentionMode"
                Layout.preferredWidth: 160
                enabled: App.historyRetentionLoaded && App.historyRetentionDays >= 0 && !App.historyRetentionSaving
                model: [qsTr("按天自动清理"), qsTr("永不自动清理")]
                currentIndex: Theme.live(App.historyRetentionDays === 0 ? 1 : 0)
                onActivated: if (currentIndex === 1) App.updateHistoryRetentionSettings(0)
            }
        }
        SettingsRow {
            objectName: "historyRetentionDaysRow"
            visible: retentionMode.currentIndex === 0
            label: qsTr("保留天数")
            description: App.historyRetentionDays > 0 ? qsTr("当前保留 %1 天。恢复后停止计时，再次删除会重新计时。").arg(App.historyRetentionDays) : qsTr("可设为 1–36500 天，点「保存」后生效。")
            StyledTextField {
                id: retentionDays
                objectName: "historyRetentionDays"
                Layout.preferredWidth: 90
                enabled: App.historyRetentionLoaded && App.historyRetentionDays >= 0 && !App.historyRetentionSaving
                text: App.historyRetentionDays > 0 ? String(App.historyRetentionDays) : "30"
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 1; top: 36500 }
                Accessible.name: qsTr("已删除记录保留天数")
            }
            AppButton {
                objectName: "saveHistoryRetention"
                text: App.historyRetentionSaving ? qsTr("正在保存…") : qsTr("保存")
                enabled: App.historyRetentionLoaded && App.historyRetentionDays >= 0 && !App.historyRetentionSaving && retentionDays.acceptableInput
                onClicked: App.updateHistoryRetentionSettings(Number(retentionDays.text))
            }
        }
        Text {
            Layout.fillWidth: true
            objectName: "historyRetentionExplanation"
            Layout.topMargin: 12
            text: qsTr("软件运行时才会自动清理，关闭期间到期的记录会在下次启动时处理。备份、导出文件和分享图片不受影响。")
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        Text {
            objectName: "historyRetentionFeedback"
            Layout.fillWidth: true
            visible: text.length > 0
            text: App.historyRetentionFeedback
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        Text {
            objectName: "historyImageCleanupFeedback"
            Layout.fillWidth: true
            visible: text.length > 0
            text: App.historyImageCleanupFeedback
            textFormat: Text.PlainText
            color: Theme.orangeText
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.Wrap
        }
        AppButton {
            objectName: "historyImageCleanupRetry"
            Layout.topMargin: 12
            text: App.historyImageCleanupRunning ? qsTr("正在重试清理…") : qsTr("重试附件清理")
            enabled: !App.historyImageCleanupRunning && App.backendConnected
            onClicked: App.retryHistoryImageCleanup()
        }
        AppButton {
            text: qsTr("重新读取保留期")
            visible: App.historyRetentionDays < 0
            enabled: !App.historyRetentionSaving && App.backendConnected
            onClicked: App.refreshHistoryRetentionSettings()
        }
    }
}
