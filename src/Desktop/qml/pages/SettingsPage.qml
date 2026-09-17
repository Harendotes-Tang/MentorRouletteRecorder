import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder

// ---------------------------------------------------------------------------
// 设置
//
// A 168 px sub-navigation (通用 / 播报 / 成就 / 数据 / 关于) beside one scrolling
// column of panels at most 760 px wide - the prototype's
// `grid-template-columns:168px minmax(0,760px)`
// (DOC/表单提交后设计/mentor-recorder-ff14.dc.html, data-screen-label="设置").
//
// Every tab is created once and only hidden, so the objectNames the start-up
// self-check and the tests look for (generalSettingsCard, appearanceSettingsCard,
// uiStyleSettingControl, logRetentionField, copyrightCard …) always exist.
// ---------------------------------------------------------------------------
Item {
    id: page

    signal openBaselineRequested()
    signal openDisclosureRequested()

    // Remembered for the session: Main.qml creates the page once and only hides
    // it. --settings-tab opens a given tab for a screenshot.
    property string currentTab: (typeof ForceSettingsTab !== "undefined" && ForceSettingsTab)
                                ? ForceSettingsTab : "general"
    readonly property var tabs: [
        { id: "general", label: qsTr("通用"), sub: qsTr("启动 · 记录 · 外观") },
        { id: "tts", label: qsTr("播报"), sub: qsTr("语音与模板") },
        { id: "goal", label: qsTr("成就"), sub: qsTr("目标与基数") },
        { id: "data", label: qsTr("数据"), sub: qsTr("数据库与备份") },
        { id: "about", label: qsTr("关于"), sub: qsTr("版权 · 隐私边界") }
    ]
    readonly property int navWidth: 168
    readonly property int contentMaxWidth: 760

    function selectTab(id) {
        if (page.currentTab === id)
            return
        page.currentTab = id
        scroll.contentY = 0
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 20

        PageHeader {
            title: qsTr("设置")
            subtitle: qsTr("所有数据仅保存在本机 · 无遥测 · 无云同步")
        }

        RowLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            spacing: 24

            ColumnLayout {
                objectName: "settingsNav"
                Layout.preferredWidth: page.navWidth
                Layout.maximumWidth: page.navWidth
                Layout.alignment: Qt.AlignTop
                spacing: 2

                Repeater {
                    model: page.tabs

                    delegate: NavItem {
                        required property var modelData

                        objectName: "settingsTab_" + modelData.id
                        Layout.fillWidth: true
                        label: modelData.label
                        sub: modelData.sub
                        current: page.currentTab === modelData.id
                        onClicked: page.selectTab(modelData.id)
                    }
                }
            }

            Flickable {
                id: scroll

                objectName: "settingsScroll"
                Layout.fillWidth: true
                Layout.fillHeight: true
                contentWidth: width
                contentHeight: column.implicitHeight
                clip: true
                boundsBehavior: Flickable.StopAtBounds
                ScrollBar.vertical: ScrollBar {
                    policy: scroll.contentHeight > scroll.height ? ScrollBar.AsNeeded
                                                                 : ScrollBar.AlwaysOff
                }

                ColumnLayout {
                    id: column

                    width: Math.min(page.contentMaxWidth, scroll.width - Theme.scrollGutter)
                    spacing: 16

                    SettingsGeneralTab {
                        objectName: "settingsGeneralTab"
                        visible: page.currentTab === "general"
                    }
                    SettingsSpeechTab {
                        objectName: "settingsSpeechTab"
                        visible: page.currentTab === "tts"
                    }
                    SettingsGoalTab {
                        objectName: "settingsGoalTab"
                        visible: page.currentTab === "goal"
                        onOpenBaselineRequested: page.openBaselineRequested()
                    }
                    SettingsDataTab {
                        objectName: "settingsDataTab"
                        visible: page.currentTab === "data"
                    }
                    SettingsAboutTab {
                        objectName: "settingsAboutTab"
                        visible: page.currentTab === "about"
                        onOpenDisclosureRequested: page.openDisclosureRequested()
                    }
                }
            }
        }
    }
}
