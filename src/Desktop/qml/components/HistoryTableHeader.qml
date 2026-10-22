pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

// The history table's header (docs/ui-design.md §4.2). Every title sits over
// its column at the x and width the page's layout gives it; the rows read the
// same layout, so header and cells never drift apart.
//
// A click on a title sorts by that column, as before. Holding a title and
// moving sideways lifts it onto a raised plate and carries it along the header;
// the other titles slide aside to open its slot and a line across header and
// rows marks where it will land. The 7 px strip at a title's right edge is the
// width handle (the pointer turns into a split cursor there): dragging it
// resizes the column live, rows included, with a line down the new edge; a
// double click on it returns the column to its default width. Neither drag can
// sort: TapHandler gives its tap up as soon as the pointer travels the drag
// threshold, which is also where the DragHandlers begin, and the handle is a
// MouseArea of its own, so a press on it never reaches the title's handlers.
// A right click anywhere on the header opens the column dialog.
Rectangle {
    id: header

    /// HistoryPage.columnLayout.
    required property var layout
    /// HistoryPage.columnCatalog: key -> { label, sortKey, minimum, ... }.
    required property var catalog
    /// App.historyColumns.
    required property var controller
    property string sortField: ""
    property bool sortAscending: false
    /// x of the first column inside `content`: the row checkbox's 32 px.
    property real columnsOrigin: 32
    /// Height of the rows under the header; the guides are drawn across them.
    property real bodyHeight: 0

    /// A width drag in progress: the page previews this width in its layout.
    property string resizeKey: ""
    property int resizeWidth: 0
    /// A reorder drag in progress: the title, where it started and where it
    /// would land (indexes among the shown columns), and how far it has moved.
    property string dragKey: ""
    property int dragFrom: -1
    property int dragTo: -1
    property real dragOffset: 0

    signal sortRequested(string key)
    signal customizeRequested()

    implicitHeight: 32
    color: Theme.eorzea
           ? (Theme.dark ? "#2e000000" : "#14785c28")
           : "transparent"

    /// How far the title at \a index slides to open the slot for the one being carried.
    function shiftFor(index) {
        if (header.dragFrom < 0 || index === header.dragFrom)
            return 0
        const carried = header.layout[header.dragKey]
        if (!carried)
            return 0
        if (header.dragFrom < header.dragTo && index > header.dragFrom && index <= header.dragTo)
            return -carried.width
        if (header.dragFrom > header.dragTo && index >= header.dragTo && index < header.dragFrom)
            return carried.width
        return 0
    }
    /// The slot the carried title's centre is over: before the first other title
    /// whose centre lies to the right of it, else after the last.
    function dropIndexFor(centreX) {
        const keys = header.layout.keys
        let index = 0
        for (let i = 0; i < keys.length; ++i) {
            if (keys[i] === header.dragKey)
                continue
            const slot = header.layout[keys[i]]
            if (centreX < slot.x + slot.width / 2)
                return index
            ++index
        }
        return index
    }
    /// x of the drop guide: the far edge of the title the carried one will pass.
    function dropGuideX() {
        if (header.dragFrom < 0 || header.dragTo === header.dragFrom)
            return 0
        const slot = header.layout[header.layout.keys[header.dragTo]]
        if (!slot)
            return 0
        const edge = header.dragTo > header.dragFrom ? slot.x + slot.width : slot.x
        return Math.round(content.x + header.columnsOrigin + edge) - 1
    }

    // A right click anywhere on the header: the dialog.
    TapHandler {
        acceptedButtons: Qt.RightButton
        onTapped: header.customizeRequested()
    }

    Item {
        id: content
        anchors.fill: parent
        anchors.leftMargin: 4
        anchors.rightMargin: 4

        SelectionCheckBox {
            objectName: "selectCurrentHistoryPage"
            x: 0
            width: 32
            height: 30
            anchors.verticalCenter: parent.verticalCenter
            checked: Runtime.app.allCurrentHistoryPageChecked
            enabled: !Runtime.app.runs.loading && Runtime.app.runs.loadError.length === 0 && Runtime.app.runs.total > 0
            Accessible.name: qsTr("全选当前页")
            ToolTip.visible: hovered
            ToolTip.text: qsTr("全选当前页")
            onClicked: Runtime.app.setCurrentHistoryPageChecked(checked)
        }

        Repeater {
            // The shown columns, not layout.keys: the layout is recomputed on
            // every pointer move of a width drag, and the titles must stay.
            model: header.controller.visible

            delegate: Item {
                id: cell

                required property string modelData
                required property int index
                readonly property string key: modelData
                readonly property var slot: header.layout[key] || ({ x: 0, width: 0, maximum: 0 })
                readonly property var column: header.catalog[key] || ({ label: key, sortKey: "", minimum: 0 })
                readonly property bool carried: header.dragKey === key
                readonly property bool sortable: typeof column.sortKey === "string" && column.sortKey.length > 0
                readonly property bool sorted: sortable && header.sortField === column.sortKey

                x: header.columnsOrigin + slot.x + (carried ? header.dragOffset : header.shiftFor(index))
                width: slot.width
                height: parent.height
                z: carried ? 2 : 0
                visible: slot.width > 0
                // The others slide aside while a title is carried; the carried
                // one follows the pointer at once.
                Behavior on x {
                    enabled: header.dragKey.length > 0 && !cell.carried
                    NumberAnimation { duration: Theme.motionFast }
                }

                // The raised plate under a carried title.
                Rectangle {
                    anchors.fill: parent
                    anchors.topMargin: 2
                    anchors.bottomMargin: 3
                    visible: cell.carried
                    radius: Theme.radiusS
                    color: Theme.surfaceRaised
                    border.width: 1
                    border.color: Theme.eorzea ? Theme.gold3 : Theme.accent
                }

                Text {
                    objectName: "historyColumnHeader_" + cell.key
                    anchors.fill: parent
                    anchors.leftMargin: 2
                    anchors.rightMargin: 2
                    text: cell.column.label
                          + (cell.sorted ? (header.sortAscending ? " ↑" : " ↓") : "")
                    textFormat: Text.PlainText
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment: Text.AlignVCenter
                    // workbench `.table th`: t5, regular, text-3.
                    color: Theme.eorzea
                           ? Theme.gold
                           : (cell.sorted ? Theme.accent : Theme.textMuted)
                    font.pixelSize: Theme.fs(12)
                    font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                    font.letterSpacing: Theme.eorzea ? 0.7 : 0
                    elide: Text.ElideRight
                }

                // A click (press and release within the drag threshold) sorts.
                // The tap is given up by the handler itself once the pointer
                // travels the threshold, so a carry never sorts.
                TapHandler {
                    enabled: cell.sortable
                    onTapped: header.sortRequested(cell.key)
                }

                // Holding the title and moving sideways carries it.
                DragHandler {
                    id: reorder
                    objectName: "historyColumnDrag_" + cell.key
                    target: null
                    xAxis.enabled: true
                    yAxis.enabled: false
                    enabled: header.resizeKey.length === 0
                    // The page scrolls vertically; a sideways drag is never a flick.
                    // Neither drag of the header approves a takeover by the other.
                    grabPermissions: PointerHandler.CanTakeOverFromItems
                                     | PointerHandler.CanTakeOverFromHandlersOfDifferentType
                    property bool cancelled: false
                    onCanceled: reorder.cancelled = true
                    onActiveChanged: {
                        if (active) {
                            reorder.cancelled = false
                            header.dragKey = cell.key
                            header.dragFrom = cell.index
                            header.dragTo = cell.index
                            header.dragOffset = 0
                            return
                        }
                        const key = header.dragKey
                        const from = header.dragFrom
                        const to = header.dragTo
                        header.dragKey = ""
                        header.dragFrom = -1
                        header.dragTo = -1
                        header.dragOffset = 0
                        if (reorder.cancelled || key.length === 0 || to === from)
                            return
                        // The move rebuilds the titles, this one included; let
                        // the handler finish its own release first.
                        Qt.callLater(function() { header.controller.moveTo(key, to) })
                    }
                    onTranslationChanged: {
                        if (!active)
                            return
                        const slot = header.layout[cell.key]
                        if (!slot)
                            return
                        // The title stays within the columns' width.
                        const limit = Math.max(0, header.layout.available - slot.width)
                        header.dragOffset = Math.max(-slot.x, Math.min(limit - slot.x, translation.x))
                        header.dragTo = header.dropIndexFor(slot.x + header.dragOffset + slot.width / 2)
                    }
                }

                // The width handle at the right edge. A MouseArea, so a press on
                // it belongs to the handle alone: the title's tap and carry
                // handlers never see it, and a double click here cannot sort.
                MouseArea {
                    id: resizeHandle
                    objectName: "historyColumnResize_" + cell.key
                    anchors.right: parent.right
                    anchors.top: parent.top
                    anchors.bottom: parent.bottom
                    width: 7
                    visible: !cell.carried
                    hoverEnabled: true
                    cursorShape: Qt.SplitHCursor
                    // 双击: the column goes back to its default width.
                    onDoubleClicked: header.controller.setWidth(cell.key, 0)

                    readonly property bool resizing: header.resizeKey === cell.key

                    // The edge, shown under the pointer and while dragging.
                    Rectangle {
                        anchors.right: parent.right
                        anchors.verticalCenter: parent.verticalCenter
                        width: resizeHandle.resizing ? 2 : 1
                        height: parent.height - 10
                        visible: resizeHandle.containsMouse || resizeHandle.resizing
                        color: resizeHandle.resizing
                               ? (Theme.eorzea ? Theme.gold : Theme.accent)
                               : Theme.borderStrong
                    }
                    DragHandler {
                        id: resize
                        objectName: "historyColumnResizeDrag_" + cell.key
                        target: null
                        xAxis.enabled: true
                        yAxis.enabled: false
                        enabled: header.dragKey.length === 0
                        cursorShape: Qt.SplitHCursor
                        grabPermissions: PointerHandler.CanTakeOverFromItems
                                         | PointerHandler.CanTakeOverFromHandlersOfDifferentType
                        property int startWidth: 0
                        property int narrowest: 0
                        property int widest: 0
                        property bool cancelled: false
                        onCanceled: resize.cancelled = true
                        onActiveChanged: {
                            if (active) {
                                const slot = header.layout[cell.key]
                                resize.cancelled = false
                                resize.startWidth = slot ? Math.round(slot.width) : 0
                                resize.narrowest = Math.max(header.controller.minimumWidth, cell.column.minimum || 0)
                                // Never below what is on screen: a column the
                                // window has squeezed can still be dragged
                                // narrower, and does not jump when touched.
                                resize.widest = Math.max(resize.narrowest, resize.startWidth, slot ? slot.maximum : 0)
                                // Width first: the layout previews the pair as soon as the key is set.
                                header.resizeWidth = resize.startWidth
                                header.resizeKey = cell.key
                                return
                            }
                            const key = header.resizeKey
                            const width = header.resizeWidth
                            header.resizeKey = ""
                            header.resizeWidth = 0
                            // A drag that ends where it began, or is cancelled, stores nothing.
                            if (resize.cancelled || key.length === 0 || width <= 0 || width === resize.startWidth)
                                return
                            header.controller.setWidth(key, width)
                        }
                        onTranslationChanged: {
                            if (!active)
                                return
                            header.resizeWidth = Math.round(Math.max(resize.narrowest,
                                                                     Math.min(resize.widest, resize.startWidth + translation.x)))
                        }
                    }
                }
            }
        }
    }

    // Where the carried title will land: a line across header and rows.
    Rectangle {
        objectName: "historyColumnDropGuide"
        visible: header.dragKey.length > 0 && header.dragTo !== header.dragFrom
        x: header.dropGuideX()
        y: 0
        width: 2
        height: header.height + header.bodyHeight
        z: 3
        color: Theme.eorzea ? Theme.gold : Theme.accent
    }

    // The new right edge of the column being resized, across header and rows.
    Rectangle {
        objectName: "historyColumnResizeGuide"
        readonly property var slot: header.resizeKey.length > 0 ? header.layout[header.resizeKey] : null
        visible: slot !== null && slot !== undefined
        x: slot ? Math.round(content.x + header.columnsOrigin + slot.x + slot.width) - 1 : 0
        y: 0
        width: 2
        height: header.height + header.bodyHeight
        z: 3
        color: Theme.eorzea ? Theme.gold : Theme.accent
    }

    Rectangle {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        color: Theme.eorzea ? Theme.gold3 : Theme.border
    }
}
