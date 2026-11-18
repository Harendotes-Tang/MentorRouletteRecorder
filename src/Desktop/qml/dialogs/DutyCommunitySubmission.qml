pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// The right pane of 副本提醒与导随宏 while submitting: what becomes public, how it is sent,
// under which licence. The player confirms here; nothing opens on top of the list.
ColumnLayout {
    id: pane

    required property var store
    readonly property var service: store.service
    property var selected: []
    property var account: null
    property string code: ""
    property bool posting: false
    property bool cancelled: false
    property bool showFullText: false
    readonly property bool requiresShareAlike: selected.some(row => row.upstream && row.upstream.id !== row.content.id
        && row.upstream.license === "CC-BY-SA-4.0")
    signal finished()
    spacing: 0

    function start(items) {
        selected = items
        consent.checked = false
        license.currentIndex = 0
        showFullText = false
    }

    function auth(action) {
        if (action === "sign_in") cancelled = false
        store.call(action, {}, (result) => {
            if (result.status === "authorizing" && visible && !cancelled) {
                code = result.user_code
                poll.interval = result.interval * 1000
                poll.restart()
            } else if (result.status === "authorizing") {
                cancelAuth()
            } else if (result.status !== "auth_busy") {
                code = ""
                account = result.account || null
            }
            store.report(result)
        })
    }

    function cancelAuth() {
        cancelled = true
        poll.stop()
        code = ""
        store.call("cancel", {}, (result) => {
            if (result.status === "auth_busy") cancelRetry.restart()
        })
    }

    function draftFor(row) {
        const item = Object.assign({}, row.content)
        const base = row.upstream
        if (base && base.id !== item.id) {
            const origin = "@" + base.author.login + " · " + base.license
                + " · https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/" + base.issue
            if (!item.source.includes(origin)) item.source = item.source ? item.source + " · " + origin : origin
        }
        return item
    }

    function submit(native) {
        const bases = ({})
        const items = selected.map(row => {
            const latest = store.publicRows.find(item => item.content.id === row.content.id)
            if (latest) bases[row.content.id] = latest.content.revision
            return draftFor(row)
        })
        const data = { items: items, base_revisions: bases, license: license.currentText, consent: consent.checked }
        if (account) data.author_id = account.id
        posting = true
        store.call(native ? "submit" : "prepare_issue", data, (result) => {
            posting = false
            if (result.status === "submitted") {
                store.issueText = String(result.issue)
                store.issueState = ""
                store.basket = []
                if (visible) finished()
            } else if (result.status === "prepared") {
                if (!service.copy(result.body)) { store.note = store.statusText("copy_failed"); return }
                service.openGitHub("fallback", 0)
                store.basket = []
                if (visible) finished()
            }
            store.report(result)
        })
    }

    onVisibleChanged: if (!visible && code) cancelAuth()
    Timer { id: poll; onTriggered: pane.auth("poll") }
    Timer { id: cancelRetry; interval: 500; onTriggered: pane.cancelAuth() }

    component Body: Text {
        renderType: Theme.bodyRenderType
        Layout.fillWidth: true
        wrapMode: Text.Wrap
        lineHeight: 1.35
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(13)
    }

    ScrollView {
        id: scroller
        Layout.fillWidth: true
        Layout.fillHeight: true
        contentWidth: availableWidth
        clip: true

        ColumnLayout {
            width: scroller.availableWidth - 48
            x: 24
            spacing: 14

            Text {
                objectName: "dutyCommunitySubmissionHeading"
                renderType: Theme.bodyRenderType
                Layout.topMargin: 18
                text: qsTr("投稿到公共共享库")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(18)
                font.bold: true
            }
            Body { text: qsTr("投稿通过 GitHub Issue 提交，处理完成后，所有人都能在公共共享库里看到。") }

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 6
                Repeater {
                    model: pane.selected
                    delegate: InsetBox {
                        id: entry
                        required property var modelData
                        Layout.fillWidth: true
                        implicitHeight: 36
                        border.width: 1
                        RowLayout {
                            anchors.fill: parent
                            anchors.leftMargin: 10
                            anchors.rightMargin: 10
                            spacing: 8
                            ContentTypeTag { type: entry.modelData.content.type }
                            Text {
                                renderType: Theme.bodyRenderType
                                Layout.fillWidth: true
                                textFormat: Text.PlainText
                                elide: Text.ElideRight
                                text: entry.modelData.content.title
                                color: Theme.textPrimary
                                font.pixelSize: Theme.fs(13)
                                font.weight: Font.DemiBold
                            }
                            Text {
                                renderType: Theme.bodyRenderType
                                textFormat: Text.PlainText
                                text: pane.store.dutyLabel(entry.modelData.content)
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(12)
                            }
                        }
                    }
                }
            }

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 9
                Repeater {
                    model: [
                        qsTr("核对下面会公开的内容，选好许可并勾选确认。"),
                        qsTr("用 GitHub 账号直接提交；或者复制投稿正文，在浏览器打开的 GitHub 表单里粘贴并提交。"),
                        qsTr("把生成的 Issue 编号填进底部的「投稿进度」，随时查看处理状态。")
                    ]
                    delegate: RowLayout {
                        id: step
                        required property string modelData
                        required property int index
                        Layout.fillWidth: true
                        spacing: 10
                        Rectangle {
                            Layout.alignment: Qt.AlignTop
                            implicitWidth: 22
                            implicitHeight: 22
                            radius: 11
                            color: Theme.accentMuted
                            Text {
                                anchors.centerIn: parent
                                text: String(step.index + 1)
                                color: Theme.accent
                                font.pixelSize: 12
                                font.bold: true
                            }
                        }
                        Text {
                            renderType: Theme.bodyRenderType
                            Layout.fillWidth: true
                            wrapMode: Text.Wrap
                            text: step.modelData
                            color: Theme.textPrimary
                            font.pixelSize: Theme.fs(13)
                        }
                    }
                }
            }

            InsetBox {
                Layout.fillWidth: true
                implicitHeight: publicColumn.implicitHeight + 24
                border.width: 1
                ColumnLayout {
                    id: publicColumn
                    x: 14
                    y: 12
                    width: parent.width - 28
                    spacing: 10

                    Body {
                        text: qsTr("会公开这 %1 项的标题、正文、副本、BOSS / 阶段、区服和语言，以及所选许可。只投稿你有权分享的文字，避免包含个人信息。")
                              .arg(pane.selected.length)
                    }
                    Body {
                        textFormat: Text.PlainText
                        color: Theme.textPrimary
                        text: pane.account ? qsTr("公开作者：@%1").arg(pane.account.login)
                                           : qsTr("公开作者以提交时登录的 GitHub 账号为准。")
                    }
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 8
                        Text {
                            renderType: Theme.bodyRenderType
                            text: qsTr("许可")
                            color: Theme.textSecondary
                            font.pixelSize: Theme.fs(12)
                        }
                        StyledComboBox {
                            id: license
                            objectName: "dutyCommunityLicense"
                            Layout.preferredWidth: 170
                            keepsRowAcrossLanguages: false
                            model: pane.requiresShareAlike ? ["CC-BY-SA-4.0"] : ["CC-BY-4.0", "CC-BY-SA-4.0"]
                        }
                        Item { Layout.fillWidth: true }
                        AppButton {
                            compact: true
                            variant: "ghost"
                            text: pane.showFullText ? qsTr("收起全文") : qsTr("查看将公开的全文")
                            onClicked: pane.showFullText = !pane.showFullText
                        }
                    }
                    ScrollView {
                        visible: pane.showFullText
                        Layout.fillWidth: true
                        Layout.preferredHeight: 190
                        StyledTextArea {
                            readOnly: true
                            textFormat: TextEdit.PlainText
                            font.family: Theme.monoFamily
                            text: pane.showFullText
                                  ? pane.selected.map(row => JSON.stringify(pane.draftFor(row), null, 2)).join("\n\n") : ""
                        }
                    }
                    SelectionCheckBox {
                        id: consent
                        objectName: "dutyCommunityConsent"
                        text: qsTr("我确认来源，并同意按所选许可公开发布。")
                    }
                }
            }

            Body {
                visible: !pane.store.configured
                text: pane.store.statusText("app_not_configured")
            }
            Flow {
                Layout.fillWidth: true
                Layout.bottomMargin: 18
                spacing: 6
                visible: pane.store.configured
                AppButton { compact: true; visible: !pane.account && !pane.code; text: qsTr("GitHub 账号"); onClicked: pane.auth("account") }
                AppButton { compact: true; visible: !pane.account && !pane.code; text: qsTr("开始设备授权"); onClicked: pane.auth("sign_in") }
                AppButton { compact: true; visible: !!pane.code; text: qsTr("打开 GitHub 授权"); onClicked: pane.service.openGitHub("device", 0) }
                AppButton { compact: true; visible: !!pane.code; text: qsTr("复制代码 %1").arg(pane.code); onClicked: pane.service.copy(pane.code) }
                AppButton { compact: true; visible: !!pane.code; text: qsTr("取消授权"); onClicked: pane.cancelAuth() }
                AppButton { compact: true; visible: !!pane.account; text: qsTr("退出 GitHub"); onClicked: pane.auth("sign_out") }
            }
        }
    }

    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
    RowLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 24
        Layout.rightMargin: 24
        Layout.topMargin: 12
        Layout.bottomMargin: 12
        spacing: 8
        AppButton {
            objectName: "dutyCommunitySubmitNative"
            visible: pane.store.configured
            variant: "primary"
            text: qsTr("用 GitHub 账号提交（%1 项）").arg(pane.selected.length)
            enabled: consent.checked && !!pane.account && !pane.posting
            onClicked: pane.submit(true)
        }
        AppButton {
            objectName: "dutyCommunitySubmitBrowser"
            variant: pane.store.configured ? "secondary" : "primary"
            text: qsTr("复制投稿并打开浏览器") + " ↗"
            enabled: consent.checked && !pane.posting
            onClicked: pane.submit(false)
        }
        AppButton { objectName: "dutyCommunitySubmissionCancel"; text: qsTr("取消"); onClicked: pane.finished() }
        Item { Layout.fillWidth: true }
    }
}
