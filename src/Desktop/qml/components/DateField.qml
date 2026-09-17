import QtQuick
import QtQuick.Controls
import MentorRecorder
import "Lucide.js" as Lucide

// A date input matching the prototype's <input type="date">: shows 年/月/日 until
// it holds a date, accepts a typed yyyy-MM-dd, and the calendar icon on its right
// opens a month grid. `text` is always the yyyy-MM-dd string, or "" when empty,
// so callers can treat it as a plain text field.
StyledTextField {
    id: field

    /// True when the text is empty or a real calendar date.
    readonly property bool valid: text.length === 0 || field.parse(text) !== null
    /// The month grid's visible page, opened on the field's own date or today.
    readonly property alias calendarOpen: popup.visible
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1

    placeholderText: qsTr("年/月/日")
    leftPadding: 8
    rightPadding: 28
    color: field.valid ? Theme.textPrimary : Theme.red

    /// yyyy-MM-dd -> Date, or null when the text is not a real date.
    function parse(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.trim())
        if (!match)
            return null
        const year = Number(match[1]), month = Number(match[2]), day = Number(match[3])
        const date = new Date(year, month - 1, day)
        if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day)
            return null
        return date
    }

    /// Writes `date` into the field the way the user would have typed it.
    function select(date) {
        field.text = Qt.formatDate(date, "yyyy-MM-dd")
        popup.close()
    }

    function openCalendar() {
        const shown = field.parse(field.text) || new Date()
        grid.year = shown.getFullYear()
        grid.month = shown.getMonth()
        popup.open()
    }

    function shiftMonth(delta) {
        const shown = new Date(grid.year, grid.month + delta, 1)
        grid.year = shown.getFullYear()
        grid.month = shown.getMonth()
    }

    Image {
        id: calendarIcon
        objectName: "calendarIcon"
        anchors.right: parent.right
        anchors.rightMargin: 8
        anchors.verticalCenter: parent.verticalCenter
        width: 14
        height: 14
        sourceSize: Qt.size(Math.round(14 * field.pixelRatio), Math.round(14 * field.pixelRatio))
        source: Lucide.source("calendar", calendarTap.hovered || popup.visible ? Theme.textPrimary : Theme.textSecondary)
        fillMode: Image.PreserveAspectFit
        smooth: true

        HoverHandler { id: calendarTap; cursorShape: Qt.PointingHandCursor }
        TapHandler {
            onTapped: popup.visible ? popup.close() : field.openCalendar()
        }
    }

    Popup {
        id: popup
        objectName: "calendarPopup"
        y: field.height + 4
        padding: 8
        modal: false
        closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutsideParent

        background: Rectangle {
            radius: Theme.radiusS
            color: Theme.surface
            border.width: 1
            border.color: Theme.border
        }

        contentItem: Column {
            spacing: 4

            Row {
                width: grid.width
                height: 28

                AppButton {
                    objectName: "calendarPrevMonth"
                    variant: "ghost"
                    compact: true
                    width: 28
                    height: 28
                    iconName: "chevron-left"
                    onClicked: field.shiftMonth(-1)
                }
                Text {
                    objectName: "calendarMonthLabel"
                    width: parent.width - 56
                    height: 28
                    text: qsTr("%1 年 %2 月").arg(grid.year).arg(grid.month + 1)
                    color: Theme.textPrimary
                    font.pixelSize: Theme.fs(12)
                    font.weight: Font.DemiBold
                    font.family: Theme.figureFamily
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment: Text.AlignVCenter
                }
                AppButton {
                    objectName: "calendarNextMonth"
                    variant: "ghost"
                    compact: true
                    width: 28
                    height: 28
                    iconName: "chevron-right"
                    onClicked: field.shiftMonth(1)
                }
            }

            DayOfWeekRow {
                width: grid.width
                locale: grid.locale
                delegate: Text {
                    required property var model
                    text: model.narrowName
                    color: Theme.textMuted
                    font.pixelSize: Theme.fs(11)
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment: Text.AlignVCenter
                    height: 20
                }
            }

            MonthGrid {
                id: grid
                objectName: "calendarGrid"
                locale: Qt.locale("zh_CN")
                spacing: 2

                delegate: Rectangle {
                    id: day
                    required property var model

                    readonly property bool inMonth: model.month === grid.month
                    readonly property bool selected: {
                        const chosen = field.parse(field.text)
                        return chosen !== null && inMonth && model.day === chosen.getDate()
                               && model.year === chosen.getFullYear()
                    }

                    objectName: inMonth ? "calendarDay" + model.day : "calendarOtherDay"
                    width: 30
                    height: 26
                    radius: Theme.radiusS
                    color: selected ? Theme.accent : (dayHover.hovered ? Theme.fill : "transparent")
                    border.width: model.today && !selected ? 1 : 0
                    border.color: Theme.accent

                    Text {
                        anchors.centerIn: parent
                        text: model.day
                        color: day.selected ? "#ffffff" : (day.inMonth ? Theme.textPrimary : Theme.textMuted)
                        font.pixelSize: Theme.fs(12)
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                    }

                    HoverHandler { id: dayHover; cursorShape: Qt.PointingHandCursor }
                    TapHandler { onTapped: field.select(day.model.date) }
                }
            }
        }
    }
}
