import QtQuick

// 分享图片专用题头：度量当前字体后依次简化日期、省略角色名。
// 服务器与未知来源时间保留全文，极端长值允许增高，不写回任何记录字段。
Item {
    id: heading
    property bool single: false
    property string gameIdentity: ""
    property string countLabel: ""
    property var dateOptions: []
    readonly property real textBaseline: single ? 30 : 34
    readonly property real firstLineCenter: textBaseline
        + prefixMetrics.tightBoundingRect.y + prefixMetrics.tightBoundingRect.height / 2
    readonly property string identity: gameIdentity.trim()
    readonly property int at: identity.lastIndexOf("@")
    readonly property string characterName: at >= 0 ? identity.slice(0, at).trim() : identity
    readonly property string serverName: at >= 0 ? identity.slice(at).trim() : ""
    readonly property real identityGap: characterName.length && serverName.length ? 2 : 0
    readonly property real separatorWidth: Math.ceil(separatorMetrics.advanceWidth) + 28
    readonly property real prefixWidth: single ? Math.ceil(prefixMetrics.advanceWidth) + 1 : 0
    readonly property real countWidth: countLabel.length ? Math.ceil(countMetrics.advanceWidth) + 1 : 0
    readonly property real nameNaturalWidth: characterName.length ? Math.ceil(nameMetrics.advanceWidth) + 1 : 0
    readonly property real worldNaturalWidth: serverName.length ? Math.ceil(worldMetrics.advanceWidth) + 1 : 0
    readonly property real dateNaturalWidth: dateLabel.length ? Math.ceil(dateMetrics.advanceWidth) + 1 : 0
    readonly property real identityNaturalWidth: nameNaturalWidth + identityGap + worldNaturalWidth
    readonly property string dateLabel: {
        if (!dateOptions.length) return ""
        for (let i = 0; i < dateOptions.length; ++i)
            if (fullWidth(dateOptions[i]) <= width) return dateOptions[i]
        return dateOptions[dateOptions.length - 1]
    }
    readonly property real availableWidth: Math.max(1, width
        - prefixWidth - countWidth
        - Math.max(0, groups(dateLabel) - 1) * separatorWidth)
    readonly property real minimumNameWidth: Math.min(nameNaturalWidth, nameFont.font.pixelSize * 3)
    // 来源时间无法省年份时，先为日期和服务器各留可读的换行宽度。
    // 不把完整来源时间先扣掉，避免普通服务器被挤成每行一个字。
    readonly property real minimumDateWidth: Math.min(dateNaturalWidth,
        Math.max(date.font.pixelSize * 6, availableWidth * 0.45))
    readonly property real worldWidth: Math.min(worldNaturalWidth,
        Math.max(1, availableWidth - minimumNameWidth - identityGap - minimumDateWidth))
    readonly property real reservedDateWidth: Math.min(dateNaturalWidth,
        Math.max(1, availableWidth - minimumNameWidth - identityGap - worldWidth))
    readonly property real nameWidth: Math.min(nameNaturalWidth,
        Math.max(0, availableWidth - reservedDateWidth - identityGap - worldWidth))
    readonly property real dateWidth: Math.min(dateNaturalWidth,
        Math.max(1, availableWidth - nameWidth - identityGap - worldWidth))
    height: (single ? 36 : 40) + Math.max(0, world.lineCount - 1, date.lineCount - 1) * 30

    function groups(date) {
        return (single ? 1 : 0) + (identity.length ? 1 : 0)
            + (countLabel.length ? 1 : 0) + (date.length ? 1 : 0)
    }
    function otherWidth(date) {
        const count = groups(date) - (identity.length ? 1 : 0)
        return prefixWidth + countWidth + (date.length ? Math.ceil(dateFontMetrics.advanceWidth(date)) + 1 : 0)
            + Math.max(0, count - 1) * separatorWidth
    }
    function fullWidth(date) {
        return otherWidth(date) + (identity.length ? identityNaturalWidth + separatorWidth : 0)
    }
    // 按字形墨迹而非排版框对齐；宋体回退的粗体和常规字形留白不同。
    // baselineOffset 始终指向首行，多行服务器/时间继续向下增长。
    function centeredTextY(item, metrics) {
        const ink = metrics.tightBoundingRect
        return firstLineCenter - item.baselineOffset - ink.y - ink.height / 2
    }

    // 用相同字体对象度量并绘制，字距不随缩略策略估算。
    TextMetrics { id: prefixMetrics; text: qsTr("导随记录"); font: prefix.font }
    TextMetrics { id: nameMetrics; text: heading.characterName; font: nameFont.font }
    TextMetrics { id: nameElideMetrics; text: heading.characterName; font: nameFont.font
        elide: Text.ElideRight; elideWidth: heading.nameWidth }
    TextMetrics { id: nameInkMetrics; text: nameElideMetrics.elidedText.split("\n")[0]; font: nameFont.font }
    TextMetrics { id: worldMetrics; text: heading.serverName; font: world.font }
    TextMetrics { id: worldInkMetrics; text: heading.serverName.split("\n")[0]; font: world.font }
    TextMetrics { id: countMetrics; text: heading.countLabel; font: count.font }
    TextMetrics { id: dateMetrics; text: heading.dateLabel; font: date.font }
    TextMetrics { id: dateInkMetrics; text: heading.dateLabel.split("\n")[0]; font: date.font }
    FontMetrics { id: dateFontMetrics; font: date.font }
    TextMetrics { id: separatorMetrics; text: "·"; font: date.font }
    // 不参与布局，仅提供角色名的统一字体。
    Text {
        id: nameFont
        visible: false
        font { family: "Noto Serif SC"; pixelSize: heading.single ? 22 : 24; bold: true; letterSpacing: heading.single ? 1 : 1.5 }
    }
    Row {
        id: row
        Text {
            id: prefix
            objectName: "reflectionShareEyebrow"
            visible: heading.single
            width: heading.prefixWidth
            y: heading.centeredTextY(prefix, prefixMetrics)
            text: qsTr("导随记录"); textFormat: Text.PlainText; color: "#45342A"
            font { family: "Noto Serif SC"; pixelSize: 24; bold: true; letterSpacing: 6 }
        }
        Text {
            id: prefixSeparator
            visible: heading.single && heading.identity.length > 0
            y: heading.centeredTextY(prefixSeparator, separatorMetrics); width: heading.separatorWidth
            text: "·"; horizontalAlignment: Text.AlignHCenter; color: "#6E5A44"; font: date.font
        }
        Item {
            id: identityItem
            objectName: "reflectionShareGameIdentity"
            visible: heading.identity.length > 0
            width: heading.nameWidth + heading.identityGap + heading.worldWidth
            height: Math.max(name.y + name.height, world.visible ? world.y + world.height : 0)
            Text {
                id: name
                objectName: "reflectionShareCharacterName"
                y: heading.centeredTextY(name, nameInkMetrics)
                width: heading.nameWidth
                text: heading.characterName; textFormat: Text.PlainText; elide: Text.ElideRight
                color: "#45342A"; font: nameFont.font
            }
            Text {
                id: world
                objectName: "reflectionShareServerName"
                x: name.width + heading.identityGap; y: heading.centeredTextY(world, worldInkMetrics)
                width: heading.worldWidth; visible: heading.serverName.length > 0
                text: heading.serverName; textFormat: Text.PlainText; wrapMode: Text.WrapAnywhere
                color: "#6E5A44"
                font { family: "Noto Serif SC"; pixelSize: heading.single ? 20 : 22; letterSpacing: 1 }
                lineHeightMode: Text.FixedHeight; lineHeight: 30
            }
        }
        Text {
            id: identitySeparator
            visible: heading.countLabel.length > 0 && heading.identity.length > 0
            y: heading.centeredTextY(identitySeparator, separatorMetrics); width: heading.separatorWidth
            text: "·"; horizontalAlignment: Text.AlignHCenter; color: "#6E5A44"; font: date.font
        }
        Text {
            id: count
            objectName: "reflectionShareCount"
            visible: heading.countLabel.length > 0
            width: heading.countWidth
            y: heading.centeredTextY(count, countMetrics)
            text: heading.countLabel; textFormat: Text.PlainText; color: "#6E5A44"; font: date.font
        }
        Text {
            id: dateSeparator
            visible: heading.dateLabel.length > 0 && (heading.single || heading.identity.length > 0 || heading.countLabel.length > 0)
            y: heading.centeredTextY(dateSeparator, separatorMetrics); width: heading.separatorWidth
            text: "·"; horizontalAlignment: Text.AlignHCenter; color: "#6E5A44"; font: date.font
        }
        Text {
            id: date
            objectName: "reflectionShareTimeText"
            visible: heading.dateLabel.length > 0
            y: heading.centeredTextY(date, dateInkMetrics); width: heading.dateWidth
            text: heading.dateLabel; textFormat: Text.PlainText; wrapMode: Text.Wrap
            color: "#6E5A44"
            font { family: "Noto Serif SC"; pixelSize: heading.single ? 22 : 24; letterSpacing: heading.single ? 1 : 1.5 }
            lineHeightMode: Text.FixedHeight; lineHeight: 30
        }
    }
}
