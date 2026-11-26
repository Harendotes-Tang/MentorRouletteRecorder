pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// 网卡选择, the card the 2026-11-03 report asked for. The Collector had refused
// to start capture because it could not tell which card carries the game
// (capture.start.adapter_undetermined, last_error_code ERR_BAD_REQUEST), and
// its sentence told the player to choose a card on this page, which had nowhere
// to do so. The same card answers a TCP-table reading that puts the game's
// connections where no card of Npcap's is (network_path.kind UNMAPPED /
// LOOPBACK). The rows are ListCaptureAdapters as the maintainer
// table lists them, without the opaque adapter_id; 「使用这张网卡」 writes the
// card into CaptureSettings.adapter_id the way the stale-card notice does, and
// the Collector's own poll retries with it; 「改回自动选择」 clears the setting.
// When to show the card is CaptureChainModel.adapterChoiceOffered's call,
// weighed against the stale-card notice by CaptureNotices.
Card {
    id: picker
    objectName: "captureAdapterPicker"

    readonly property string chosenId: String(Runtime.app.captureSettings.adapter_id || "")
    readonly property bool writable: Runtime.app.captureSettingsSupported && Runtime.app.captureSettingsLoaded

    horizontalPadding: 20
    verticalPadding: 14
    borderColor: Theme.orange
    spacing: 10

    /// A name a player can recognise: the connection name Windows shows, else
    /// the hardware's. Never the opaque adapter_id.
    function adapterName(row) {
        return row.friendly_name || row.description || qsTr("未命名网卡")
    }

    /// What tells the rows apart: the hardware name when a connection name
    /// stands above it, and the addresses the Collector masked to their network
    /// (192.168.1.x), which tell an accelerator's card from the house's.
    function adapterDetail(row) {
        const parts = []
        if (row.friendly_name && row.description && row.description !== row.friendly_name)
            parts.push(row.description)
        const addresses = (row.ipv4_addresses || []).join(", ")
        parts.push(addresses.length > 0 ? addresses : qsTr("没有 IPv4 地址"))
        if (!row.is_up)
            parts.push(qsTr("未连接"))
        return parts.join(" · ")
    }

    /// Mirrors the Collector's CapturePathPlan.IsUsable: a card that is up and
    /// is the loopback card or has an IPv4 address. Any other card the
    /// Collector ignores, so it cannot be chosen here (audit 2026-11-26 K2-2).
    function usable(row) {
        return !!row.is_up && (!!row.is_loopback || (row.ipv4_addresses || []).length > 0)
    }

    /// Writes the choice; an empty id is 自动选择. The Collector reads the
    /// setting on its next start attempt, and a following capture that reads
    /// nothing is replaced onto the chosen card by the Collector's own path
    /// rediscovery (CaptureController.RediscoverNetworkPaths), so nothing else
    /// is asked of it.
    function choose(adapterId) {
        Runtime.app.updateCaptureSetting("adapter_id", adapterId.length > 0 ? adapterId : null)
        Runtime.app.captureAdapterId = adapterId
    }

    ColumnLayout {
        Layout.fillWidth: true
        spacing: 4

        Text {
            objectName: "captureAdapterPickerTitle"
            Layout.fillWidth: true
            text: qsTr("请选择游戏流量所在的网卡")
            color: Theme.orangeText
            font.pixelSize: Theme.fs(15)
            font.bold: true
            wrapMode: Text.WordWrap
        }

        Text {
            objectName: "captureAdapterPickerText"
            Layout.fillWidth: true
            text: qsTr("还没有确定哪张网卡承载游戏流量。开着加速器或 VPN 时，游戏流量往往走它们的网卡；选错了可以随时改回自动选择。")
            textFormat: Text.PlainText
            color: Theme.textPrimary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        Text {
            Layout.fillWidth: true
            visible: !Runtime.app.captureSettingsSupported
            text: qsTr("当前后台服务版本无法保存网卡选择，请更新软件后重试。")
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            wrapMode: Text.WordWrap
        }
    }

    Repeater {
        model: Runtime.app.captureAdapters

        delegate: RowLayout {
            id: adapterRow
            required property var modelData
            required property int index
            readonly property string adapterId: String(adapterRow.modelData.adapter_id || "")
            readonly property bool chosen: picker.chosenId.length > 0 && picker.chosenId === adapterRow.adapterId
            objectName: "captureAdapterChoice_" + adapterRow.index

            Layout.fillWidth: true
            spacing: 10

            Rectangle {
                implicitWidth: 8
                implicitHeight: 8
                radius: 4
                color: adapterRow.modelData.is_up ? Theme.green : Theme.neutral400
            }

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 0

                Text {
                    objectName: "captureAdapterChoiceName_" + adapterRow.index
                    Layout.fillWidth: true
                    text: picker.adapterName(adapterRow.modelData)
                    textFormat: Text.PlainText
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12)
                    font.bold: true
                    elide: Text.ElideRight
                }

                Text {
                    objectName: "captureAdapterChoiceDetail_" + adapterRow.index
                    Layout.fillWidth: true
                    text: picker.adapterDetail(adapterRow.modelData)
                    textFormat: Text.PlainText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    elide: Text.ElideRight
                }
            }

            Tag {
                visible: adapterRow.modelData.is_loopback
                text: qsTr("本机回环")
            }

            Tag {
                visible: adapterRow.modelData.recommended
                text: qsTr("推荐")
                variant: "ink"
            }

            Tag {
                objectName: "captureAdapterChosenTag_" + adapterRow.index
                visible: adapterRow.chosen
                text: qsTr("已选择")
                variant: "ink"
            }

            AppButton {
                objectName: "captureAdapterUseButton_" + adapterRow.index
                visible: !adapterRow.chosen
                compact: true
                variant: "primary"
                text: qsTr("使用这张网卡")
                enabled: picker.writable && adapterRow.adapterId.length > 0
                         && picker.usable(adapterRow.modelData)
                onClicked: picker.choose(adapterRow.adapterId)
            }
        }
    }

    // The empty state carries its own 重新检测网卡: the hero's button is shown
    // to a player only after the Collector's own recovery, and the refusal that
    // raises this card normally comes before any (audit 2026-11-26 K2-3).
    RowLayout {
        Layout.fillWidth: true
        visible: Runtime.app.captureAdapters.length === 0
        spacing: 8

        Text {
            objectName: "captureAdapterPickerEmpty"
            Layout.fillWidth: true
            text: qsTr("没有找到网卡。请点右侧的“重新检测网卡”重试。")
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        AppButton {
            objectName: "captureAdapterPickerRescan"
            compact: true
            text: qsTr("重新检测网卡")
            onClicked: Runtime.app.rescanAdapters()
        }
    }

    RowLayout {
        Layout.fillWidth: true
        visible: picker.chosenId.length > 0
        spacing: 8

        Text {
            Layout.fillWidth: true
            objectName: "captureAdapterChosenNote"
            text: qsTr("软件会优先使用这张网卡；游戏连接不在这张网卡上、而在另一张网卡上时，会自动改用那一张。")
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }

        AppButton {
            objectName: "captureAdapterAutoButton"
            compact: true
            text: qsTr("改回自动选择")
            enabled: picker.writable
            onClicked: picker.choose("")
        }
    }
}
