import QtQuick

// One cell of a history row (docs/ui-design.md §4.2), placed where the page's
// column layout puts its column. Header and rows read the same layout, so a
// title and its cells cannot drift apart; a hidden column has no slot and the
// cell disappears with it.
Item {
    id: cell

    required property string columnKey
    /// HistoryPage.columnLayout: { keys, available, <key>: { x, width, ... } }.
    required property var layout
    /// x of the first column: the row checkbox's 32 px.
    property real origin: 32
    readonly property var slot: layout[columnKey] || ({ x: 0, width: 0 })

    objectName: "historyCell_" + columnKey
    visible: slot.width > 0
    x: origin + slot.x
    width: slot.width
    anchors.top: parent ? parent.top : undefined
    anchors.bottom: parent ? parent.bottom : undefined
}
