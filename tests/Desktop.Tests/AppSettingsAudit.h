#pragma once
// Every requested native-settings access is recorded and redirected to a temporary INI.
// This header is force-included only in the isolated AppSettings regression target.
#include <QSettings>
#include <QStandardPaths>
#include <QDir>
#include <QJsonArray>
#include <QJsonObject>
inline QJsonArray &nativeSettingsAccesses()
{
    static QJsonArray accesses;
    return accesses;
}
inline QString &settingsAuditRoot()
{
    static QString root;
    return root;
}
class IsolatedSettings
{
public:
    using Format = QSettings::Format;
    static constexpr Format NativeFormat = QSettings::NativeFormat;
    static constexpr Format IniFormat = QSettings::IniFormat;

    IsolatedSettings(const QString &requested, Format format, QObject *parent = nullptr)
        : m_settings(isolatedFilePath(format), IniFormat, parent)
        , m_requested(requested), m_format(format)
    {
        if (format == NativeFormat)
            nativeSettingsAccesses().append(QJsonObject{{"operation", "open"}, {"target", requested}});
    }
    QString fileName() const { return m_settings.fileName(); }
    QVariant value(QAnyStringView key, const QVariant &fallback = {}) const
    { return m_settings.value(key, fallback); }
    bool contains(QAnyStringView key) const { return m_settings.contains(key); }
    void sync() { m_settings.sync(); }
    void setValue(QAnyStringView key, const QVariant &value)
    {
        if (m_format == NativeFormat)
            nativeSettingsAccesses().append(QJsonObject{{"operation", "set"}, {"key", key.toString()},
                                                        {"value", value.toString()}, {"target", m_requested}});
        m_settings.setValue(key, value);
    }
    void remove(QAnyStringView key)
    {
        if (m_format == NativeFormat)
            nativeSettingsAccesses().append(QJsonObject{{"operation", "remove"}, {"key", key.toString()}});
        m_settings.remove(key);
    }
private:
    static QString isolatedFilePath(Format format)
    {
        Q_ASSERT(!settingsAuditRoot().isEmpty());
        return QDir(settingsAuditRoot()).filePath(format == NativeFormat
            ? QStringLiteral("native.ini") : QStringLiteral("desktop.ini"));
    }
    QSettings m_settings;
    QString m_requested;
    Format m_format;
};
class IsolatedStandardPaths
{
public:
    using StandardLocation = QStandardPaths::StandardLocation;
    static constexpr StandardLocation GenericConfigLocation = QStandardPaths::GenericConfigLocation;

    static bool isTestModeEnabled() { return QStandardPaths::isTestModeEnabled(); }
    static void setTestModeEnabled(bool enabled) { QStandardPaths::setTestModeEnabled(enabled); }
    static QString writableLocation(StandardLocation)
    { return QDir(settingsAuditRoot()).filePath(QStringLiteral("config")); }
};
#define QSettings IsolatedSettings
#define QStandardPaths IsolatedStandardPaths
