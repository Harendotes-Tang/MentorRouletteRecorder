import QtQuick
import QtQuick.Controls
import MentorRecorder
import "Lucide.js" as Lucide

// Shared button for `.btn` / `.btn-primary` / `.btn-secondary` / `.btn-ghost`.
// Classic (workbench.css): secondary is a surface button with a neutral-300
// frame, primary a solid accent with 600 text, ghost accent text on nothing;
// disabled is a fill plate with muted text rather than a faded button.
Button {
    id: control

    property string variant: "secondary"
    // The small 11 px variant used in the title bar and inside panels.
    property bool compact: false
    // A Lucide icon (components/Lucide.js) drawn before the label in the label's
    // colour; empty for a text-only button. `iconAfterText` puts it after.
    property string iconName: ""
    property bool iconAfterText: false
    readonly property int iconSize: compact ? 12 : 14
    readonly property bool hasIcon: iconName.length > 0 && Lucide.has(iconName)
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1

    readonly property bool primary: variant === "primary"
    readonly property bool ghost: variant === "ghost"
    // The other theme's page colours (Theme.inverseBackground/Text): the title
    // bar's 深色 / 浅色 switch.
    readonly property bool inverse: variant === "inverse"
    // activeFocus, not visualFocus: a marker is briefly disabled while Space
    // waits for its reply, and visualFocus drops during that transition.
    readonly property bool keyboardFocusVisible: control.activeFocus
    // Hover fill. At rest a ghost button holds this colour at zero alpha so the
    // hover fade only changes alpha.
    readonly property color hoverColor: Theme.eorzea || !control.ghost
                                        ? Theme.fill : Theme.accentMuted

    focusPolicy: Qt.TabFocus
    hoverEnabled: true
    implicitHeight: compact ? 24 : (Theme.eorzea ? 32 : 30)
    implicitWidth: Math.max(compact ? 44 : 80, contentItem.implicitWidth + (compact ? 20 : 28))
    padding: 0
    opacity: control.enabled || !Theme.eorzea ? 1.0 : 0.4
    // `.btn:active:not(:disabled)`: scale .96; reduced motion keeps the button
    // still, matching prefers-reduced-motion.
    scale: Theme.motion && control.down ? 0.96 : 1.0
    Behavior on scale {
        NumberAnimation {
            duration: Theme.motionControl
            easing.type: Easing.Bezier
            easing.bezierCurve: Theme.curveStandard
        }
    }
    Behavior on opacity { NumberAnimation { duration: Theme.motionFast } }

    background: Rectangle {
        readonly property bool active: control.down || control.hovered

        radius: Theme.eorzea ? Theme.radiusM : Theme.radiusS
        border.width: control.keyboardFocusVisible
                      ? 3
                      : (control.inverse
                         ? 0
                         : (Theme.eorzea
                            ? (control.ghost ? 0 : 1)
                            : (control.ghost || !control.enabled ? 0 : 1)))
        border.color: control.keyboardFocusVisible
                      ? (Theme.dark ? "#ffffff" : Theme.accent700)
                      : (Theme.eorzea
                         ? (control.primary ? Theme.gold2 : Theme.gold3)
                         : (control.primary
                            ? (active ? Theme.accentStrong : Theme.accent)
                            : Theme.neutral300))
        color: control.inverse ? inverseColor() : (Theme.eorzea ? eorzeaColor() : classicColor())
        Behavior on color { ColorAnimation { duration: Theme.motionControl } }

        function inverseColor() {
            if (!control.enabled)
                return Theme.fill
            if (!active)
                return Theme.inverseBackground
            // A light plate darkens a touch under the pointer, a dark one lightens.
            return Theme.dark ? Qt.darker(Theme.inverseBackground, 1.06)
                              : Qt.lighter(Theme.inverseBackground, 1.35)
        }

        function eorzeaColor() {
            if (control.primary)
                return Theme.clear(Theme.accent)
            if (!control.ghost)
                return Theme.surfaceRaised
            return control.down ? Theme.fillStrong
                                : (control.hovered ? Theme.fill : Theme.clear(Theme.fill))
        }

        function classicColor() {
            if (control.ghost)
                return control.enabled && active ? control.hoverColor
                                                 : Theme.clear(control.hoverColor)
            if (!control.enabled)
                return Theme.fill
            if (control.primary)
                return active ? Theme.accentStrong : Theme.accent
            return active ? Theme.fill : Theme.surface
        }

        // btn-primary: linear-gradient(180deg,#f0dc9e,#c9a24a 55%,#a98330)
        Rectangle {
            anchors.fill: parent
            anchors.margins: 1
            radius: Math.max(0, parent.radius - 1)
            visible: Theme.eorzea && control.primary
            gradient: Gradient {
                GradientStop { position: 0.0; color: Theme.buttonPrimaryTop }
                GradientStop { position: 0.55; color: Theme.buttonPrimaryMid }
                GradientStop { position: 1.0; color: Theme.buttonPrimaryBottom }
            }
        }

        // The .btn sheen: linear-gradient(rgba(255,255,255,.06), rgba(0,0,0,.18))
        Rectangle {
            anchors.fill: parent
            anchors.margins: 1
            radius: Math.max(0, parent.radius - 1)
            visible: Theme.eorzea && !control.primary && !control.ghost && !control.inverse
            gradient: Gradient {
                GradientStop { position: 0.0; color: "#0fffffff" }
                GradientStop { position: 1.0; color: "#2e000000" }
            }
        }

        Rectangle {
            anchors.fill: parent
            radius: parent.radius
            visible: Theme.eorzea && opacity > 0
            opacity: control.hovered ? 1 : 0
            Behavior on opacity { NumberAnimation { duration: Theme.motionFast } }
            color: "#1affffff"
        }

        // The custom background replaces Qt Basic's default focus frame; this
        // restores a keyboard-only, high-contrast focus ring for every AppButton.
        Rectangle {
            anchors.fill: parent
            anchors.margins: -3
            radius: parent.radius + 3
            visible: control.keyboardFocusVisible
            color: Theme.dark ? "#18ffffff" : "#12000000"
            border.width: 3
            border.color: Theme.dark ? "#ffffff" : Theme.accent700
        }
    }

    // The label colour; the icon is drawn in the same one.
    readonly property color foreground: {
        if (!control.enabled)
            return Theme.textMuted
        if (control.inverse)
            return Theme.inverseText
        if (Theme.eorzea)
            return control.primary
                   ? Theme.buttonPrimaryText
                   : (control.ghost ? Theme.accentStrong
                                    : (Theme.dark ? Theme.gold2 : Theme.accentStrong))
        if (control.primary)
            return "#ffffff"
        return control.ghost ? Theme.accent : Theme.textPrimary
    }

    // Laid out by hand rather than in a Row: a Row's implicit width follows its
    // children's *widths*, while the label's width must follow the button's - a
    // binding loop that collapsed the label. Here implicit size reads only
    // implicit sizes.
    contentItem: Item {
        id: content

        readonly property real iconSpan: icon.visible ? icon.width : 0
        readonly property real gap: icon.visible && label.visible ? 6 : 0
        // The label, not the icon, gives way when the button is narrower than
        // its content.
        readonly property real labelWidth: Math.min(label.implicitWidth,
                                                    Math.max(0, width - iconSpan - gap))
        readonly property real contentWidth: iconSpan + gap + labelWidth
        // Not `left`: that is Item's own anchor line.
        readonly property real contentLeft: Math.max(0, (width - contentWidth) / 2)

        implicitWidth: iconSpan + gap + label.implicitWidth
        implicitHeight: Math.max(icon.visible ? icon.height : 0, label.implicitHeight)

        Image {
            id: icon
            objectName: "buttonIcon"
            visible: control.hasIcon
            width: control.iconSize
            height: control.iconSize
            x: control.iconAfterText ? content.contentLeft + content.labelWidth + content.gap
                                     : content.contentLeft
            y: Math.round((parent.height - height) / 2)
            // Rendered at the screen's pixel density so the strokes stay crisp.
            sourceSize: Qt.size(Math.round(control.iconSize * control.pixelRatio),
                                Math.round(control.iconSize * control.pixelRatio))
            source: control.hasIcon ? Lucide.source(control.iconName, control.foreground) : ""
            fillMode: Image.PreserveAspectFit
            smooth: true
        }

        Text {
            id: label
            objectName: "buttonLabel"
            visible: text.length > 0
            width: content.labelWidth
            height: parent.height
            x: control.iconAfterText ? content.contentLeft
                                     : content.contentLeft + content.iconSpan + content.gap
            text: control.text
            color: control.foreground
            horizontalAlignment: Text.AlignHCenter
            verticalAlignment: Text.AlignVCenter
            font.pixelSize: Theme.fs(control.compact ? 11 : 13)
            font.weight: Theme.eorzea ? Font.Bold : (control.primary ? Font.DemiBold : Font.Normal)
            font.letterSpacing: Theme.eorzea ? 0.3 : 0
            elide: Text.ElideRight
        }
    }
}
