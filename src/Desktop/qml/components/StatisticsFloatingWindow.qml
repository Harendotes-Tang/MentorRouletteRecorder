import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// 导随统计浮窗: a frameless, always-on-top window the player keeps over the
// game (docs/ui-design.md §4.9). Two forms: the full window - a 30 px title
// bar, the modules, a resize corner - and the one-line 迷你条. 锁定 hides the
// chrome and lets the pointer pass through; the main window or the tray
// unlocks it. Only the plate is translucent: text and graphics stay solid.
Window {
    id: floating
    objectName: "statisticsFloatingWindow"

    readonly property var controller: App.statisticsOverlay
    readonly property bool mini: controller.form === "mini"
    readonly property bool locked: controller.locked
    property bool positionReady: false
    property bool customizing: false
    readonly property int miniWidth: 280

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

    // The full form's geometry is the saved one; the mini bar shares its
    // position and has its own size.
    function rememberGeometry() {
        if (!positionReady) return
        if (mini) controller.setPosition(Qt.rect(x, y, width, height))
        else controller.setGeometry(Qt.rect(x, y, width, height))
    }
    // The constraints are set here, in order, not bound: a binding may run
    // after this handler and clamp the size it just set.
    function applyForm() {
        const rect = controller.geometry
        if (mini) {
            const bar = miniHeight()
            minimumWidth = miniWidth
            maximumWidth = miniWidth
            minimumHeight = bar
            maximumHeight = bar
            width = miniWidth
            height = bar
            // The bar's own rectangle decides what fits: docked at the bottom
            // edge it stays there, and a second line grows it upwards, not off
            // the screen.
            controller.setPosition(Qt.rect(x, y, miniWidth, bar))
            x = controller.geometry.x
            y = controller.geometry.y
        } else {
            minimumWidth = 240
            maximumWidth = 520
            minimumHeight = 150
            maximumHeight = 1200
            width = rect.width
            height = rect.height
            // Back from the bar: the full window must fit where the bar was.
            controller.setGeometry(Qt.rect(x, y, rect.width, rect.height))
            x = controller.geometry.x
            y = controller.geometry.y
        }
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
            applyForm()
            Qt.callLater(function() { floating.positionReady = floating.visible })
        } else {
            rememberGeometry()
            savePosition.stop()
            customizing = false
            positionReady = false
        }
    }
    onMiniChanged: {
        if (!visible) return
        const ready = positionReady
        positionReady = false
        rememberGeometry()
        customizing = false
        applyForm()
        positionReady = ready
    }
    onClosing: {
        rememberGeometry()
        controller.hide()
    }
    Timer { id: savePosition; interval: 180; onTriggered: floating.rememberGeometry() }
    Connections {
        target: floating.controller
        function onLockedChanged() { if (floating.controller.locked) floating.customizing = false }
    }
    Connections {
        target: miniBar
        function onImplicitHeightChanged() { if (floating.mini && floating.visible) floating.applyForm() }
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
        visible: !floating.mini
        anchors.fill: parent
        anchors.margins: 1
        spacing: 0

        // `.ov-hd`: grip, crest, title, then 收成迷你条 / 自定义 / 锁定 / 关闭.
        Item {
            id: header
            objectName: "floatingHeader"
            visible: !floating.locked
            Layout.fillWidth: true
            Layout.preferredHeight: 30

            MouseArea {
                anchors.fill: parent
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
                    iconName: "customize"
                    tip: qsTr("自定义")
                    on: floating.customizing
                    onClicked: floating.customizing = !floating.customizing
                }
                OverlayIconButton {
                    id: lockButton
                    objectName: "floatingLockButton"
                    iconName: "lock"
                    tip: qsTr("锁定位置和大小，鼠标穿透；在主窗口或托盘解锁")
                    onClicked: floating.controller.locked = true
                }
                OverlayIconButton {
                    id: closeButton
                    objectName: "floatingCloseButton"
                    iconName: "close"
                    tip: qsTr("关闭浮窗")
                    onClicked: { floating.rememberGeometry(); floating.controller.hide() }
                }
            }
        }
        Rectangle { visible: header.visible; Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }

        // `.ov-body`: the modules, or the 自定义 panel in their place.
        Item {
            Layout.fillWidth: true
            Layout.fillHeight: true

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
                        visible: !App.achievementSettingsLoaded
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
        visible: !floating.mini && !floating.locked
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
        visible: floating.mini
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
