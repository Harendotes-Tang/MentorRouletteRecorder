pragma ComponentBehavior: Bound
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

Dialog {
    id: root
    objectName: "dutyCommunityDialog"
    required property var run
    readonly property var service: Runtime.app.dutyCommunity
    readonly property var duty: service.dutyIdentity(run)
    readonly property bool remote: tabs.currentIndex === 1
    property var localRows: []
    property var publicRows: []
    property var basket: []
    property var callbacks: ({})
    property int serial: 0
    property int issueNumber: 0
    property bool configured: false
    property bool online: false
    property string libraryStatus: ""
    property string note: ""
    property string selectedId: ""
    readonly property var current: filtered.find(row => row.content.id === selectedId) || null
    readonly property var source: current && current.upstream
        ? (publicRows.find(row => row.content.id === current.upstream.id) || {}).content : null
    readonly property var filtered: (remote ? publicRows : localRows).filter(row => {
        const item = row.content
        if (typeFilter.currentIndex > 0 && item.type !== ["reminder", "macro"][typeFilter.currentIndex - 1]) return false
        if (regionFilter.currentIndex > 0 && item.region !== "ALL" && item.region !== regionFilter.currentText) return false
        if (languageFilter.currentIndex > 0 && item.language !== languageFilter.currentText) return false
        if (!remote && favoriteFilter.checked && !row.favorite) return false
        if (dutyFilter.checked && ((item.content_id !== 0 && item.content_id !== duty.content_id)
                || (item.region !== "ALL" && item.region !== duty.region))) return false
        const author = item.author || (row.upstream && row.upstream.author) || ({})
        const haystack = [item.title, item.body, item.boss, item.phase, item.role, item.content_id,
            item.content_id ? Theme.dutyName(item) : "", author.login].concat(item.tags).join(" ").toLowerCase()
        return haystack.includes(search.text.trim().toLowerCase())
            && (item.boss + " " + item.phase).toLowerCase().includes(stageFilter.text.trim().toLowerCase())
    })

    title: qsTr("副本提醒与导随宏")
    modal: true
    anchors.centerIn: parent
    width: Math.min(1050, parent.width - 20)
    height: Math.min(710, parent.height - 20)
    standardButtons: Dialog.Close

    function call(action, args, callback) {
        const id = ++serial
        callbacks[id] = callback || report
        service.request(id, action, args)
    }

    Connections {
        target: root.service
        function onAnswered(ticket, result, error) {
            const callback = root.callbacks[ticket]
            delete root.callbacks[ticket]
            if (!callback) return
            if (error) root.note = error
            if (result.app_configured !== undefined) root.configured = result.app_configured
            callback(error ? { status: "transport_error" } : result)
        }
    }

    Connections {
        target: Runtime.app
        function onBackendChanged() {
            if (root.visible && Runtime.app.backendConnected) root.reload()
        }
    }

    function pages(action, args, done, prior, first) {
        call(action, args, result => {
            const rows = result.items || result.entries
            if (!rows) { report(result); return }
            const start = first || result
            if (first && result.generation !== first.generation) { note = statusText("source_changed"); return }
            const all = (prior || []).concat(rows)
            if (result.next === null || result.next === undefined) done(all, start)
            else pages(result.items ? "list" : "browse", { offset: result.next }, done, all, start)
        })
    }

    function loadPublic(action, args) {
        pages(action, args || ({}), (rows, result) => {
            publicRows = rows.map(content => ({ content: content }))
            online = result.online
            libraryStatus = result.status
        })
    }

    function loadLocal() { pages("list", {}, rows => { localRows = rows }) }
    function reload() { loadLocal(); loadPublic("browse") }
    onOpened: reload()
    onDutyChanged: if (visible && dutyFilter.checked) {
        selectedId = ""
        copyDialog.close()
        compare.close()
    }

    function openFor(currentDuty) {
        tabs.currentIndex = 0
        dutyFilter.checked = currentDuty
        selectedId = ""
        note = ""
        open()
    }

    function defaultLanguage() {
        const code = Runtime.lang ? Runtime.lang.code : ""
        return code === "ja" || code === "en" ? code : code === "zh-TW" ? "zh-Hant" : "zh-Hans"
    }

    function editNew(type, related) {
        const item = { schema_version: 1, id: service.newId(), type: type,
            content_id: dutyFilter.checked ? duty.content_id || 0 : 0,
            region: dutyFilter.checked ? duty.region || "ALL" : "ALL", language: defaultLanguage(),
            title: "", body: "", boss: "", phase: "", role: "", tags: [], game_version: "", links: [], source: "" }
        if (related) {
            for (const key of ["title", "content_id", "region", "language", "boss", "phase", "role", "game_version"])
                item[key] = related[key]
            item.body = "/p " + related.body.replace(/[\r\n]+/g, " ")
            item.links = [related.id]
        }
        editor.edit({ content: item, revision: 0, position: 0, favorite: false })
    }

    function mutate(action, args, nextId) {
        call(action, args, result => {
            report(result)
            if (!["saved", "installed", "deleted", "updated"].includes(result.status)) {
                if (action === "save") editor.edit(args)
                return
            }
            if (nextId !== undefined) { tabs.currentIndex = 0; selectedId = nextId }
            loadLocal()
        })
    }

    function batch() {
        const queue = basket.length ? basket.slice() : [selectedId]
        const rows = []
        while (queue.length) {
            const id = queue.shift()
            if (rows.some(row => row.content.id === id)) continue
            const row = localRows.find(item => item.content.id === id)
            if (!row) continue
            rows.push(row)
            row.content.links.forEach(link => queue.push(link))
        }
        return rows
    }

    function report(result) { if (result.status !== "transport_error") note = statusText(result.status) }

    function statusText(status) {
        switch (status) {
        case "ready": return ""
        case "offline": return qsTr("公共库联网已关闭，仍可使用本地内容与缓存。")
        case "not_refreshed": return qsTr("手动刷新时连接公共共享库。")
        case "refreshing": return qsTr("正在刷新公共共享库。")
        case "not_deployed": return qsTr("公共共享库尚未部署。")
        case "empty": return qsTr("公共共享库暂时没有内容。")
        case "temporarily_unavailable": return qsTr("暂时无法联网或请求受限，保留上次有效缓存。")
        case "invalid_index": return qsTr("公共索引或内容校验失败，保留上次有效缓存。")
        case "stale_mirror": return qsTr("镜像尚未同步，继续使用现有版本。")
        case "saved": return qsTr("已保存到本地。")
        case "deleted": return qsTr("已删除本地条目。")
        case "installed": return qsTr("已导入，可离线使用。")
        case "updated": return qsTr("已应用来源更新。")
        case "already_installed": return qsTr("已安装，请在本地查看或检查更新。")
        case "source_changed": return qsTr("来源版本已变化，请重新刷新和对比。")
        case "source_unavailable": return qsTr("来源或关联内容已撤回或不可用。")
        case "local_conflict": return qsTr("本地有同一条目或更新的修改，请重新加载。")
        case "local_limit": return qsTr("本地库已达到 400 条。")
        case "invalid_title": return qsTr("请填写标题，最多 120 字。")
        case "empty_body": return qsTr("请填写正文，最多 1600 字。")
        case "macro_lines": return qsTr("完整宏最多 15 行。")
        case "macro_length": return qsTr("每行宏最多 180 个 UTF-8 字节。")
        case "chat_length": return qsTr("聊天指令最多 500 个 UTF-8 字节。")
        case "single_chat_required": return qsTr("聊天复制只支持带正文的单行 /p 或 /a 指令。")
        case "copy_failed": return qsTr("剪贴板写入失败或内容不一致。")
        case "copied": return qsTr("已复制到剪贴板。")
        case "invalid_file": return qsTr("文件格式不正确，单次最多 20 条。")
        case "file_error": return qsTr("无法读取或写入此文件。")
        case "exported": return qsTr("已导出。")
        case "invalid_links": return qsTr("关联项必须存在，且提醒和宏只能关联另一种类型。")
        case "invalid_submission": return qsTr("请选择 1 至 20 条内容并确认投稿许可。")
        case "submission_size": return qsTr("投稿文字超出大小限制，请分批投稿。")
        case "app_not_configured": return qsTr("原生投稿尚未配置 GitHub App，可使用浏览器投稿。")
        case "authorizing": return qsTr("请在 GitHub 输入设备代码并确认授权。")
        case "device_expired": return qsTr("授权代码已过期。")
        case "authorization_denied": return qsTr("授权未完成。")
        case "cancelled": return qsTr("已取消授权。")
        case "auth_busy": return qsTr("上一项授权操作尚在处理。")
        case "signed_out": return qsTr("未登录 GitHub。")
        case "signed_in": return qsTr("已登录 GitHub。")
        case "account_changed": return qsTr("GitHub 账号已变化，请重新预览提交。")
        case "submitted": return qsTr("已提交投稿，等待工作流处理。")
        case "processing": return qsTr("投稿正在处理。")
        case "published": return qsTr("已公开发布。刷新共享库后可查看。")
        case "processing_failed": return qsTr("处理失败，请打开 GitHub Issue 查看原因。")
        case "submit_unconfirmed": return qsTr("提交结果暂时无法确认，请在 GitHub 检查投稿后再重试。")
        case "closed": return qsTr("投稿已关闭。")
        case "prepared": return qsTr("投稿正文已复制，请粘贴到浏览器表单并提交。")
        case "verified": return qsTr("已核验")
        case "outdated": return qsTr("过时")
        case "withdrawn": return qsTr("已撤回")
        case "community": return qsTr("社区投稿／未核验")
        default: return qsTr("输入无效或操作失败。") + " (" + status + ")"
        }
    }

    function details(item) {
        return [item.region, item.language, item.content_id ? Theme.dutyName(item) || String(item.content_id) : qsTr("通用"),
            item.boss, item.phase, item.role, item.game_version, item.author ? "@" + item.author.login : "",
            item.license, item.source, item.status ? statusText(item.status) : "", item.updated_at].filter(x => x).join(" · ")
    }

    function confirm(mode) {
        copyDialog.mode = mode
        copyDialog.body = current.content.body
        copyDialog.open()
    }

    function apply(mode) {
        mutate("apply", { id: source.id, local_id: current.content.id, revision: current.revision,
            source_revision: source.revision, mode: mode })
        compare.close()
    }

    ColumnLayout {
        anchors.fill: parent
        TabBar {
            id: tabs
            Layout.fillWidth: true
            TabButton { text: qsTr("本地提醒与宏") }
            TabButton { text: qsTr("公共共享库") }
            onCurrentIndexChanged: root.selectedId = ""
        }
        Flow {
            Layout.fillWidth: true
            spacing: 6
            CheckBox { id: dutyFilter; text: qsTr("当前副本与通用") }
            CheckBox { id: favoriteFilter; text: qsTr("收藏"); visible: !root.remote }
            ComboBox { id: typeFilter; model: [qsTr("全部类型"), qsTr("提醒"), qsTr("宏")] }
            ComboBox { id: regionFilter; model: [qsTr("全部区服"), "CN", "TC", "GLOBAL"] }
            ComboBox { id: languageFilter; model: [qsTr("全部语言"), "zh-Hans", "zh-Hant", "en", "ja"] }
            TextField { id: search; placeholderText: qsTr("搜索标题、正文、副本或作者") }
            TextField { id: stageFilter; placeholderText: qsTr("按 BOSS 或阶段筛选") }
        }
        Flow {
            Layout.fillWidth: true
            spacing: 6
            visible: !root.remote
            Button { text: qsTr("创建提醒"); onClicked: root.editNew("reminder", null) }
            Button { text: qsTr("创建宏"); onClicked: root.editNew("macro", null) }
            Button { text: qsTr("常用宏示例"); onClicked: examples.open() }
            Button { text: qsTr("从共享库导入"); onClicked: tabs.currentIndex = 1 }
            DutyGuideButton { run: Runtime.app.currentGuideRun || ({}) }
            Button {
                text: qsTr("导入文件")
                onClicked: {
                    const file = root.service.pickImport()
                    if (file.data) root.mutate("import_file", { file: file.data })
                    else if (file.status) root.report(file)
                }
            }
            Button {
                text: qsTr("导出选中项")
                enabled: !!root.current || root.basket.length > 0
                onClicked: {
                    const status = root.service.exportFile(root.batch())
                    if (status) root.report({ status: status })
                }
            }
            Button {
                text: qsTr("投稿选中项")
                enabled: !!root.current || root.basket.length > 0
                onClicked: {
                    const rows = root.batch()
                    if (rows.length && rows.length <= 20) submission.start(rows)
                    else root.note = root.statusText("invalid_submission")
                }
            }
        }
        Flow {
            Layout.fillWidth: true
            spacing: 6
            visible: root.remote
            CheckBox { text: qsTr("允许公共库联网"); checked: root.online; onToggled: root.loadPublic("online", { enabled: checked }) }
            Button { text: qsTr("刷新公共库"); enabled: root.online; onClicked: root.loadPublic("refresh") }
            Label { text: root.statusText(root.libraryStatus || "not_refreshed"); padding: 6 }
        }
        SplitView {
            Layout.fillHeight: true
            Layout.fillWidth: true
            ListView {
                id: listing
                SplitView.preferredWidth: 260
                SplitView.minimumWidth: 125
                clip: true
                model: root.filtered
                ScrollBar.vertical: ScrollBar {}
                delegate: ItemDelegate {
                    id: entry
                    required property var modelData
                    width: listing.width
                    text: (modelData.favorite ? "★ " : "") + modelData.content.title
                    contentItem: Label {
                        text: entry.text
                        textFormat: Text.PlainText
                        elide: Text.ElideRight
                        verticalAlignment: Text.AlignVCenter
                    }
                    highlighted: root.selectedId === modelData.content.id
                    onClicked: root.selectedId = modelData.content.id
                }
                Label { anchors.centerIn: parent; visible: listing.count === 0; text: qsTr("没有匹配的内容") }
            }
            ScrollView {
                SplitView.fillWidth: true
                contentWidth: availableWidth
                clip: true
                ColumnLayout {
                    width: parent.width
                    visible: !!root.current
                    Label {
                        Layout.fillWidth: true
                        wrapMode: Text.Wrap
                        textFormat: Text.PlainText
                        text: root.current ? root.details(root.current.content)
                            + (root.current.upstream ? "\n" + qsTr("来源") + " · " + root.details(root.source || root.current.upstream) : "") : ""
                    }
                    TextArea {
                        Layout.fillWidth: true
                        readOnly: true
                        selectByMouse: true
                        textFormat: TextEdit.PlainText
                        wrapMode: TextEdit.Wrap
                        text: root.current ? root.current.content.body : ""
                    }
                    Repeater {
                        model: root.current ? root.current.content.links : []
                        delegate: Button {
                            required property string modelData
                            readonly property var target: (root.remote ? root.publicRows : root.localRows).find(row => row.content.id === modelData)
                            text: qsTr("关联项") + " · " + (target ? target.content.title : modelData)
                            enabled: !!target
                            onClicked: {
                                dutyFilter.checked = false
                                favoriteFilter.checked = false
                                typeFilter.currentIndex = 0
                                languageFilter.currentIndex = 0
                                regionFilter.currentIndex = 0
                                search.clear()
                                stageFilter.clear()
                                root.selectedId = modelData
                            }
                        }
                    }
                    Flow {
                        Layout.fillWidth: true
                        spacing: 6
                        Button { visible: !!root.current && root.current.content.type === "reminder"; text: qsTr("复制提醒文字"); onClicked: root.confirm("reminder") }
                        Button { visible: !!root.current && root.current.content.type === "macro"; text: qsTr("复制队聊／团聊消息"); onClicked: root.confirm("chat") }
                        Button { visible: !!root.current && root.current.content.type === "macro"; text: qsTr("复制完整宏"); onClicked: root.confirm("macro") }
                        Button {
                            visible: root.remote
                            enabled: !!root.current && root.current.content.status !== "withdrawn"
                            text: qsTr("导入到本地")
                            onClicked: root.mutate("install", { id: root.selectedId }, root.selectedId)
                        }
                        Button { visible: !root.remote; text: qsTr("编辑"); onClicked: editor.edit(root.current) }
                        Button {
                            visible: !root.remote && !!root.current && root.current.content.type === "reminder"
                            text: qsTr("从提醒生成宏")
                            onClicked: root.editNew("macro", root.current.content)
                        }
                        Button { visible: !root.remote; text: qsTr("删除"); onClicked: removeDialog.open() }
                        CheckBox {
                            visible: !root.remote
                            text: qsTr("加入批量选择")
                            checked: root.basket.includes(root.selectedId)
                            onToggled: root.basket = checked ? root.basket.concat(root.selectedId)
                                : root.basket.filter(id => id !== root.selectedId)
                        }
                        Button {
                            visible: !root.remote && !!root.current && !!root.current.upstream
                            text: qsTr("另存个人版本")
                            onClicked: {
                                const id = root.service.newId()
                                root.mutate("fork", { id: root.selectedId, local_id: id, revision: root.current.revision }, id)
                            }
                        }
                        Button {
                            visible: !root.remote && !!root.source && !!root.current
                                && root.source.status !== "withdrawn" && root.source.revision > root.current.upstream.revision
                            text: qsTr("对比来源更新")
                            onClicked: compare.open()
                        }
                    }
                }
            }
        }
        Flow {
            Layout.fillWidth: true
            spacing: 6
            Label { text: qsTr("投稿 Issue 编号"); padding: 6 }
            SpinBox { id: issue; from: 0; to: 2147483647; editable: true; value: root.issueNumber; onValueModified: root.issueNumber = value }
            Button { text: qsTr("查看处理状态"); enabled: issue.value > 0; onClicked: root.call("issue_status", { issue: issue.value }) }
            Button { text: qsTr("打开 GitHub Issue"); enabled: issue.value > 0; onClicked: root.service.openGitHub("issue", issue.value) }
        }
        Label { Layout.fillWidth: true; wrapMode: Text.Wrap; textFormat: Text.PlainText; text: root.note }
    }

    DutyCommunityEditor {
        id: editor
        rows: root.localRows
        service: root.service
        onSaved: value => root.mutate("save", value, value.content.id)
    }
    DutyCommunitySubmission { id: submission; browser: root; service: root.service }
    Dialog {
        id: examples
        title: qsTr("常用宏示例")
        anchors.centerIn: parent
        modal: true
        width: Math.min(660, root.width)
        standardButtons: Dialog.Close
        contentItem: ColumnLayout {
            Repeater {
                model: [
                    qsTr("大家好～不熟悉机制的话可以说一声，需要的话我会简单说明。"),
                    qsTr("等大家看完动画再开，先准备一下。"),
                    qsTr("我是中途加入的，现在进度到哪里？有什么需要先说明的吗？"),
                    qsTr("先停一下，我补充一下刚才的机制，说明后再开。"),
                    qsTr("辛苦了，谢谢大家！")
                ]
                delegate: Button {
                    required property string modelData
                    Layout.fillWidth: true
                    text: modelData
                    onClicked: {
                        examples.close()
                        const item = { schema_version: 1, id: root.service.newId(), type: "macro", content_id: 0,
                            region: "ALL", language: root.defaultLanguage(), title: modelData, body: "/p " + modelData,
                            boss: "", phase: "", role: "", tags: [], game_version: "", links: [], source: "" }
                        editor.edit({ content: item, revision: 0, position: 0, favorite: false })
                    }
                }
            }
        }
    }
    Dialog {
        id: removeDialog
        title: qsTr("删除本地条目")
        anchors.centerIn: parent
        modal: true
        standardButtons: Dialog.Ok | Dialog.Cancel
        onAccepted: if (root.current) root.mutate("delete", { id: root.selectedId, revision: root.current.revision }, "")
    }
    Dialog {
        id: copyDialog
        title: qsTr("确认复制内容")
        anchors.centerIn: parent
        modal: true
        width: Math.min(640, root.width)
        property string mode
        property string body
        standardButtons: Dialog.Ok | Dialog.Cancel
        contentItem: ScrollView {
            implicitHeight: 195
            TextArea { text: copyDialog.body; readOnly: true; textFormat: TextEdit.PlainText; wrapMode: TextEdit.Wrap }
        }
        onAccepted: root.call("copy", { mode: mode, body: body }, result => {
            if (result.status === "copy_ready") root.note = root.statusText(root.service.copy(result.body) ? "copied" : "copy_failed")
            else root.report(result)
        })
    }
    Dialog {
        id: compare
        title: qsTr("来源更新对比")
        anchors.centerIn: parent
        modal: true
        width: Math.min(640, root.width)
        contentItem: ColumnLayout {
            ScrollView {
                Layout.fillWidth: true
                Layout.preferredHeight: 225
                TextArea {
                    readOnly: true
                    textFormat: TextEdit.PlainText
                    wrapMode: TextEdit.Wrap
                    text: {
                        if (!root.source || !root.current) return ""
                        const old = root.current.upstream
                        const keys = Object.keys(root.source).filter(k => JSON.stringify(root.source[k]) !== JSON.stringify(old[k]))
                        return keys.map(k => k + "\n− " + JSON.stringify(old[k]) + "\n+ " + JSON.stringify(root.source[k])).join("\n\n")
                    }
                }
            }
            Flow {
                Layout.fillWidth: true
                spacing: 6
                Button { text: qsTr("应用更新并保留我的修改"); enabled: !!root.current && !!root.source; onClicked: root.apply("keep") }
                Button { text: qsTr("使用来源版本"); enabled: !!root.current && !!root.source; onClicked: root.apply("replace") }
                Button { text: qsTr("取消"); onClicked: compare.close() }
            }
        }
    }
}
