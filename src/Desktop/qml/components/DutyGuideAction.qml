import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

// Opens the duty's guide on 新大陆见闻录 in the browser, or the site's directory when the duty
// is unknown or has no page. `tip` always names the site; `text` does only with showSource.
Action {
    property var run: ({})
    property bool showSource: true
    readonly property var community: Runtime.app && typeof Runtime.app.dutyCommunity !== "undefined"
                                     ? Runtime.app.dutyCommunity : null
    readonly property bool direct: !!community && community.hasGuide(run)
    readonly property string tip: direct ? qsTr("在浏览器打开「新大陆见闻录」上这个副本的攻略")
                                         : qsTr("在浏览器打开「新大陆见闻录」的攻略目录")
    enabled: !!community
    text: (direct ? qsTr("查看攻略") : qsTr("攻略目录"))
          + (showSource ? " · " + qsTr("新大陆见闻录") : "") + " ↗"
    onTriggered: if (community) community.openGuide(run)
}
