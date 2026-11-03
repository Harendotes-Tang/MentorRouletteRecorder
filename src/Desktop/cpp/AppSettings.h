#pragma once

// ---------------------------------------------------------------------------
// Persisted UI preferences.
//
// Stored as INI in %LOCALAPPDATA%\MentorRecorder\desktop.ini, or under
// MentorRecorder-dev in a development build (BuildFlavour.h). Nothing here
// leaves the machine and nothing here is a secret; the database itself is
// owned by the Collector.
//
// The object is exposed to QML as the `Settings` context property, so the
// settings page binds directly to typed properties instead of pushing raw
// key strings around.
// ---------------------------------------------------------------------------

#include "BuildFlavour.h"

#include <QObject>
#include <QSettings>
#include <QString>
#include <QRect>
#include <QStringList>

namespace mr {

class AppSettings : public QObject
{
    Q_OBJECT

    Q_PROPERTY(QString shareGameIdentity READ shareGameIdentity WRITE setShareGameIdentity NOTIFY sharingChanged)

    // -- 通用 ---------------------------------------------------------------
    Q_PROPERTY(bool autostart READ autostart WRITE setAutostart NOTIFY generalChanged)
    Q_PROPERTY(bool followFfxiv READ followFfxiv WRITE setFollowFfxiv NOTIFY generalChanged)
    Q_PROPERTY(bool minimizeToTray READ minimizeToTray WRITE setMinimizeToTray NOTIFY generalChanged)
    Q_PROPERTY(bool autostartWritable READ autostartWritable CONSTANT)
    Q_PROPERTY(QString autostartUnavailableReason READ autostartUnavailableReason CONSTANT)
    Q_PROPERTY(bool autostartRegistrationFailed READ autostartRegistrationFailed NOTIFY generalChanged)
    /// 通关后弹出心得窗口 - DUTY_RESULT 到达后弹出；可稍后在历史记录补录。
    Q_PROPERTY(bool reflectPrompt READ reflectPrompt WRITE setReflectPrompt NOTIFY generalChanged)
    /// 结束后询问本次结果 - 离开副本前没有收到通关结算时，一局导随停在
    /// UNKNOWN_FINAL_STATE（待复核）。关掉它就完全靠 待复核 列表事后确认。
    Q_PROPERTY(bool confirmPrompt READ confirmPrompt WRITE setConfirmPrompt NOTIFY generalChanged)
    /// Reminder delivery: window, floating or inbox; existing prompt switches still gate eligibility.
    Q_PROPERTY(QString promptPresentation READ promptPresentation WRITE setPromptPresentation NOTIFY generalChanged)

    // -- 外观 ---------------------------------------------------------------
    /// 界面风格 "classic"（经典，workbench，默认）或 "eorzea"（艾欧泽亚）。
    Q_PROPERTY(QString uiStyle READ uiStyle WRITE setUiStyle NOTIFY appearanceChanged)

    // -- 本地 TTS -----------------------------------------------------------
    Q_PROPERTY(bool ttsEnabled READ ttsEnabled WRITE setTtsEnabled NOTIFY ttsChanged)
    Q_PROPERTY(int ttsRate READ ttsRate WRITE setTtsRate NOTIFY ttsChanged)
    Q_PROPERTY(int ttsVolume READ ttsVolume WRITE setTtsVolume NOTIFY ttsChanged)
    /// 语音引擎的选择：`local:<名称>`（本机语音）、`azure:<音色>` / `openai:<音色>`（在线语音）。
    /// 空 = 默认（第一个 zh-CN 本机语音）。
    Q_PROPERTY(QString ttsVoice READ ttsVoice WRITE setTtsVoice NOTIFY ttsChanged)
    /// 第一次选在线语音时的确认框已经确认过（键 `tts/online_confirmed`，每台机器一次）。
    Q_PROPERTY(bool ttsOnlineConfirmed READ ttsOnlineConfirmed WRITE setTtsOnlineConfirmed NOTIFY ttsChanged)
    Q_PROPERTY(QString templateMatched READ templateMatched WRITE setTemplateMatched NOTIFY ttsChanged)
    Q_PROPERTY(QString templateEntered READ templateEntered WRITE setTemplateEntered NOTIFY ttsChanged)
    Q_PROPERTY(QString templateCompleted READ templateCompleted WRITE setTemplateCompleted NOTIFY ttsChanged)
    Q_PROPERTY(QString templateFinished READ templateFinished WRITE setTemplateFinished NOTIFY ttsChanged)
    Q_PROPERTY(QString templateAborted READ templateAborted WRITE setTemplateAborted NOTIFY ttsChanged)
    /// Some template of the interface language differs from its default.
    Q_PROPERTY(bool templatesCustomised READ templatesCustomised NOTIFY ttsChanged)

