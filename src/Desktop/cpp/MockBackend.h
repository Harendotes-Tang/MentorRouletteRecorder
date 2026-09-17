#pragma once

// ---------------------------------------------------------------------------
// Deterministic in-memory backend.
//
// It reproduces the sample dataset of the UI prototype
// (DOC/mentor-recorder-v2.dc.html, genRuns()): the same LCG seeded with
// 20260904, 96 runs spread over 60 days, the same result weights, ~2 % unknown
// duty, ~6 % unknown job, one corrected run carrying a revision, one manual
// run and one soft-deleted run.
//
// Every statistic is computed with the definitions of
// docs/statistics-definitions.md - including the parts that are easy to get
// wrong: CANCELLED_BEFORE_ENTRY stays out of attempt_count, soft-deleted runs
// never count, undefined ratios are null (not zero) and DISCONNECTED /
// INTERRUPTED / UNKNOWN are never folded into leave_rate.
// ---------------------------------------------------------------------------

#include "IBackend.h"

#include <QDateTime>
#include <QJsonArray>
#include <QList>
#include <QString>

namespace mr {

class MockBackend : public IBackend
{
    Q_OBJECT

public:
    enum class LiveMode { None, Matched, Entered };

    explicit MockBackend(QObject *parent = nullptr);

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }
    QString connectionDetail() const override;

    BackendReply *request(const QString &messageType,
                          const QJsonObject &payload = {}) override;

    /// Simulate a machine without the Npcap driver.
    void setNpcapMissing(bool missing);
    /// Synthetic-only UI capability fixture; never used by IpcBackend.
    void setRecordingFixture(const QString &state) { m_recordingFixture = state; }
    bool npcapMissing() const { return m_npcapMissing; }

    /// Simulate the live run state shown on the dashboard.
    void setLiveMode(LiveMode mode);
    LiveMode liveMode() const { return m_liveMode; }

    /// Deterministic test-only validation state used by Qt screenshots. The
    /// paths and counters are synthetic and never represent production proof.
    void setValidationFixture(const QString &state);

    /// Synthetic candidate ledger for offline review screenshots: two sessions,
    /// several connections and anchors across query pages. Never changes runs.
    void setCandidateFixture();

    /// Keep a terminal validation fixture as history while simulating a
    /// currently running formal capture for projection screenshots.
    void setFormalCaptureRunning(bool running);

    /// Synthetic 本机校准 fixture: "observing", "ready", "blocked" or "done".
    /// Screenshot and test only: this backend has never seen a packet and never
    /// writes a profile.
    void setCalibrationFixture(const QString &state);
    QString calibrationFixture() const { return m_calibrationState; }

    /// Synthetic 共享校准 fixture: "fetching", "verifying", "consent", "verified",
    /// "rejected", "unavailable", "user-rejected", "none-for-build" or "share".
    /// Arms a 本机校准 fixture when none is set: "share" is a finished local
    /// calibration whose code GetCalibrationShareCode hands out; every other state
    /// is still observing ("verified" records with the shared profile while
    /// calibration stays armed, as the Collector does until that profile records
    /// one whole duty). Nothing here was ever downloaded, decoded or verified.
    void setSharedCalibrationFixture(const QString &state);
    QString sharedCalibrationFixture() const { return m_sharedState; }

    /// Synthetic 在线语音 fixture:
    /// "azure" (Azure configured, key stored), "openai" (OpenAI-compatible
    /// configured), "unconfigured" (nothing chosen) or "fail" (Azure
    /// configured, every SynthesizeSpeech refused with ERR_SPEECH_NETWORK).
    /// Nothing is ever sent anywhere: SynthesizeSpeech writes a short silent
    /// WAV into <dataDirectory()>/tts-cache/. The key itself is never kept.
    void setSpeechFixture(const QString &state);
    QString speechFixture() const { return m_speechState; }
    static bool isSpeechFixture(const QString &state);
    /// The desktop voice a fixture stands for ("azure:…" / "openai:…").
    static QString speechFixtureVoiceId(const QString &state);
    /// Where the mock's data lives: GetStatus.database_path names
    /// <dir>/mentor_recorder.db and SynthesizeSpeech writes <dir>/tts-cache/.
    /// Defaults to <temp>/MentorRecorder-mock; nothing but the WAVs is written.
    void setDataDirectory(const QString &directory) { m_dataDirectory = directory; }
    QString dataDirectory() const;
    /// SynthesizeSpeech requests answered so far (tests).
    int speechSynthesisCount() const { return m_speechSynthesisCount; }

