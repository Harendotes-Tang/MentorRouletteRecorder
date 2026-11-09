#pragma once

#include <QDateTime>
#include <QJsonValue>
#include <QString>
#include <QTimeZone>

namespace mr {

/// 解析后端 ISO 时间；缺少时区时按 UTC 解释，显式偏移保持原值，非法输入返回无效时间。
/// 不转换为本地显示时间；需要本地显示的调用方自行调用 toLocalTime()。
inline QDateTime parseUtcTimestamp(const QString &text)
{
    QDateTime stamp = QDateTime::fromString(text, Qt::ISODateWithMs);
    if (!stamp.isValid())
        stamp = QDateTime::fromString(text, Qt::ISODate);
    if (stamp.isValid() && stamp.timeSpec() == Qt::LocalTime)
        stamp.setTimeZone(QTimeZone::UTC);
    return stamp;
}

/// JSON 契约只接受字符串，不将数字、布尔或 null 隐式转换为时间。
inline QDateTime parseUtcTimestamp(const QJsonValue &value)
{
    return value.isString() ? parseUtcTimestamp(value.toString()) : QDateTime();
}

} // namespace mr
