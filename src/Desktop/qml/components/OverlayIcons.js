.pragma library

// The 16 x 16 line icons of the statistics float and the result popup
// (docs/ui-design.md §4.9). Drawn for this program after the design canvas;
// not Lucide, so they carry no third-party licence. Like Lucide.js, an icon is
// the drawing inside a 16 x 16 viewBox and source(name, colour) renders it as
// a data: URL for Image. `COLOR` inside a drawing is replaced by the colour,
// for the few shapes that are filled rather than stroked.

// name -> { body, width (stroke width), fill (true: a filled glyph, no stroke) }
var icons = {
    // Module marks
    "progress": { body: "<circle cx='8' cy='10.2' r='3.9'/><path d='M5.6 6.9 3.6 1.8h3l1.4 3M10.4 6.9l2-5.1h-3L8 4.8'/>", width: 1.6 },
    "current": { body: "<path d='M4 14.2V2.2'/><path d='M4 2.8h8.4l-1.9 2.9 1.9 2.9H4' fill='COLOR' fill-opacity='.35'/>", width: 1.6 },
    "time": { body: "<circle cx='8' cy='9' r='5'/><path d='M8 9V6.3M6.3 2h3.4M12.3 4.5l1-1'/>", width: 1.6 },
    "duty": { body: "<path d='M2.4 14V7.6a5.6 5.6 0 0 1 11.2 0V14M1.6 14h12.8M6 14v-3.6a2 2 0 0 1 4 0V14'/>", width: 1.6 },
    "roles": { body: "<circle cx='8' cy='4.6' r='2.1'/><path d='M4.5 12.6a3.5 3.5 0 0 1 7 0'/><circle cx='3.2' cy='7' r='1.5'/><circle cx='12.8' cy='7' r='1.5'/><path d='M1 12.6a2.5 2.5 0 0 1 3-2.4M15 12.6a2.5 2.5 0 0 0-3-2.4'/>", width: 1.6 },
    "pending": { body: "<path d='M2 9.4 4 3.4h8l2 6v3.8a.8.8 0 0 1-.8.8H2.8a.8.8 0 0 1-.8-.8z'/><path d='M2 9.4h3.4l1 1.7h3.2l1-1.7H14'/>", width: 1.6 },
    // Window chrome
    "grip": { body: "<circle cx='6' cy='4' r='1.15'/><circle cx='10' cy='4' r='1.15'/><circle cx='6' cy='8' r='1.15'/><circle cx='10' cy='8' r='1.15'/><circle cx='6' cy='12' r='1.15'/><circle cx='10' cy='12' r='1.15'/>", fill: true },
    "crest": { body: "<path d='M8 1.2 14.8 8 8 14.8 1.2 8z' fill='none' stroke='COLOR' stroke-width='1.4' stroke-linejoin='round'/><path d='M8 4.9 11.1 8 8 11.1 4.9 8z' fill='COLOR'/>", fill: true },
    "to-mini": { body: "<path d='M4.5 2.8 8 6.2l3.5-3.4M4.5 13.2 8 9.8l3.5 3.4'/>", width: 1.5 },
    "to-full": { body: "<path d='M4.5 6 8 2.6 11.5 6M4.5 10 8 13.4 11.5 10'/>", width: 1.5 },
    "customize": { body: "<path d='M2.5 4.5h11M2.5 11.5h11'/><circle cx='6' cy='4.5' r='1.7' fill='COLOR'/><circle cx='10.5' cy='11.5' r='1.7' fill='COLOR'/>", width: 1.5 },
    "lock": { body: "<rect x='3.2' y='7' width='9.6' height='6.8' rx='1.4'/><path d='M5.4 7V5.1a2.6 2.6 0 0 1 5.2 0V7'/>", width: 1.5 },
    "unlock": { body: "<rect x='3.2' y='7' width='9.6' height='6.8' rx='1.4'/><path d='M5.4 7V5.1a2.6 2.6 0 0 1 5-.9'/>", width: 1.5 },
    "close": { body: "<path d='M4 4l8 8M12 4l-8 8'/>", width: 1.6 },
    "resize": { body: "<path d='M14 7.5 7.5 14M14 11 11 14'/>", width: 1.3 },
    "arrow-up": { body: "<path d='M4 10 8 6l4 4'/>", width: 1.7 },
    "arrow-down": { body: "<path d='M4 6l4 4 4-4'/>", width: 1.7 },
    "chevron-right": { body: "<path d='M6 3.5 10.5 8 6 12.5'/>", width: 1.7 },
    "chevron-left": { body: "<path d='M10 3.5 5.5 8 10 12.5'/>", width: 1.7 },
    // States
    "check": { body: "<path d='M3.4 8.6 6.6 11.6 12.8 4.6'/>", width: 2 },
    "clock": { body: "<circle cx='8' cy='8' r='5.8'/><path d='M8 4.9V8l2.1 1.5'/>", width: 1.5 },
    "timer": { body: "<circle cx='8' cy='9' r='5'/><path d='M8 9V6.4M6.4 2h3.2M12.2 4.6l1-1'/>", width: 1.4 },
    "leave": { body: "<path d='M9.5 2.8H4.6a1 1 0 0 0-1 1v8.4a1 1 0 0 0 1 1h4.9'/><path d='M7.4 8h6.2M11.2 5.6 13.6 8l-2.4 2.4'/>", width: 1.5 },
    // Popup header
    "bell": { body: "<path d='M4.1 11.2V7.4a3.9 3.9 0 0 1 7.8 0v3.8l1.1 1.2H3z'/><path d='M6.7 14a1.4 1.4 0 0 0 2.6 0'/>", width: 1.5 },
    "bell-off": { body: "<path d='M4.1 11.2V7.4a3.9 3.9 0 0 1 7.8 0v3.8l1.1 1.2H3z'/><path d='M6.7 14a1.4 1.4 0 0 0 2.6 0M2.4 2.4l11.2 11.2'/>", width: 1.5 },
    "info": { body: "<circle cx='8' cy='8' r='6'/><path d='M8 7.3v4'/><circle cx='8' cy='4.9' r='.55' fill='COLOR'/>", width: 1.5 },
    "collapse": { body: "<path d='M4.6 4.2 8 7.6l3.4-3.4'/><path d='M2.6 9.6h3l1 1.6h2.8l1-1.6h3v3.2a.8.8 0 0 1-.8.8H3.4a.8.8 0 0 1-.8-.8z'/>", width: 1.5 },
    // Moods
    "face-good": { body: "<circle cx='8' cy='8' r='6.4'/><path d='M5.2 9.5a3.3 3.3 0 0 0 5.6 0'/><path d='M5.9 6.4v.2M10.1 6.4v.2' stroke-width='2.2'/>", width: 1.5 },
    "face-ok": { body: "<circle cx='8' cy='8' r='6.4'/><path d='M5.5 10.2h5'/><path d='M5.9 6.4v.2M10.1 6.4v.2' stroke-width='2.2'/>", width: 1.5 },
    "face-bad": { body: "<circle cx='8' cy='8' r='6.4'/><path d='M5.3 11.2a3.2 3.2 0 0 1 5.4 0'/><path d='M5.9 6.4v.2M10.1 6.4v.2' stroke-width='2.2'/><path d='M4.6 4.9l2 .8M11.4 4.9l-2 .8'/>", width: 1.5 }
}