    /// Pretend the Collector's daily update check found a newer release. The
    /// version and the page it names are synthetic; nothing is ever fetched.
    void setUpdateAvailable(bool available);
    bool updateAvailable() const { return m_updateAvailable; }
    /// The `changes` object of the last accepted UpdateCaptureSettings, so a
    /// test can pin what a switch actually put on the wire.
    QJsonObject lastCaptureSettingsUpdate() const { return m_lastCaptureSettingsUpdate; }

    /// Simulate the live-validated failure mode of
    /// docs/live-validation-guide.md section 6: capture was started after the
    /// client had already logged in, so nothing decodes and the Collector says
    /// so through midstream_suspected + hint.
    void setMidstreamSuspected(bool suspected);
    bool midstreamSuspected() const { return m_midstreamSuspected; }

    /// $defs/LiveEvent.sequence of the last emitted event.
    qint64 liveSequence() const { return m_liveSequence; }

    /// Last marker received through the typed request wrapper. This test-only
    /// seam verifies that Space activates the focused marker's exact payload.
    QString lastValidationMarker() const { return m_lastValidationMarker; }

    /// Emit one StateChanged live event, as the real Collector would on a
    /// state-machine transition. Used by setLiveMode(), by capture start/stop
    /// and by the tests that exercise the TTS announcements.
    void emitStateChanged(const QString &state);

    /// Emit one RunFinished live event carrying \a state and the current run,
    /// as the Collector's bus always does for a terminal state.
    void emitRunFinished(const QString &state);

    /// Walk the simulated run through MENTOR_MATCHED then ENTERED_DUTY then
    /// finalState, emitting one live event per transition.
    void simulateRunTransitions(const QString &finalState);

    /// The instant the dataset is anchored to. Fixed so screenshots and tests
    /// are reproducible.
    QDateTime now() const { return m_now; }

    /// Test seam: replace the dataset with a hand-written one.
    void resetRuns(const QJsonArray &runs);
    /// Test seam: set the achievement settings directly.
    void setAchievement(int goalCount, int baselineCompletedCount);

    /// Statistics helpers, public so the unit tests can call them without
    /// going through the asynchronous request path.
    QJsonObject dashboardStats(const QJsonObject &filter,
                               const QString &granularity = {}) const;
    /// $defs/TrendSeries, built by the same rules the Collector's SQL uses so
    /// the dashboard cannot look different offline than it does live.
    QJsonObject trendSeries(const QJsonObject &filter, const QString &granularity) const;
    QJsonObject resultStats(const QJsonObject &filter) const;
    QJsonArray dungeonStats(const QJsonObject &filter) const;
    QJsonArray jobStats(const QJsonObject &filter) const;

