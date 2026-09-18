import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 设置 · 关于: version and licence, the privacy facts the Collector reports
// (GetStatus.oodle_mode / reads_game_executable, DEC-OODLE-01), the first-run
// disclosure and 版权与来源.
ColumnLayout {
    id: tab

    signal openDisclosureRequested()

    Layout.fillWidth: true
    spacing: 16

    readonly property string disclosureValue: App.disclosureAcknowledged
        ? qsTr("已确认 · %1").arg(App.disclosureAcknowledgedAt
                                  ? Fmt.localTime(App.disclosureAcknowledgedAt)
                                  : qsTr("时间未知"))
        : qsTr("未确认")

    SettingsPanel {
        objectName: "aboutSettingsCard"
        freeLayout: true

        RowLayout {
            Layout.fillWidth: true
            spacing: 12

            CardKicker {
                Layout.fillWidth: false
                text: qsTr("导随记录器")
            }
            Text {
                objectName: "aboutVersionText"
                Layout.fillWidth: true
                // 有没有新版本由后台进程判断；这里只把结论接在版本号后面。
                text: App.update.updateAvailable
                      ? qsTr("v%1 · GPL-3.0 或更高版本 · 有新版本 %2")
                        .arg(App.appVersion).arg(App.update.latestVersion)
                      : qsTr("v%1 · GPL-3.0 或更高版本").arg(App.appVersion)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                elide: Text.ElideRight
            }

            AppButton {
                objectName: "aboutOpenReleasePageButton"
                Layout.fillWidth: false
                visible: App.update.updateAvailable
                compact: true
                text: qsTr("打开下载页")
                onClicked: App.update.openReleasePage()
            }

            // 没有新版本时，同一个位置是主动检查（privacy-boundary.md §8.4）。
            AppButton {
                objectName: "aboutCheckUpdateButton"
                Layout.fillWidth: false
                visible: !App.update.updateAvailable
                compact: true
                text: qsTr("检查更新")
                enabled: App.update.canCheck
                onClicked: App.update.checkNow()
            }
        }

        FieldLabel {
            Layout.fillWidth: true
            text: qsTr("隐私与边界")
        }

        GridLayout {
            Layout.fillWidth: true
            columns: 3
            columnSpacing: 12
            rowSpacing: 12

            FactTile {
                label: qsTr("Oodle 解压")
                value: App.oodleMode || Fmt.dash()
            }
            FactTile {
                objectName: "readsGameExecutableTile"
                label: qsTr("读取游戏可执行文件")
                value: App.readsGameExecutable ? qsTr("是（DEC-OODLE-01）") : qsTr("否")
                valueColor: App.readsGameExecutable ? Theme.orangeText : Theme.textPrimary
            }
            FactTile {
                objectName: "disclosureTile"
                label: qsTr("首次运行说明")
                value: tab.disclosureValue
            }
        }

        RowLayout {
            Layout.fillWidth: true
            spacing: 8
            AppButton {
                text: qsTr("重新查看首次运行说明")
                onClicked: tab.openDisclosureRequested()
            }
            AppButton {
                variant: "ghost"
                text: qsTr("打开捕获诊断")
                onClicked: App.navigate(4)
            }
            Item { Layout.fillWidth: true }
        }
    }

    // -------------------------------------------------------- 版权与来源 --
    // Kept as written: more precise than the prototype's shortened version.
    SettingsPanel {
        objectName: "copyrightCard"
        freeLayout: true
        kicker: qsTr("版权与来源")
        spacing: 8

        Repeater {
            model: [
                qsTr("FINAL FANTASY XIV 是 SQUARE ENIX CO., LTD. 的商标。本软件与 SQUARE ENIX 无任何关联，未获其授权、认可或赞助。"),
                qsTr("界面里的职业、职能、副本类型图标，以及副本与职业名称，是 SQUARE ENIX 的游戏素材，依据 FINAL FANTASY XIV Materials Usage License 作非商业用途使用并在此注明来源。© SQUARE ENIX CO., LTD. All rights reserved."),
                qsTr("图标来源：XIVAPI v2 与 Gamer Escape（社区服务，仅作获取途径，不拥有版权）。名称数据来源：XIVAPI v2、thewakingsands/ffxiv-datamining-cn（社区整理的国服文本导出）。"),
                qsTr("本软件按 GPL-3.0-or-later 开源，使用 Machina.FFXIV（GPL-3.0）与 Qt 6（LGPL-3.0；Qt Graphs 为 GPL-3.0）。Npcap 不随本软件分发，由用户自行安装并遵守其许可条款。完整清单见安装目录下的 THIRD_PARTY_NOTICES.md。")
            ]
            delegate: Text {
                required property string modelData
                Layout.fillWidth: true
                text: modelData
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                lineHeight: 1.4
                wrapMode: Text.WordWrap
            }
        }
    }

    // One inset fact: a small muted label over a 600 value.
    component FactTile: InsetBox {
        id: tile

        property string label: ""
        property string value: ""
        property color valueColor: Theme.textPrimary

        Layout.fillWidth: true
        Layout.preferredWidth: 1
        Layout.fillHeight: true
        implicitHeight: tileColumn.implicitHeight + 20

        ColumnLayout {
            id: tileColumn

            anchors.left: parent.left
            anchors.right: parent.right
            anchors.top: parent.top
            anchors.margins: 10
            anchors.leftMargin: 12
            anchors.rightMargin: 12
            spacing: 2

            Text {
                Layout.fillWidth: true
                text: tile.label
                color: Theme.textSecondary
                opacity: Theme.dimOpacity(0.7)
                font.pixelSize: Theme.fs(11)
                elide: Text.ElideRight
            }
            Text {
                Layout.fillWidth: true
                text: tile.value
                color: tile.valueColor
                font.pixelSize: Theme.fs(13)
                font.weight: Theme.eorzea ? Font.Bold : Font.DemiBold
                wrapMode: Text.WordWrap
            }
        }
    }
}
