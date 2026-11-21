#pragma once

// ---------------------------------------------------------------------------
// Deterministic in-memory backend.
//
// It reproduces the sample dataset of the UI prototype
// (DOC/mentor-recorder-v2.dc.html, genRuns()): the same LCG seeded with
// 20260904, 96 runs spread over 60 days, the same result weights, ~2 % unknown
// duty, ~6 % unknown job, one corrected run carrying a revision, one manual
// run and one soft-deleted run. Unlike the prototype, an UNKNOWN run has an
// end time: a stored run the Collector let go never reads as still in flight.
//
// Every statistic is computed with the definitions of
// docs/statistics-definitions.md - including the parts that are easy to get
// wrong: CANCELLED_BEFORE_ENTRY stays out of attempt_count, soft-deleted runs
// never count, undefined ratios are null (not zero) and DISCONNECTED /
// INTERRUPTED / UNKNOWN are never folded into leave_rate.
// ---------------------------------------------------------------------------

#include "IBackend.h"

#include <QDateTime>
#include <QHash>
#include <QJsonArray>
#include <QList>
#include <QSet>
#include <QString>
#include <QTimeZone>
#include <QVariantMap>

class QTimer;

namespace mr {

class MockBackend : public IBackend
{
    Q_OBJECT

public:
    enum class LiveMode : quint8 { None, Matched, Entered };

    explicit MockBackend(QObject *parent = nullptr);

    QString backendName() const override { return QStringLiteral("mock"); }
    bool isConnected() const override { return true; }
    QString connectionDetail() const override;

    BackendReply *request(const QString &messageType,
                          const QJsonObject &payload = {}) override;
    /// Like request(), under  requestId, the way the Collector keys its mutations
    /// (RunMutationService.Apply): a mutation resent under the id of one already applied is
    /// answered from that first outcome, flagged as a replay and applied once, and the same
    /// id for a different request is refused with ERR_IDEMPOTENCY_CONFLICT.
    BackendReply *requestWithId(const QString &messageType, const QJsonObject &payload,
                                const QString &requestId) override;

    /// Holds replies to \a messageType back by \a milliseconds (every type when
    /// \a messageType is empty; 0 removes the delay). Replies otherwise arrive
    /// on the next event-loop turn, in the order they were asked, so a test of
    /// a late or out-of-order Collector answer sets one here.
    void setReplyDelay(int milliseconds, const QString &messageType = {});

    /// Simulate a machine without the Npcap driver.
    void setNpcapMissing(bool missing);
    /// Synthetic-only UI capability fixture; never used by IpcBackend.
    void setRecordingFixture(const QString &state);
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

    /// A GLOBAL client on 2026.11.03 with no profile that records it and the shipped
    /// global.2026.11.03.candidate (the player's 第二轮 on the 新区服采证 page). The Collector
    /// follows the game, so capture runs while the game does and candidate validation is on.
    /// Test only.
    void setCandidateRoundFixture(bool gameRunning);
    /// $defs/CandidateHypothesis rows of protocol-profiles/global/global.2026.11.03.candidate.json.
    static QJsonArray candidateRoundHypotheses();
    /// $defs/CandidateRoundGuesses the round fixture reports while its validation is on: by
    /// default a roulette and the first job named, the second job by id alone and no duty yet.
    /// Setting them reports a CaptureStatusChanged at once. Test only.
    void setCandidateRoundGuesses(const QJsonObject &guesses);
    /// The payload of the last ExportCandidateEvidence, so a test can read its round_answers.
    QJsonObject lastCandidateExportRequest() const { return m_lastCandidateExportRequest; }

    /// Keep a terminal validation fixture as history while simulating a
    /// currently running formal capture for projection screenshots.
    void setFormalCaptureRunning(bool running);

    /// Synthetic 本机校准 fixture: "observing", "ready", "blocked" or "done".
    /// Screenshot and test only: this backend has never seen a packet and never
    /// writes a profile.
    void setCalibrationFixture(const QString &state);
    QString calibrationFixture() const { return m_calibrationState; }

