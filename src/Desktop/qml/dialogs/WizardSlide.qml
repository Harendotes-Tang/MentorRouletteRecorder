import QtQuick

// Offset only the step body; the wizard controller owns progress and direction.
Translate {
    property real progress: 1
    property int direction: 1
    x: (1 - progress) * 16 * direction
}
