pragma ComponentBehavior: Bound

import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import MentorRecorder 1.0

// A draft page number only. Query/filter/selection state stays in RunListModel.
RowLayout {
    id: root

    property var paginationModel: null
    readonly property int currentPage: paginationModel ? paginationModel.page : 1
    readonly property int maximumPage: paginationModel ? Math.max(1, paginationModel.pageCount) : 1
    readonly property bool available: !!paginationModel && !paginationModel.loading
    readonly property real targetPage: Number(numberField.text)
    readonly property bool validPage: /^[0-9]+$/.test(numberField.text)
        && Number.isSafeInteger(targetPage) && targetPage >= 1 && targetPage <= maximumPage

    spacing: 6
    enabled: available

    function synchronize() {
        if (numberField)
            numberField.text = String(root.currentPage)
    }
    function jump() {
        if (!root.available || !root.validPage)
            return
        root.paginationModel.goToPage(root.targetPage)
        root.synchronize()
    }
    onCurrentPageChanged: synchronize()
    onMaximumPageChanged: synchronize()
    onPaginationModelChanged: synchronize()
    Component.onCompleted: synchronize()
    Connections {
        target: root.paginationModel
        // Filtering may keep both page and pageCount unchanged. Its committed
        // paging notification must still discard a draft from the old query.
        function onPagingChanged() { root.synchronize() }
    }

    StyledTextField {
        id: numberField
        objectName: "pageNumberField"
        Layout.preferredWidth: Math.max(48, Math.min(100, 20 + String(root.maximumPage).length * Theme.fs(8)))
        text: String(root.currentPage)
        horizontalAlignment: TextInput.AlignHCenter
        inputMethodHints: Qt.ImhDigitsOnly
        validator: IntValidator { bottom: 1; top: root.maximumPage }
        Accessible.name: qsTr("跳转页码")
        color: text.length === 0 || root.validPage ? Theme.textPrimary : Theme.red
        onAccepted: root.jump()
        ToolTip.visible: hovered || activeFocus
        ToolTip.delay: 500
        ToolTip.text: qsTr("输入 1～%1 页，按回车或点击跳转").arg(root.maximumPage)
    }
    Text {
        objectName: "pageNumberTotal"
        text: qsTr("/ %1 页").arg(root.maximumPage)
        color: Theme.textSecondary
        font.pixelSize: Theme.fs(12)
        font.family: Theme.figureFamily
        font.features: ({ "tnum": 1 })
    }
    AppButton {
        objectName: "pageNumberJumpButton"
        compact: true
        text: qsTr("跳转")
        enabled: root.validPage
        onClicked: root.jump()
    }
}
