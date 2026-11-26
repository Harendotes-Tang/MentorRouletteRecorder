pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0
import "DutyCommunityMatch.js" as Match

// The right pane of 副本提醒与导随宏 while submitting: what becomes public, how it is sent,
// under which licence. The player confirms here; nothing opens on top of the list.
// It also withdraws entries this library published (kind "withdraw"): that block goes into the
// same GitHub Issue form, so the paste steps serve both.
ColumnLayout {
    id: pane

    required property var store
    readonly property var service: store.service
    property string kind: "submit"
    readonly property bool withdrawing: kind === "withdraw"
    // Which GitHub form DutyCommunityController.openGitHub opens.
    readonly property string formTarget: withdrawing ? "withdrawal" : "fallback"
    // Withdrawing: the public entries, each as { content }. Submitting: the local rows.
    property var selected: []
    property var account: null
    property string code: ""
    property bool posting: false
    property int submissionGeneration: 0
    property int reviewGeneration: 0
    property int authGeneration: 0
    property bool authorizing: false
    property bool showFullText: false
    // The submission is on the clipboard and the GitHub form is open. The pane stays and says
    // where to paste it: going back to the entry left only a footnote, and a player who then
    // copied the macro itself sent that alone, which the workflow rejects (owner, 2026-11-23).
    property string copiedBody: ""
    property int copiedItemCount: 0
    readonly property bool copied: copiedBody.length > 0
    property bool copiedAgain: false
    property var confirmed: ({})
    readonly property var published: store.publicById
    readonly property var sameText: withdrawing ? ({}) : Match.repeats(selected.map(row => row.content), published)
    // The GitHub accounts that published what is being withdrawn: only they can withdraw it.
    readonly property string authors: withdrawing
        ? Array.from(new Set(selected.map(row => "@" + row.content.author.login))).join(qsTr("、")) : ""
    readonly property bool mixedWithdrawalAuthors: withdrawing
        && new Set(selected.map(row => row.content.author.id)).size > 1
    readonly property bool requiresShareAlike: selected.some(row => !sameText[row.content.id] && row.upstream
        && row.upstream.id !== row.content.id && row.upstream.license === "CC-BY-SA-4.0")
    readonly property var licenseChoices: requiresShareAlike ? store.licenseOptions.filter(option => option.value === "CC-BY-SA-4.0")
                                                             : store.licenseOptions
    readonly property string licenseCode: license.currentValue || (licenseChoices.length ? licenseChoices[0].value : "")
    readonly property var plan: {
        const result = { rows: [], items: [], bases: {}, reviews: {}, outdated: {}, twins: {}, longSource: {} }
        if (withdrawing) return result
        for (const row of selected) {
            const draft = attributedDraft(row)
            const id = draft.id
            const current = published[id]
            // A withdrawn entry is never published again under its ID; the workflow refuses it.
            if (current && current.status === "withdrawn") {
                result.twins[id] = { kind: "withdrawn", entry: current }
                continue
            }
            // Retained revisions keep their ID. Only new entries omitted by the batch redirect links.
            draft.links = Array.from(new Set(draft.links.map(link =>
                sameText[link] && !published[link] ? sameText[link].id : link)))
            const changes = current ? Match.differences(current, draft, licenseCode) : []
            if (current && changes.length === 0) {
                result.twins[id] = { kind: "unchanged", entry: current }
                continue
            }
            if (sameText[id]) {
                result.twins[id] = { kind: "same_text", entry: sameText[id] }
                continue
            }
            const base = row.upstream
            if (base && base.id === id) {
                result.bases[id] = base.revision
                if (current && current.revision > base.revision)
                    result.outdated[id] = { adopted: base.revision, current: current.revision }
            } else if (current) {
                result.reviews[id] = { revision: current.revision, changes: changes,
                    key: JSON.stringify([id, current.revision, draft, licenseCode, changes]) }
            }
            // The Collector and the publisher hold 来源 to 240 characters, the attribution included.
            if (draft.source.length > 240) result.longSource[id] = true
            result.rows.push(row)
            result.items.push(draft)
        }
        return result
    }
    readonly property var twins: plan.twins
    readonly property var submittable: plan.rows
    readonly property string reviewContext: JSON.stringify([licenseCode, plan.items, plan.bases, plan.reviews,
                                                            plan.outdated, account ? account.id : null])
    readonly property bool needsReview: Object.keys(plan.reviews).some(id => confirmed[id] !== plan.reviews[id].key)
    readonly property bool hasOutdatedBase: Object.keys(plan.outdated).length > 0
    readonly property bool hasLongSource: Object.keys(plan.longSource).length > 0
    // What the copy button sends: the entries submitted, or the entries withdrawn.
    readonly property int sendCount: withdrawing ? selected.length : submittable.length
    readonly property bool canSubmit: visible && !copied && !posting && !store.refreshing && consent.checked
        && sendCount > 0 && sendCount <= 20 && (withdrawing ? !mixedWithdrawalAuthors
            : !needsReview && !hasOutdatedBase && !hasLongSource)
    signal finished()
    spacing: 0

    onReviewContextChanged: {
        ++reviewGeneration
        confirmed = ({})
        if (consent) consent.checked = false
    }

    /// nextKind: "submit" (the default) or "withdraw".
    function start(items, nextKind) {
        ++submissionGeneration
        posting = false
        kind = nextKind || "submit"
        selected = items
        confirmed = ({})
        consent.checked = false
        license.currentIndex = 0
        showFullText = false
        copiedBody = ""
        copiedAgain = false
    }

    function copyAgain() {
        copiedAgain = service.copy(copiedBody)
        store.note = store.statusText(!copiedAgain ? "copy_failed" : withdrawing ? "withdrawal_prepared" : "prepared")
        if (copiedAgain) copiedAgainTimer.restart()
    }

    function auth(action) {
        if (action !== "poll") ++authGeneration
        if (action === "sign_in") authorizing = true
        if (action === "sign_out") resetAuthorization()
        const generation = authGeneration
        store.call(action, {}, (result) => {
            if (generation !== authGeneration) return
            if (result.status === "authorizing") {
                code = result.user_code
                poll.interval = result.interval * 1000
                poll.restart()
            } else if (action === "poll" && (result.status === "auth_busy" || result.status === "temporarily_unavailable")) {
                poll.restart()
            } else {
                resetAuthorization()
                if (result.status !== "auth_busy") account = result.account || null
            }
            store.report(result)
        })
    }

    function resetAuthorization() {
        authorizing = false
        poll.stop()
        code = ""
    }

    function cancelAuth() {
        const generation = ++authGeneration
        resetAuthorization()
        store.call("cancel", {}, (result) => {
            if (generation === authGeneration && visible) store.report(result)
        })
    }

    function attributedDraft(row) {
        const item = Object.assign({}, row.content)
        const base = row.upstream
        if (base && base.id !== item.id) {
            const origin = "@" + base.author.login + " · " + base.license
                + " · https://github.com/Harendotes-Tang/MentorRecorder-Calibrations/issues/" + base.issue
            if (!item.source.includes(origin)) item.source = item.source ? item.source + " · " + origin : origin
        }
        return item
    }

    function confirmRevision(id, key, checked) {
        const review = plan.reviews[id]
        if (!review || review.key !== key) return
        confirmed = Object.assign({}, confirmed, { [id]: checked ? key : "" })
        consent.checked = false
    }

    function comparisonText(changes) {
        const labels = {
            schema_version: qsTr("格式版本"), id: qsTr("条目 ID"), type: qsTr("类型"),
            content_id: qsTr("副本编号"), region: qsTr("区服"), language: qsTr("语言"),
            title: qsTr("标题"), body: qsTr("正文"), boss: qsTr("BOSS"), phase: qsTr("阶段"),
            role: qsTr("职业"), tags: qsTr("标签"), game_version: qsTr("游戏版本"),
            links: qsTr("关联条目"), source: qsTr("来源"), license: qsTr("许可")
        }
        // A linked entry by its title where it is known; a list as its items.
        const title = id => {
            const row = store.localRows.concat(store.publicRows).find(entry => entry.content.id === id)
            return row ? "「" + row.content.title + "」" : id
        }
        // A stored code as the dialog shows it elsewhere: the duty, server, language, type, jobs,
        // version range and licence in words (audit 2026-11-26 RE-6).
        const label = (key, item) => {
            switch (key) {
            case "content_id": return store.dutyLabel({ content_id: Number(item) })
            case "region": return store.regionLabel(item)
            case "language": return store.languageLabel(item)
            case "type": return (store.typeOptions.find(option => option.value === item) || { label: String(item) }).label
            case "role": return store.jobsLabel(item)
            case "game_version": return store.versionLabel(item)
            case "license": return store.licenseLabel(item)
            }
            return typeof item === "string" ? item : String(item)
        }
        const value = (key, item) => Array.isArray(item)
            ? (item.length ? item.map(entry => key === "links" ? title(entry) : String(entry)).join(qsTr("、")) : qsTr("（空）"))
            : item === undefined || item === null || item === "" ? qsTr("（空）") : label(key, item) || qsTr("（空）")
        return changes.map(change => labels[change.key] + "\n− " + value(change.key, change.previous)
                                                     + "\n+ " + value(change.key, change.next)).join("\n\n")
    }

    /// direct: through the signed-in GitHub account (submissions only); otherwise the block is
    /// copied for the browser form.
    function submit(direct) {
        if (!canSubmit || (direct && (!account || withdrawing))) return
        const submission = submissionGeneration
        const review = reviewGeneration
        const isCurrent = () => visible && submission === submissionGeneration
            && review === reviewGeneration && !store.refreshing
        let data = { withdraw: selected.map(row => row.content.id) }
        if (!withdrawing) {
            const bases = Object.assign({}, plan.bases)
            for (const id of Object.keys(plan.reviews)) bases[id] = plan.reviews[id].revision
            data = { items: plan.items, base_revisions: bases, license: licenseCode, consent: consent.checked }
            if (account) data.author_id = account.id
        }
        const count = sendCount
        posting = true
        store.call(direct ? "submit" : withdrawing ? "prepare_withdrawal" : "prepare_issue", data, (result) => {
            if (submission === submissionGeneration) posting = false
            const current = isCurrent()
            if (result.status === "submitted") {
                store.issueText = String(result.issue)
                store.issueState = ""
                if (!current) return
                store.basket = []
                finished()
            } else if (result.status === "prepared") {
                if (!current) return
                if (!service.copy(result.body)) { store.note = store.statusText("copy_failed"); return }
                copiedItemCount = count
                copiedBody = result.body
                service.openGitHub(formTarget, 0)
                store.basket = []
            }
            if (current) store.report(withdrawing && result.status === "prepared" ? { status: "withdrawal_prepared" } : result)
        }, direct ? undefined : () => {
            if (isCurrent()) return true
            if (submission === submissionGeneration) posting = false
            return false
        })
    }

    onVisibleChanged: if (!visible) {
        ++submissionGeneration
        posting = false
        if (authorizing) cancelAuth()
    }
    Timer { id: poll; onTriggered: pane.auth("poll") }
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
                text: pane.withdrawing
                      ? (pane.copied ? qsTr("撤回请求已复制，去浏览器粘贴") : qsTr("从公共共享库撤回"))
                      : pane.copied ? qsTr("投稿正文已复制，去浏览器粘贴") : qsTr("投稿到公共共享库")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(18)
                font.bold: true
            }
            Body {
                text: pane.withdrawing
                      ? (pane.copied
                         ? qsTr("撤回请求（%1 项）已复制到剪贴板，GitHub 表单已在浏览器打开；没看到表单就点下面的「重新打开表单」。")
                               .arg(pane.copiedItemCount)
                         : qsTr("撤回通过 GitHub Issue 提交，处理完成后，这些内容在公共共享库里标为「已撤回」。"))
                      : pane.copied
                      ? qsTr("投稿正文（%1 项）已复制到剪贴板，GitHub 投稿表单已在浏览器打开；没看到表单就点下面的「重新打开表单」。")
                            .arg(pane.copiedItemCount)
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
                        readonly property string draftId: modelData.content.id
                        readonly property var twin: pane.twins[draftId] || null
                        readonly property var review: pane.plan.reviews[draftId] || null
                        readonly property var outdated: pane.plan.outdated[draftId] || null
                        // 来源 (with the original author's attribution added) and 标签 become public
                        // too, though the form does not edit them: shown here whenever they hold text.
                        readonly property var draft: pane.attributedDraft(modelData)
                        property string openReviewKey: ""
                        readonly property bool comparing: !!review && openReviewKey === review.key
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
                                text: {
                                    if (!entry.twin) return ""
                                    if (entry.twin.kind === "unchanged") return qsTr("公共库里已有这一项，没有改动，不再投稿")
                                    if (entry.twin.kind === "withdrawn")
                                        return qsTr("这一项已从公共共享库撤回，不能再用原来的条目投稿；想再公开，请新建一条再投稿")
                                    const other = entry.twin.entry
                                    return pane.published[other.id]
                                        ? qsTr("公共库已有相同内容「%1」（%2），不再投稿").arg(other.title).arg(other.author.login)
                                        : qsTr("本次投稿的「%1」已有相同正文，此项不再投稿").arg(other.title)
                                }
                                color: Theme.textSecondary
                                font.pixelSize: Theme.fs(12)
                            }
                            Body {
                                objectName: "dutyCommunitySubmissionSource"
                                visible: !pane.withdrawing && !entry.twin && entry.draft.source.length > 0
                                textFormat: Text.PlainText
                                text: qsTr("来源：%1").arg(entry.draft.source)
                            }
                            Body {
                                objectName: "dutyCommunitySubmissionTags"
                                visible: !pane.withdrawing && !entry.twin && entry.draft.tags.length > 0
                                textFormat: Text.PlainText
                                text: qsTr("标签：%1").arg(entry.draft.tags.join(qsTr("、")))
                            }
                            Body {
                                objectName: "dutyCommunityLongSource"
                                visible: !entry.twin && !!pane.plan.longSource[entry.draftId]
                                color: Theme.red
                                text: qsTr("来源加上原作者署名后超过 240 字，公共共享库收不下，这一项不能投稿。")
                            }
                            Body {
                                objectName: "dutyCommunityOutdatedBase"
                                visible: !!entry.outdated
                                textFormat: Text.PlainText
                                text: entry.outdated
                                    ? qsTr("本地稿件基于第 %1 版，公共库已更新到第 %2 版。请返回条目，核对并应用来源更新后再投稿。")
                                        .arg(entry.outdated.adopted).arg(entry.outdated.current)
                                    : ""
                            }
                            Body {
                                visible: !!entry.review
                                text: entry.review
                                    ? qsTr("此稿件缺少来源修订号。请对比公共库第 %1 版，确认后再投稿。").arg(entry.review.revision)
                                    : ""
                            }
                            AppButton {
                                objectName: "dutyCommunityCompareRevision"
                                visible: !!entry.review
                                compact: true
                                text: entry.comparing ? qsTr("收起对比") : qsTr("对比公共版本与本地稿件")
                                onClicked: entry.openReviewKey = entry.comparing ? "" : entry.review.key
                            }
                            Body {
                                visible: entry.comparing
                                text: qsTr("− 公共版本 / + 本次投稿")
                            }
                            ScrollView {
                                visible: entry.comparing
                                Layout.fillWidth: true
                                Layout.preferredHeight: 156
                                StyledTextArea {
                                    objectName: "dutyCommunityRevisionComparison"
                                    readOnly: true
                                    textFormat: TextEdit.PlainText
                                    wrapMode: TextEdit.Wrap
                                    text: entry.comparing ? pane.comparisonText(entry.review.changes) : ""
                                }
                            }
                            SelectionCheckBox {
                                objectName: "dutyCommunityConfirmRevision"
                                visible: !!entry.review
                                enabled: entry.comparing && !pane.posting
                                text: qsTr("确认以此公共版本为基线")
                                checked: !!entry.review && pane.confirmed[entry.draftId] === entry.review.key
                                onToggled: pane.confirmRevision(entry.draftId, entry.review.key, checked)
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
                        pane.withdrawing
                            ? qsTr("粘贴进去的是一段像程序代码的文字，不要删改，用发布这些内容的 GitHub 账号在页面上提交。")
                            : qsTr("粘贴进去的是一大段像程序代码的文字，不要删改，直接在 GitHub 页面上提交。"),
                        qsTr("把生成的 Issue 编号填进底部的「投稿进度」，随时查看处理状态。")
                    ] : pane.withdrawing ? [
                        qsTr("核对下面要撤回的内容并勾选确认。"),
                        qsTr("复制撤回请求，在浏览器打开的 GitHub 表单里粘贴，用发布这些内容的 GitHub 账号提交。"),
                        qsTr("把生成的 Issue 编号填进底部的「投稿进度」，随时查看处理状态。")
                    ] : [
                        qsTr("核对下面会公开的内容，选好许可并勾选确认。"),
                        pane.store.configured
                            ? qsTr("用 GitHub 账号直接提交；或者复制投稿正文，在浏览器打开的 GitHub 表单里粘贴并提交。")
                            : qsTr("复制投稿正文，在浏览器打开的 GitHub 表单里粘贴并提交。"),
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
                        objectName: "dutyCommunitySubmissionScope"
                        text: pane.withdrawing
                              ? (pane.sendCount > 20 ? qsTr("一次最多撤回 20 项，请少选几项再撤回。")
                                 : qsTr("撤回后，这 %1 项在公共共享库里标为「已撤回」：别人不能再导入，已经导入的人会看到「已撤回」，本地的条目照常保留。撤回不能取消，正文仍留在公开仓库的历史记录里；以后想再公开，需要新建一条再投稿。")
                                       .arg(pane.sendCount))
                              : pane.submittable.length === 0
                              ? (pane.selected.length > 0 && pane.selected.every(row => (pane.twins[row.content.id] || {}).kind === "withdrawn")
                                 ? qsTr("选中的内容都已从公共共享库撤回，不能再投稿。")
                                 : qsTr("选中的内容公共共享库里都已经有了，不需要再投稿。"))
                              : pane.submittable.length > 20 ? qsTr("一次最多投稿 20 项，请少选几项再投稿。")
                              : qsTr("会公开这 %1 项的类型、标题、正文、副本、BOSS / 阶段、职业、游戏版本、区服、语言、标签、来源和关联项，以及所选许可。只投稿你有权分享的文字，避免包含个人信息。")
                                    .arg(pane.submittable.length)
                    }
                    Body {
                        textFormat: Text.PlainText
                        color: Theme.textPrimary
                        text: pane.mixedWithdrawalAuthors
                              ? qsTr("所选条目由不同的 GitHub 账号发布（%1）。请返回列表，按发布账号分别撤回。").arg(pane.authors)
                              : pane.withdrawing
                              ? qsTr("只有发布时的 GitHub 账号能撤回：%1。用别的账号提交会处理失败。").arg(pane.authors)
                              : pane.account ? qsTr("公开作者：@%1").arg(pane.account.login)
                                             : qsTr("公开作者以提交时登录的 GitHub 账号为准。")
                    }
                    RowLayout {
                        visible: !pane.withdrawing
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
                            enabled: !pane.posting
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
                        visible: !pane.withdrawing
                        text: {
                            const choice = pane.licenseChoices[Math.max(0, license.currentIndex)]
                            const detail = choice ? choice.detail : ""
                            return pane.requiresShareAlike
                                ? detail + qsTr("改编的来源要求改过的版本按同样的规则公开，所以只能选这一项。") : detail
                        }
                    }
                    ScrollView {
                        visible: pane.showFullText && !pane.withdrawing
                        Layout.fillWidth: true
                        Layout.preferredHeight: 190
                        StyledTextArea {
                            readOnly: true
                            textFormat: TextEdit.PlainText
                            font.family: Theme.monoFamily
                            text: pane.showFullText
                                  ? pane.plan.items.map(item => JSON.stringify(item, null, 2)).join("\n\n") : ""
                        }
                    }
                    SelectionCheckBox {
                        id: consent
                        objectName: "dutyCommunityConsent"
                        text: pane.withdrawing ? qsTr("我确认撤回这 %1 项，撤回后不能取消。").arg(pane.sendCount)
                                               : qsTr("我确认来源，并同意按所选许可公开发布。")
                        enabled: !pane.posting && (pane.withdrawing ? !pane.mixedWithdrawalAuthors
                            : !pane.needsReview && !pane.hasOutdatedBase)
                    }
                }
            }

            // Without a GitHub App (no shipped build has one) the browser route is all there is:
            // no sign-in, and nothing that suggests one could be set up.
            Body {
                visible: !pane.copied && pane.store.refreshing
                text: pane.store.statusText("refreshing")
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
                    text: pane.withdrawing
                          ? qsTr("粘贴之前不要再复制别的内容。剪贴板只留最后复制的一段，撤回请求被换掉后，GitHub 会因为找不到完整的请求而处理失败。已经复制过别的，就点「再次复制撤回请求」。")
                          : qsTr("粘贴之前不要再复制别的内容，宏也不用单独复制。剪贴板只留最后复制的一段，投稿正文被换掉后，GitHub 会因为找不到完整的投稿而处理失败。已经复制过别的，就点「再次复制投稿正文」。")
                }
            }
            Flow {
                Layout.fillWidth: true
                Layout.bottomMargin: 18
                spacing: 6
                visible: pane.store.configured && !pane.copied && !pane.withdrawing
                AppButton { compact: true; visible: !pane.account && !pane.authorizing; text: qsTr("GitHub 账号"); onClicked: pane.auth("account") }
                AppButton { compact: true; visible: !pane.account && !pane.authorizing; text: qsTr("开始设备授权"); onClicked: pane.auth("sign_in") }
                AppButton { compact: true; visible: !!pane.code; text: qsTr("打开 GitHub 授权"); onClicked: pane.service.openGitHub("device", 0) }
                AppButton { compact: true; visible: !!pane.code; text: qsTr("复制代码 %1").arg(pane.code); onClicked: pane.service.copy(pane.code) }
                AppButton { compact: true; visible: pane.authorizing; text: qsTr("取消授权"); onClicked: pane.cancelAuth() }
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
            text: pane.copiedAgain ? qsTr("已复制") : pane.withdrawing ? qsTr("再次复制撤回请求") : qsTr("再次复制投稿正文")
            onClicked: pane.copyAgain()
        }
        AppButton {
            visible: pane.copied
            iconName: "external-link"
            iconAfterText: true
            text: qsTr("重新打开表单")
            onClicked: pane.service.openGitHub(pane.formTarget, 0)
        }
        AppButton {
            objectName: "dutyCommunitySubmissionDone"
            visible: pane.copied
            text: qsTr("完成")
            onClicked: { pane.copiedBody = ""; pane.finished() }
        }
        AppButton {
            objectName: "dutyCommunitySubmitNative"
            visible: pane.store.configured && !pane.copied && !pane.withdrawing
            variant: "primary"
            text: qsTr("用 GitHub 账号提交（%1 项）").arg(pane.submittable.length)
            enabled: pane.canSubmit && !!pane.account
            onClicked: pane.submit(true)
        }
        AppButton {
            objectName: "dutyCommunitySubmitBrowser"
            visible: !pane.copied
            variant: pane.store.configured && !pane.withdrawing ? "secondary" : "primary"
            iconName: "external-link"
            iconAfterText: true
            text: pane.withdrawing ? qsTr("点此复制撤回请求（%1 项）并粘贴到 GitHub 当中").arg(pane.sendCount)
                                   : qsTr("点此复制（%1 项）并粘贴到 GitHub 当中").arg(pane.submittable.length)
            enabled: pane.canSubmit
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
