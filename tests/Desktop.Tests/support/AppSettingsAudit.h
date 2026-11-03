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
// While set, a native write or removal is recorded but not applied and status() reports
// AccessError: what security software guarding the Run key does to the real one.
inline bool &nativeSettingsRefuseWrites()
{
    static bool refuse = false;
    return refuse;
}
// While set, a native write or removal is recorded and silently dropped with status() still
// NoError: software that claims success and blocks the change anyway.
inline bool &nativeSettingsDropWrites()
{
    static bool drop = false;
    return drop;
}
class IsolatedSettings
{
public:
    using Format = QSettings::Format;
    static constexpr Format NativeFormat = QSettings::NativeFormat;
    static constexpr Format IniFormat = QSettings::IniFormat;
    using Status = QSettings::Status;
    static constexpr Status NoError = QSettings::NoError;
    static constexpr Status AccessError = QSettings::AccessError;

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
    QStringList allKeys() const { return m_settings.allKeys(); }
    void sync() { m_settings.sync(); }
    Status status() const { return m_refused ? AccessError : m_settings.status(); }
    void setValue(QAnyStringView key, const QVariant &value)
    {
        if (m_format == NativeFormat)
            nativeSettingsAccesses().append(QJsonObject{{"operation", "set"}, {"key", key.toString()},
                                                        {"value", value.toString()}, {"target", m_requested}});
        if (refuses())
            return;
        m_settings.setValue(key, value);
    }
    void remove(QAnyStringView key)
    {
        if (m_format == NativeFormat)
            nativeSettingsAccesses().append(QJsonObject{{"operation", "remove"}, {"key", key.toString()}});
        if (refuses())
            return;
        m_settings.remove(key);
    }
private:
    static QString isolatedFilePath(Format format)
    {
        Q_ASSERT(!settingsAuditRoot().isEmpty());
        return QDir(settingsAuditRoot()).filePath(format == NativeFormat
            ? QStringLiteral("native.ini") : QStringLiteral("desktop.ini"));
    }
    bool refuses()
    {
        if (m_format != NativeFormat)
            return false;
        if (nativeSettingsDropWrites())
            return true;
        if (!nativeSettingsRefuseWrites())
            return false;
        m_refused = true;
        return true;
    }
    QSettings m_settings;
    QString m_requested;
    Format m_format;
    bool m_refused = false;
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
