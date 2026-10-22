import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// 导随统计浮窗: a frameless, always-on-top window the player keeps over the
// game (docs/ui-design.md §4.9). Two forms: the full window - a 30 px title
// bar, the modules, a resize corner - and the one-line 迷你条. 锁定 hides the
// chrome and lets the pointer pass through; the main window or the tray
// unlocks it. Only the plate is translucent: text and graphics stay solid.
Window {
    id: floating
    objectName: "statisticsFloatingWindow"

    readonly property var controller: Runtime.app.statisticsOverlay
    readonly property bool mini: controller.form === "mini"
    readonly property bool locked: controller.locked
    property bool positionReady: false
    property bool customizing: false
    readonly property bool transitioning: formTransition.running
    property rect targetGeometry: Qt.rect(0, 0, 300, 560)
    property point formOrigin: Qt.point(0, 0)
    property int formDuration: 0
    property real formBlend: 0

    OverlayData { id: ov }

    width: 300
    height: 560
    minimumWidth: 240
    maximumWidth: 520
    minimumHeight: 150
    maximumHeight: 1200
    visible: controller.visible
    color: "transparent"
    title: qsTr("导随统计浮窗")
    transientParent: null
    flags: Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint
           | (controller.locked ? Qt.WindowTransparentForInput | Qt.WindowDoesNotAcceptFocus : 0)

    function miniHeight() { return Math.max(40, Math.round(miniBar.implicitHeight)) }

    // Persist only settled geometry. Animation frames must never replace the
    // remembered full height, including when the window closes mid-transition.
    function rememberGeometry() {
        if (!positionReady) return
        if (mini) controller.setPosition(Qt.rect(x, y, width, height))
        else controller.setGeometry(Qt.rect(x, y, width, height))
    }
    function finishForm() {
        positionReady = false
        formTransition.stop()
        width = targetGeometry.width
        height = targetGeometry.height
        x = targetGeometry.x
        y = targetGeometry.y
        formBlend = mini ? 1 : 0
        minimumWidth = mini ? width : 240
        maximumWidth = mini ? width : 520
        minimumHeight = mini ? height : 150
        maximumHeight = mini ? height : 1200
        positionReady = visible
    }
    // Fit the destination before animating. Reversals retain the sequence's
    // original position and start from the current height and opacity.
    function applyForm(animate) {
        if (!transitioning) formOrigin = Qt.point(x, y)
        positionReady = false
        savePosition.stop()
        formTransition.stop()
        minimumWidth = 240
        maximumWidth = 520
        minimumHeight = 40
        maximumHeight = 1200
        const rect = controller.geometry
        if (mini) {
            controller.setPosition(Qt.rect(formOrigin.x, formOrigin.y, rect.width, miniHeight()))
        } else {
            controller.setGeometry(Qt.rect(formOrigin.x, formOrigin.y, rect.width, rect.height))
        }
        const fitted = controller.geometry
        targetGeometry = Qt.rect(fitted.x, fitted.y, fitted.width, mini ? miniHeight() : fitted.height)
        width = targetGeometry.width
        x = targetGeometry.x
        if (animate && Theme.motion) {
            formDuration = Theme.motionControl
            formTransition.start()
        }
        else finishForm()
    }
    onXChanged: { if (positionReady && visible) savePosition.restart() }
    onYChanged: { if (positionReady && visible) savePosition.restart() }
    onWidthChanged: { if (positionReady && visible && !mini) savePosition.restart() }
    onHeightChanged: { if (positionReady && visible && !mini) savePosition.restart() }
    onVisibleChanged: {
        if (visible) {
            positionReady = false
            const rect = controller.geometry
            x = rect.x
            y = rect.y
            applyForm(false)
            Qt.callLater(function() {
                if (!floating.transitioning) floating.positionReady = floating.visible
            })
        } else {
            rememberGeometry()
            savePosition.stop()
            customizing = false
            positionReady = false
            formTransition.stop()
        }
    }
    onMiniChanged: {
        if (!visible) return
        const animate = positionReady || transitioning
        // `mini` is already the destination here; save the previous form before
        // disabling geometry persistence, even if its debounce has not fired.
        if (positionReady) {
            if (mini) controller.setGeometry(Qt.rect(x, y, width, height))
            else controller.setPosition(Qt.rect(x, y, width, height))
        }
        customizing = false
        applyForm(animate)
    }
    Connections {
        target: floating
        function onClosing() {
            floating.rememberGeometry()
            floating.controller.hide()
        }
    }
    Timer { id: savePosition; interval: 180; onTriggered: floating.rememberGeometry() }
    ParallelAnimation {
        id: formTransition
        NumberAnimation { target: floating; property: "height"; to: floating.targetGeometry.height; duration: floating.formDuration; easing.type: Easing.Bezier; easing.bezierCurve: Theme.curveStandard }
        NumberAnimation { target: floating; property: "y"; to: floating.targetGeometry.y; duration: floating.formDuration; easing.type: Easing.Bezier; easing.bezierCurve: Theme.curveStandard }
        // Fade the outgoing form fully before revealing the incoming form;
        // different title/count positions must never be drawn over each other.
        NumberAnimation { target: floating; property: "formBlend"; to: floating.mini ? 1 : 0; duration: floating.formDuration; easing.type: Easing.Bezier; easing.bezierCurve: Theme.curveStandard }
        onFinished: floating.finishForm()
    }
    Connections {
        target: Theme
        function onMotionChanged() {
            // Each run captures its duration: changing live child durations to
            // zero while stopping can leave Qt's animation group running.
            if (!Theme.motion && floating.transitioning) floating.finishForm()
        }
    }
    Connections {
        target: floating.controller
        function onLockedChanged() { if (floating.controller.locked) floating.customizing = false }
    }
    Connections {
        target: miniBar
        function onImplicitHeightChanged() { if (floating.mini && floating.visible) floating.applyForm(floating.transitioning) }
    }

    // ---------------------------------------------------------------- 底板 --
    Rectangle {
        id: plate
        anchors.fill: parent
        radius: Theme.eorzea ? Theme.radiusM : Theme.radiusL
        color: Qt.rgba(Theme.surface.r, Theme.surface.g, Theme.surface.b, floating.controller.opacity)
        border.width: 1
        border.color: Theme.border
        PanelDecoration { }
    }

    // ---------------------------------------------------------------- 完整 --
    ColumnLayout {
        id: full
        objectName: "floatingFullContent"
        opacity: Math.max(0, 1 - floating.formBlend * 2)
        visible: !floating.mini || floating.transitioning
        enabled: !floating.mini
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.margins: 1
        height: (floating.transitioning ? floating.controller.geometry.height : floating.height) - 2
        spacing: 0

        // The form action leads the right-hand button group in both forms.
        Item {
            id: header
            objectName: "floatingHeader"
            visible: !floating.locked
            Layout.fillWidth: true
            Layout.preferredHeight: 30

            MouseArea {
                anchors.fill: parent
                enabled: !floating.transitioning
                cursorShape: Qt.SizeAllCursor
                onPressed: floating.startSystemMove()
                HoverHandler { id: headerHover }
            }
            // The buttons lie on the drag area: while the pointer rests on one of
            // them its own tip speaks, not the bar's.
            readonly property bool buttonHovered: miniButton.hovered || customizeButton.hovered
                                                  || lockButton.hovered || closeButton.hovered
            OverlayToolTip {
                objectName: "floatingHeaderTip"
                visible: headerHover.hovered && !header.buttonHovered
                text: qsTr("拖动移动")
            }

            RowLayout {
                anchors.fill: parent
                anchors.leftMargin: 6
                anchors.rightMargin: 3
                spacing: 4

                OverlayIcon { name: "grip"; color: Theme.textSecondary; opacity: 0.7; Layout.alignment: Qt.AlignVCenter }
                OverlayIcon { name: "crest"; color: ov.accent; Layout.alignment: Qt.AlignVCenter }
                Text {
                    Layout.fillWidth: true
                    Layout.minimumWidth: 0
                    Layout.leftMargin: 2
                    text: qsTr("导随统计")
                    color: Theme.textSecondary
                    elide: Text.ElideRight
                    font.pixelSize: Theme.fs(11.5)
                    font.weight: Font.DemiBold
                    font.letterSpacing: 0.4
                }
                RowLayout {
                    objectName: "floatingFullActions"
                    spacing: 4
                    OverlayIconButton {
                        id: miniButton
                        objectName: "floatingMiniButton"
                        iconName: "to-mini"
                        tip: qsTr("收成迷你条")
                        onClicked: floating.controller.form = "mini"
                    }
                    OverlayIconButton {
                        id: customizeButton
                        objectName: "floatingCustomizeButton"
                        enabled: !floating.transitioning
                        iconName: "customize"
                        tip: qsTr("自定义")
                        on: floating.customizing
                        onClicked: floating.customizing = !floating.customizing
                    }
                    OverlayIconButton {
                        id: lockButton
                        objectName: "floatingLockButton"
                        enabled: !floating.transitioning
                        iconName: "lock"
                        tip: qsTr("锁定位置和大小，鼠标穿透；在主窗口或托盘解锁")
                        onClicked: floating.controller.locked = true
                    }
                    OverlayIconButton {
                        id: closeButton
                        objectName: "floatingCloseButton"
                        enabled: !floating.transitioning
                        iconName: "close"
                        tip: qsTr("关闭浮窗")
                        onClicked: { floating.rememberGeometry(); floating.controller.hide() }
                    }
                }
            }
        }
        Rectangle { visible: header.visible; Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

        OverlayPeriodSelector {
            enabled: !floating.transitioning
            Layout.fillWidth: true
            Layout.leftMargin: 12
            Layout.rightMargin: 12
            Layout.topMargin: visible ? 8 : 0
            Layout.bottomMargin: visible ? 2 : 0
            visible: !floating.customizing && floating.controller.hasPeriodStatistics
            controller: floating.controller
        }

        // `.ov-body`: the modules, or the 自定义 panel in their place.
        Item {
            Layout.fillWidth: true
            Layout.fillHeight: true
            enabled: !floating.transitioning

            ScrollView {
                id: scroll
                objectName: "floatingStatisticsScroll"
                anchors.fill: parent
                anchors.leftMargin: 12
                anchors.rightMargin: 12
                anchors.topMargin: 10
                anchors.bottomMargin: 12
                visible: !floating.customizing
                clip: true
                contentWidth: availableWidth
                ScrollBar.horizontal.policy: ScrollBar.AlwaysOff
                ColumnLayout {
                    width: scroll.availableWidth
                    spacing: 8
                    Text {
                        Layout.fillWidth: true
                        visible: !Runtime.app.achievementSettingsLoaded
                        text: qsTr("数据暂未就绪，已有统计可能不是最新。")
                        wrapMode: Text.WordWrap
                        color: Theme.orangeText
                        font.pixelSize: Theme.fs(11)
                    }
                    StatisticsFloatingContent { Layout.fillWidth: true; ov: ov }
                }
            }

            OverlayCustomizePanel {
                objectName: "floatingCustomizePanel"
                anchors.fill: parent
                anchors.leftMargin: 12
                anchors.rightMargin: 12
                anchors.topMargin: 10
                anchors.bottomMargin: 12
                visible: floating.customizing
                ov: ov
                controller: floating.controller
                onDone: floating.customizing = false
            }
        }
    }

    // `.ov-resize`: the corner glyph; the platform does the resizing.
    Item {
        visible: !floating.mini && !floating.locked && !floating.transitioning
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.margins: 1
        width: 14
        height: 14
        OverlayIcon { anchors.fill: parent; name: "resize"; color: Theme.textSecondary; size: 14; opacity: resizeHover.hovered ? 1 : 0.65 }
        MouseArea {
            anchors.fill: parent
            cursorShape: Qt.SizeFDiagCursor
            onPressed: floating.startSystemResize(Qt.RightEdge | Qt.BottomEdge)
            HoverHandler { id: resizeHover }
        }
        OverlayToolTip { visible: resizeHover.hovered; text: qsTr("拖动调整大小") }
    }

    // ---------------------------------------------------------------- 迷你条 --
    OverlayMiniBar {
        id: miniBar
        objectName: "floatingMiniContent"
        visible: floating.mini || floating.transitioning
        enabled: floating.mini
        opacity: Math.max(0, floating.formBlend * 2 - 1)
        movingEnabled: !floating.transitioning
        secondaryActionsEnabled: !floating.transitioning
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.margins: 1
        ov: ov
        controller: floating.controller
        onLockRequested: floating.controller.locked = true
        onExpandRequested: floating.controller.form = "full"
        onMoveRequested: floating.startSystemMove()
    }

    Shortcut {
        sequence: "Escape"
        enabled: floating.visible && floating.customizing
        onActivated: floating.customizing = false
    }
}
