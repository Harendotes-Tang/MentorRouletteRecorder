pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// 自定义: the float's own settings, in place of its content; the modules are
// reordered by dragging the grip (or with ↑ / ↓ on it, or the arrows that
// appear on hover) and shown or hidden with a switch; below are 副本排行,
// 背景不透明度 and 文字标签. 恢复默认 puts everything back; 完成 returns.
Item {
    id: panel

    required property var ov
    required property var controller
    signal done()

    implicitHeight: viewport.implicitHeight

    ScrollView {
        id: viewport
        anchors.fill: parent
        clip: true
        contentWidth: availableWidth
        implicitHeight: column.implicitHeight
        ScrollBar.horizontal.policy: ScrollBar.AlwaysOff

        ColumnLayout {
            id: column
            width: viewport.availableWidth
            spacing: 12

            ColumnLayout {
                spacing: 2
                Text { text: qsTr("自定义浮窗"); color: Theme.textPrimary; font.pixelSize: Theme.fs(13); font.weight: Font.Bold }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("拖动排序，开关控制显示")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    wrapMode: Text.WordWrap
                }
            }

            // ---------------------------------------------------- 模块列表 --
            ColumnLayout {
                id: list
                Layout.fillWidth: true
                spacing: 3

                property string dragKey: ""
                property int dragFrom: -1
                property int dragTo: -1
                property real dragOffset: 0
                readonly property real pitch: 34 + spacing
                // The rows are rebuilt after every change; the control that had the
                // keyboard gets it back on the new row of the same module.
                property string focusKey: ""
                property string focusPart: ""
                function keepFocus(key, part) { focusKey = key; focusPart = part }

                function shiftFor(index) {
                    if (dragFrom < 0 || index === dragFrom) return 0
                    if (dragFrom < dragTo && index > dragFrom && index <= dragTo) return -pitch
                    if (dragFrom > dragTo && index >= dragTo && index < dragFrom) return pitch
                    return 0
                }

                Repeater {
                    model: panel.controller.moduleRows
                    delegate: Rectangle {
                        id: row
                        required property var modelData
                        required property int index
                        readonly property string key: modelData.key
                        readonly property bool on: modelData.on === true
                        readonly property bool dragging: list.dragKey === key

                        Layout.fillWidth: true
                        implicitHeight: 34
                        radius: Theme.radiusS
                        color: dragging ? Theme.fillStrong : Theme.insetBackground
                        border.width: 1
                        border.color: dragging ? (Theme.eorzea ? Theme.gold3 : Theme.accent) : Theme.insetBorder
                        z: dragging ? 2 : 0
                        transform: Translate { y: row.dragging ? list.dragOffset : list.shiftFor(row.index) }
                        Component.onCompleted: {
                            if (list.focusKey !== key) return
                            const part = list.focusPart
                            list.focusKey = ""
                            list.focusPart = ""
                            Qt.callLater(function() {
                                const target = part === "up" ? upButton : part === "down" ? downButton
                                             : part === "switch" ? moduleSwitch : handle
                                target.forceActiveFocus()
                            })
                        }

                        RowLayout {
                            anchors.fill: parent
                            anchors.leftMargin: 2
                            anchors.rightMargin: 6
                            spacing: 7

                            // `.cz-handle`: the grip. Drag it, or press ↑ / ↓ on it.
                            Rectangle {
                                id: handle
                                objectName: "floatingDragHandle_" + row.key
                                Layout.preferredWidth: 18
                                Layout.preferredHeight: 26
                                radius: Theme.radiusXs
                                color: handleHover.hovered || activeFocus ? Theme.fill : "transparent"
                                activeFocusOnTab: true
                                Accessible.role: Accessible.Button
                                Accessible.name: qsTr("拖动排序") + " · " + row.modelData.title
                                OverlayIcon { anchors.centerIn: parent; name: "grip"; color: handleHover.hovered || handle.activeFocus ? Theme.textPrimary : Theme.textSecondary }
                                HoverHandler { id: handleHover; cursorShape: Qt.OpenHandCursor }
                                OverlayToolTip { visible: handleHover.hovered && list.dragKey.length === 0; text: qsTr("拖动排序") }
                                Keys.onUpPressed: function(event) { list.keepFocus(row.key, "handle"); panel.controller.moveModule(row.key, -1); event.accepted = true }
                                Keys.onDownPressed: function(event) { list.keepFocus(row.key, "handle"); panel.controller.moveModule(row.key, 1); event.accepted = true }
                                DragHandler {
                                    id: drag
                                    target: null
                                    yAxis.enabled: true
                                    xAxis.enabled: false
                                    cursorShape: Qt.ClosedHandCursor
                                    // The list scrolls; a grip drag is never a flick.
                                    grabPermissions: PointerHandler.CanTakeOverFromItems
                                                     | PointerHandler.CanTakeOverFromHandlersOfDifferentType
                                                     | PointerHandler.ApprovesTakeOverByHandlersOfSameType
                                    onActiveChanged: {
                                        if (active) {
                                            list.dragKey = row.key
                                            list.dragFrom = row.index
                                            list.dragTo = row.index
                                            list.dragOffset = 0
                                        } else {
                                            const to = list.dragTo
                                            const from = list.dragFrom
                                            list.dragKey = ""
                                            list.dragFrom = -1
                                            list.dragTo = -1
                                            list.dragOffset = 0
                                            if (to !== from) panel.controller.moveModuleTo(row.key, to)
                                        }
                                    }
                                    onTranslationChanged: {
                                        if (!active) return
                                        list.dragOffset = translation.y
                                        const rows = panel.controller.moduleRows.length
                                        list.dragTo = Math.max(0, Math.min(rows - 1, list.dragFrom + Math.round(translation.y / list.pitch)))
                                    }
                                }
                            }

                            OverlayIcon {
                                name: row.key === "progress" ? "progress" : row.key === "current_duty" ? "current"
                                    : row.key === "elapsed" ? "time" : row.key === "dungeons" ? "duty"
                                    : row.key === "jobs" ? "roles" : "pending"
                                color: Theme.textSecondary
                                opacity: row.on ? 1 : 0.5
                                Layout.alignment: Qt.AlignVCenter
                            }

                            ColumnLayout {
                                Layout.fillWidth: true
                                Layout.minimumWidth: 0
                                spacing: 0
                                opacity: row.on ? 1 : 0.5
                                Text {
                                    Layout.fillWidth: true
                                    text: row.modelData.title
                                    textFormat: Text.PlainText
                                    elide: Text.ElideRight
                                    color: Theme.textPrimary
                                    font.pixelSize: Theme.fs(12.5)
                                    font.weight: Font.DemiBold
                                }
                                Text {
                                    Layout.fillWidth: true
                                    visible: text.length > 0
                                    text: row.modelData.hint || ""
                                    textFormat: Text.PlainText
                                    elide: Text.ElideRight
                                    color: Theme.textSecondary
                                    font.pixelSize: Theme.fs(10.5)
                                }
                            }

                            // `.cz-arrows`: shown while the pointer is on the row.
                            RowLayout {
                                spacing: 1
                                opacity: rowHover.hovered || upButton.activeFocus || downButton.activeFocus ? 1 : 0
                                Behavior on opacity { NumberAnimation { duration: Theme.motionFast } }
                                OverlayIconButton {
                                    id: upButton
                                    objectName: "floatingMoveUp_" + row.key
                                    iconName: "arrow-up"
                                    buttonSize: 20
                                    tip: qsTr("上移")
                                    enabled: row.index > 0
                                    onClicked: { list.keepFocus(row.key, "up"); panel.controller.moveModule(row.key, -1) }
                                }
                                OverlayIconButton {
                                    id: downButton
                                    objectName: "floatingMoveDown_" + row.key
                                    iconName: "arrow-down"
                                    buttonSize: 20
                                    tip: qsTr("下移")
                                    enabled: row.index < panel.controller.moduleRows.length - 1
                                    onClicked: { list.keepFocus(row.key, "down"); panel.controller.moveModule(row.key, 1) }
                                }
                            }

                            ToggleSwitch {
                                id: moduleSwitch
                                objectName: "floatingModuleToggle_" + row.key
                                Layout.alignment: Qt.AlignVCenter
                                scale: 0.9
                                accessibleName: row.modelData.title
                                checked: row.on
                                onToggled: { if (activeFocus) list.keepFocus(row.key, "switch"); panel.controller.toggleModule(row.key) }
                            }
                        }
                        HoverHandler { id: rowHover }
                    }
                }
            }

            // ---------------------------------------------------- 副本排行 --
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 6
                Text { text: qsTr("副本排行"); color: Theme.textPrimary; font.pixelSize: Theme.fs(12); font.weight: Font.DemiBold }
                SegmentedControl {
                    objectName: "floatingRowLimit"
                    Layout.fillWidth: true
                    options: [{ value: 0, label: qsTr("关") }, { value: 3, label: qsTr("前 3") },
                              { value: 5, label: qsTr("前 5") }, { value: 10, label: qsTr("前 10") },
                              { value: panel.controller.allRows, label: qsTr("全部") }]
                    currentValue: panel.controller.rowLimit
                    onActivated: function(value) { panel.controller.rowLimit = value }
                }
            }

            // ---------------------------------------------------- 不透明度 --
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 6
                RowLayout {
                    Layout.fillWidth: true
                    Text { text: qsTr("背景不透明度"); color: Theme.textPrimary; font.pixelSize: Theme.fs(12); font.weight: Font.DemiBold }
                    Item { Layout.fillWidth: true }
                    Text {
                        text: Math.round(panel.controller.opacity * 100) + "%"
                        color: panel.ov.accent
                        font.pixelSize: Theme.fs(12)
                        font.weight: Font.DemiBold
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                    }
                }
                Slider {
                    id: opacitySlider
                    objectName: "floatingOpacitySlider"
                    Layout.fillWidth: true
                    from: 30
                    to: 100
                    stepSize: 1
                    snapMode: Slider.SnapAlways
                    focusPolicy: Qt.TabFocus
                    Accessible.name: qsTr("背景不透明度")
                    // Dragging writes Slider.value; the Binding keeps it a view of the setting.
                    Binding {
                        target: opacitySlider
                        property: "value"
                        value: Math.round(panel.controller.opacity * 100)
                        restoreMode: Binding.RestoreNone
                    }
                    background: Rectangle {
                        x: opacitySlider.leftPadding
                        y: opacitySlider.topPadding + opacitySlider.availableHeight / 2 - height / 2
                        width: opacitySlider.availableWidth
                        height: 4
                        radius: 2
                        color: Theme.fillStrong
                        Rectangle {
                            width: opacitySlider.visualPosition * parent.width
                            height: parent.height
                            radius: 2
                            color: panel.ov.accent
                        }
                    }
                    handle: Rectangle {
                        x: opacitySlider.leftPadding + opacitySlider.visualPosition * (opacitySlider.availableWidth - width)
                        y: opacitySlider.topPadding + opacitySlider.availableHeight / 2 - height / 2
                        width: 16
                        height: 16
                        radius: 8
                        color: panel.ov.accent
                        border.width: 2
                        border.color: Theme.surface
                    }
                    onMoved: panel.controller.opacity = Math.round(value) / 100
                }
                Text {
                    Layout.fillWidth: true
                    text: qsTr("只影响底板，文字和图形始终清晰")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    wrapMode: Text.WordWrap
                }
            }

            // ---------------------------------------------------- 文字标签 --
            RowLayout {
                Layout.fillWidth: true
                spacing: 10
                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 2
                    Text { text: qsTr("文字标签"); color: Theme.textPrimary; font.pixelSize: Theme.fs(12); font.weight: Font.DemiBold }
                    Text {
                        Layout.fillWidth: true
                        text: qsTr("显示模块名和图例名称；关闭时鼠标悬停查看")
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(11)
                        wrapMode: Text.WordWrap
                    }
                }
                ToggleSwitch {
                    objectName: "floatingLabelsToggle"
                    Layout.alignment: Qt.AlignTop
                    accessibleName: qsTr("文字标签")
                    checked: panel.controller.labels
                    onToggled: function(value) { panel.controller.labels = value }
                }
            }

            // ---------------------------------------------------- 页脚 --
            Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: Theme.border }
            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                AppButton {
                    objectName: "floatingCustomizationReset"
                    variant: "ghost"
                    compact: true
                    text: qsTr("恢复默认")
                    onClicked: panel.controller.reset()
                }
                Item { Layout.fillWidth: true }
                AppButton {
                    objectName: "floatingCustomizationDone"
                    variant: "primary"
                    compact: true
                    text: qsTr("完成")
                    onClicked: panel.done()
                }
            }
        }
    }
}
