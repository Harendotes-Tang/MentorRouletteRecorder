import QtQuick
import QtQuick.Controls
import MentorRecorder 1.0

// Opens the duty's guide in the browser, or the site's list of duties when the duty is unknown
// or has no page. The site follows the interface language (新大陆见闻录, or Console Games Wiki in
// English and Japanese). `tip` always names the site; `text` does only with showSource. A text
// button marks the link with the external-link icon (DutyGuideButton), not an arrow in the text.
Action {
    property var run: ({})
    property bool showSource: true
    readonly property var community: Runtime.app && typeof Runtime.app.dutyCommunity !== "undefined"
                                     ? Runtime.app.dutyCommunity : null
    readonly property bool direct: !!community && community.hasGuide(run)
    // Called from bindings that also call qsTr(), which a language switch re-evaluates. The Chinese
    // site's name is written in the interface's script (繁體中文: 新大陸見聞錄); the wiki's is not translated.
    function siteName() {
        const name = community ? community.guideSiteName() : ""
        return name === "新大陆见闻录" ? qsTr("新大陆见闻录") : name
    }
    readonly property string tip: !community ? ""
        : (direct ? qsTr("在浏览器打开「%1」上这个副本的攻略") : qsTr("在浏览器打开「%1」的攻略目录")).arg(siteName())
    enabled: !!community
    text: (direct ? qsTr("查看攻略") : qsTr("攻略目录"))
          + (showSource && community ? " · " + siteName() : "")
    onTriggered: if (community) community.openGuide(run)
}