    // -- 数据 ---------------------------------------------------------------
    Q_PROPERTY(bool autoBackup READ autoBackup WRITE setAutoBackup NOTIFY dataChanged)
    Q_PROPERTY(int logRetentionDays READ logRetentionDays WRITE setLogRetentionDays NOTIFY dataChanged)
    Q_PROPERTY(bool diagnosticsMode READ diagnosticsMode WRITE setDiagnosticsMode NOTIFY dataChanged)

    // Main dungeon page only; the floating statistics keep their own preferences.
    Q_PROPERTY(bool dungeonCategoryChartVisible READ dungeonCategoryChartVisible WRITE setDungeonCategoryChartVisible NOTIFY dungeonChartsChanged)
    Q_PROPERTY(bool dungeonSpecialChartVisible READ dungeonSpecialChartVisible WRITE setDungeonSpecialChartVisible NOTIFY dungeonChartsChanged)
    Q_PROPERTY(QStringList dungeonCategoryHiddenBuckets READ dungeonCategoryHiddenBuckets WRITE setDungeonCategoryHiddenBuckets NOTIFY dungeonChartsChanged)
    Q_PROPERTY(QStringList dungeonSpecialHiddenBuckets READ dungeonSpecialHiddenBuckets WRITE setDungeonSpecialHiddenBuckets NOTIFY dungeonChartsChanged)

public:
    /// 用户手填的本机分享署名，不代表历史记录或采集到的角色身份。
    QString shareGameIdentity() const;
    void setShareGameIdentity(const QString &identity);
    explicit AppSettings(QObject *parent = nullptr);
    /// Explicit INI location for isolated local harnesses.
    AppSettings(const QString &iniPath, QObject *parent);
    /// As above, with the login-autostart rules of a build of \a flavour instead
    /// of this one's (build::thisBuild()), so one test binary covers both.
    AppSettings(const QString &iniPath, build::Flavour flavour, QObject *parent = nullptr);
    bool dungeonCategoryChartVisible() const;
    void setDungeonCategoryChartVisible(bool visible);
    bool dungeonSpecialChartVisible() const;
    void setDungeonSpecialChartVisible(bool visible);
    /// Canonical bucket keys hidden on the main page. Unknown future keys are
    /// retained; new buckets default to visible because selections are exclusions.
    QStringList dungeonCategoryHiddenBuckets() const;
    void setDungeonCategoryHiddenBuckets(const QStringList &keys);
    QStringList dungeonSpecialHiddenBuckets() const;
    void setDungeonSpecialHiddenBuckets(const QStringList &keys);
    Q_INVOKABLE void resetDungeonCharts();
    /// Floating-window preferences; the overlay controller validates semantic bounds.
    QRect statisticsOverlayGeometry() const;
    void setStatisticsOverlayGeometry(const QRect &geometry);
    double statisticsOverlayOpacity() const;
    void setStatisticsOverlayOpacity(double opacity);
    QStringList statisticsOverlayModules(const QStringList &fallback) const;
    void setStatisticsOverlayModules(const QStringList &modules);
    QStringList statisticsOverlayModuleOrder(const QStringList &fallback) const;
    void setStatisticsOverlayModuleOrder(const QStringList &order);
    int statisticsOverlayRowLimit() const;
    void setStatisticsOverlayRowLimit(int limit);
    /// "full" or "mini"; the controller validates.
    QString statisticsOverlayForm() const;
    void setStatisticsOverlayForm(const QString &form);
    bool statisticsOverlayLabels() const;
    void setStatisticsOverlayLabels(bool labels);
    QString statisticsOverlayAttemptPeriod() const;
    void setStatisticsOverlayAttemptPeriod(const QString &period);
    /// Versioned once so a later explicit removal of the new module is retained.
    int statisticsOverlayModuleVersion() const;
    void setStatisticsOverlayModuleVersion(int version);

    /// The history table's columns (docs/ui-design.md §4.2), by column key:
    /// the user's order, the hidden columns and the widths they dragged out.
    /// HistoryColumnsController validates; an empty list or map removes the key.
    QStringList historyColumnOrder() const;
    void setHistoryColumnOrder(const QStringList &order);
    QStringList historyHiddenColumns() const;
    void setHistoryHiddenColumns(const QStringList &keys);
    /// key -> width in pixels, one key `history/column_width/<key>` each.
    QVariantMap historyColumnWidths() const;
    void setHistoryColumnWidths(const QVariantMap &widths);

