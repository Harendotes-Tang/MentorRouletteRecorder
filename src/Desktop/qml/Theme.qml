pragma Singleton

import QtQuick

// Single source of truth for the palette. Two visual styles share one token
// vocabulary (docs/ui-design.md):
//
//   * "classic" - DOC/表单提交后设计/workbench.css (default: light work surface,
//                 #2f6bd8 accent, six-step type scale, IBM Plex Mono figures)
//   * "eorzea"  - DOC/表单提交后设计/ff14.css      (gilded dark slate)
//
// Pages bind to token names, never to a literal colour, so flipping
// `Settings.uiStyle` repaints the whole shell.
QtObject {
    readonly property bool dark: typeof App === "undefined" ? true : App.dark

    // Defaults to classic unless the setting explicitly says "eorzea", so the
    // UI still renders when the property is missing.
    readonly property bool eorzea: {
        // --mock-ui-style pins the style for a screenshot run without touching
        // the persisted desktop.ini.
        if (typeof ForceUiStyle !== "undefined" && ForceUiStyle !== "")
            return ForceUiStyle === "eorzea"
        if (typeof Settings === "undefined")
            return false
        const style = Settings.uiStyle
        if (style === undefined || style === null || style === "")
            return false
        return style === "eorzea"
    }

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
    readonly property string headingFamily: eorzea ? "Cinzel" : bodyFamily
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
    // workbench.css maps the gold family onto its text and divider colours.
    readonly property color gold: eorzea ? (dark ? "#cfae62" : "#8a6a1f") : textSecondary
    readonly property color gold2: eorzea ? (dark ? "#e9d18f" : "#a8853a") : textPrimary
    readonly property color gold3: eorzea ? (dark ? "#8a6f35" : "#c9ab6a") : border
    readonly property color accent700: eorzea
        ? (dark ? "#f3e2ad" : "#553f10") : (dark ? "#9dbcf8" : "#1e4a9a")
    readonly property color headingColor: eorzea ? (dark ? gold2 : accent700) : textPrimary
    readonly property color ruleAccent: eorzea ? gold2 : clear(border)
    readonly property color badgeForeground: eorzea ? (dark ? "#0d1017" : "#fff8e8") : "#ffffff"

    // --inset-bg / --inset-bg-2 / --inset-border
    readonly property color insetBackground: eorzea
        ? (dark ? "#38000000" : "#14785c28")
        : (dark ? "#212936" : "#f3f7fe")
    readonly property color insetBackgroundStrong: eorzea
        ? (dark ? "#52000000" : "#24785c28")
        : (dark ? "#2a3341" : fill)
    readonly property color insetBorder: eorzea ? border : clear(border)

    // .panel / --panel-bg
    readonly property color panelTop: eorzea
        ? (dark ? "#eb262c3a" : "#f2faf5e8") : surface
    readonly property color panelBottom: eorzea
        ? (dark ? "#f512161f" : "#f7ece2cc") : surface
    // --chrome-bg (title bar + sidebar)
    readonly property color chromeTop: eorzea
        ? (dark ? "#f21e2430" : "#fae8dec6") : titlebarBackground
    readonly property color chromeBottom: eorzea
        ? (dark ? "#fa0e1119" : "#fad8cbac") : titlebarBackground
    // The two radial washes over --color-bg (`.app` and `.app[data-theme="light"]`
    // in mentor-recorder-ff14.dc.html), identical in both styles and painted by
    // Main.qml's backdropWash Canvas. Top: 1000x600 at (50%, -20%); bottom:
    // 600x400 at (100%, 100%).
    readonly property color washTop: dark ? Qt.rgba(70 / 255, 85 / 255, 120 / 255, 0.35)
                                          : Qt.rgba(1, 250 / 255, 235 / 255, 0.7)
    readonly property color washBottom: dark ? Qt.rgba(140 / 255, 110 / 255, 50 / 255, 0.15)
                                             : Qt.rgba(160 / 255, 130 / 255, 70 / 255, 0.25)

    // The title bar's theme switch wears the other theme's --color-bg and text:
    // 深色 is a dark button on the light page, 浅色 a light one on the dark.
    readonly property color inverseBackground: eorzea ? (dark ? "#d9cdb2" : "#0d1017")
                                                      : (dark ? "#f5f7fa" : "#141922")
    readonly property color inverseText: eorzea ? (dark ? "#2b2418" : "#e9d18f")
                                                : (dark ? "#1c2430" : "#e8ecf2")

    // --bar-fill: linear-gradient(180deg, gold-2, gold-3)
    readonly property color barFillStart: eorzea ? gold3 : accent
    readonly property color barFillEnd: eorzea ? gold2 : accent
    // The progress ring stroke gradient (#f0dc9e -> #b08a3a).
    readonly property color ringStart: eorzea ? (dark ? "#f0dc9e" : "#b08a3a") : accent
    readonly property color ringEnd: eorzea ? (dark ? "#b08a3a" : "#6f5417") : accent
    readonly property color ringTrack: eorzea
        ? (dark ? "#59000000" : "#26785c28") : fill

    // btn-primary: linear-gradient(180deg,#f0dc9e,#c9a24a 55%,#a98330)
    readonly property color buttonPrimaryTop: eorzea ? "#f0dc9e" : accent
    readonly property color buttonPrimaryMid: eorzea ? "#c9a24a" : accent
    readonly property color buttonPrimaryBottom: eorzea ? "#a98330" : accent
    readonly property color buttonPrimaryText: eorzea ? "#2a2109" : "#ffffff"

    // ------------------------------------------------------ 基础色板 --
    readonly property color desktopBackdropStart: eorzea ? "#111725" : "#5b6270"
    readonly property color desktopBackdropEnd: eorzea ? "#07090e" : "#2c2f36"
    readonly property color shellBorder: eorzea ? gold3 : borderStrong
    // --chrome-bg is --color-surface in the classic style.
    readonly property color titlebarBackground: eorzea
        ? (dark ? "#1e2430" : "#e8dec6") : surface
    readonly property color sidebarBackground: titlebarBackground
    // .status-panel: the sidebar status plate sits on the chrome, not on a
    // panel, so it gets its own tint and border instead of the panel gradient
    // (ff14.css rgba(255,235,190,.05) / workbench.css --color-bg + 1 px divider).
    readonly property color statusPanelBackground: eorzea
        ? (dark ? "#0dffebbe" : "#1a785c28") : contentBackground
    readonly property color statusPanelBorder: eorzea
        ? gold3 : border
    // --color-bg
    readonly property color contentBackground: eorzea
        ? (dark ? "#0d1017" : "#d9cdb2") : (dark ? "#141922" : "#f5f7fa")
    readonly property color windowBackground: contentBackground
    // Classic values are workbench.css (light / dark) one for one: --color-surface,
    // --color-fill, --color-fill-2, --color-divider, --color-neutral-300,
    // --color-text / -2 / -3, --color-accent / -100 / -600.
    readonly property color surface: eorzea
        ? (dark ? "#171c26" : "#efe6d2") : (dark ? "#1b212c" : "#ffffff")
    readonly property color surfaceRaised: eorzea
        ? (dark ? "#1f2532" : "#f6efe0") : surface
    readonly property color surfaceMuted: eorzea
        ? (dark ? "#1f2532" : "#f6efe0") : fill
    readonly property color fill: eorzea
        ? (dark ? "#1ac9aa6a" : "#1a785c28") : (dark ? "#252d3a" : "#eceff3")
    readonly property color fillStrong: eorzea
        ? (dark ? "#2ec9aa6a" : "#2e785c28") : (dark ? "#2f384a" : "#e2e6ec")
    readonly property color border: eorzea
        ? (dark ? "#38c9aa6a" : "#47785c28") : (dark ? "#2b3342" : "#e4e8ee")
    readonly property color borderStrong: eorzea ? gold3 : neutral300
    readonly property color textPrimary: eorzea
        ? (dark ? "#e8e1cf" : "#2b2418") : (dark ? "#e8ecf2" : "#1c2430")
    readonly property color textSecondary: eorzea
        ? (dark ? "#9ed6c8a8" : "#9e40341e") : (dark ? "#a4adbb" : "#5d6675")
    readonly property color textMuted: eorzea
        ? (dark ? "#7ad6c8a8" : "#7a40341e") : (dark ? "#7b8494" : "#8a93a1")
    readonly property color accent: eorzea
        ? (dark ? "#cfae62" : "#8a6a1f") : (dark ? "#5b8ff0" : "#2f6bd8")
    // --color-accent-100 (dark: rgba(91,143,240,.18)).
    readonly property color accentMuted: eorzea
        ? (dark ? "#29cfae62" : "#248a6a1f") : (dark ? "#2e5b8ff0" : "#e8effc")
    // --color-accent-600: the hover shade of a primary button.
    readonly property color accentStrong: eorzea
        ? (dark ? "#e9d18f" : "#6f5417") : (dark ? "#7aa4f5" : "#2559b8")
    readonly property color green: eorzea
        ? (dark ? "#6fcf7a" : "#3f8f4a") : "#31a24c"
    readonly property color orange: eorzea ? (dark ? "#e8944a" : "#c2691f") : "#dfa937"
    readonly property color yellow: eorzea ? (dark ? "#f2d55c" : "#b89a1a") : "#e9b949"
    readonly property color red: eorzea
        ? (dark ? "#e0574f" : "#b83a32") : "#d92b2b"
    readonly property color purple: eorzea
        ? (dark ? "#b07ad9" : "#7b4fa8") : "#7b5cd6"
    readonly property color teal: eorzea ? (dark ? "#6fc3d6" : "#2f8ea3") : "#2b8fb8"
    readonly property color blue: eorzea ? (dark ? "#6ea0e0" : "#3b6fb5") : accent
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
        ? (dark ? "#1ac9aa6a" : "#1a785c28") : (dark ? "#252d3a" : "#eceff3")
    readonly property color neutral300: eorzea
        ? (dark ? "#39404d" : "#c9bc9d") : (dark ? "#3a4454" : "#d5dae2")
    readonly property color neutral400: eorzea
        ? (dark ? "#5a6272" : "#a89a7a") : (dark ? "#80556072" : "#b6bdc8")
    readonly property color neutral500: eorzea
        ? (dark ? "#7c8494" : "#7d7159") : (dark ? "#7b8494" : "#8a93a1")
    readonly property color neutral600: eorzea
        ? (dark ? "#a2a8b4" : "#5a5040") : (dark ? "#a4adbb" : "#5d6675")
    readonly property color neutral800: eorzea
        ? (dark ? "#d0d3da" : "#3a3226") : (dark ? "#d5dae2" : "#2b3442")
    // .dialog-backdrop: rgba(28,36,48,.4) in the classic style.
    readonly property color blackScrim: eorzea ? "#99050710" : "#661c2430"
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
            return dark ? "#2e000000" : "#59ffffff"
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
