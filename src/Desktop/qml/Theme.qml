pragma Singleton

import QtQuick

// Single source of truth for the palette. Two visual styles share one token
// vocabulary (docs/ui-design.md):
//
//   * "classic" - DOC/表单提交后设计/workbench.css (default: light work surface,
//                 #2f6bd8 accent, six-step type scale, IBM Plex Mono figures)
//   * "eorzea"  - the game's own window palette: dark = warm slate grey with
//                 pale text, light = parchment with dark-brown text; gold is
//                 kept for markers, rings and bars only (docs/ui-design.md 6.1)
//   * "harendotes" - the wolf's own colours. Dark "夜色狼身": navy-black fur
//                 as the ground, flame orange (the mane) as the accent, old
//                 gold only as a garnish. Light "白胸冷光": the white chest as
//                 panels on a cool blue-grey, chrome as pale as the page. Shares
//                 eorzea's layout; headings are cold-white Song, not gold Cinzel
//
// Pages bind to token names, never to a literal colour, so flipping
// `Settings.uiStyle` repaints the whole shell.
QtObject {
    readonly property bool dark: typeof App === "undefined" ? true : App.dark

    // The style on screen: "classic", "eorzea" or "harendotes". Defaults to
    // classic unless the setting explicitly names another style, so the UI
    // still renders when the property is missing.
    readonly property string uiStyle: {
        // --mock-ui-style pins the style for a screenshot run without touching
        // the persisted desktop.ini.
        if (typeof ForceUiStyle !== "undefined" && ForceUiStyle !== "")
            return normalizeStyle(ForceUiStyle)
        if (typeof Settings === "undefined")
            return "classic"
        return normalizeStyle(Settings.uiStyle)
    }
    function normalizeStyle(value) {
        return value === "eorzea" || value === "harendotes" ? value : "classic"
    }
    // The gilded family: eorzea and harendotes share layout, fonts, radii and
    // motion; only their palettes differ (gilded() below).
    readonly property bool eorzea: uiStyle !== "classic"
    // Harendotes: navy-black / cool white grounds with a flame-orange accent.
    readonly property bool harendotes: uiStyle === "harendotes"

    // ----------------------------------------------------------- 动效 --
    // One vocabulary for every animation in the shell (docs/ui-design.md 动效).
    // `motion` is the master switch: ReduceMotion (screenshot runs, Windows
    // "显示动画 = 关"; set in main.cpp) zeroes every motion* value below, so each
    // animation lands on its end state in the same frame.
    readonly property bool motion: typeof ReduceMotion === "undefined" ? true : !ReduceMotion
    readonly property int motionFast: motion ? 120 : 0
    readonly property int motionMedium: motion ? 220 : 0
    readonly property int motionSlow: motion ? 600 : 0
    // workbench.css --dur: micro-interactions and the classic row / panel entrances.
    readonly property int motionControl: motion ? 200 : 0
    readonly property int motionEnter: motion ? 200 : 0
    // wbSlide: the classic page slide.
    readonly property int motionPage: motion ? 300 : 0
    // Entrance steps: rows 22 ms apart (12 steps), panels 30 ms apart (5 steps).
    readonly property int motionRowStagger: motion ? 22 : 0
    readonly property int motionPanelStagger: motion ? 30 : 0
    // 完成趋势: height change, merge (日 -> 周 / 月), the regrow after a merge
    // and the split (周 / 月 -> 日), as in the prototype's switchTrend().
    readonly property int motionTrendHeight: motion ? 300 : 0
    readonly property int motionTrendMerge: motion ? 380 : 0
    readonly property int motionTrendRegrow: motion ? 300 : 0
    readonly property int motionTrendRegrowStagger: motion ? 22 : 0
    readonly property int motionTrendSplit: motion ? 440 : 0
    readonly property int motionTrendSplitStagger: motion ? 6 : 0
    // The circular 深色 / 浅色 reveal.
    readonly property int motionReveal: motion ? 520 : 0

    // Easing.Bezier curves (x1, y1, x2, y2, 1, 1). curveStandard is
    // workbench.css --ease, cubic-bezier(.2,.7,.2,1).
    readonly property var curveStandard: [0.2, 0.7, 0.2, 1.0, 1.0, 1.0]
    readonly property var curveMerge: [0.4, 0.0, 0.2, 1.0, 1.0, 1.0]
    readonly property var curveReveal: [0.05, 0.75, 0.2, 1.0, 1.0, 1.0]

    // ----------------------------------------------------------- 字体 --
    // Classic headings use the application (body) font; an empty family name
    // would make Qt fall back to a serif face on Windows.
    readonly property string bodyFamily: Qt.application.font.family
    // Harendotes sets headings and big figures in Song (Noto Serif SC), Latin
    // included; eorzea keeps Cinzel for Latin and figures.
    readonly property string headingFamily: eorzea
        ? (harendotes ? "Noto Serif SC" : "Cinzel") : bodyFamily
    readonly property string headingFamilyCjk: eorzea ? "Noto Serif SC" : bodyFamily
    // The QML font value type has no `families` list, so a heading picks the
    // face by its own content: Cinzel covers Latin and figures, Noto Serif SC
    // the Chinese (main.cpp registers substitutions for machines without it).
    //
    // IBM Plex Mono (bundled, SIL OFL 1.1) is workbench.css's --font-mono; the
    // classic style sets every figure in it at 600, both the big numbers eorzea
    // draws in Cinzel (numFamily) and the tabular-nums texts eorzea leaves in
    // the body face (figureFamily + figureWeight()).
    readonly property string plexMonoFamily: "IBM Plex Mono"
    readonly property string numFamily: eorzea ? headingFamily : plexMonoFamily
    readonly property string figureFamily: eorzea ? bodyFamily : plexMonoFamily

    // Opcodes, hashes and file paths are read character by character, so they need a
    // fixed-pitch face. A single hard-coded family is not enough: on a machine without
    // it Qt silently falls back to the default proportional face and a 64-character
    // hash becomes unreadable. The QML font value type has no `families` fallback list
    // (see above), so the fallback is resolved here against the installed families.
    readonly property string monoFamily: {
        // The classic style prefers the bundled IBM Plex Mono; eorzea keeps Consolas.
        const fallbacks = ["Consolas", "Cascadia Mono", "Cascadia Code", "Courier New",
                           "DejaVu Sans Mono", "Liberation Mono", "monospace"]
        const candidates = eorzea ? fallbacks : [plexMonoFamily].concat(fallbacks)
        const installed = Qt.fontFamilies()
        for (let i = 0; i < candidates.length; ++i) {
            if (installed.indexOf(candidates[i]) >= 0)
                return candidates[i]
        }
        // Qt maps this to the platform's default fixed-pitch family.
        return "monospace"
    }

    function headingFamilyFor(text) {
        if (!eorzea)
            return bodyFamily
        return /[⺀-鿿豈-﫿＀-￯]/.test(String(text))
               ? headingFamilyCjk : headingFamily
    }

    // Weight of a figure: eorzea keeps the text's own bold flag, classic sets
    // every figure at 600 (workbench.css `[style*="tabular-nums"]`).
    function figureWeight(bold) {
        if (eorzea)
            return bold ? Font.Bold : Font.Normal
        return Font.DemiBold
    }

    // ------------------------------------------------------- 字号 --
    // workbench.css t1-t6 = 18 / 15 / 13 / 12.5 / 11.5 / 11. Pages keep writing the
    // eorzea size they were laid out with; the classic style maps it onto the six
    // steps (workbench.css "type-scale enforcement over inline sizes").
    // font.pixelSize is an int, so t4 / t5 land on 13 / 12. Eorzea passes through.
    //   <=11 -> 11 | 12 -> 12 | 13 -> 13 | 14-16 -> 15 | 18-22 -> 18
    //   24-28 -> 22 | 40 -> 32
    function fs(px) {
        if (eorzea)
            return px
        if (px <= 11)
            return 11
        if (px <= 13)
            return px
        if (px <= 16)
            return 15
        if (px <= 22)
            return 18
        if (px < 40)
            return 22
        return 32
    }

    // Page titles are t1 and dialog titles t2 in the classic style, whatever size
    // the eorzea layout gave them.
    readonly property int pageTitleSize: eorzea ? 28 : 18
    function dialogTitleSize(px) {
        return eorzea ? px : 15
    }

    // Eorzea dims secondary text with opacity; workbench.css puts those back to
    // opacity 1 and paints them in --color-text-2 instead.
    function dimOpacity(value) {
        return eorzea ? value : 1
    }
    function dimColor(value) {
        return eorzea ? value : textSecondary
    }

    // ------------------------------------------------------ 艾欧泽亚 --
    // A gilded-family value: eorzea (the game's own window palette) or
    // harendotes (navy and flame), each in dark and light. Pages still
    // write `eorzea ? gilded(...) : classic`; classic keeps workbench.css.
    function gilded(eorzeaDark, eorzeaLight, harendotesDark, harendotesLight) {
        if (harendotes)
            return dark ? harendotesDark : harendotesLight
        return dark ? eorzeaDark : eorzeaLight
    }

    // The game's gold is the small marker on the active tab, so gold / gold2 stay
    // saturated for markers and the sidebar title; gold3 is the frame line, which
    // the game draws in a warm neutral. In harendotes the "gold" family is the
    // flame: gold = 橙焰, gold2 = 亮焰 (dark) / 深焰 (light), gold3 = the blue-grey
    // divider; the real old gold is `oldGold`, a garnish only.
    // workbench.css maps the family onto its text and divider colours.
    readonly property color gold: eorzea
        ? gilded("#e3b94a", "#8a6a1f", "#e85018", "#c8420f") : textSecondary
    readonly property color gold2: eorzea
        ? gilded("#f0cf70", "#9e7a2e", "#ff7a3d", "#a83408") : textPrimary
    readonly property color gold3: eorzea
        ? gilded("#7a7260", "#a8946c", "#3897a0b8", "#33263961") : border
    readonly property color accent700: eorzea
        ? gilded("#f5e6b8", "#4a3612", "#ffb48c", "#7a2505") : (dark ? "#9dbcf8" : "#1e4a9a")
    // Window titles are pale text in the game's dark theme and dark brown in the
    // light one; harendotes sets them in its own text colour (cold white / ink).
    // 古金 · 仅点缀: the tail of the panel's top line and of the ring.
    readonly property color oldGold: eorzea
        ? gilded(gold, gold, "#c8a45a", "#8a6a24") : textSecondary
    readonly property color headingColor: eorzea
        ? gilded("#f6f1e4", "#3a2c17", textPrimary, textPrimary) : textPrimary
    readonly property color ruleAccent: eorzea ? gold2 : clear(border)
    readonly property color badgeForeground: eorzea
        ? gilded("#1c1c1c", "#fff8e8", "#0a0d16", "#ffffff") : "#ffffff"

    // --inset-bg / --inset-bg-2 / --inset-border
    readonly property color insetBackground: eorzea
        ? gilded("#40000000", "#1a4d3d26", "#4d05070d", "#0f263961")
        : (dark ? "#212936" : "#f3f7fe")
    readonly property color insetBackgroundStrong: eorzea
        ? gilded("#5c000000", "#2e4d3d26", "#7005070d", "#1f263961")
        : (dark ? "#2a3341" : fill)
    readonly property color insetBorder: eorzea ? border : clear(border)

    // .panel / --panel-bg: the game window plate, lighter at the top; the
    // archive's near-black card.
    readonly property color panelTop: eorzea
        ? gilded("#f5474747", "#f7e8dab4", "#f5141b2d", "#f7f8f9fc") : surface
    readonly property color panelBottom: eorzea
        ? gilded("#f82e2e2e", "#f9d4c296", "#f8111726", "#f9f3f5fa") : surface
    // --chrome-bg (title bar + sidebar)
    readonly property color chromeTop: eorzea
        ? gilded("#f8333333", "#fadccb9f", "#f80e1320", "#faeef1f7") : titlebarBackground
    readonly property color chromeBottom: eorzea
        ? gilded("#fb1a1a1a", "#fac9b585", "#fb0a0d16", "#fae4e8f1") : titlebarBackground
    // The two radial washes over --color-bg (`.app` and `.app[data-theme="light"]`
    // in mentor-recorder-ff14.dc.html), identical in both styles and painted by
    // Main.qml's backdropWash Canvas. Top: 1000x600 at (50%, -20%); bottom:
    // 600x400 at (100%, 100%). Harendotes: a navy glow above and a faint flame
    // below in the dark, plain cool white and navy in the light.
    readonly property color washTop: harendotes
        ? (dark ? Qt.rgba(38 / 255, 57 / 255, 97 / 255, 0.45)
                : Qt.rgba(1, 1, 1, 0.7))
        : (dark ? Qt.rgba(70 / 255, 85 / 255, 120 / 255, 0.35)
                : Qt.rgba(1, 250 / 255, 235 / 255, 0.7))
    readonly property color washBottom: harendotes
        ? (dark ? Qt.rgba(232 / 255, 80 / 255, 24 / 255, 0.10)
                : Qt.rgba(38 / 255, 57 / 255, 97 / 255, 0.10))
        : (dark ? Qt.rgba(140 / 255, 110 / 255, 50 / 255, 0.15)
                : Qt.rgba(160 / 255, 130 / 255, 70 / 255, 0.25))

    // The title bar's theme switch wears the other theme's --color-bg and text:
    // 深色 is a dark button on the light page, 浅色 a light one on the dark.
    readonly property color inverseBackground: eorzea
        ? gilded("#e4d5ae", "#2c2c2c", "#f3f5fa", "#0a0d16") : (dark ? "#f5f7fa" : "#141922")
    readonly property color inverseText: eorzea
        ? gilded("#3a2d1b", "#f2f2f2", "#141a2b", "#eef0f6") : (dark ? "#1c2430" : "#e8ecf2")

    // --bar-fill: linear-gradient(180deg, gold-2, dim gold). gold3 is a frame
    // line, so the bar keeps its own dim stop.
    readonly property color barFillStart: eorzea
        ? gilded("#a8863c", "#7d5f1c", "#9a330c", "#a83408") : accent
    readonly property color barFillEnd: eorzea
        ? gilded(gold2, gold2, "#ff7a3d", "#e85018") : accent
    // The progress ring stroke gradient (#f0dc9e -> #b08a3a).
    readonly property color ringStart: eorzea
        ? gilded("#f0dc9e", "#b08a3a", "#e8401a", "#c8380f") : accent
    readonly property color ringEnd: eorzea
        ? gilded("#b08a3a", "#6f5417", "#c8a45a", "#8a6a24") : accent
    // Harendotes runs the ring along its arc from ringTail to ringStart: yellow
    // flame to deep flame, both opaque and far apart in hue and lightness. A translucent or greyish tail mixes with the track
    // into mud, so the whole run stays in saturated warm colours.
    readonly property color ringTail: eorzea
        ? gilded(ringEnd, ringEnd, "#ffd84a", "#f7bc1f") : accent
    readonly property color ringTrack: eorzea
        ? gilded("#59000000", "#264d3d26", "#59000000", "#1f263961") : fill

    // btn-primary. Eorzea: the game's button, a plate darker than the window
    // with a pale 1 px frame and pale text (dark grey / dark brown). Harendotes:
    // the archive's gold, linear-gradient(gold-2, gold, dim gold) with ink text.
    readonly property color buttonPrimaryTop: eorzea
        ? gilded("#525252", "#65503a", "#f2601f", "#e85018") : accent
    readonly property color buttonPrimaryMid: eorzea
        ? gilded("#3a3a3a", "#4c3a25", "#e85018", "#c8420f") : accent
    readonly property color buttonPrimaryBottom: eorzea
        ? gilded("#242424", "#332514", "#c8420f", "#a83408") : accent
    readonly property color buttonPrimaryText: eorzea
        ? gilded("#f4f4f4", "#f3e9d2", "#ffffff", "#ffffff") : "#ffffff"
    readonly property color buttonPrimaryBorder: eorzea
        ? gilded("#8c8c8c", "#c8b58c", "#ff7a3d", "#a83408") : accent
    // .btn (secondary): a plate a step lighter than the window, framed in gold3.
    // Eorzea labels it in the window's text colour, harendotes in gold.
    readonly property color buttonBorder: eorzea
        ? gilded(gold3, gold3, "#9a330c", "#e85018") : neutral300
    readonly property color buttonText: eorzea
        ? gilded(textPrimary, textPrimary, "#ff7a3d", "#c8420f") : textPrimary

    // The chosen tab. Eorzea: the game's plate darker than the window with pale
    // text; harendotes: the archive's dark card with gold text (light: a gold
    // plate). Classic keeps its accent chip (components/SegmentedControl.qml).
    readonly property color tabActiveTop: eorzea
        ? gilded("#2c2c2c", "#5c4830", "#f2601f", "#d94c12") : accent
    readonly property color tabActiveBottom: eorzea
        ? gilded("#141414", "#3a2b19", "#d8460f", "#c8420f") : accent
    readonly property color tabActiveBorder: eorzea
        ? gilded("#6e6e6e", "#8c7554", "#ff7a3d", "#a83408") : border
    readonly property color tabActiveText: eorzea
        ? gilded("#f4f4f4", "#f3e9d2", "#ffffff", "#ffffff") : accent

    // The title bar shares --chrome-bg with the sidebar in every style. Its
    // brand is gold in eorzea and the plain text colour elsewhere; harendotes
    // marks the bar's lower edge with the flame -> old gold line (Main.qml).
    readonly property color titlebarTop: chromeTop
    readonly property color titlebarBottom: chromeBottom
    readonly property color titlebarBrand: eorzea
        ? gilded(gold2, gold2, textPrimary, textPrimary) : textPrimary
    readonly property color titlebarText: textPrimary
    readonly property color titlebarTextSecondary: textSecondary
    readonly property color titlebarMark: gold
    readonly property color titlebarFill: fill

    // .navi[data-active] / a chosen detail tab: the accent fading out to the right.
    readonly property color navActiveStart: eorzea
        ? Qt.rgba(accent.r, accent.g, accent.b, 0.28) : accentMuted
    readonly property color navActiveEnd: eorzea
        ? Qt.rgba(accent.r, accent.g, accent.b, 0.04) : clear(accentMuted)
    // .sw: the track tint and the knob gradient of a switch that is on.
    readonly property color switchTrackOn: eorzea
        ? Qt.rgba(accent.r, accent.g, accent.b, 0.22) : accent
    readonly property color switchKnobTop: eorzea
        ? gilded("#f0dc9e", "#f0dc9e", "#ff9a66", "#f2601f") : "#ffffff"
    readonly property color switchKnobBottom: eorzea
        ? gilded("#c9a24a", "#c9a24a", "#e85018", "#c8420f") : "#ffffff"

    // ------------------------------------------------------ 基础色板 --
    readonly property color desktopBackdropStart: eorzea
        ? (harendotes ? "#111726" : "#1e3a6e") : "#5b6270"
    readonly property color desktopBackdropEnd: eorzea
        ? (harendotes ? "#0a0d16" : "#0a1630") : "#2c2f36"
    readonly property color shellBorder: eorzea ? gold3 : borderStrong
    // --chrome-bg is --color-surface in the classic style.
    readonly property color titlebarBackground: eorzea
        ? gilded("#2a2a2a", "#d6c59c", "#0e1320", "#eef1f7") : surface
    readonly property color sidebarBackground: titlebarBackground
    // .status-panel: the sidebar status plate sits on the chrome, not on a
    // panel, so it gets its own tint and border instead of the panel gradient
    // (ff14.css rgba(255,235,190,.05) / workbench.css --color-bg + 1 px divider).
    readonly property color statusPanelBackground: eorzea
        ? gilded("#0dffffff", "#1a4d3d26", "#263961", "#ffffff") : contentBackground
    readonly property color statusPanelBorder: eorzea
        ? gold3 : border
    // --color-bg
    readonly property color contentBackground: eorzea
        ? gilded("#222222", "#c6b387", "#0a0d16", "#e4e8f1") : (dark ? "#141922" : "#f5f7fa")
    readonly property color windowBackground: contentBackground
    // Classic values are workbench.css (light / dark) one for one: --color-surface,
    // --color-fill, --color-fill-2, --color-divider, --color-neutral-300,
    // --color-text / -2 / -3, --color-accent / -100 / -600.
    readonly property color surface: eorzea
        ? gilded("#3a3a3a", "#e3d4ad", "#111726", "#f3f5fa") : (dark ? "#1b212c" : "#ffffff")
    readonly property color surfaceRaised: eorzea
        ? gilded("#4a4a4a", "#ede1bf", "#182034", "#ffffff") : surface
    readonly property color surfaceMuted: eorzea
        ? gilded("#444444", "#e9dcb8", "#182034", "#ebeef5") : fill
    // Translucent white over the dark slate, translucent brown over parchment;
    // the archive tints with its gold.
    readonly property color fill: eorzea
        ? gilded("#14ffffff", "#144d3d26", "#1497a0b8", "#12263961") : (dark ? "#252d3a" : "#eceff3")
    readonly property color fillStrong: eorzea
        ? gilded("#24ffffff", "#244d3d26", "#2497a0b8", "#1f263961") : (dark ? "#2f384a" : "#e2e6ec")
    readonly property color border: eorzea
        ? gilded("#3dffffff", "#4d4d3d26", "#3897a0b8", "#33263961") : (dark ? "#2b3342" : "#e4e8ee")
    readonly property color borderStrong: eorzea ? gold3 : neutral300
    readonly property color textPrimary: eorzea
        ? gilded("#f2f2f2", "#3a2d1b", "#eef0f6", "#141a2b") : (dark ? "#e8ecf2" : "#1c2430")
    readonly property color textSecondary: eorzea
        ? gilded("#a6f2f2f2", "#a83a2d1b", "#97a0b8", "#4f5a75") : (dark ? "#a4adbb" : "#5d6675")
    readonly property color textMuted: eorzea
        ? gilded("#78f2f2f2", "#7a3a2d1b", "#6b748c", "#7a849c") : (dark ? "#7b8494" : "#8a93a1")
    readonly property color accent: eorzea
        ? gilded("#e3b94a", "#8a6a1f", "#e85018", "#c8420f") : (dark ? "#5b8ff0" : "#2f6bd8")
    // --color-accent-100 (dark: rgba(91,143,240,.18)).
    readonly property color accentMuted: eorzea
        ? gilded("#29e3b94a", "#248a6a1f", "#29e85018", "#1fc8420f") : (dark ? "#2e5b8ff0" : "#e8effc")
    // --color-accent-600: the hover shade of a primary button.
    readonly property color accentStrong: eorzea
        ? gilded("#f0cf70", "#6f5417", "#ff7a3d", "#a83408") : (dark ? "#7aa4f5" : "#2559b8")
    readonly property color green: eorzea
        ? gilded("#6fcf7a", "#3f8f4a", "#6fbf8a", "#35804e") : "#31a24c"
    readonly property color orange: eorzea
        ? gilded("#e8944a", "#c2691f", "#c8a020", "#8f7010") : "#dfa937"
    readonly property color yellow: eorzea
        ? gilded("#f2d55c", "#b89a1a", "#c8a45a", "#8a6a24") : "#e9b949"
    // The archive's --red / --red2 and --blue2 (Egyptian blue).
    readonly property color red: eorzea
        ? gilded("#e0574f", "#b83a32", "#e74c3c", "#b8322a") : "#d92b2b"
    readonly property color purple: eorzea
        ? gilded("#b07ad9", "#7b4fa8", "#a98bd6", "#7b4fa8") : "#7b5cd6"
    readonly property color teal: eorzea
        ? gilded("#6fc3d6", "#2f8ea3", "#1ab8cc", "#0f8d9e") : "#2b8fb8"
    readonly property color blue: eorzea
        ? gilded("#6ea0e0", "#3b6fb5", "#5fa8d3", "#2f6f9e") : accent
    // Tinted plates behind a coloured label (workbench.css --color-*-bg / -fg). The
    // eorzea tags are framed instead, so the eorzea values only mirror the palette.
    readonly property color greenBackground: eorzea
        ? Qt.rgba(green.r, green.g, green.b, 0.18) : (dark ? "#2e31a24c" : "#e9f7ee")
    readonly property color orangeBackground: eorzea
        ? Qt.rgba(orange.r, orange.g, orange.b, 0.16) : (dark ? "#29dfa937" : "#fdf6e7")
    // Orange text on a light plate needs the darker --color-orange-fg.
    readonly property color orangeForeground: eorzea
        ? orange : (dark ? "#e6c26a" : "#9a7017")
    // Warning text. #dfa937 is a fill colour; as text on white it is too faint.
    readonly property color orangeText: eorzea ? orange : orangeForeground
    readonly property color redBackground: eorzea
        ? Qt.rgba(red.r, red.g, red.b, 0.18) : (dark ? "#2ed92b2b" : "#fdecec")
    // --color-neutral-100 ... -800 (ff14.css / workbench.css).
    readonly property color neutral100: eorzea
        ? gilded("#14ffffff", "#144d3d26", "#1497a0b8", "#12263961") : (dark ? "#252d3a" : "#eceff3")
    readonly property color neutral300: eorzea
        ? gilded("#5a5a5a", "#b9a67c", "#2a3450", "#c9d0de") : (dark ? "#3a4454" : "#d5dae2")
    readonly property color neutral400: eorzea
        ? gilded("#767676", "#9c885e", "#47526f", "#9aa4ba") : (dark ? "#80556072" : "#b6bdc8")
    readonly property color neutral500: eorzea
        ? gilded("#929292", "#7d6a46", "#6b748c", "#6b7690") : (dark ? "#7b8494" : "#8a93a1")
    readonly property color neutral600: eorzea
        ? gilded("#b4b4b4", "#5e4d32", "#97a0b8", "#4f5a75") : (dark ? "#a4adbb" : "#5d6675")
    readonly property color neutral800: eorzea
        ? gilded("#dcdcdc", "#3a2d1b", "#d5d9e6", "#1f2740") : (dark ? "#d5dae2" : "#2b3442")
    // .dialog-backdrop: rgba(28,36,48,.4) in the classic style.
    readonly property color blackScrim: eorzea
        ? (harendotes ? "#a605070d" : "#99101010") : "#661c2430"
    // A modal dialog's backdrop. Eorzea keeps Qt Basic's own
    // `Color.transparent(control.palette.shadow, 0.5)` (alpha int(255 * .5));
    // classic uses workbench's .dialog-backdrop.
    function modalScrim(shadow) {
        return eorzea ? Qt.rgba(shadow.r, shadow.g, shadow.b, 127 / 255) : blackScrim
    }

    // ------------------------------------------------------- 圆角 --
    // --radius-xxs / -xs / -sm / -md / -lg
    readonly property int radiusXxs: eorzea ? 1 : 3
    readonly property int radiusXs: eorzea ? 2 : 4
    readonly property int radiusS: eorzea ? 3 : 6
    readonly property int radiusM: eorzea ? 4 : 8
    readonly property int radiusL: eorzea ? 6 : 10

    /// Width kept free down the right edge of a scrolling page so the scroll bar has
    /// somewhere of its own to be. Without it the bar is drawn over the cards.
    readonly property int scrollGutter: 16
    readonly property int pagePadding: 28

    // The same colour at zero alpha. A `Behavior on color` must rest on this, never
    // on "transparent": that is #00000000, and ColorAnimation interpolates straight
    // RGBA, so a fade from it to a light fill passes through grey.
    function clear(value) {
        return Qt.rgba(value.r, value.g, value.b, 0)
    }

    function token(name) {
        switch (name) {
        case "accent":
            return accent
        case "green":
            return green
        case "orange":
            return orange
        case "yellow":
            return yellow
        case "red":
            return red
        case "purple":
            return purple
        case "teal":
            return teal
        case "blue":
            return blue
        case "neutral500":
            return neutral500
        case "neutral400":
            return neutral400
        default:
            return textPrimary
        }
    }

    // .tag: eorzea draws a 1 px currentColor frame over a translucent plate;
    // classic is workbench.css's tinted pill (.tag-accent / -outline / -ink /
    // -blue / -neutral).
    function tagBackground(variant) {
        if (eorzea)
            return gilded("#2e000000", "#59ffffff", "#33000000", "#99ffffff")
        switch (variant) {
        case "blue":
            return accentMuted
        case "accent":
        case "outline":
            return orangeBackground
        case "ink":
            return greenBackground
        default:
            return fill
        }
    }

    function tagForeground(variant) {
        if (eorzea) {
            switch (variant) {
            case "blue":
                return blue
            case "accent":
                return orange
            case "ink":
                return green
            case "outline":
                return yellow
            default:
                return neutral600
            }
        }
        switch (variant) {
        case "blue":
            return accent
        case "accent":
        case "outline":
            return orangeForeground
        case "ink":
            return green
        default:
            return textSecondary
        }
    }

    function tagBorder(variant) {
        if (eorzea)
            return variant === "neutral" || variant === undefined || variant === ""
                   ? neutral400 : tagForeground(variant)
        return clear(tagBackground(variant))
    }

    // 心得 mood: good=顺利 / ok=一般 / bad=糟心 (design's MOOD table).
    function moodLabel(mood) {
        switch (mood) {
        case "good":
            return qsTr("顺利")
        case "ok":
            return qsTr("一般")
        case "bad":
            return qsTr("糟心")
        default:
            return qsTr("未记录")
        }
    }

    function moodVariant(mood) {
        switch (mood) {
        case "good":
            return "ink"
        case "bad":
            return "accent"
        default:
            return "blue"
        }
    }
}
