import QtQuick

// Owns one form's submission identity and its record -> reflection -> image
// workflow. Views supply validated payloads and narrow service facts; no visual
// control, Popup or application singleton is required by this coordinator.
QtObject {
    id: submission

    property bool sessionActive: false
    property bool editMode: false
    property string recordId: ""
    property bool hasManualReflection: false
    property string reflectionMood: ""
    property string reflectionText: ""
    property string databaseKey: ""
    property bool identityVerified: false
    property bool reflectionBusy: false
    property var imageStore: null

    property int openSerial: 0
    readonly property string currentTicket: (editMode ? "run:" + recordId : "new:") + ":" + openSerial
    property string submissionTicket: ""
    property bool submitting: false
    property bool awaitingRetry: false
    property var lastSubmission: null
    property string savedRunId: ""
    readonly property bool recordSaved: savedRunId.length > 0
    property bool reflectionPending: false
    property bool reflectionSubmitting: false
    property string reflectionDatabaseKey: ""
    property bool reflectionScopeChanged: false
    property var pendingImageAdds: []
    property var removedImagePaths: []
    readonly property bool imagesDirty: pendingImageAdds.length > 0 || removedImagePaths.length > 0
    property string errorCode: ""
    property string errorText: ""

    signal createRequested(var fields, string reason)
    signal correctRequested(var changes, string reason)
    signal reflectionRequested(string runId, string mood, string text)
    signal imagesCommitted(int added, int removed)
    signal completed()

    // Resetting a form invalidates replies to its previous incarnation. Image
    // staging is reset separately by the form when it reads existing images.
    function resetSession() {
        submitting = false
        awaitingRetry = false
        lastSubmission = null
        ++openSerial
        submissionTicket = ""
        savedRunId = ""
        reflectionPending = false
        reflectionSubmitting = false
        reflectionDatabaseKey = ""
        reflectionScopeChanged = false
    }

    function begin(create, payload, reason) {
        submitting = true
        submissionTicket = currentTicket
        lastSubmission = { create: create, payload: payload, reason: reason }
        if (create) {
            reflectionDatabaseKey = databaseKey
            reflectionScopeChanged = false
            createRequested(payload, reason)
        } else correctRequested(payload, reason)
    }

    // True means this click belongs to an in-flight or unfinished submission;
    // callers must not validate/rebuild and send a new record in that case.
    function resume() {
        if (submitting) return true
        if (savedRunId.length > 0) {
            finishSavedRecord()
            return true
        }
        if (!awaitingRetry || !lastSubmission) return false
        errorCode = ""
        errorText = ""
        submitting = true
        submissionTicket = currentTicket
        if (lastSubmission.create)
            createRequested(lastSubmission.payload, lastSubmission.reason)
        else correctRequested(lastSubmission.payload, lastSubmission.reason)
        return true
    }

    function ownsReply(runId) {
        if (!sessionActive || !submitting || submissionTicket.length === 0
                || submissionTicket !== currentTicket) return false
        return !editMode || !runId || runId.length === 0 || runId === recordId
    }

    function acceptSubmission(runId) {
        if (!ownsReply(runId ? runId : "")) return false
        submitting = false
        submissionTicket = ""
        awaitingRetry = false
        lastSubmission = null
        const target = runId && runId.length > 0 ? runId : recordId
        if (hasManualReflection) {
            savedRunId = target
            reflectionPending = true
            finishSavedRecord()
            return true
        }
        if (imagesDirty) {
            savedRunId = target
            if (!commitNoteImages(target)) return true
            savedRunId = ""
        }
        completed()
        return true
    }

    function rejectSubmission(runId, code, text, neverSent) {
        if (!ownsReply(runId)) return
        // A refused transport retry cannot clear an earlier unknown outcome.
        // Only an actual response can prove that an editable retry is safe.
        if (!neverSent) awaitingRetry = code === "ERR_INTERNAL" && !!lastSubmission
        errorText = neverSent
            ? text + "\n" + (awaitingRetry
                ? qsTranslate("EditRunDialog", "这次重试未能发给后台服务，上次提交可能已经保存。为避免重复，"
                              + "只能原样重试或关闭窗口；关闭后请先在历史记录中确认，再决定是否重新填写。")
                : qsTranslate("EditRunDialog", "这次提交未能发给后台服务，记录尚未保存。连接恢复后可重试。"))
            : awaitingRetry
                ? text + "\n" + qsTranslate("EditRunDialog", "尚未确认保存结果，记录可能已经保存。"
                                             + "为避免重复，只能原样重试或关闭窗口；关闭后请先在历史记录中确认，再决定是否重新填写。")
                : text
        submitting = false
        submissionTicket = ""
    }

    // A mood-only note uses stable source words, not the current UI language.
    function manualReflectionText() {
        if (reflectionText.trim().length > 0) return reflectionText.trim()
        return reflectionMood === "good" ? "顺利" : reflectionMood === "ok" ? "一般"
             : reflectionMood === "bad" ? "糟心" : ""
    }

    function scopeChanging() {
        if (!editMode && (submitting || recordSaved)) reflectionScopeChanged = true
    }

    function finishSavedRecord() {
        if (!reflectionPending) {
            finishWithImages(savedRunId)
            return
        }
        if (reflectionScopeChanged || !identityVerified || reflectionDatabaseKey !== databaseKey) {
            errorCode = "ERR_RECOVERY_SCOPE_CHANGED"
            errorText = qsTranslate("EditRunDialog", "记录库连接已变化，心情和心得尚未保存。请保留输入，并在原记录库的历史记录中确认后补录。")
            return
        }
        if (reflectionBusy) {
            errorText = qsTranslate("EditRunDialog", "记录已保存，上一条心得仍在保存中，请稍候重试。")
            return
        }
        errorText = ""
        reflectionSubmitting = true
        submitting = true
        reflectionRequested(savedRunId, reflectionMood, manualReflectionText())
    }

    function reflectionSaved(runId) {
        if (!reflectionSubmitting || runId !== savedRunId) return
        reflectionSubmitting = false
        submitting = false
        reflectionPending = false
        finishWithImages(runId)
    }

    function reflectionFailed(code, message) {
        // A different editor's ERR_BUSY must not release this request.
        if (!reflectionSubmitting || reflectionBusy) return
        reflectionSubmitting = false
        submitting = false
        errorCode = code
        errorText = qsTranslate("EditRunDialog", "记录已保存，心情和心得未保存，请重试。") + "\n" + message
    }

    // Consume only successful additions so retry never copies them twice.
    // Failed removals are reported once unless failed additions prevented them
    // from being attempted, matching NoteImageStore's partial-success contract.
    function commitNoteImages(runId) {
        if (!imagesDirty) return true
        if (!imageStore || !runId) {
            errorCode = "ERR_NOTE_IMAGE"
            errorText = qsTranslate("EditRunDialog", "图片未保存，暂时无法关联到这条记录。")
            return false
        }
        const adds = pendingImageAdds.map(function(item) { return item.path })
        const result = imageStore.commit(runId, adds, removedImagePaths)
        const added = result.added || []
        const removed = result.removed || []
        pendingImageAdds = pendingImageAdds.filter(function(item) { return added.indexOf(item.path) < 0 })
        removedImagePaths = pendingImageAdds.length > 0
            ? removedImagePaths.filter(function(path) { return removed.indexOf(path) < 0 }) : []
        if (!result.ok) {
            errorCode = "ERR_NOTE_IMAGE"
            errorText = result.error || qsTranslate("EditRunDialog", "图片没有保存。")
            return false
        }
        imagesCommitted(added.length, removed.length)
        return true
    }

    function finishWithImages(runId) {
        if (!commitNoteImages(runId)) {
            savedRunId = runId
            return
        }
        savedRunId = ""
        completed()
    }
}
