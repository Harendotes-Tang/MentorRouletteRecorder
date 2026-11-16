pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// The prototype's `.dz`: a ring of shares beside its legend. `items` is the
// legend from top to bottom and the ring clockwise from 12 o'clock; an item
// that counts nothing is folded into one faded row under the others. Hovering
// a segment or a legend row, or moving keyboard focus onto a row, fades the
// rest and puts that item's count and share in the middle; a linkable item
// asks its owner to open it (`activated`).
//
// Each item: { key, label, color, count, linkable, quiet, role }. `quiet` marks
// the neutral remainder, which is never faded; `role` (a role group, optional)
// puts the game's role glyph before the label. Shares are of the items' own
// sum, so the owner decides what the denominator is by what it passes.
GridLayout {
    id: root

    property var items: []
    /// Under the total in the middle while nothing is picked out.
    property string caption: ""
    /// Shown in the legend when the items count nothing at all.
    property string emptyText: ""
    /// A remark under the legend, while there is something to show.
    property string note: ""
    /// The ring's accessible name leads with it.
    property string title: ""
    /// Ring above the legend instead of beside it.
    property bool stacked: false

    signal activated(string key)

    readonly property real total: {
        let sum = 0
        for (const item of items)
            sum += Math.max(0, Number(item.count || 0))
        return sum
    }
    readonly property var shownItems: items.filter(item => Number(item.count || 0) > 0)
    readonly property var zeroItems: total > 0 ? items.filter(item => !(Number(item.count || 0) > 0)) : []
    readonly property bool linkable: shownItems.some(item => item.linkable === true)

    // What is picked out: the pointer on the ring, the pointer on a row, or the
    // row holding keyboard focus, in that order.
    property string ringKey: ""
    property string rowKey: ""
    property string focusKey: ""
    readonly property string hotKey: ringKey !== "" ? ringKey : rowKey !== "" ? rowKey : focusKey
    readonly property var hotItem: {
        if (hotKey === "")
            return null
        for (const item of shownItems) {
            if (item.key === hotKey)
                return item
        }
        return null
    }

    // The prototype's ring: a 148 px box, the stroke centred on r = 65, 18 px
    // wide and 23 px on the item picked out, with 2 px of card between
    // neighbours. A sliver keeps at least 1.2 px so it never vanishes.
    readonly property real ringSize: 148
    readonly property real ringRadius: 65
    readonly property real ringWidth: 18
    readonly property real ringWidthHot: 23
    readonly property real ringGap: 2
    readonly property var segments: {
        const circumference = Math.PI * 2 * ringRadius
        const gap = shownItems.length > 1 ? ringGap : 0
        const out = []
        let from = 0
        for (const item of shownItems) {
            const share = Number(item.count) / total
            const length = share * circumference
            const visible = Math.max(length - gap, 1.2)
            out.push({
                key: item.key, color: item.color, quiet: item.quiet === true, linkable: item.linkable === true,
                from: from, to: from + share,
                startAngle: (from * circumference + (length - visible) / 2) / ringRadius,
                sweepAngle: visible / ringRadius
            })
            from += share
        }
        return out
    }

    function shareText(count) {
        return total > 0 ? (Number(count) / total * 100).toFixed(1) + "%" : "—"
    }

    function countText(count) {
        return qsTr("%1 次").arg(Number(count || 0))
    }

    // The item whose segment lies under a point of the ring box, or "".
    function keyAt(x, y) {
        const dx = x - ringSize / 2
        const dy = y - ringSize / 2
        const offset = Math.abs(Math.sqrt(dx * dx + dy * dy) - ringRadius)
        if (offset > ringWidthHot / 2)
            return ""
        let turn = Math.atan2(dx, -dy) / (Math.PI * 2)
        if (turn < 0)
            turn += 1
        for (const segment of segments) {
            if (turn < segment.from || turn >= segment.to)
                continue
            // Only the item picked out is drawn at the wider stroke.
            return offset <= (segment.key === hotKey ? ringWidthHot : ringWidth) / 2 ? segment.key : ""
        }
        return ""
    }

    function activate(key) {
        for (const item of shownItems) {
            if (item.key === key && item.linkable === true) {
                root.activated(key)
                return
            }
        }
    }

    // The prototype's dzIn: the ring turns and grows into place when its page
    // is entered. The owner calls this; nothing moves with motion reduced.
    function playEntrance() {
        if (Theme.motion && root.total > 0)
            entrance.restart()
    }

    // A picked item that the data no longer shows must not stay picked.
    onShownItemsChanged: {
        if (ringKey !== "" && !shownItems.some(item => item.key === ringKey))
            ringKey = ""
        if (rowKey !== "" && !shownItems.some(item => item.key === rowKey))
            rowKey = ""
        if (focusKey !== "" && !shownItems.some(item => item.key === focusKey))
            focusKey = ""
    }

    columns: stacked ? 1 : 2
    columnSpacing: 16
    rowSpacing: 14

    Item {
        id: ring
        objectName: "shareDonutRing"
        Layout.preferredWidth: root.ringSize
        Layout.preferredHeight: root.ringSize
        Layout.alignment: root.stacked ? Qt.AlignHCenter : Qt.AlignVCenter

        Accessible.role: Accessible.Graphic
        Accessible.name: {
            const parts = root.shownItems.map(item => item.label + " " + root.countText(item.count)
                                                      + " " + root.shareText(item.count))
            return root.title + (parts.length > 0 ? qsTr("：") + parts.join(qsTr("；")) : "")
        }

        Item {
            id: ringArt
            anchors.fill: parent

            ParallelAnimation {
                id: entrance
                NumberAnimation { target: ringArt; property: "opacity"; from: 0; to: 1; duration: 500; easing.type: Easing.OutCubic }
                NumberAnimation { target: ringArt; property: "rotation"; from: -24; to: 0; duration: 500; easing.type: Easing.OutCubic }
                NumberAnimation { target: ringArt; property: "scale"; from: 0.92; to: 1; duration: 500; easing.type: Easing.OutCubic }
            }

            // Nothing counted: the bare track, not a full segment of anything.
            Canvas {
                id: track
                objectName: "shareDonutTrack"
                anchors.fill: parent
                visible: root.total <= 0
                readonly property color trackColor: Theme.dutyOther
                onTrackColorChanged: requestPaint()
                onVisibleChanged: requestPaint()
                onPaint: {
                    const ctx = getContext("2d")
                    ctx.reset()
                    ctx.lineWidth = root.ringWidth
                    ctx.strokeStyle = trackColor
                    ctx.beginPath()
                    ctx.arc(width / 2, height / 2, root.ringRadius, 0, Math.PI * 2)
                    ctx.stroke()
                }
            }

            Repeater {
                model: root.segments
                delegate: Canvas {
                    id: segment
                    required property var modelData
                    required property int index
                    readonly property bool hot: root.hotKey === modelData.key
                    readonly property bool dimmed: root.hotKey !== "" && !hot && !modelData.quiet
                    readonly property color tone: modelData.color
                    property real strokeWidth: hot ? root.ringWidthHot : root.ringWidth

                    objectName: "shareDonutSegment" + index
                    // Room for the wider stroke of the item picked out.
                    readonly property real bleed: (root.ringWidthHot - root.ringWidth) / 2 + 1
                    x: -bleed
                    y: -bleed
                    width: root.ringSize + bleed * 2
                    height: root.ringSize + bleed * 2
                    opacity: dimmed ? 0.28 : 1

                    Behavior on opacity { NumberAnimation { duration: Theme.motion ? 180 : 0 } }
                    Behavior on strokeWidth { NumberAnimation { duration: Theme.motion ? 180 : 0 } }
                    onStrokeWidthChanged: requestPaint()

                    onPaint: {
                        const ctx = getContext("2d")
                        ctx.reset()
                        ctx.lineWidth = strokeWidth
                        ctx.lineCap = "butt"
                        ctx.strokeStyle = tone
                        const start = modelData.startAngle - Math.PI / 2
                        ctx.beginPath()
                        ctx.arc(width / 2, height / 2, root.ringRadius, start, start + modelData.sweepAngle)
                        ctx.stroke()
                    }
                }
            }
        }

        HoverHandler {
            id: ringHover
            cursorShape: root.hotItem && root.ringKey !== "" && root.hotItem.linkable === true
                         ? Qt.PointingHandCursor : Qt.ArrowCursor
            onPointChanged: root.ringKey = hovered ? root.keyAt(point.position.x, point.position.y) : ""
            onHoveredChanged: if (!hovered) root.ringKey = ""
        }

        TapHandler {
            onTapped: function(eventPoint) {
                root.activate(root.keyAt(eventPoint.position.x, eventPoint.position.y))
            }
        }

        // `.dz-center`: the total, or the item picked out with its share.
        Column {
            anchors.centerIn: parent
            width: root.ringSize - 56
            spacing: 2

            Text {
                objectName: "shareDonutNumber"
                width: parent.width
                text: String(root.hotItem ? Number(root.hotItem.count) : root.total)
                color: Theme.eorzea ? Theme.gold2 : Theme.textPrimary
                font.family: Theme.numFamily
                font.pixelSize: 30
                font.weight: Theme.figureWeight(true)
                font.features: ({ "tnum": 1 })
                fontSizeMode: Text.HorizontalFit
                minimumPixelSize: 16
                horizontalAlignment: Text.AlignHCenter
            }

            Text {
                objectName: "shareDonutCaption"
                width: parent.width
                text: root.hotItem ? root.hotItem.label : root.caption
                textFormat: Text.PlainText
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                elide: Text.ElideRight
                horizontalAlignment: Text.AlignHCenter
            }

            Text {
                objectName: "shareDonutShare"
                width: parent.width
                visible: root.hotItem !== null
                text: root.hotItem ? root.shareText(root.hotItem.count) : ""
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                font.family: Theme.figureFamily
                font.weight: Theme.figureWeight(false)
                font.features: ({ "tnum": 1 })
                horizontalAlignment: Text.AlignHCenter
            }
        }
    }

    ColumnLayout {
        id: legend
        objectName: "shareDonutLegend"
        Layout.fillWidth: true
        Layout.minimumWidth: 0
        Layout.alignment: Qt.AlignVCenter
        spacing: 1

        Repeater {
            model: root.shownItems
            delegate: Rectangle {
                id: legendRow
                required property var modelData
                required property int index
                readonly property string key: modelData.key
                readonly property bool linkable: modelData.linkable === true
                readonly property bool hot: root.hotKey === key

                objectName: "shareDonutRow" + index
                Layout.fillWidth: true
                Layout.minimumWidth: 0
                implicitHeight: Math.max(28, rowContent.implicitHeight + 8)
                radius: Theme.radiusS
                color: hot ? Theme.fill : Theme.clear(Theme.fill)

                activeFocusOnTab: true
                Accessible.role: linkable ? Accessible.Button : Accessible.StaticText
                Accessible.name: modelData.label + " " + root.countText(modelData.count)
                                 + " " + root.shareText(modelData.count)
                Accessible.description: linkable ? qsTr("在历史记录中查看") : ""
                Accessible.onPressAction: root.activate(key)
                Keys.onSpacePressed: root.activate(key)
                Keys.onReturnPressed: root.activate(key)
                Keys.onEnterPressed: root.activate(key)
                onActiveFocusChanged: {
                    if (activeFocus)
                        root.focusKey = key
                    else if (root.focusKey === key)
                        root.focusKey = ""
                }
                // New data rebuilds the rows. A row that goes while it holds the
                // focus or the pointer is told neither, so it lets go here.
                Component.onDestruction: {
                    if (root.focusKey === key)
                        root.focusKey = ""
                    if (root.rowKey === key)
                        root.rowKey = ""
                }

                HoverHandler {
                    cursorShape: legendRow.linkable ? Qt.PointingHandCursor : Qt.ArrowCursor
                    onHoveredChanged: {
                        if (hovered)
                            root.rowKey = legendRow.key
                        else if (root.rowKey === legendRow.key)
                            root.rowKey = ""
                    }
                }

                TapHandler { onTapped: root.activate(legendRow.key) }

                // `.dz-row:focus-visible`: the ring sits inside the row.
                Rectangle {
                    anchors.fill: parent
                    radius: legendRow.radius
                    color: "transparent"
                    border.width: 2
                    border.color: Theme.eorzea ? Theme.gold2 : Theme.accent
                    visible: legendRow.activeFocus
                }

                RowLayout {
                    id: rowContent
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.verticalCenter: parent.verticalCenter
                    anchors.leftMargin: 8
                    anchors.rightMargin: 8
                    spacing: 9

                    Rectangle {
                        objectName: "shareDonutSwatch" + legendRow.index
                        Layout.preferredWidth: 10
                        Layout.preferredHeight: 10
                        radius: Theme.radiusXxs
                        color: legendRow.modelData.color
                    }

                    RoleIcon {
                        objectName: "shareDonutRole" + legendRow.index
                        Layout.fillWidth: false
                        visible: legendRow.modelData.role !== undefined
                        role: legendRow.modelData.role || ""
                        size: 20
                    }

                    Text {
                        objectName: "shareDonutLabel" + legendRow.index
                        Layout.fillWidth: true
                        Layout.minimumWidth: 0
                        text: legendRow.modelData.label
                        textFormat: Text.PlainText
                        wrapMode: Text.Wrap
                        maximumLineCount: 2
                        elide: Text.ElideRight
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                    }

                    Text {
                        objectName: "shareDonutCount" + legendRow.index
                        text: root.countText(legendRow.modelData.count)
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(12)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(true)
                        font.features: ({ "tnum": 1 })
                        horizontalAlignment: Text.AlignRight
                    }

                    Text {
                        objectName: "shareDonutShare" + legendRow.index
                        Layout.preferredWidth: 46
                        text: root.shareText(legendRow.modelData.count)
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(12)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(false)
                        font.features: ({ "tnum": 1 })
                        horizontalAlignment: Text.AlignRight
                    }
                }
            }
        }

        // `.dz-zero`: everything that counted nothing, in one faded row. Its
        // count stays in the count column while the names fit on one line; a
        // longer list takes the share column too and wraps.
        RowLayout {
            id: zeroRow
            objectName: "shareDonutZeroRow"
            readonly property bool aligned: zeroMetrics.advanceWidth
                <= legend.width - 16 - 10 - spacing * 3 - zeroCount.implicitWidth - 46
            Layout.fillWidth: true
            Layout.minimumWidth: 0
            Layout.minimumHeight: 28
            Layout.leftMargin: 8
            Layout.rightMargin: 8
            visible: root.zeroItems.length > 0
            spacing: 9

            Accessible.role: Accessible.StaticText
            Accessible.name: zeroLabel.text + " " + root.countText(0)

            Rectangle {
                Layout.preferredWidth: 10
                Layout.preferredHeight: 10
                Layout.alignment: zeroLabel.lineCount > 1 ? Qt.AlignTop : Qt.AlignVCenter
                Layout.topMargin: zeroLabel.lineCount > 1 ? 4 : 0
                radius: Theme.radiusXxs
                color: "transparent"
                border.width: 1
                border.color: Theme.neutral500
            }

            Text {
                id: zeroLabel
                objectName: "shareDonutZeroLabel"
                Layout.fillWidth: true
                Layout.minimumWidth: 0
                text: root.zeroItems.map(item => item.label).join(" · ")
                textFormat: Text.PlainText
                wrapMode: Text.Wrap
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
            }

            TextMetrics {
                id: zeroMetrics
                font: zeroLabel.font
                text: zeroLabel.text
            }

            Text {
                id: zeroCount
                Layout.alignment: zeroLabel.lineCount > 1 ? Qt.AlignTop : Qt.AlignVCenter
                text: root.countText(0)
                color: Theme.textSecondary
                font.pixelSize: Theme.fs(12)
                font.family: Theme.figureFamily
                font.weight: Theme.figureWeight(false)
                font.features: ({ "tnum": 1 })
                horizontalAlignment: Text.AlignRight
            }

            // The share column of the rows above, left empty.
            Item {
                Layout.preferredWidth: 46
                visible: zeroRow.aligned
            }
        }

        Text {
            objectName: "shareDonutEmpty"
            Layout.fillWidth: true
            Layout.leftMargin: 8
            Layout.rightMargin: 8
            visible: root.total <= 0 && text.length > 0
            text: root.emptyText
            textFormat: Text.PlainText
            wrapMode: Text.WordWrap
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            horizontalAlignment: root.stacked ? Text.AlignHCenter : Text.AlignLeft
        }

        Text {
            objectName: "shareDonutNote"
            Layout.fillWidth: true
            Layout.topMargin: 6
            Layout.leftMargin: 8
            Layout.rightMargin: 8
            visible: root.total > 0 && text.length > 0
            text: root.note
            textFormat: Text.PlainText
            wrapMode: Text.WordWrap
            color: Theme.textSecondary
            font.pixelSize: Theme.fs(11)
            lineHeight: 1.3
        }
    }
}
