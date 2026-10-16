import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 共享校准：the narrow panel of the calibration row.
//
// 文案由 App.calibration.shared 在 C++ 侧生成并经用词校验，此处仅排布文案与按钮，
// 不显示校准码编号、十六进制或任何状态令牌。「分享给其他玩家」与共享档案在本机完整记录过
// 一次后出现的「这份校准好用，告诉其他玩家」（shareKind 为 local / verified）走同一个 share()：
// 仅将 github.com 地址交由系统浏览器打开，本进程不发起网络访问；是否提交由玩家在浏览器中决定。
Card {
    id: section

    readonly property var shared: App.calibration ? App.calibration.shared : null
    readonly property string view: section.shared ? section.shared.view : "none"
    // 已绑定并在记录，但排本与进本仍在核对（plan §18.4）。
    readonly property bool auditPending: !!section.shared && section.shared.auditPending
    // 页面已说明「已使用其他玩家分享的校准」时，此处不再重复标题。
    property bool headlineShownByCard: false
    readonly property bool offersSomething: !!section.shared
        && (section.shared.canCheck || section.shared.canImport
            || section.shared.canReject || section.shared.canShare)
    // The Collector's own lines on the shared profile that records (when the
    // calibration row shows no local panel to list them under 下一步).
    property var retainingLines: []
    property alias importDialog: importDialog
    property alias rejectDialog: rejectDialog

    visible: !!section.shared && (section.view !== "none" || section.offersSomething)
    horizontalPadding: 20
    verticalPadding: 16
    spacing: 8

    function toneColor() {
        if (section.view === "verified")
            return Theme.green
        if (["consent", "rejected", "unavailable"].indexOf(section.view) >= 0)
            return Theme.orangeText
        return Theme.textPrimary
    }

    CardKicker { text: qsTr("共享校准") }

    Text {
        objectName: "sharedCalibrationHeadline"
        Layout.fillWidth: true
        visible: text.length > 0 && !section.headlineShownByCard
        text: section.shared ? section.shared.headline : ""
        textFormat: Text.PlainText
        color: section.toneColor()
        font.pixelSize: Theme.fs(13)
        font.bold: true
        wrapMode: Text.WordWrap
    }

    Text {
        objectName: "sharedCalibrationDetail"
        Layout.fillWidth: true
        // 共享档案记录时，页面上方已原样显示采集服务的说明，此处不再重复；
        // 但排本与进本仍在核对时，这一句灰字是唯一说明该状态的地方，须照常显示。
        visible: text.length > 0 && (!section.headlineShownByCard || section.auditPending)
        text: section.shared ? section.shared.detail : ""
        textFormat: Text.PlainText
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    Repeater {
        model: section.retainingLines

        delegate: Text {
            required property string modelData

            objectName: "sharedRetainingText"
            Layout.fillWidth: true
            text: modelData
            textFormat: Text.PlainText
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
    }

    // 按排本推断的共享校准：先说清楚代价，同意一次才绑定（plan §4.2 末条）。
    InsetBox {
        objectName: "sharedConsentBox"
        Layout.fillWidth: true
        Layout.topMargin: 2
        implicitHeight: consentColumn.implicitHeight + 24
        visible: section.view === "consent"
        border.width: 1
        border.color: Theme.orange

        ColumnLayout {
            id: consentColumn
            anchors.fill: parent
            anchors.margins: 12
            spacing: 8

            Text {
                objectName: "sharedConsentText"
                Layout.fillWidth: true
                text: section.shared ? section.shared.consentText : ""
                textFormat: Text.PlainText
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(12)
                lineHeight: 1.3
                wrapMode: Text.WordWrap
            }

            // 两个答案并排：同意这份按排本推断的校准，或者先导入手上更好的校准码。
            // 用 Flow 而非 RowLayout，面板窄时换行而不是把按钮挤出面板。
            Flow {
                Layout.fillWidth: true
                spacing: 8

                AppButton {
                    objectName: "sharedAcceptButton"
                    variant: "primary"
                    text: qsTr("同意，开始记录")
                    enabled: !!section.shared && !section.shared.busy
                    onClicked: section.shared.acceptQueueInference()
                }

                AppButton {
                    objectName: "sharedConsentImportButton"
                    visible: !!section.shared && section.shared.canImport
                    text: qsTr("导入校准码")
                    enabled: !!section.shared && !section.shared.busy
                    onClicked: importDialog.openDialog()
                }
            }
        }
    }

    Text {
        objectName: "sharedShareHint"
        Layout.fillWidth: true
        visible: !!section.shared && section.shared.canShare
        // 与协议档案卡共用同一份文案（C++ 的 shareHintText，随 shareKind 变化），避免两张卡出现不同说法。
        text: section.shared ? section.shared.shareHintText : ""
        textFormat: Text.PlainText
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        wrapMode: Text.WordWrap
    }

    Flow {
        objectName: "sharedCalibrationButtons"
        Layout.fillWidth: true
        Layout.topMargin: 4
        visible: section.offersSomething
        spacing: 8

        AppButton {
            objectName: "sharedShareButton"
            visible: !!section.shared && section.shared.canShare
            variant: "primary"
            // 「分享给其他玩家」或「这份校准好用，告诉其他玩家」，按 shareKind 由 C++ 给出。
            text: section.shared ? section.shared.shareButtonText : ""
            enabled: !!section.shared && !section.shared.busy
            onClicked: section.shared.share()
        }

        AppButton {
            objectName: "sharedCheckButton"
            visible: !!section.shared && section.shared.canCheck
            text: qsTr("立即检查")
            enabled: !!section.shared && !section.shared.busy
            onClicked: section.shared.checkNow()
        }

        AppButton {
            objectName: "sharedImportButton"
            // 征求同意时由同意框里的那个按钮承担，避免同屏出现两个一样的按钮。
            visible: !!section.shared && section.shared.canImport && section.view !== "consent"
            text: qsTr("导入校准码")
            enabled: !!section.shared && !section.shared.busy
            onClicked: importDialog.openDialog()
        }

        AppButton {
            objectName: "sharedRejectButton"
            visible: !!section.shared && section.shared.canReject
            text: qsTr("改为本机校准")
            enabled: !!section.shared && !section.shared.busy
            onClicked: rejectDialog.open()
        }
    }

    ImportCalibrationCodeDialog {
        id: importDialog
        parent: Overlay.overlay
        anchors.centerIn: parent
    }

    Dialog {
        id: rejectDialog
        objectName: "sharedRejectDialog"
        parent: Overlay.overlay
        anchors.centerIn: parent
        modal: true
        // Qt Basic's backdrop in eorzea, workbench's .dialog-backdrop in classic.
        Overlay.modal: Rectangle { color: Theme.modalScrim(rejectDialog.palette.shadow) }
        width: 460
        padding: 20
        closePolicy: Popup.CloseOnEscape

        background: DialogFrame {}

        contentItem: ColumnLayout {
            spacing: 12

            HeadingLabel {
                Layout.fillWidth: true
                text: qsTr("改为本机校准？")
                font.pixelSize: Theme.dialogTitleSize(20)
            }

            Text {
                Layout.fillWidth: true
                text: section.shared && section.shared.inUse
                      ? qsTr("将停止使用当前的共享校准。如果已有可用的本机校准，会改用它记录；否则需要重新校准，完成前暂时不能自动记录。已保存的记录不受影响。"
                             + "当前游戏版本将不再使用共享校准。想重新使用时，点「清空校准进度」"
                             + "，本机尚未完成的校准进度也会一起清空。")
                      : qsTr("将取消这份共享校准的检查，继续本机校准，已有进度会保留。"
                             + "当前游戏版本将不再使用共享校准。想重新使用时，点「清空校准进度」"
                             + "，本机尚未完成的校准进度也会一起清空。")
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
                    onClicked: rejectDialog.close()
                }

                AppButton {
                    objectName: "sharedRejectConfirm"
                    variant: "primary"
                    text: qsTr("改为本机校准")
                    enabled: !!section.shared && !section.shared.busy
                    onClicked: {
                        section.shared.reject()
                        rejectDialog.close()
                    }
                }
            }
        }
    }
}
