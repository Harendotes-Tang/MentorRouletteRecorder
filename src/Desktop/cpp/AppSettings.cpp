#include "AppSettings.h"

#include <QCoreApplication>
#include <QDate>
#include <QDir>
#include <QStandardPaths>

namespace {

constexpr int kDefaultUiScale = 100;

#ifdef Q_OS_WIN
constexpr char kRunKey[] = "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows"
                           "\\CurrentVersion\\Run";
constexpr char kRunValue[] = "MentorRecorder";
#endif

QString settingsDirectory()
{
    // QStandardPaths::AppLocalDataLocation depends on the application name;
    // the path is pinned here so it is the same for the app and the tests.
    const QString base =
        QStandardPaths::writableLocation(QStandardPaths::GenericConfigLocation);
    return QDir(base).absoluteFilePath(QStringLiteral("MentorRecorder"));
}

int clampScale(int percent)
{
    for (int allowed : {100, 125, 150, 175}) {
        if (percent == allowed)
            return percent;
    }
    return kDefaultUiScale;
}

} // namespace

namespace mr {

QString AppSettings::filePath()
{
    return QDir(settingsDirectory()).absoluteFilePath(QStringLiteral("desktop.ini"));
}

AppSettings::AppSettings(QObject *parent)
    : QObject(parent)
    , m_settings(filePath(), QSettings::IniFormat)
{
    QDir().mkpath(settingsDirectory());
}

QString AppSettings::themeMode() const
{
    const QString mode =
        m_settings.value(QStringLiteral("ui/theme"), QStringLiteral("system")).toString();
    if (mode == QLatin1String("dark") || mode == QLatin1String("light"))
        return mode;
    return QStringLiteral("system");
}

void AppSettings::setThemeMode(const QString &mode)
{
    m_settings.setValue(QStringLiteral("ui/theme"), mode);
}

int AppSettings::uiScale() const
{
    return clampScale(m_settings.value(QStringLiteral("ui/scale"), kDefaultUiScale).toInt());
}

void AppSettings::setUiScale(int percent)
{
    m_settings.setValue(QStringLiteral("ui/scale"), clampScale(percent));
}

bool AppSettings::firstRunCompleted() const
{
    return m_settings.value(QStringLiteral("ui/first_run_completed"), false).toBool();
}

void AppSettings::setFirstRunCompleted(bool completed)
{
    m_settings.setValue(QStringLiteral("ui/first_run_completed"), completed);
}

// ---------------------------------------------------------------------------
// First-run disclosure (DEC-OODLE-01)
//
// The acknowledgement is stored as the version of the text the user actually
// saw. Raising kDisclosureVersion therefore re-shows the page instead of
// silently carrying an acknowledgement of an older, different disclosure.
// ---------------------------------------------------------------------------

int AppSettings::disclosureVersion()
{
    return kDisclosureVersion;
}

int AppSettings::acknowledgedDisclosureVersion() const
{
    return m_settings.value(QStringLiteral("ui/disclosure_acknowledged_version"), 0).toInt();
}

bool AppSettings::disclosureAcknowledged() const
{
    return acknowledgedDisclosureVersion() >= kDisclosureVersion;
}

QString AppSettings::disclosureAcknowledgedAt() const
{
    return m_settings.value(QStringLiteral("ui/disclosure_acknowledged_at"), QString())
        .toString();
}

void AppSettings::acknowledgeDisclosure(const QString &utcTimestamp)
{
    m_settings.setValue(QStringLiteral("ui/disclosure_acknowledged_version"),
                        kDisclosureVersion);
    m_settings.setValue(QStringLiteral("ui/disclosure_acknowledged_at"), utcTimestamp);
    m_settings.sync();
    Q_EMIT disclosureChanged();
}

void AppSettings::resetDisclosureAcknowledgement()
{
    m_settings.remove(QStringLiteral("ui/disclosure_acknowledged_version"));
    m_settings.remove(QStringLiteral("ui/disclosure_acknowledged_at"));
    m_settings.sync();
    Q_EMIT disclosureChanged();
}

// ---------------------------------------------------------------------------
// 通用
// ---------------------------------------------------------------------------

bool AppSettings::autostartWritable()
{
#ifdef Q_OS_WIN
    return true;
#else
    return false;
#endif
}

void AppSettings::applyAutostartRegistration(bool enabled)
{
#ifdef Q_OS_WIN
    // Per-user Run key only. Never HKLM, never a service, never a scheduled
    // task - uninstalling the app by deleting it must not leave anything that
    // survives outside this one value.
    QSettings run(QString::fromLatin1(kRunKey), QSettings::NativeFormat);
    if (!enabled) {
        run.remove(QString::fromLatin1(kRunValue));
        return;
    }
    const QString exe = QDir::toNativeSeparators(QCoreApplication::applicationFilePath());
    run.setValue(QString::fromLatin1(kRunValue), QStringLiteral("\"%1\"").arg(exe));
#else
    Q_UNUSED(enabled)
#endif
}

bool AppSettings::autostart() const
{
    return m_settings.value(QStringLiteral("general/autostart"), false).toBool();
}

void AppSettings::setAutostart(bool enabled)
{
    if (autostart() == enabled)
        return;
    m_settings.setValue(QStringLiteral("general/autostart"), enabled);
    applyAutostartRegistration(enabled);
    Q_EMIT generalChanged();
}

bool AppSettings::followFfxiv() const
{
    return m_settings.value(QStringLiteral("general/follow_ffxiv"), true).toBool();
}

void AppSettings::setFollowFfxiv(bool enabled)
{
    if (followFfxiv() == enabled)
        return;
    m_settings.setValue(QStringLiteral("general/follow_ffxiv"), enabled);
    Q_EMIT generalChanged();
}

bool AppSettings::minimizeToTray() const
{
    return m_settings.value(QStringLiteral("general/minimize_to_tray"), true).toBool();
}

void AppSettings::setMinimizeToTray(bool enabled)
{
    if (minimizeToTray() == enabled)
        return;
    m_settings.setValue(QStringLiteral("general/minimize_to_tray"), enabled);
    Q_EMIT generalChanged();
}

bool AppSettings::reflectPrompt() const
{
    return m_settings.value(QStringLiteral("general/reflectPrompt"), true).toBool();
}

void AppSettings::setReflectPrompt(bool enabled)
{
    if (reflectPrompt() == enabled)
        return;
    m_settings.setValue(QStringLiteral("general/reflectPrompt"), enabled);
    Q_EMIT generalChanged();
}

bool AppSettings::confirmPrompt() const
{
    return m_settings.value(QStringLiteral("general/confirmPrompt"), true).toBool();
}

void AppSettings::setConfirmPrompt(bool enabled)
{
    if (confirmPrompt() == enabled)
        return;
    m_settings.setValue(QStringLiteral("general/confirmPrompt"), enabled);
    Q_EMIT generalChanged();
}

// ---------------------------------------------------------------------------
// 外观
//
// Only the two styles this build ships are accepted: an INI edited by hand, or
// written by a newer build, keeps the previous value rather than leaving the UI
// bound to a theme that has no tokens.
//
// The default is "classic" (the workbench skin). setUiStyle() never writes the
// value that is already current, so a stored "eorzea" is always a user's choice.
// ---------------------------------------------------------------------------

QString AppSettings::uiStyle() const
{
    const QString style =
        m_settings.value(QStringLiteral("appearance/uiStyle"), QStringLiteral("classic"))
            .toString();
    return style == QLatin1String("eorzea") ? style : QStringLiteral("classic");
}

void AppSettings::setUiStyle(const QString &style)
{
    if (style != QLatin1String("eorzea") && style != QLatin1String("classic"))
        return;
    if (uiStyle() == style)
        return;
    m_settings.setValue(QStringLiteral("appearance/uiStyle"), style);
    Q_EMIT appearanceChanged();
}

// ---------------------------------------------------------------------------
// 本地 TTS
// ---------------------------------------------------------------------------

bool AppSettings::ttsEnabled() const
{
    return m_settings.value(QStringLiteral("tts/enabled"), true).toBool();
}

void AppSettings::setTtsEnabled(bool enabled)
{
    if (ttsEnabled() == enabled)
        return;
    m_settings.setValue(QStringLiteral("tts/enabled"), enabled);
    Q_EMIT ttsChanged();
}

int AppSettings::ttsRate() const
{
    return qBound(50, m_settings.value(QStringLiteral("tts/rate"), 100).toInt(), 200);
}

void AppSettings::setTtsRate(int percent)
{
    const int clamped = qBound(50, percent, 200);
    if (ttsRate() == clamped)
        return;
    m_settings.setValue(QStringLiteral("tts/rate"), clamped);
    Q_EMIT ttsChanged();
}

int AppSettings::ttsVolume() const
{
    return qBound(0, m_settings.value(QStringLiteral("tts/volume"), 80).toInt(), 100);
}

void AppSettings::setTtsVolume(int percent)
{
    const int clamped = qBound(0, percent, 100);
    if (ttsVolume() == clamped)
        return;
    m_settings.setValue(QStringLiteral("tts/volume"), clamped);
    Q_EMIT ttsChanged();
}

QString AppSettings::ttsVoice() const
{
    return m_settings.value(QStringLiteral("tts/voice"), QString()).toString().trimmed();
}

void AppSettings::setTtsVoice(const QString &id)
{
    const QString trimmed = id.trimmed();
    if (ttsVoice() == trimmed)
        return;
    if (trimmed.isEmpty())
        m_settings.remove(QStringLiteral("tts/voice"));
    else
        m_settings.setValue(QStringLiteral("tts/voice"), trimmed);
    Q_EMIT ttsChanged();
}

bool AppSettings::ttsOnlineConfirmed() const
{
    return m_settings.value(QStringLiteral("tts/online_confirmed"), false).toBool();
}

void AppSettings::setTtsOnlineConfirmed(bool confirmed)
{
    if (ttsOnlineConfirmed() == confirmed)
        return;
    if (confirmed)
        m_settings.setValue(QStringLiteral("tts/online_confirmed"), true);
    else
        m_settings.remove(QStringLiteral("tts/online_confirmed"));
    Q_EMIT ttsChanged();
}

QString AppSettings::templateMatched() const
{
    return m_settings
        .value(QStringLiteral("tts/tpl_matched"), QString::fromUtf8("指导者任务匹配成功"))
        .toString();
}

void AppSettings::setTemplateMatched(const QString &value)
{
    if (templateMatched() == value)
        return;
    m_settings.setValue(QStringLiteral("tts/tpl_matched"), value);
    Q_EMIT ttsChanged();
}

QString AppSettings::templateEntered() const
{
    return m_settings
        .value(QStringLiteral("tts/tpl_entered"), QString::fromUtf8("进入 {duty}"))
        .toString();
}

void AppSettings::setTemplateEntered(const QString &value)
{
    if (templateEntered() == value)
        return;
    m_settings.setValue(QStringLiteral("tts/tpl_entered"), value);
    Q_EMIT ttsChanged();
}

QString AppSettings::templateCompleted() const
{
    return m_settings
        .value(QStringLiteral("tts/tpl_completed"),
               QString::fromUtf8("导随完成，当前 {progress} 次，剩余 {remaining}"))
        .toString();
}

void AppSettings::setTemplateCompleted(const QString &value)
{
    if (templateCompleted() == value)
        return;
    m_settings.setValue(QStringLiteral("tts/tpl_completed"), value);
    Q_EMIT ttsChanged();
}

QString AppSettings::templateFinished() const
{
    return m_settings
        .value(QStringLiteral("tts/tpl_finished"),
               QString::fromUtf8("导随结束，请确认是否通关，已确认 {progress} 次"))
        .toString();
}

void AppSettings::setTemplateFinished(const QString &value)
{
    if (templateFinished() == value)
        return;
    m_settings.setValue(QStringLiteral("tts/tpl_finished"), value);
    Q_EMIT ttsChanged();
}

QString AppSettings::templateAborted() const
{
    return m_settings
        .value(QStringLiteral("tts/tpl_aborted"), QString::fromUtf8("导随异常结束"))
        .toString();
}

void AppSettings::setTemplateAborted(const QString &value)
{
    if (templateAborted() == value)
        return;
    m_settings.setValue(QStringLiteral("tts/tpl_aborted"), value);
    Q_EMIT ttsChanged();
}

// ---------------------------------------------------------------------------
// 数据
// ---------------------------------------------------------------------------

bool AppSettings::autoBackup() const
{
    return m_settings.value(QStringLiteral("data/auto_backup"), true).toBool();
}

void AppSettings::setAutoBackup(bool enabled)
{
    if (autoBackup() == enabled)
        return;
    m_settings.setValue(QStringLiteral("data/auto_backup"), enabled);
    Q_EMIT dataChanged();
}

int AppSettings::logRetentionDays() const
{
    return qBound(1, m_settings.value(QStringLiteral("data/log_days"), 14).toInt(), 365);
}

void AppSettings::setLogRetentionDays(int days)
{
    const int clamped = qBound(1, days, 365);
    if (logRetentionDays() == clamped)
        return;
    m_settings.setValue(QStringLiteral("data/log_days"), clamped);
    Q_EMIT dataChanged();
}

bool AppSettings::diagnosticsMode() const
{
    return m_settings.value(QStringLiteral("data/diagnostics_mode"), false).toBool();
}

void AppSettings::setDiagnosticsMode(bool enabled)
{
    if (diagnosticsMode() == enabled)
        return;
    m_settings.setValue(QStringLiteral("data/diagnostics_mode"), enabled);
    Q_EMIT dataChanged();
}

QString AppSettings::lastAutoBackupDate() const
{
    return m_settings.value(QStringLiteral("data/last_auto_backup"), QString()).toString();
}

void AppSettings::setLastAutoBackupDate(const QString &isoDate)
{
    m_settings.setValue(QStringLiteral("data/last_auto_backup"), isoDate);
}

// ---------------------------------------------------------------------------
// Raw access
// ---------------------------------------------------------------------------

bool AppSettings::value(const QString &key, bool defaultValue) const
{
    return m_settings.value(key, defaultValue).toBool();
}

int AppSettings::value(const QString &key, int defaultValue) const
{
    return m_settings.value(key, defaultValue).toInt();
}

QString AppSettings::value(const QString &key, const QString &defaultValue) const
{
    return m_settings.value(key, defaultValue).toString();
}

void AppSettings::setValue(const QString &key, const QVariant &value)
{
    m_settings.setValue(key, value);
}

int readPersistedUiScale()
{
    QSettings settings(AppSettings::filePath(), QSettings::IniFormat);
    return clampScale(settings.value(QStringLiteral("ui/scale"), kDefaultUiScale).toInt());
}

} // namespace mr
