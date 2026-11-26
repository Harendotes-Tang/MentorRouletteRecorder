.pragma library

// How 副本提醒与导随宏 tells a repeated or nearly repeated entry apart. The public repository's
// publisher (tools/shared-calibration/community_publish.py) applies the same rules and the same
// threshold, so what the app marks before a submission is what the workflow decides after it.
// tests/Fixtures/shared-calibration/community-text-vectors.json holds both sides to one set of
// cases.

/// Two bodies at least this alike (see similarity) are folded together in the public library.
var SIMILAR = 0.9

// Spelled out because JavaScript's \s and Python's differ; the publisher uses the same class.
var SPACE = "[\\t\\n\\x0b\\x0c\\r \\x1c-\\x1f\\x85\\xa0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff]"
var SPACES = new RegExp(SPACE + "+", "g")
var TRAILING = new RegExp(SPACE + "+$")
var SOUND = /<se\.[0-9]+>/gi

/// The body as the game runs it: line breaks unified, spaces at line ends and blank lines at
/// either end dropped. Two entries of one type with the same exact text are the same entry.
function exactText(body) {
    const lines = String(body || "").replace(/\r\n?/g, "\n").split("\n").map(line => line.replace(TRAILING, ""))
    while (lines.length > 0 && lines[0] === "") lines.shift()
    while (lines.length > 0 && lines[lines.length - 1] === "") lines.pop()
    return lines.join("\n")
}

/// What a reader compares: NFKC (full- and half-width and other compatibility forms folded),
/// sound cues (<se.N>) and all spacing dropped, lower case with the Greek final sigma as sigma.
function looseText(body) {
    let text = String(body || "")
    if (typeof text.normalize === "function") text = text.normalize("NFKC")
    return text.replace(SOUND, "").replace(SPACES, "").toLowerCase().replace(/ς/g, "σ")
}

// The text as code points (Array.from counts UTF-16 units in Qt's engine; spread does not) and
// its neighbouring pairs with their counts.
function shape(text) {
    const chars = [...text]
    const pairs = ({})
    for (let i = 0; i + 1 < chars.length; ++i) {
        const pair = chars[i] + chars[i + 1]
        pairs[pair] = (pairs[pair] || 0) + 1
    }
    return { text: text, length: chars.length, pairs: pairs }
}

function shapedSimilarity(a, b) {
    if (a.text === b.text) return 1
    if (a.length < 2 || b.length < 2) return 0
    let shared = 0
    for (const pair in a.pairs)
        if (b.pairs[pair]) shared += Math.min(a.pairs[pair], b.pairs[pair])
    return 2 * shared / (a.length - 1 + b.length - 1)
}

/// Dice coefficient over neighbouring character pairs of two looseText values, 0 to 1.
function similarity(first, second) {
    return shapedSimilarity(shape(first), shape(second))
}

// The highest similarity two texts of these lengths can reach; skips most comparisons.
function reachable(first, second) {
    const a = Math.max(first - 1, 0), b = Math.max(second - 1, 0)
    return a + b === 0 || 2 * Math.min(a, b) / (a + b) >= SIMILAR
}

/// The entry that stands for a group: a reviewed one first, then the lowest Issue number.
function ranksBefore(first, second) {
    const reviewed = item => item.status === "verified" ? 0 : 1
    if (reviewed(first) !== reviewed(second)) return reviewed(first) < reviewed(second)
    if ((first.issue || 0) !== (second.issue || 0)) return (first.issue || 0) < (second.issue || 0)
    return first.id < second.id
}

/// Groups similar entries of the same type: { id: id of the entry that stands for its group }.
/// Withdrawn entries stay on their own. `rows` are { content } objects.
function groups(rows) {
    const items = rows.map(row => row.content)
    const parent = items.map((item, index) => index)
    const find = index => {
        while (parent[index] !== index) index = parent[index] = parent[parent[index]]
        return index
    }
    const shapes = items.map(item => shape(looseText(item.body)))
    const order = items.map((item, index) => index).sort((a, b) => shapes[a].length - shapes[b].length)
    for (let i = 0; i < order.length; ++i) {
        const a = order[i]
        if (items[a].status === "withdrawn") continue
        for (let j = i + 1; j < order.length; ++j) {
            const b = order[j]
            if (!reachable(shapes[a].length, shapes[b].length)) break
            if (items[b].status === "withdrawn" || items[b].type !== items[a].type) continue
            if (shapedSimilarity(shapes[a], shapes[b]) < SIMILAR) continue
            const rootA = find(a), rootB = find(b)
            if (rootA === rootB) continue
            if (ranksBefore(items[rootA], items[rootB])) parent[rootB] = rootA
            else parent[rootA] = rootB
        }
    }
    const out = ({})
    items.forEach((item, index) => { out[item.id] = items[find(index)].id })
    return out
}

