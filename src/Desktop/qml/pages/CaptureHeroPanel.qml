import QtQuick
import QtQuick.Layouts
import MentorRecorder 1.0

// The hero card of 捕获诊断: one sentence on what the capture is doing (a LED,
// a headline, a line under it, the actions for that state), then 链路 - the
// summary line and the four nodes FF14 进程 → Npcap → 适配器 → 协议档案, each
// with a tone-coloured rule on top, a value and a sub line.
// 降级模式 lives here too: with Npcap missing the hero is the explanation, the
// four install steps on the right and 打开 npcap.com / 重新检测.
Card {
    id: hero
    objectName: "captureHeroPanel"

    readonly property CaptureChainModel chain: CaptureChainModel {}
    readonly property CaptureHeroCopy copy: CaptureHeroCopy { chain: hero.chain }
    // The page header's badge reads these.
    readonly property string badgeText: hero.copy.badge
    readonly property string badgeTone: hero.copy.tone
    readonly property bool broken: hero.copy.kind === "broken"

    horizontalPadding: 20
    verticalPadding: 16
    spacing: 12

    // -- dg-hero: LED + headline + text | install steps | actions ------------
    RowLayout {
        Layout.fillWidth: true
        spacing: 24

        RowLayout {
            Layout.fillWidth: true
            Layout.horizontalStretchFactor: 3
            Layout.alignment: Qt.AlignTop
            spacing: 10

            StatusLed {
                Layout.alignment: Qt.AlignTop
                Layout.topMargin: 6
                tone: hero.copy.tone
            }

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 2

                HeadingLabel {
                    objectName: "captureHeroHeadline"
                    Layout.fillWidth: true
                    text: Theme.live(hero.copy.headline())
                    font.pixelSize: Theme.eorzea ? 17 : 15
                    wrapMode: Text.WordWrap
                }

                Text {
                    objectName: "captureHeroText"
                    Layout.fillWidth: true
                    visible: text.length > 0
                    // Collector sentences (hint, install hint) are built in C++ or
                    // through Fmt.collectorText: live() re-reads them on a switch.
                    text: Theme.live(hero.copy.text())
                    textFormat: Text.PlainText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(13)
                    lineHeight: 1.25
                    wrapMode: Text.WordWrap
                }

                Text {
                    objectName: "captureHeroDetail"
                    Layout.fillWidth: true
                    Layout.topMargin: 2
                    visible: text.length > 0
                    text: hero.copy.detail()
                    textFormat: Text.PlainText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    font.family: Theme.figureFamily
                    font.weight: Theme.figureWeight(false)
                    font.features: ({ "tnum": 1 })
                    wrapMode: Text.WordWrap
                }
            }
        }

        // 降级模式: the four install steps, in the design's short sentences.
        ColumnLayout {
            objectName: "captureNpcapPanel"
            visible: hero.broken
            Layout.fillWidth: true
            Layout.horizontalStretchFactor: 2
            Layout.maximumWidth: 380
            Layout.alignment: Qt.AlignTop
            spacing: 4

            Repeater {
                model: [
                    qsTr("从 npcap.com 下载安装包（需要管理员权限）。"),
                    qsTr("勾选 “Install Npcap in WinPcap API-compatible Mode”。"),
                    qsTr("不要勾选 “Restrict … to Administrators only”。"),
                    qsTr("安装完成后点“重新检测”；已登录的话回到标题画面重新登录一次，不用重启游戏。")
                ]

                delegate: RowLayout {
                    id: installStep
                    required property string modelData
                    required property int index

                    Layout.fillWidth: true
                    spacing: 8

                    Text {
                        Layout.alignment: Qt.AlignTop
                        Layout.preferredWidth: 14
                        text: String(installStep.index + 1) + "."
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                        font.family: Theme.figureFamily
                        font.weight: Theme.figureWeight(false)
                        font.features: ({ "tnum": 1 })
                    }

                    Text {
                        Layout.fillWidth: true
                        text: installStep.modelData
                        textFormat: Text.PlainText
                        color: Theme.textSecondary
                        font.pixelSize: Theme.fs(13)
                        lineHeight: 1.25
                        wrapMode: Text.WordWrap
                    }
                }
            }
        }

        RowLayout {
            Layout.alignment: Qt.AlignTop
            spacing: 8

            AppButton {
                objectName: "openNpcapButton"
                visible: hero.broken
                text: qsTr("打开 npcap.com")
                variant: "primary"
                onClicked: Runtime.app.openNpcapWebsite()
            }

            AppButton {
                objectName: "redetectNpcapButton"
                visible: hero.broken
                text: qsTr("重新检测")
                onClicked: Runtime.app.rescanAdapters()
            }

            AppButton {
                objectName: "rescanGameButton"
                visible: !hero.broken
                variant: "ghost"
                text: qsTr("重新检测游戏")
                onClicked: Runtime.app.rescanGame()
            }

            AppButton {
                objectName: "rescanAdaptersButton"
                visible: !hero.broken
                variant: "ghost"
                text: qsTr("重新检测网卡")
                onClicked: Runtime.app.rescanAdapters()
            }
        }
    }

    // -- dg-chain: the kicker and the summary over a divider ----------------
    Rectangle {
        Layout.fillWidth: true
        Layout.preferredHeight: 1
        color: Theme.border
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 12

        CardKicker {
            Layout.alignment: Qt.AlignTop
            Layout.fillWidth: false
            text: qsTr("连接状态")
        }

        Text {
            objectName: "captureChainSummary"
            Layout.fillWidth: true
            Layout.alignment: Qt.AlignVCenter
            text: hero.chain.summaryLine[0]
            textFormat: Text.PlainText
            color: hero.chain.summaryLine[1] === "bad" ? Theme.orangeText : Theme.textSecondary
            font.pixelSize: Theme.fs(12)
            wrapMode: Text.WordWrap
        }
    }

    // -- dg-nodes: four equal columns, each under a 2 px rule in its tone -----
    RowLayout {
        Layout.fillWidth: true
        spacing: 16

        Repeater {
            model: hero.chain.nodes

            delegate: ColumnLayout {
                id: node
                required property var modelData

                objectName: "captureChain_" + modelData.key
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                Layout.alignment: Qt.AlignTop
                spacing: 0

                Rectangle {
                    Layout.fillWidth: true
                    Layout.preferredHeight: 2
                    color: Theme.toneColor(node.modelData.tone)
                    Behavior on color { ColorAnimation { duration: Theme.motionControl } }
                }

                RowLayout {
                    Layout.fillWidth: true
                    Layout.topMargin: 10
                    spacing: 8

                    StatusLed {
                        Layout.alignment: Qt.AlignVCenter
                        tone: node.modelData.tone
                    }

                    Text {
                        Layout.fillWidth: true
                        text: node.modelData.label
                        textFormat: Text.PlainText
                        color: Theme.eorzea ? Theme.textSecondary : Theme.textMuted
                        font.pixelSize: Theme.fs(11)
                        elide: Text.ElideRight
                    }
                }

                Text {
                    objectName: "captureChainValue_" + node.modelData.key
                    Layout.fillWidth: true
                    Layout.topMargin: 6
                    text: node.modelData.value
                    textFormat: Text.PlainText
                    color: node.modelData.tone === "bad" ? Theme.red
                           : node.modelData.tone === "warn" ? Theme.orangeText
                           : node.modelData.tone === "off" ? Theme.textSecondary
                           : Theme.textPrimary
                    font.family: Theme.figureFamily
                    font.pixelSize: Theme.fs(16)
                    font.weight: Theme.figureWeight(true)
                    font.features: ({ "tnum": 1 })
                    elide: Text.ElideRight
                }

                Text {
                    objectName: "captureChainSub_" + node.modelData.key
                    Layout.fillWidth: true
                    Layout.topMargin: 2
                    text: node.modelData.sub
                    textFormat: Text.PlainText
                    color: Theme.textSecondary
                    font.pixelSize: Theme.fs(12)
                    wrapMode: Text.WordWrap
                }
            }
        }
    }
}
