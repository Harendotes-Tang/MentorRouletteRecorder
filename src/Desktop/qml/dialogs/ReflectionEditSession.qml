import QtQml

// One in-memory reflection edit, independent of its page, dialog or compact
// editor. RecoveryController remains the owner of persisted drafts; callers
// adapt backend replies and presentation lifecycle to these shared fields.
QtObject {
    property string text: ""
    property string mood: "good"
    property string baselineText: ""
    property string baselineMood: "good"
    property bool loadingDraft: false
    property bool completedAction: false
    property bool conflictUnresolved: false
    property string draftToken: ""
    property string editScope: ""

    property bool submitting: false
    property bool resolving: false
    property bool waitingForReflection: false
    property string pendingResult: ""
    property string pendingReason: ""
    property string submittedDraftToken: ""
    property string submittedNote: ""
    readonly property bool busy: submitting || resolving

    // Preserve the displayed label's source word across live language changes.
    // Free text is never translated or rewritten by the session.
    property string shownMoodLabel: ""
    property string shownMoodWord: ""

    function moodOnlyText(value) {
        return value === "good" ? "顺利" : value === "ok" ? "一般" : value === "bad" ? "糟心" : ""
    }

    // The presentation supplies the label once; no editor or translator is
    // retained, so replacing every visual host cannot discard the note.
    function showNote(stored, displayed) {
        const value = stored || ""
        const shown = displayed || ""
        shownMoodLabel = shown !== value ? shown : ""
        shownMoodWord = shown !== value ? value : ""
        text = shown
    }

    function storedNote(value) {
        const current = value || ""
        return shownMoodLabel.length > 0 && current.trim() === shownMoodLabel ? shownMoodWord : current
    }

    // Explicit save accepts the displayed mood even when unchanged. Completing
    // an untouched prompt does not create a note; clearing an existing one
    // deliberately remains an empty string for the storage deletion contract.
    function noteToSave(explicit) {
        const typed = storedNote(text).trim()
        const stored = baselineText.trim()
        if (typed.length > 0)
            return typed === stored && stored === moodOnlyText(baselineMood) ? moodOnlyText(mood) : typed
        if (stored.length > 0)
            return ""
        return explicit || mood !== baselineMood ? moodOnlyText(mood) : ""
    }
}
