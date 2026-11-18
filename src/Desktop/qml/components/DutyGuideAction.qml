import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

Action {
    property var run: ({})
    readonly property var community: Runtime.app && typeof Runtime.app.dutyCommunity !== "undefined"
                                     ? Runtime.app.dutyCommunity : null
    enabled: !!community
    text: (community && community.hasGuide(run) ? qsTr("查看攻略 ↗") : qsTr("打开攻略目录 ↗"))
          + " · " + qsTr("新大陆见闻录")
    onTriggered: if (community) community.openGuide(run)
}