    /// Absolute path of the INI file.
    static QString filePath();
    /// This object's actual INI, including an explicitly injected harness location.
    QString instanceFilePath() const;

    /// True when a run of this program must keep away from the player's own
    /// settings, backup date and note images: a screenshot, or any run on the
    /// deterministic mock backend, whose fake backup and fixture runs would
    /// otherwise land in the real files (review OH-7). main() then puts
    /// QStandardPaths into test mode before the first AppSettings exists.
    static bool isHarnessRun(bool screenshotMode, const QString &backend);
    /// True when the raw command line \a arguments (program name first) carries
    /// the valued option \a name ("--screenshot") in either spelling the parser
    /// accepts: "--screenshot FILE" or "--screenshot=FILE". main() reads it this
    /// way before the application exists, so the harness isolation it decides
    /// agrees with the branch the parser takes later (review I1-6).
    static bool hasRawOption(const QStringList &arguments, const QString &name);

    /// "dark", "light" or "system".
    QString themeMode() const;
    void setThemeMode(const QString &mode);

    /// 100 / 125 / 150 / 175. Applied at start-up via QT_SCALE_FACTOR.
    int uiScale() const;
    void setUiScale(int percent);

    /// ui/first_run_completed. Builds before 2026-10-04 also kept
    /// ui/disclosure_acknowledged_version and ui/disclosure_acknowledged_at here,
    /// the confirmation of 这个软件做什么、不做什么 that a first start used to ask
    /// for; a file that still holds them loads as any other and they are unused.
    bool firstRunCompleted() const;
    void setFirstRunCompleted(bool completed);

    bool autostart() const;
    /// Registers or removes the Run value. When Windows does not accept the
    /// change the setting keeps its value and autostartRegistrationFailed() is
    /// set until a later change goes through.
    void setAutostart(bool enabled);
    /// True only when autostartUnavailableReason() is empty.
    bool autostartWritable() const;
    /// Why this run may not touch the login Run value, for the settings row:
    /// "" when it may; "development" for a development build, which never
    /// registers itself nor rewrites or removes the installed copy's value
    /// (the account has one); "harness" for mock/screenshot/test runs;
    /// "platform" off Windows.
    QString autostartUnavailableReason() const;
    /// True when Windows refused the last write or removal of the Run value
    /// (security software guarding the key, a policy). In memory only.
    bool autostartRegistrationFailed() const { return m_autostartRegistrationFailed; }
    /// The marker the Run value passes after the executable, so a login start
    /// begins in the tray instead of opening the main window. A positional
    /// argument, not an option: 1.8.x and older stop with "Unknown option" on
    /// an option they do not know but ignore an extra positional argument, so
    /// a player who goes back to one keeps a working login start (review I1-3).
    static QString autostartArgument();
    /// True for a launch the Run value made: \a positionalArguments holds
    /// autostartArgument(), or the option --autostart (\a autostartOptionSet)
    /// that unreleased builds registered was given.
    static bool isAutostartLaunch(const QStringList &positionalArguments, bool autostartOptionSet);
    /// With 开机启动 on, bring the Run value up to date for this executable when
    /// it holds anything else: a value registered before the marker existed, in
    /// the option form, or by a copy since moved. Nothing is written while the
    /// setting is off or when the value already matches. main() reaches it only
    /// through refreshAutostartRegistrationAtStartUp().
    void refreshAutostartRegistration();
    /// The start-up step main() takes once the whole command line was accepted:
    /// refreshAutostartRegistration() for the account's one instance only.
    /// \a primaryInstance is true only for a run that holds the single-instance
    /// guard, which main() creates for a non-harness IPC run alone, so a refused
    /// option, a second copy that only wakes the first and every harness run
    /// leave the Run value alone (review I5-3, J-1). True when the Run value was
    /// looked at: a primary instance whose flavour and run may register, with
    /// 开机启动 on.
    bool refreshAutostartRegistrationAtStartUp(bool primaryInstance);

    bool followFfxiv() const;
    void setFollowFfxiv(bool enabled);

    bool minimizeToTray() const;
    void setMinimizeToTray(bool enabled);

    bool reflectPrompt() const;
    void setReflectPrompt(bool enabled);

    bool confirmPrompt() const;
    void setConfirmPrompt(bool enabled);
    QString promptPresentation() const;
    void setPromptPresentation(const QString &presentation);

