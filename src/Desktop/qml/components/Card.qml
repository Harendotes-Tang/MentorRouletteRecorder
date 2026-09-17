import QtQuick
import QtQuick.Layouts
import MentorRecorder

// `.panel` of the prototype: a gilded slate plate in the eorzea style; in the
// classic one workbench's surface with a 1 px divider ring (--shadow-sm).
Rectangle {
    id: root

    property int padding: 16
    // Left/right and top/bottom separately; both default to `padding`. The
    // settings panels use them (workbench `.panel`: 8 px 24 px).
    property int horizontalPadding: padding
    property int verticalPadding: padding
    property alias spacing: body.spacing
    property color fillColor: Theme.surface
    property color borderColor: Theme.border
    property int borderWidth: 1
    // A flat card paints fillColor in both styles (no eorzea slate gradient);
    // the sidebar status plate uses it.
    property bool flat: false
    // Panels nested inside another panel drop the corner ticks.
    property bool decorated: true
    default property alias contentData: body.data

    radius: Theme.radiusM
    border.width: root.borderWidth
    border.color: root.borderColor
    implicitWidth: 240
    implicitHeight: body.implicitHeight + verticalPadding * 2

    // Classic wbPop: when the holding page becomes current the panel pops in
    // from .97 and transparent, staggered per sibling (the fifth and later share
    // the last step). Panels outside a page (sidebar plate, dialogs) never pop.
    property Item pageHost: null
    scale: popIn.scale
    opacity: popIn.opacity

    readonly property Entrance popIn: Entrance { fromScale: 0.97 }

    function findPageHost() {
        for (let item = root.parent; item; item = item.parent) {
            if (item.isPageHost === true)
                return item
        }
        return null
    }

    function siblingIndex() {
        if (!root.parent)
            return 0
        const siblings = root.parent.children
        let position = 0
        for (let index = 0; index < siblings.length; ++index) {
            if (siblings[index] === root)
                return position
            if (siblings[index].visible)
                ++position
        }
        return 0
    }

    Component.onCompleted: root.pageHost = root.findPageHost()
    onParentChanged: root.pageHost = root.findPageHost()

    Connections {
        target: root.pageHost
        enabled: root.pageHost !== null
        function onEntered() {
            root.popIn.play(Math.min(root.siblingIndex(), 4) * Theme.motionPanelStagger)
        }
    }

    gradient: Gradient {
        GradientStop {
            position: 0.0
            color: (Theme.eorzea && !root.flat) ? Theme.panelTop : root.fillColor
        }
        GradientStop {
            position: 1.0
            color: (Theme.eorzea && !root.flat) ? Theme.panelBottom : root.fillColor
        }
    }

    PanelDecoration {
        visible: Theme.eorzea && root.decorated
    }

    ColumnLayout {
        id: body
        anchors.fill: parent
        anchors.leftMargin: root.horizontalPadding
        anchors.rightMargin: root.horizontalPadding
        anchors.topMargin: root.verticalPadding
        anchors.bottomMargin: root.verticalPadding
        spacing: 12
        z: 1
    }
}
