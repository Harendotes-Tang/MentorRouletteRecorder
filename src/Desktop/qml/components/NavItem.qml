import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder
import "Lucide.js" as Lucide

// One entry of the sidebar navigation: `.navi` / `.navi[data-active]`.
// The first column carries the page's Lucide icon (`iconName`), or the
// prototype's two-digit number when a caller passes one; the settings page's
// sub-navigation (`.navi.set-navi`) uses neither and adds a `sub` line.
// Classic (workbench.css) draws the current page as an accent-100 plate with
// accent text at 600; eorzea as a gold gradient plate with a 2 px gold bar down
// its left edge.
Button {
    id: navItem

    property string number: ""
    property string iconName: ""
    property string label: ""
    readonly property bool hasIcon: iconName.length > 0 && Lucide.has(iconName)
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1
    property string sub: ""
    property bool current: false
    // The hover plate; at rest the background holds it at zero alpha.
    readonly property color hoverColor: Theme.fill

    focusPolicy: Qt.TabFocus
    hoverEnabled: true
    padding: 0
    implicitHeight: sub.length > 0
                    ? labelColumn.implicitHeight + 18
                    : (Theme.eorzea ? 34 : 40)

    // `.navi:hover` moves 2 px right, `.navi:active` scales to .97 (and drops
    // the hover shift). Both stay put under reduced motion.
    scale: Theme.motion && navItem.down ? 0.97 : 1.0
    transform: Translate {
        x: Theme.motion && navItem.hovered && !navItem.down ? 2 : 0
        Behavior on x {
            NumberAnimation {
                duration: Theme.motionControl
                easing.type: Easing.Bezier
                easing.bezierCurve: Theme.curveStandard
            }
        }
    }
    Behavior on scale {
        NumberAnimation {
            duration: Theme.motionControl
            easing.type: Easing.Bezier
            easing.bezierCurve: Theme.curveStandard
        }
    }

    background: Rectangle {
        radius: Theme.radiusS
        color: Theme.eorzea
               ? (navItem.current || !navItem.hovered
                  ? Theme.clear(Theme.fill) : Theme.fill)
               : (navItem.current
                  ? Theme.accentMuted
                  : (navItem.hovered ? navItem.hoverColor : Theme.clear(navItem.hoverColor)))
        Behavior on color { ColorAnimation { duration: Theme.motionControl } }

        // .navi[data-active] gradient plate
        Rectangle {
            anchors.fill: parent
            radius: parent.radius
            visible: Theme.eorzea && opacity > 0
            opacity: navItem.current ? 1 : 0
            Behavior on opacity { NumberAnimation { duration: Theme.motionMedium } }
            gradient: Gradient {
                orientation: Gradient.Horizontal
                GradientStop { position: 0.0; color: "#47cfae62" }
                GradientStop { position: 1.0; color: "#0acfae62" }
            }
        }

        // the 2 px gold bar with its soft glow
        Rectangle {
            anchors.left: parent.left
            anchors.top: parent.top
            anchors.bottom: parent.bottom
            anchors.topMargin: 4
            anchors.bottomMargin: 4
            width: 2
            visible: Theme.eorzea && navItem.current
            color: Theme.gold2

            Rectangle {
                anchors.centerIn: parent
                width: 6
                height: parent.height + 4
                color: Theme.gold
                opacity: 0.28
            }
        }
    }

    contentItem: RowLayout {
        anchors.fill: parent
        anchors.leftMargin: Theme.eorzea ? 10 : 12
        anchors.rightMargin: 12
        spacing: 10

        // Drawn in the label's colour, so the current page's icon is accent
        // (classic) or heading gold (eorzea) like its text.
        Image {
            id: icon
            objectName: "navIcon"
            visible: navItem.hasIcon
            Layout.preferredWidth: 16
            Layout.preferredHeight: 16
            Layout.alignment: Qt.AlignVCenter
            sourceSize: Qt.size(Math.round(16 * navItem.pixelRatio), Math.round(16 * navItem.pixelRatio))
            source: navItem.hasIcon ? Lucide.source(navItem.iconName, labelText.color) : ""
            fillMode: Image.PreserveAspectFit
            smooth: true
        }

        Text {
            objectName: "navNumber"
            visible: navItem.number.length > 0 && !navItem.hasIcon
            text: navItem.number
            // Classic: the number column is set in the mono face, t4, text-3.
            color: Theme.eorzea
                   ? (navItem.current ? Theme.headingColor
                                      : Theme.textSecondary)
                   : Theme.textMuted
            opacity: Theme.eorzea && !navItem.current ? 0.6 : 1.0
            font.family: Theme.eorzea ? Theme.headingFamily : Theme.plexMonoFamily
            font.pixelSize: Theme.eorzea ? 10 : 13
            font.weight: Theme.eorzea && navItem.current ? Font.Bold
                         : (Theme.eorzea ? Font.Normal : Font.Medium)
            font.features: ({ "tnum": 1 })
        }

        ColumnLayout {
            id: labelColumn

            Layout.fillWidth: true
            spacing: 1

            Text {
                id: labelText

                Layout.fillWidth: true
                text: navItem.label
                color: Theme.eorzea
                       ? (navItem.current ? Theme.headingColor
                                          : (navItem.hovered ? Theme.textPrimary
                                                             : Theme.textSecondary))
                       : (navItem.current ? Theme.accent
                                          : (navItem.hovered ? Theme.textPrimary : Theme.textSecondary))
                // Classic: t2 (15 px); 500 at rest, 600 on the current page.
                font.pixelSize: Theme.eorzea ? 13 : 15
                font.weight: Theme.eorzea
                             ? (navItem.current ? Font.Bold : Font.Normal)
                             : (navItem.current ? Font.DemiBold : Font.Medium)
                elide: Text.ElideRight
            }

            // `.set-navi-sub`: text-2 at rest; on the current entry it takes the
            // label's colour at .8.
            Text {
                Layout.fillWidth: true
                visible: navItem.sub.length > 0
                text: navItem.sub
                color: navItem.current ? labelText.color : Theme.textSecondary
                opacity: navItem.current ? 0.8 : 1.0
                font.pixelSize: 11
                font.weight: Font.Normal
                elide: Text.ElideRight
            }
        }
    }
}
