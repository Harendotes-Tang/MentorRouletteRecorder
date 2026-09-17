#pragma once

// ---------------------------------------------------------------------------
// Spoken announcements.
//
// Two routes, chosen by Settings.ttsVoice:
//
//   local:<name> (or empty) - QTextToSpeech drives whatever SAPI voice Windows
//     already has installed. Nothing leaves the machine; without a usable
//     engine the service reports "unavailable".
//
//   azure:<voice> / openai:<voice> - online speech (docs/privacy-boundary.md
//     §8.3). The Desktop still sends nothing anywhere: it asks the Collector
//     over IPC (SynthesizeSpeech), checks that the returned file really is a
//     WAV inside <data dir>\tts-cache\, and plays it with a SpeechPlayer
//     (QSoundEffect). Sentences are played one after another. Any failure -
//     an error code, no answer in time, a refused path, a playback error -
//     speaks the same sentence with the local default voice and raises one
//     toast per session per error code. No announcement is dropped.
//
// Templates are the strings of the settings page:
//   匹配   - no placeholder
//   进本   - {duty}
//   完成   - {progress} {remaining}   (an observed DUTY_RESULT victory)
//   结束   - {progress}               (finished, result still unconfirmed)
//   异常   - no placeholder
// Substitution is literal token replacement, not a template language.
// ---------------------------------------------------------------------------

#include <QList>
#include <QLocale>
#include <QObject>
#include <QPointer>
#include <QSet>
#include <QString>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>
#include <QVoice>

#include <optional>

class QTextToSpeech;

namespace mr {

class AppSettings;
class IBackend;
class SpeechController;
class SpeechPlayer;

class TtsService : public QObject
{
    Q_OBJECT

    Q_PROPERTY(bool available READ isAvailable NOTIFY availabilityChanged)
    Q_PROPERTY(QString statusText READ statusText NOTIFY availabilityChanged)
    Q_PROPERTY(QString voiceName READ voiceName NOTIFY availabilityChanged)
    /// The machine's installed voices, zh-CN first, then - while the Collector
    /// offers online speech - its Azure and OpenAI-compatible voices:
    /// {id, name, label, locale, provider, group}. group is "local" or
    /// "online". The local part is empty until the engine is Ready (SAPI
    /// enumerates late).
    Q_PROPERTY(QVariantList voices READ voices NOTIFY voicesChanged)
    /// The id of the voice in use, or the one about to be used once the engine
    /// is idle again (see setVoice()). An online id while online speech is
    /// offered. Empty without a voice.
    Q_PROPERTY(QString voiceId READ voiceId NOTIFY availabilityChanged)

public:
    /// System: the machine's speech engine, when there is one. None: no engine
    /// at all, so a test observes the local route through spokeVia() only.
    enum class EngineMode { System, None };

    explicit TtsService(AppSettings *settings, QObject *parent = nullptr,
                        EngineMode engineMode = EngineMode::System);
    ~TtsService() override;

    /// One installed voice, independent of the engine so the selection rules
    /// can be tested on a machine without one.
    struct VoiceEntry {
        QString name;
        QLocale locale;
        QVoice::Gender gender = QVoice::Unknown;
    };

    /// True only while the engine can actually produce sound: an engine that
    /// reported an error is not "available" just because the object exists.
    bool isAvailable() const;
    QString statusText() const { return m_statusText; }
    QString voiceName() const { return m_voiceName; }
    QVariantList voices() const { return m_allVoices; }
    QString voiceId() const;

    // -- online speech wiring ------------------------------------------------
    /// The backend SynthesizeSpeech goes through.
    void setBackend(IBackend *backend);
    /// The Collector's online-speech settings (App.speech).
    void setSpeech(SpeechController *speech);
    /// Replaces the player; the service takes ownership. A SoundEffectPlayer
    /// is created on first use otherwise.
    void setPlayer(SpeechPlayer *player);
    /// GetStatus.database_path. Its directory's tts-cache\ is the only place a
    /// sentence is played from.
    void setCollectorDatabasePath(const QString &path);
    QString collectorDatabasePath() const { return m_databasePath; }
    /// How long to wait for SynthesizeSpeech before speaking locally. Below
    /// the IPC deadline (IpcClient::kSpeechRequestTimeoutMs) and above the
    /// contract's 20 s.
    static constexpr int kDefaultOnlineReplyTimeoutMs = 22000;
    void setOnlineReplyTimeoutMs(int milliseconds) { m_onlineReplyTimeoutMs = milliseconds; }
    /// Settings.ttsVoice names an online voice.
    bool onlineVoiceSelected() const;
    /// Sentences waiting for, or being, played online (tests).
    int pendingOnlineCount() const;

