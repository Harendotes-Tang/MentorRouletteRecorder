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

    function captureSettingDescription(text) {
        if (!App.captureSettingsSupported)
            return qsTr("当前采集器不支持该设置，开关已停用。")
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
            description: qsTr("每日启动时备份，保留 14 份")
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
                ? qsTr("1–90 天 · 采集器当前生效值：%1 天")
                  .arg(App.captureSettings.log_retention_days !== undefined
                       ? App.captureSettings.log_retention_days : Fmt.dash())
                : qsTr("1–90 天 · 正在读取采集器的生效值…"))

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
                objectName: "integrityCheckButton"
                text: App.integrityCheckRunning ? qsTr("正在校验…") : qsTr("完整性校验")
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
            text: qsTr("采集器在启动时和每次备份时也会自动校验，备份完成的提示会显示校验结果。"
                       + "桌面端通过采集器访问数据，不直接连接数据库。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }
}