    /// Synthetic 共享校准 fixture: "fetching", "verifying", "consent", "verified",
    /// "verified-auditing", "verified-proven", "imported-published",
    /// "imported-unpublished", "rejected", "unavailable", "user-rejected",
    /// "none-for-build" or "share".
    /// Arms a 本机校准 fixture when none is set: "share" is a finished local
    /// calibration whose code GetCalibrationShareCode hands out with basis
    /// CALIBRATED; "verified-proven" is a shared profile that recorded one whole
    /// duty, so calibration is DONE and its code is handed out with basis VERIFIED
    /// (plans/shared-calibration.md §20.6); every other state is still observing
    /// ("verified" records with the shared profile while calibration stays armed,
    /// as the Collector does until that profile records one whole duty, and its
    /// code is refused as SHARED_NOT_PROVEN). Nothing here was ever downloaded,
    /// decoded or verified.
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

    /// 下载并安装 (MockBackendUpdate.cpp): StartUpdateDownload and
    /// CancelUpdateDownload against a simulated download that never touches the
    /// network. It declares kMockInstallerBytes, counts up in eight steps of
    /// setUpdateDownloadStepMs(), verifies for one more and is READY with a small
    /// synthetic file under <dataDirectory()>/updates/ - not a program, and in a
    /// folder the Desktop's own check refuses unless a test points it there.
    /// false: an older Collector - no `download` object, both messages refused.
    void setUpdateDownloadSupported(bool supported) { m_updateDownloadSupported = supported; }
    bool updateDownloadSupported() const { return m_updateDownloadSupported; }
    /// A held state for screenshots and tests: "downloading" (45 %), "verifying",
    /// "ready" or "failed". Also makes a newer version known. A held download
    /// does not advance until it is started again or cancelled.
    void setUpdateDownloadFixture(const QString &state);
    static bool isUpdateDownloadFixture(const QString &state);
    /// $defs/UpdateDownload.mirror_outcome of a finished (READY or FAILED) download, as the
    /// Collector reports what became of a GitCode offer; empty sends none. This backend never
    /// probes a mirror, so only a test sets it; starting a download clears it.
    void setUpdateMirrorOutcome(const QString &outcome) { m_updateMirrorOutcome = outcome; }
    void setUpdateDownloadStepMs(int milliseconds);
    /// StartUpdateDownload / CancelUpdateDownload requests answered, and the
    /// payload of the last StartUpdateDownload.
    int startUpdateDownloadCount() const { return m_startUpdateDownloadCount; }
    QJsonObject lastStartUpdateDownload() const { return m_lastStartUpdateDownload; }
    int cancelUpdateDownloadCount() const { return m_cancelUpdateDownloadCount; }
    /// The size the simulated server declares.
    static constexpr qint64 kMockInstallerBytes = 60LL * 1024 * 1024;
    /// The `changes` object of the last accepted UpdateCaptureSettings, so a
    /// test can pin what a switch actually put on the wire.
    QJsonObject lastCaptureSettingsUpdate() const { return m_lastCaptureSettingsUpdate; }

    /// DiscardCalibration requests received, and the payload of the last one, so
    /// a test can tell 重新观察 from 重新校准 from 恢复上一份本机校准 by what
    /// actually went on the wire.
    int discardCalibrationCount() const { return m_discardCalibrationCount; }
    QJsonObject lastDiscardCalibration() const { return m_lastDiscardCalibration; }

    /// Pretend a local profile the player retired is still on disk, so the
    /// rollback is offered. Synthetic: this backend has no profile directory.
    void setRetiredLocalProfileAvailable(bool available)
    {
        m_retiredLocalProfileAvailable = available;
    }
    bool retiredLocalProfileAvailable() const { return m_retiredLocalProfileAvailable; }

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
    bool validationIdentityProbe() const { return m_validationIdentityProbe; }
    /// The validation fixture as a guided identity round; \a complete confirms its three logins.
    void setValidationIdentityFixture(bool complete);
    /// identity_steps of the mock's CaptureValidationStatus: every confirmed step named someone.
    QJsonValue validationIdentitySteps() const;

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
    /// Test seam for an older import library, including retained purge tombstones.
    void resetRuns(const QJsonArray &runs, const QHash<QString, QString> &importFingerprints);
    /// Test seam: set the achievement settings directly.
    void setAchievement(int goalCount, int baselineCompletedCount);
    /// Test seam: set only the named region; omitted-region callers retain CN semantics.
    /// Addresses the region's current character profile, created when the region has none.
    void setAchievement(const QString &region, int goalCount, int baselineCompletedCount);

