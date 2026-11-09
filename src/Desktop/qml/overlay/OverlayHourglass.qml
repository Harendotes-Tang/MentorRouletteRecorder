import QtQuick
import MentorRecorder 1.0
import "../components/Lucide.js" as Lucide

// The bundled Lucide hourglass keeps the float's existing box and turn cycle.
// `sand` remains the cycle phase for callers and the turn begins after each wait.
Item {
    id: root

    property color color: Theme.textSecondary
    property real size: 14
    /// The caller decides when someone can see the sand run.
    property bool running: false

    /// Cycle phase from 0 to 1, retained for the existing animation observers.
    property real sand: 0.5
    /// The glass's turn in degrees, 0 to 180.
    property real turn: 0

    implicitWidth: Math.ceil(size * 1.06)
    implicitHeight: implicitWidth

    onRunningChanged: if (!running) { sand = 0.5; turn = 0 }

    // Keep the 3 s phase, short rest and 0.6 s turn: about 3.9 s a round.
    SequentialAnimation {
        running: root.running
        loops: Animation.Infinite
        NumberAnimation { target: root; property: "sand"; from: 0; to: 1; duration: 3000 }
        PauseAnimation { duration: 300 }
        NumberAnimation { target: root; property: "turn"; from: 0; to: 180; duration: 600; easing.type: Easing.InOutCubic }
        ScriptAction { script: { root.sand = 0; root.turn = 0 } }
    }

    Image {
        anchors.centerIn: parent
        width: root.size
        height: root.size
        rotation: root.turn
        source: Lucide.source("hourglass", root.color)
        sourceSize: Qt.size(Math.ceil(width * Screen.devicePixelRatio),
                            Math.ceil(height * Screen.devicePixelRatio))
        fillMode: Image.PreserveAspectFit
        Accessible.ignored: true
    }
}
