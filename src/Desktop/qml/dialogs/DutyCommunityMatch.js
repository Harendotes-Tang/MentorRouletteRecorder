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

/// The live public entry with the same type and exact text under another id, or null. An edit
/// of one's own published entry that keeps its text is never a repeat (a title change, say),
/// even when an older entry happens to share the text - as the publisher decides.
function sameTextTwin(item, publicRows) {
    const text = exactText(item.body)
    if (publicRows.some(entry => entry.content.id === item.id && exactText(entry.content.body) === text))
        return null
    for (const entry of publicRows) {
        const other = entry.content
        if (other.id !== item.id && other.status !== "withdrawn" && other.type === item.type
                && exactText(other.body) === text)
            return other
    }
    return null
}

/// The public entry this draft would leave exactly as it is, licence included, or null: the
/// publisher keeps such an entry without a new revision.
function unchangedEntry(draft, publicRows, license) {
    const keys = ["schema_version", "type", "content_id", "region", "language", "title", "body", "boss", "phase",
                  "role", "game_version", "source"]
    for (const entry of publicRows) {
        const other = entry.content
        if (other.id === draft.id && other.license === license
                && keys.every(key => other[key] === draft[key])
                && JSON.stringify(other.tags || []) === JSON.stringify(draft.tags || [])
                && JSON.stringify(other.links || []) === JSON.stringify(draft.links || []))
            return other
    }
    return null
}
