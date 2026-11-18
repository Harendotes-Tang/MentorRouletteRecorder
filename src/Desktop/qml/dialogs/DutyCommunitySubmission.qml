pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

Dialog {
    id: pane
    required property var browser
    required property var service
    property var selected: []
    property var account: null
    property string code: ""
    property bool posting: false
    property bool cancelled: false
    anchors.centerIn: parent
    modal: true
    width: Math.min(760, parent.width)
    title: qsTr("预览公开投稿")
    standardButtons: Dialog.Close

    function start(items) {
        selected = items
        consent.checked = false
        license.currentIndex = 0
        open()
    }

    function auth(action) {
        if (action === "sign_in") cancelled = false
        browser.call(action, {}, (result) => {
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
            browser.report(result)
        })
    }

    function cancelAuth() {
        cancelled = true
        poll.stop()
        code = ""
        browser.call("cancel", {}, (result) => {
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

    readonly property bool requiresShareAlike: selected.some(row => row.upstream && row.upstream.id !== row.content.id
        && row.upstream.license === "CC-BY-SA-4.0")

    function submit(native) {
        const bases = ({})
        const items = selected.map(row => {
            const latest = browser.publicRows.find(item => item.content.id === row.content.id)
            if (latest) bases[row.content.id] = latest.content.revision
            return draftFor(row)
        })
        const data = { items: items, base_revisions: bases, license: license.currentText, consent: consent.checked }
        if (account) data.author_id = account.id
        posting = true
        browser.call(native ? "submit" : "prepare_issue", data, (result) => {
            posting = false
            if (result.status === "submitted") {
                browser.issueNumber = result.issue
                close()
            } else if (result.status === "prepared") {
                if (!service.copy(result.body)) { browser.note = browser.statusText("copy_failed"); return }
                service.openGitHub("fallback", 0)
                close()
            }
            browser.report(result)
        })
    }

    onClosed: if (code) cancelAuth()
    Timer { id: poll; onTriggered: pane.auth("poll") }
    Timer { id: cancelRetry; interval: 500; onTriggered: pane.cancelAuth() }

    contentItem: ColumnLayout {
        Label {
            Layout.fillWidth: true
            wrapMode: Text.Wrap
            textFormat: Text.PlainText
            text: pane.account ? qsTr("公开作者：@%1").arg(pane.account.login)
                : qsTr("公开作者以提交时登录的 GitHub 账号为准。")
        }
        Label {
            Layout.fillWidth: true
            wrapMode: Text.Wrap
            text: qsTr("以下内容、作者和许可将公开。只投稿你有权分享的文字，避免包含个人信息。")
        }
        ScrollView {
            Layout.fillWidth: true
            Layout.preferredHeight: 205
            TextArea {
                readOnly: true
                textFormat: TextEdit.PlainText
                wrapMode: TextEdit.Wrap
                text: pane.selected.map(row => JSON.stringify(pane.draftFor(row), null, 2)).join("\n\n")
            }
        }
        ComboBox { id: license; model: pane.requiresShareAlike ? ["CC-BY-SA-4.0"] : ["CC-BY-4.0", "CC-BY-SA-4.0"] }
        CheckBox { id: consent; text: qsTr("我确认来源，并同意按所选许可公开发布。") }
        Label {
            visible: !pane.browser.configured
            Layout.fillWidth: true
            wrapMode: Text.Wrap
            text: pane.browser.statusText("app_not_configured")
        }
        Flow {
            Layout.fillWidth: true
            spacing: 6
            Button { visible: pane.browser.configured && !pane.account && !pane.code; text: qsTr("GitHub 账号"); onClicked: pane.auth("account") }
            Button { visible: pane.browser.configured && !pane.account && !pane.code; text: qsTr("开始设备授权"); onClicked: pane.auth("sign_in") }
            Button { visible: !!pane.code; text: qsTr("打开 GitHub 授权"); onClicked: pane.service.openGitHub("device", 0) }
            Button { visible: !!pane.code; text: qsTr("复制代码 %1").arg(pane.code); onClicked: pane.service.copy(pane.code) }
            Button { visible: !!pane.code; text: qsTr("取消授权"); onClicked: pane.cancelAuth() }
            Button { visible: !!pane.account; text: qsTr("退出 GitHub"); onClicked: pane.auth("sign_out") }
            Button { visible: pane.browser.configured; text: qsTr("原生提交"); enabled: consent.checked && !!pane.account && !pane.posting; onClicked: pane.submit(true) }
            Button { text: qsTr("复制投稿并打开浏览器"); enabled: consent.checked && !pane.posting; onClicked: pane.submit(false) }
        }
        Label { Layout.fillWidth: true; wrapMode: Text.Wrap; textFormat: Text.PlainText; text: pane.browser.note }
    }
}