    // -- pure helpers, unit-tested without an engine ------------------------

    /// "local:" - the prefix of a machine voice id. Online voices use
    /// "azure:" and "openai:" (SpeechController).
    static QString localVoicePrefix();
    /// "local:<name>".
    static QString localVoiceId(const QString &name);
    /// zh-CN voices first, everything else after them, each group in the
    /// engine's own order.
    static QList<VoiceEntry> orderVoices(const QList<VoiceEntry> &voices);
    /// "Microsoft Huihui Desktop（中文 · 女声）".
    static QString voiceLabel(const VoiceEntry &voice);
    /// The rows of the `voices` property for \a ordered.
    static QVariantList describeVoices(const QList<VoiceEntry> &ordered);
    /// Index in \a ordered of the voice \a savedId names, or -1 when there is
    /// none: an empty id, an online id, or a local voice that is not installed
    /// (any more). -1 means "use the default voice" - which is also the voice
    /// an online sentence falls back to.
    static int savedVoiceIndex(const QList<VoiceEntry> &ordered, const QString &savedId);
    /// 在线语音暂不可用（<原因>），本次改用本机语音
    static QString fallbackToast(const QString &code);

    /// Replace every {key} in \a templateText with values[key]. Unknown
    /// placeholders are left untouched so a typo is visible instead of silent.
    static QString render(const QString &templateText, const QVariantMap &values);

    /// Map a 50..200 % speed onto QTextToSpeech's [-1, 1] rate scale.
    /// 100 % is 0 (the engine default); the ends clamp instead of wrapping.
    static double rateFromPercent(int percent);

    /// Map a 0..100 setting onto QTextToSpeech's [0, 1] volume scale.
    static double volumeFromPercent(int percent);

    /// True when a voice / locale change may be handed to the engine.
    ///
    /// The SAPI plugin forces itself back to Ready inside setVoice and dequeues
    /// whatever is next, so applying a selection while a line is still being
    /// spoken cuts that line off mid-word - which is exactly what the queueing
    /// in deliver() exists to prevent. \a state is a QTextToSpeech::State.
    static bool voiceApplicableInState(int state);

public Q_SLOTS:
    /// Speak \a text verbatim. No-op when TTS is off or unavailable.
    void speak(const QString &text);

    /// Render one of the four templates and speak it. \a kind is
    /// "matched" / "entered" / "completed" / "aborted".
    void announce(const QString &kind, const QVariantMap &values = {});

    /// Speak \a text as announcement \a kind without going through a template.
    /// Used for a terminal line whose {progress} and {remaining} would have to be
    /// invented, because the dashboard read that supplies them failed.
    void announceText(const QString &kind, const QString &text);

    /// Settings-page "试听" button: speak the template for \a kind with
    /// representative sample values, ignoring the master switch. With an
    /// online voice the request carries test = true.
    void preview(const QString &kind);
    /// The online panel's 测试: the 试听 sentence, online, and the outcome
    /// reported to SpeechController::noteTestResult.
    void testOnline();
    /// Values the settings-page preview substitutes into the templates. The
    /// controller keeps them equal to what a real announcement would use
    /// (current duty, achievement progress, remaining), so 试听 never speaks
    /// sample numbers that differ from the dashboard.
    void setContextValues(const QVariantMap &values);

    /// Stops what is being said and forgets the sentences waiting online.
    void stop();

