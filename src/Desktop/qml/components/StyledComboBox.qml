import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

ComboBox {
    id: control

    // Popup rows show `job_id`'s framed glyph and its role icon; the closed
    // field keeps the plain text.
    property bool showJobIcons: false
    // Optional group headers in the popup: the model role that groups the rows
    // (rows of one group must be adjacent) and the header text for a value.
    property string sectionRole: ""
    property var sectionLabel: function(value) { return value }

    focusPolicy: Qt.TabFocus
    implicitHeight: Theme.eorzea ? 32 : 30

    delegate: ItemDelegate {
        id: row

        required property var model
        required property int index

        width: ListView.view ? ListView.view.width : control.width
        height: 34
        highlighted: control.highlightedIndex === index

        background: Rectangle {
            radius: Theme.radiusS
            color: row.highlighted ? Theme.fill : "transparent"
        }

        contentItem: RowLayout {
            spacing: 7

            JobIcon {
                visible: control.showJobIcons
                jobId: row.model.job_id !== undefined ? row.model.job_id : null
                size: 20
            }

            Text {
                Layout.fillWidth: true
                text: control.textRole.length > 0 ? (row.model[control.textRole] || "")
                                                  : String(row.model.modelData || "")
                color: Theme.textPrimary
                font.pixelSize: Theme.fs(13)
                verticalAlignment: Text.AlignVCenter
                elide: Text.ElideRight
            }

            RoleIcon {
                visible: control.showJobIcons
                role: Jobs.roleGroup(row.model.job_id !== undefined ? row.model.job_id : null)
                size: 18
            }
        }
    }

    background: Rectangle {
        radius: Theme.radiusS
        color: Theme.eorzea
               ? (Theme.dark ? "#47000000" : "#8cffffff")
               : (control.enabled ? Theme.surface : Theme.fill)
        border.width: 1
        border.color: control.activeFocus
                      ? (Theme.eorzea ? Theme.gold : Theme.accent)
                      : (control.hovered && control.enabled
                         ? Theme.neutral400
                         : (Theme.eorzea ? Theme.border : Theme.neutral300))
        // `.input:hover:not(:focus)`: neutral-400 in both styles, faded on --dur.
        Behavior on border.color { ColorAnimation { duration: Theme.motionControl } }

        // workbench `.input:focus-visible`: 3 px accent-100 ring outside the frame.
        Rectangle {
            anchors.fill: parent
            anchors.margins: -3
            radius: parent.radius + 3
            visible: !Theme.eorzea && control.activeFocus
            color: "transparent"
            border.width: 3
            border.color: Theme.accentMuted
        }
    }

    contentItem: Text {
        leftPadding: 10
        rightPadding: 24
        text: control.displayText
        color: Theme.textPrimary
        verticalAlignment: Text.AlignVCenter
        elide: Text.ElideRight
        font.pixelSize: Theme.fs(13)
    }

    // The prototype draws the caret with two CSS gradients. It is painted here
    // because a glyph would depend on the fonts installed on the machine.
    indicator: Canvas {
        id: caret

        width: 10
        height: 6
        anchors.right: parent.right
        anchors.rightMargin: 11
        anchors.verticalCenter: parent.verticalCenter

        Connections {
            target: Theme
            function onDarkChanged() { caret.requestPaint() }
            function onEorzeaChanged() { caret.requestPaint() }
        }

        onPaint: {
            const ctx = getContext("2d")
            ctx.reset()
            ctx.fillStyle = Theme.eorzea ? Theme.gold : Theme.textSecondary
            ctx.beginPath()
            ctx.moveTo(0, 0)
            ctx.lineTo(width, 0)
            ctx.lineTo(width / 2, height)
            ctx.closePath()
            ctx.fill()
        }
    }

    popup: Popup {
        id: popup

        // 列表不设高度上限时会超出窗口，下半部分既无法查看也无法点击；
        // 封顶后其余选项由滚动条访问。
        readonly property int maxHeight: 340
        readonly property real topOnScreen: control.mapToItem(null, 0, 0).y
        readonly property real windowHeight: control.Window.height > 0 ? control.Window.height : 800
        // 下方空间不足而上方足够时向上弹出，避免挤在窗口底边。
        readonly property bool above:
            popup.topOnScreen + control.height + 4 + popup.implicitHeight > popup.windowHeight - 8
            && popup.topOnScreen > popup.implicitHeight + 8

        y: popup.above ? -popup.implicitHeight - 4 : control.height + 4
        // At least the field's width, wider when an option needs it, so the
        // narrow filter boxes (86 px) never elide an option.
        width: Math.max(control.width, popup.widestOption)
        implicitHeight: Math.min(contentItem.implicitHeight + 8, popup.maxHeight)
        padding: 4

        property real widestOption: 0
        onAboutToShow: {
            let widest = 0
            for (let index = 0; index < control.count; ++index) {
                optionMetrics.text = control.textAt(index)
                widest = Math.max(widest, optionMetrics.width)
            }
            // Row padding, the popup's own padding and the scroll bar's lane;
            // the job glyphs when the rows carry them.
            popup.widestOption = Math.ceil(widest) + 8 + 16 + 12 + (control.showJobIcons ? 20 + 18 + 14 : 0)
        }
        TextMetrics {
            id: optionMetrics
            font.pixelSize: Theme.fs(13)
        }

        contentItem: ListView {
            clip: true
            implicitHeight: contentHeight
            model: control.popup.visible ? control.delegateModel : null
            currentIndex: control.highlightedIndex
            // 打开时将当前项滚入视野，否则选中项可能位于高度上限之外。
            onVisibleChanged: if (visible && currentIndex >= 0) positionViewAtIndex(currentIndex, ListView.Contain)
            ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
            // Only a box that names a sectionRole gets headers; an empty
            // property sections a string-list model on the row text itself,
            // giving every option its own heading.
            section.property: control.sectionRole
            section.criteria: ViewSection.FullString
            section.delegate: control.sectionRole.length > 0 ? sectionHeader : null

            Component {
                id: sectionHeader

                Text {
                    required property string section

                    objectName: "comboSectionHeader"
                    width: ListView.view ? ListView.view.width : control.width
                    topPadding: 6
                    bottomPadding: 3
                    leftPadding: 8
                    text: control.sectionLabel(section)
                    color: Theme.textMuted
                    font.pixelSize: Theme.fs(11)
                    font.weight: Font.DemiBold
                    elide: Text.ElideRight
                }
            }
        }

        background: Rectangle {
            radius: Theme.radiusS
            color: Theme.surface
            border.width: 1
            border.color: Theme.eorzea ? Theme.gold3 : Theme.border
        }
    }
}
