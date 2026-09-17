#pragma once

// ---------------------------------------------------------------------------
// Persisted UI preferences.
//
// Stored as INI in %LOCALAPPDATA%\MentorRecorder\desktop.ini. Nothing here
// leaves the machine and nothing here is a secret; the database itself is
// owned by the Collector.
//
// The object is exposed to QML as the `Settings` context property, so the
// settings page binds directly to typed properties instead of pushing raw
// key strings around.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QSettings>
#include <QString>

namespace mr {

class AppSettings : public QObject
{
    Q_OBJECT

    // -- 通用 ---------------------------------------------------------------
    Q_PROPERTY(bool autostart READ autostart WRITE setAutostart NOTIFY generalChanged)
    Q_PROPERTY(bool followFfxiv READ followFfxiv WRITE setFollowFfxiv NOTIFY generalChanged)
    Q_PROPERTY(bool minimizeToTray READ minimizeToTray WRITE setMinimizeToTray NOTIFY generalChanged)
    Q_PROPERTY(bool autostartWritable READ autostartWritable CONSTANT)
    /// 通关后弹出心得窗口 - DUTY_RESULT 到达后弹出；可稍后在历史记录补录。
    Q_PROPERTY(bool reflectPrompt READ reflectPrompt WRITE setReflectPrompt NOTIFY generalChanged)
    /// 结束后询问本次结果 - 国服协议档案看不到通关判定，一局导随只会停在
    /// UNKNOWN_FINAL_STATE（待复核）。关掉它就完全靠 待复核 列表事后确认。
    Q_PROPERTY(bool confirmPrompt READ confirmPrompt WRITE setConfirmPrompt NOTIFY generalChanged)

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

    // -- 数据 ---------------------------------------------------------------
    Q_PROPERTY(bool autoBackup READ autoBackup WRITE setAutoBackup NOTIFY dataChanged)
    Q_PROPERTY(int logRetentionDays READ logRetentionDays WRITE setLogRetentionDays NOTIFY dataChanged)
    Q_PROPERTY(bool diagnosticsMode READ diagnosticsMode WRITE setDiagnosticsMode NOTIFY dataChanged)

    // -- 首次说明 -----------------------------------------------------------
    Q_PROPERTY(bool disclosureAcknowledged READ disclosureAcknowledged NOTIFY disclosureChanged)
    Q_PROPERTY(QString disclosureAcknowledgedAt READ disclosureAcknowledgedAt NOTIFY disclosureChanged)

public:
    explicit AppSettings(QObject *parent = nullptr);

    /// Absolute path of the INI file.
    static QString filePath();

    /// "dark", "light" or "system".
    QString themeMode() const;
    void setThemeMode(const QString &mode);

    /// 100 / 125 / 150 / 175. Applied at start-up via QT_SCALE_FACTOR.
    int uiScale() const;
    void setUiScale(int percent);

    bool firstRunCompleted() const;
    void setFirstRunCompleted(bool completed);

    /// Version of the first-run disclosure text this build ships. Raising it
    /// re-shows the page: an acknowledgement is only ever valid for the exact
    /// text the user was shown (docs/privacy-boundary.md, DEC-OODLE-01).
    // 2: the notice gained the run order and the pending-review step.
    // 3: the notice names the one network request, the shared-calibration
    //    download, and how to turn it off (docs/privacy-boundary.md §8.2).
    // 4: the notice gained a third network class, the update check, which the
    //    earlier text explicitly ruled out.
    static constexpr int kDisclosureVersion = 4;
    static int disclosureVersion();
    int acknowledgedDisclosureVersion() const;
    bool disclosureAcknowledged() const;
    /// UTC ISO-8601 of the acknowledgement, empty when never acknowledged.
    QString disclosureAcknowledgedAt() const;
    void acknowledgeDisclosure(const QString &utcTimestamp);
    void resetDisclosureAcknowledgement();

    bool autostart() const;
    void setAutostart(bool enabled);
    /// False when the platform has no per-user autostart mechanism we support.
    static bool autostartWritable();

    bool followFfxiv() const;
    void setFollowFfxiv(bool enabled);

    bool minimizeToTray() const;
    void setMinimizeToTray(bool enabled);

    bool reflectPrompt() const;
    void setReflectPrompt(bool enabled);

    bool confirmPrompt() const;
    void setConfirmPrompt(bool enabled);

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
    /// 结束（待确认）: the only line a CN mentor roulette really reaches, because
    /// the shipping profile carries no duty result. It asks the user to confirm
    /// instead of claiming a 通关 nobody observed.
    QString templateFinished() const;
    void setTemplateFinished(const QString &value);
    QString templateAborted() const;
    void setTemplateAborted(const QString &value);

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

    bool value(const QString &key, bool defaultValue) const;
    int value(const QString &key, int defaultValue) const;
    QString value(const QString &key, const QString &defaultValue) const;
    void setValue(const QString &key, const QVariant &value);

Q_SIGNALS:
    void generalChanged();
    void appearanceChanged();
    void ttsChanged();
    void dataChanged();
    void disclosureChanged();

private:
    /// Add or remove the HKCU\...\Run value. No-op off Windows.
    static void applyAutostartRegistration(bool enabled);

    QSettings m_settings;
};

/// Read the persisted UI scale before QApplication exists, so the value can be
/// pushed into the environment where Qt picks it up.
int readPersistedUiScale();

} // namespace mr