function has(name) {
    return Object.prototype.hasOwnProperty.call(icons, name)
}

// QML writes a translucent colour as #aarrggbb; SVG wants rgb plus an opacity.
function split(color) {
    var text = String(color)
    if (text.length === 9)
        return { rgb: "#" + text.substr(3), opacity: (parseInt(text.substr(1, 2), 16) / 255).toFixed(3) }
    return { rgb: text, opacity: "" }
}

function svg(name, color) {
    if (!has(name))
        return ""
    var icon = icons[name]
    var c = split(color)
    var body = icon.body.replace(/COLOR/g, c.rgb)
    var open
    if (icon.fill)
        open = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' fill='" + c.rgb + "'"
               + (c.opacity ? " fill-opacity='" + c.opacity + "'" : "") + ">"
    else
        open = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' fill='none' stroke='" + c.rgb + "'"
               + (c.opacity ? " stroke-opacity='" + c.opacity + "'" : "")
               + " stroke-width='" + icon.width + "' stroke-linecap='round' stroke-linejoin='round'>"
    return open + body + "</svg>"
}

// An Image source for `name` drawn in `color`; empty when the icon is unknown.
function source(name, color) {
    var text = svg(name, color)
    return text ? "data:image/svg+xml;utf8," + encodeURIComponent(text) : ""
}
