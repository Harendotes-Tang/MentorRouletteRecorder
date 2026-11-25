pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "DutyCommunityMatch.js" as Match

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
    // The submission is on the clipboard and the GitHub form is open. The pane stays and says
    // where to paste it: going back to the entry left only a footnote, and a player who then
    // copied the macro itself sent that alone, which the workflow rejects (owner, 2026-11-23).
    property string copiedBody: ""
    readonly property bool copied: copiedBody.length > 0
    property bool copiedAgain: false
    // What the public library already holds is left out and said why (owner, 2026-11-23): the
    // same text under another entry, or this very entry as it would be sent, licence included.
    // The publishing workflow decides the same way, so sending it would change nothing. Without
    // a refreshed library nothing is known here and the workflow alone decides.
    readonly property var sameText: {
        const out = ({})
        for (const row of selected) {
            const twin = Match.sameTextTwin(row.content, store.publicRows)
            if (twin) out[row.content.id] = twin
        }
        return out
    }
    readonly property bool requiresShareAlike: selected.some(row => !sameText[row.content.id] && row.upstream
        && row.upstream.id !== row.content.id && row.upstream.license === "CC-BY-SA-4.0")
    readonly property var licenseChoices: requiresShareAlike ? store.licenseOptions.filter(option => option.value === "CC-BY-SA-4.0")
                                                             : store.licenseOptions
    readonly property string licenseCode: license.currentValue || (licenseChoices.length ? licenseChoices[0].value : "")
    /// { id: { kind: "same_text" | "unchanged", entry } } for every entry left out.
    readonly property var twins: {
        const out = ({})
        for (const row of selected) {
            const id = row.content.id
            const unchanged = sameText[id] ? null : Match.unchangedEntry(draftFor(row), store.publicRows, licenseCode)
            if (sameText[id]) out[id] = { kind: "same_text", entry: sameText[id] }
            else if (unchanged) out[id] = { kind: "unchanged", entry: unchanged }
        }
        return out
    }
    readonly property var submittable: selected.filter(row => !twins[row.content.id])
    signal finished()
    spacing: 0

    function start(items) {
        selected = items
        consent.checked = false
        license.currentIndex = 0
        showFullText = false
        copiedBody = ""
        copiedAgain = false
    }

    function copyAgain() {
        copiedAgain = service.copy(copiedBody)
        store.note = store.statusText(copiedAgain ? "prepared" : "copy_failed")
        if (copiedAgain) copiedAgainTimer.restart()
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
        // A link to a new entry left out for its public twin points at the twin, as the workflow
        // does; a published entry whose edit is held back stays, and so do links to it.
        const published = link => store.publicRows.some(entry => entry.content.id === link)
        item.links = Array.from(new Set(item.links.map(link => sameText[link] && !published(link) ? sameText[link].id : link)))
        return item
    }

    function submit(direct) {
        const bases = ({})
        const items = submittable.map(row => {
            const latest = store.publicRows.find(item => item.content.id === row.content.id)
            if (latest) bases[row.content.id] = latest.content.revision
            return draftFor(row)
        })
        const data = { items: items, base_revisions: bases, license: pane.licenseCode, consent: consent.checked }
        if (account) data.author_id = account.id
        posting = true
        store.call(direct ? "submit" : "prepare_issue", data, (result) => {
            posting = false
            if (result.status === "submitted") {
                store.issueText = String(result.issue)
                store.issueState = ""
                store.basket = []
                if (visible) finished()
            } else if (result.status === "prepared") {
                if (!visible) return
                if (!service.copy(result.body)) { store.note = store.statusText("copy_failed"); return }
                copiedBody = result.body
                service.openGitHub("fallback", 0)
                store.basket = []
            }
            store.report(result)
        })
    }

    onVisibleChanged: if (!visible && code) cancelAuth()
    Timer { id: poll; onTriggered: pane.auth("poll") }
    Timer { id: cancelRetry; interval: 500; onTriggered: pane.cancelAuth() }
    Timer { id: copiedAgainTimer; interval: 1800; onTriggered: pane.copiedAgain = false }

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
                text: pane.copied ? qsTr("投稿正文已复制，去浏览器粘贴") : qsTr("投稿到公共共享库")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(18)
                font.bold: true
            }
            Body {
                text: pane.copied
                      ? qsTr("投稿正文（%1 项）已复制到剪贴板，GitHub 投稿表单已在浏览器打开；没看到表单就点下面的「重新打开表单」。")
                            .arg(pane.submittable.length)
                      : qsTr("投稿通过 GitHub Issue 提交，处理完成后，所有人都能在公共共享库里看到。")
            }

            ColumnLayout {
                visible: !pane.copied
                Layout.fillWidth: true
                spacing: 6
                Repeater {
                    model: pane.selected
                    delegate: InsetBox {
                        id: entry
                        required property var modelData
                        readonly property var twin: pane.twins[modelData.content.id] || null
                        Layout.fillWidth: true
                        implicitHeight: entryColumn.implicitHeight + 16
                        border.width: 1
                        ColumnLayout {
                            id: entryColumn
                            x: 10
                            y: 8
                            width: parent.width - 20
                            spacing: 4
                            RowLayout {
                                Layout.fillWidth: true
                                spacing: 8
                                ContentTypeTag { type: entry.modelData.content.type }
                                Text {
                                    renderType: Theme.bodyRenderType
                                    Layout.fillWidth: true
                                    textFormat: Text.PlainText
                                    elide: Text.ElideRight
                                    text: entry.modelData.content.title
                                    // Left out: told by colour, never by fading the text.
                                    color: entry.twin ? Theme.textSecondary : Theme.textPrimary
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
                            Text {
                                renderType: Theme.bodyRenderType
                                visible: !!entry.twin
                                Layout.fillWidth: true
                                wrapMode: Text.Wrap
                                textFormat: Text.PlainText
                                text: !entry.twin ? ""
                                    : entry.twin.kind === "unchanged" ? qsTr("公共库里已有这一项，没有改动，不再投稿")
                                    : qsTr("公共库已有相同内容「%1」（%2），不再投稿")
                                          .arg(entry.twin.entry.title).arg(entry.twin.entry.author ? entry.twin.entry.author.login : "")
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
                    model: pane.copied ? [
                        qsTr("在浏览器的 GitHub 表单里，点「Community submission」下面的输入框，粘贴（Ctrl+V）。"),
                        qsTr("粘贴进去的是一大段像程序代码的文字，不要删改，直接在 GitHub 页面上提交。"),
                        qsTr("把生成的 Issue 编号填进底部的「投稿进度」，随时查看处理状态。")
                    ] : [
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
                visible: !pane.copied
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
                        text: pane.submittable.length === 0
                              ? qsTr("选中的内容公共共享库里都已经有了，不需要再投稿。")
                              : pane.submittable.length > 20 ? qsTr("一次最多投稿 20 项，请少选几项再投稿。")
                              : qsTr("会公开这 %1 项的标题、正文、副本、BOSS / 阶段、区服和语言，以及所选许可。只投稿你有权分享的文字，避免包含个人信息。")
                                    .arg(pane.submittable.length)
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
                            Layout.fillWidth: true
                            Layout.maximumWidth: 420
                            keepsRowAcrossLanguages: true
                            textRole: "label"
                            valueRole: "value"
                            model: pane.licenseChoices
                        }
                        AppButton {
                            compact: true
                            variant: "ghost"
                            text: pane.showFullText ? qsTr("收起全文") : qsTr("查看将公开的全文")
                            onClicked: pane.showFullText = !pane.showFullText
                        }
                    }
                    Body {
                        objectName: "dutyCommunityLicenseDetail"
                        text: {
                            const choice = pane.licenseChoices[Math.max(0, license.currentIndex)]
                            const detail = choice ? choice.detail : ""
                            return pane.requiresShareAlike
                                ? detail + qsTr("改编的来源要求改过的版本按同样的规则公开，所以只能选这一项。") : detail
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
                                  ? pane.submittable.map(row => JSON.stringify(pane.draftFor(row), null, 2)).join("\n\n") : ""
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
                visible: !pane.store.configured && !pane.copied
                text: pane.store.statusText("app_not_configured")
            }
            // The clipboard holds one text: copying the macro, or anything else, before pasting
            // replaces the submission.
            Rectangle {
                objectName: "dutyCommunityPasteWarning"
                visible: pane.copied
                Layout.fillWidth: true
                Layout.bottomMargin: 18
                implicitHeight: warning.implicitHeight + 24
                radius: Theme.radiusM
                color: Theme.orangeBackground
                border.width: 1
                border.color: Theme.orange
                Body {
                    id: warning
                    x: 14
                    y: 12
                    width: parent.width - 28
                    color: Theme.textPrimary
                    text: qsTr("粘贴之前不要再复制别的内容，宏也不用单独复制。剪贴板只留最后复制的一段，投稿正文被换掉后，GitHub 会因为找不到完整的投稿而处理失败。已经复制过别的，就点「再次复制投稿正文」。")
                }
            }
            Flow {
                Layout.fillWidth: true
                Layout.bottomMargin: 18
                spacing: 6
                visible: pane.store.configured && !pane.copied
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
            objectName: "dutyCommunityCopyAgain"
            visible: pane.copied
            variant: "primary"
            iconName: pane.copiedAgain ? "check" : "copy"
            text: pane.copiedAgain ? qsTr("已复制") : qsTr("再次复制投稿正文")
            onClicked: pane.copyAgain()
        }
        AppButton {
            visible: pane.copied
            iconName: "external-link"
            iconAfterText: true
            text: qsTr("重新打开表单")
            onClicked: pane.service.openGitHub("fallback", 0)
        }
        AppButton {
            objectName: "dutyCommunitySubmissionDone"
            visible: pane.copied
            text: qsTr("完成")
            onClicked: { pane.copiedBody = ""; pane.finished() }
        }
        AppButton {
            objectName: "dutyCommunitySubmitNative"
            visible: pane.store.configured && !pane.copied
            variant: "primary"
            text: qsTr("用 GitHub 账号提交（%1 项）").arg(pane.submittable.length)
            enabled: consent.checked && !!pane.account && !pane.posting && pane.submittable.length > 0
                     && pane.submittable.length <= 20
            onClicked: pane.submit(true)
        }
        AppButton {
            objectName: "dutyCommunitySubmitBrowser"
            visible: !pane.copied
            variant: pane.store.configured ? "secondary" : "primary"
            iconName: "external-link"
            iconAfterText: true
            text: qsTr("点此复制（%1 项）并粘贴到 GitHub 当中").arg(pane.submittable.length)
            enabled: consent.checked && !pane.posting && pane.submittable.length > 0 && pane.submittable.length <= 20
            onClicked: pane.submit(false)
        }
        AppButton {
            objectName: "dutyCommunitySubmissionCancel"
            visible: !pane.copied
            enabled: !pane.posting
            text: qsTr("取消")
            onClicked: pane.finished()
        }
        Item { Layout.fillWidth: true }
    }
}
