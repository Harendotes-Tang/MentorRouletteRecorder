#pragma once

// ---------------------------------------------------------------------------
// 在线语音 / online speech, desktop half (docs/privacy-boundary.md §8.3).
//
// Holds the Collector's $defs/SpeechSettings - never a key, only has_key - and
// sends the settings page's 保存 / 清除 / 测试. It never touches the network:
// the Collector sends every request, and TtsService plays the local file it
// writes. Exposed to QML as App.speech, the same way the shared-calibration
// controller hangs off App.calibration.
//
// A Collector older than these messages answers ERR_UNKNOWN_MESSAGE; then
// `supported` is false and the settings page shows no online voice at all.
// ---------------------------------------------------------------------------

#include <QJsonObject>
#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QString>
#include <QVariantList>
#include <QVariantMap>

namespace mr {

class IBackend;

class SpeechController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS

    /// A GetSpeechSettings answer has arrived since the last connect.
    Q_PROPERTY(bool loaded READ loaded NOTIFY changed)
    /// False once the Collector refused the message as unknown.
    Q_PROPERTY(bool supported READ supported NOTIFY changed)
    /// "none", "azure" or "openai_compatible".
    Q_PROPERTY(QString provider READ provider NOTIFY changed)
    Q_PROPERTY(QString azureRegion READ azureRegion NOTIFY changed)
    Q_PROPERTY(QString openaiBaseUrl READ openaiBaseUrl NOTIFY changed)
    Q_PROPERTY(QString openaiModel READ openaiModel NOTIFY changed)
    Q_PROPERTY(QString voice READ voice NOTIFY changed)
    Q_PROPERTY(bool hasKey READ hasKey NOTIFY changed)
    Q_PROPERTY(bool configured READ configured NOTIFY changed)
    /// Host a sentence goes to under the settings in force; empty when none.
    Q_PROPERTY(QString targetHost READ targetHost NOTIFY changed)
    /// {name, label} rows, as the Collector lists them.
    Q_PROPERTY(QVariantList azureVoices READ azureVoices NOTIFY changed)
    Q_PROPERTY(QVariantList openaiVoices READ openaiVoices NOTIFY changed)
    /// A save, a key deletion or a test is on its way.
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    /// The outcome line under the panel: "", "ok" or "error", and its text.
    Q_PROPERTY(QString resultState READ resultState NOTIFY changed)
    Q_PROPERTY(QString resultText READ resultText NOTIFY changed)
    /// The last failure in Chinese, empty after a success.
    Q_PROPERTY(QString lastError READ lastError NOTIFY changed)

public:
    explicit SpeechController(QObject *parent = nullptr);

    void setBackend(IBackend *backend);

    bool loaded() const { return m_loaded; }
    bool supported() const { return m_supported; }
    QString provider() const { return m_provider; }
    QString azureRegion() const { return m_settings.value(QStringLiteral("azure_region")).toString(); }
    QString openaiBaseUrl() const { return m_settings.value(QStringLiteral("openai_base_url")).toString(); }
    QString openaiModel() const { return m_settings.value(QStringLiteral("openai_model")).toString(); }
    QString voice() const { return m_settings.value(QStringLiteral("voice")).toString(); }
    bool hasKey() const { return m_settings.value(QStringLiteral("has_key")).toBool(); }
    bool configured() const { return m_settings.value(QStringLiteral("configured")).toBool(); }
    QString targetHost() const { return m_settings.value(QStringLiteral("target_host")).toString(); }
    QVariantList azureVoices() const { return m_azureVoices; }
    QVariantList openaiVoices() const { return m_openaiVoices; }
    bool busy() const { return m_busy; }
    QString resultState() const { return m_resultState; }
    QString resultText() const { return m_resultText; }
    QString lastError() const { return m_resultState == QLatin1String("error") ? m_resultText : QString(); }

    /// Online speech can be offered: a Collector answered and knows the messages.
    bool available() const { return m_loaded && m_supported; }

    // -- voice ids -----------------------------------------------------------
    static QString azurePrefix() { return QStringLiteral("azure:"); }
    static QString openaiPrefix() { return QStringLiteral("openai:"); }
    /// "azure" / "openai_compatible" for an online voice id, "" otherwise.
    Q_INVOKABLE static QString providerOfVoiceId(const QString &id);
    /// The voice name inside an online id, "" otherwise.
    Q_INVOKABLE static QString voiceNameOf(const QString &id);
    /// "azure:<voice>" / "openai:<voice>", or "" for another provider.
    Q_INVOKABLE static QString voiceIdFor(const QString &provider, const QString &voice);
    Q_INVOKABLE static bool isOnlineVoiceId(const QString &id);
    /// True when \a id names a voice of the service the Collector has in force
    /// and that service can send (configured).
    Q_INVOKABLE bool configuredFor(const QString &id) const;
    /// The combo box rows of the online voices, empty unless available().
    /// \a current (the selected id) is appended when it is not in the lists.
    QVariantList voiceRows(const QString &current) const;

    // -- sentences -----------------------------------------------------------
    /// The short reason for an error code, as in 在线语音暂不可用（<原因>）.
    Q_INVOKABLE static QString errorReason(const QString &code);
    /// The panel's 播报文字会发送到 <…> for a draft that may not be saved yet.
    Q_INVOKABLE QString describeTarget(const QString &provider, const QString &region,
                                       const QString &baseUrl) const;
    /// Host of an address the user typed, or "" when it is not an absolute URL.
    Q_INVOKABLE static QString hostOfUrl(const QString &url);

    /// The voice the desktop has selected (Settings.ttsVoice). When it names a
    /// voice of the service in force that the Collector does not have yet, the
    /// voice alone is sent - never a provider change, which would delete the
    /// stored key.
    void setSelectedVoice(const QString &id);

    /// TtsService reports how a 测试 sentence went.
    void noteTestResult(bool ok, const QString &code, bool spokenOnline);

public Q_SLOTS:
    void refresh();
    /// 保存: the keys of \a fields that UpdateSpeechSettings knows are sent as
    /// given; api_key only when non-empty (清除 is clearKey()).
    void save(const QVariantMap &fields);
    /// 清除: api_key = "".
    void clearKey();
    /// 测试: asks TtsService to speak the sample with test = true.
    void test();

Q_SIGNALS:
    void changed();
    /// A GetSpeechSettings / UpdateSpeechSettings answer replaced the settings;
    /// the panel re-reads its fields.
    void settingsReplaced();
    /// 保存 answered. \a ok false keeps what the user typed (except the key).
    void saveFinished(bool ok);
    void testRequested();

private:
    void adopt(const QJsonObject &settings);
    void send(const QVariantMap &fields, const QString &successText, const QString &failurePrefix);
    void setResult(const QString &state, const QString &text);
    void maybeSyncVoice();
    /// True for an answer that says the request itself is wrong, as opposed to
    /// one the Collector could not take right now.
    static bool isRefusal(const QString &code);

    QPointer<IBackend> m_backend;
    QJsonObject m_settings;
    QString m_provider = QStringLiteral("none");
    QVariantList m_azureVoices;
    QVariantList m_openaiVoices;
    QString m_selectedVoice;
    QString m_syncedVoice;
    QString m_resultState;
    QString m_resultText;
    bool m_loaded = false;
    bool m_supported = true;
    bool m_busy = false;
    bool m_testing = false;
    int m_generation = 0;
};

} // namespace mr
