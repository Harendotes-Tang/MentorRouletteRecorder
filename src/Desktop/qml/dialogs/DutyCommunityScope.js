.pragma library

// Pure rules of the 副本提醒与导随宏 list (Design artifact, 条目很多时): how a hand-written
// BOSS / 阶段 falls into a group, and how a search hit is marked. No interface text here: the
// dialog turns a phase code into words of the interface language.

// BOSS / 阶段 is free text and one meaning has many spellings, so entries are grouped by what
// the text means: BOSS 2 = 2 号 BOSS = 老二 = Boss 2 = 2ボス. `code` names the meaning (with
// `n` for a numbered one); a spelling that is not recognised keeps its own group (`code`
// "raw", shown as written) and an empty one is "none". `order` puts the groups in the order of
// a run: 开场, 全程, 道中, the bosses, 尾王, the phases, 转场, unrecognised ones, 结束, 未标.
var phaseCache = ({})
function phase(raw) {
    var text = String(raw === undefined || raw === null ? "" : raw).trim()
    var known = phaseCache[text]
    if (known) return known
    var t = text.toLowerCase().replace(/[\s·・\-_]+/g, "")
    var digits = ({ "一": 1, "二": 2, "三": 3, "四": 4, "五": 5 })
    var m, out
    if (!t) out = { key: "none", order: 900, code: "none", n: 0, raw: "" }
    else if (/^(开场|開場|开幕|開幕|进本|進本|開始|opening|pull|start)$/.test(t)) out = { key: "open", order: 10, code: "open", n: 0, raw: text }
    else if (/^(全程|全体|全體|全般|wholefight)$/.test(t)) out = { key: "all", order: 15, code: "all", n: 0, raw: text }
    else if (/^(道中|道中小怪|小怪|雑魚|道中雑魚|trash)$/.test(t)) out = { key: "trash", order: 20, code: "trash", n: 0, raw: text }
    else if (/^(尾王|最终boss|最終boss|ラスボス|finalboss|lastboss)$/.test(t)) out = { key: "last", order: 60, code: "last", n: 0, raw: text }
    else if ((m = /^(?:boss|ボス)([1-9])$/.exec(t)) || (m = /^([1-9])(?:号boss|號boss|boss|ボス|王)$/.exec(t))
             || (m = /^老([一二三四五])$/.exec(t))) {
        var number = digits[m[1]] || Number(m[1])
        out = { key: "b" + number, order: 30 + number, code: "boss", n: number, raw: text }
    } else if ((m = /^(?:p|阶段|階段|phase|フェーズ)([1-9])$/.exec(t)))
        out = { key: "p" + m[1], order: 70 + Number(m[1]), code: "phase", n: Number(m[1]), raw: text }
    else if (/^(转场|轉場|フェーズ移行|transition)$/.test(t)) out = { key: "trans", order: 80, code: "trans", n: 0, raw: text }
    else if (/^(结束|結束|通关|通關|終了|ending|end)$/.test(t)) out = { key: "end", order: 95, code: "end", n: 0, raw: text }
    else out = { key: "x:" + t, order: 85, code: "raw", n: 0, raw: text }
    phaseCache[text] = out
    return out
}

function escaped(text) {
    return String(text).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
}

// `text` as StyledText with every occurrence of `term` (lower case) in `color` and bold.
// Everything the player wrote is escaped first, so it can never act as markup.
function highlight(text, term, color) {
    var source = String(text)
    if (!term) return escaped(source)
    var low = source.toLowerCase(), out = "", at = 0, hit
    // toLowerCase can change a string's length (İ); then the offsets no longer line up.
    if (low.length !== source.length) return escaped(source)
    while ((hit = low.indexOf(term, at)) >= 0) {
        out += escaped(source.slice(at, hit)) + '<font color="' + color + '"><b>'
             + escaped(source.slice(hit, hit + term.length)) + "</b></font>"
        at = hit + term.length
    }
    return out + escaped(source.slice(at))
}

// The line of `body` that holds `term`, cut so that the hit is in view; "" when none does.
function snippet(body, term) {
    if (!term) return ""
    var lines = String(body).split(/\r?\n/)
    for (var i = 0; i < lines.length; ++i) {
        var at = lines[i].toLowerCase().indexOf(term)
        if (at >= 0) return at > 16 ? "…" + lines[i].slice(at - 10) : lines[i]
    }
    return ""
}