    /// Choose the voice by id ("local:<name>", "azure:<voice>",
    /// "openai:<voice>", or "" for the default) and persist it as
    /// Settings.ttsVoice. The engine switches only while it is idle
    /// (voiceApplicableInState), never in the middle of a line; a choice made
    /// while speaking is applied at the next Ready. Ids of any other form are
    /// ignored and nothing is stored.
    Q_INVOKABLE void setVoice(const QString &id);

Q_SIGNALS:
    void availabilityChanged();
    void voicesChanged();
    /// Emitted for every announcement that was actually spoken; the tests and
    /// the toast use it instead of listening to the audio device.
    void spoke(const QString &kind, const QString &text);
    /// Emitted once per announcement when its route is settled: \a route is
    /// "online" when the Collector's file starts playing, "local" when the
    /// engine speaks it (a local voice, or the fallback of an online one).
    void spokeVia(const QString &kind, const QString &text, const QString &route);
    /// An online sentence went to the local voice because of \a code.
    void onlineFallback(const QString &code);
    /// One sentence for the toast; at most once per session per error code.
    void toastRequested(const QString &message);

private:
    struct Utterance {
        QString kind;
        QString text;
        /// SynthesizeSpeech.test: skip the Collector's cache (试听 / 测试).
        bool test = false;
        /// The 测试 button waits for this sentence's outcome.
        bool reportsTest = false;
    };
    enum class OnlinePhase { Idle, Synthesizing, Playing, SpeakingLocal };

    void onSettingsChanged();
    void refreshAllVoices();
    void applyVoiceAndLevels();
    /// Push the preferred locale and voice into the engine. Only ever called
    /// while \ref voiceApplicableInState says it is safe.
    void applyVoiceSelection();
    /// Enumerate every installed voice once. findVoices() may switch the
    /// engine's locale to do so, which is why it only runs while Ready.
    void refreshVoiceList();
    /// Today's default: the first voice of the zh-CN locale, or of the
    /// engine's own locale on a machine without a Chinese voice.
    void applyDefaultVoice();
    /// Re-read the engine's state and rebuild statusText. Called from the
    /// engine's own stateChanged/errorOccurred, because Windows SAPI finishes
    /// initialising after the constructor returns: a list sampled once at
    /// construction can leave the service claiming 已就绪 with no voice.
    void handleEngineState();
    QString templateFor(const QString &kind) const;
    QString previewText(const QString &kind) const;
    void deliver(const QString &kind, const QString &text, bool force, bool reportsTest = false);

    // -- online route (TtsServiceOnline.cpp) ----------------------------------
    void enqueueOnline(const Utterance &utterance, bool replace);
    void pumpOnline();
    void schedulePump();
    /// A reason not to ask the Collector at all, or "".
    QString onlinePrecondition() const;
    void handleSynthesis(quint64 generation, bool ok, const QVariantMap &payload,
                         const QString &code);
    void handlePlaybackFinished(bool ok, const QString &error);
    void fallBackToLocal(const QString &code);
    void noteLocalEngineState(int state);
    void finishCurrent(bool spokenOnline);
    void cancelOnline(const QString &code);
    void noteFallback(const QString &code);
    SpeechPlayer *player();

    AppSettings *m_settings = nullptr;
    QTextToSpeech *m_engine = nullptr;
    QVariantMap m_contextValues;
    QString m_statusText;
    QString m_voiceName;
    /// "local:<m_voiceName>", or empty.
    QString m_voiceId;
    /// Settings.ttsVoice as it was when the selection was last applied; a
    /// different value means the user chose again.
    QString m_appliedVoiceSetting;
    QList<VoiceEntry> m_voiceEntries;
    /// Parallel to m_voiceEntries: what setVoice() needs.
    QList<QVoice> m_engineVoices;
    /// The local rows, and those followed by the online rows.
    QVariantList m_voices;
    QVariantList m_allVoices;
    /// Guards against the engine's own signals re-entering the selection
    /// while findVoices() / setVoice() are running.
    bool m_selecting = false;
    /// Set from errorOccurred and kept, so a later Ready does not paper over an
    /// engine the machine cannot use. Announcements are still emitted while it
    /// is set - the UI shows what would have been said - but reach no speaker.
    QString m_errorText;
    /// A voice / locale change arrived while the engine was busy speaking and
    /// still has to be applied. Cleared by the next Ready.
    bool m_voicePending = false;

    QPointer<IBackend> m_backend;
    QPointer<SpeechController> m_speech;
    SpeechPlayer *m_player = nullptr;
    QString m_databasePath;
    QList<Utterance> m_onlineQueue;
    std::optional<Utterance> m_current;
    OnlinePhase m_phase = OnlinePhase::Idle;
    quint64 m_onlineGeneration = 0;
    bool m_localStarted = false;
    bool m_pumpScheduled = false;
    int m_onlineReplyTimeoutMs = kDefaultOnlineReplyTimeoutMs;
    QTimer m_replyTimer;
    QTimer m_localTimer;
    /// Error codes already toasted this session.
    QSet<QString> m_toastedCodes;
};

} // namespace mr
