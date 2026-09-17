import QtQuick
import MentorRecorder

// h1..h4 of the prototype: Cinzel for Latin and numbers, Noto Serif SC for
// Chinese, gold; the classic style falls back to the body font.
Text {
    id: root

    color: Theme.headingColor
    font.family: Theme.headingFamilyFor(text)
    font.pixelSize: Theme.fs(20)
    font.bold: true
    // A constant, not `font.pixelSize * 0.04`: reading the size back out of the
    // same font group is a binding loop.
    font.letterSpacing: Theme.eorzea ? 0.9 : 0
    lineHeight: 1.15
    lineHeightMode: Text.ProportionalHeight
}
