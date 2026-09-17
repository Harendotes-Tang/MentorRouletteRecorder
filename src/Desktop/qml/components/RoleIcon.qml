import QtQuick
import QtQuick.Layouts
import MentorRecorder

// Framed FF14 role glyph (盾 / 十字 / 拳 / 箭 / 闪电 / 全能剪影).
//
// `role` uses the fine-grained Chinese group of the legend
// (坦克 / 治疗 / 近战 / 远程物理 / 魔法 / 未知); unmapped values fall back to the
// All-Rounder plate, so the box is never empty.
RowLayout {
    id: root

    property string role: ""
    property real size: 20
    property bool showLabel: false
    property string labelText: root.role.length > 0 ? root.role : qsTr("未知")
    property color labelColor: Theme.textPrimary
    property real labelPixelSize: 11
    property alias iconItem: glyph

    spacing: 6

    // Character and colour standing in for the glyph when no PNG is available
    // (the normal case in a distributed build).
    readonly property string badgeText: {
        const key = Roles.roleKey(root.role)
        if (key === "tank") return qsTr("坦")
        if (key === "healer") return qsTr("治")
        if (key === "melee") return qsTr("近")
        if (key === "ranged") return qsTr("远")
        if (key === "magic") return qsTr("魔")
        return qsTr("全")
    }
    readonly property color badgeColor: {
        const key = Roles.roleKey(root.role)
        if (key === "tank") return "#3d7bd6"
        if (key === "healer") return "#3aa457"
        if (key === "melee" || key === "ranged" || key === "magic") return "#c9433d"
        return Theme.eorzea ? Theme.gold3 : Theme.borderStrong
    }

    Rectangle {
        Layout.preferredWidth: root.size
        Layout.preferredHeight: root.size
        Layout.alignment: Qt.AlignVCenter
        visible: !glyph.visible
        radius: Theme.eorzea ? Theme.radiusXs : width / 2
        color: Qt.rgba(root.badgeColor.r, root.badgeColor.g, root.badgeColor.b, 0.18)
        border.width: 1
        border.color: root.badgeColor

        Text {
            anchors.centerIn: parent
            text: root.badgeText
            color: Theme.eorzea ? Theme.gold2 : root.badgeColor
            font.pixelSize: Math.max(9, root.size * 0.5)
            font.bold: true
        }
    }

    Image {
        id: glyph
        visible: source !== "" && status === Image.Ready

        Layout.preferredWidth: root.size
        Layout.preferredHeight: root.size
        Layout.alignment: Qt.AlignVCenter

        source: Roles.roleIconSource(root.role)
        // Decode the 64x64 PNGs at native size once; mipmaps handle the smaller
        // on-screen variants without re-decoding.
        sourceSize.width: 64
        sourceSize.height: 64
        fillMode: Image.PreserveAspectFit
        smooth: true
        mipmap: true
        asynchronous: false
    }

    Text {
        Layout.fillWidth: true
        Layout.alignment: Qt.AlignVCenter
        visible: root.showLabel
        text: root.labelText
        color: root.labelColor
        font.pixelSize: Theme.fs(root.labelPixelSize)
        elide: Text.ElideRight
    }
}
