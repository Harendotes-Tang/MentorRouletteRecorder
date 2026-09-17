import QtQuick
import QtQuick.Controls
import MentorRecorder
import "Lucide.js" as Lucide

// Time-of-day entry: typed digits (2130, 21:30, 9:5) are written as HH:mm, or
// the hour and minute are picked behind the clock icon. `text` stays the plain
// string the run form validates (HH:mm, HH:mm:ss or HH:mm:ss.zzz).
StyledTextField {
    id: field

    /// True when the text is empty or a time the run form accepts.
    readonly property bool valid: text.trim().length === 0 || field.parse(text) !== null
    readonly property alias pickerOpen: popup.visible
    readonly property real pixelRatio: Screen.devicePixelRatio > 0 ? Screen.devicePixelRatio : 1

    placeholderText: qsTr("时:分")
    leftPadding: 8
    rightPadding: 28
    inputMethodHints: Qt.ImhPreferNumbers
    color: field.valid ? Theme.textPrimary : Theme.red

    /// "H:m", "HH:mm", "HH:mm:ss" or "HH:mm:ss.zzz" -> {hour, minute, second, msec}, else null.
    function parse(value) {
        const match = /^(\d{1,2}):(\d{1,2})(?::(\d{1,2})(?:\.(\d{1,3}))?)?$/.exec(value.trim())
        if (!match)
            return null
        const hour = Number(match[1]), minute = Number(match[2])
        const second = match[3] === undefined ? 0 : Number(match[3])
        if (hour > 23 || minute > 59 || second > 59)
            return null
        return { hour: hour, minute: minute, second: second,
                 msec: match[4] === undefined ? 0 : Number(match[4].padEnd(3, "0")) }
    }

    function pad(number) {
        return (number < 10 ? "0" : "") + number
    }

    /// Bare digits are punctuated as they are typed: 21 -> 21:, 2130 -> 21:30,
    /// 930 -> 09:30, 213045 -> 21:30:45; an already punctuated 21:3045 gains its
    /// second colon. Other text containing a colon is left alone.
    function completeDigits() {
        const raw = field.text.trim()
        const seconds = /^(\d{2}):(\d{2})(\d{1,2})$/.exec(raw)
        if (seconds) {
            field.text = seconds[1] + ":" + seconds[2] + ":" + seconds[3]
            field.cursorPosition = field.text.length
            return
        }
        if (!/^\d+$/.test(raw))
            return
        let next = raw
        if (raw.length === 2 && Number(raw) <= 23)
            next = raw + ":"
        else if (raw.length === 3)
            next = "0" + raw[0] + ":" + raw.substr(1)
        else if (raw.length === 4)
            next = raw.substr(0, 2) + ":" + raw.substr(2)
        else if (raw.length === 6)
            next = raw.substr(0, 2) + ":" + raw.substr(2, 2) + ":" + raw.substr(4)
        if (next !== raw) {
            field.text = next
            field.cursorPosition = next.length
        }
    }

    /// 9:5 -> 09:05 once the user leaves the field; invalid text stays as typed (and red).
    function canonicalize() {
        const parsed = field.parse(field.text)
        if (!parsed)
            return
        const seconds = parsed.second > 0 || parsed.msec > 0 ? ":" + pad(parsed.second) : ""
        const msec = parsed.msec > 0 ? "." + String(parsed.msec).padStart(3, "0") : ""
        field.text = pad(parsed.hour) + ":" + pad(parsed.minute) + seconds + msec
    }

    function select(hour, minute) {
        field.text = pad(hour) + ":" + pad(minute)
        popup.close()
    }

    function selectNow() {
        const now = new Date()
        field.text = pad(now.getHours()) + ":" + pad(now.getMinutes()) + ":" + pad(now.getSeconds())
        popup.close()
    }

    function openPicker() {
        const parsed = field.parse(field.text)
        const now = new Date()
        hourWheel.currentIndex = parsed ? parsed.hour : now.getHours()
        minuteWheel.currentIndex = parsed ? parsed.minute : now.getMinutes()
        popup.open()
    }

    onTextEdited: completeDigits()
    onEditingFinished: canonicalize()

    Image {
        id: clockIcon
        objectName: "clockIcon"
        anchors.right: parent.right
        anchors.rightMargin: 8
        anchors.verticalCenter: parent.verticalCenter
        width: 14
        height: 14
        sourceSize: Qt.size(Math.round(14 * field.pixelRatio), Math.round(14 * field.pixelRatio))
        source: Lucide.source("clock", clockHover.hovered || popup.visible ? Theme.textPrimary : Theme.textSecondary)
        fillMode: Image.PreserveAspectFit
        smooth: true

        HoverHandler { id: clockHover; cursorShape: Qt.PointingHandCursor }
        TapHandler { onTapped: popup.visible ? popup.close() : field.openPicker() }
    }

    Popup {
        id: popup
        objectName: "timePopup"
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
            spacing: 6

            Row {
                spacing: 2

                component Wheel: Tumbler {
                    id: wheel
                    width: 44
                    height: 120
                    visibleItemCount: 5
                    wrap: true
                    delegate: Text {
                        required property var modelData
                        required property int index
                        text: field.pad(modelData)
                        color: Theme.textPrimary
                        font.pixelSize: Theme.fs(index === wheel.currentIndex ? 15 : 12)
                        font.weight: index === wheel.currentIndex ? Font.DemiBold : Font.Normal
                        font.family: Theme.figureFamily
                        font.features: ({ "tnum": 1 })
                        opacity: 1.0 - Math.abs(Tumbler.displacement) / (wheel.visibleItemCount / 2)
                        horizontalAlignment: Text.AlignHCenter
                        verticalAlignment: Text.AlignVCenter
                    }
                }

                Wheel { id: hourWheel; objectName: "hourWheel"; model: 24 }
                Text {
                    height: 120
                    text: ":"
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(15)
                    verticalAlignment: Text.AlignVCenter
                }
                Wheel { id: minuteWheel; objectName: "minuteWheel"; model: 60 }
            }

            Row {
                spacing: 6
                AppButton {
                    objectName: "timeNowButton"
                    compact: true
                    variant: "ghost"
                    text: qsTr("现在")
                    onClicked: field.selectNow()
                }
                AppButton {
                    objectName: "timePickButton"
                    compact: true
                    variant: "primary"
                    text: qsTr("确定")
                    onClicked: field.select(hourWheel.currentIndex, minuteWheel.currentIndex)
                }
            }
        }
    }
}
