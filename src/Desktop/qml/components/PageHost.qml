import QtQuick
import MentorRecorder

// Hosts one page of the shell and animates it in when it becomes current:
//   * classic (workbench.css wbSlide): slides in from 12 px to the right while
//     fading in;
//   * eorzea: a short fade plus an 8 px rise, the same motion the dialogs use.
// `entered()` tells the page's panels and rows to play their own classic
// entrances (Card, HistoryPage); they find this host by `isPageHost`.
// Pages that are not current stay hidden; their bindings still evaluate.
Item {
    id: host

    property int index: -1
    readonly property bool active: typeof App !== "undefined" && App.currentPage === index
    readonly property bool isPageHost: true
    default property alias content: inner.data

    /// The page just became current.
    signal entered()

    // Assigned here rather than bound, so the page is visible before its
    // panels count their visible siblings for the entrance stagger.
    visible: false
    Component.onCompleted: host.visible = host.active
    onActiveChanged: {
        host.visible = host.active
        if (!active)
            return
        host.entered()
        if (!Theme.motion)
            return
        if (Theme.eorzea)
            rise.restart()
        else
            slide.restart()
    }

    Item {
        id: inner
        anchors.fill: parent
        transform: Translate { id: shift; x: 0; y: 0 }
    }

    ParallelAnimation {
        id: rise
        NumberAnimation {
            target: inner
            property: "opacity"
            from: 0
            to: 1
            duration: Theme.motionMedium
            easing.type: Easing.OutCubic
        }
        NumberAnimation {
            target: shift
            property: "y"
            from: 8
            to: 0
            duration: Theme.motionMedium
            easing.type: Easing.OutCubic
        }
    }

    ParallelAnimation {
        id: slide
        NumberAnimation {
            target: inner
            property: "opacity"
            from: 0
            to: 1
            duration: Theme.motionPage
            easing.type: Easing.Bezier
            easing.bezierCurve: Theme.curveStandard
        }
        NumberAnimation {
            target: shift
            property: "x"
            from: 12
            to: 0
            duration: Theme.motionPage
            easing.type: Easing.Bezier
            easing.bezierCurve: Theme.curveStandard
        }
    }

    // A style switch or reduced motion in the middle of an entrance must not
    // leave the page offset or faded.
    Connections {
        target: Theme
        function onEorzeaChanged() { host.settle() }
        function onMotionChanged() { host.settle() }
    }

    function settle() {
        rise.stop()
        slide.stop()
        inner.opacity = 1
        shift.x = 0
        shift.y = 0
    }
}
