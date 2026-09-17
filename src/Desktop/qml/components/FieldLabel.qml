import QtQuick
import MentorRecorder

// `.field > label`: t6 at regular weight in the classic style.
Text {
    color: Theme.textSecondary
    font.pixelSize: Theme.eorzea ? 12 : 11
    font.weight: Theme.eorzea ? Font.Bold : Font.Normal
    wrapMode: Text.WordWrap
}