/// Match community_publish.repeats. Revisions first alter the proposed library together.
/// A rejected revision restores the old text and may make another revision a repeat.
/// New drafts then compete in selection order; ties use the lowest ID in the current view.
function repeats(drafts, published) {
    const view = ({})
    const buckets = new Map()

    function setText(entry, text) {
        if (entry.members) entry.members.delete(entry.item.id)
        entry.text = text
        if (entry.item.status === "withdrawn") return
        const type = entry.item.type
        if (!buckets.has(type)) buckets.set(type, new Map())
        const texts = buckets.get(type)
        if (!texts.has(text)) texts.set(text, new Set())
        entry.members = texts.get(text)
        entry.members.add(entry.item.id)
    }

    function add(item, text) {
        const entry = { item: item, text: "", members: null }
        view[item.id] = entry
        setText(entry, text)
    }

    function twinOf(item, text) {
        const texts = buckets.get(item.type)
        const members = texts ? texts.get(text) : undefined
        if (!members) return null
        let first = null
        for (const id of members)
            if (id !== item.id && (first === null || id < first)) first = id
        return first === null ? null : view[first].item
    }

    for (const id of Object.keys(published)) add(published[id], exactText(published[id].body))
    const revisions = []
    const edits = drafts.filter(draft => published[draft.id]).sort((a, b) => a.id < b.id ? -1 : 1)
    for (const draft of edits) {
        const entry = view[draft.id]
        const text = exactText(draft.body)
        if (entry.text === text) continue
        revisions.push({ draft: draft, entry: entry, previous: entry.text })
        setText(entry, text)
    }

    const found = ({})
    let changed
    do {
        changed = false
        for (const revision of revisions) {
            const id = revision.draft.id
            if (found[id]) continue
            const twin = twinOf(revision.draft, revision.entry.text)
            if (!twin) continue
            found[id] = twin
            setText(revision.entry, revision.previous)
            changed = true
        }
    } while (changed)

    for (const draft of drafts) {
        if (published[draft.id]) continue
        const text = exactText(draft.body)
        const twin = twinOf(draft, text)
        if (twin) found[draft.id] = twin
        else add(draft, text)
    }
    return found
}

// Characters a field may not hold, as the Collector (CommunityContent) and the publisher refuse
// them: control characters, bidi controls and the line and paragraph separators everywhere, and in
// a single-line field the zero-width characters as well.
var CONTROL = /[\u0000-\u001f\u007f-\u009f]/
var BIDI = /[\u202a-\u202e\u2066-\u2069\u2028\u2029]/
var ZERO_WIDTH = /[\u200b-\u200d\u2060\ufeff]/

/// Whether `text` may be saved in a field: a multi-line body keeps its line feeds and tabs.
function validText(text, multiline) {
    let value = String(text || "")
    if (multiline) value = value.replace(/\r\n/g, "\n").replace(/[\n\t]/g, "")
    return !CONTROL.test(value) && !BIDI.test(value) && (multiline || !ZERO_WIDTH.test(value))
}

/// A line's length as the Collector counts it for a macro: UTF-8 bytes (a lone surrogate as 3).
function utf8Bytes(line) {
    let bytes = 0
    for (const char of String(line)) {
        const code = char.codePointAt(0)
        bytes += code < 0x80 ? 1 : code < 0x800 ? 2 : code < 0x10000 ? 3 : 4
    }
    return bytes
}

/// Every submitted field compared by the publisher, including the submission-wide licence.
var PUBLISHED_DRAFT_FIELDS = ["schema_version", "id", "type", "content_id", "region", "language", "title", "body",
                              "boss", "phase", "role", "tags", "game_version", "links", "source", "license"]

function differences(current, draft, license) {
    const changes = []
    for (const key of PUBLISHED_DRAFT_FIELDS) {
        const next = key === "license" ? license : draft[key]
        const previous = current[key]
        if (JSON.stringify(previous) !== JSON.stringify(next))
            changes.push({ key: key, previous: previous, next: next })
    }
    return changes
}