    // -- 角色档案 (MockBackendProfiles.cpp) -----------------------------------
    /// Test and --mock-profiles seam: adds a profile named \a displayName to \a region the
    /// way CreateCharacterProfile does, without its validation, and returns its id.
    int addCharacterProfile(const QString &region, const QString &displayName, int goalCount,
                            int baselineCompletedCount, bool makeCurrent);
    /// --mock-profiles: CN holds two named characters with different progress, the
    /// second one current, and a few CN runs assigned to no character.
    void setCharacterProfilesFixture();
    /// $defs/DashboardStats.profiles as it stands: every profile with its own progress.
    QJsonArray characterProfiles() const;
    /// The profile a stored run belongs to: its profile_id, 0 for an explicit null, and
    /// for a hand-written run without the field the first profile of its region.
    int runProfileId(const QJsonObject &run) const;

    // -- 自动识别角色 (MockBackendIdentities.cpp) -------------------------------
    /// Adds a recognised character in \a region. PENDING and IGNORED characters have no
    /// profile; BOUND belongs to the current profile. It takes up to \a runCount sample
    /// runs. \a registerOriginal marks it for upgraded-profile confirmation. Returns its id.
    int addCharacterIdentity(const QString &region, const QString &state, int runCount,
                             bool registerOriginal = false);
    /// Dashboard identities, including bound characters awaiting upgraded-profile confirmation.
    QVariantList characterIdentities() const { return identitiesJson().toVariantList(); }
    /// Test and --mock-pending-identity seam: the character the game is logged in as (0 =
    /// nobody, the default). The Collector decides this from live traffic; BIND and
    /// create-with-identity make the profile current only for the logged-in character.
    void setLoggedInCharacterIdentity(int identityId) { m_loggedInIdentityId = identityId; }
    /// Test seam: the identity disappears, as if another client or a restored database had
    /// removed it; a later request about it is ERR_NOT_FOUND.
    void dropCharacterIdentity(int identityId);

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
    /// A sentence of the Collector's catalogue (contracts/collector-texts.json),
    /// sent the way the Collector sends it: the Chinese in the legacy field and
    /// the reference in its `*_ref` companion.
    struct CollectorSentence
    {
        QString chinese;
        QJsonObject ref;
    };
    static CollectorSentence collectorSentence(const char *key, const QVariantMap &args = {});
    /// Refuses the way the Collector does with a catalogue sentence: \a code in
    /// \a errorCode, the Chinese in \a errorMessage, and the reference kept for
    /// the reply's message_ref. Returns an empty result.
    QJsonObject refuseWith(QString *errorCode, QString *errorMessage, const QString &code,
                           const char *key, const QVariantMap &args = {});
    /// Answers  reply on the next event-loop turn, or after setReplyDelay()'s delay.
    void deliver(BackendReply *reply, const QJsonObject &result, const QString &errorCode,
                 const QString &errorMessage, const QJsonObject &errorDetails,
                 const QJsonObject &errorRef);

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
    /// duty entry are still being audited beside it or it already proved itself
    /// (verified, verified-auditing, verified-proven).
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
    /// GetCalibrationEvidence / GetTemplateObservation (MockBackendShared.cpp). This
    /// backend never completed a healthy session or sealed an observation, so the answer
    /// is always unavailable, with the reason the Collector gives.
    QJsonObject calibrationEvidence(const QString &messageType, const QJsonObject &payload,
                                    QString *errorCode, QString *errorMessage);
    // -- 导入本人记录 (MockBackend.cpp) ---------------------------------------
    /// PreviewRunImport: candidate rows (every source kind) and JSON sources, classified as
    /// a whole against this dataset and handed out whole or in frozen pages. Tables,
    /// workbooks and backups are refused: this backend does not parse them.
    QJsonObject previewRunImport(const QJsonObject &payload, QString *errorCode, QString *errorMessage);
    /// CommitRunImport: saves the selected rows of a preview once, under a receipt.
    QJsonObject commitRunImport(const QJsonObject &payload, QString *errorCode, QString *errorMessage);
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
    // -- 下载并安装 (MockBackendUpdate.cpp) ----------------------------------
    static bool isUpdateDownloadMessage(const QString &messageType);
    /// {update: $defs/UpdateStatus} after one of the two messages.
    QJsonObject applyUpdateDownload(const QString &messageType, const QJsonObject &payload,
                                    QString *errorCode, QString *errorMessage, QJsonObject *errorDetails);
    /// $defs/UpdateDownload as it stands.
    QJsonObject updateDownload() const;
    /// One simulated step: bytes arrive, then the check, then READY.
    void advanceUpdateDownload();
    /// READY with a freshly written synthetic file, or FAILED / DISK_FAILED.
    void finishUpdateDownload();
    /// FAILED for \a failure, with the Collector's sentence \a key.
    void failUpdateDownload(const QString &failure, const char *key);
    QString mockInstallerPath() const;
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
    /// selectRuns(filter, false) in the order a $defs/RunSort asks for; an empty \a sort is
    /// entered_at_utc descending (RunSort.Default). QueryRuns and the exports share it.
    QList<QJsonObject> sortedRuns(const QJsonObject &filter, const QJsonObject &sort) const;
    /// RequestParsers.Sort: a sort with an undeclared field, a direction other than asc /
    /// desc, or a key the contract does not declare is refused as ERR_BAD_REQUEST. Omitted or
    /// null is the default and passes.
    bool refusedSort(const QJsonObject &payload, QString *errorCode, QString *errorMessage);
    QJsonObject queryRunCalendarDaysPayload(const QJsonObject &payload) const;
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
    int m_historyRetentionDays = 30;
    // SetDisplayTimeZone: the zone the imitated Collector cuts calendar days and the
    // history_date anchor of a date-only record in; the system zone until the Desktop
    // sends another, as the Collector does.
    QTimeZone m_displayZone = QTimeZone(QTimeZone::LocalTime);
    QStringList m_pendingImageCleanup;
    QDateTime m_now;
    struct AchievementSettings {
        int goalCount = 2000;
        int baselineCompletedCount = 0;
        QDateTime baselineEffectiveAt;
        QDateTime updatedAt;
        QString auditEventId;
    };
    /// The settings of the region's current profile. A region without a profile reads
    /// the defaults: it starts at zero, never at CN's baseline.
    AchievementSettings achievementSettings(const QString &region) const;
    /// One character_profiles row. Ids are never reused, like the Collector's AUTOINCREMENT.
    struct CharacterProfileEntry
    {
        int id = 0;
        QString region;
        QString displayName;
        bool current = false;
        AchievementSettings settings;
    };
    QList<CharacterProfileEntry> m_profiles;
    int m_nextProfileId = 1;
    /// CharacterProfileRepository.EnsureCurrent: the region's current profile, created
    /// unnamed with the default goal and a zero baseline when the region has none.
    int ensureCurrentProfile(const QString &region);
    const CharacterProfileEntry *findProfile(int profileId) const;
    CharacterProfileEntry *findProfile(int profileId);
    bool profileNameTaken(const QString &region, const QString &name, int exceptId) const;
    /// Checks the target and applies display_name when supplied. The enclosing mutation notifies once.
    bool applyProfileName(CharacterProfileEntry *profile, const QJsonValue &requestedName,
                          QString *errorCode, QString *errorMessage);
    const CharacterProfileEntry *currentProfile(const QString &region) const;
    /// $defs/CharacterProfile for \a profile, its progress counted over the stored runs.
    QJsonObject profileJson(const CharacterProfileEntry &profile) const;
    /// Profiles of \a region, oldest first.
    QList<const CharacterProfileEntry *> profilesOf(const QString &region) const;
    /// Runs of \a region assigned to no profile and not deleted.
    int unassignedRunCount(const QString &region) const;
    /// RunFilter.profile_id / profile_unassigned against one stored run.
    bool runMatchesProfileFilter(const QJsonObject &run, const QJsonObject &filter) const;
    /// RequestParsers: profile_id and profile_unassigned together are ERR_BAD_REQUEST.
    bool refusedProfileFilter(const QJsonObject &filter, QString *errorCode, QString *errorMessage);
    static bool isCharacterProfileMessage(const QString &messageType);
    /// The three profile messages, with CharacterProfileService's refusals.
    QJsonObject applyCharacterProfile(const QString &messageType, const QJsonObject &payload,
                                      QString *errorCode, QString *errorMessage,
                                      QJsonObject *errorDetails);
    /// History's profile assignment and region migration in the demo backend.
    QJsonObject assignRunsToProfile(const QJsonObject &payload, QString *errorCode,
                                    QString *errorMessage, QJsonObject *errorDetails, bool previewRegion = false);
    /// The profile a newly created run of \a region is filed under (CharacterProfileService.Resolve):
    /// \a requested, which must exist and belong to the region, or the region's current one.
    /// Returns 0 after a refusal.
    int resolveProfileForNewRun(const QString &region, const QJsonValue &requested,
                                QString *errorCode, QString *errorMessage);
    /// One character_identities row. boundProfileId 0: bound to no profile, and listed.
    struct CharacterIdentityEntry
    {
        int id = 0;
        QString region;
        QString state = QStringLiteral("PENDING");
        QDateTime firstSeen;
        QDateTime lastSeen;
        int boundProfileId = 0;
        /// The runs recorded while this character was logged in; the live ones still
        /// assigned to no profile are its run_count, and move with a BIND.
        QStringList runIds;
    };
    QList<CharacterIdentityEntry> m_identities;
    /// Upgraded profile -> proposed identity (zero while waiting for another login).
    QHash<int, int> m_registrationCandidates;
    void replaceRegistrationCandidate(int profileId);
    int m_nextIdentityId = 1;
    int m_loggedInIdentityId = 0;
    /// BatchMutateRuns assign_profile clears the identity stamp of the runs it moves.
    void forgetIdentityStamps(const QStringList &runIds);
    CharacterIdentityEntry *findIdentity(int identityId);
    /// The identity's live runs still assigned to no profile.
    QStringList unassignedIdentityRuns(const CharacterIdentityEntry &identity) const;
    /// Dashboard identities, with each region's registration candidate first.
    QJsonArray identitiesJson() const;
    /// How many identities are bound to \a profileId (CharacterProfile.identity_count).
    int boundIdentityCount(int profileId) const;
    /// RegionalAchievement.identification of \a region.
    QString identificationOf(const QString &region) const;
    /// Binds \a identity to \a profileId the way the Collector does: its unassigned runs
    /// move there, the profile becomes current. Returns how many runs moved.
    int bindIdentity(CharacterIdentityEntry &identity, int profileId);
    /// Validates CreateCharacterProfile.identity_id; returns the identity or nullptr after
    /// a refusal. Absent: nullptr without a refusal (*refused stays false).
    CharacterIdentityEntry *identityForNewProfile(const QJsonObject &payload, const QString &region,
                                                  bool *refused, QString *errorCode, QString *errorMessage);
    /// UpdateCharacterProfile.forget_identities: the profile's identities back to PENDING.
    void forgetIdentitiesOf(int profileId);
    QJsonObject resolveCharacterIdentity(const QJsonObject &payload, QString *errorCode,
                                         QString *errorMessage);
    void emitStatsInvalidatedLater();
    static QJsonArray declaredCandidateHypotheses();
    /// What setCandidateRoundFixture() makes GetStatus.capture say.
    void applyCandidateRoundFixture(QJsonObject &status) const;
    bool m_candidateRound = false;
    bool m_candidateRoundGameRunning = false;
    QJsonObject m_candidateRoundGuesses;
    QJsonObject m_lastCandidateExportRequest;
    QString captureRegion() const;
    bool unsupportedCaptureRegion() const;
    bool m_npcapMissing = false;
    bool m_capturing = true;
    bool m_midstreamSuspected = false;
    bool m_updateAvailable = false;
    bool m_updateDownloadSupported = true;
    /// $defs/UpdateDownload.state, and the rest of the simulated download.
    QString m_updateDownloadState = QStringLiteral("IDLE");
    qint64 m_updateReceivedBytes = 0;
    QString m_updateFilePath;
    QString m_updateSha256;
    QString m_updateFailure;
    QString m_updateMessage;
    QJsonObject m_updateMessageRef;
    QString m_updateMirrorOutcome;
    /// Steps the simulated download; stopped while a fixture holds it.
    QTimer *m_updateDownloadTimer = nullptr;
    int m_updateDownloadStepMs = 150;
    int m_startUpdateDownloadCount = 0;
    int m_cancelUpdateDownloadCount = 0;
    QJsonObject m_lastStartUpdateDownload;
    QJsonObject m_lastCaptureSettingsUpdate;
    int m_discardCalibrationCount = 0;
    QJsonObject m_lastDiscardCalibration;
    bool m_retiredLocalProfileAvailable = false;
    /// Monotonic $defs/LiveEvent.sequence handed to every emitted event.
    qint64 m_liveSequence = 0;
    QString m_recordingFixture;
    qint64 m_fixtureListeningPackets = 0;
    int m_selectedGameProcessId = 0;
    QString m_calibrationState;
    QString m_sharedState;
    /// provider / azure_region / openai_base_url / openai_model / voice.
    QJsonObject m_speech;
    bool m_speechHasKey = false;
    QString m_speechState;
    QString m_dataDirectory;
    int m_speechSynthesisCount = 0;
    QString m_validationState = QStringLiteral("IDLE");
    /// setReplyDelay(): message type ("" = every type) -> milliseconds.
    QHash<QString, int> m_replyDelays;
    int m_validationMarkerCount = 0;
    QString m_lastValidationMarker;
    // The guided identity round: whether the session was started for it, and its steps so far.
    bool m_validationIdentityProbe = false;
    QSet<QString> m_validationIdentitySteps;
    LiveMode m_liveMode = LiveMode::Entered;
    /// The last refuseWith(): the reply's message_ref while its message is still
    /// the one refuseWith() wrote.
    QJsonObject m_errorRef;
    QString m_errorRefMessage;
    /// The first outcome of every mutation applied under a request_id (requestWithId).
    struct AppliedRequest
    {
        QString messageType;
        QByteArray fingerprint;
        QJsonObject result;
    };
    QHash<QString, AppliedRequest> m_appliedRequests;
    /// A classified import batch: `whole` is the complete PreviewRunImport answer; `entries`
    /// keep each row's run and identity for the commit. pageSize 0 is an unpaged preview.
    struct ImportPreview
    {
        QString previewId;
        QJsonObject whole;
        QJsonArray entries;
        int pageSize = 0;
    };
    /// Newest last, at most eight (RunImportService.MaxPreviews).
    QList<ImportPreview> m_importPreviews;
    /// run_import_batches: what each commit answered, by preview and by request.
    struct ImportReceipt
    {
        QString previewId;
        QString requestId;
        QByteArray selection;
        QJsonObject response;
    };
    QList<ImportReceipt> m_importReceipts;
    /// run_import_metadata.source_fingerprint -> run_id of every run imported here.
    QHash<QString, QString> m_importFingerprints;
    /// The request_id, and the payload's fingerprint, request() is answering under.
    QString m_requestId;
    QByteArray m_requestFingerprint;
};

} // namespace mr
