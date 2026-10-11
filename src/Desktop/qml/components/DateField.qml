import QtQuick
import QtQuick.Controls
import MentorRecorder
import "Lucide.js" as Lucide

// Single-date callers retain text/valid. Range callers share this calendar and
// expose the two editable dates; an unfinished mouse range is never committed.
StyledTextField {
    id: field
    property bool rangeMode: false
    // Restriction is opt-in for history; the new-record wizard stays unrestricted.
    property bool restrictToRecords: false
    property var availableDays: []
    property var knownMonths: []
    property bool availabilityLoading: false
    property string availabilityError: ""
    readonly property string shownMonth: String(grid.year).padStart(4, "0") + "-" + String(grid.month + 1).padStart(2, "0")
    signal calendarRequested(string month)
    signal endpointRequested(string date)
    function endpointAvailable(value) {
        return !restrictToRecords || value.length === 0 || availableDays.indexOf(value.trim()) >= 0
    }
    function endpointUnavailable(value) {
        return restrictToRecords && parse(value) !== null
            && knownMonths.indexOf(value.trim().slice(0, 7)) >= 0 && !endpointAvailable(value)
    }
    onShownMonthChanged: { if (popup.visible && restrictToRecords) calendarRequested(shownMonth) }
    property bool selectionPending: false
    property bool monthView: false
    property string savedStart: ""
    property string savedEnd: ""
    property alias startField: fromInput
    property alias endField: toInput
    property alias startText: fromInput.text
    property alias endText: toInput.text
    readonly property bool valid: rangeMode ? fromInput.valid && toInput.valid
                                           : text.length === 0 || parse(text) !== null
    readonly property alias calendarOpen: popup.visible
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1
    readonly property var years: Array.from({length: 9900}, (_, i) => String(i + 100))

    placeholderText: rangeMode ? qsTr("日期范围") : qsTr("年/月/日")
    readOnly: rangeMode
    leftPadding: 8
    rightPadding: 28
    color: valid ? Theme.textPrimary : Theme.red
    Binding {
        target: field
        property: "text"
        when: field.rangeMode
        value: fromInput.text.length || toInput.text.length
               ? (fromInput.text || qsTr("开始")) + " — " + (toInput.text || qsTr("结束")) : ""
    }

    function parse(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.trim())
        if (!match) return null
        const year = Number(match[1]), month = Number(match[2]), day = Number(match[3])
        const date = new Date(year, month - 1, day)
        if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day)
            return null
        return date
    }
    function cancelSelection() {
        if (!selectionPending) return
        fromInput.text = savedStart
        toInput.text = savedEnd
        selectionPending = false
    }
    function select(date) {
        const chosen = Qt.formatDate(date, "yyyy-MM-dd")
        if (rangeMode && restrictToRecords && availableDays.indexOf(chosen) < 0) return
        if (!rangeMode) {
            text = chosen
            popup.close()
        } else if (!selectionPending) {
            savedStart = fromInput.text
            savedEnd = toInput.text
            selectionPending = true
            fromInput.text = chosen
            toInput.text = ""
        } else {
            const start = fromInput.text
            fromInput.text = start < chosen ? start : chosen
            toInput.text = start < chosen ? chosen : start
            selectionPending = false
            popup.close()
        }
    }
    function openCalendar() {
        const shown = parse(rangeMode ? fromInput.text : text) || new Date()
        grid.year = shown.getFullYear()
        grid.month = shown.getMonth()
        monthView = false
        popup.open()
    }
    function shiftMonth(delta) {
        const shown = new Date(grid.year, grid.month + delta, 1)
        if (shown.getFullYear() < 100 || shown.getFullYear() > 9999) return
        grid.year = shown.getFullYear()
        grid.month = shown.getMonth()
    }
    function navigate(delta) {
        if (monthView) grid.year = Math.max(100, Math.min(9999, grid.year + delta))
        else shiftMonth(delta)
    }
    Keys.onPressed: function(event) {
        if (event.key === Qt.Key_Down && (event.modifiers & Qt.AltModifier)
                || rangeMode && (event.key === Qt.Key_Return || event.key === Qt.Key_Enter || event.key === Qt.Key_Space)) {
            openCalendar()
            event.accepted = true
        }
    }
    TapHandler {
        enabled: field.rangeMode
        onTapped: popup.visible ? popup.close() : field.openCalendar()
    }
    Image {
        objectName: "calendarIcon"
        anchors.right: parent.right
        anchors.rightMargin: 8
        anchors.verticalCenter: parent.verticalCenter
        width: 14; height: 14
        sourceSize: Qt.size(Math.round(14 * field.pixelRatio), Math.round(14 * field.pixelRatio))
        source: Lucide.source("calendar", calendarHover.hovered || popup.visible ? Theme.textPrimary : Theme.textSecondary)
        fillMode: Image.PreserveAspectFit
        smooth: true
        HoverHandler { id: calendarHover; cursorShape: Qt.PointingHandCursor }
        TapHandler {
            enabled: !field.rangeMode
            onTapped: popup.visible ? popup.close() : field.openCalendar()
        }
    }
    Popup {
        id: popup
        objectName: "calendarPopup"
        y: field.height + 4
        padding: 12
        focus: true
        modal: false
        closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutsideParent
        onOpened: {
            monthTitle.forceActiveFocus()
            if (field.restrictToRecords) field.calendarRequested(field.shownMonth)
        }
        onClosed: {
            field.cancelSelection()
            field.forceActiveFocus()
        }
        background: Rectangle {
            radius: Theme.radiusS
            color: Theme.surface
            border.width: 1
            border.color: Theme.border
            // Accept popup background presses so history rows never receive them.
            TapHandler { acceptedButtons: Qt.AllButtons; gesturePolicy: TapHandler.ReleaseWithinBounds }
        }
        contentItem: Column {
            width: field.rangeMode ? 284 : 252
            spacing: 8
            WheelHandler {
                target: null
                onWheel: function(wheel) {
                    const delta = wheel.angleDelta.y || wheel.angleDelta.x
                    if (delta !== 0) field.navigate(delta > 0 ? -1 : 1)
                    wheel.accepted = true
                }
            }
            Row {
                visible: field.rangeMode
                spacing: 8
                Column {
                    spacing: 4
                    Text { text: qsTr("开始日期"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                    StyledTextField {
                        id: fromInput
                        objectName: "fromDateField"
                        width: 138
                        placeholderText: qsTr("开始 · 年/月/日")
                        readonly property bool valid: text.length === 0 || (field.parse(text) !== null && field.endpointAvailable(text))
                        color: field.parse(text) !== null || text.length === 0 ? (field.endpointUnavailable(text) ? Theme.red : Theme.textPrimary) : Theme.red
                        Accessible.name: qsTr("开始日期")
                        onTextEdited: field.selectionPending = false
                        onTextChanged: { if (field.restrictToRecords && field.parse(text) !== null) field.endpointRequested(text.trim()) }
                    }
                }
                Column {
                    spacing: 4
                    Text { text: qsTr("结束日期"); color: Theme.textSecondary; font.pixelSize: Theme.fs(12) }
                    StyledTextField {
                        id: toInput
                        objectName: "toDateField"
                        width: 138
                        placeholderText: qsTr("结束 · 年/月/日")
                        readonly property bool valid: text.length === 0 || (field.parse(text) !== null && field.endpointAvailable(text))
                        color: field.parse(text) !== null || text.length === 0 ? (field.endpointUnavailable(text) ? Theme.red : Theme.textPrimary) : Theme.red
                        Accessible.name: qsTr("结束日期")
                        onTextEdited: field.selectionPending = false
                        onTextChanged: { if (field.restrictToRecords && field.parse(text) !== null) field.endpointRequested(text.trim()) }
                    }
                }
            }
            Text {
                objectName: "calendarSelectionStatus"
                visible: field.rangeMode
                width: parent.width
                text: field.availabilityError.length ? qsTr("日期加载失败，请重试")
                    : field.restrictToRecords && field.availabilityLoading ? qsTr("正在加载有记录的日期…")
                    : field.restrictToRecords && field.knownMonths.indexOf(field.shownMonth) >= 0
                      && !field.availableDays.some(day => day.slice(0, 7) === field.shownMonth) ? qsTr("本月没有匹配记录")
                    : field.selectionPending ? qsTr("选择结束日期 · 同日再点一次选单日")
                    : qsTr("选择开始日期，再选择结束日期")
                color: field.availabilityError.length ? Theme.red : Theme.textSecondary
                font.pixelSize: Theme.fs(11)
                wrapMode: Text.Wrap
                Accessible.role: Accessible.StaticText
            }
            AppButton {
                objectName: "calendarRetry"
                visible: field.rangeMode && field.availabilityError.length > 0
                text: qsTr("重新加载")
                compact: true
                onClicked: field.calendarRequested(field.shownMonth)
            }
            Row {
                width: parent.width
                height: 30
                AppButton {
                    objectName: "calendarPrevMonth"
                    variant: "ghost"; compact: true
                    width: 30; height: 30
                    iconName: "chevron-left"
                    Accessible.name: field.monthView ? qsTr("上一年") : qsTr("上一月")
                    onClicked: field.navigate(-1)
                }
                Item {
                    width: parent.width - 60
                    height: 30
                    AppButton {
                        id: monthTitle
                        objectName: "calendarMonthLabel"
                        anchors.fill: parent
                        visible: !field.monthView
                        variant: "ghost"; compact: true
                        text: qsTr("%1 年 %2 月").arg(grid.year).arg(grid.month + 1)
                        contentItem: Item {
                            Row {
                                anchors.centerIn: parent
                                spacing: 6
                                Text {
                                    text: monthTitle.text
                                    color: monthTitle.foreground
                                    font.pixelSize: Theme.fs(11)
                                    font.weight: Theme.eorzea ? Font.Bold : Font.Normal
                                }
                                Image {
                                    objectName: "calendarMonthChevron"
                                    anchors.verticalCenter: parent.verticalCenter
                                    width: 12; height: 12
                                    sourceSize: Qt.size(Math.round(12 * field.pixelRatio), Math.round(12 * field.pixelRatio))
                                    // Rotate the project's bundled Lucide chevron; no new asset dependency.
                                    source: Lucide.source("chevron-right", monthTitle.foreground)
                                    rotation: 90
                                    Accessible.ignored: true
                                }
                            }
                        }
                        background: Rectangle {
                            radius: Theme.radiusS
                            color: monthTitle.hovered || monthTitle.down ? Theme.fill : "transparent"
                            border.width: monthTitle.visualFocus ? 1 : 0
                            border.color: Theme.accent
                        }
                        Accessible.name: qsTr("选择月份和年份")
                        onClicked: {
                            field.monthView = true
                            yearSelect.forceActiveFocus()
                        }
                    }
                    StyledComboBox {
                        id: yearSelect
                        objectName: "calendarYearSelect"
                        anchors.fill: parent
                        visible: field.monthView
                        model: field.years
                        currentIndex: grid.year - 100
                        Accessible.name: qsTr("选择年份")
                        delegate: ItemDelegate {
                            required property string modelData
                            required property int index
                            width: ListView.view ? ListView.view.width : yearSelect.width
                            height: 34
                            highlighted: yearSelect.highlightedIndex === index
                            text: modelData
                            font.pixelSize: Theme.fs(13)
                            contentItem: Text {
                                text: parent.text
                                color: Theme.textPrimary
                                font: parent.font
                                verticalAlignment: Text.AlignVCenter
                            }
                            background: Rectangle {
                                radius: Theme.radiusS
                                color: parent.highlighted ? Theme.fill : "transparent"
                            }
                        }
                        onActivated: grid.year = currentIndex + 100
                    }
                }
                AppButton {
                    objectName: "calendarNextMonth"
                    variant: "ghost"; compact: true
                    width: 30; height: 30
                    iconName: "chevron-right"
                    Accessible.name: field.monthView ? qsTr("下一年") : qsTr("下一月")
                    onClicked: field.navigate(1)
                }
            }
            Column {
                width: parent.width
                visible: !field.monthView
                spacing: 4
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
                    width: parent.width
                    height: 204
                    locale: Qt.locale("zh_CN")
                    spacing: 2
                    delegate: AppButton {
                        id: day
                        required property var model
                        readonly property bool inMonth: model.month === grid.month
                        enabled: model.year >= 100 && model.year <= 9999
                            && (!field.rangeMode || !field.restrictToRecords
                                || field.availableDays.indexOf(dateKey) >= 0)
                        readonly property string dateKey: Qt.formatDate(model.date, "yyyy-MM-dd")
                        readonly property bool selected: field.rangeMode
                            ? dateKey === fromInput.text || dateKey === toInput.text
                            : dateKey === field.text.trim()
                        readonly property bool inRange: field.rangeMode && fromInput.text.length > 0
                            && toInput.text.length > 0 && dateKey > fromInput.text && dateKey < toInput.text
                        objectName: inMonth ? "calendarDay" + model.day : "calendarOtherDay"
                        width: 34; height: 32
                        compact: true
                        text: String(model.day)
                        Accessible.name: dateKey + (!enabled ? qsTr(" 无匹配记录") : selected ? qsTr(" 已选") : "")
                        onClicked: field.select(model.date)
                        background: Rectangle {
                            radius: Theme.radiusS
                            color: day.selected ? Theme.accent : day.inRange ? Theme.accentMuted
                                   : day.hovered ? Theme.fill : "transparent"
                            border.width: day.visualFocus ? 2 : day.model.today && day.enabled && !day.selected ? 1 : 0
                            border.color: Theme.accent
                        }
                        contentItem: Text {
                            text: day.text
                            color: !day.enabled ? Theme.textMuted : day.selected ? "#ffffff" : day.inMonth ? Theme.textPrimary : Theme.textMuted
                            font.pixelSize: Theme.fs(12)
                            font.family: Theme.figureFamily
                            font.features: ({ "tnum": 1 })
                            horizontalAlignment: Text.AlignHCenter
                            verticalAlignment: Text.AlignVCenter
                        }
                    }
                }
            }
            Grid {
                objectName: "calendarMonths"
                visible: field.monthView
                width: parent.width
                columns: 3
                spacing: 6
                Repeater {
                    model: 12
                    AppButton {
                        required property int index
                        objectName: "calendarMonth" + (index + 1)
                        width: (parent.width - 12) / 3
                        height: 51
                        compact: true
                        text: qsTr("%1 月").arg(index + 1)
                        onClicked: {
                            grid.month = index
                            field.monthView = false
                            monthTitle.forceActiveFocus()
                        }
                    }
                }
            }
        }
    }
}
