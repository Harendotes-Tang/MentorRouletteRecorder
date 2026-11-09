.pragma library
.import "Lucide.js" as Lucide

// Preserve the float and result-popup keys while drawing every mark from the
// same bundled Lucide SVG library used by the rest of the interface.
var icons = {
    "progress": "medal",
    "current": "flag",
    "time": "timer",
    "duty": "swords",
    "roles": "users",
    "pending": "inbox",
    "grip": "grip-vertical",
    "crest": "diamond",
    "to-mini": "minimize-2",
    "to-full": "maximize-2",
    "customize": "sliders-horizontal",
    "lock": "lock",
    "unlock": "lock-open",
    "close": "x",
    "resize": "move-diagonal-2",
    "arrow-up": "arrow-up",
    "arrow-down": "arrow-down",
    "chevron-right": "chevron-right",
    "chevron-left": "chevron-left",
    "check": "check",
    "clock": "clock",
    "timer": "timer",
    "leave": "log-out",
    "bell": "bell",
    "bell-off": "bell-off",
    "info": "info",
    "collapse": "panel-top-close",
    "face-good": "smile",
    "face-ok": "meh",
    "face-bad": "frown"
}

function has(name) {
    return Object.prototype.hasOwnProperty.call(icons, name) && Lucide.has(icons[name])
}

function svg(name, color) {
    return has(name) ? Lucide.svg(icons[name], color) : ""
}

function source(name, color) {
    return has(name) ? Lucide.source(icons[name], color) : ""
}
