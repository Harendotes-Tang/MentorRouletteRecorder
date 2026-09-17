import QtQuick
import MentorRecorder

// The recurring `background:var(--inset-bg);border:1px solid var(--inset-border)`
// well used for event cards, the mono status block and empty states.
Rectangle {
    radius: Theme.radiusS
    color: Theme.insetBackground
    border.width: Theme.eorzea ? 1 : 0
    border.color: Theme.insetBorder
}
