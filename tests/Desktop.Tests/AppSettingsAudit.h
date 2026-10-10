#pragma once
// Every requested native-settings access is recorded and redirected to a temporary INI.
// This header is force-included only in the isolated AppSettings regression target.
#include <QSettings>
#include <QStandardPaths>
#include <QDir>
#include <QJsonArray>
#include <QJsonObject>
inline QJsonArray nativeSettingsAccesses;
inline QString settingsAuditRoot;
class IsolatedSettings : public QSettings
{
public:
    IsolatedSettings(const QString &requested, Format format, QObject *parent = nullptr)
        : QSettings(QDir(settingsAuditRoot).filePath(format == NativeFormat
              ? QStringLiteral("native.ini") : QStringLiteral("desktop.ini")), IniFormat, parent)
        , m_requested(requested), m_format(format)
    {
        Q_ASSERT(!settingsAuditRoot.isEmpty());
        if (format == NativeFormat)
            nativeSettingsAccesses.append(QJsonObject{{"operation", "open"}, {"target", requested}});
    }
    void setValue(QAnyStringView key, const QVariant &value)
    {
        if (m_format == NativeFormat)
            nativeSettingsAccesses.append(QJsonObject{{"operation", "set"}, {"key", key.toString()},
                                                      {"value", value.toString()}, {"target", m_requested}});
        QSettings::setValue(key, value);
    }
    void remove(QAnyStringView key)
    {
        if (m_format == NativeFormat)
            nativeSettingsAccesses.append(QJsonObject{{"operation", "remove"}, {"key", key.toString()}});
        QSettings::remove(key);
    }
private:
    QString m_requested;
    Format m_format;
};
class IsolatedStandardPaths : public QStandardPaths
{
public:
    static QString writableLocation(StandardLocation)
    { return QDir(settingsAuditRoot).filePath(QStringLiteral("config")); }
};
#define QSettings IsolatedSettings
#define QStandardPaths IsolatedStandardPaths
