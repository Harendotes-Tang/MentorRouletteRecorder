#pragma once

#include <QHash>
#include <QJsonObject>
#include <QPointer>
#include <QStringList>
#include <QUrl>
#include <QObject>
#include <QQmlEngine>
#include <QSet>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>

#include "LiveText.h"

#include <functional>

namespace mr {
class AppSettings;
class IBackend;

/// Candidate-only read/review surface. Owns bounded paging and a complete cross-page
/// anchor index; it has no run, statistics, achievement or speech dependency.
class CandidateReviewController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    Q_PROPERTY(QVariantList rows READ rows NOTIFY changed)
    Q_PROPERTY(QVariantMap pageInfo READ pageInfo NOTIFY changed)
    Q_PROPERTY(int page READ page NOTIFY changed)
    Q_PROPERTY(int pageSize READ pageSize CONSTANT)
    Q_PROPERTY(int total READ total NOTIFY changed)
    Q_PROPERTY(QString sessionId READ sessionId WRITE setSessionId NOTIFY changed)
    Q_PROPERTY(QVariantList sessions READ sessions NOTIFY changed)
    Q_PROPERTY(QVariantList anchorPairs READ anchorPairs NOTIFY changed)
    // Plain-language event timeline built from the full ledger: login, queue, pop,
    // inferred duty entry/exit, plain zone changes. One entry per event, never per packet.
    Q_PROPERTY(QVariantList timeline READ timeline NOTIFY changed)
    Q_PROPERTY(bool loading READ loading NOTIFY changed)
    Q_PROPERTY(bool indexLoading READ indexLoading NOTIFY changed)
    Q_PROPERTY(bool indexComplete READ indexComplete NOTIFY changed)
    Q_PROPERTY(int indexLoadedCount READ indexLoadedCount NOTIFY changed)
    Q_PROPERTY(QString indexMessage READ indexMessage NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    Q_PROPERTY(bool hasNewObservations READ hasNewObservations NOTIFY changed)
    Q_PROPERTY(QString reviewBusyId READ reviewBusyId NOTIFY changed)
    Q_PROPERTY(QString reviewError READ reviewError NOTIFY changed)
    Q_PROPERTY(bool exporting READ exporting NOTIFY changed)
    Q_PROPERTY(QVariantMap exportResult READ exportResult NOTIFY changed)
    Q_PROPERTY(QString exportError READ exportError NOTIFY changed)
    Q_PROPERTY(bool candidateSettingsBusy READ candidateSettingsBusy NOTIFY changed)
    Q_PROPERTY(QString candidateSettingsError READ candidateSettingsError NOTIFY changed)
    /// Hypothesis names the live candidate_observed events named since the last startRound(),
    /// distinct and in arrival order: the player's round shows how many of the declared ones it saw.
    Q_PROPERTY(QStringList seenHypotheses READ seenHypotheses NOTIFY changed)
    /// True from finishRound() until its export has answered (or the switch-off failed).
    Q_PROPERTY(bool finishing READ finishing NOTIFY changed)
    Q_PROPERTY(bool roundSettingsKnown READ roundSettingsKnown NOTIFY changed)
    Q_PROPERTY(bool scopedRoundsSupported READ scopedRoundsSupported NOTIFY changed)
    Q_PROPERTY(bool finishPending READ finishPending NOTIFY changed)
    Q_PROPERTY(bool roundRecoverable READ roundRecoverable NOTIFY changed)
    Q_PROPERTY(bool canDiscardRoundFinish READ canDiscardRoundFinish NOTIFY changed)
    Q_PROPERTY(QString roundId READ roundId NOTIFY changed)
    Q_PROPERTY(QVariantMap roundExportResult READ roundExportResult NOTIFY changed)
    Q_PROPERTY(QString roundExportError READ roundExportError NOTIFY changed)
    /// What the tester of the player's round wrote beside the software's guesses
    /// ($defs/CandidateRoundAnswers, blanks left out), sent with every later export until
    /// the next startRound().
    Q_PROPERTY(QVariantMap roundAnswers READ roundAnswers WRITE setRoundAnswers NOTIFY changed)

public:
    explicit CandidateReviewController(QObject *parent = nullptr);
    void setBackend(IBackend *backend);
    void setSettings(AppSettings *settings);
    void setActive(bool active);
    void setTargetOverride(const QString &directory) { m_targetOverride = directory; }
    void observeLiveEvent(const QVariantMap &event);
    void adoptCaptureSettings(const QVariantMap &settings);

    QVariantList rows() const { return m_rows; }
    QVariantMap pageInfo() const;
    int page() const { return m_page; }
    int pageSize() const { return 50; }
    int total() const { return m_total; }
    QString sessionId() const { return m_sessionId; }
    QVariantList sessions() const { return m_sessions; }
    QVariantList anchorPairs() const;
    QVariantList timeline() const;
    bool loading() const { return m_loading; }
    bool indexLoading() const { return m_indexLoading; }
    bool indexComplete() const { return m_indexComplete; }
    int indexLoadedCount() const { return static_cast<int>(m_indexIds.size()); }
    QString indexMessage() const { return m_indexMessage.text(); }
    QString error() const { return m_error.text(); }
    bool hasNewObservations() const { return m_hasNewObservations; }
    QString reviewBusyId() const { return m_reviewBusyId; }
    QString reviewError() const { return m_reviewError.text(); }
    bool exporting() const { return m_exporting; }
    QVariantMap exportResult() const { return m_exportResult; }
    QString exportError() const { return m_exportError.text(); }
    bool candidateSettingsBusy() const { return m_settingsBusy; }
    QString candidateSettingsError() const { return m_settingsError.text(); }
    QStringList seenHypotheses() const { return m_seen; }
    bool finishing() const { return m_finishing; }
    bool roundSettingsKnown() const { return m_roundSettingsKnown; }
    bool scopedRoundsSupported() const { return m_scopedRoundsSupported; }
    bool finishPending() const { return !m_finishRoundId.isEmpty(); }
    bool roundRecoverable() const;
    bool canDiscardRoundFinish() const;
    QString roundId() const { return m_roundId; }
    QVariantMap roundExportResult() const { return m_roundExportResult; }
    QString roundExportError() const { return m_roundExportError.text(); }
    QVariantMap roundAnswers() const { return m_roundAnswers; }
    /// Research-eligible opcodes of  hypotheses ($defs/CandidateHypothesis rows), every group,
    /// in declaration order: what the player's round asks the Collector to keep the content of.
    Q_INVOKABLE QVariantList eligibleOpcodes(const QVariantList &hypotheses) const;
    /// Tests replace the shell hand-off of openExportFolder().
    void setOpenUrl(std::function<bool(const QUrl &)> open) { m_openUrl = std::move(open); }

public Q_SLOTS:
    void refresh();
    void loadPage(int page);
    void setSessionId(const QString &sessionId);
    void review(const QString &observationId, const QString &verdict, const QString &note);
    void exportEvidence();
    void setCandidateEnabled(bool enabled);
    void setResearchOpcodes(const QVariantList &opcodes);
    /// Hands over the round's four answers before the first finish attempt.
    void setRoundAnswers(const QVariantMap &answers);
    /// The player's 国际服候选验证 round (EvidenceCandidateRound.qml): candidate validation on and
    /// every eligible hypothesis of  hypotheses whitelisted, in one UpdateCaptureSettings.
    void startRound(const QVariantList &hypotheses);
    /// Stops the player round and exports its evidence. Repeated attempts reuse its ID and answers.
    void finishRound();
    /// Forgets submitted answers and a retry intent. The Collector keeps its observations.
    void discardRoundFinish();
    /// Stops an older candidate session whose observations have no player round identity.
    void stopUnscopedRound();
    /// Opens the folder of the last maintainer export or player export, respectively.
    void openExportFolder();
    void openRoundExportFolder();

Q_SIGNALS:
    void changed();
    void reviewFinished(const QString &observationId, bool ok, const QString &error);
    void captureSettingsApplied(const QVariantMap &settings);
    void captureSettingsRefreshRequested();
    void captureStatusRefreshRequested();

private:
    void startIndex(bool retry = false);
    void readIndexPage(int page, int generation);
    void indexFailed(const LiveText &message, bool retryable);
    void finishIndex();
    /// "MM-dd HH:mm · <session> · N 条" for one session summary, in the language on screen.
    static QString sessionLabel(const QVariantMap &summary);
    void buildTimeline();
    void writeSettings(const QJsonObject &changes, std::function<void()> applied = {});
    void clearRoundFinishDraft();
    void exportEvidence(const QString &roundId);
    void openExportFolder(const QVariantMap &result, LiveText &error);
    void disconnected();

    IBackend *m_backend = nullptr;
    QPointer<AppSettings> m_settings;
    QVariantList m_rows;
    QVariantList m_sessions;
    QVariantList m_pairs;
    QVariantList m_timeline;
    QHash<QString, QVariantMap> m_indexSessions;
    // Event-bearing rows per session|connection: zone anchors, queue registration and
    // cancellation, finder notifications. Acks and cluster members stay out.
    QHash<QString, QVariantList> m_indexEvents;
    QSet<QString> m_indexIds;
    QTimer m_liveRefresh;
    QString m_sessionId;
    QString m_indexUpperUtc;
    // Recipes, so a language switch re-renders them (LiveText.h).
    LiveText m_indexMessage;
    LiveText m_error;
    QString m_reviewBusyId;
    LiveText m_reviewError;
    QString m_targetOverride;
    LiveText m_exportError;
    LiveText m_settingsError;
    QVariantMap m_exportResult;
    QVariantMap m_roundAnswers;
    QVariantMap m_roundExportResult;
    LiveText m_roundExportError;
    QString m_roundId;
    QString m_finishRoundId;
    QString m_exportedRoundId;
    QStringList m_seen;
    std::function<bool(const QUrl &)> m_openUrl;
    int m_page = 1;
    int m_total = 0;
    int m_pageGeneration = 0;
    int m_indexGeneration = 0;
    int m_connectionGeneration = 0;
    int m_indexExpectedTotal = -1;
    bool m_active = false;
    bool m_loading = false;
    bool m_indexLoading = false;
    bool m_indexComplete = false;
    bool m_indexRetried = false;
    bool m_indexChanged = false;
    bool m_hasNewObservations = false;
    bool m_exporting = false;
    bool m_settingsBusy = false;
    bool m_finishing = false;
    bool m_roundSettingsKnown = false;
    bool m_scopedRoundsSupported = false;
    bool m_candidateEnabled = false;
};
} // namespace mr