    /// "classic" (default) or "eorzea"; anything else is ignored and keeps the old value.
    QString uiStyle() const;
    void setUiStyle(const QString &style);

    bool ttsEnabled() const;
    void setTtsEnabled(bool enabled);
    /// 50..200 (percent of the engine's neutral rate).
    int ttsRate() const;
    void setTtsRate(int percent);
    /// 0..100.
    int ttsVolume() const;
    void setTtsVolume(int percent);
    /// Persisted voice id (key `tts/voice`); empty means the default voice.
    /// The value is stored as given: TtsService decides what it can use and
    /// falls back to the default for anything it does not recognise.
    QString ttsVoice() const;
    void setTtsVoice(const QString &id);
    /// The online-speech confirmation (what is sent, to whom, how to turn it
    /// off) was accepted on this machine. Cancelling it never sets this.
    bool ttsOnlineConfirmed() const;
    void setTtsOnlineConfirmed(bool confirmed);

    QString templateMatched() const;
    void setTemplateMatched(const QString &value);
    QString templateEntered() const;
    void setTemplateEntered(const QString &value);
    QString templateCompleted() const;
    void setTemplateCompleted(const QString &value);
    /// 结束（待确认）: the line of a duty that ends without the clear signal
    /// (docs/protocol-profile-format.md §12). It asks the user to confirm
    /// instead of claiming a 通关 nobody observed.
    QString templateFinished() const;
    void setTemplateFinished(const QString &value);
    QString templateAborted() const;
    void setTemplateAborted(const QString &value);
    /// 播报跟随界面语言：the voice and the five templates are kept per interface
    /// language (简体中文 under the original keys). Unedited templates read the
    /// language's own default.
    bool templatesCustomised() const;
    /// 恢复默认模板 - the interface language's five templates only.
    Q_INVOKABLE void resetTemplates();
    /// The interface language the voice and the templates are kept for
    /// ("zh-CN" by default). LanguageManager sets it before it announces a
    /// switch; ttsChanged follows from there (main.cpp).
    void setSpeechLanguage(const QString &code);
    QString speechLanguage() const { return m_speechLanguage; }

    bool autoBackup() const;
    void setAutoBackup(bool enabled);
    int logRetentionDays() const;
    void setLogRetentionDays(int days);
    bool diagnosticsMode() const;
    void setDiagnosticsMode(bool enabled);

    /// yyyy-MM-dd of the last automatic backup, empty when none ran yet.
    QString lastAutoBackupDate() const;
    void setLastAutoBackupDate(const QString &isoDate);

    /// The version 忽略此版本 was last pressed for, empty when never. Stored per
    /// version so a later release raises the update banner again by itself.
    QString dismissedUpdateVersion() const;
    void setDismissedUpdateVersion(const QString &version);

    /// 已关闭更新说明的最高完整版本；空表示尚未读过。与忽略待下载版本互相独立。
    QString releaseNotesSeenVersion() const;
    /// 同步写入本地 INI；调用方负责仅在关闭说明时保存更高版本。
    void setReleaseNotesSeenVersion(const QString &version);

    bool value(const QString &key, bool defaultValue) const;
    int value(const QString &key, int defaultValue) const;
    QString value(const QString &key, const QString &defaultValue) const;
    void setValue(const QString &key, const QVariant &value);

Q_SIGNALS:
    void sharingChanged();
    void generalChanged();
    void appearanceChanged();
    void ttsChanged();
    void dataChanged();
    void dungeonChartsChanged();

private:
    /// tts/<leaf> for 简体中文, tts/<language>/<leaf> otherwise.
    QString ttsKey(const char *leaf) const;
    QString templateValue(const char *leaf) const;
    void setTemplateValue(const char *leaf, const QString &value);

    /// Add or remove the HKCU\...\Run value; with \a onlyWhenDifferent a value
    /// that already holds this executable's command line is left alone. False
    /// when Windows did not accept the change, and without touching anything
    /// when autostartWritable() is false.
    bool applyAutostartRegistration(bool enabled, bool onlyWhenDifferent = false);

    QSettings m_settings;
    QString m_speechLanguage = QStringLiteral("zh-CN");
    build::Flavour m_flavour = build::thisBuild();
    bool m_autostartRegistrationFailed = false;
};

/// Read the persisted UI scale before QApplication exists, so the value can be
/// pushed into the environment where Qt picks it up.
int readPersistedUiScale();

} // namespace mr