    /// $defs/GetReflectionSummary response, public so the tests can read the
    /// counts without going through the asynchronous request path.
    QJsonObject reflectionSummary(int recentLimit) const;

private:
    // -- dataset ------------------------------------------------------------
    void generateRuns();
    /// Seeds the six prototype reflections (DOC/表单提交后设计, const RF=).
    void seedReflections();
    QJsonObject captureStatus() const;
    /// $defs/CalibrationStatus for the current fixture, or an empty object
    /// when no calibration fixture is armed.
    QJsonObject calibrationStatus() const;
    /// A profile this machine calibrated is in force: either calibration just
    /// finished (done) or it finished in an earlier run and nothing is being
    /// calibrated any more (idle, the state after a restart).
    bool localProfileBound() const;
    /// $defs/ConfirmCalibrationRequest against the fixture timeline.
    QJsonObject applyCalibrationVerdicts(const QJsonObject &payload, QString *errorCode,
                                         QString *errorMessage);
    // -- 共享校准 (MockBackendShared.cpp) ------------------------------------
    /// $defs/SharedCalibrationStatus for the shared fixture.
    QJsonObject sharedCalibrationStatus() const;
    /// Another player's profile is in force, whether or not the match and the
    /// duty entry are still being audited beside it (verified, verified-auditing).
    bool sharedProfileBound() const;
    static bool isSharedCalibrationMessage(const QString &messageType);
    /// The five shared-calibration requests against the fixture.
    QJsonObject applySharedCalibration(const QString &messageType, const QJsonObject &payload,
                                       QString *errorCode, QString *errorMessage,
                                       QJsonObject *errorDetails);
    QJsonObject shareCodePayload(QString *errorCode, QString *errorMessage,
                                 QJsonObject *errorDetails) const;
    QJsonObject importCodePayload(const QJsonObject &payload, QString *errorCode,
                                  QString *errorMessage);
    QString checkSharedOutcome();
    QJsonObject rejectSharedPayload();
    /// Announces a shared-state change the way the Collector does.
    void emitCalibrationChangedLater();
    // -- 在线语音 (MockBackendSpeech.cpp) ------------------------------------
    static bool isSpeechMessage(const QString &messageType);
    QJsonObject applySpeech(const QString &messageType, const QJsonObject &payload,
                            QString *errorCode, QString *errorMessage,
                            QJsonObject *errorDetails);
    /// $defs/SpeechSettings for the in-memory state.
    QJsonObject speechSettings() const;
    QJsonObject updateSpeechPayload(const QJsonObject &payload, QString *errorCode,
                                    QString *errorMessage);
    QJsonObject synthesizePayload(const QJsonObject &payload, QString *errorCode,
                                  QString *errorMessage, QJsonObject *errorDetails);
    QJsonObject captureValidationStatus() const;
    /// $defs/CaptureSettings, defaulted the way a fresh Collector would.
    QJsonObject captureSettings() const;
    /// Deterministic $defs/RunEventEntry rows for one run. Synthetic: the
    /// opcodes and hashes are invented, and the mock says so in the UI.
    QJsonArray runEvents(const QString &runId) const;
    QJsonArray recentParserErrors() const;
    QJsonObject collectorStatus() const;
    QJsonObject currentRun() const;

    // -- querying -----------------------------------------------------------
    QList<QJsonObject> selectRuns(const QJsonObject &filter, bool forStatistics) const;
    QJsonObject queryRunsPayload(const QJsonObject &payload) const;
    QJsonObject queryCandidatePayload(const QJsonObject &payload, QString *errorCode,
                                       QString *errorMessage) const;
    QJsonObject reviewCandidatePayload(const QJsonObject &payload, QString *errorCode,
                                        QString *errorMessage);
    QJsonObject exportCandidatePayload(const QJsonObject &payload) const;
    void emitCaptureStatusChanged();
    /// The $defs/LiveEvent fields every emitted event carries: a fresh
    /// event_id, the two type tokens, the timestamp and the next sequence.
    QVariantMap liveEventEnvelope(const QString &eventType, const QString &kind);
    /// Validate the complete partial update before replacing any mock setting.
    QJsonObject applyCaptureSettings(const QJsonObject &payload, QString *errorCode,
                                      QString *errorMessage);

    // -- mutations ----------------------------------------------------------
    QJsonObject applyMutation(const QString &messageType, const QJsonObject &payload,
                              QString *errorCode, QString *errorMessage);
    /// $defs/SetRunReflectionRequest. Never bumps a revision and never writes
    /// a run_revisions row (spec 1.4).
    QJsonObject applyReflection(const QJsonObject &payload, QString *errorCode,
                                QString *errorMessage);

    int indexOfRun(const QString &runId) const;
    void touchRun(QJsonObject &run, const QString &changeKind, const QString &reason,
                  const QJsonArray &changes);

    QJsonArray m_runs;
    QJsonArray m_revisions;
    QJsonArray m_candidateObservations;
    QJsonArray m_candidateReviews;
    QJsonObject m_captureSettings;
    QDateTime m_now;
    int m_goalCount = 2000;
    int m_baselineCompletedCount = 1374;
    bool m_npcapMissing = false;
    bool m_capturing = true;
    bool m_midstreamSuspected = false;
    bool m_updateAvailable = false;
    QJsonObject m_lastCaptureSettingsUpdate;
    /// Monotonic $defs/LiveEvent.sequence handed to every emitted event.
    qint64 m_liveSequence = 0;
    QString m_recordingFixture;
    QString m_calibrationState;
    QString m_sharedState;
    /// provider / azure_region / openai_base_url / openai_model / voice.
    QJsonObject m_speech;
    bool m_speechHasKey = false;
    QString m_speechState;
    QString m_dataDirectory;
    int m_speechSynthesisCount = 0;
    QString m_validationState = QStringLiteral("IDLE");
    int m_validationMarkerCount = 0;
    QString m_lastValidationMarker;
    LiveMode m_liveMode = LiveMode::Entered;
};

} // namespace mr
