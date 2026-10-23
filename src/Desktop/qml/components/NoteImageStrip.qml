pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls 6.8
import QtQuick.Layouts
import MentorRecorder 1.0
import "Lucide.js" as Lucide

// 备注图片: the thumbnails attached to a run's 备注. Read-only in the detail panel;
// in the wizard (`editable`) it also shows an add tile and a remove badge on every
// thumbnail. Tapping a thumbnail opens it full size in a modal viewer.
//
// `rows` is what NoteImageStore.imagesFor() hands out, or the wizard's staged list:
//   { url, name, path, pending } - `pending` marks a file chosen but not yet copied.
// The strip only shows and asks; the dialog owns the staging and the store owns the files.
ColumnLayout {
    id: strip

    property var rows: []
    property bool editable: false
    property int thumbSize: 72
    /// One line under the thumbnails, typically where the files are kept.
    property string hintText: ""

    signal addRequested()
    signal removeRequested(var row)

    readonly property int count: rows ? rows.length : 0
    readonly property real pixelRatio: Screen.devicePixelRatio || 1

    spacing: 6

    Flow {
        objectName: "noteImageFlow"
        Layout.fillWidth: true
        spacing: 8

        Repeater {
            model: strip.rows

            delegate: AbstractButton {
                id: thumb

                required property var modelData
                required property int index

                objectName: "noteImageThumb"
                width: strip.thumbSize
                height: strip.thumbSize
                focusPolicy: Qt.TabFocus
                hoverEnabled: true
                Accessible.name: thumb.modelData && thumb.modelData.name ? thumb.modelData.name : ""
                onClicked: viewer.show(thumb.modelData)
                background: Rectangle {
                    radius: Theme.radiusS
                    color: Theme.insetBackground
                    border.width: thumb.activeFocus ? 3 : 1
                    border.color: thumb.hovered || thumb.activeFocus ? Theme.accent : Theme.insetBorder
                }
                clip: true

                Image {
                    anchors.fill: parent
                    anchors.margins: thumb.activeFocus ? 3 : 1
                    source: thumb.modelData && thumb.modelData.url ? thumb.modelData.url : ""
                    fillMode: Image.PreserveAspectCrop
                    asynchronous: true
                    cache: false
                    smooth: true
                    // Decoded at thumbnail size, not at the screenshot's full size.
                    sourceSize: Qt.size(Math.round(strip.thumbSize * strip.pixelRatio),
                                        Math.round(strip.thumbSize * strip.pixelRatio))
                }

                // A file the wizard has staged but not yet copied into the folder.
                Tag {
                    visible: !!(thumb.modelData && thumb.modelData.pending)
                    anchors.left: parent.left
                    anchors.bottom: parent.bottom
                    anchors.margins: 4
                    text: qsTr("待保存")
                    variant: "accent"
                }

                ToolTip.visible: thumb.hovered && !!(thumb.modelData && thumb.modelData.name)
                ToolTip.delay: 600
                ToolTip.text: thumb.modelData && thumb.modelData.name ? thumb.modelData.name : ""

                // 移除: sits on the corner so it cannot be mistaken for the picture.
                AbstractButton {
                    id: removeButton
                    objectName: "noteImageRemove"
                    visible: strip.editable
                    anchors.top: parent.top
                    anchors.right: parent.right
                    anchors.margins: 3
                    width: 20
                    height: 20
                    focusPolicy: Qt.TabFocus
                    hoverEnabled: true
                    Accessible.name: qsTr("移除") + " " + thumb.Accessible.name
                    onClicked: {
                        // The delegate may be destroyed synchronously by its owner.
                        if (activeFocus)
                            addButton.forceActiveFocus(Qt.TabFocusReason)
                        strip.removeRequested(thumb.modelData)
                    }
                    background: Rectangle {
                        radius: 10
                        color: removeButton.hovered ? Theme.red : Theme.inverseBackground
                        border.width: removeButton.activeFocus ? 2 : 0
                        border.color: Theme.accent
                    }
                    opacity: 0.92

                    Image {
                        anchors.centerIn: parent
                        width: 12
                        height: 12
                        sourceSize: Qt.size(Math.round(12 * strip.pixelRatio),
                                            Math.round(12 * strip.pixelRatio))
                        source: Lucide.source("x", Theme.inverseText)
                        smooth: true
                    }

                }
            }
        }

        // 添加图片 tile, a dashed well the size of a thumbnail.
        AbstractButton {
            id: addButton
            objectName: "noteImageAdd"
            visible: strip.editable
            width: strip.thumbSize
            height: strip.thumbSize
            focusPolicy: Qt.TabFocus
            hoverEnabled: true
            Accessible.name: qsTr("添加图片")
            onClicked: strip.addRequested()
            background: Rectangle {
                radius: Theme.radiusS
                color: addButton.hovered ? Theme.fill : "transparent"
                border.width: addButton.activeFocus ? 3 : 1
                border.color: addButton.hovered || addButton.activeFocus ? Theme.accent : Theme.borderStrong
            }

            Column {
                anchors.centerIn: parent
                spacing: 4

                Image {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 18
                    height: 18
                    sourceSize: Qt.size(Math.round(18 * strip.pixelRatio),
                                        Math.round(18 * strip.pixelRatio))
                    source: Lucide.source("plus", Theme.textSecondary)
                    smooth: true
                }

                Text {
                    anchors.horizontalCenter: parent.horizontalCenter
                    text: qsTr("添加图片")
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                }
            }

        }
    }

    Text {
        objectName: "noteImageHint"
        Layout.fillWidth: true
        visible: strip.hintText.length > 0
        text: strip.hintText
        color: Theme.textMuted
        font.pixelSize: Theme.fs(11)
        wrapMode: Text.WrapAnywhere
    }

    // ------------------------------------------------------------ 查看 --
    // Full-size view. Click anywhere, or Esc, to close.
    QtObject {
        readonly property Popup imageViewer: Popup {
            id: viewer

            objectName: "noteImageViewer"
            property var row: null

            function show(value) {
                row = value
                open()
            }

            parent: Overlay.overlay
            anchors.centerIn: parent
            modal: true
            focus: true
            closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutside
            padding: 12
            width: Math.min(parent ? parent.width - 48 : 800, 1100)
            height: Math.min(parent ? parent.height - 48 : 700, 820)
            Overlay.modal: Rectangle { color: Theme.modalScrim(viewer.palette.shadow) }
            background: DialogFrame { opaque: true }

            contentItem: ColumnLayout {
                spacing: 8

                Image {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    source: viewer.row && viewer.row.url ? viewer.row.url : ""
                    fillMode: Image.PreserveAspectFit
                    asynchronous: true
                    cache: false
                    smooth: true

                    TapHandler {
                        gesturePolicy: TapHandler.ReleaseWithinBounds
                        onTapped: viewer.close()
                    }
                }

                Text {
                    Layout.fillWidth: true
                    text: viewer.row && viewer.row.name ? viewer.row.name : ""
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(11)
                    elide: Text.ElideMiddle
                    horizontalAlignment: Text.AlignHCenter
                }
            }
        }
    }
}
